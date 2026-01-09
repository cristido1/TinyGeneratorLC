using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Models;
using TinyGenerator.Data;
using System.Text.Json;
using System.Threading.Tasks;
using ModelInfo = TinyGenerator.Models.ModelInfo;
// CallRecord model removed - no alias kept
using TestDefinition = TinyGenerator.Models.TestDefinition;

namespace TinyGenerator.Services;

public sealed class DatabaseService
{
    public void UpdateStoryTitle(long storyId, string title)
    {
        using var context = CreateDbContext();
        var story = context.Stories.FirstOrDefault(s => s.Id == storyId);
        if (story != null)
        {
            story.Title = title ?? string.Empty;
            context.SaveChanges();
        }
    }
    private readonly IOllamaMonitorService? _ollamaMonitor;
    private readonly System.Threading.SemaphoreSlim _dbSemaphore = new System.Threading.SemaphoreSlim(1,1);
    private const int EvaluatedStatusId = 2;
    private const int InitialStoryStatusId = 1;

    private readonly string _connectionString;
    private readonly IServiceProvider _serviceProvider;

    public DatabaseService(string dbPath = "data/storage.db", IOllamaMonitorService? ollamaMonitor = null, IServiceProvider? serviceProvider = null)
    {
        Console.WriteLine($"[DB] DatabaseService ctor start (dbPath={dbPath})");
        var ctorSw = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? ".");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Error creating data directory: {ex.Message}");
        }
        // Enable foreign key enforcement, WAL mode for better concurrency, and reduced busy timeout
        _connectionString = $"Data Source={dbPath};Foreign Keys=True;Mode=ReadWriteCreate;Cache=Shared;Pooling=True";
        // Defer heavy initialization to the explicit Initialize() method so the
        // service can be registered without blocking `builder.Build()`.
        _ollamaMonitor = ollamaMonitor;
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        ctorSw.Stop();
        Console.WriteLine($"[DB] DatabaseService ctor completed in {ctorSw.ElapsedMilliseconds}ms");
    }

    // Wrapper that disposes both the DbContext and the scope
    private sealed class DbContextWrapper : IDisposable
    {
        private readonly IServiceScope _scope;
        public TinyGeneratorDbContext Context { get; }

        public DbContextWrapper(IServiceScope scope, TinyGeneratorDbContext context)
        {
            _scope = scope;
            Context = context;
        }

        public void Dispose()
        {
            Context?.Dispose();
            _scope?.Dispose();
        }
    }

    // Helper method to create a scoped DbContext with proper disposal
    private DbContextWrapper CreateDbContextWrapper()
    {
        var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TinyGeneratorDbContext>();
        return new DbContextWrapper(scope, context);
    }

    // Helper method to create a scoped DbContext (legacy - prefer CreateDbContextWrapper)
    private TinyGeneratorDbContext CreateDbContext()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<TinyGeneratorDbContext>();
    }

    // NOTE: Dapper is retained only for a small set of low-level operations
    // (for example embedding storage/lookup) that are not yet modeled with
    // EF entities. Prefer `CreateDbContext()` and EF Core for all higher-
    // level data access. Use `CreateDapperConnection()` when a raw SQLite
    // connection is required.
    private IDbConnection CreateDapperConnection() => new SqliteConnection(_connectionString);

    // Dapper-only helpers for embedding management
    /// <summary>
    /// Save embedding vector for a row into the specified table.
    /// Embedding is serialized as JSON text into the specified column.
    /// This is intentionally implemented with Dapper/raw connection for
    /// performance and small binary/blob handling; keep all other SQL
    /// access routed through EF Core objects.
    /// </summary>
    public void SaveEmbedding(string tableName, string idColumn, long id, string embeddingColumn, float[] embedding)
    {
        if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentNullException(nameof(tableName));
        if (string.IsNullOrWhiteSpace(idColumn)) throw new ArgumentNullException(nameof(idColumn));
        if (string.IsNullOrWhiteSpace(embeddingColumn)) throw new ArgumentNullException(nameof(embeddingColumn));

        var embJson = JsonSerializer.Serialize(embedding ?? Array.Empty<float>());
        using var conn = CreateDapperConnection();
        conn.Open();
        // Use parameterized update to avoid SQL injection; table/column names are interpolated
        // but should be controlled by the caller (trusted/internal).
        var sql = $"UPDATE \"{tableName}\" SET \"{embeddingColumn}\" = @emb WHERE \"{idColumn}\" = @id";
        conn.Execute(sql, new { emb = embJson, id });
    }

    /// <summary>
    /// Execute a raw SQL command and return the number of rows affected.
    /// Use with caution - prefer EF Core methods when possible.
    /// </summary>
    public int ExecuteRaw(string sql, object? parameters = null)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentNullException(nameof(sql));
        using var conn = CreateDapperConnection();
        conn.Open();
        return conn.Execute(sql, parameters);
    }

    /// <summary>
    /// Find nearest rows by embedding using an in-memory similarity scan.
    /// Reads the embedding column (assumed JSON array of floats) for all rows
    /// in the given table and returns the top K nearest by cosine similarity.
    /// Implemented with Dapper to read raw embedding blobs but performs the
    /// numeric search in-process (no vector index required).
    /// </summary>
    public List<(long Id, double Score)> SearchByEmbedding(string tableName, string idColumn, string embeddingColumn, float[] queryEmbedding, int topK = 10)
    {
        if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentNullException(nameof(tableName));
        if (queryEmbedding == null) throw new ArgumentNullException(nameof(queryEmbedding));

        using var conn = CreateDapperConnection();
        conn.Open();

        var sql = $"SELECT \"{idColumn}\" AS Id, \"{embeddingColumn}\" AS EmbJson FROM \"{tableName}\" WHERE \"{embeddingColumn}\" IS NOT NULL";
        var rows = conn.Query(sql).ToList();

        var results = new List<(long Id, double Score)>();
        foreach (var r in rows)
        {
            try
            {
                long id = Convert.ToInt64(r.Id);
                string embJson = r.EmbJson as string ?? string.Empty;
                if (string.IsNullOrWhiteSpace(embJson)) continue;
                var emb = JsonSerializer.Deserialize<float[]>(embJson);
                if (emb == null || emb.Length != queryEmbedding.Length) continue;
                // cosine similarity
                double dot = 0, nq = 0, ne = 0;
                for (int i = 0; i < emb.Length; i++)
                {
                    dot += emb[i] * queryEmbedding[i];
                    nq += queryEmbedding[i] * queryEmbedding[i];
                    ne += emb[i] * emb[i];
                }
                if (nq == 0 || ne == 0) continue;
                var score = dot / (Math.Sqrt(nq) * Math.Sqrt(ne));
                results.Add((id, score));
            }
            catch { /* best-effort: skip malformed rows */ }
        }

        return results.OrderByDescending(x => x.Score).Take(topK).ToList();
    }

    public void SaveChapter(string memoryKey, int chapterNumber, string content)
    {
        using var context = CreateDbContext();
        var ch = new Chapter
        {
            MemoryKey = memoryKey ?? string.Empty,
            ChapterNumber = chapterNumber,
            Content = content ?? string.Empty,
            Ts = DateTime.UtcNow.ToString("o")
        };
        context.Chapters.Add(ch);
        context.SaveChanges();
    }

    // Public method to initialize schema and run migrations - call after
    // DI container is built in Program.cs to avoid blocking builder.Build().
    public void Initialize()
    {
        try
        {
            Console.WriteLine("[DB] Initialize() called");
            
            // Enable WAL mode for better concurrency
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                cmd.ExecuteNonQuery();
                Console.WriteLine("[DB] Enabled WAL mode and busy timeout");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] Warning: failed to enable WAL mode: {ex.Message}");
            }
            
            // Check if database file exists; if not, recreate from schema file
            var dbPath = _connectionString.Replace("Data Source=", "").Split(';')[0].Trim();
            if (!File.Exists(dbPath))
            {
                Console.WriteLine($"[DB] Database file not found at {dbPath}, recreating from schema...");
                RecreateFromSchema(dbPath);
            }
            
            InitializeSchema();
            // Ensure generated_* columns exist for stories table (best-effort)
            try
            {
                EnsureStoryGeneratedColumnsExist();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] Warning: failed to ensure generated columns: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Initialize() error: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// Ensure the generated_* boolean columns exist in the `stories` table.
    /// This is best-effort and will add columns when missing using ALTER TABLE.
    /// </summary>
    private void EnsureStoryGeneratedColumnsExist()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info('stories');";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                existing.Add(Convert.ToString(reader[1]) ?? string.Empty);
            }
        }

        void AddIfMissing(string columnName, string columnDef)
        {
            if (existing.Contains(columnName)) return;
            try
            {
                using var addCmd = conn.CreateCommand();
                addCmd.CommandText = $"ALTER TABLE stories ADD COLUMN {columnName} {columnDef};";
                addCmd.ExecuteNonQuery();
                Console.WriteLine($"[DB] Added column {columnName} to stories table");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] Warning: unable to add column {columnName}: {ex.Message}");
            }
        }

        // SQLite represents booleans as INTEGER 0/1
        AddIfMissing("generated_tts_json", "INTEGER DEFAULT 0");
        AddIfMissing("generated_tts", "INTEGER DEFAULT 0");
        AddIfMissing("generated_ambient", "INTEGER DEFAULT 0");
        AddIfMissing("generated_music", "INTEGER DEFAULT 0");
        AddIfMissing("generated_effects", "INTEGER DEFAULT 0");
        AddIfMissing("generated_mixed_audio", "INTEGER DEFAULT 0");
    }

    /// <summary>
    /// Recreate database from saved schema file (db_schema.sql).
    /// </summary>
    private void RecreateFromSchema(string dbPath)
    {
        // Schema file path - relative to application working directory
        var schemaPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "db_schema.sql");
        
        if (!File.Exists(schemaPath))
        {
            Console.WriteLine($"[DB] Warning: Schema file not found at {schemaPath}. Creating empty database with InitializeSchema().");
            // Create connection to empty database
            using var connEmpty = CreateConnection();
            connEmpty.Open();
            connEmpty.Close();
            return;
        }
        
        Console.WriteLine($"[DB] Loading schema from {schemaPath}");
        var schema = File.ReadAllText(schemaPath);
        
        // Create connection and apply schema
        using var connSchema = CreateConnection();
        connSchema.Open();
        
        // Split by semicolon and execute each statement
        var statements = schema.Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var statement in statements)
        {
            var trimmed = statement.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                try
                {
                    using var cmd = ((SqliteConnection)connSchema).CreateCommand();
                    cmd.CommandText = trimmed + ";";
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DB] Warning: Failed to execute schema statement: {ex.Message}");
                    // Continue with next statement
                }
            }
        }
        
        connSchema.Close();
        Console.WriteLine($"[DB] Database recreated from schema successfully");
    }

    /// <summary>
    /// Execute a SQL script file against the configured database. Best-effort execution.
    /// </summary>
    public void ExecuteSqlScript(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        try
        {
            var sql = File.ReadAllText(filePath);
            using var conn = CreateConnection();
            conn.Open();
            conn.Execute(sql);
            Console.WriteLine($"[DB] Executed SQL script: {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Failed to execute SQL script {filePath}: {ex.Message}");
        }
    }

    private IDbConnection CreateConnection() => new SqliteConnection(_connectionString);

    // Run a synchronous action with an exclusive sqlite connection protected by semaphore.
    public T WithSqliteConnection<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> work)
    {
        _dbSemaphore.Wait();
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
            conn.Open();
            try { using var fkCmd = conn.CreateCommand(); fkCmd.CommandText = "PRAGMA foreign_keys = ON;"; fkCmd.ExecuteNonQuery(); } catch { }
            return work(conn);
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    // Run an async function with an exclusive sqlite connection protected by semaphore.
    public async System.Threading.Tasks.Task<T> WithSqliteConnectionAsync<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, System.Threading.Tasks.Task<T>> work)
    {
        await _dbSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            try { using var fkCmd = conn.CreateCommand(); fkCmd.CommandText = "PRAGMA foreign_keys = ON;"; fkCmd.ExecuteNonQuery(); } catch { }
            return await work(conn).ConfigureAwait(false);
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    public List<ModelInfo> ListModels()
    {
        // Use EF Core to list models (preferable to raw SQL/Dapper)
        using var context = CreateDbContext();
        try
        {
            return context.Models.AsNoTracking().OrderBy(m => m.Name).ToList();
        }
        catch (Exception)
        {
            // Fallback to Dapper in case EF cannot read (legacy DB schema)
            using var conn = CreateDapperConnection();
            conn.Open();
            var cols = SelectModelColumns();
            var sql = $"SELECT {cols} FROM models";
            return conn.Query<ModelInfo>(sql).OrderBy(m => m.Name).ToList();
        }
    }

    /// <summary>
    /// Returns a paged, optionally filtered and ordered list of models with total count.
    /// This helper implements server-side filtering, sorting and paging for model lists.
    /// </summary>
    public (List<ModelInfo> Items, int TotalCount) GetPagedModels(string? search, string? orderBy, int pageIndex, int pageSize, bool showDisabled = true)
    {
        using var context = CreateDbContext();
        var query = context.Models.AsNoTracking().AsQueryable();
        if (!showDisabled)
        {
            query = query.Where(m => m.Enabled);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(m => m.Name.Contains(s) || m.Provider.Contains(s) || (m.Note ?? string.Empty).Contains(s));
        }

        // Simple orderBy handling (name, provider, createdat, updatedat)
        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            var ob = orderBy.ToLowerInvariant();
            if (ob == "provider") query = query.OrderBy(m => m.Provider);
            else if (ob == "createdat") query = query.OrderByDescending(m => m.CreatedAt);
            else if (ob == "updatedat") query = query.OrderByDescending(m => m.UpdatedAt);
            else query = query.OrderBy(m => m.Name);
        }
        else
        {
            query = query.OrderBy(m => m.Name);
        }

        var total = query.Count();
        if (pageIndex < 1) pageIndex = 1;
        if (pageSize <= 0) pageSize = 25;
        var items = query.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToList();
        return (items, total);
    }

    /// <summary>
    /// Return a lightweight summary of the latest test run for the given model id, or null if none.
    /// </summary>
    public (int runId, string testCode, bool passed, long? durationMs, string? runDate)? GetLatestTestRunSummaryById(int modelId)
    {
        try
        {
            using var context = CreateDbContext();
            var run = context.ModelTestRuns.Where(r => r.ModelId == modelId).OrderByDescending(r => r.Id).FirstOrDefault();
            if (run == null) return null;
            return (run.Id, run.TestGroup ?? string.Empty, run.Passed, run.DurationMs, run.RunDate);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Return a lightweight summary of the latest test run for the given model name, or null if none.
    /// </summary>
    public (int runId, string testCode, bool passed, long? durationMs, string? runDate)? GetLatestTestRunSummary(string modelName)
    {
        try
        {
            using var context = CreateDbContext();
            var model = context.Models.FirstOrDefault(m => m.Name == modelName);
            if (model == null || !model.Id.HasValue) return null;
            var modelId = model.Id.Value;
            var run = context.ModelTestRuns.Where(r => r.ModelId == modelId).OrderByDescending(r => r.Id).FirstOrDefault();
            if (run == null) return null;
            return (run.Id, run.TestGroup ?? string.Empty, run.Passed, run.DurationMs, run.RunDate);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get the duration in milliseconds of the latest test run for a specific group by model id.
    /// </summary>
    public long? GetGroupTestDurationById(int modelId, string groupName)
    {
        try
        {
            using var context = CreateDbContext();
            var run = context.ModelTestRuns.Where(r => r.ModelId == modelId && r.TestGroup == groupName).OrderByDescending(r => r.Id).FirstOrDefault();
            return run?.DurationMs;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get the duration in milliseconds of the latest test run for a specific group.
    /// </summary>
    public long? GetGroupTestDuration(string modelName, string groupName)
    {
        try
        {
            using var context = CreateDbContext();
            var model = context.Models.FirstOrDefault(m => m.Name == modelName);
            if (model == null || !model.Id.HasValue) return null;
            var modelId = model.Id.Value;
            var run = context.ModelTestRuns.Where(r => r.ModelId == modelId && r.TestGroup == groupName).OrderByDescending(r => r.Id).FirstOrDefault();
            return run?.DurationMs;
        }
        catch
        {
            return null;
        }
    }

    public ModelInfo? GetModelInfo(string modelOrProvider)
    {
        if (string.IsNullOrWhiteSpace(modelOrProvider)) return null;
        using var context = CreateDbContext();
        
        var byName = context.Models.FirstOrDefault(m => m.Name == modelOrProvider);
        if (byName != null) return byName;
        
        var provider = modelOrProvider.Split(':')[0];
        return context.Models.FirstOrDefault(m => m.Provider == provider);
    }

    /// <summary>
    /// Get model info by explicit ID (preferred over name-based lookup).
    /// </summary>
    public ModelInfo? GetModelInfoById(int modelId)
    {
        using var context = CreateDbContext();
        try
        {
            return context.Models.Find(modelId);
        }
        catch
        {
            return null;
        }
    }

    public void UpsertModel(ModelInfo model)
    {
        if (model == null || string.IsNullOrWhiteSpace(model.Name)) return;
        using var context = CreateDbContext();
        
        var now = DateTime.UtcNow.ToString("o");
        ModelInfo? existing = null;
        if (model.Id.HasValue)
        {
            existing = context.Models.FirstOrDefault(m => m.Id == model.Id);
        }
        if (existing == null)
        {
            existing = context.Models.FirstOrDefault(m => m.Name == model.Name);
        }
        
        // Preserve an existing non-zero FunctionCallingScore if the caller didn't set a meaningful score.
        if (existing != null && existing.FunctionCallingScore != 0 && model.FunctionCallingScore == 0)
        {
            model.FunctionCallingScore = existing.FunctionCallingScore;
        }
        model.CreatedAt ??= existing?.CreatedAt ?? now;
        model.UpdatedAt = now;

        if (existing != null)
        {
            // Update existing
            model.Id = existing.Id;
            context.Entry(existing).CurrentValues.SetValues(model);
        }
        else
        {
            // Insert new
            context.Models.Add(model);
        }
        context.SaveChanges();
    }

    // Delete a model by name from the models table (best-effort). Also deletes related model_test_runs entries.
    public void DeleteModel(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        using var context = CreateDbContext();
        try
        {
            var model = context.Models.FirstOrDefault(m => m.Name == name);
            if (model != null)
            {
                // Delete related model_test_runs via EF
                if (model.Id.HasValue)
                {
                    var runs = context.ModelTestRuns.Where(r => r.ModelId == model.Id.Value).ToList();
                    if (runs.Any())
                    {
                        context.ModelTestRuns.RemoveRange(runs);
                    }
                }

                context.Models.Remove(model);
                context.SaveChanges();
            }
        }
        catch { }
    }

    /// <summary>
    /// Check if a model is used by any agent. Returns list of agent names using the model.
    /// </summary>
    public List<string> GetAgentsUsingModel(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return new List<string>();
        
        using var context = CreateDbContext();
        var model = context.Models.FirstOrDefault(m => m.Name == modelName);
        if (model == null || !model.Id.HasValue) return new List<string>();
        
        var agents = context.Agents
            .Where(a => a.ModelId == model.Id.Value)
            .Select(a => a.Name ?? string.Empty)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        
        return agents;
    }

    public bool TryReserveUsage(string monthKey, long tokensToAdd, double costToAdd, long maxTokensPerRun, double maxCostPerMonth)
    {
        using var context = CreateDbContext();
        EnsureUsageRow(context, monthKey);

        var state = context.UsageStates.FirstOrDefault(u => u.Month == monthKey);
        if (state == null) return false;
        var tokensThisRun = state.TokensThisRun;
        var tokensThisMonth = state.TokensThisMonth;
        var costThisMonth = state.CostThisMonth;

        if (tokensThisRun + tokensToAdd > maxTokensPerRun) return false;
        if (costThisMonth + costToAdd > maxCostPerMonth) return false;

        state.TokensThisRun += tokensToAdd;
        state.TokensThisMonth += tokensToAdd;
        state.CostThisMonth += costToAdd;
        context.SaveChanges();
        return true;
    }

    public void ResetRunCounters(string monthKey)
    {
        using var context = CreateDbContext();
        EnsureUsageRow(context, monthKey);
        var state = context.UsageStates.FirstOrDefault(u => u.Month == monthKey);
        if (state != null)
        {
            state.TokensThisRun = 0;
            context.SaveChanges();
        }
    }

    /// <summary>
    /// Normalize existing test prompts to explicitly reference the library addin/function.
    /// Example: "Add 3 hours to the time 10:00 using addhours" ->
    /// "Add 3 hours to the time 10:00 using the time.addhours addin/function"
    /// This method is idempotent and will only update prompts that look like they use a bare function name
    /// (i.e. contain "using <name>" but not already "using the <lib>.<name>").
    /// </summary>
    public void NormalizeTestPrompts()
    {
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            // Helper to process a table (test_prompts preferred, otherwise test_definitions)
            void ProcessTable(string tableName)
            {
                try
                {
                    using var check = conn.CreateCommand();
                    check.CommandText = $"PRAGMA table_info('{tableName}');";
                    using var rdr = check.ExecuteReader();
                    if (!rdr.Read()) return; // table not present
                }
                catch { return; }

                var rows = conn.Query($"SELECT id AS Id, prompt AS Prompt, library AS Library FROM {tableName}").ToList();
                foreach (var r in rows)
                {
                    try
                    {
                        string prompt = r.Prompt ?? string.Empty;
                        string lib = (r.Library ?? string.Empty).Trim().ToLowerInvariant();
                        if (string.IsNullOrWhiteSpace(prompt)) continue;
                        // Only process prompts that contain 'using ' and do NOT already contain 'using the ' or a dotted reference like 'time.addhours'
                        if (!prompt.Contains("using ", StringComparison.OrdinalIgnoreCase)) continue;
                        if (prompt.IndexOf("using the ", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (prompt.IndexOf('.', prompt.IndexOf("using ", StringComparison.OrdinalIgnoreCase)) >= 0) continue;

                        // Find the token after 'using '
                        var idx = prompt.IndexOf("using ", StringComparison.OrdinalIgnoreCase);
                        var tail = prompt.Substring(idx + "using ".Length);
                        // token is up to whitespace or punctuation
                        var end = 0;
                        while (end < tail.Length && (char.IsLetterOrDigit(tail[end]) || tail[end] == '_')) end++;
                        if (end == 0) continue;
                        var func = tail.Substring(0, end);
                        if (string.IsNullOrWhiteSpace(func)) continue;

                        var libName = string.IsNullOrWhiteSpace(lib) ? "" : lib + ".";
                        var replacement = $"using the {libName}{func} addin/function";
                        var newPrompt = prompt.Substring(0, idx) + replacement + tail.Substring(end);

                        // Update row
                        conn.Execute($"UPDATE {tableName} SET prompt = @p WHERE id = @id", new { p = newPrompt, id = (int)r.Id });
                    }
                    catch { }
                }
            }

            ProcessTable("test_prompts");
            ProcessTable("test_definitions");
        }
        catch
        {
            // ignore normalization failures
        }
    }

    public (long tokensThisMonth, double costThisMonth) GetMonthUsage(string monthKey)
    {
        using var context = CreateDbContext();
        EnsureUsageRow(context, monthKey);
        var row = context.UsageStates.FirstOrDefault(u => u.Month == monthKey);
        if (row == null) return (0L, 0.0);
        return (row.TokensThisMonth, row.CostThisMonth);
    }

    // calls table and CallRecord model removed; call tracking disabled.
    // If you need to re-enable tracking later, reintroduce the `calls` table and corresponding methods.

    // Agents CRUD (EF Core)
    public List<TinyGenerator.Models.Agent> ListAgents()
    {
        using var context = CreateDbContext();
        // Include related data if needed (VoiceName, MultiStepTemplateName are non-persistent)
        return context.Agents.OrderBy(a => a.Name).ToList();
    }

    public List<string> ListAgentRoles()
    {
        using var context = CreateDbContext();
        return context.Agents.AsNoTracking()
            .Select(a => a.Role ?? string.Empty)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct()
            .OrderBy(r => r)
            .ToList();
    }

    /// <summary>
    /// Return a paged list of agents with optional search, sort, and model filter.
    /// Filtering/sorting/paging are executed in the database.
    /// </summary>
    public (List<TinyGenerator.Models.Agent> Items, int TotalCount) GetPagedAgents(
        string? search,
        string? orderBy,
        int pageIndex,
        int pageSize,
        string? modelFilter = null,
        string? roleFilter = null)
    {
        if (pageIndex < 1) pageIndex = 1;
        if (pageSize <= 0) pageSize = 25;

        using var context = CreateDbContext();
        var query =
            from a in context.Agents.AsNoTracking()
            join m in context.Models.AsNoTracking() on a.ModelId equals m.Id into mj
            from m in mj.DefaultIfEmpty()
            join t in context.StepTemplates.AsNoTracking() on a.MultiStepTemplateId equals (int?)t.Id into tj
            from t in tj.DefaultIfEmpty()
            select new { Agent = a, ModelName = m != null ? m.Name : null, TemplateName = t != null ? t.Name : null };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(x =>
                (x.Agent.Name ?? string.Empty).Contains(s) ||
                (x.Agent.Role ?? string.Empty).Contains(s) ||
                (x.Agent.Skills ?? string.Empty).Contains(s) ||
                (x.ModelName ?? string.Empty).Contains(s));
        }

        if (!string.IsNullOrWhiteSpace(modelFilter))
        {
            if (modelFilter.Equals("__none__", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(x => !x.Agent.ModelId.HasValue);
            }
            else
            {
                query = query.Where(x => (x.ModelName ?? string.Empty) == modelFilter);
            }
        }

        if (!string.IsNullOrWhiteSpace(roleFilter))
        {
            var rf = roleFilter.Trim().ToLowerInvariant();
            query = query.Where(x => (x.Agent.Role ?? string.Empty).ToLower() == rf);
        }

        var ob = (orderBy ?? "name").ToLowerInvariant();
        query = ob switch
        {
            "role" => query.OrderBy(x => x.Agent.Role).ThenBy(x => x.Agent.Name),
            "model" => query.OrderBy(x => x.ModelName).ThenBy(x => x.Agent.Name),
            "temperature" => query.OrderBy(x => x.Agent.Temperature).ThenBy(x => x.Agent.Name),
            "topp" => query.OrderBy(x => x.Agent.TopP).ThenBy(x => x.Agent.Name),
            "repeatpenalty" => query.OrderBy(x => x.Agent.RepeatPenalty).ThenBy(x => x.Agent.Name),
            "topk" => query.OrderBy(x => x.Agent.TopK).ThenBy(x => x.Agent.Name),
            "repeatlastn" => query.OrderBy(x => x.Agent.RepeatLastN).ThenBy(x => x.Agent.Name),
            "numpredict" => query.OrderBy(x => x.Agent.NumPredict).ThenBy(x => x.Agent.Name),
            "voice" => query.OrderBy(x => x.Agent.VoiceId).ThenBy(x => x.Agent.Name),
            "skills" => query.OrderBy(x => x.Agent.Skills).ThenBy(x => x.Agent.Name),
            _ => query.OrderBy(x => x.Agent.Name)
        };

        var total = query.Count();
        var items = query.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToList();

        var result = new List<TinyGenerator.Models.Agent>(items.Count);
        foreach (var row in items)
        {
            row.Agent.ModelName = row.ModelName;
            row.Agent.MultiStepTemplateName = row.TemplateName;
            result.Add(row.Agent);
        }

        return (result, total);
    }

    public TinyGenerator.Models.Agent? GetAgentById(int id)
    {
        using var context = CreateDbContext();
        return context.Agents.Find(id);
    }

    public int? GetAgentIdByName(string name)
    {
        using var context = CreateDbContext();
        try
        {
            var agent = context.Agents.FirstOrDefault(a => a.Name == name);
            return agent?.Id;
        }
        catch { return null; }
    }

    public TinyGenerator.Models.Agent? GetAgentByRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        using var context = CreateDbContext();
        return context.Agents
            .Where(a => a.Role.ToLower() == role.ToLower())
            .OrderByDescending(a => a.IsActive)
            .ThenBy(a => a.Id)
            .FirstOrDefault();
    }

    public int InsertAgent(TinyGenerator.Models.Agent a)
    {
        using var context = CreateDbContext();
        var now = DateTime.UtcNow.ToString("o");
        a.CreatedAt ??= now;
        a.UpdatedAt = now;
        context.Agents.Add(a);
        context.SaveChanges();
        return a.Id;
    }

    public void UpdateAgent(TinyGenerator.Models.Agent a)
    {
        if (a == null) return;
        using var context = CreateDbContext();
        a.UpdatedAt = DateTime.UtcNow.ToString("o");
        context.Agents.Update(a);
        context.SaveChanges();
    }

    public void DeleteAgent(int id)
    {
        using var context = CreateDbContext();
        var agent = context.Agents.Find(id);
        if (agent != null)
        {
            context.Agents.Remove(agent);
            context.SaveChanges();
        }
    }

    // ╔══════════════════════════════════════════════════════════════╗
    // ║ Series Methods                                                ║
    // ╚══════════════════════════════════════════════════════════════╝

    public Series? GetSeriesById(int id)
    {
        using var context = CreateDbContext();
        return context.Series.Find(id);
    }

    public List<Series> ListAllSeries()
    {
        using var context = CreateDbContext();
        return context.Series
            .OrderBy(s => s.Titolo)
            .ToList();
    }

    public SeriesEpisode? GetSeriesEpisodeById(int id)
    {
        using var context = CreateDbContext();
        return context.SeriesEpisodes.Find(id);
    }

    public List<SeriesEpisode> ListSeriesEpisodes(int serieId)
    {
        using var context = CreateDbContext();
        return context.SeriesEpisodes
            .Where(e => e.SerieId == serieId)
            .OrderBy(e => e.Number)
            .ToList();
    }

    public List<SeriesEpisode> ListAllSeriesEpisodes()
    {
        using var context = CreateDbContext();
        return context.SeriesEpisodes
            .OrderBy(e => e.SerieId)
            .ThenBy(e => e.Number)
            .ToList();
    }

    public List<SeriesCharacter> ListSeriesCharacters(int serieId)
    {
        using var context = CreateDbContext();
        return context.SeriesCharacters
            .Where(c => c.SerieId == serieId)
            .OrderBy(c => c.Name)
            .ToList();
    }

    public void UpdateSeriesCharacterImage(int characterId, string? image)
    {
        using var context = CreateDbContext();
        var character = context.SeriesCharacters.Find(characterId);
        if (character == null) return;

        character.Image = image;
        context.SaveChanges();
    }

    public void IncrementSeriesEpisodeCount(int serieId)
    {
        using var context = CreateDbContext();
        var serie = context.Series.Find(serieId);
        if (serie != null)
        {
            serie.EpisodiGenerati++;
            context.SaveChanges();
        }
    }

    public int InsertSeries(Series serie)
    {
        using var context = CreateDbContext();
        serie.DataInserimento = DateTime.UtcNow;
        serie.EpisodiGenerati = 0;
        context.Series.Add(serie);
        context.SaveChanges();
        return serie.Id;
    }

    public void UpdateSeries(Series serie)
    {
        if (serie == null) return;
        using var context = CreateDbContext();
        context.Series.Update(serie);
        context.SaveChanges();
    }

    public void DeleteSeries(int id)
    {
        using var context = CreateDbContext();
        var serie = context.Series.Find(id);
        if (serie != null)
        {
            context.Series.Remove(serie);
            context.SaveChanges();
        }
    }

    public int EnsureSeriesFoldersOnDisk(string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(contentRootPath)) return 0;

        using var context = CreateDbContext();
        var list = context.Series.ToList();
        if (list.Count == 0) return 0;

        var updated = 0;
        var seriesRoot = Path.Combine(contentRootPath, "series_folder");
        Directory.CreateDirectory(seriesRoot);

        foreach (var s in list)
        {
            var folder = (s.Folder ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = $"serie_{s.Id:D4}";
                s.Folder = folder;
                updated++;
            }

            var full = Path.Combine(seriesRoot, folder);
            Directory.CreateDirectory(full);
        }

        if (updated > 0)
        {
            context.SaveChanges();
        }

        return updated;
    }

    /// <summary>
    /// Apply pending SQL migrations manually (for when EF migrations are disabled).
    /// This method should be called during startup to ensure database schema is up to date.
    /// </summary>
    public void ApplyPendingManualMigrations()
    {
        using var conn = CreateDapperConnection();
        conn.Open();

        // Persistent numerator state (threadid + story_id)
        // Keeps counters stable across restarts and independent from deletions.
        try
        {
            conn.Execute(@"CREATE TABLE IF NOT EXISTS numerators_state (
    key TEXT PRIMARY KEY,
    value INTEGER NOT NULL
);");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: failed to ensure numerators_state table: {ex.Message}");
        }

        // Ensure story_id exists in Log table (for correlating logs to a story creation/execution)
        var checkLogStoryId = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM pragma_table_info('Log') WHERE name='story_id'");
        if (checkLogStoryId == 0)
        {
            Console.WriteLine("[DB] Applying migration: AddStoryIdToLog");
            conn.Execute("ALTER TABLE Log ADD COLUMN story_id INTEGER");
            Console.WriteLine("[DB] ✓ Migration AddStoryIdToLog applied");
        }

        // Ensure story_id exists in stories table (preallocated story numbering independent from DB row id)
        var checkStoriesStoryId = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM pragma_table_info('stories') WHERE name='story_id'");
        if (checkStoriesStoryId == 0)
        {
            Console.WriteLine("[DB] Applying migration: AddStoryIdToStories");
            conn.Execute("ALTER TABLE stories ADD COLUMN story_id INTEGER");
            Console.WriteLine("[DB] ✓ Migration AddStoryIdToStories applied");
        }

        // Check if summary column exists
        var checkSummary = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM pragma_table_info('stories') WHERE name='summary'");
        
        if (checkSummary == 0)
        {
            Console.WriteLine("[DB] Applying migration: AddSummaryToStories");
            conn.Execute("ALTER TABLE stories ADD COLUMN summary TEXT");
            conn.Execute("INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20251227082500_AddSummaryToStories', '10.0.0')");
            Console.WriteLine("[DB] ✓ Migration AddSummaryToStories applied");
        }

        // Ensure qwen2.5:7b-instruct model exists
        var qwenModelExists = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM models WHERE name = 'qwen2.5:7b-instruct'");
        
        if (qwenModelExists == 0)
        {
            Console.WriteLine("[DB] Adding qwen2.5:7b-instruct model");
            conn.Execute(@"INSERT INTO models (name, provider, context_length, created_at, updated_at, note)
                VALUES ('qwen2.5:7b-instruct', 'ollama', 128000, datetime('now'), datetime('now'), 
                'Qwen 2.5 7B Instruct - Excellent for summarization with 128k context')");
            Console.WriteLine("[DB] ✓ qwen2.5:7b-instruct model added");
        }

        // Check if summarizer agent exists
        var summarizerExists = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM agents WHERE role = 'summarizer' AND name = 'Story Summarizer'");
        
        if (summarizerExists == 0)
        {
            Console.WriteLine("[DB] Creating Story Summarizer agent");
            
            // Get model_id for qwen2.5:7b-instruct
            var modelId = conn.ExecuteScalar<int>(
                "SELECT id FROM models WHERE name = 'qwen2.5:7b-instruct' LIMIT 1");
            
            conn.Execute(@"INSERT INTO agents (
                name, role, model_id, skills, prompt, instructions, is_active, 
                created_at, updated_at, notes, temperature, top_p, repeat_penalty, top_k, repeat_last_n, num_predict
            ) VALUES (
                'Story Summarizer',
                'summarizer',
                @modelId,
                '[]',
                'You are a professional story summarizer. Read the complete story and generate a concise summary.',
                'Read the entire story carefully and create a summary of 3-5 sentences that captures:
1. Main characters and their roles
2. The central conflict or problem
3. Key events in chronological order
4. The resolution (without major spoilers)

The summary should be:
- Concise but informative (3-5 sentences max)
- Engaging and encouraging readers to read the full story
- Written in the same language as the story (Italian for Italian stories)
- Free of spoilers for major plot twists
- Focused on the narrative arc

Output only the summary text, nothing else. No introductions, no formatting, just the summary.',
                1,
                datetime('now'),
                datetime('now'),
                'Summarizer agent using Qwen 2.5 7B with 128k context window',
                0.3,
                0.8,
                NULL,
                NULL,
                NULL,
                NULL
            )", new { modelId });
            
            Console.WriteLine("[DB] ✓ Story Summarizer agent created");
        }
    }

    // ╔══════════════════════════════════════════════════════════════╗
    // ║ End of Series Methods                                         ║
    // ╚══════════════════════════════════════════════════════════════╝

    public void UpdateModelTestResults(string modelName, int functionCallingScore, IReadOnlyDictionary<string, bool?> skillFlags, double? testDurationSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return;
        using var context = CreateDbContext();
        var model = context.Models.FirstOrDefault(m => m.Name == modelName);
        if (model == null) return;
        model.FunctionCallingScore = functionCallingScore;
        model.UpdatedAt = DateTime.UtcNow.ToString("o");
        if (testDurationSeconds.HasValue) model.TestDurationSeconds = testDurationSeconds;
        if (skillFlags != null && skillFlags.TryGetValue("__NoToolsMarker", out var noTools) && noTools.HasValue)
        {
            model.NoTools = noTools.Value;
        }
        context.SaveChanges();
    }

    public void UpdateModelTtsScore(string modelName, double score)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return;
        using var context = CreateDbContext();
        var model = context.Models.FirstOrDefault(m => m.Name == modelName);
        if (model == null) return;
        model.TtsScore = score;
        model.TotalScore = model.WriterScore + model.BaseScore + model.TextEvalScore + score + model.MusicScore + model.FxScore + model.AmbientScore;
        model.UpdatedAt = DateTime.UtcNow.ToString("o");
        context.SaveChanges();
    }

    /// <summary>
    /// Return list of available test groups.
    /// </summary>
    public List<string> GetTestGroups()
    {
        using var context = CreateDbContext();
        try
        {
            // Prefer test_prompts if present (DB may not have migrated table)
            try
            {
                if (context.TestPrompts.Any())
                {
                    return context.TestPrompts.Where(t => t.Active).Select(t => t.GroupName ?? string.Empty).Distinct().OrderBy(x => x).ToList();
                }
            }
            catch { /* table might not exist yet */ }

            return context.TestDefinitions.Where(t => t.Active).Select(t => t.GroupName ?? string.Empty).Distinct().OrderBy(x => x).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static void EnsureUsageRow(TinyGeneratorDbContext context, string monthKey)
    {
        if (context == null) return;
        try
        {
            var exists = context.UsageStates.Any(u => u.Month == monthKey);
            if (!exists)
            {
                context.UsageStates.Add(new UsageState { Month = monthKey, TokensThisRun = 0, TokensThisMonth = 0, CostThisMonth = 0 });
                context.SaveChanges();
            }
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    /// Retrieve test definitions for a given group name ordered by priority and id.
    /// </summary>
    public List<TestDefinition> GetTestsByGroup(string groupName)
    {
        using var context = CreateDbContext();
        return context.TestDefinitions.Where(t => t.GroupName == groupName && t.Active).OrderBy(t => t.Priority).ThenBy(t => t.Id).ToList();
    }

    /// <summary>
    /// Retrieve prompts for a given group from the newer `test_prompts` table when available,
    /// otherwise fall back to `test_definitions`.
    /// </summary>
    public List<TestDefinition> GetPromptsByGroup(string groupName)
    {
        using var context = CreateDbContext();
        try
        {
            try
            {
                if (context.TestPrompts.Any())
                {
                    return context.TestPrompts.Where(p => p.GroupName == groupName && p.Active)
                        .OrderBy(p => p.Priority)
                        .Select(p => new TestDefinition { Id = p.Id, GroupName = p.GroupName, Library = p.Library, Prompt = p.Prompt, Priority = p.Priority, Active = p.Active })
                        .ToList();
                }
            }
            catch { }

            // fallback to legacy table
            return GetTestsByGroup(groupName);
        }
        catch
        {
            return new List<TestDefinition>();
        }
    }

    /// <summary>
    /// List all test definitions with optional search and sort. Returns active tests only.
    /// </summary>
    public List<TestDefinition> ListAllTestDefinitions(string? search = null, string? sortBy = null, bool ascending = true)
    {
        using var context = CreateDbContext();
        IQueryable<TestDefinition> query = context.TestDefinitions.Where(t => t.Active);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.ToLower();
            query = query.Where(t => 
                (t.GroupName != null && t.GroupName.ToLower().Contains(q)) ||
                (t.Library != null && t.Library.ToLower().Contains(q)) ||
                (t.FunctionName != null && t.FunctionName.ToLower().Contains(q)) ||
                (t.Prompt != null && t.Prompt.ToLower().Contains(q)));
        }

        // Sort
        var col = (sortBy ?? "id").ToLowerInvariant();
        query = col switch
        {
            "group" or "groupname" => ascending ? query.OrderBy(t => t.GroupName) : query.OrderByDescending(t => t.GroupName),
            "library" => ascending ? query.OrderBy(t => t.Library) : query.OrderByDescending(t => t.Library),
            "function" or "functionname" => ascending ? query.OrderBy(t => t.FunctionName) : query.OrderByDescending(t => t.FunctionName),
            "priority" => ascending ? query.OrderBy(t => t.Priority) : query.OrderByDescending(t => t.Priority),
            _ => ascending ? query.OrderBy(t => t.Id) : query.OrderByDescending(t => t.Id)
        };

        return query.ToList();
    }

    /// <summary>
    /// Return a paged list of TestDefinitions (active only) with total count. PageIndex starts at 1.
    /// </summary>
    public (List<TestDefinition> Items, int TotalCount) GetPagedTestDefinitions(int pageIndex, int pageSize, string? search = null, string? sortBy = null, bool ascending = true)
    {
        if (pageIndex < 1) pageIndex = 1;
        if (pageSize <= 0) pageSize = 25;
        using var context = CreateDbContext();
        IQueryable<TestDefinition> query = context.TestDefinitions.Where(t => t.Active);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.ToLower();
            query = query.Where(t =>
                (t.GroupName != null && t.GroupName.ToLower().Contains(q)) ||
                (t.Library != null && t.Library.ToLower().Contains(q)) ||
                (t.FunctionName != null && t.FunctionName.ToLower().Contains(q)) ||
                (t.Prompt != null && t.Prompt.ToLower().Contains(q)));
        }

        // Sort
        var col = (sortBy ?? "id").ToLowerInvariant();
        query = col switch
        {
            "group" or "groupname" => ascending ? query.OrderBy(t => t.GroupName) : query.OrderByDescending(t => t.GroupName),
            "library" => ascending ? query.OrderBy(t => t.Library) : query.OrderByDescending(t => t.Library),
            "function" or "functionname" => ascending ? query.OrderBy(t => t.FunctionName) : query.OrderByDescending(t => t.FunctionName),
            "priority" => ascending ? query.OrderBy(t => t.Priority) : query.OrderByDescending(t => t.Priority),
            _ => ascending ? query.OrderBy(t => t.Id) : query.OrderByDescending(t => t.Id)
        };

        var total = query.Count();
        var items = query.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToList();
        return (items, total);
    }

    public TestDefinition? GetTestDefinitionById(int id)
    {
        using var context = CreateDbContext();
        return context.TestDefinitions.FirstOrDefault(t => t.Id == id);
    }

    public int InsertTestDefinition(TestDefinition td)
    {
        using var context = CreateDbContext();
        context.TestDefinitions.Add(td);
        context.SaveChanges();
        return td.Id;
    }

    public void UpdateTestDefinition(TestDefinition td)
    {
        using var context = CreateDbContext();
        var existing = context.TestDefinitions.Find(td.Id);
        if (existing == null) return;
        context.Entry(existing).CurrentValues.SetValues(td);
        context.SaveChanges();
    }

    public void DeleteTestDefinition(int id)
    {
        using var context = CreateDbContext();
        var td = context.TestDefinitions.Find(id);
        if (td != null)
        {
            // Soft delete: mark active = 0
            td.Active = false;
            context.SaveChanges();
        }
    }

    /// <summary>
    /// Return counts for a given run id: passed count and total steps.
    /// </summary>
    public (int passed, int total) GetRunStepCounts(int runId)
    {
        using var context = CreateDbContext();
        var total = context.ModelTestSteps.Count(s => s.RunId == runId);
        var passed = context.ModelTestSteps.Count(s => s.RunId == runId && s.Passed);
        return (passed, total);
    }

    /// <summary>
    /// Return the latest run score (0-10) for a given model name and group (test_code).
    /// Returns null if no run exists for that model+group.
    /// </summary>
    public int? GetLatestGroupScore(string modelName, string groupName)
    {
        try
        {
            using var context = CreateDbContext();
            var model = context.Models.FirstOrDefault(m => m.Name == modelName);
            if (model == null || !model.Id.HasValue) return null;
            var modelId = model.Id.Value;
            var run = context.ModelTestRuns.Where(r => r.ModelId == modelId && r.TestGroup == groupName).OrderByDescending(r => r.Id).FirstOrDefault();
            if (run == null) return null;
            var counts = GetRunStepCounts(run.Id);
            if (counts.total == 0) return 0;
            var score = (int)Math.Round((double)counts.passed / counts.total * 10);
            return score;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Return the latest run's step results as a JSON array for the given model and group.
    /// Each element contains: step_name, passed (bool), message (error or null), duration_ms (nullable), output_json (nullable)
    /// Returns null if no run exists.
    /// </summary>
    public string? GetLatestRunStepsJson(string modelName, string groupName)
    {
        try
        {
            using var context = CreateDbContext();
            var model = context.Models.FirstOrDefault(m => m.Name == modelName);
            if (model == null || !model.Id.HasValue) return null;
            var modelId = model.Id.Value;
            var run = context.ModelTestRuns.Where(r => r.ModelId == modelId && r.TestGroup == groupName).OrderByDescending(r => r.Id).FirstOrDefault();
            if (run == null) return null;

            var rows = context.ModelTestSteps.Where(s => s.RunId == run.Id).OrderBy(s => s.StepNumber).ToList();

            var list = new List<object>();
            foreach (var r in rows)
            {
                bool passed = r.Passed;
                string stepName = r.StepName ?? string.Empty;
                string? inputJson = r.InputJson;
                string? outputJson = r.OutputJson;
                string? error = r.Error;
                long? dur = r.DurationMs;
                object? inputElem = null;
                try { if (!string.IsNullOrWhiteSpace(inputJson)) inputElem = System.Text.Json.JsonDocument.Parse(inputJson).RootElement; } catch { inputElem = inputJson; }
                list.Add(new { name = stepName, ok = passed, message = !string.IsNullOrWhiteSpace(error) ? error : (object?)null, durationMs = dur, input = inputElem, output = !string.IsNullOrWhiteSpace(outputJson) ? System.Text.Json.JsonDocument.Parse(outputJson).RootElement : (System.Text.Json.JsonElement?)null });
            }

            var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
            return System.Text.Json.JsonSerializer.Serialize(list, opts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Create a new test run and return its id.
    /// Automatically cleans old test runs for the same model+group, keeping only the most recent one.
    /// </summary>
    public int CreateTestRun(string modelName, string testCode, string? description = null, bool passed = false, long? durationMs = null, string? notes = null, string? testFolder = null)
    {
        using var context = CreateDbContext();

        // Resolve model
        var model = context.Models.FirstOrDefault(m => m.Name == modelName);
        if (model == null || !model.Id.HasValue) return 0;
        var modelId = model.Id.Value;

        // Cleanup older runs for this model+group, keep only the most recent one
        var runs = context.ModelTestRuns.Where(r => r.ModelId == modelId && r.TestGroup == testCode).OrderByDescending(r => r.Id).ToList();
        if (runs.Count > 1)
        {
            var toDelete = runs.Skip(1).ToList();
            if (toDelete.Any())
            {
                context.ModelTestRuns.RemoveRange(toDelete);
                context.SaveChanges();
            }
        }

        var newRun = new ModelTestRun
        {
            ModelId = modelId,
            TestGroup = testCode,
            Passed = passed,
            DurationMs = durationMs,
            RunDate = DateTime.UtcNow.ToString("o"),
            Description = description,
            Notes = notes,
            TestFolder = testFolder
        };

        context.ModelTestRuns.Add(newRun);
        context.SaveChanges();
        return newRun.Id;
    }

    /// <summary>
    /// Update an existing test run's passed flag and/or duration_ms.
    /// </summary>
    public void UpdateTestRunResult(int runId, bool? passed = null, long? durationMs = null)
    {
        using var context = CreateDbContext();
        var run = context.ModelTestRuns.Find(runId);
        if (run == null) return;
        if (passed.HasValue) run.Passed = passed.Value;
        if (durationMs.HasValue) run.DurationMs = durationMs.Value;
        context.SaveChanges();
    }

    public void UpdateTestRunNotes(int runId, string? notes)
    {
        using var context = CreateDbContext();
        var run = context.ModelTestRuns.Find(runId);
        if (run == null) return;
        run.Notes = notes;
        context.SaveChanges();
    }

    public int AddTestStep(int runId, int stepNumber, string stepName, string? inputJson = null)
    {
        using var context = CreateDbContext();
        var step = new ModelTestStep
        {
            RunId = runId,
            StepNumber = stepNumber,
            StepName = stepName,
            InputJson = inputJson,
            Passed = false
        };
        context.ModelTestSteps.Add(step);
        context.SaveChanges();
        return step.Id;
    }

    public void UpdateTestStepResult(int stepId, bool passed, string? outputJson = null, string? error = null, long? durationMs = null)
    {
        using var context = CreateDbContext();
        var step = context.ModelTestSteps.Find(stepId);
        if (step == null) return;
        step.Passed = passed;
        if (!string.IsNullOrWhiteSpace(outputJson)) step.OutputJson = outputJson;
        if (!string.IsNullOrWhiteSpace(error)) step.Error = error;
        if (durationMs.HasValue) step.DurationMs = durationMs.Value;
        context.SaveChanges();
    }

    /// <summary>
    /// Discover locally installed Ollama models and insert only those that are not present in the models table.
    /// Returns the number of newly added models.
    /// </summary>
    public async Task<int> AddLocalOllamaModelsAsync()
    {
            try
            {
                var list = _ollamaMonitor == null ? null : await _ollamaMonitor.GetInstalledModelsAsync();
                if (list == null) return 0;
            var added = 0;
            foreach (var m in list)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(m?.Name)) continue;
                    var existing = GetModelInfo(m.Name);
                    if (existing != null) continue; // do not update existing models

                    var ctx = 0;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(m.Context))
                        {
                            var digits = new string(m.Context.Where(char.IsDigit).ToArray());
                            if (int.TryParse(digits, out var parsed)) ctx = parsed;
                        }
                    }
                    catch { }

                    var mi = new ModelInfo
                    {
                        Name = m.Name ?? string.Empty,
                        Provider = "ollama",
                        IsLocal = true,
                        MaxContext = ctx > 0 ? ctx : 4096,
                        ContextToUse = ctx > 0 ? ctx : 4096,
                        CostInPerToken = 0.0,
                        CostOutPerToken = 0.0,
                        LimitTokensDay = 0,
                        LimitTokensWeek = 0,
                        LimitTokensMonth = 0,
                        Metadata = System.Text.Json.JsonSerializer.Serialize(new { m.Id, m.Size, m.Processor, m.Context, m.Until }),
                        Enabled = true
                    };

                    UpsertModel(mi);
                    added++;
                }
                catch { /* ignore per-model failures */ }
            }

            return added;
        }
        catch
        {
            return 0;
        }
    }

    public int AddTestAsset(int stepId, string fileType, string filePath, string? description = null, double? durationSec = null, long? sizeBytes = null, long? storyId = null)
    {
        using var context = CreateDbContext();
        var asset = new ModelTestAsset
        {
            StepId = stepId,
            FileType = fileType,
            FilePath = filePath,
            Description = description,
            DurationSec = durationSec,
            SizeBytes = sizeBytes,
            StoryId = storyId
        };
        context.ModelTestAssets.Add(asset);
        context.SaveChanges();
        return asset.Id;
    }

    // Overload with all evaluation fields parsed directly
    public long AddStoryEvaluation(
        long storyId,
        int narrativeScore, string narrativeDefects,
        int originalityScore, string originalityDefects,
        int emotionalScore, string emotionalDefects,
        int actionScore, string actionDefects,
        double totalScore,
        string rawJson,
        int? modelId = null,
        int? agentId = null)
    {
        using var context = CreateDbContext();
        var eval = new StoryEvaluation
        {
            StoryId = storyId,
            NarrativeCoherenceScore = narrativeScore,
            NarrativeCoherenceDefects = narrativeDefects ?? string.Empty,
            OriginalityScore = originalityScore,
            OriginalityDefects = originalityDefects ?? string.Empty,
            EmotionalImpactScore = emotionalScore,
            EmotionalImpactDefects = emotionalDefects ?? string.Empty,
            ActionScore = actionScore,
            ActionDefects = actionDefects ?? string.Empty,
            TotalScore = totalScore,
            RawJson = rawJson ?? string.Empty,
            ModelId = modelId.HasValue ? (long?)modelId.Value : null,
            AgentId = agentId,
            Timestamp = DateTime.UtcNow.ToString("o")
        };
        context.StoryEvaluations.Add(eval);
        context.SaveChanges();

        if (modelId.HasValue)
        {
            RecalculateWriterScore(modelId.Value);
        }

        UpdateStoryStatusAfterEvaluation(context, storyId, agentId);
        return eval.Id;
    }

    public long AddStoryEvaluation(long storyId, string rawJson, double totalScore, int? modelId = null, int? agentId = null)
    {
        using var context = CreateDbContext();
        // Deduplicate: avoid inserting identical evaluation (same story, same agent, same raw JSON)
        try
        {
            if (!string.IsNullOrWhiteSpace(rawJson) && agentId.HasValue)
            {
                var existing = context.StoryEvaluations.FirstOrDefault(se => se.StoryId == storyId && se.AgentId == agentId && se.RawJson == rawJson);
                if (existing != null) return existing.Id;
            }
        }
        catch { /* best-effort dedupe, ignore on error */ }

        // Try parse JSON to extract category fields - best effort
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawJson ?? string.Empty);
            var root = doc.RootElement;
            int GetScoreFromCategory(string cat)
            {
                try
                {
                    if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty(cat, out var catEl) && catEl.ValueKind == System.Text.Json.JsonValueKind.Object && catEl.TryGetProperty("score", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Number) return s.GetInt32();
                    var alt = cat + "_score";
                    if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty(alt, out var altEl) && altEl.ValueKind == System.Text.Json.JsonValueKind.Number) return altEl.GetInt32();
                }
                catch { }
                return 0;
            }
            string GetDefectsFromCategory(string cat)
            {
                try
                {
                    if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty(cat, out var catEl) && catEl.ValueKind == System.Text.Json.JsonValueKind.Object && catEl.TryGetProperty("defects", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String) return d.GetString() ?? string.Empty;
                    var alt = cat + "_defects";
                    if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty(alt, out var altEl) && altEl.ValueKind == System.Text.Json.JsonValueKind.String) return altEl.GetString() ?? string.Empty;
                }
                catch { }
                return string.Empty;
            }

            var nc = GetScoreFromCategory("narrative_coherence");
            var ncdef = GetDefectsFromCategory("narrative_coherence");
            var org = GetScoreFromCategory("originality");
            var orgdef = GetDefectsFromCategory("originality");
            var em = GetScoreFromCategory("emotional_impact");
            var emdef = GetDefectsFromCategory("emotional_impact");

            var action = GetScoreFromCategory("action");
            if (action == 0)
            {
                action = GetScoreFromCategory("pacing");
            }
            var actionDef = GetDefectsFromCategory("action");
            if (string.IsNullOrEmpty(actionDef))
            {
                actionDef = GetDefectsFromCategory("pacing");
            }

            var eval = new StoryEvaluation
            {
                StoryId = storyId,
                NarrativeCoherenceScore = nc,
                NarrativeCoherenceDefects = ncdef ?? string.Empty,
                OriginalityScore = org,
                OriginalityDefects = orgdef ?? string.Empty,
                EmotionalImpactScore = em,
                EmotionalImpactDefects = emdef ?? string.Empty,
                ActionScore = action,
                ActionDefects = actionDef ?? string.Empty,
                TotalScore = totalScore,
                RawJson = rawJson ?? string.Empty,
                ModelId = modelId.HasValue ? (long?)modelId.Value : null,
                AgentId = agentId,
                Timestamp = DateTime.UtcNow.ToString("o")
            };
            context.StoryEvaluations.Add(eval);
            context.SaveChanges();

            if (modelId.HasValue)
            {
                RecalculateWriterScore(modelId.Value);
            }

            UpdateStoryStatusAfterEvaluation(context, storyId, agentId);

            return eval.Id;
        }
        catch (Exception)
        {
            // Best-effort fallback: insert minimal evaluation with defaulted category fields
            try
            {
                var eval = new StoryEvaluation
                {
                    StoryId = storyId,
                    NarrativeCoherenceScore = 0,
                    NarrativeCoherenceDefects = string.Empty,
                    OriginalityScore = 0,
                    OriginalityDefects = string.Empty,
                    EmotionalImpactScore = 0,
                    EmotionalImpactDefects = string.Empty,
                    ActionScore = 0,
                    ActionDefects = string.Empty,
                    TotalScore = totalScore,
                    RawJson = rawJson ?? string.Empty,
                    ModelId = modelId.HasValue ? (long?)modelId.Value : null,
                    AgentId = agentId,
                    Timestamp = DateTime.UtcNow.ToString("o")
                };
                context.StoryEvaluations.Add(eval);
                context.SaveChanges();

                if (modelId.HasValue)
                {
                    RecalculateWriterScore(modelId.Value);
                }

                UpdateStoryStatusAfterEvaluation(context, storyId, agentId);

                return eval.Id;
            }
            catch
            {
                // give up after fallback failure
                return 0;
            }
        }
    }

    public void RecalculateWriterScore(int modelId)
    {
        using var context = CreateDbContext();
        try
        {
            var model = context.Models.FirstOrDefault(m => m.Id == modelId);
            if (model == null) return;

            var query = from se in context.StoryEvaluations
                        join s in context.Stories on se.StoryId equals s.Id
                        where s.ModelId == modelId
                        select se.TotalScore;

            var count = query.Count();
            double writerScore = 0;
            if (count > 0)
            {
                var sum = query.Sum();
                writerScore = (sum * 10.0) / (count * 100.0);
            }

            model.WriterScore = writerScore;
            context.SaveChanges();
        }
        catch
        {
            // best-effort; don't throw on score recalculation failure
        }
    }

    /// <summary>
    /// Calculate score for a test group based on passed tests in the latest run.
    /// Formula: (passed_count / total_count) * 10, rounded to 1 decimal.
    /// </summary>
    private void RecalculateGroupScore(System.Data.IDbConnection conn, string groupName, string scoreColumn)
    {
        var sql = $@"
UPDATE models
SET {scoreColumn} = (
    SELECT CASE 
        WHEN COUNT(s.id) = 0 THEN 0
        ELSE ROUND((SUM(CASE WHEN s.passed = 1 THEN 1 ELSE 0 END) * 10.0) / COUNT(s.id), 1)
    END
    FROM model_test_steps s
    INNER JOIN model_test_runs r ON s.run_id = r.id
    WHERE r.model_id = models.Id
      AND r.test_group = @groupName
      AND r.id = (
          SELECT id FROM model_test_runs 
          WHERE model_id = models.Id AND test_group = @groupName 
          ORDER BY run_date DESC LIMIT 1
      )
);";
        
        conn.Execute(sql, new { groupName });
    }

    public void RecalculateAllWriterScores()
    {
        using var conn = CreateConnection();
        conn.Open();
        
        // Reset all scores to 0
        conn.Execute("UPDATE models SET WriterScore = 0, BaseScore = 0, TextEvalScore = 0, TtsScore = 0, MusicScore = 0, FxScore = 0, AmbientScore = 0, TotalScore = 0;");
        
        // Recalculate WriterScore (complex calculation based on story evaluations)
        var writerSql = @"
UPDATE models
SET WriterScore = (
    SELECT CASE 
        WHEN COUNT(*) = 0 THEN 0
        ELSE (COALESCE(SUM(se.total_score), 0) * 10.0) / (COUNT(*) * 100.0)
    END
    FROM stories_evaluations se
    INNER JOIN stories s ON s.id = se.story_id
    WHERE s.model_id = models.Id
);";
        conn.Execute(writerSql);
        
        // Calculate group-based scores (base, texteval, tts, music, fx, ambient)
        RecalculateGroupScore(conn, "base", "BaseScore");
        RecalculateGroupScore(conn, "texteval", "TextEvalScore");
        RecalculateGroupScore(conn, "tts", "TtsScore");
        RecalculateGroupScore(conn, "music", "MusicScore");
        RecalculateGroupScore(conn, "fx", "FxScore");
        RecalculateGroupScore(conn, "ambient", "AmbientScore");
        
        // Calculate TotalScore (sum of all scores)
        var totalSql = @"
UPDATE models
SET TotalScore = (
    WriterScore +
    BaseScore +
    TextEvalScore +
    TtsScore +
    MusicScore +
    FxScore +
    AmbientScore
);";
        conn.Execute(totalSql);
    }

    public List<TinyGenerator.Models.LogEntry> GetStoryEvaluationsByStoryId(long storyId)
    {
        // For now return as a LogEntry-like structure or a dedicated DTO. Simpler approach: return raw rows with JSON in raw_json.
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT se.id AS Id, se.ts AS Ts, COALESCE(m.Name,'') AS Model, COALESCE(a.name,'') AS Category, se.total_score AS Score, se.raw_json AS Message FROM stories_evaluations se LEFT JOIN models m ON se.model_id = m.Id LEFT JOIN agents a ON se.agent_id = a.id WHERE se.story_id = @sid ORDER BY se.id";
        return conn.Query<TinyGenerator.Models.LogEntry>(sql, new { sid = storyId }).ToList();
    }

    public List<TinyGenerator.Models.StoryEvaluation> GetStoryEvaluations(long storyId)
    {
        using var conn = CreateConnection();
        conn.Open();
         var sql = @"SELECT se.id AS Id,
                      se.story_id AS StoryId,
                      se.narrative_coherence_score AS NarrativeCoherenceScore,
                      se.narrative_coherence_defects AS NarrativeCoherenceDefects,
                      se.originality_score AS OriginalityScore,
                      se.originality_defects AS OriginalityDefects,
                      se.emotional_impact_score AS EmotionalImpactScore,
                      se.emotional_impact_defects AS EmotionalImpactDefects,
                      se.action_score AS ActionScore,
                      se.action_defects AS ActionDefects,
                      se.total_score AS TotalScore,
                      se.raw_json AS RawJson,
                      se.model_id AS ModelId,
                      se.agent_id AS AgentId,
                      se.ts AS Ts,
                      COALESCE(m.Name, '') AS Model,
                      COALESCE(a.name, '') AS AgentName,
                      COALESCE(ma.Name, '') AS AgentModel,
                      se.total_score AS Score
                  FROM stories_evaluations se
                  LEFT JOIN models m ON se.model_id = m.Id
                  LEFT JOIN agents a ON se.agent_id = a.id
                  LEFT JOIN models ma ON a.model_id = ma.Id
                  WHERE se.story_id = @sid
                  ORDER BY se.id";
        var list = conn.Query<TinyGenerator.Models.StoryEvaluation>(sql, new { sid = storyId }).ToList();

        try
        {
            // If a global coherence row exists for this story, expose it as a synthetic StoryEvaluation
            // so the UI can display the coherence result alongside other evaluations.
            var global = GetGlobalCoherence((int)storyId);
            if (global != null)
            {
                // Create a synthetic evaluation entry for coherence if not already present
                var already = list.Any(e => e.RawJson != null && e.RawJson.Contains("global_coherence", StringComparison.OrdinalIgnoreCase));
                if (!already)
                {
                    var coherenceScore = (double)Math.Round(global.GlobalCoherenceValue * 10.0, 2);
                    var synthetic = new TinyGenerator.Models.StoryEvaluation
                    {
                        Id = global.Id > 0 ? -global.Id : 0, // negative id to avoid collision
                        StoryId = storyId,
                        NarrativeCoherenceScore = (int)Math.Round(global.GlobalCoherenceValue * 10.0),
                        NarrativeCoherenceDefects = string.Empty,
                        OriginalityScore = 0,
                        OriginalityDefects = string.Empty,
                        EmotionalImpactScore = 0,
                        EmotionalImpactDefects = string.Empty,
                        ActionScore = 0,
                        ActionDefects = string.Empty,
                        TotalScore = coherenceScore,
                        RawJson = System.Text.Json.JsonSerializer.Serialize(new { global.GlobalCoherenceValue, global.ChunkCount, global.Notes, global.Ts }),
                        Model = string.Empty,
                        ModelId = null,
                        AgentId = null,
                        AgentName = "coherence",
                        AgentModel = string.Empty,
                        Timestamp = global.Ts ?? string.Empty
                    };
                    list.Add(synthetic);
                }
            }
        }
        catch
        {
            // best-effort: do not fail evaluation retrieval if global coherence cannot be read
        }

        return list;
    }

    public (int count, double averageScore) GetStoryEvaluationStats(long storyId)
    {
        using var context = CreateDbContext();
        var query = context.StoryEvaluations
            .Where(e => e.StoryId == storyId)
            .Select(e => e.TotalScore);

        var count = query.Count();
        var average = count > 0 ? query.Average() : 0.0;
        return (count, average);
    }

    public void DeleteStoryEvaluationById(long evaluationId)
    {
        using var context = CreateDbContext();
        var eval = context.StoryEvaluations.Find(evaluationId);
        if (eval != null)
        {
            context.StoryEvaluations.Remove(eval);
            context.SaveChanges();
        }
    }

    /// <summary>
    /// Delete all evaluations related to a story (best-effort).
    /// </summary>
    public void DeleteEvaluationsForStory(long storyId)
    {
        using var context = CreateDbContext();
        var evals = context.StoryEvaluations.Where(e => e.StoryId == storyId).ToList();
        if (evals.Count == 0) return;
        context.StoryEvaluations.RemoveRange(evals);
        context.SaveChanges();
    }

    /// <summary>
    /// Delete all story evaluations (and related global coherence rows) across the whole database.
    /// Also clears aggregate fields on stories (Score/Eval) as best-effort.
    /// </summary>
    public void DeleteAllEvaluations()
    {
        using var conn = CreateDapperConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        conn.Execute("DELETE FROM stories_evaluations;", transaction: tx);

        // best-effort: if present, wipe coherence rows too
        try
        {
            var hasGlobalCoherence = conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='global_coherence'",
                transaction: tx);
            if (hasGlobalCoherence > 0)
            {
                conn.Execute("DELETE FROM global_coherence;", transaction: tx);
            }
        }
        catch
        {
            // ignore
        }

        // best-effort: reset aggregate story columns if present
        try
        {
            var hasScore = conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM pragma_table_info('stories') WHERE name='score'",
                transaction: tx);
            if (hasScore > 0)
            {
                conn.Execute("UPDATE stories SET score = 0;", transaction: tx);
            }

            var hasEval = conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM pragma_table_info('stories') WHERE name='eval'",
                transaction: tx);
            if (hasEval > 0)
            {
                conn.Execute("UPDATE stories SET eval = '';", transaction: tx);
            }
        }
        catch
        {
            // ignore
        }

        tx.Commit();
    }

    // Stories CRUD operations
    /// <summary>
    /// LEGACY: Saves stories using fixed WriterA/B/C structure.
    /// Used only by legacy test pages. For production, stories are saved automatically
    /// during multi-step execution via MultiStepOrchestrationService.
    /// </summary>
    public long SaveGeneration(string prompt, TinyGenerator.Models.StoryGenerationResult r, string? memoryKey = null)
    {
        using var context = CreateDbContext();
        var genId = Guid.NewGuid().ToString();
        var ts = DateTime.UtcNow.ToString("o");

        int? aidA = GetAgentIdByName("WriterA");
        var charCountA = (r.StoryA ?? string.Empty).Length;
        var storyA = new StoryRecord
        {
            GenerationId = genId,
            MemoryKey = memoryKey ?? genId,
            Timestamp = ts,
            Prompt = prompt ?? string.Empty,
            StoryRaw = r.StoryA ?? string.Empty,
            CharCount = charCountA,
            Eval = r.EvalA ?? string.Empty,
            Score = r.ScoreA,
            Approved = !string.IsNullOrEmpty(r.Approved),
            StatusId = InitialStoryStatusId,
            AgentId = aidA
        };
        context.Stories.Add(storyA);

        int? aidB = GetAgentIdByName("WriterB");
        var charCountB = (r.StoryB ?? string.Empty).Length;
        var storyB = new StoryRecord
        {
            GenerationId = genId,
            MemoryKey = memoryKey ?? genId,
            Timestamp = ts,
            Prompt = prompt ?? string.Empty,
            StoryRaw = r.StoryB ?? string.Empty,
            CharCount = charCountB,
            Eval = r.EvalB ?? string.Empty,
            Score = r.ScoreB,
            Approved = !string.IsNullOrEmpty(r.Approved),
            StatusId = InitialStoryStatusId,
            AgentId = aidB
        };
        context.Stories.Add(storyB);

        int? aidC = GetAgentIdByName("WriterC");
        var charCountC = (r.StoryC ?? string.Empty).Length;
        var storyC = new StoryRecord
        {
            GenerationId = genId,
            MemoryKey = memoryKey ?? genId,
            Timestamp = ts,
            Prompt = prompt ?? string.Empty,
            StoryRaw = r.StoryC ?? string.Empty,
            CharCount = charCountC,
            Eval = r.EvalC ?? string.Empty,
            Score = r.ScoreC,
            Approved = !string.IsNullOrEmpty(r.Approved),
            StatusId = InitialStoryStatusId,
            AgentId = aidC
        };
        context.Stories.Add(storyC);

        context.SaveChanges();
        return storyC.Id != 0 ? storyC.Id : storyB.Id;
    }

    public List<TinyGenerator.Models.StoryRecord> GetAllStories()
    {
        using var context = CreateDbContext();
        var stories = context.Stories.OrderByDescending(s => s.Id).ToList();
        // Populate navigation properties
        foreach (var s in stories)
        {
            if (s.StatusId.HasValue)
            {
                var status = context.StoriesStatus.Find(s.StatusId.Value);
                if (status != null)
                {
                    s.Status = status.Code ?? string.Empty;
                    s.StatusDescription = status.Description;
                    s.StatusColor = status.Color;
                    s.StatusStep = status.Step;
                    s.StatusOperationType = status.OperationType;
                    s.StatusAgentType = status.AgentType;
                    s.StatusFunctionName = status.FunctionName;
                }
            }
            if (s.ModelId.HasValue)
            {
                var model = context.Models.Find(s.ModelId.Value);
                s.Model = model?.Name ?? string.Empty;
            }
            if (s.AgentId.HasValue)
            {
                var agent = context.Agents.Find(s.AgentId.Value);
                s.Agent = agent?.Name ?? string.Empty;
            }
        }
        return stories;
    }

    public TinyGenerator.Models.StoryRecord? GetStoryById(long id)
    {
        using var context = CreateDbContext();
        var s = context.Stories.FirstOrDefault(st => st.Id == id);
        if (s == null) return null;
        // Populate navigation properties
        if (s.StatusId.HasValue)
        {
            var status = context.StoriesStatus.Find(s.StatusId.Value);
            if (status != null)
            {
                s.Status = status.Code ?? string.Empty;
                s.StatusDescription = status.Description;
                s.StatusColor = status.Color;
                s.StatusStep = status.Step;
                s.StatusOperationType = status.OperationType;
                s.StatusAgentType = status.AgentType;
                s.StatusFunctionName = status.FunctionName;
            }
        }
        if (s.ModelId.HasValue)
        {
            var model = context.Models.Find(s.ModelId.Value);
            s.Model = model?.Name ?? string.Empty;
        }
        if (s.AgentId.HasValue)
        {
            var agent = context.Agents.Find(s.AgentId.Value);
            s.Agent = agent?.Name ?? string.Empty;
        }
        return s;
    }

    public long? GetStoryCorrelationId(long storyDbId)
    {
        using var context = CreateDbContext();
        var s = context.Stories.Find(storyDbId);
        return s?.StoryId;
    }

    public long EnsureStoryCorrelationId(long storyDbId, long newStoryId)
    {
        using var context = CreateDbContext();
        var s = context.Stories.Find(storyDbId);
        if (s == null) throw new InvalidOperationException($"Story {storyDbId} not found");
        if (s.StoryId.HasValue && s.StoryId.Value > 0) return s.StoryId.Value;

        s.StoryId = newStoryId;
        context.SaveChanges();
        return newStoryId;
    }

    public void DeleteStoryById(long id)
    {
        using var context = CreateDbContext();
        var story = context.Stories.Find(id);
        if (story == null) return;
        var genId = story.GenerationId;
        if (!string.IsNullOrEmpty(genId))
        {
            var related = context.Stories.Where(s => s.GenerationId == genId).ToList();
            context.Stories.RemoveRange(related);
        }
        else
        {
            context.Stories.Remove(story);
        }
        context.SaveChanges();
    }

    public (int? runId, int? stepId) GetTestInfoForStory(long storyId)
    {
        using var context = CreateDbContext();
        var asset = context.ModelTestAssets.FirstOrDefault(a => a.StoryId == storyId);
        if (asset == null) return (null, null);
        var step = context.ModelTestSteps.Find(asset.StepId);
        if (step == null) return (null, asset.StepId);
        return (step.RunId, asset.StepId);
    }

    public long InsertSingleStory(string prompt, string story, int? modelId = null, int? agentId = null, double score = 0.0, string? eval = null, int approved = 0, int? statusId = null, string? memoryKey = null, string? title = null, int? serieId = null, int? serieEpisode = null)
    {
        using var context = CreateDbContext();
        var ts = DateTime.UtcNow.ToString("o");
        var genId = Guid.NewGuid().ToString();
        
        // Generate folder name
        string? folder = null;
        if (agentId.HasValue)
        {
            var agent = context.Agents.Find(agentId.Value);
            var sanitizedAgentName = SanitizeFolderName(agent?.Name ?? $"agent{agentId.Value}");
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            folder = $"{sanitizedAgentName}_{timestamp}";
        }
        
        var charCount = (story ?? string.Empty).Length;
        var storyRecord = new StoryRecord
        {
            StoryId = null,
            GenerationId = genId,
            MemoryKey = memoryKey ?? genId,
            Timestamp = ts,
            Prompt = prompt ?? string.Empty,
            StoryRaw = story ?? string.Empty,
            StoryRevised = story ?? string.Empty,
            Title = title ?? string.Empty,
            CharCount = charCount,
            Eval = eval ?? string.Empty,
            Score = score,
            Approved = approved != 0,
            StatusId = statusId ?? InitialStoryStatusId,
            Folder = null,
            ModelId = modelId,
            AgentId = agentId,
            SerieId = serieId,
            SerieEpisode = serieEpisode
        };
        context.Stories.Add(storyRecord);
        
        try
        {
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            var fullMessage = ExceptionHelper.GetFullExceptionMessage(ex);
            Console.WriteLine($"[DB][ERROR] Failed to insert new story: {fullMessage}");
            throw new InvalidOperationException($"Failed to insert new story: {fullMessage}", ex);
        }

        // After saving we have the story Id; construct a folder name prefixed with a 5-digit zero-padded id
        try
        {
            var paddedId = storyRecord.Id.ToString("D5");
            string finalFolder;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                finalFolder = SanitizeFolderName($"{paddedId}_{folder}");
            }
            else
            {
                finalFolder = SanitizeFolderName($"{paddedId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
            }
            storyRecord.Folder = finalFolder;
            context.SaveChanges();
        }
        catch
        {
            // best-effort: if folder update fails, keep original state
        }

        return storyRecord.Id;
    }

    public long InsertSingleStory(string prompt, string story, long? storyId, int? modelId = null, int? agentId = null, double score = 0.0, string? eval = null, int approved = 0, int? statusId = null, string? memoryKey = null, string? title = null, int? serieId = null, int? serieEpisode = null)
    {
        using var context = CreateDbContext();
        var ts = DateTime.UtcNow.ToString("o");
        var genId = Guid.NewGuid().ToString();

        string? folder = null;
        if (agentId.HasValue)
        {
            var agent = context.Agents.Find(agentId.Value);
            var sanitizedAgentName = SanitizeFolderName(agent?.Name ?? $"agent{agentId.Value}");
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            folder = $"{sanitizedAgentName}_{timestamp}";
        }

        var charCount = (story ?? string.Empty).Length;
        var storyRecord = new StoryRecord
        {
            StoryId = storyId,
            GenerationId = genId,
            MemoryKey = memoryKey ?? genId,
            Timestamp = ts,
            Prompt = prompt ?? string.Empty,
            StoryRaw = story ?? string.Empty,
            StoryRevised = story ?? string.Empty,
            Title = title ?? string.Empty,
            CharCount = charCount,
            Eval = eval ?? string.Empty,
            Score = score,
            Approved = approved != 0,
            StatusId = statusId ?? InitialStoryStatusId,
            Folder = null,
            ModelId = modelId,
            AgentId = agentId,
            SerieId = serieId,
            SerieEpisode = serieEpisode
        };

        context.Stories.Add(storyRecord);
        
        try
        {
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            var fullMessage = ExceptionHelper.GetFullExceptionMessage(ex);
            Console.WriteLine($"[DB][ERROR] Failed to insert new story with specified ID {storyId}: {fullMessage}");
            throw new InvalidOperationException($"Failed to insert new story: {fullMessage}", ex);
        }

        try
        {
            var paddedId = storyRecord.Id.ToString("D5");
            string finalFolder;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                finalFolder = SanitizeFolderName($"{paddedId}_{folder}");
            }
            else
            {
                finalFolder = SanitizeFolderName($"{paddedId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
            }
            storyRecord.Folder = finalFolder;
            context.SaveChanges();
        }
        catch
        {
        }

        return storyRecord.Id;
    }

    public void UpdateStorySeriesInfo(long storyId, int? serieId, int? serieEpisode, bool allowSeriesUpdate = false)
    {
        if (!serieId.HasValue && !serieEpisode.HasValue) return;
        using var context = CreateDbContext();
        var story = context.Stories.Find(storyId);
        if (story == null) return;
        
        // Series metadata (serie_id/serie_episode) should be stable: only allow updates if allowSeriesUpdate=true (explicit Edit page operation)
        if (!allowSeriesUpdate)
        {
            var attemptedOverwrite = false;
            if (serieId.HasValue && story.SerieId.HasValue && story.SerieId.Value != serieId.Value) attemptedOverwrite = true;
            if (serieEpisode.HasValue && story.SerieEpisode.HasValue && story.SerieEpisode.Value != serieEpisode.Value) attemptedOverwrite = true;
            if (attemptedOverwrite)
            {
                Console.WriteLine($"[DB][WARN] Blocked series metadata overwrite for story {storyId}: attempted serieId={serieId?.ToString() ?? "<null>"}, serieEpisode={serieEpisode?.ToString() ?? "<null>"}; current serieId={story.SerieId?.ToString() ?? "<null>"}, serieEpisode={story.SerieEpisode?.ToString() ?? "<null>"}");
                return;
            }
            // Allow initializing only when NULL
            if (serieId.HasValue && !story.SerieId.HasValue) story.SerieId = serieId;
            if (serieEpisode.HasValue && !story.SerieEpisode.HasValue) story.SerieEpisode = serieEpisode;
        }
        else
        {
            // Explicit update allowed (from Edit page)
            story.SerieId = serieId;
            story.SerieEpisode = serieEpisode;
        }
        
        try
        {
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            var fullMessage = ExceptionHelper.GetFullExceptionMessage(ex);
            Console.WriteLine($"[DB][ERROR] Failed to save series info for story {storyId}: {fullMessage}");
            throw new InvalidOperationException($"Failed to update series info for story {storyId}: {fullMessage}", ex);
        }
    }

    public long GetMaxStoryId()
    {
        using var conn = CreateDapperConnection();
        conn.Open();
        try
        {
            return conn.ExecuteScalar<long>("SELECT COALESCE(MAX(story_id), 0) FROM stories");
        }
        catch
        {
            return conn.ExecuteScalar<long>("SELECT COALESCE(MAX(id), 0) FROM stories");
        }
    }

    public int GetMaxLogThreadId()
    {
        using var conn = CreateDapperConnection();
        conn.Open();
        try
        {
            return conn.ExecuteScalar<int>("SELECT COALESCE(MAX(ThreadId), 0) FROM Log");
        }
        catch
        {
            return 0;
        }
    }

    public long? GetNumeratorState(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        using var conn = CreateDapperConnection();
        conn.Open();
        try
        {
            return conn.ExecuteScalar<long?>("SELECT value FROM numerators_state WHERE key = @k LIMIT 1", new { k = key.Trim() });
        }
        catch
        {
            return null;
        }
    }

    public void SetNumeratorState(string key, long value)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        using var conn = CreateDapperConnection();
        conn.Open();
        try
        {
            conn.Execute(@"INSERT INTO numerators_state(key, value)
VALUES(@k, @v)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;", new { k = key.Trim(), v = value });
        }
        catch
        {
            // best-effort
        }
    }

    private int? ResolveStoryStatusId(string? statusCode)
    {
        if (string.IsNullOrWhiteSpace(statusCode)) return null;
        using var context = CreateDbContext();
        var status = context.StoriesStatus.FirstOrDefault(s => s.Code == statusCode);
        return status?.Id;
    }

    private string SanitizeFolderName(string name)
    {
        // Remove or replace invalid characters for folder names
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in invalidChars)
        {
            name = name.Replace(c, '_');
        }
        return name.Trim().Replace(" ", "_").ToLowerInvariant();
    }

    public bool UpdateStoryById(long id, string? story = null, int? modelId = null, int? agentId = null, int? statusId = null, bool updateStatus = false, bool allowCreatorMetadataUpdate = false)
    {
        using var context = CreateDbContext();
        var storyRecord = context.Stories.Find(id);
        if (storyRecord == null) return false;

        // Creator metadata (model_id/agent_id) must be stable: it can be SET only if currently NULL,
        // unless allowCreatorMetadataUpdate=true (explicit admin/repair operation).
        if (!allowCreatorMetadataUpdate)
        {
            var attemptedOverwrite = false;
            if (modelId.HasValue && storyRecord.ModelId.HasValue && storyRecord.ModelId.Value != modelId.Value) attemptedOverwrite = true;
            if (agentId.HasValue && storyRecord.AgentId.HasValue && storyRecord.AgentId.Value != agentId.Value) attemptedOverwrite = true;
            if (attemptedOverwrite)
            {
                Console.WriteLine($"[DB][WARN] Blocked creator metadata overwrite for story {id}: attempted modelId={modelId?.ToString() ?? "<null>"}, agentId={agentId?.ToString() ?? "<null>"}; current modelId={storyRecord.ModelId?.ToString() ?? "<null>"}, agentId={storyRecord.AgentId?.ToString() ?? "<null>"}");
            }
        }

        if (story != null)
        {
            storyRecord.StoryRaw = story;
            storyRecord.CharCount = story.Length;
        }
        if (allowCreatorMetadataUpdate)
        {
            if (modelId.HasValue) storyRecord.ModelId = modelId.Value;
            if (agentId.HasValue) storyRecord.AgentId = agentId.Value;
        }
        else
        {
            // Allow initializing only when NULL
            if (modelId.HasValue && !storyRecord.ModelId.HasValue) storyRecord.ModelId = modelId.Value;
            if (agentId.HasValue && !storyRecord.AgentId.HasValue) storyRecord.AgentId = agentId.Value;
        }
        if (updateStatus) storyRecord.StatusId = statusId;
        
        try
        {
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            var fullMessage = ExceptionHelper.GetFullExceptionMessage(ex);
            Console.WriteLine($"[DB][ERROR] Failed to save changes for story {id}: {fullMessage}");
            throw new InvalidOperationException($"Failed to update story {id}: {fullMessage}", ex);
        }
        
        return true;
    }

    public bool UpdateStoryRevised(long storyId, string storyRevised)
    {
        using var context = CreateDbContext();
        var storyRecord = context.Stories.Find(storyId);
        if (storyRecord == null) return false;
        storyRecord.StoryRevised = storyRevised ?? string.Empty;
        
        try
        {
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            var fullMessage = ExceptionHelper.GetFullExceptionMessage(ex);
            Console.WriteLine($"[DB][ERROR] Failed to save revised story for {storyId}: {fullMessage}");
            throw new InvalidOperationException($"Failed to update revised story {storyId}: {fullMessage}", ex);
        }
        
        return true;
    }

    /// <summary>
    /// Bulk realign of creator model id for stories:
    /// sets stories.model_id = agents.model_id when a story has an agent_id and the ids are mismatched.
    /// Does not change agent_id.
    /// </summary>
    public int RealignStoriesCreatorModelIds()
    {
        using var context = CreateDbContext();

        // SQLite supports correlated subqueries in UPDATE.
        // Only touch stories that have an agent, where agent has a model, and story model differs.
        var sql = @"
UPDATE stories
SET model_id = (SELECT a.model_id FROM agents a WHERE a.id = stories.agent_id)
WHERE agent_id IS NOT NULL
  AND (SELECT a.model_id FROM agents a WHERE a.id = stories.agent_id) IS NOT NULL
  AND (model_id IS NULL OR model_id != (SELECT a.model_id FROM agents a WHERE a.id = stories.agent_id));
";

        return context.Database.ExecuteSqlRaw(sql);
    }

    /// <summary>
    /// Updates the characters JSON field for a story.
    /// </summary>
    public bool UpdateStoryCharacters(long storyId, string charactersJson)
    {
        using var context = CreateDbContext();
        var storyRecord = context.Stories.Find(storyId);
        if (storyRecord == null) return false;
        storyRecord.Characters = charactersJson;
        
        try
        {
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            var fullMessage = ExceptionHelper.GetFullExceptionMessage(ex);
            Console.WriteLine($"[DB][ERROR] Failed to save characters for story {storyId}: {fullMessage}");
            throw new InvalidOperationException($"Failed to update characters for story {storyId}: {fullMessage}", ex);
        }
        
        return true;
    }

    /// <summary>
    /// Updates the summary field for a story.
    /// </summary>
    public bool UpdateStorySummary(long storyId, string summary)
    {
        using var context = CreateDbContext();
        var storyRecord = context.Stories.Find(storyId);
        if (storyRecord == null) return false;
        storyRecord.Summary = summary;
        
        try
        {
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            var fullMessage = ExceptionHelper.GetFullExceptionMessage(ex);
            Console.WriteLine($"[DB][ERROR] Failed to save summary for story {storyId}: {fullMessage}");
            throw new InvalidOperationException($"Failed to update summary for story {storyId}: {fullMessage}", ex);
        }
        
        return true;
    }

    /// <summary>
    /// Updates the story_structure field for a story.
    /// </summary>
    public bool UpdateStoryStructure(long storyId, string structureJson)
    {
        using var context = CreateDbContext();
        var storyRecord = context.Stories.Find(storyId);
        if (storyRecord == null) return false;
        storyRecord.StoryStructure = structureJson;
        
        try
        {
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            var fullMessage = ExceptionHelper.GetFullExceptionMessage(ex);
            Console.WriteLine($"[DB][ERROR] Failed to save story_structure for story {storyId}: {fullMessage}");
            throw new InvalidOperationException($"Failed to update story_structure for story {storyId}: {fullMessage}", ex);
        }
        
        return true;
    }

    /// <summary>
    /// Removes markdown artifacts and special characters from tagged story text.
    /// </summary>
    private string SanitizeStoryTagged(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        
        // Remove markdown bold markers (double asterisks)
        text = text.Replace("**", "");
        
        // Remove markdown italic markers (single asterisks) - careful to not break normal asterisks
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*([^\*]+)\*", "$1");
        
        // Remove all quote characters
        text = text.Replace("\u00AB", ""); // guillemet left
        text = text.Replace("\u00BB", ""); // guillemet right
        text = text.Replace("\u201C", ""); // left double quote
        text = text.Replace("\u201D", ""); // right double quote
        text = text.Replace("\u2018", ""); // left single quote
        text = text.Replace("\u2019", ""); // right single quote
        text = text.Replace("\"", "");     // standard double quote
        text = text.Replace("'", "");      // standard single quote
        
        return text;
    }

    /// <summary>
    /// Updates tagged story fields and formatter metadata.
    /// </summary>
    public bool UpdateStoryTagged(long storyId, string storyTagged, int? formatterModelId, string? formatterPromptHash, int? storyTaggedVersion = null)
    {
        using var context = CreateDbContext();
        var storyRecord = context.Stories.Find(storyId);
        if (storyRecord == null) return false;
        storyRecord.StoryTagged = SanitizeStoryTagged(storyTagged ?? string.Empty);
        if (storyTaggedVersion.HasValue)
        {
            storyRecord.StoryTaggedVersion = storyTaggedVersion.Value;
        }
        if (formatterModelId.HasValue)
        {
            storyRecord.FormatterModelId = formatterModelId.Value;
        }
        storyRecord.FormatterPromptHash = formatterPromptHash;
        
        try
        {
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            var fullMessage = ExceptionHelper.GetFullExceptionMessage(ex);
            Console.WriteLine($"[DB][ERROR] Failed to save tagged story for {storyId}: {fullMessage}");
            throw new InvalidOperationException($"Failed to update tagged story {storyId}: {fullMessage}", ex);
        }
        
        return true;
    }

    /// <summary>
    /// Clears tagged story fields and formatter metadata.
    /// </summary>
    public bool ClearStoryTagged(long storyId)
    {
        using var context = CreateDbContext();
        var storyRecord = context.Stories.Find(storyId);
        if (storyRecord == null) return false;
        storyRecord.StoryTagged = string.Empty;
        storyRecord.StoryTaggedVersion = null;
        storyRecord.FormatterModelId = null;
        storyRecord.FormatterPromptHash = null;
        
        try
        {
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            var fullMessage = ExceptionHelper.GetFullExceptionMessage(ex);
            Console.WriteLine($"[DB][ERROR] Failed to clear tagged story for {storyId}: {fullMessage}");
            throw new InvalidOperationException($"Failed to clear tagged story {storyId}: {fullMessage}", ex);
        }
        
        return true;
    }

    /// <summary>
    /// Set the generated flags for a story. Best-effort using a direct SQL update so
    /// this method can be used even if EF migrations adding these columns have
    /// not yet been applied. Returns true if the update executed without error.
    /// </summary>
    public bool UpdateStoryGeneratedTts(long storyId, bool generatedTts)
    {
        try
        {
            using var conn = CreateDapperConnection();
            conn.Open();
            // SQLite uses integer 0/1 for booleans
            var sql = "UPDATE stories SET generated_tts = @g WHERE id = @id";
            conn.Execute(sql, new { g = generatedTts ? 1 : 0, id = storyId });
            return true;
        }
        catch
        {
            // Best-effort: if the column doesn't exist or DB schema isn't updated yet,
            // swallow the error and return false so callers can continue.
            return false;
        }
    }

    /// <summary>
    /// Set the generated_tts_json flag for a story. Best-effort using a direct SQL update so
    /// this method can be used even if EF migrations adding these columns have
    /// not yet been applied. Returns true if the update executed without error.
    /// </summary>
    public bool UpdateStoryGeneratedTtsJson(long storyId, bool generated)
    {
        try
        {
            using var conn = CreateDapperConnection();
            conn.Open();
            var sql = "UPDATE stories SET generated_tts_json = @g WHERE id = @id";
            conn.Execute(sql, new { g = generated ? 1 : 0, id = storyId });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool UpdateStoryGeneratedMusic(long storyId, bool generated)
    {
        try
        {
            using var conn = CreateDapperConnection();
            conn.Open();
            var sql = "UPDATE stories SET generated_music = @g WHERE id = @id";
            conn.Execute(sql, new { g = generated ? 1 : 0, id = storyId });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool UpdateStoryGeneratedEffects(long storyId, bool generated)
    {
        try
        {
            using var conn = CreateDapperConnection();
            conn.Open();
            var sql = "UPDATE stories SET generated_effects = @g WHERE id = @id";
            conn.Execute(sql, new { g = generated ? 1 : 0, id = storyId });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool UpdateStoryGeneratedAmbient(long storyId, bool generated)
    {
        try
        {
            using var conn = CreateDapperConnection();
            conn.Open();
            var sql = "UPDATE stories SET generated_ambient = @g WHERE id = @id";
            conn.Execute(sql, new { g = generated ? 1 : 0, id = storyId });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool UpdateStoryGeneratedMixedAudio(long storyId, bool generated)
    {
        try
        {
            using var conn = CreateDapperConnection();
            conn.Open();
            var sql = "UPDATE stories SET generated_mixed_audio = @g WHERE id = @id";
            conn.Execute(sql, new { g = generated ? 1 : 0, id = storyId });
            return true;
        }
        catch
        {
            return false;
        }
    }

    // TTS voices: list and upsert
    public List<TinyGenerator.Models.TtsVoice> ListTtsVoices(bool onlyEnabled = false)
    {
        using var context = CreateDbContext();
        var query = context.TtsVoices.AsQueryable();
        if (onlyEnabled)
        {
            query = query.Where(v => !v.Disabled);
        }
        var voices = query.OrderBy(v => v.Name).ToList();
        EnsureVoiceDerivedFields(voices);
        return voices;
    }

    public int GetTtsVoiceCount()
    {
        using var context = CreateDbContext();
        return context.TtsVoices.Count();
    }

    public TinyGenerator.Models.TtsVoice? GetTtsVoiceByVoiceId(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId)) return null;
        using var context = CreateDbContext();
        var voice = context.TtsVoices.FirstOrDefault(v => v.VoiceId == voiceId);
        if (voice != null) EnsureVoiceDerivedFields(new List<TinyGenerator.Models.TtsVoice> { voice });
        return voice;
    }

    public TinyGenerator.Models.TtsVoice? GetTtsVoiceById(int id)
    {
        using var context = CreateDbContext();
        var voice = context.TtsVoices.Find(id);
        if (voice != null) EnsureVoiceDerivedFields(new List<TinyGenerator.Models.TtsVoice> { voice });
        return voice;
    }

    public TinyGenerator.Models.TtsVoice? GetTtsVoiceByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        using var context = CreateDbContext();
        var voice = context.TtsVoices.FirstOrDefault(v => v.Name.ToLower() == name.ToLower());
        if (voice != null) EnsureVoiceDerivedFields(new List<TinyGenerator.Models.TtsVoice> { voice });
        return voice;
    }

    public void UpdateTtsVoiceTemplateWavById(int id, string templateWav)
    {
        if (string.IsNullOrWhiteSpace(templateWav)) return;
        using var context = CreateDbContext();
        var voice = context.TtsVoices.Find(id);
        if (voice == null) return;
        voice.TemplateWav = templateWav;
        voice.UpdatedAt = DateTime.UtcNow.ToString("o");
        context.SaveChanges();
    }

    public void UpdateTtsVoiceScoreById(int id, double? score)
    {
        using var context = CreateDbContext();
        var voice = context.TtsVoices.Find(id);
        if (voice == null) return;
        voice.Score = score;
        voice.UpdatedAt = DateTime.UtcNow.ToString("o");
        context.SaveChanges();
    }

    public void DeleteTtsVoiceById(int id)
    {
        using var context = CreateDbContext();
        var voice = context.TtsVoices.Find(id);
        if (voice != null)
        {
            context.TtsVoices.Remove(voice);
            context.SaveChanges();
        }
    }

    public void UpdateTtsVoice(TinyGenerator.Models.TtsVoice v)
    {
        if (v == null || v.Id <= 0) return;
        using var context = CreateDbContext();
        var existing = context.TtsVoices.Find(v.Id);
        if (existing == null) return;
        v.UpdatedAt = DateTime.UtcNow.ToString("o");
        context.Entry(existing).CurrentValues.SetValues(v);
        context.SaveChanges();
    }

    public int InsertTtsVoice(TinyGenerator.Models.TtsVoice v)
    {
        if (v == null) throw new ArgumentNullException(nameof(v));
        using var context = CreateDbContext();
        var now = DateTime.UtcNow.ToString("o");
        v.CreatedAt = now;
        v.UpdatedAt = now;
        context.TtsVoices.Add(v);
        context.SaveChanges();
        return v.Id;
    }

    public void UpsertTtsVoice(TinyGenerator.Services.VoiceInfo v, string? model = null)
    {
        if (v == null || string.IsNullOrWhiteSpace(v.Id)) return;
        using var context = CreateDbContext();
        var now = DateTime.UtcNow.ToString("o");
        string? tagsJson;
        try { tagsJson = v.Tags != null ? JsonSerializer.Serialize(v.Tags) : null; } catch { tagsJson = null; }
        var (archetype, notes) = ExtractArchetypeNotesFromVoiceInfo(v);

        var existing = context.TtsVoices.FirstOrDefault(tv => tv.VoiceId == v.Id);
        if (existing != null)
        {
            existing.Name = string.IsNullOrWhiteSpace(v.Name) ? v.Id : v.Name;
            existing.Model = model;
            existing.Language = v.Language;
            existing.Gender = v.Gender;
            existing.Age = v.Age;
            existing.Confidence = v.Confidence;
            existing.Score = GetScoreFromTags(v.Tags, v.Confidence);
            existing.Tags = tagsJson;
            existing.TemplateWav = v.Tags != null && v.Tags.ContainsKey("template_wav") ? v.Tags["template_wav"] : null;
            existing.Archetype = archetype;
            existing.Notes = notes;
            existing.UpdatedAt = now;
        }
        else
        {
            var voice = new TtsVoice
            {
                VoiceId = v.Id,
                Name = string.IsNullOrWhiteSpace(v.Name) ? v.Id : v.Name,
                Model = model,
                Language = v.Language,
                Gender = v.Gender,
                Age = v.Age,
                Confidence = v.Confidence,
                Score = GetScoreFromTags(v.Tags, v.Confidence),
                Tags = tagsJson,
                TemplateWav = v.Tags != null && v.Tags.ContainsKey("template_wav") ? v.Tags["template_wav"] : null,
                Archetype = archetype,
                Notes = notes,
                CreatedAt = now,
                UpdatedAt = now
            };
            context.TtsVoices.Add(voice);
        }
        context.SaveChanges();
    }

    /// <summary>
    /// Add or update voices and return a list of voice ids that were newly inserted.
    /// </summary>
    public sealed class TtsVoiceSyncResult
    {
        public List<string> AddedIds { get; } = new();
        public List<string> UpdatedIds { get; } = new();
        public List<string> Errors { get; } = new();
    }

    public async Task<TtsVoiceSyncResult> AddOrUpdateTtsVoicesAsyncDetailed(TinyGenerator.Services.TtsService ttsService)
    {
        var result = new TtsVoiceSyncResult();
        if (ttsService == null) return result;
        try
        {
            var list = await ttsService.GetVoicesAsync();
            if (list == null || list.Count == 0) return result;

            using var context = CreateDbContext();
            foreach (var v in list)
            {
                if (v == null || string.IsNullOrWhiteSpace(v.Id)) continue;
                try
                {
                    var existing = context.TtsVoices.FirstOrDefault(tv => tv.VoiceId == v.Id);
                    var now = DateTime.UtcNow.ToString("o");
                    string? tagsJson;
                    try { tagsJson = v.Tags != null ? JsonSerializer.Serialize(v.Tags) : null; } catch { tagsJson = null; }
                    var (archetype, notes) = ExtractArchetypeNotesFromVoiceInfo(v);

                    if (existing != null)
                    {
                        // Update only technical fields from API, preserve user-customizable fields
                        // Update: Name, Model, Language, Gender, Age, Confidence, Tags, TemplateWav
                        // Preserve: Score (user can adjust), Archetype (user can override), Notes (user can override), Disabled (user preference)
                        existing.Name = string.IsNullOrWhiteSpace(v.Name) ? v.Id : v.Name;
                        existing.Model = v.Model;
                        existing.Language = v.Language;
                        existing.Gender = v.Gender;
                        existing.Age = !string.IsNullOrWhiteSpace(v.Age) ? v.Age : v.AgeRange;
                        existing.Confidence = v.Confidence;
                        // Only update Score if not manually set (check if it matches confidence or is null)
                        if (existing.Score == null || existing.Score == existing.Confidence)
                        {
                            existing.Score = GetScoreFromTags(v.Tags, v.Confidence);
                        }
                        existing.Tags = tagsJson;
                        existing.TemplateWav = v.Tags != null && v.Tags.ContainsKey("template_wav") ? v.Tags["template_wav"] : null;
                        // Only update Archetype/Notes if currently empty (preserve user edits)
                        if (string.IsNullOrWhiteSpace(existing.Archetype))
                        {
                            existing.Archetype = archetype;
                        }
                        if (string.IsNullOrWhiteSpace(existing.Notes))
                        {
                            existing.Notes = notes;
                        }
                        existing.UpdatedAt = now;
                        result.UpdatedIds.Add(v.Id);
                    }
                    else
                    {
                        var voice = new TtsVoice
                        {
                            VoiceId = v.Id,
                            Name = string.IsNullOrWhiteSpace(v.Name) ? v.Id : v.Name,
                            Model = v.Model,
                            Language = v.Language,
                            Gender = v.Gender,
                            Age = !string.IsNullOrWhiteSpace(v.Age) ? v.Age : v.AgeRange,
                            Confidence = v.Confidence,
                            Score = GetScoreFromTags(v.Tags, v.Confidence),
                            Tags = tagsJson,
                            TemplateWav = v.Tags != null && v.Tags.ContainsKey("template_wav") ? v.Tags["template_wav"] : null,
                            Archetype = archetype,
                            Notes = notes,
                            CreatedAt = now,
                            UpdatedAt = now
                        };
                        context.TtsVoices.Add(voice);
                        result.AddedIds.Add(v.Id);
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Voice {v?.Id}: {ex.Message}");
                }
            }
            context.SaveChanges();
            return result;
        }
        catch
        {
            return result;
        }
    }

    private static (string? archetype, string? notes) ExtractArchetypeNotesFromVoiceInfo(TinyGenerator.Services.VoiceInfo voice)
    {
        string? archetype = null;
        string? notes = null;
        if (voice.Tags != null)
        {
            if (voice.Tags.TryGetValue("archetype", out var arch) && !string.IsNullOrWhiteSpace(arch))
                archetype = arch;
            if (voice.Tags.TryGetValue("notes", out var n) && !string.IsNullOrWhiteSpace(n))
                notes = n;
        }
        return (archetype, notes);
    }

    private static double? GetScoreFromTags(Dictionary<string,string>? tags, double? confidenceFallback)
    {
        if (tags == null) return confidenceFallback;
        if (tags.TryGetValue("score", out var sval) && !string.IsNullOrWhiteSpace(sval))
        {
            if (double.TryParse(sval, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return confidenceFallback;
    }

    private void EnsureVoiceDerivedFields(List<TinyGenerator.Models.TtsVoice> voices)
    {
        if (voices == null || voices.Count == 0) return;
        var toUpdate = new List<TinyGenerator.Models.TtsVoice>();
        foreach (var voice in voices)
        {
            if (voice == null) continue;
                // Try to extract archetype/notes from tags JSON if present
                var (arch, notes) = ExtractArchetypeNotesFromTags(voice.Tags);
            bool changed = false;
            if (!string.IsNullOrWhiteSpace(arch) && !string.Equals(voice.Archetype, arch, StringComparison.Ordinal))
            {
                voice.Archetype = arch;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(notes) && !string.Equals(voice.Notes, notes, StringComparison.Ordinal))
            {
                voice.Notes = notes;
                changed = true;
            }
            if (changed) toUpdate.Add(voice);
        }
        if (toUpdate.Count == 0) return;
        using var context = CreateDbContext();
        foreach (var voice in toUpdate)
        {
            var existing = context.TtsVoices.Find(voice.Id);
            if (existing != null)
            {
                existing.Archetype = voice.Archetype;
                existing.Notes = voice.Notes;
            }
        }
        context.SaveChanges();
    }

    private static (string? archetype, string? notes) ExtractArchetypeNotesFromTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(tagsJson);
            var root = doc.RootElement;
            var archetype = TryGetPropertyStringCaseInsensitive(root, "archetype");
            var notes = TryGetPropertyStringCaseInsensitive(root, "notes");
            return (archetype, notes);
        }
        catch
        {
            return (null, null);
        }
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? TryGetPropertyStringCaseInsensitive(JsonElement element, string propertyName)
    {
        if (TryGetPropertyCaseInsensitive(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var str = value.GetString();
            return string.IsNullOrWhiteSpace(str) ? null : str;
        }
        return null;
    }

    public async Task<int> AddOrUpdateTtsVoicesAsync(TinyGenerator.Services.TtsService ttsService)
    {
        if (ttsService == null) return 0;
        try
        {
            var list = await ttsService.GetVoicesAsync();
            if (list == null || list.Count == 0) return 0;
            var added = 0;
            foreach (var v in list)
            {
                try
                {
                    UpsertTtsVoice(v);
                    added++;
                }
                catch { /* ignore per-voice failures */ }
            }
            return added;
        }
        catch { return 0; }
    }

    public int AddOrUpdateTtsVoicesFromJsonString(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            JsonElement voicesEl;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("voices", out voicesEl))
            {
                // OK
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                voicesEl = root;
            }
            else
            {
                return 0;
            }

            var added = 0;
            foreach (var e in voicesEl.EnumerateArray())
            {
                try
                {
                    var model = e.TryGetProperty("model", out var pm) ? pm.GetString() : null;
                    var speaker = e.TryGetProperty("speaker", out var ps) && ps.ValueKind != JsonValueKind.Null ? ps.GetString() : null;
                    var language = e.TryGetProperty("language", out var pl) ? pl.GetString() : null;
                    var gender = e.TryGetProperty("gender", out var pg) ? pg.GetString() : null;
                    var age = e.TryGetProperty("age_range", out var pa) ? pa.GetString() : (e.TryGetProperty("age", out var pa2) ? pa2.GetString() : null);
                    var sample = e.TryGetProperty("sample", out var psample) ? psample.GetString() : null;
                    var template = e.TryGetProperty("template_wav", out var ptemp) ? ptemp.GetString() : null;
                    var notes = e.TryGetProperty("notes", out var pnotes) ? pnotes.GetString() : null;
                    var rating = e.TryGetProperty("rating", out var prat) && prat.ValueKind != JsonValueKind.Null ? prat.GetRawText() : null;

                    var vid = !string.IsNullOrWhiteSpace(speaker) ? (model + ":" + speaker) : (model ?? Guid.NewGuid().ToString());
                    var name = !string.IsNullOrWhiteSpace(speaker) ? speaker : (model ?? vid);

                    var v = new TinyGenerator.Services.VoiceInfo()
                    {
                        Id = vid,
                        Name = name,
                        Language = language,
                        Gender = gender,
                        Age = age,
                        Tags = new System.Collections.Generic.Dictionary<string, string>()
                    };

                    if (!string.IsNullOrWhiteSpace(sample)) v.Tags["sample"] = sample!;
                    if (!string.IsNullOrWhiteSpace(template)) v.Tags["template_wav"] = template!;
                    if (!string.IsNullOrWhiteSpace(notes)) v.Tags["notes"] = notes!;
                    if (!string.IsNullOrWhiteSpace(rating)) v.Tags["rating"] = rating!;
                    if (!string.IsNullOrWhiteSpace(model)) v.Tags["model"] = model!;

                    UpsertTtsVoice(v, model);
                    added++;
                }
                catch { }
            }

            return added;
        }
        catch { return 0; }
    }

    private void InitializeSchema()
    {
        Console.WriteLine("[DB] InitializeSchema start");
        var sw = Stopwatch.StartNew();
        using var conn = CreateConnection();
        var openSw = Stopwatch.StartNew();
        conn.Open();
        openSw.Stop();
        Console.WriteLine($"[DB] Connection opened in {openSw.ElapsedMilliseconds}ms");

        // Seed some commonly used, lower-cost OpenAI chat models so they appear in the models table
        try
        {
            Console.WriteLine("[DB] Seeding default OpenAI models (if missing)...");
            var seedSw = Stopwatch.StartNew();
            SeedDefaultOpenAiModels();
            seedSw.Stop();
            Console.WriteLine($"[DB] SeedDefaultOpenAiModels completed in {seedSw.ElapsedMilliseconds}ms");
        }
        catch
        {
            // best-effort seeding, ignore failures
        }

        // Run migrations to handle schema updates
        RunMigrations(conn);

        Console.WriteLine($"[DB] InitializeSchema completed in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Run schema migrations. These are applied to handle schema updates when database
    /// is recreated from db_schema.sql but needs subsequent modifications.
    /// </summary>
    private void RunMigrations(IDbConnection conn)
    {
        // Migration: add story_revised column to stories if missing
        try
        {
            var hasStoryRevised = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('stories') WHERE name='story_revised'") > 0;
            if (!hasStoryRevised)
            {
                Console.WriteLine("[DB] Migration: adding story_revised column to stories");
                conn.Execute("ALTER TABLE stories ADD COLUMN story_revised TEXT");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: unable to add story_revised to stories: {ex.Message}");
        }

        // Migration: add Result column to Log if missing
        try
        {
            var hasResult = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('Log') WHERE name = 'Result'") > 0;
            if (!hasResult)
            {
                conn.Execute("ALTER TABLE Log ADD COLUMN Result TEXT");
                Console.WriteLine("[DB] Migration: added Result column to Log");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: unable to add Result column to Log: {ex.Message}");
        }

        // Migration: add StepNumber and MaxStep columns to Log for multi-step tracking
        try
        {
            var hasStepNumber = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('Log') WHERE name = 'StepNumber'") > 0;
            if (!hasStepNumber)
            {
                conn.Execute("ALTER TABLE Log ADD COLUMN StepNumber INTEGER");
                Console.WriteLine("[DB] Migration: added StepNumber column to Log");
            }
            var hasMaxStep = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('Log') WHERE name = 'MaxStep'") > 0;
            if (!hasMaxStep)
            {
                conn.Execute("ALTER TABLE Log ADD COLUMN MaxStep INTEGER");
                Console.WriteLine("[DB] Migration: added MaxStep column to Log");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: unable to add step columns to Log: {ex.Message}");
        }

        // Migration: create app_events table and seed default event types
        var hasAppEventsTable = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='app_events'");
        if (hasAppEventsTable == 0)
        {
            conn.Execute(@"
CREATE TABLE app_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    event_type TEXT UNIQUE NOT NULL,
    description TEXT,
    enabled INTEGER NOT NULL DEFAULT 1,
    logged INTEGER NOT NULL DEFAULT 1,
    notified INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
)");
            Console.WriteLine("[DB] Created app_events table");
        }

        var defaultAppEvents = new (string EventType, string Description)[]
        {
            ("CommandEnqueued", "Dispatcher received a command"),
            ("CommandDequeued", "Dispatcher claimed a command for execution"),
            ("CommandStarted", "Command execution began"),
            ("CommandProgress", "Progress update or heartbeat"),
            ("CommandCompleted", "Command finished successfully"),
            ("CommandFailed", "Command failed with an error"),
            ("CommandCancelled", "Command removed from queue"),
            ("QueueSnapshot", "Overall queue/dispatcher status"),
            ("AgentAssigned", "Agent picked up a command"),
            ("AgentIdle", "Agent finished work and is free"),
            ("ModelBusy", "Model request started"),
            ("ModelFree", "Model released from busy state"),
            ("TestRunEnqueued", "Test command enqueued"),
            ("TestStepProgress", "Intermediate test step update"),
            ("TestRunResult", "Final test outcome"),
            ("UserNotification", "User-facing notification"),
            ("SystemAlert", "System-level alert")
        };

        foreach (var appEvent in defaultAppEvents)
        {
            conn.Execute(@"
INSERT OR IGNORE INTO app_events (event_type, description, enabled, logged, notified, created_at, updated_at)
VALUES (@event_type, @description, 1, 1, 1, datetime('now'), datetime('now'))",
                new { event_type = appEvent.EventType, description = appEvent.Description });
        }

        // Migration: Consolidate legacy lowercase 'logs' table into canonical 'Log'
        try
        {
            var legacyLogs = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='logs'");
            if (legacyLogs > 0)
            {
                Console.WriteLine("[DB] Migrating legacy 'logs' table into 'Log'");
                try
                {
                    conn.Execute(@"
                        INSERT INTO Log (Ts, Level, Category, Message, Exception, State, ThreadId, AgentName, Context, Result)
                        SELECT l.ts, l.level, l.category, l.message, l.exception, l.state, 0, NULL, NULL, NULL
                        FROM logs l
                        WHERE NOT EXISTS (
                            SELECT 1 FROM Log existing 
                            WHERE existing.Ts = l.ts AND existing.Level = l.level AND existing.Message = l.message
                        )");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DB] Warning: failed to copy legacy logs: {ex.Message}");
                }

                try
                {
                    conn.Execute("DROP TABLE IF EXISTS logs");
                    Console.WriteLine("[DB] Dropped legacy 'logs' table");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DB] Warning: failed to drop legacy logs table: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: unable to inspect legacy logs table: {ex.Message}");
        }

        // Migration: Add files_to_copy column if not exists
        var hasFilesToCopy = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('test_definitions') WHERE name='files_to_copy'");
        if (hasFilesToCopy == 0)
        {
            Console.WriteLine("[DB] Adding files_to_copy column to test_definitions");
            conn.Execute("ALTER TABLE test_definitions ADD COLUMN files_to_copy TEXT");
        }

        // Migration: ensure ThreadScope column exists on Log table
        var hasThreadScope = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('Log') WHERE name='ThreadScope'");
        if (hasThreadScope == 0)
        {
            Console.WriteLine("[DB] Adding ThreadScope column to Log");
            conn.Execute("ALTER TABLE Log ADD COLUMN ThreadScope TEXT");
        }

        // Migration: Add archetype and notes columns to tts_voices if missing
        try
        {
            var hasArchetype = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('tts_voices') WHERE name='archetype'");
            var hasNotes = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('tts_voices') WHERE name='notes'");
            if (hasArchetype == 0)
            {
                Console.WriteLine("[DB] Adding archetype column to tts_voices");
                conn.Execute("ALTER TABLE tts_voices ADD COLUMN archetype TEXT");
            }
            if (hasNotes == 0)
            {
                Console.WriteLine("[DB] Adding notes column to tts_voices");
                conn.Execute("ALTER TABLE tts_voices ADD COLUMN notes TEXT");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: failed to add archetype/notes to tts_voices: {ex.Message}");
        }

        // Migration: Rename group_name to test_group in test_definitions if needed
        var hasGroupName = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('test_definitions') WHERE name='group_name'");
        var hasTestGroupInDefs = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('test_definitions') WHERE name='test_group'");
        
        if (hasGroupName > 0 && hasTestGroupInDefs == 0)
        {
            Console.WriteLine("[DB] Migrating test_definitions: renaming group_name to test_group");
            conn.Execute("ALTER TABLE test_definitions RENAME COLUMN group_name TO test_group");
        }

        // Migration: Add Id column to models if not exists
        var hasIdColumn = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('models') WHERE name='Id'");
        if (hasIdColumn == 0)
        {
            Console.WriteLine("[DB] Adding Id column to models table");
            conn.Execute("ALTER TABLE models ADD COLUMN Id INTEGER PRIMARY KEY AUTOINCREMENT");
        }

        // Migration: Add writer_score column to models if not exists
        var hasWriterScore = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('models') WHERE name='WriterScore'");
        if (hasWriterScore == 0)
        {
            Console.WriteLine("[DB] Adding WriterScore column to models");
            conn.Execute("ALTER TABLE models ADD COLUMN WriterScore REAL DEFAULT 0");
        }

        // Migration: Add test_folder column if not exists
        var hasTestFolder = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('model_test_runs') WHERE name='test_folder'");
        if (hasTestFolder == 0)
        {
            Console.WriteLine("[DB] Adding test_folder column to model_test_runs");
            conn.Execute("ALTER TABLE model_test_runs ADD COLUMN test_folder TEXT");
        }

        // Migration: Add temperature and top_p columns to test_definitions if not exists
        var hasTemperature = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('test_definitions') WHERE name='temperature'");
        if (hasTemperature == 0)
        {
            Console.WriteLine("[DB] Adding temperature column to test_definitions");
            conn.Execute("ALTER TABLE test_definitions ADD COLUMN temperature REAL DEFAULT NULL");
        }

        var hasTopP = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('test_definitions') WHERE name='top_p'");
        if (hasTopP == 0)
        {
            Console.WriteLine("[DB] Adding top_p column to test_definitions");
            conn.Execute("ALTER TABLE test_definitions ADD COLUMN top_p REAL DEFAULT NULL");
        }

        // Migration: Add temperature and top_p columns to agents if not exists (persist agent-level model params)
        try
        {
            var hasAgentTemp = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name='temperature'");
            if (hasAgentTemp == 0)
            {
                Console.WriteLine("[DB] Adding temperature column to agents");
                conn.Execute("ALTER TABLE agents ADD COLUMN temperature REAL DEFAULT NULL");
            }

            var hasAgentTopP = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name='top_p'");
            if (hasAgentTopP == 0)
            {
                Console.WriteLine("[DB] Adding top_p column to agents");
                conn.Execute("ALTER TABLE agents ADD COLUMN top_p REAL DEFAULT NULL");
            }

            var hasAgentRepeatPenalty = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name='repeat_penalty'");
            if (hasAgentRepeatPenalty == 0)
            {
                Console.WriteLine("[DB] Adding repeat_penalty column to agents");
                conn.Execute("ALTER TABLE agents ADD COLUMN repeat_penalty REAL DEFAULT NULL");
            }

            var hasAgentTopK = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name='top_k'");
            if (hasAgentTopK == 0)
            {
                Console.WriteLine("[DB] Adding top_k column to agents");
                conn.Execute("ALTER TABLE agents ADD COLUMN top_k INTEGER DEFAULT NULL");
            }

            var hasAgentRepeatLastN = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name='repeat_last_n'");
            if (hasAgentRepeatLastN == 0)
            {
                Console.WriteLine("[DB] Adding repeat_last_n column to agents");
                conn.Execute("ALTER TABLE agents ADD COLUMN repeat_last_n INTEGER DEFAULT NULL");
            }

            var hasAgentNumPredict = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name='num_predict'");
            if (hasAgentNumPredict == 0)
            {
                Console.WriteLine("[DB] Adding num_predict column to agents");
                conn.Execute("ALTER TABLE agents ADD COLUMN num_predict INTEGER DEFAULT NULL");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: failed to add temperature/top_p/repeat_penalty/top_k/repeat_last_n/num_predict to agents: {ex.Message}");
        }

        // Migration: Add note column to models if not exists
        var hasNote = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('models') WHERE name='note'");
        if (hasNote == 0)
        {
            Console.WriteLine("[DB] Adding note column to models");
            try
            {
                conn.Execute("ALTER TABLE models ADD COLUMN note TEXT");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] Warning: failed to add note column to models: {ex.Message}");
            }
        }

        // Migration: Rename test_code to test_group if needed
        var hasTestCode = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('model_test_runs') WHERE name='test_code'");
        var hasTestGroup = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('model_test_runs') WHERE name='test_group'");
        
        if (hasTestCode > 0 && hasTestGroup == 0)
        {
            Console.WriteLine("[DB] Migrating model_test_runs: renaming test_code to test_group");
            conn.Execute("ALTER TABLE model_test_runs RENAME COLUMN test_code TO test_group");
        }

        try
        {
            var legacyEvalColumns = conn.ExecuteScalar<long>(@"SELECT COUNT(*) FROM pragma_table_info('stories_evaluations')
                                                               WHERE name IN ('structure_score','characterization_score','dialogues_score','pacing_score','style_score','worldbuilding_score','thematic_coherence_score','overall_evaluation')");
            var hasActionColumn = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('stories_evaluations') WHERE name='action_score'");
            if (legacyEvalColumns > 0 || hasActionColumn == 0)
            {
                Console.WriteLine("[DB] Migrating stories_evaluations table to compact schema");
                var oldHasActionScore = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('stories_evaluations') WHERE name='action_score'") > 0;
                var oldHasActionDefects = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('stories_evaluations') WHERE name='action_defects'") > 0;
                var oldHasPacingScore = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('stories_evaluations') WHERE name='pacing_score'") > 0;
                var oldHasPacingDefects = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('stories_evaluations') WHERE name='pacing_defects'") > 0;

                conn.Execute("ALTER TABLE stories_evaluations RENAME TO stories_evaluations_old");

                conn.Execute(@"
CREATE TABLE stories_evaluations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    story_id INTEGER NOT NULL,
    narrative_coherence_score INTEGER DEFAULT 0,
    narrative_coherence_defects TEXT,
    originality_score INTEGER DEFAULT 0,
    originality_defects TEXT,
    emotional_impact_score INTEGER DEFAULT 0,
    emotional_impact_defects TEXT,
    action_score INTEGER DEFAULT 0,
    action_defects TEXT,
    total_score REAL DEFAULT 0,
    raw_json TEXT,
    model_id INTEGER NULL,
    agent_id INTEGER NULL,
    ts TEXT,
    FOREIGN KEY (story_id) REFERENCES stories(id) ON DELETE CASCADE,
    FOREIGN KEY (model_id) REFERENCES models(Id) ON DELETE SET NULL,
    FOREIGN KEY (agent_id) REFERENCES agents(id) ON DELETE SET NULL
);");

                var actionScoreExpr = oldHasActionScore ? "action_score" : (oldHasPacingScore ? "pacing_score" : "0");
                var actionDefectsExpr = oldHasActionDefects ? "action_defects" : (oldHasPacingDefects ? "pacing_defects" : "''");

                var copySql = $@"
INSERT INTO stories_evaluations (id, story_id, narrative_coherence_score, narrative_coherence_defects, originality_score, originality_defects, emotional_impact_score, emotional_impact_defects, action_score, action_defects, total_score, raw_json, model_id, agent_id, ts)
SELECT id, story_id, narrative_coherence_score, narrative_coherence_defects, originality_score, originality_defects, emotional_impact_score, emotional_impact_defects, {actionScoreExpr}, {actionDefectsExpr}, total_score, raw_json, model_id, agent_id, ts
FROM stories_evaluations_old;";
                conn.Execute(copySql);

                conn.Execute("DROP TABLE IF EXISTS stories_evaluations_old");
                Console.WriteLine("[DB] stories_evaluations migration completed");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: failed to migrate stories_evaluations schema: {ex.Message}");
        }

        // Migration: Add multi_step_template_id column to agents table
        var hasMultiStepTemplateId = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name='multi_step_template_id'");
        if (hasMultiStepTemplateId == 0)
        {
            Console.WriteLine("[DB] Adding multi_step_template_id column to agents");
            conn.Execute("ALTER TABLE agents ADD COLUMN multi_step_template_id INTEGER NULL");
        }

        // Migration: Create task_types table if not exists
        var hasTaskTypes = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='task_types'");
        if (hasTaskTypes == 0)
        {
            Console.WriteLine("[DB] Creating task_types table");
            conn.Execute(@"
CREATE TABLE task_types (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT UNIQUE NOT NULL,
    description TEXT,
    default_executor_role TEXT NOT NULL,
    default_checker_role TEXT NOT NULL,
    output_merge_strategy TEXT NOT NULL,
    validation_criteria TEXT
)");
            
            // Seed default task type: story
            conn.Execute(@"
INSERT INTO task_types (code, description, default_executor_role, default_checker_role, output_merge_strategy, validation_criteria)
VALUES ('story', 'Story Generation', 'writer', 'response_checker', 'accumulate_chapters', '{""min_length_check"":true,""no_questions"":true,""semantic_threshold"":0.6}');
INSERT INTO task_types (code, description, default_executor_role, default_checker_role, output_merge_strategy, validation_criteria)
VALUES ('tts_schema', 'TTS Schema Generation', 'tts_json', 'response_checker', 'last_only', '{""require_confirm"":true}');
");
            Console.WriteLine("[DB] Seeded default task_types");
        }
        else
        {
            var hasTtsSchema = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM task_types WHERE code = 'tts_schema'");
            if (hasTtsSchema == 0)
            {
                conn.Execute(@"
INSERT INTO task_types (code, description, default_executor_role, default_checker_role, output_merge_strategy, validation_criteria)
VALUES ('tts_schema', 'TTS Schema Generation', 'tts_json', 'response_checker', 'last_only', '{""require_confirm"":true}')");
                Console.WriteLine("[DB] Added tts_schema task_type");
            }
        }

        conn.Execute("UPDATE step_templates SET task_type='tts_schema' WHERE name LIKE 'tts_schema%' AND task_type <> 'tts_schema'");

        // Migration: Create task_executions table if not exists
        var hasTaskExecutions = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='task_executions'");
        if (hasTaskExecutions == 0)
        {
            Console.WriteLine("[DB] Creating task_executions table");
            conn.Execute(@"
CREATE TABLE task_executions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    task_type TEXT NOT NULL,
    entity_id INTEGER NULL,
    step_prompt TEXT NOT NULL,
    current_step INTEGER DEFAULT 1,
    max_step INTEGER NOT NULL,
    retry_count INTEGER DEFAULT 0,
    status TEXT DEFAULT 'pending' CHECK(status IN ('pending','in_progress','completed','failed','paused')),
    executor_agent_id INTEGER NULL,
    checker_agent_id INTEGER NULL,
    config TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
)");
            
            // Create unique constraint for active executions
            conn.Execute("CREATE UNIQUE INDEX idx_task_executions_active ON task_executions(entity_id, task_type) WHERE status IN ('pending','in_progress')");
            Console.WriteLine("[DB] Created task_executions table");
        }

        // Migration: Create task_execution_steps table if not exists
        var hasTaskExecutionSteps = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='task_execution_steps'");
        if (hasTaskExecutionSteps == 0)
        {
            Console.WriteLine("[DB] Creating task_execution_steps table");
            conn.Execute(@"
CREATE TABLE task_execution_steps (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    execution_id INTEGER NOT NULL,
    step_number INTEGER NOT NULL,
    step_instruction TEXT NOT NULL,
    step_output TEXT NULL,
    validation_result TEXT NULL,
    attempt_count INTEGER DEFAULT 1,
    started_at TEXT,
    completed_at TEXT,
    FOREIGN KEY(execution_id) REFERENCES task_executions(id) ON DELETE CASCADE
)");
            Console.WriteLine("[DB] Created task_execution_steps table");
        }

        // Migration: Create step_templates table if not exists
        var hasStepTemplates = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='step_templates'");
        if (hasStepTemplates == 0)
        {
            Console.WriteLine("[DB] Creating step_templates table");
            conn.Execute(@"
CREATE TABLE step_templates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT UNIQUE NOT NULL,
    task_type TEXT NOT NULL,
    step_prompt TEXT NOT NULL,
    description TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
)");
            
            // Seed default template: 9-step story generation
            var defaultStepPrompt = @"1. Scrivi la trama dettagliata (minimo 1500 parole) divisa in 6 capitoli.
2. Genera la lista completa dei PERSONAGGI con nome, sesso, età approssimativa, ruolo e carattere.
3. Genera la STRUTTURA dettagliata di ogni capitolo (scene, eventi, dialoghi previsti).
4. Scrivi il CAPITOLO 1 (minimo 1500 parole). Contesto: {{STEP_1}}, {{STEP_2}}, {{STEP_3_EXTRACT:Capitolo 1}}
5. Scrivi il CAPITOLO 2 (minimo 1500 parole). Contesto: {{STEP_1}}, {{STEP_2}}, {{STEP_3_EXTRACT:Capitolo 2}}, {{STEP_4_SUMMARY}}
6. Scrivi il CAPITOLO 3 (minimo 1500 parole). Contesto: {{STEP_1}}, {{STEP_2}}, {{STEP_3_EXTRACT:Capitolo 3}}, {{STEPS_4-5_SUMMARY}}
7. Scrivi il CAPITOLO 4 (minimo 1500 parole). Contesto: {{STEP_1}}, {{STEP_2}}, {{STEP_3_EXTRACT:Capitolo 4}}, {{STEPS_4-6_SUMMARY}}
8. Scrivi il CAPITOLO 5 (minimo 1500 parole). Contesto: {{STEP_1}}, {{STEP_2}}, {{STEP_3_EXTRACT:Capitolo 5}}, {{STEPS_4-7_SUMMARY}}
9. Scrivi il CAPITOLO 6 (minimo 1500 parole). Contesto: {{STEP_1}}, {{STEP_2}}, {{STEP_3_EXTRACT:Capitolo 6}}, {{STEPS_4-8_SUMMARY}}";
            
            conn.Execute(@"
INSERT INTO step_templates (name, task_type, step_prompt, description, created_at, updated_at)
VALUES ('story_9_chapters', 'story', @stepPrompt, 'Standard 9-step story generation with 6 chapters', datetime('now'), datetime('now'))",
                new { stepPrompt = defaultStepPrompt });
            Console.WriteLine("[DB] Seeded default step_templates");
        }

        // Patch tts_schema templates: correct task_type and ensure instructions are populated
        try
        {
            conn.Execute("UPDATE step_templates SET task_type='tts_schema' WHERE name LIKE 'tts_schema%' AND task_type <> 'tts_schema'");

            var instr = conn.ExecuteScalar<string?>("SELECT instructions FROM step_templates WHERE name='tts_schema_chunk_fixed20' LIMIT 1");
            if (string.IsNullOrWhiteSpace(instr))
            {
                var defaultInstr = @"Leggi attentamente il testo e trascrivilo integralmente nel formato seguente, senza riassumere o saltare frasi, senza aggiungere note o testo extra.

Usa SOLO queste sezioni ripetute nell’ordine del testo:

[NARRATORE]
Testo narrativo così come appare nel chunk

[PERSONAGGIO: NomePersonaggio | EMOZIONE: emotion]
Battuta di dialogo così come appare nel testo

Regole:
- NON includere il testo originale nella risposta.
- NON cambiare lingua, NON abbreviare, NON riassumere.
- Se non è chiaramente un dialogo, usa NARRATORE.
- EMOZIONE: usa una tra neutral, happy, sad, angry, fearful, disgusted, surprised (default neutral se non indicata).
- Non aggiungere spiegazioni o altro testo fuori dai blocchi.
- Copri tutto il testo, più blocchi uno dopo l’altro finché il testo è esaurito.";

                conn.Execute("UPDATE step_templates SET instructions=@instr WHERE name='tts_schema_chunk_fixed20'", new { instr = defaultInstr });
                Console.WriteLine("[DB] Patched instructions for tts_schema_chunk_fixed20");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning patching tts_schema templates: {ex.Message}");
        }

        // Migration: Add characters column to stories table for storing structured character list
        try
        {
            var hasCharacters = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('stories') WHERE name='characters'");
            if (hasCharacters == 0)
            {
                Console.WriteLine("[DB] Adding characters column to stories");
                conn.Execute("ALTER TABLE stories ADD COLUMN characters TEXT");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: failed to add characters column to stories: {ex.Message}");
        }

        // Migration: Add characters_step column to step_templates for specifying which step generates character list
        try
        {
            var hasCharactersStep = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('step_templates') WHERE name='characters_step'");
            if (hasCharactersStep == 0)
            {
                Console.WriteLine("[DB] Adding characters_step column to step_templates");
                conn.Execute("ALTER TABLE step_templates ADD COLUMN characters_step INTEGER");
                // Set default value for existing story templates (step 2 generates characters)
                conn.Execute("UPDATE step_templates SET characters_step = 2 WHERE task_type = 'story' AND characters_step IS NULL");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: failed to add characters_step column to step_templates: {ex.Message}");
        }

        // Migration: Add evaluation_steps column to step_templates for specifying which steps require evaluator validation
        try
        {
            var hasEvaluationSteps = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('step_templates') WHERE name='evaluation_steps'");
            if (hasEvaluationSteps == 0)
            {
                Console.WriteLine("[DB] Adding evaluation_steps column to step_templates");
                conn.Execute("ALTER TABLE step_templates ADD COLUMN evaluation_steps TEXT");
                // Set default value for existing story templates (evaluate chapter steps 4-9)
                conn.Execute("UPDATE step_templates SET evaluation_steps = '4,5,6,7,8,9' WHERE task_type = 'story' AND evaluation_steps IS NULL");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: failed to add evaluation_steps column to step_templates: {ex.Message}");
        }

        // Migration: Add trama_steps column to step_templates for specifying which steps contain the chapter trama
        try
        {
            var hasTramaSteps = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('step_templates') WHERE name='trama_steps'");
            if (hasTramaSteps == 0)
            {
                Console.WriteLine("[DB] Adding trama_steps column to step_templates");
                conn.Execute("ALTER TABLE step_templates ADD COLUMN trama_steps TEXT");
                // Default: step 1 contains the overall trama for story templates
                conn.Execute("UPDATE step_templates SET trama_steps = '1' WHERE task_type = 'story' AND trama_steps IS NULL");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: failed to add trama_steps column to step_templates: {ex.Message}");
        }

        // Migration: Create mapped_sentiments table for sentiment mapping cache
        try
        {
            var hasMappedSentiments = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mapped_sentiments'");
            if (hasMappedSentiments == 0)
            {
                Console.WriteLine("[DB] Creating mapped_sentiments table");
                conn.Execute(@"
CREATE TABLE mapped_sentiments (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source_sentiment TEXT NOT NULL UNIQUE,
    dest_sentiment TEXT NOT NULL,
    confidence REAL,
    source_type TEXT,
    created_at TEXT DEFAULT (datetime('now'))
)");
                // Seed common mappings
                conn.Execute(@"
INSERT OR IGNORE INTO mapped_sentiments (source_sentiment, dest_sentiment, source_type, confidence)
VALUES 
    ('neutrale', 'neutral', 'seed', 1.0),
    ('felice', 'happy', 'seed', 1.0),
    ('contento', 'happy', 'seed', 1.0),
    ('gioioso', 'happy', 'seed', 1.0),
    ('triste', 'sad', 'seed', 1.0),
    ('malinconico', 'sad', 'seed', 1.0),
    ('arrabbiato', 'angry', 'seed', 1.0),
    ('furioso', 'angry', 'seed', 1.0),
    ('irritato', 'angry', 'seed', 1.0),
    ('spaventato', 'fearful', 'seed', 1.0),
    ('terrorizzato', 'fearful', 'seed', 1.0),
    ('ansioso', 'fearful', 'seed', 1.0),
    ('disgustato', 'disgusted', 'seed', 1.0),
    ('nauseato', 'disgusted', 'seed', 1.0),
    ('sorpreso', 'surprised', 'seed', 1.0),
    ('stupito', 'surprised', 'seed', 1.0)
");
                Console.WriteLine("[DB] Created mapped_sentiments table with seed data");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: failed to create mapped_sentiments table: {ex.Message}");
        }

        // Migration: Create sentiment_embeddings table for caching TTS sentiment embeddings
        try
        {
            var hasSentimentEmbeddings = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='sentiment_embeddings'");
            if (hasSentimentEmbeddings == 0)
            {
                Console.WriteLine("[DB] Creating sentiment_embeddings table");
                conn.Execute(@"
CREATE TABLE sentiment_embeddings (
    sentiment TEXT PRIMARY KEY,
    embedding TEXT NOT NULL,
    model TEXT,
    created_at TEXT DEFAULT (datetime('now'))
)");
                Console.WriteLine("[DB] Created sentiment_embeddings table");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: failed to create sentiment_embeddings table: {ex.Message}");
        }

        // Migration: Create global_coherence table for storing final coherence scores
        try
        {
            var hasGlobalCoherence = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='global_coherence'");
            if (hasGlobalCoherence == 0)
            {
                Console.WriteLine("[DB] Creating global_coherence table");
                conn.Execute(@"
CREATE TABLE global_coherence (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    story_id INTEGER NOT NULL,
    global_coherence_value REAL NOT NULL,
    chunk_count INTEGER NOT NULL DEFAULT 0,
    notes TEXT,
    ts TEXT DEFAULT (datetime('now'))
)");
                Console.WriteLine("[DB] Created global_coherence table");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: failed to create global_coherence table: {ex.Message}");
        }

        // Migration: Seed SentimentMapper agent if not exists
        try
        {
            var hasSentimentMapper = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM agents WHERE name='SentimentMapper'");
            if (hasSentimentMapper == 0)
            {
                Console.WriteLine("[DB] Seeding SentimentMapper agent");
                conn.Execute(@"
INSERT INTO agents (name, description, is_active, role, prompt, instructions, created_at, updated_at)
VALUES (
    'SentimentMapper',
    'Mappa sentimenti liberi ai 7 sentimenti TTS supportati (neutral, happy, sad, angry, fearful, disgusted, surprised)',
    1,
    'sentiment_mapper',
    'Mappa il sentimento ''{{sentiment}}'' a UNO solo di questi valori:
neutral, happy, sad, angry, fearful, disgusted, surprised

Rispondi SOLO con la parola inglese, senza spiegazioni.',
    'Sei un esperto di analisi del sentiment. Devi mappare qualsiasi sentimento o emozione italiana a uno dei 7 sentimenti base supportati dal TTS.',
    datetime('now'),
    datetime('now')
)");
                Console.WriteLine("[DB] Seeded SentimentMapper agent");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: failed to seed SentimentMapper agent: {ex.Message}");
        }
    }

    // Async batch insert for log entries. Will insert all provided entries in a single INSERT statement when possible.
    public async Task InsertLogsAsync(IEnumerable<TinyGenerator.Models.LogEntry> entries)
    {
        var list = entries?.ToList() ?? new List<TinyGenerator.Models.LogEntry>();
        if (list.Count == 0) return;

        // Pre-process list: coalesce consecutive llama.cpp entries into a single aggregated entry
        var processed = new List<TinyGenerator.Models.LogEntry>();
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (string.Equals(e.Category, "llama.cpp", StringComparison.OrdinalIgnoreCase))
            {
                // start aggregation
                var msgSb = new System.Text.StringBuilder();
                var level = e.Level ?? "Information";
                var exceptionSb = new System.Text.StringBuilder();
                var chatTextSb = new System.Text.StringBuilder();
                var ts = e.Ts;
                var threadId = e.ThreadId;
                var storyId = e.StoryId;
                var threadScope = e.ThreadScope;
                var agentName = e.AgentName;

                void AppendEntry(TinyGenerator.Models.LogEntry le)
                {
                    if (!string.IsNullOrWhiteSpace(le.Message))
                    {
                        if (msgSb.Length > 0) msgSb.AppendLine();
                        msgSb.Append(le.Message);
                    }
                    if (!string.IsNullOrWhiteSpace(le.Exception))
                    {
                        if (exceptionSb.Length > 0) exceptionSb.AppendLine();
                        exceptionSb.Append(le.Exception);
                    }
                    if (!string.IsNullOrWhiteSpace(le.ChatText))
                    {
                        if (chatTextSb.Length > 0) chatTextSb.AppendLine();
                        chatTextSb.Append(le.ChatText);
                    }
                    // escalate level severity if any entry is warning/error
                    var lvl = (le.Level ?? "Information").ToLowerInvariant();
                    if (lvl == "error" || lvl == "fatal") level = "Error";
                    else if (lvl == "warning" && level.ToLowerInvariant() != "error") level = "Warning";
                }

                AppendEntry(e);
                int j = i + 1;
                while (j < list.Count && string.Equals(list[j].Category, "llama.cpp", StringComparison.OrdinalIgnoreCase))
                {
                    AppendEntry(list[j]);
                    j++;
                }

                // create aggregated entry
                var agg = new TinyGenerator.Models.LogEntry
                {
                    Ts = ts,
                    Level = level,
                    Category = "llama.cpp",
                    Message = msgSb.Length > 0 ? msgSb.ToString() : string.Empty,
                    Exception = exceptionSb.Length > 0 ? exceptionSb.ToString() : null,
                    State = null,
                    ThreadId = threadId,
                    StoryId = storyId,
                    ThreadScope = threadScope,
                    AgentName = agentName,
                    Context = null,
                    Analized = false,
                    ChatText = chatTextSb.Length > 0 ? chatTextSb.ToString() : string.Empty,
                    Result = null
                };

                // Trim very large aggregated messages to avoid DB bloat
                const int MaxLen = 8000;
                if (!string.IsNullOrEmpty(agg.Message) && agg.Message.Length > MaxLen)
                {
                    agg.Message = agg.Message.Substring(0, MaxLen) + "\n... (truncated)";
                }
                if (!string.IsNullOrEmpty(agg.ChatText) && agg.ChatText.Length > MaxLen)
                {
                    agg.ChatText = agg.ChatText.Substring(0, MaxLen) + "\n... (truncated)";
                }

                processed.Add(agg);
                i = j - 1; // advance outer loop
            }
            else
            {
                processed.Add(e);
            }
        }

        list = processed;

        using var conn = CreateConnection();
        await ((SqliteConnection)conn).OpenAsync();

        // Build a single INSERT ... VALUES (...),(...),... with uniquely named parameters to avoid collisions
        var cols = new[] { "Ts", "Level", "Category", "Message", "Exception", "State", "ThreadId", "story_id", "ThreadScope", "AgentName", "Context", "analized", "chat_text", "Result" };
        var sb = new System.Text.StringBuilder();
        sb.Append("INSERT INTO Log (" + string.Join(", ", cols) + ") VALUES ");

        var parameters = new DynamicParameters();
        for (int i = 0; i < list.Count; i++)
        {
            var pNames = cols.Select(c => "@" + c + i).ToArray();
            sb.Append("(" + string.Join(", ", pNames) + ")");
            if (i < list.Count - 1) sb.Append(",");

            var e = list[i];
            parameters.Add("@Ts" + i, e.Ts);
            parameters.Add("@Level" + i, e.Level);
            parameters.Add("@Category" + i, e.Category);
            parameters.Add("@Message" + i, e.Message);
            parameters.Add("@Exception" + i, e.Exception);
            parameters.Add("@State" + i, e.State);
            parameters.Add("@ThreadId" + i, e.ThreadId);
            parameters.Add("@story_id" + i, e.StoryId);
            parameters.Add("@ThreadScope" + i, e.ThreadScope);
            parameters.Add("@AgentName" + i, e.AgentName);
            parameters.Add("@Context" + i, e.Context);
            parameters.Add("@analized" + i, e.Analized ? 1 : 0);
            parameters.Add("@chat_text" + i, e.ChatText);
            parameters.Add("@Result" + i, e.Result);
        }

        sb.Append(";");

        try
        {
            await conn.ExecuteAsync(sb.ToString(), parameters);
        }
        catch (SqliteException ex) when (ex.Message != null && ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase) && ex.Message.Contains("story_id", StringComparison.OrdinalIgnoreCase))
        {
            // Backward-compat: database not migrated yet.
            var legacyCols = new[] { "Ts", "Level", "Category", "Message", "Exception", "State", "ThreadId", "ThreadScope", "AgentName", "Context", "analized", "chat_text", "Result" };
            var legacySb = new System.Text.StringBuilder();
            legacySb.Append("INSERT INTO Log (" + string.Join(", ", legacyCols) + ") VALUES ");
            var legacyParams = new DynamicParameters();

            for (int i = 0; i < list.Count; i++)
            {
                var pNames = legacyCols.Select(c => "@" + c + i).ToArray();
                legacySb.Append("(" + string.Join(", ", pNames) + ")");
                if (i < list.Count - 1) legacySb.Append(",");

                var e = list[i];
                legacyParams.Add("@Ts" + i, e.Ts);
                legacyParams.Add("@Level" + i, e.Level);
                legacyParams.Add("@Category" + i, e.Category);
                legacyParams.Add("@Message" + i, e.Message);
                legacyParams.Add("@Exception" + i, e.Exception);
                legacyParams.Add("@State" + i, e.State);
                legacyParams.Add("@ThreadId" + i, e.ThreadId);
                legacyParams.Add("@ThreadScope" + i, e.ThreadScope);
                legacyParams.Add("@AgentName" + i, e.AgentName);
                legacyParams.Add("@Context" + i, e.Context);
                legacyParams.Add("@analized" + i, e.Analized ? 1 : 0);
                legacyParams.Add("@chat_text" + i, e.ChatText);
                legacyParams.Add("@Result" + i, e.Result);
            }

            legacySb.Append(";");
            await conn.ExecuteAsync(legacySb.ToString(), legacyParams);
        }
    }

    private void SeedDefaultOpenAiModels()
    {
        // Only seed if the models table is empty
        using var conn = CreateConnection();
        conn.Open();
        var count = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM models");
        if (count > 0)
        {
            // Table already has models, skip seeding
            return;
        }

        // Add currently enabled models from the database
        var defaults = new List<ModelInfo>
        {
            new ModelInfo { Name = "gpt-4o-mini", Provider = "openai", IsLocal = false, MaxContext = 128000, ContextToUse = 16000, CostInPerToken = 0.00015, CostOutPerToken = 0.0006, Enabled = true },
            new ModelInfo { Name = "gpt-4o", Provider = "openai", IsLocal = false, MaxContext = 128000, ContextToUse = 16000, CostInPerToken = 0.0025, CostOutPerToken = 0.01, Enabled = true },
        };

        foreach (var m in defaults)
        {
            try
            {
                UpsertModel(m);
            }
            catch
            {
                // ignore individual seed failures
            }
        }
    }





    private static void EnsureUsageRow(IDbConnection connRaw, string monthKey)
    {
        // Accept IDbConnection to work with Dapper
        if (connRaw is SqliteConnection conn)
        {
            conn.Execute("INSERT OR IGNORE INTO usage_state(month, tokens_this_run, tokens_this_month, cost_this_month) VALUES(@m, 0, 0, 0)", new { m = monthKey });
        }
    }

    private static string SelectModelColumns()
    {
        // Return columns in the EXACT order they appear in the database table
        // This order MUST match the database schema, not the ModelInfo class property order
        // Note: 'speed' column is excluded as it's not in ModelInfo
        return string.Join(", ", new[] { 
            "Id", 
            "Name", 
            "Provider", 
            "Endpoint", 
            "IsLocal", 
            "MaxContext", 
            "ContextToUse", 
            "FunctionCallingScore",
            "CostInPerToken", 
            "CostOutPerToken", 
            "LimitTokensDay", 
            "LimitTokensWeek", 
            "LimitTokensMonth", 
            "Metadata", 
            "Note",
            "Enabled", 
            "CreatedAt", 
            "UpdatedAt", 
            "TestDurationSeconds",
            "NoTools",
            "WriterScore",
            "BaseScore", 
            "TextEvalScore", 
            "TtsScore", 
            "MusicScore", 
            "FxScore", 
            "AmbientScore",
            "TotalScore"
        });
    }

    // Retrieve recent log entries with optional filtering by level or category and support offset for pagination.
    public List<TinyGenerator.Models.LogEntry> GetRecentLogs(int limit = 200, int offset = 0, string? level = null, string? category = null)
    {
        using var context = CreateDbContext();
        IQueryable<LogEntry> query = context.Logs;

        if (!string.IsNullOrWhiteSpace(level))
            query = query.Where(l => l.Level == level);
        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(l => l.Category != null && l.Category.Contains(category));

        return query.OrderByDescending(l => l.Id).Skip(offset).Take(limit).ToList();
    }

    public IReadOnlyDictionary<string, AppEventDefinition> GetAppEventDefinitions()
    {
        try
        {
            using var context = CreateDbContext();
            var events = context.AppEvents.ToList();
            return events.ToDictionary(ev => ev.EventType, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, AppEventDefinition>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // Alias for compatibility
    public List<TinyGenerator.Models.LogEntry> ListLogs(int limit = 200, int offset = 0, string? level = null, string? category = null)
    {
        return GetRecentLogs(limit, offset, level, category);
    }

    public List<TinyGenerator.Models.LogEntry> GetLogsByThread(string threadId, int limit = 500)
    {
        if (string.IsNullOrWhiteSpace(threadId))
            return new List<TinyGenerator.Models.LogEntry>();

        if (!int.TryParse(threadId, out var threadNumericId))
            return new List<TinyGenerator.Models.LogEntry>();

        using var context = CreateDbContext();
        return context.Logs.Where(l => l.ThreadId == threadNumericId).OrderBy(l => l.Id).Take(limit).ToList();
    }

    public void SetLogAnalyzed(string threadId, bool analyzed)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return;
        if (!int.TryParse(threadId, out var threadNumericId)) return;

        using var context = CreateDbContext();
        var logs = context.Logs.Where(l => l.ThreadId == threadNumericId).ToList();
        foreach (var log in logs)
        {
            log.Analized = analyzed;
        }
        context.SaveChanges();
    }

    public void DeleteLogAnalysesByThread(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return;
        using var context = CreateDbContext();
        var analyses = context.LogAnalyses.Where(la => la.ThreadId == threadId).ToList();
        if (analyses.Count > 0)
        {
            context.LogAnalyses.RemoveRange(analyses);
            context.SaveChanges();
        }
    }

    /// <summary>
    /// Restituisce la lista dei threadId presenti nei log che non hanno analisi salvata.
    /// </summary>
    public List<int> ListThreadsPendingAnalysis(int max = 200)
    {
        using var context = CreateDbContext();
        // Get distinct thread IDs from logs that are not analyzed
        var pendingThreads = context.Logs
            .Where(l => l.ThreadId > 0 && !l.Analized)
            .Select(l => l.ThreadId)
            .Distinct()
            .OrderByDescending(t => t)
            .Take(max)
            .ToList();
        return pendingThreads;
    }

    public void InsertLogAnalysis(TinyGenerator.Models.LogAnalysis analysis)
    {
        if (analysis == null) return;
        using var context = CreateDbContext();
        context.LogAnalyses.Add(analysis);
        context.SaveChanges();
    }

    public List<TinyGenerator.Models.LogAnalysis> GetLogAnalyses(int limit = 200)
    {
        using var context = CreateDbContext();
        return context.LogAnalyses.OrderByDescending(la => la.Id).Take(limit).ToList();
    }

    public List<TinyGenerator.Models.LogAnalysis> GetLogAnalysesByThread(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
            return new List<TinyGenerator.Models.LogAnalysis>();

        using var context = CreateDbContext();
        return context.LogAnalyses.Where(la => la.ThreadId == threadId).OrderByDescending(la => la.Id).ToList();
    }

    public int GetLogCount(string? level = null)
    {
        using var context = CreateDbContext();
        IQueryable<LogEntry> query = context.Logs;
        if (!string.IsNullOrWhiteSpace(level))
            query = query.Where(l => l.Level == level);
        return query.Count();
    }

    public void ClearLogs()
    {
        using var context = CreateDbContext();
        // EF Core doesn't support efficient bulk delete, but for log cleanup this is acceptable
        var logs = context.Logs.ToList();
        context.Logs.RemoveRange(logs);
        context.SaveChanges();
    }

    public List<LogEntry> GetLogsByThreadId(int threadId)
    {
        using var context = CreateDbContext();
        return context.Logs.Where(l => l.ThreadId == threadId).OrderByDescending(l => l.Id).ToList();
    }

    public LogEntry? GetLogById(long id)
    {
        using var context = CreateDbContext();
        try
        {
            return context.Logs.Find(id);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes log entries older than a specified number of days if total log count exceeds threshold.
    /// </summary>
    public void CleanupOldLogs(int daysOld = 7, int countThreshold = 1000)
    {
        try
        {
            using var context = CreateDbContext();
            var count = context.Logs.Count();
            if (count <= countThreshold) return;

            var cutoffDate = DateTime.UtcNow.AddDays(-daysOld);
            var cutoffStr = cutoffDate.ToString("o");
            
            // Filter logs by comparing timestamp strings (ISO 8601 format is sortable)
            var oldLogs = context.Logs.Where(l => string.Compare(l.Ts, cutoffStr) < 0).ToList();
            if (oldLogs.Count > 0)
            {
                context.Logs.RemoveRange(oldLogs);
                context.SaveChanges();
                System.Diagnostics.Debug.WriteLine($"[DB] Log cleanup: Deleted {oldLogs.Count} log entries older than {daysOld} days.");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DB] Log cleanup failed: {ex.Message}");
        }
    }

    // Helper methods for model updates
    public void UpdateModelContext(string modelName, int contextToUse)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return;

        var existing = GetModelInfo(modelName) ?? new ModelInfo { Name = modelName };
        existing.ContextToUse = contextToUse;

        // Also update MaxContext if it was default or lower than submitted value (safe heuristic)
        if (existing.MaxContext <= 0 || existing.MaxContext < contextToUse)
        {
            existing.MaxContext = contextToUse;
        }

        UpsertModel(existing);
    }

    public void UpdateModelCosts(string modelName, double? costInPer1k, double? costOutPer1k)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return;

        var existing = GetModelInfo(modelName) ?? new ModelInfo { Name = modelName };

        if (costInPer1k.HasValue)
        {
            existing.CostInPerToken = costInPer1k.Value;
        }

        if (costOutPer1k.HasValue)
        {
            existing.CostOutPerToken = costOutPer1k.Value;
        }

        UpsertModel(existing);
    }

    /// <summary>
    /// Get all test groups with their latest results for a specific model.
    /// Returns a list of objects with: group name, score, timestamp, steps summary.
    /// </summary>
    public List<TestGroupSummary> GetModelTestGroupsSummary(string modelName)
    {
        using var conn = CreateConnection();
        conn.Open();

        var modelId = conn.ExecuteScalar<int?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
        if (!modelId.HasValue) return new List<TestGroupSummary>();

        var groups = conn.Query<string>("SELECT DISTINCT test_group FROM model_test_runs WHERE model_id = @mid ORDER BY test_group", new { mid = modelId.Value }).ToList();

        var results = new List<TestGroupSummary>();
        foreach (var group in groups)
        {
            var runId = conn.ExecuteScalar<int?>("SELECT id FROM model_test_runs WHERE model_id = @mid AND test_group = @g ORDER BY id DESC LIMIT 1", new { mid = modelId.Value, g = group });
            if (!runId.HasValue) continue;

            var run = conn.QueryFirstOrDefault("SELECT run_date AS RunDate, passed AS Passed FROM model_test_runs WHERE id = @id", new { id = runId.Value });
            var counts = GetRunStepCounts(runId.Value);
            var score = counts.total > 0 ? (int)Math.Round((double)counts.passed / counts.total * 10) : 0;

            results.Add(new TestGroupSummary
            {
                Group = group,
                RunId = runId.Value,
                Score = score,
                Passed = counts.passed,
                Total = counts.total,
                Timestamp = run?.RunDate,
                Success = run != null && Convert.ToInt32(run?.Passed ?? 0) != 0
            });
        }

        return results;
    }

    /// <summary>
    /// Get detailed test steps for a specific model and group.
    /// Returns list with: stepNumber, stepName, passed, prompt, response, error, durationMs.
    /// </summary>
    public List<object> GetModelTestStepsDetail(string modelName, string groupName)
    {
        using var conn = CreateConnection();
        conn.Open();

        var modelId = conn.ExecuteScalar<int?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
        if (!modelId.HasValue) return new List<object>();

        var runId = conn.ExecuteScalar<int?>("SELECT id FROM model_test_runs WHERE model_id = @mid AND test_group = @g ORDER BY id DESC LIMIT 1", new { mid = modelId.Value, g = groupName });
        if (!runId.HasValue) return new List<object>();

        var steps = conn.Query(@"
            SELECT 
                step_number AS StepNumber,
                step_name AS StepName,
                passed AS Passed,
                input_json AS InputJson,
                output_json AS OutputJson,
                error AS Error,
                duration_ms AS DurationMs
            FROM model_test_steps 
            WHERE run_id = @r 
            ORDER BY step_number", new { r = runId.Value });

        var results = new List<object>();
        foreach (var step in steps)
        {
            string? prompt = null;
            string? response = null;

            // Extract prompt from input_json
            if (!string.IsNullOrWhiteSpace(step.InputJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(step.InputJson);
                    if (doc.RootElement.TryGetProperty("prompt", out System.Text.Json.JsonElement promptEl))
                        prompt = promptEl.GetString();
                }
                catch { }
            }

            // Extract response from output_json
            if (!string.IsNullOrWhiteSpace(step.OutputJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(step.OutputJson);
                    if (doc.RootElement.TryGetProperty("response", out System.Text.Json.JsonElement respEl))
                        response = respEl.GetString();
                    else
                        response = step.OutputJson; // Fallback to raw JSON
                }
                catch
                {
                    response = step.OutputJson;
                }
            }

            results.Add(new
            {
                stepNumber = step.StepNumber,
                stepName = step.StepName ?? $"Step {step.StepNumber}",
                passed = Convert.ToInt32(step.Passed) != 0,
                prompt = prompt,
                response = response,
                error = step.Error,
                durationMs = step.DurationMs
            });
        }

        return results;
    }

    /// <summary>
    /// Recalculate and update the FunctionCallingScore for a model based on all latest group test results.
    /// Score = sum of (1 point per passed test) across all groups' most recent runs.
    /// </summary>
    public void RecalculateModelScore(string modelName)
    {
        using var conn = CreateConnection();
        conn.Open();

        var modelId = conn.ExecuteScalar<int?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
        if (!modelId.HasValue) return;

        // Get all unique test groups for this model
        var groups = conn.Query<string>("SELECT DISTINCT test_group FROM model_test_runs WHERE model_id = @mid", new { mid = modelId.Value }).ToList();

        double totalScore = 0;
        int groupCount = 0;

        foreach (var group in groups)
        {
            // Calculate group-specific score
            RecalculateModelGroupScore(modelName, group);
            
            // Get latest run for this group
            var runId = conn.ExecuteScalar<int?>("SELECT id FROM model_test_runs WHERE model_id = @mid AND test_group = @g ORDER BY id DESC LIMIT 1", new { mid = modelId.Value, g = group });
            if (!runId.HasValue) continue;

            // Get all test definitions for this group to determine test type
            var testDefs = conn.Query<string>("SELECT DISTINCT test_type FROM test_definitions WHERE test_group = @g", new { g = group }).ToList();
            var isWriterTest = testDefs.Any(t => t?.Equals("writer", StringComparison.OrdinalIgnoreCase) == true);

            if (isWriterTest)
            {
                // For writer tests, calculate score based on story evaluations
                // Get all evaluations for stories generated in this test run
                // Join through model_test_assets to get evaluations for this specific run
                var evaluations = conn.Query<double>(
                    @"SELECT se.total_score 
                      FROM stories_evaluations se
                      INNER JOIN model_test_assets mta ON mta.story_id = se.story_id
                      INNER JOIN model_test_steps mts ON mts.id = mta.step_id
                      WHERE mts.run_id = @r",
                    new { r = runId.Value }).ToList();
                
                if (evaluations.Any())
                {
                    // Sum all evaluation scores (each is 0-100, with 10 categories of 0-10 each)
                    double totalEvaluationScore = evaluations.Sum();
                    int totalEvaluationCount = evaluations.Count;
                    
                    // Calculate score as proportion: (total obtained / max possible) * 10
                    // Max possible = number of evaluations × 100 (10 categories × 10 points each)
                    double maxPossibleScore = totalEvaluationCount * 100.0;
                    double groupScore = (totalEvaluationScore / maxPossibleScore) * 10.0;
                    totalScore += groupScore;
                    groupCount++;
                }
            }
            else
            {
                // For non-writer tests (function calling tests), use pass/fail logic
                var counts = conn.QuerySingle<(int passed, int total)>(
                    "SELECT COUNT(CASE WHEN passed = 1 THEN 1 END) as passed, COUNT(*) as total FROM model_test_steps WHERE run_id = @r",
                    new { r = runId.Value });
                
                if (counts.total > 0)
                {
                    // Normalize to 0-10 scale
                    double groupScore = ((double)counts.passed / counts.total) * 10.0;
                    totalScore += groupScore;
                    groupCount++;
                }
            }
        }

        // Calculate final average score across all test groups
        int finalScore = groupCount > 0 ? (int)Math.Round(totalScore / groupCount) : 0;

        // Update model's FunctionCallingScore
        conn.Execute("UPDATE models SET FunctionCallingScore = @score, UpdatedAt = @now WHERE Id = @id",
            new { score = finalScore, id = modelId.Value, now = DateTime.UtcNow.ToString("o") });
        
        // Recalculate TotalScore after updating individual scores
        RecalculateTotalScore(conn, modelId.Value);
    }

    /// <summary>
    /// Recalculate the score for a specific test group and update the corresponding column.
    /// Call this after completing a test run for a group.
    /// </summary>
    public void RecalculateModelGroupScore(string modelName, string groupName)
    {
        using var conn = CreateConnection();
        conn.Open();
        
        var modelId = conn.ExecuteScalar<int?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
        if (!modelId.HasValue) return;
        
        // Map group name to score column
        string? scoreColumn = groupName.ToLower() switch
        {
            "base" => "BaseScore",
            "texteval" => "TextEvalScore",
            "tts" => "TtsScore",
            "music" => "MusicScore",
            "fx" => "FxScore",
            "ambient" => "AmbientScore",
            "writer" => "WriterScore", // writer uses complex calculation, skip here
            _ => null
        };
        
        if (scoreColumn == null || scoreColumn == "WriterScore") return;
        
        // Calculate score for this group
        RecalculateGroupScore(conn, groupName, scoreColumn);
        
        // Recalculate TotalScore
        RecalculateTotalScore(conn, modelId.Value);
    }

    /// <summary>
    /// Recalculate TotalScore as the average of all non-zero scores.
    /// </summary>
    private void RecalculateTotalScore(System.Data.IDbConnection conn, int modelId)
    {
        var sql = @"
UPDATE models
SET TotalScore = (
    WriterScore +
    BaseScore +
    TextEvalScore +
    TtsScore +
    MusicScore +
    FxScore +
    AmbientScore
)
WHERE Id = @modelId;";
        
        conn.Execute(sql, new { modelId });
    }

    // Story Status CRUD operations
    public List<StoryStatus> ListAllStoryStatuses()
    {
        using var context = CreateDbContext();
        return context.StoriesStatus.OrderBy(s => s.Step).ThenBy(s => s.Code).ToList();
    }

    /// <summary>
    /// Return paged StoryStatus entries with optional search and sort. PageIndex starts at 1.
    /// </summary>
    public (List<StoryStatus> Items, int TotalCount) GetPagedStoryStatuses(int pageIndex, int pageSize, string? search = null, string? sortBy = null, bool ascending = true)
    {
        if (pageIndex < 1) pageIndex = 1;
        if (pageSize <= 0) pageSize = 25;
        using var context = CreateDbContext();
        IQueryable<StoryStatus> query = context.StoriesStatus;

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.ToLower();
            query = query.Where(s =>
                (s.Code != null && s.Code.ToLower().Contains(q)) ||
                (s.Description != null && s.Description.ToLower().Contains(q)) ||
                (s.FunctionName != null && s.FunctionName.ToLower().Contains(q)));
        }

        var col = (sortBy ?? "step").ToLowerInvariant();
        query = col switch
        {
            "code" => ascending ? query.OrderBy(s => s.Code) : query.OrderByDescending(s => s.Code),
            "description" => ascending ? query.OrderBy(s => s.Description) : query.OrderByDescending(s => s.Description),
            "step" => ascending ? query.OrderBy(s => s.Step) : query.OrderByDescending(s => s.Step),
            _ => ascending ? query.OrderBy(s => s.Step) : query.OrderByDescending(s => s.Step)
        };

        var total = query.Count();
        var items = query.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToList();
        return (items, total);
    }

    public StoryStatus? GetStoryStatusById(int id)
    {
        using var context = CreateDbContext();
        return context.StoriesStatus.Find(id);
    }

    public StoryStatus? GetStoryStatusByCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        using var context = CreateDbContext();
        return context.StoriesStatus.FirstOrDefault(s => s.Code == code);
    }

    public void UpdateStoryFolder(long storyId, string folder)
    {
        if (storyId <= 0 || string.IsNullOrWhiteSpace(folder)) return;
        using var context = CreateDbContext();
        var story = context.Stories.Find(storyId);
        if (story != null)
        {
            story.Folder = folder;
            context.SaveChanges();
        }
    }

    private void UpdateStoryStatusAfterEvaluation(TinyGeneratorDbContext? context, long storyId, int? agentId)
    {
        if (context == null || !agentId.HasValue) return;
        try
        {
            var totalEvaluations = context.StoryEvaluations.Count(se => se.StoryId == storyId);
            if (totalEvaluations >= 2)
            {
                var story = context.Stories.Find(storyId);
                if (story != null)
                {
                    story.StatusId = EvaluatedStatusId;
                    context.SaveChanges();
                }
            }
        }
        catch
        {
            // ignore best-effort status updates
        }
    }

    // Backwards-compatible overload for callers that still use a raw DB connection
    private void UpdateStoryStatusAfterEvaluation(IDbConnection conn, long storyId, int? agentId)
    {
        if (conn == null || !agentId.HasValue) return;
        try
        {
            var totalEvaluations = conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM stories_evaluations WHERE story_id = @sid",
                new { sid = storyId });
            if (totalEvaluations >= 2)
            {
                conn.Execute(
                    "UPDATE stories SET status_id = @statusId WHERE id = @sid",
                    new { statusId = EvaluatedStatusId, sid = storyId });
            }
        }
        catch
        {
            // ignore best-effort status updates
        }
    }

    public int InsertStoryStatus(StoryStatus status)
    {
        using var context = CreateDbContext();
        context.StoriesStatus.Add(status);
        context.SaveChanges();
        return status.Id;
    }

    public void UpdateStoryStatus(StoryStatus status)
    {
        using var context = CreateDbContext();
        var existing = context.StoriesStatus.Find(status.Id);
        if (existing != null)
        {
            existing.Code = status.Code;
            existing.Description = status.Description;
            existing.Step = status.Step;
            existing.Color = status.Color;
            existing.OperationType = status.OperationType;
            existing.AgentType = status.AgentType;
            existing.FunctionName = status.FunctionName;
            existing.CaptionToExecute = status.CaptionToExecute;
            context.SaveChanges();
        }
    }

    public void DeleteStoryStatus(int id)
    {
        using var context = CreateDbContext();
        var status = context.StoriesStatus.Find(id);
        if (status != null)
        {
            context.StoriesStatus.Remove(status);
            context.SaveChanges();
        }
    }

    // ========== Multi-Step Task Execution Methods ==========

    public long CreateTaskExecution(TinyGenerator.Models.TaskExecution execution)
    {
        using var context = CreateDbContext();
        context.TaskExecutions.Add(execution);
        context.SaveChanges();
        return execution.Id;
    }

    public TinyGenerator.Models.TaskExecution? GetTaskExecutionById(long id)
    {
        using var context = CreateDbContext();
        return context.TaskExecutions.Find(id);
    }

    public void DeleteTaskExecution(long executionId)
    {
        using var context = CreateDbContext();
        using var transaction = context.Database.BeginTransaction();
        try
        {
            var steps = context.TaskExecutionSteps.Where(s => s.ExecutionId == executionId).ToList();
            context.TaskExecutionSteps.RemoveRange(steps);
            var execution = context.TaskExecutions.Find(executionId);
            if (execution != null)
                context.TaskExecutions.Remove(execution);
            context.SaveChanges();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Delete active task executions (and their steps). If entityId is provided, limit to that entity.
    /// If taskType is provided, limit to that task type. Returns number of executions deleted.
    /// </summary>
    public int DeleteActiveExecutions(long? entityId = null, string? taskType = null)
    {
        using var context = CreateDbContext();
        using var transaction = context.Database.BeginTransaction();
        try
        {
            IQueryable<TinyGenerator.Models.TaskExecution> query = context.TaskExecutions
                .Where(e => e.Status == "pending" || e.Status == "in_progress");
            
            if (entityId.HasValue)
                query = query.Where(e => e.EntityId == entityId.Value);
            if (!string.IsNullOrWhiteSpace(taskType))
                query = query.Where(e => e.TaskType == taskType);
            
            var executions = query.ToList();
            foreach (var exec in executions)
            {
                var steps = context.TaskExecutionSteps.Where(s => s.ExecutionId == exec.Id).ToList();
                context.TaskExecutionSteps.RemoveRange(steps);
                context.TaskExecutions.Remove(exec);
            }
            context.SaveChanges();
            transaction.Commit();
            return executions.Count;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void UpdateTaskExecution(TinyGenerator.Models.TaskExecution execution)
    {
        using var context = CreateDbContext();
        var existing = context.TaskExecutions.Find(execution.Id);
        if (existing != null)
        {
            existing.TaskType = execution.TaskType;
            existing.EntityId = execution.EntityId;
            existing.StepPrompt = execution.StepPrompt;
            existing.InitialContext = execution.InitialContext;
            existing.CurrentStep = execution.CurrentStep;
            existing.MaxStep = execution.MaxStep;
            existing.RetryCount = execution.RetryCount;
            existing.Status = execution.Status;
            existing.ExecutorAgentId = execution.ExecutorAgentId;
            existing.CheckerAgentId = execution.CheckerAgentId;
            existing.Config = execution.Config;
            existing.UpdatedAt = execution.UpdatedAt;
            context.SaveChanges();
        }
    }

    public TinyGenerator.Models.TaskExecution? GetActiveExecutionForEntity(long? entityId, string taskType)
    {
        if (!entityId.HasValue) return null;
        using var context = CreateDbContext();
        return context.TaskExecutions
            .FirstOrDefault(e => e.EntityId == entityId.Value && e.TaskType == taskType && (e.Status == "pending" || e.Status == "in_progress"));
    }

    public long CreateTaskExecutionStep(TinyGenerator.Models.TaskExecutionStep step)
    {
        using var context = CreateDbContext();
        context.TaskExecutionSteps.Add(step);
        context.SaveChanges();
        return step.Id;
    }

    public List<TinyGenerator.Models.TaskExecutionStep> GetTaskExecutionSteps(long executionId)
    {
        using var context = CreateDbContext();
        return context.TaskExecutionSteps
            .Where(s => s.ExecutionId == executionId)
            .OrderBy(s => s.StepNumber)
            .ToList();
    }

    public TinyGenerator.Models.TaskTypeInfo? GetTaskTypeByCode(string code)
    {
        using var context = CreateDbContext();
        return context.TaskTypes.FirstOrDefault(t => t.Code == code);
    }

    // List all task types
    public List<TinyGenerator.Models.TaskTypeInfo> ListTaskTypes()
    {
        using var context = CreateDbContext();
        return context.TaskTypes.OrderBy(t => t.Code).ToList();
    }

    /// <summary>
    /// Return paged task types with optional search and sort. PageIndex starts at 1.
    /// </summary>
    public (List<TinyGenerator.Models.TaskTypeInfo> Items, int TotalCount) GetPagedTaskTypes(
        int pageIndex,
        int pageSize,
        string? search = null,
        string? orderBy = null)
    {
        if (pageIndex < 1) pageIndex = 1;
        if (pageSize <= 0) pageSize = 25;
        using var context = CreateDbContext();
        var query = context.TaskTypes.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(t =>
                (t.Code ?? string.Empty).Contains(s) ||
                (t.Description ?? string.Empty).Contains(s) ||
                (t.DefaultExecutorRole ?? string.Empty).Contains(s) ||
                (t.DefaultCheckerRole ?? string.Empty).Contains(s));
        }

        var ob = (orderBy ?? "code").ToLowerInvariant();
        query = ob switch
        {
            "description" => query.OrderBy(t => t.Description).ThenBy(t => t.Code),
            "executor" => query.OrderBy(t => t.DefaultExecutorRole).ThenBy(t => t.Code),
            "checker" => query.OrderBy(t => t.DefaultCheckerRole).ThenBy(t => t.Code),
            "merge" => query.OrderBy(t => t.OutputMergeStrategy).ThenBy(t => t.Code),
            _ => query.OrderBy(t => t.Code)
        };

        var total = query.Count();
        var items = query.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToList();
        return (items, total);
    }

    // Upsert a task type by code
    public void UpsertTaskType(TinyGenerator.Models.TaskTypeInfo tt)
    {
        if (tt == null || string.IsNullOrWhiteSpace(tt.Code)) return;
        using var context = CreateDbContext();
        var existing = context.TaskTypes.FirstOrDefault(t => t.Code == tt.Code);
        if (existing != null)
        {
            existing.Description = tt.Description;
            existing.DefaultExecutorRole = tt.DefaultExecutorRole;
            existing.DefaultCheckerRole = tt.DefaultCheckerRole;
            existing.OutputMergeStrategy = tt.OutputMergeStrategy;
            existing.ValidationCriteria = tt.ValidationCriteria;
        }
        else
        {
            context.TaskTypes.Add(tt);
        }
        context.SaveChanges();
    }

    public void DeleteTaskTypeByCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        using var context = CreateDbContext();
        var tt = context.TaskTypes.FirstOrDefault(t => t.Code == code);
        if (tt != null)
        {
            context.TaskTypes.Remove(tt);
            context.SaveChanges();
        }
    }

    public TinyGenerator.Models.StepTemplate? GetStepTemplateById(long id)
    {
        using var context = CreateDbContext();
        return context.StepTemplates.Find(id);
    }

    public TinyGenerator.Models.StepTemplate? GetStepTemplateByName(string name)
    {
        using var context = CreateDbContext();
        return context.StepTemplates.FirstOrDefault(s => s.Name == name);
    }

    public List<TinyGenerator.Models.StepTemplate> ListStepTemplates(string? taskType = null)
    {
        using var context = CreateDbContext();
        IQueryable<TinyGenerator.Models.StepTemplate> query = context.StepTemplates;
        if (!string.IsNullOrWhiteSpace(taskType))
            query = query.Where(s => s.TaskType == taskType);
        return query.OrderBy(s => s.Name).ToList();
    }

    /// <summary>
    /// Return paged step templates with optional search and sort. PageIndex starts at 1.
    /// </summary>
    public (List<TinyGenerator.Models.StepTemplate> Items, int TotalCount) GetPagedStepTemplates(
        int pageIndex,
        int pageSize,
        string? search = null,
        string? orderBy = null,
        string? taskType = null)
    {
        if (pageIndex < 1) pageIndex = 1;
        if (pageSize <= 0) pageSize = 25;
        using var context = CreateDbContext();
        IQueryable<TinyGenerator.Models.StepTemplate> query = context.StepTemplates.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(taskType))
        {
            query = query.Where(s => s.TaskType == taskType);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(t =>
                (t.Name ?? string.Empty).Contains(s) ||
                (t.TaskType ?? string.Empty).Contains(s) ||
                (t.Description ?? string.Empty).Contains(s));
        }

        var ob = (orderBy ?? "name").ToLowerInvariant();
        query = ob switch
        {
            "tasktype" => query.OrderBy(t => t.TaskType).ThenBy(t => t.Name),
            "description" => query.OrderBy(t => t.Description).ThenBy(t => t.Name),
            "created" => query.OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Name),
            "updated" => query.OrderByDescending(t => t.UpdatedAt).ThenBy(t => t.Name),
            _ => query.OrderBy(t => t.Name)
        };

        var total = query.Count();
        var items = query.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToList();
        return (items, total);
    }

    public void UpsertStepTemplate(TinyGenerator.Models.StepTemplate template)
    {
        using var context = CreateDbContext();
        var existing = context.StepTemplates.FirstOrDefault(s => s.Name == template.Name);
        if (existing != null)
        {
            existing.TaskType = template.TaskType;
            existing.StepPrompt = template.StepPrompt;
            existing.Instructions = template.Instructions;
            existing.Description = template.Description;
            existing.CharactersStep = template.CharactersStep;
            existing.EvaluationSteps = template.EvaluationSteps;
            existing.TramaSteps = template.TramaSteps;
            existing.MinCharsTrama = template.MinCharsTrama;
            existing.MinCharsStory = template.MinCharsStory;
            existing.FullStoryStep = template.FullStoryStep;
            existing.UpdatedAt = DateTime.UtcNow.ToString("o");
        }
        else
        {
            context.StepTemplates.Add(template);
        }
        context.SaveChanges();
    }

    public void UpdateStepTemplate(TinyGenerator.Models.StepTemplate template)
    {
        using var context = CreateDbContext();
        var existing = context.StepTemplates.Find(template.Id);
        if (existing != null)
        {
            existing.Name = template.Name;
            existing.TaskType = template.TaskType;
            existing.StepPrompt = template.StepPrompt;
            existing.Instructions = template.Instructions;
            existing.Description = template.Description;
            existing.CharactersStep = template.CharactersStep;
            existing.EvaluationSteps = template.EvaluationSteps;
            existing.TramaSteps = template.TramaSteps;
            existing.MinCharsTrama = template.MinCharsTrama;
            existing.MinCharsStory = template.MinCharsStory;
            existing.FullStoryStep = template.FullStoryStep;
            existing.UpdatedAt = template.UpdatedAt;
            context.SaveChanges();
        }
    }

    public void DeleteStepTemplate(long id)
    {
        using var context = CreateDbContext();
        var template = context.StepTemplates.Find(id);
        if (template != null)
        {
            context.StepTemplates.Remove(template);
            context.SaveChanges();
        }
    }

    public void CleanupOldTaskExecutions()
    {
        using var context = CreateDbContext();
        var cutoffDate = DateTime.UtcNow.AddDays(-7).ToString("o");
        var oldExecutions = context.TaskExecutions
            .Where(e => (e.Status == "completed" || e.Status == "failed") && string.Compare(e.UpdatedAt, cutoffDate) < 0)
            .ToList();
        if (oldExecutions.Count > 0)
        {
            context.TaskExecutions.RemoveRange(oldExecutions);
            context.SaveChanges();
            Console.WriteLine($"[DB] Cleaned up {oldExecutions.Count} old task executions");
        }
    }

    #region Coherence Evaluation Methods

    /// <summary>
    /// Salva i fatti estratti da un chunk di storia
    /// </summary>
    public void SaveChunkFacts(ChunkFacts facts)
    {
        using var context = CreateDbContext();
        context.ChunkFacts.Add(facts);
        context.SaveChanges();
    }

    /// <summary>
    /// Recupera i fatti di un chunk specifico
    /// </summary>
    public ChunkFacts? GetChunkFacts(int storyId, int chunkNumber)
    {
        using var context = CreateDbContext();
        return context.ChunkFacts.FirstOrDefault(c => c.StoryId == storyId && c.ChunkNumber == chunkNumber);
    }

    /// <summary>
    /// Recupera tutti i fatti di una storia
    /// </summary>
    public List<ChunkFacts> GetAllChunkFacts(int storyId)
    {
        using var context = CreateDbContext();
        return context.ChunkFacts.Where(c => c.StoryId == storyId).OrderBy(c => c.ChunkNumber).ToList();
    }

    /// <summary>
    /// Salva lo score di coerenza di un chunk
    /// </summary>
    public void SaveCoherenceScore(CoherenceScore score)
    {
        using var context = CreateDbContext();
        context.CoherenceScores.Add(score);
        context.SaveChanges();
    }

    /// <summary>
    /// Recupera tutti gli score di coerenza di una storia
    /// </summary>
    public List<CoherenceScore> GetCoherenceScores(int storyId)
    {
        using var context = CreateDbContext();
        return context.CoherenceScores.Where(c => c.StoryId == storyId).OrderBy(c => c.ChunkNumber).ToList();
    }

    /// <summary>
    /// Salva la coerenza globale finale di una storia
    /// </summary>
    public void SaveGlobalCoherence(GlobalCoherence coherence)
    {
        using var context = CreateDbContext();
        var existing = context.GlobalCoherences.FirstOrDefault(g => g.StoryId == coherence.StoryId);
        if (existing != null)
        {
            existing.GlobalCoherenceValue = coherence.GlobalCoherenceValue;
            existing.ChunkCount = coherence.ChunkCount;
            existing.Notes = coherence.Notes;
            existing.Ts = coherence.Ts;
        }
        else
        {
            context.GlobalCoherences.Add(coherence);
        }
        context.SaveChanges();
    }

    /// <summary>
    /// Recupera la coerenza globale di una storia
    /// </summary>
    public GlobalCoherence? GetGlobalCoherence(int storyId)
    {
        using var context = CreateDbContext();
        return context.GlobalCoherences.FirstOrDefault(g => g.StoryId == storyId);
    }

    #endregion

    #region Sentiment Mapping

    /// <summary>
    /// Cerca una mappatura sentimento esistente
    /// </summary>
    public MappedSentiment? GetMappedSentiment(string sourceSentiment)
    {
        if (string.IsNullOrWhiteSpace(sourceSentiment)) return null;
        using var conn = CreateConnection();
        conn.Open();
        return conn.QueryFirstOrDefault<MappedSentiment>(
            "SELECT * FROM mapped_sentiments WHERE source_sentiment = @sourceSentiment COLLATE NOCASE",
            new { sourceSentiment });
    }

    /// <summary>
    /// Recupera tutte le mappature sentimento
    /// </summary>
    public List<MappedSentiment> GetAllMappedSentiments()
    {
        using var conn = CreateConnection();
        conn.Open();
        return conn.Query<MappedSentiment>(
            "SELECT * FROM mapped_sentiments ORDER BY source_sentiment").ToList();
    }

    /// <summary>
    /// Inserisce o aggiorna una mappatura sentimento
    /// </summary>
    public void InsertMappedSentiment(MappedSentiment mapping)
    {
        if (mapping == null) return;
        using var conn = CreateConnection();
        conn.Open();
        conn.Execute(@"
            INSERT OR REPLACE INTO mapped_sentiments 
                (source_sentiment, dest_sentiment, confidence, source_type, created_at)
            VALUES 
                (@SourceSentiment, @DestSentiment, @Confidence, @SourceType, @CreatedAt)",
            mapping);
    }

    /// <summary>
    /// Elimina una mappatura sentimento
    /// </summary>
    public void DeleteMappedSentiment(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
        conn.Execute("DELETE FROM mapped_sentiments WHERE id = @id", new { id });
    }

    /// <summary>
    /// Recupera tutti gli embedding dei sentimenti destinazione
    /// </summary>
    public List<SentimentEmbedding> GetAllSentimentEmbeddings()
    {
        using var conn = CreateConnection();
        conn.Open();
        return conn.Query<SentimentEmbedding>("SELECT * FROM sentiment_embeddings").ToList();
    }

    /// <summary>
    /// Inserisce o aggiorna un embedding sentimento
    /// </summary>
    public void UpsertSentimentEmbedding(SentimentEmbedding embedding)
    {
        if (embedding == null) return;
        using var conn = CreateConnection();
        conn.Open();
        conn.Execute(@"
            INSERT OR REPLACE INTO sentiment_embeddings 
                (sentiment, embedding, model, created_at)
            VALUES 
                (@Sentiment, @Embedding, @Model, @CreatedAt)",
            embedding);
    }

    /// <summary>
    /// Cerca un agente per nome
    /// </summary>
    public TinyGenerator.Models.Agent? GetAgentByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        using var context = CreateDbContext();
        return context.Agents.FirstOrDefault(a => a.Name == name);
    }

    /// <summary>
    /// Top 10 storie per media valutazioni dalla tabella stories_evaluations
    /// </summary>
    public List<(long Id, string Title, string Agent, double AvgScore, string Timestamp, bool GeneratedMixedAudio)> GetTopStoriesByEvaluation(int top = 10)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"
            SELECT 
                s.id AS Id,
                COALESCE(s.title, '') AS Title,
                COALESCE(a.name, '') AS Agent,
                AVG(se.total_score) AS AvgScore,
                COALESCE(s.ts, '') AS Timestamp
            FROM stories s
            INNER JOIN stories_evaluations se ON se.story_id = s.id
            LEFT JOIN agents a ON s.agent_id = a.id
            LEFT JOIN models m ON s.model_id = m.Id
            GROUP BY s.id
            ORDER BY AvgScore DESC
            LIMIT @top";
        var list = conn.Query(sql, new { top }).Select(r => (
            Id: (long)r.Id,
            Title: (string)r.Title,
            Agent: (string)r.Agent,
            AvgScore: (double)r.AvgScore,
            Timestamp: (string)r.Timestamp,
            GeneratedMixedAudio: ((IDictionary<string, object>)r).ContainsKey("GeneratedMixedAudio") ? Convert.ToBoolean(((IDictionary<string, object>)r)["GeneratedMixedAudio"]) : false
        )).ToList();
        // The query above doesn't include GeneratedMixedAudio column in SQL because SQLite cannot reference it via aggregate grouping easily; retrieve generated flag separately
        foreach (var i in Enumerable.Range(0, list.Count))
        {
            try {
                var gid = list[i].Id;
                var gm = conn.QuerySingleOrDefault<bool?>("SELECT generated_mixed_audio FROM stories WHERE id = @id", new { id = gid }) ?? false;
                list[i] = (list[i].Id, list[i].Title, list[i].Agent, list[i].AvgScore, list[i].Timestamp, gm);
            } catch { }
        }
        return list;
    }

    /// <summary>
    /// Top 10 scrittori (agenti) per media valutazioni delle loro storie
    /// </summary>
    public List<(string AgentName, string ModelName, double AvgScore, int StoryCount)> GetTopWritersByEvaluation(int top = 10)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"
            SELECT 
                COALESCE(a.name, '') AS AgentName,
                COALESCE(m.Name, '') AS ModelName,
                AVG(se.total_score) AS AvgScore,
                COUNT(DISTINCT s.id) AS StoryCount
            FROM stories s
            INNER JOIN stories_evaluations se ON se.story_id = s.id
            LEFT JOIN agents a ON s.agent_id = a.id
            LEFT JOIN models m ON s.model_id = m.Id
            WHERE s.agent_id IS NOT NULL
            GROUP BY s.agent_id, s.model_id
            ORDER BY AvgScore DESC
            LIMIT @top";
        return conn.Query<(string AgentName, string ModelName, double AvgScore, int StoryCount)>(sql, new { top }).ToList();
    }

    #endregion
}
