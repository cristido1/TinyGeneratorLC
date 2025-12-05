using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using TinyGenerator.Models;
using System.Text.Json;
using System.Threading.Tasks;
using ModelInfo = TinyGenerator.Models.ModelInfo;
using CallRecord = TinyGenerator.Models.CallRecord;
using TestDefinition = TinyGenerator.Models.TestDefinition;

namespace TinyGenerator.Services;

public sealed class DatabaseService
{
    private readonly IOllamaMonitorService? _ollamaMonitor;
    private readonly System.Threading.SemaphoreSlim _dbSemaphore = new System.Threading.SemaphoreSlim(1,1);
    private const int EvaluatedStatusId = 2;
    private const int InitialStoryStatusId = 1;


    private readonly string _connectionString;

    public DatabaseService(string dbPath = "data/storage.db", IOllamaMonitorService? ollamaMonitor = null)
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
        // Enable foreign key enforcement for SQLite connections
        _connectionString = $"Data Source={dbPath};Foreign Keys=True";
        // Defer heavy initialization to the explicit Initialize() method so the
        // service can be registered without blocking `builder.Build()`.
        _ollamaMonitor = ollamaMonitor;
        ctorSw.Stop();
        Console.WriteLine($"[DB] DatabaseService ctor completed in {ctorSw.ElapsedMilliseconds}ms");
    }

    public void SaveChapter(string memoryKey, int chapterNumber, string content)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = "INSERT INTO chapters(memory_key, chapter_number, content, ts) VALUES(@mk, @cn, @c, @ts);";
        conn.Execute(sql, new { mk = memoryKey ?? string.Empty, cn = chapterNumber, c = content ?? string.Empty, ts = DateTime.UtcNow.ToString("o") });
    }

    // Public method to initialize schema and run migrations - call after
    // DI container is built in Program.cs to avoid blocking builder.Build().
    public void Initialize()
    {
        try
        {
            Console.WriteLine("[DB] Initialize() called");
            
            // Check if database file exists; if not, recreate from schema file
            var dbPath = _connectionString.Replace("Data Source=", "").Replace(";", "").Trim();
            if (!File.Exists(dbPath))
            {
                Console.WriteLine($"[DB] Database file not found at {dbPath}, recreating from schema...");
                RecreateFromSchema(dbPath);
            }
            
            InitializeSchema();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Initialize() error: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
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
        using var conn = CreateConnection();
        conn.Open();
        var cols = SelectModelColumns();
        var sql = $"SELECT {cols} FROM models";
        return conn.Query<ModelInfo>(sql).OrderBy(m => m.Name).ToList();
    }

    /// <summary>
    /// Return a lightweight summary of the latest test run for the given model id, or null if none.
    /// </summary>
    public (int runId, string testCode, bool passed, long? durationMs, string? runDate)? GetLatestTestRunSummaryById(int modelId)
    {
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            var sql = @"SELECT id AS RunId, test_group AS TestCode, passed AS Passed, duration_ms AS DurationMs, run_date AS RunDate FROM model_test_runs WHERE model_id = @mid ORDER BY id DESC LIMIT 1";
            var row = conn.QueryFirstOrDefault(sql, new { mid = modelId });
            if (row == null) return null;
            int runId = (int)row.RunId;
            string testCode = row.TestCode ?? string.Empty;
            bool passed = Convert.ToInt32(row.Passed) != 0;
            long? duration = row.DurationMs == null ? (long?)null : Convert.ToInt64(row.DurationMs);
            string? runDate = row.RunDate;
            return (runId, testCode, passed, duration, runDate);
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
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            // Resolve model id from name and query by model_id (model_name column was removed)
            var modelId = conn.ExecuteScalar<int?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
            if (!modelId.HasValue) return null;
            var sql = @"SELECT id AS RunId, test_group AS TestCode, passed AS Passed, duration_ms AS DurationMs, run_date AS RunDate FROM model_test_runs WHERE model_id = @mid ORDER BY id DESC LIMIT 1";
            var row = conn.QueryFirstOrDefault(sql, new { mid = modelId.Value });
            if (row == null) return null;
            int runId = (int)row.RunId;
            string testCode = row.TestCode ?? string.Empty;
            bool passed = Convert.ToInt32(row.Passed) != 0;
            long? duration = row.DurationMs == null ? (long?)null : Convert.ToInt64(row.DurationMs);
            string? runDate = row.RunDate;
            return (runId, testCode, passed, duration, runDate);
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
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            var sql = @"SELECT duration_ms FROM model_test_runs 
                        WHERE model_id = @mid AND test_group = @group 
                        ORDER BY id DESC LIMIT 1";
            var duration = conn.ExecuteScalar<long?>(sql, new { mid = modelId, group = groupName });
            return duration;
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
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            var modelId = conn.ExecuteScalar<int?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
            if (!modelId.HasValue) return null;
            
            var sql = @"SELECT duration_ms FROM model_test_runs 
                        WHERE model_id = @mid AND test_group = @group 
                        ORDER BY id DESC LIMIT 1";
            var duration = conn.ExecuteScalar<long?>(sql, new { mid = modelId.Value, group = groupName });
            return duration;
        }
        catch
        {
            return null;
        }
    }

    public ModelInfo? GetModelInfo(string modelOrProvider)
    {
        if (string.IsNullOrWhiteSpace(modelOrProvider)) return null;
        using var conn = CreateConnection();
        conn.Open();
        var sql = $"SELECT {SelectModelColumns()} FROM models WHERE Name = @Name LIMIT 1";
        var byName = conn.QueryFirstOrDefault<ModelInfo>(sql, new { Name = modelOrProvider });
        if (byName != null) return byName;
        var provider = modelOrProvider.Split(':')[0];
        sql = $"SELECT {SelectModelColumns()} FROM models WHERE Provider = @Provider LIMIT 1";
        return conn.QueryFirstOrDefault<ModelInfo>(sql, new { Provider = provider });
    }

    /// <summary>
    /// Get model info by explicit ID (preferred over name-based lookup).
    /// </summary>
    public ModelInfo? GetModelInfoById(int modelId)
    {
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            var sql = $"SELECT {SelectModelColumns()} FROM models WHERE Id = @Id LIMIT 1";
            return conn.QueryFirstOrDefault<ModelInfo>(sql, new { Id = modelId });
        }
        catch
        {
            return null;
        }
    }

    public void UpsertModel(ModelInfo model)
    {
        if (model == null || string.IsNullOrWhiteSpace(model.Name)) return;
        using var conn = CreateConnection();
        conn.Open();
        var now = DateTime.UtcNow.ToString("o");
        // Ensure CreatedAt/UpdatedAt
        var existing = GetModelInfo(model.Name);
        // Preserve an existing non-zero FunctionCallingScore if the caller didn't set a meaningful score.
        // ModelInfo.FunctionCallingScore is an int (default 0). Many callers create ModelInfo instances
        // for discovery/upsert and leave the score at 0 which would overwrite a previously computed score.
        // If an existing model has a non-zero score and the provided model has 0, keep the existing score.
        if (existing != null && existing.FunctionCallingScore != 0 && model.FunctionCallingScore == 0)
        {
            model.FunctionCallingScore = existing.FunctionCallingScore;
        }
        model.CreatedAt ??= existing?.CreatedAt ?? now;
        model.UpdatedAt = now;

        var sql = @"INSERT INTO models(Name, Provider, Endpoint, IsLocal, MaxContext, ContextToUse, FunctionCallingScore, CostInPerToken, CostOutPerToken, LimitTokensDay, LimitTokensWeek, LimitTokensMonth, Metadata, Note, Enabled, CreatedAt, UpdatedAt, NoTools)
    VALUES(@Name,@Provider,@Endpoint,@IsLocal,@MaxContext,@ContextToUse,@FunctionCallingScore,@CostInPerToken,@CostOutPerToken,@LimitTokensDay,@LimitTokensWeek,@LimitTokensMonth,@Metadata,@Note,@Enabled,@CreatedAt,@UpdatedAt,@NoTools)
    ON CONFLICT(Name) DO UPDATE SET Provider=@Provider, Endpoint=@Endpoint, IsLocal=@IsLocal, MaxContext=@MaxContext, ContextToUse=@ContextToUse, FunctionCallingScore=@FunctionCallingScore, CostInPerToken=@CostInPerToken, CostOutPerToken=@CostOutPerToken, LimitTokensDay=@LimitTokensDay, LimitTokensWeek=@LimitTokensWeek, LimitTokensMonth=@LimitTokensMonth, Metadata=@Metadata, Note=@Note, Enabled=@Enabled, UpdatedAt=@UpdatedAt, NoTools=@NoTools;";

        conn.Execute(sql, model);
    }

    // Delete a model by name from the models table (best-effort). Also deletes related model_test_runs entries.
    public void DeleteModel(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            // Resolve model id first
            var modelId = conn.ExecuteScalar<int?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = name });
            if (modelId.HasValue)
            {
                conn.Execute("DELETE FROM model_test_runs WHERE model_id = @id", new { id = modelId.Value });
            }
            conn.Execute("DELETE FROM models WHERE Name = @Name", new { Name = name });
        }
        catch { }
    }

    public bool TryReserveUsage(string monthKey, long tokensToAdd, double costToAdd, long maxTokensPerRun, double maxCostPerMonth)
    {
        using var conn = CreateConnection();
        conn.Open();
        EnsureUsageRow(conn, monthKey);

        var row = conn.QueryFirstOrDefault<(long tokensThisRun, long tokensThisMonth, double costThisMonth)>("SELECT tokens_this_run AS tokensThisRun, tokens_this_month AS tokensThisMonth, cost_this_month AS costThisMonth FROM usage_state WHERE month = @m", new { m = monthKey });
        var tokensThisRun = row.tokensThisRun;
        var tokensThisMonth = row.tokensThisMonth;
        var costThisMonth = row.costThisMonth;

        if (tokensThisRun + tokensToAdd > maxTokensPerRun) return false;
        if (costThisMonth + costToAdd > maxCostPerMonth) return false;

        conn.Execute("UPDATE usage_state SET tokens_this_run = tokens_this_run + @tokens, tokens_this_month = tokens_this_month + @tokens, cost_this_month = cost_this_month + @cost WHERE month = @m", new { tokens = tokensToAdd, cost = costToAdd, m = monthKey });
        return true;
    }

    public void ResetRunCounters(string monthKey)
    {
        using var conn = CreateConnection();
        conn.Open();
        EnsureUsageRow(conn, monthKey);
        conn.Execute("UPDATE usage_state SET tokens_this_run = 0 WHERE month = @m", new { m = monthKey });
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
        using var conn = CreateConnection();
        conn.Open();
        EnsureUsageRow(conn, monthKey);
        var row = conn.QueryFirstOrDefault<(long tokensThisMonth, double costThisMonth)>("SELECT tokens_this_month AS tokensThisMonth, cost_this_month AS costThisMonth FROM usage_state WHERE month = @m", new { m = monthKey });
        return row;
    }

    public void RecordCall(string model, int inputTokens, int outputTokens, double cost, string request, string response)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"INSERT INTO calls(ts, model, provider, input_tokens, output_tokens, tokens, cost, request, response) VALUES(@ts,@model,@provider,@in,@out,@t,@c,@req,@res)";
        var provider = model?.Split(':')[0] ?? string.Empty;
        var total = inputTokens + outputTokens;
        conn.Execute(sql, new { ts = DateTime.UtcNow.ToString("o"), model, provider, @in = inputTokens, @out = outputTokens, t = total, c = cost, req = request ?? string.Empty, res = response ?? string.Empty });
    }

    public List<CallRecord> GetRecentCalls(int limit = 50)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, ts AS Timestamp, model AS Model, provider AS Provider, input_tokens AS InputTokens, input_tokens AS InputTokens, output_tokens AS OutputTokens, tokens AS Tokens, cost AS Cost, request AS Request, response AS Response FROM calls ORDER BY id DESC LIMIT @Limit";
        try
        {
            var results = conn.Query<CallRecord>(sql, new { Limit = limit });
            return results.ToList();
        }
        catch (Exception ex)
        {
            // Fallback: attempt dynamic mapping if typed mapping fails
            try
            {
                var dynSql = @"SELECT id, ts, model, provider, input_tokens, output_tokens, tokens, cost, request, response FROM calls ORDER BY id DESC LIMIT @Limit";
                var dyn = conn.Query<dynamic>(dynSql, new { Limit = limit });
                return dyn.Select(r => new CallRecord
                {
                    Id = Convert.ToInt64((object?)r.id ?? 0L),
                    Timestamp = (string?)(r.ts ?? string.Empty) ?? string.Empty,
                    Model = (string?)(r.model ?? string.Empty) ?? string.Empty,
                    Provider = (string?)(r.provider ?? string.Empty) ?? string.Empty,
                    InputTokens = Convert.ToInt32((object?)r.input_tokens ?? 0),
                    OutputTokens = Convert.ToInt32((object?)r.output_tokens ?? 0),
                    Tokens = Convert.ToInt32((object?)r.tokens ?? 0),
                    Cost = Convert.ToDouble((object?)r.cost ?? 0.0),
                    Request = (string?)(r.request ?? string.Empty) ?? string.Empty,
                    Response = (string?)(r.response ?? string.Empty) ?? string.Empty
                }).ToList();
            }
            catch (Exception inner)
            {
                Console.WriteLine($"[DB] GetRecentCalls failed: {ex.Message}; fallback also failed: {inner.Message}");
                return new List<CallRecord>();
            }
        }
    }

    // Agents CRUD
    public List<TinyGenerator.Models.Agent> ListAgents()
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT a.id AS Id, a.voice_rowid AS VoiceId, t.name AS VoiceName, a.name AS Name, a.role AS Role, a.model_id AS ModelId, a.skills AS Skills, a.config AS Config, a.json_response_format AS JsonResponseFormat, a.prompt AS Prompt, a.instructions AS Instructions, a.temperature AS Temperature, a.top_p AS TopP, a.execution_plan AS ExecutionPlan, a.is_active AS IsActive, a.multi_step_template_id AS MultiStepTemplateId, st.name AS MultiStepTemplateName, a.created_at AS CreatedAt, a.updated_at AS UpdatedAt, a.notes AS Notes FROM agents a LEFT JOIN tts_voices t ON a.voice_rowid = t.id LEFT JOIN step_templates st ON a.multi_step_template_id = st.id ORDER BY a.name";
        return conn.Query<TinyGenerator.Models.Agent>(sql).ToList();
    }

    public TinyGenerator.Models.Agent? GetAgentById(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT a.id AS Id, a.voice_rowid AS VoiceId, t.name AS VoiceName, a.name AS Name, a.role AS Role, a.model_id AS ModelId, a.skills AS Skills, a.config AS Config, a.json_response_format AS JsonResponseFormat, a.prompt AS Prompt, a.instructions AS Instructions, a.temperature AS Temperature, a.top_p AS TopP, a.execution_plan AS ExecutionPlan, a.is_active AS IsActive, a.multi_step_template_id AS MultiStepTemplateId, st.name AS MultiStepTemplateName, a.created_at AS CreatedAt, a.updated_at AS UpdatedAt, a.notes AS Notes FROM agents a LEFT JOIN tts_voices t ON a.voice_rowid = t.id LEFT JOIN step_templates st ON a.multi_step_template_id = st.id WHERE a.id = @id LIMIT 1";
        return conn.QueryFirstOrDefault<TinyGenerator.Models.Agent>(sql, new { id });
    }

    public int? GetAgentIdByName(string name)
    {
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            var sql = "SELECT id FROM agents WHERE name = @name LIMIT 1";
            var id = conn.ExecuteScalar<long?>(sql, new { name });
            if (id == null || id == 0) return null;
            return (int)id;
        }
        catch { return null; }
    }

    public TinyGenerator.Models.Agent? GetAgentByRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT a.id AS Id, a.voice_rowid AS VoiceId, t.name AS VoiceName, a.name AS Name, a.role AS Role, a.model_id AS ModelId, a.skills AS Skills, a.config AS Config, a.json_response_format AS JsonResponseFormat, a.prompt AS Prompt, a.instructions AS Instructions, a.temperature AS Temperature, a.top_p AS TopP, a.execution_plan AS ExecutionPlan, a.is_active AS IsActive, a.multi_step_template_id AS MultiStepTemplateId, st.name AS MultiStepTemplateName, a.created_at AS CreatedAt, a.updated_at AS UpdatedAt, a.notes AS Notes
                FROM agents a
                LEFT JOIN tts_voices t ON a.voice_rowid = t.id
                LEFT JOIN step_templates st ON a.multi_step_template_id = st.id
                WHERE LOWER(a.role) = LOWER(@role)
                ORDER BY a.is_active DESC, a.id ASC
                LIMIT 1";
        return conn.QueryFirstOrDefault<TinyGenerator.Models.Agent>(sql, new { role });
    }

    public int InsertAgent(TinyGenerator.Models.Agent a)
    {
        using var conn = CreateConnection();
        conn.Open();
        var now = DateTime.UtcNow.ToString("o");
        a.CreatedAt ??= now;
        a.UpdatedAt = now;
        var sql = @"INSERT INTO agents(voice_rowid, name, role, model_id, skills, config, json_response_format, prompt, instructions, temperature, top_p, execution_plan, is_active, multi_step_template_id, created_at, updated_at, notes) VALUES(@VoiceId,@Name,@Role,@ModelId,@Skills,@Config,@JsonResponseFormat,@Prompt,@Instructions,@Temperature,@TopP,@ExecutionPlan,@IsActive,@MultiStepTemplateId,@CreatedAt,@UpdatedAt,@Notes); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, a);
        return (int)id;
    }

    public void UpdateAgent(TinyGenerator.Models.Agent a)
    {
        if (a == null) return;
        using var conn = CreateConnection();
        conn.Open();
        a.UpdatedAt = DateTime.UtcNow.ToString("o");
        var sql = @"UPDATE agents SET voice_rowid=@VoiceId, name=@Name, role=@Role, model_id=@ModelId, skills=@Skills, config=@Config, json_response_format=@JsonResponseFormat, prompt=@Prompt, instructions=@Instructions, temperature=@Temperature, top_p=@TopP, execution_plan=@ExecutionPlan, is_active=@IsActive, multi_step_template_id=@MultiStepTemplateId, updated_at=@UpdatedAt, notes=@Notes WHERE id = @Id";
        conn.Execute(sql, a);
    }

    public void DeleteAgent(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
        conn.Execute("DELETE FROM agents WHERE id = @id", new { id });
    }

    public void UpdateModelTestResults(string modelName, int functionCallingScore, IReadOnlyDictionary<string, bool?> skillFlags, double? testDurationSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return;
        using var conn = CreateConnection();
        conn.Open();

        // Update only core model metadata in the models table. Skill* and Last* columns have been removed.
        var setList = new List<string> { "FunctionCallingScore = @FunctionCallingScore", "UpdatedAt = @UpdatedAt" };
        var parameters = new DynamicParameters();
        parameters.Add("FunctionCallingScore", functionCallingScore);
        parameters.Add("UpdatedAt", DateTime.UtcNow.ToString("o"));
        if (testDurationSeconds.HasValue)
        {
            setList.Add("TestDurationSeconds = @TestDurationSeconds");
            parameters.Add("TestDurationSeconds", testDurationSeconds.Value);
        }
        // Allow callers to optionally mark a model as not supporting tools
        // (backwards-compatible: method overload accepts parameter noTools if provided via DynamicParameters)
        if (skillFlags != null && skillFlags.TryGetValue("__NoToolsMarker", out var nt) && nt.HasValue)
        {
            setList.Add("NoTools = @NoTools");
            parameters.Add("NoTools", nt.Value ? 1 : 0);
        }
        parameters.Add("Name", modelName);
        var sql = $"UPDATE models SET {string.Join(", ", setList)} WHERE Name = @Name";
        conn.Execute(sql, parameters);
    }

    /// <summary>
    /// Return list of available test groups.
    /// </summary>
    public List<string> GetTestGroups()
    {
        using var conn = CreateConnection();
        conn.Open();
        // Prefer test_prompts table if present (newer schema), otherwise fall back to test_definitions
        try
        {
            using var check = conn.CreateCommand();
            check.CommandText = "PRAGMA table_info('test_prompts');";
            using var rdr = check.ExecuteReader();
            if (rdr.Read())
            {
                var sql = "SELECT DISTINCT group_name FROM test_prompts WHERE active = 1 ORDER BY group_name";
                return conn.Query<string>(sql).ToList();
            }
        }
        catch { }

        try
        {
            var sql = "SELECT DISTINCT test_group FROM test_definitions WHERE active = 1 ORDER BY test_group";
            return conn.Query<string>(sql).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Retrieve test definitions for a given group name ordered by priority and id.
    /// </summary>
    public List<TestDefinition> GetTestsByGroup(string groupName)
    {
        using var conn = CreateConnection();
        conn.Open();

        var sql = @"SELECT id AS Id, test_group AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_secs AS TimeoutSecs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue, allowed_plugins AS AllowedPlugins, execution_plan AS ExecutionPlan, json_response_format AS JsonResponseFormat, files_to_copy AS FilesToCopy, temperature AS Temperature, top_p AS TopP
            FROM test_definitions WHERE test_group = @g AND active = 1 ORDER BY priority, id";
        return conn.Query<TestDefinition>(sql, new { g = groupName }).ToList();
    }

    /// <summary>
    /// Retrieve prompts for a given group from the newer `test_prompts` table when available,
    /// otherwise fall back to `test_definitions`.
    /// </summary>
    public List<TestDefinition> GetPromptsByGroup(string groupName)
    {
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            using var check = conn.CreateCommand();
            check.CommandText = "PRAGMA table_info('test_prompts');";
            using var rdr = check.ExecuteReader();
            if (rdr.Read())
            {
                var sql = @"SELECT id AS Id, group_name AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_secs AS TimeoutSecs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue, allowed_plugins AS AllowedPlugins, json_response_format AS JsonResponseFormat, active AS Active
FROM test_prompts WHERE group_name = @g AND active = 1 ORDER BY priority, id";
                return conn.Query<TestDefinition>(sql, new { g = groupName }).ToList();
            }
        }
        catch { }

        // fallback to legacy table
        return GetTestsByGroup(groupName);
    }

    /// <summary>
    /// List all test definitions with optional search and sort. Returns active tests only.
    /// </summary>
    public List<TestDefinition> ListAllTestDefinitions(string? search = null, string? sortBy = null, bool ascending = true)
    {
        using var conn = CreateConnection();
        conn.Open();

        var where = new List<string> { "active = 1" };
        var parameters = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(test_group LIKE @q OR library LIKE @q OR function_name LIKE @q OR prompt LIKE @q)");
            parameters.Add("q", "%" + search + "%");
        }

        var order = "id ASC";
        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            // Whitelist allowed sort columns
            var col = sortBy.ToLowerInvariant();
            if (col == "group" || col == "groupname") col = "test_group";
            else if (col == "library") col = "library";
            else if (col == "function" || col == "functionname") col = "function_name";
            else if (col == "priority") col = "priority";
            else col = "id";
            order = col + (ascending ? " ASC" : " DESC");
        }

        var sql = $@"SELECT id AS Id, test_group AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_secs AS TimeoutSecs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue, allowed_plugins AS AllowedPlugins, execution_plan AS ExecutionPlan, json_response_format AS JsonResponseFormat, files_to_copy AS FilesToCopy, temperature AS Temperature, top_p AS TopP, active AS Active
    FROM test_definitions WHERE {string.Join(" AND ", where)} ORDER BY {order}";

        return conn.Query<TestDefinition>(sql, parameters).ToList();
    }

    public TestDefinition? GetTestDefinitionById(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, test_group AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_secs AS TimeoutSecs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue, allowed_plugins AS AllowedPlugins, execution_plan AS ExecutionPlan, json_response_format AS JsonResponseFormat, files_to_copy AS FilesToCopy, temperature AS Temperature, top_p AS TopP, active AS Active
    FROM test_definitions WHERE id = @id LIMIT 1";
        return conn.QueryFirstOrDefault<TestDefinition>(sql, new { id });
    }

    public int InsertTestDefinition(TestDefinition td)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"INSERT INTO test_definitions(test_group, library, function_name, expected_behavior, expected_asset, prompt, timeout_secs, priority, valid_score_range, test_type, expected_prompt_value, allowed_plugins, execution_plan, json_response_format, files_to_copy, temperature, top_p, active)
    VALUES(@GroupName,@Library,@FunctionName,@ExpectedBehavior,@ExpectedAsset,@Prompt,@TimeoutSecs,@Priority,@ValidScoreRange,@TestType,@ExpectedPromptValue,@AllowedPlugins,@ExecutionPlan,@JsonResponseFormat,@FilesToCopy,@Temperature,@TopP,@Active); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, td);
        return (int)id;
    }

    public void UpdateTestDefinition(TestDefinition td)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"UPDATE test_definitions SET test_group=@GroupName, library=@Library, function_name=@FunctionName, expected_behavior=@ExpectedBehavior, expected_asset=@ExpectedAsset, prompt=@Prompt, timeout_secs=@TimeoutSecs, priority=@Priority, valid_score_range=@ValidScoreRange, test_type=@TestType, expected_prompt_value=@ExpectedPromptValue, allowed_plugins=@AllowedPlugins, execution_plan=@ExecutionPlan, json_response_format=@JsonResponseFormat, files_to_copy=@FilesToCopy, temperature=@Temperature, top_p=@TopP, active=@Active WHERE id = @Id";
        conn.Execute(sql, td);
    }

    public void DeleteTestDefinition(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
        // Soft delete: mark active = 0
        conn.Execute("UPDATE test_definitions SET active = 0 WHERE id = @id", new { id });
    }

    /// <summary>
    /// Return counts for a given run id: passed count and total steps.
    /// </summary>
    public (int passed, int total) GetRunStepCounts(int runId)
    {
        using var conn = CreateConnection();
        conn.Open();
        var total = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM model_test_steps WHERE run_id = @r", new { r = runId });
        var passed = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM model_test_steps WHERE run_id = @r AND passed = 1", new { r = runId });
        return (passed, total);
    }

    /// <summary>
    /// Return the latest run score (0-10) for a given model name and group (test_code).
    /// Returns null if no run exists for that model+group.
    /// </summary>
    public int? GetLatestGroupScore(string modelName, string groupName)
    {
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            var modelId = conn.ExecuteScalar<int?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
            if (!modelId.HasValue) return null;
            var runId = conn.ExecuteScalar<int?>("SELECT id FROM model_test_runs WHERE model_id = @mid AND test_group = @g ORDER BY id DESC LIMIT 1", new { mid = modelId.Value, g = groupName });
            if (!runId.HasValue) return null;
            var counts = GetRunStepCounts(runId.Value);
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
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            var modelId = conn.ExecuteScalar<int?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
            if (!modelId.HasValue) return null;
            var runId = conn.ExecuteScalar<int?>("SELECT id FROM model_test_runs WHERE model_id = @mid AND test_group = @g ORDER BY id DESC LIMIT 1", new { mid = modelId.Value, g = groupName });
            if (!runId.HasValue) return null;

            var rows = conn.Query(@"SELECT step_number AS StepNumber, step_name AS StepName, passed AS Passed, input_json AS InputJson, output_json AS OutputJson, error AS Error, duration_ms AS DurationMs
FROM model_test_steps WHERE run_id = @r ORDER BY step_number", new { r = runId.Value });

            var list = new List<object>();
            foreach (var r in rows)
            {
                bool passed = Convert.ToInt32(r.Passed) != 0;
                string stepName = r.StepName ?? string.Empty;
                string? inputJson = r.InputJson;
                string? outputJson = r.OutputJson;
                string? error = r.Error;
                long? dur = r.DurationMs == null ? (long?)null : Convert.ToInt64(r.DurationMs);
                object? inputElem = null;
                try { if (!string.IsNullOrWhiteSpace(inputJson)) inputElem = System.Text.Json.JsonDocument.Parse(inputJson).RootElement; } catch { inputElem = inputJson; }
                list.Add(new { name = stepName, ok = passed, message = !string.IsNullOrWhiteSpace(error) ? error : (object?)null, durationMs = dur, input = inputElem, output = !string.IsNullOrWhiteSpace(outputJson) ? System.Text.Json.JsonDocument.Parse(outputJson).RootElement : (System.Text.Json.JsonElement?)null });
            }

            // Serialize with System.Text.Json; if any element contains a JsonElement as 'output' it's fine.
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
        using var conn = CreateConnection();
        conn.Open();
        
        // Clean old test runs for this model+group BEFORE creating new one
        // Keep only the most recent run, delete older ones
        // Note: Stories are preserved via story_id in model_test_assets (not CASCADE deleted)
        var cleanupSql = @"
            DELETE FROM model_test_runs 
            WHERE model_id = (SELECT Id FROM models WHERE Name = @model_name LIMIT 1)
              AND test_group = @test_group
              AND id NOT IN (
                  SELECT id FROM model_test_runs
                  WHERE model_id = (SELECT Id FROM models WHERE Name = @model_name LIMIT 1)
                    AND test_group = @test_group
                  ORDER BY run_date DESC
                  LIMIT 1
              )";
        
        conn.Execute(cleanupSql, new { model_name = modelName, test_group = testCode });
        
        // Insert new run
        var sql = @"INSERT INTO model_test_runs(model_id, test_group, description, passed, duration_ms, notes, test_folder) VALUES((SELECT Id FROM models WHERE Name = @model_name LIMIT 1), @test_group, @description, @passed, @duration_ms, @notes, @test_folder); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, new { model_name = modelName, test_group = testCode, description, passed = passed ? 1 : 0, duration_ms = durationMs, notes, test_folder = testFolder });
        return (int)id;
    }

    /// <summary>
    /// Update an existing test run's passed flag and/or duration_ms.
    /// </summary>
    public void UpdateTestRunResult(int runId, bool? passed = null, long? durationMs = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        var set = new List<string>();
        var parameters = new DynamicParameters();
        if (passed.HasValue)
        {
            set.Add("passed = @passed");
            parameters.Add("passed", passed.Value ? 1 : 0);
        }
        if (durationMs.HasValue)
        {
            set.Add("duration_ms = @duration_ms");
            parameters.Add("duration_ms", durationMs.Value);
        }
        if (set.Count == 0) return;
        parameters.Add("id", runId);
        var sql = $"UPDATE model_test_runs SET {string.Join(", ", set)} WHERE id = @id";
        conn.Execute(sql, parameters);
    }

    public int AddTestStep(int runId, int stepNumber, string stepName, string? inputJson = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"INSERT INTO model_test_steps(run_id, step_number, step_name, input_json) VALUES(@run_id, @step_number, @step_name, @input_json); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, new { run_id = runId, step_number = stepNumber, step_name = stepName, input_json = inputJson });
        return (int)id;
    }

    public void UpdateTestStepResult(int stepId, bool passed, string? outputJson = null, string? error = null, long? durationMs = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        var set = new List<string> { "passed = @passed" };
        if (!string.IsNullOrWhiteSpace(outputJson)) set.Add("output_json = @output_json");
        if (!string.IsNullOrWhiteSpace(error)) set.Add("error = @error");
        if (durationMs.HasValue) set.Add("duration_ms = @duration_ms");
        var sql = $"UPDATE model_test_steps SET {string.Join(", ", set)} WHERE id = @id";
        conn.Execute(sql, new { id = stepId, passed = passed ? 1 : 0, output_json = outputJson, error, duration_ms = durationMs });
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
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"INSERT INTO model_test_assets(step_id, file_type, file_path, description, duration_sec, size_bytes, story_id) VALUES(@step_id, @file_type, @file_path, @description, @duration_sec, @size_bytes, @story_id); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, new { step_id = stepId, file_type = fileType, file_path = filePath, description, duration_sec = durationSec, size_bytes = sizeBytes, story_id = storyId });
        return (int)id;
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
        using var conn = CreateConnection();
        conn.Open();

        var sql = @"INSERT INTO stories_evaluations(story_id, narrative_coherence_score, narrative_coherence_defects, originality_score, originality_defects, emotional_impact_score, emotional_impact_defects, action_score, action_defects, total_score, raw_json, model_id, agent_id, ts) 
                    VALUES(@story_id, @ncs, @ncd, @org, @orgdef, @em, @emdef, @action, @actiondef, @total, @raw, @model_id, @agent_id, @ts); 
                    SELECT last_insert_rowid();";
        
        var id = conn.ExecuteScalar<long>(sql, new
        {
            story_id = storyId,
            ncs = narrativeScore,
            ncd = narrativeDefects,
            org = originalityScore,
            orgdef = originalityDefects,
            em = emotionalScore,
            emdef = emotionalDefects,
            action = actionScore,
            actiondef = actionDefects,
            total = totalScore,
            raw = rawJson,
            model_id = modelId,
            agent_id = agentId,
            ts = DateTime.UtcNow.ToString("o")
        });
        
        // Recalculate writer score for the model
        if (modelId.HasValue)
        {
            RecalculateWriterScore(modelId.Value);
        }

        UpdateStoryStatusAfterEvaluation(conn, storyId, agentId);
        
        return id;
    }

    public long AddStoryEvaluation(long storyId, string rawJson, double totalScore, int? modelId = null, int? agentId = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        // Deduplicate: avoid inserting identical evaluation (same story, same agent, same raw JSON)
        try
        {
            if (!string.IsNullOrWhiteSpace(rawJson) && agentId.HasValue)
            {
                var existingId = conn.ExecuteScalar<long?>("SELECT id FROM stories_evaluations WHERE story_id = @sid AND agent_id = @aid AND raw_json = @raw LIMIT 1", new { sid = storyId, aid = agentId.Value, raw = rawJson });
                if (existingId.HasValue && existingId.Value > 0)
                {
                    return existingId.Value;
                }
            }
        }
        catch { /* best-effort dedupe, ignore on error */ }
        // Try parse JSON to extract category fields - best effort
        // Parsing helper logic inline below

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            // try to extract fields (e.g. root.narrative_coherence.score or root.narrative_coherence)
            int GetScoreFromCategory(string cat)
            {
                try
                {
                    if (root.TryGetProperty(cat, out var catEl) && catEl.ValueKind == System.Text.Json.JsonValueKind.Object && catEl.TryGetProperty("score", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Number) return s.GetInt32();
                    // Also try root[cat + "_score"]
                    var alt = cat + "_score";
                    if (root.TryGetProperty(alt, out var altEl) && altEl.ValueKind == System.Text.Json.JsonValueKind.Number) return altEl.GetInt32();
                }
                catch { }
                return 0;
            }
            string GetDefectsFromCategory(string cat)
            {
                try
                {
                    if (root.TryGetProperty(cat, out var catEl) && catEl.ValueKind == System.Text.Json.JsonValueKind.Object && catEl.TryGetProperty("defects", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String) return d.GetString() ?? string.Empty;
                    var alt = cat + "_defects";
                    if (root.TryGetProperty(alt, out var altEl) && altEl.ValueKind == System.Text.Json.JsonValueKind.String) return altEl.GetString() ?? string.Empty;
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

            var sql = @"INSERT INTO stories_evaluations(story_id, narrative_coherence_score, narrative_coherence_defects, originality_score, originality_defects, emotional_impact_score, emotional_impact_defects, action_score, action_defects, total_score, raw_json, model_id, agent_id, ts) 
                        VALUES(@story_id, @ncs, @ncd, @org, @orgdef, @em, @emdef, @action, @actiondef, @total, @raw, @model_id, @agent_id, @ts); 
                        SELECT last_insert_rowid();";
            var id = conn.ExecuteScalar<long>(sql, new { story_id = storyId, ncs = nc, ncd = ncdef, org = org, orgdef = orgdef, em = em, emdef = emdef, action = action, actiondef = actionDef, total = totalScore, raw = rawJson, model_id = modelId ?? (int?)null, agent_id = agentId ?? (int?)null, ts = DateTime.UtcNow.ToString("o") });
            
            // Recalculate writer score for the model
        if (modelId.HasValue)
        {
            RecalculateWriterScore(modelId.Value);
        }

        UpdateStoryStatusAfterEvaluation(conn, storyId, agentId);
        
        return id;
    }
        catch (Exception)
        {
            var sql = @"INSERT INTO stories_evaluations(story_id, narrative_coherence_score, narrative_coherence_defects, originality_score, originality_defects, emotional_impact_score, emotional_impact_defects, action_score, action_defects, total_score, raw_json, model_id, agent_id, ts) 
                        VALUES(@story_id, 0, '', 0, '', 0, '', 0, '', @total, @raw, @model_id, @agent_id, @ts); 
                        SELECT last_insert_rowid();";
            var id = conn.ExecuteScalar<long>(sql, new { story_id = storyId, total = totalScore, raw = rawJson, model_id = modelId ?? (int?)null, agent_id = agentId ?? (int?)null, ts = DateTime.UtcNow.ToString("o") });
            
            // Recalculate writer score for the model
            if (modelId.HasValue)
            {
                RecalculateWriterScore(modelId.Value);
            }

            UpdateStoryStatusAfterEvaluation(conn, storyId, agentId);

            return id;
        }
    }

    public void RecalculateWriterScore(int modelId)
    {
        using var conn = CreateConnection();
        conn.Open();
        
        var sql = @"
UPDATE models
SET WriterScore = (
    SELECT CASE 
        WHEN COUNT(*) = 0 THEN 0
        ELSE (COALESCE(SUM(se.total_score), 0) * 10.0) / (COUNT(*) * 100.0)
    END
    FROM stories_evaluations se
    INNER JOIN stories s ON s.id = se.story_id
    WHERE s.model_id = models.Id
)
WHERE models.Id = @modelId;";
        
        conn.Execute(sql, new { modelId });
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
                        Ts = global.Ts ?? string.Empty
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

    public void DeleteStoryEvaluationById(long evaluationId)
    {
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            conn.Execute("DELETE FROM stories_evaluations WHERE id = @id", new { id = evaluationId });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Failed to delete story evaluation {evaluationId}: {ex.Message}");
            throw;
        }
    }

    // Stories CRUD operations
    public long SaveGeneration(string prompt, TinyGenerator.Models.StoryGenerationResult r, string? memoryKey = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        var genId = Guid.NewGuid().ToString();

        var midA = (long?)null;
        var aidA = (int?)null;
        try { aidA = GetAgentIdByName("WriterA"); } catch { }
        var charCountA = (r.StoryA ?? string.Empty).Length;
        var defaultStatusId = InitialStoryStatusId;
        var sqlA = @"INSERT INTO stories(generation_id, memory_key, ts, prompt, story, char_count, eval, score, approved, status_id, model_id, agent_id) VALUES(@gid,@mk,@ts,@p,@c,@cc,@e,@s,@ap,@stid,@mid,@aid);";
        conn.Execute(sqlA, new { gid = genId, mk = memoryKey ?? genId, ts = DateTime.UtcNow.ToString("o"), p = prompt ?? string.Empty, c = r.StoryA ?? string.Empty, cc = charCountA, e = r.EvalA ?? string.Empty, s = r.ScoreA, ap = string.IsNullOrEmpty(r.Approved) ? 0 : 1, stid = defaultStatusId, mid = midA, aid = aidA });

        var midB = (long?)null;
        var aidB = (int?)null;
        try { aidB = GetAgentIdByName("WriterB"); } catch { }
        var charCountB = (r.StoryB ?? string.Empty).Length;
        var sqlB = @"INSERT INTO stories(generation_id, memory_key, ts, prompt, story, char_count, eval, score, approved, status_id, model_id, agent_id) VALUES(@gid,@mk,@ts,@p,@c,@cc,@e,@s,@ap,@stid,@mid,@aid); SELECT last_insert_rowid();";
        var idRowB = conn.ExecuteScalar<long>(sqlB, new { gid = genId, mk = memoryKey ?? genId, ts = DateTime.UtcNow.ToString("o"), p = prompt ?? string.Empty, c = r.StoryB ?? string.Empty, cc = charCountB, e = r.EvalB ?? string.Empty, s = r.ScoreB, ap = string.IsNullOrEmpty(r.Approved) ? 0 : 1, stid = defaultStatusId, mid = midB, aid = aidB });

        var midC = (long?)null;
        var aidC = (int?)null;
        try { aidC = GetAgentIdByName("WriterC"); } catch { }
        var charCountC = (r.StoryC ?? string.Empty).Length;
        var sqlC = @"INSERT INTO stories(generation_id, memory_key, ts, prompt, story, char_count, eval, score, approved, status_id, model_id, agent_id) VALUES(@gid,@mk,@ts,@p,@c,@cc,@e,@s,@ap,@stid,@mid,@aid); SELECT last_insert_rowid();";
        var idRowC = conn.ExecuteScalar<long>(sqlC, new { gid = genId, mk = memoryKey ?? genId, ts = DateTime.UtcNow.ToString("o"), p = prompt ?? string.Empty, c = r.StoryC ?? string.Empty, cc = charCountC, e = r.EvalC ?? string.Empty, s = r.ScoreC, ap = string.IsNullOrEmpty(r.Approved) ? 0 : 1, stid = defaultStatusId, mid = midC, aid = aidC });
        var finalId = idRowC == 0 ? idRowB : idRowC;
        return finalId;
    }

    public List<TinyGenerator.Models.StoryRecord> GetAllStories()
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT s.id AS Id, s.generation_id AS GenerationId, s.memory_key AS MemoryKey, s.ts AS Timestamp, s.prompt AS Prompt, s.story AS Story, s.char_count AS CharCount, m.name AS Model, a.name AS Agent, s.eval AS Eval, s.score AS Score, s.approved AS Approved, s.status_id AS StatusId, COALESCE(ss.code, '') AS Status, ss.description AS StatusDescription, ss.color AS StatusColor, ss.step AS StatusStep, ss.operation_type AS StatusOperationType, ss.agent_type AS StatusAgentType, ss.function_name AS StatusFunctionName, s.folder AS Folder 
                    FROM stories s 
                    LEFT JOIN stories_status ss ON s.status_id = ss.id
                    LEFT JOIN models m ON s.model_id = m.id 
                    LEFT JOIN agents a ON s.agent_id = a.id 
                    ORDER BY s.id DESC";
        return conn.Query<TinyGenerator.Models.StoryRecord>(sql).ToList();
    }

    public TinyGenerator.Models.StoryRecord? GetStoryById(long id)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT s.id AS Id, s.generation_id AS GenerationId, s.memory_key AS MemoryKey, s.ts AS Timestamp, s.prompt AS Prompt, s.story AS Story, s.char_count AS CharCount, m.name AS Model, a.name AS Agent, s.eval AS Eval, s.score AS Score, s.approved AS Approved, s.status_id AS StatusId, COALESCE(ss.code, '') AS Status, ss.description AS StatusDescription, ss.color AS StatusColor, ss.step AS StatusStep, ss.operation_type AS StatusOperationType, ss.agent_type AS StatusAgentType, ss.function_name AS StatusFunctionName, s.folder AS Folder FROM stories s LEFT JOIN stories_status ss ON s.status_id = ss.id LEFT JOIN models m ON s.model_id = m.id LEFT JOIN agents a ON s.agent_id = a.id WHERE s.id = @id LIMIT 1";
        var row = conn.QueryFirstOrDefault<TinyGenerator.Models.StoryRecord>(sql, new { id = id });
        if (row == null) return null;
        if (row.Approved) row.Approved = true; // Ensure boolean conversion
        return row;
    }

    public void DeleteStoryById(long id)
    {
        using var conn = CreateConnection();
        conn.Open();
        var genId = conn.QueryFirstOrDefault<string>("SELECT generation_id FROM stories WHERE id = @id LIMIT 1", new { id });
        if (!string.IsNullOrEmpty(genId)) conn.Execute("DELETE FROM stories WHERE generation_id = @gid", new { gid = genId });
    }

    public (int? runId, int? stepId) GetTestInfoForStory(long storyId)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT mts.run_id AS RunId, mta.step_id AS StepId 
                    FROM model_test_assets mta 
                    JOIN model_test_steps mts ON mta.step_id = mts.id 
                    WHERE mta.story_id = @sid 
                    LIMIT 1";
        var result = conn.QueryFirstOrDefault<(int RunId, int StepId)?>(sql, new { sid = storyId });
        if (result.HasValue)
            return ((int?)result.Value.RunId, (int?)result.Value.StepId);
        return (null, null);
    }

    public long InsertSingleStory(string prompt, string story, int? modelId = null, int? agentId = null, double score = 0.0, string? eval = null, int approved = 0, int? statusId = null, string? memoryKey = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        var ts = DateTime.UtcNow.ToString("o");
        var genId = Guid.NewGuid().ToString();
        
        // Generate folder name: <agent_name>_<yyyyMMdd_HHmmss> or <agent_id>_<yyyyMMdd_HHmmss> if name not available
        string? folder = null;
        if (agentId.HasValue)
        {
            var agentName = conn.ExecuteScalar<string>("SELECT name FROM agents WHERE id = @aid LIMIT 1", new { aid = agentId.Value });
            var sanitizedAgentName = SanitizeFolderName(agentName ?? $"agent{agentId.Value}");
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            folder = $"{sanitizedAgentName}_{timestamp}";
        }
        
        var charCount = (story ?? string.Empty).Length;
        var sql = @"INSERT INTO stories(generation_id, memory_key, ts, prompt, story, char_count, eval, score, approved, status_id, folder, model_id, agent_id) VALUES(@gid,@mk,@ts,@p,@c,@cc,@e,@s,@ap,@stid,@folder,@mid,@aid); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, new { gid = genId, mk = memoryKey ?? genId, ts = ts, p = prompt ?? string.Empty, c = story ?? string.Empty, cc = charCount, mid = modelId, aid = agentId, e = eval ?? string.Empty, s = score, ap = approved, stid = statusId ?? InitialStoryStatusId, folder = folder });
        return id;
    }

    private int? ResolveStoryStatusId(IDbConnection conn, string? statusCode)
    {
        if (conn == null || string.IsNullOrWhiteSpace(statusCode)) return null;
        try
        {
            return conn.ExecuteScalar<int?>("SELECT id FROM stories_status WHERE code = @code LIMIT 1", new { code = statusCode });
        }
        catch
        {
            return null;
        }
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

    public bool UpdateStoryById(long id, string? story = null, int? modelId = null, int? agentId = null, int? statusId = null, bool updateStatus = false)
    {
        using var conn = CreateConnection();
        conn.Open();
        var updates = new List<string>();
        var parms = new Dictionary<string, object?>();
        if (story != null) 
        { 
            updates.Add("story = @story"); 
            parms["story"] = story; 
            updates.Add("char_count = @char_count"); 
            parms["char_count"] = story.Length; 
        }
        if (modelId.HasValue) { updates.Add("model_id = @model_id"); parms["model_id"] = modelId.Value; }
        if (agentId.HasValue) { updates.Add("agent_id = @agent_id"); parms["agent_id"] = agentId.Value; }
        if (updateStatus)
        {
            updates.Add("status_id = @status_id");
            parms["status_id"] = statusId;
        }
        if (updates.Count == 0) return false;
        parms["id"] = id;
        var sql = $"UPDATE stories SET {string.Join(", ", updates)} WHERE id = @id";
        var rows = conn.Execute(sql, parms);
        return rows > 0;
    }

    // TTS voices: list and upsert
    public List<TinyGenerator.Models.TtsVoice> ListTtsVoices()
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = "SELECT id AS Id, voice_id AS VoiceId, name AS Name, model AS Model, language AS Language, gender AS Gender, age AS Age, confidence AS Confidence, score AS Score, tags AS Tags, template_wav AS TemplateWav, archetype AS Archetype, notes AS Notes, created_at AS CreatedAt, updated_at AS UpdatedAt FROM tts_voices ORDER BY name";
        var voices = conn.Query<TinyGenerator.Models.TtsVoice>(sql).ToList();
        EnsureVoiceDerivedFields(voices);
        return voices;
    }

    public int GetTtsVoiceCount()
    {
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            var c = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tts_voices");
            return (int)c;
        }
        catch
        {
            return 0;
        }
    }

    public TinyGenerator.Models.TtsVoice? GetTtsVoiceByVoiceId(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId)) return null;
        using var conn = CreateConnection();
        conn.Open();
        var sql = "SELECT id AS Id, voice_id AS VoiceId, name AS Name, model AS Model, language AS Language, gender AS Gender, age AS Age, confidence AS Confidence, score AS Score, tags AS Tags, template_wav AS TemplateWav, archetype AS Archetype, notes AS Notes, created_at AS CreatedAt, updated_at AS UpdatedAt FROM tts_voices WHERE voice_id = @vid LIMIT 1";
        var voice = conn.QueryFirstOrDefault<TinyGenerator.Models.TtsVoice>(sql, new { vid = voiceId });
        if (voice != null) EnsureVoiceDerivedFields(new List<TinyGenerator.Models.TtsVoice> { voice });
        return voice;
    }

    public TinyGenerator.Models.TtsVoice? GetTtsVoiceById(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = "SELECT id AS Id, voice_id AS VoiceId, name AS Name, model AS Model, language AS Language, gender AS Gender, age AS Age, confidence AS Confidence, score AS Score, tags AS Tags, template_wav AS TemplateWav, archetype AS Archetype, notes AS Notes, created_at AS CreatedAt, updated_at AS UpdatedAt FROM tts_voices WHERE id = @id LIMIT 1";
        var voice = conn.QueryFirstOrDefault<TinyGenerator.Models.TtsVoice>(sql, new { id });
        if (voice != null) EnsureVoiceDerivedFields(new List<TinyGenerator.Models.TtsVoice> { voice });
        return voice;
    }

    public TinyGenerator.Models.TtsVoice? GetTtsVoiceByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        using var conn = CreateConnection();
        conn.Open();
        var sql = "SELECT id AS Id, voice_id AS VoiceId, name AS Name, model AS Model, language AS Language, gender AS Gender, age AS Age, confidence AS Confidence, score AS Score, tags AS Tags, template_wav AS TemplateWav, archetype AS Archetype, notes AS Notes, created_at AS CreatedAt, updated_at AS UpdatedAt FROM tts_voices WHERE LOWER(name) = LOWER(@name) LIMIT 1";
        var voice = conn.QueryFirstOrDefault<TinyGenerator.Models.TtsVoice>(sql, new { name });
        if (voice != null) EnsureVoiceDerivedFields(new List<TinyGenerator.Models.TtsVoice> { voice });
        return voice;
    }

    public void UpdateTtsVoiceTemplateWavById(int id, string templateWav)
    {
        if (string.IsNullOrWhiteSpace(templateWav)) return;
        using var conn = CreateConnection();
        conn.Open();
        conn.Execute("UPDATE tts_voices SET template_wav = @p, updated_at = @u WHERE id = @id", new { p = templateWav, u = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"), id });
    }

    public void UpdateTtsVoiceScoreById(int id, double? score)
    {
        using var conn = CreateConnection();
        conn.Open();
        conn.Execute("UPDATE tts_voices SET score = @s, updated_at = @u WHERE id = @id", new { s = score, u = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"), id });
    }

    public void DeleteTtsVoiceById(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
        conn.Execute("DELETE FROM tts_voices WHERE id = @id", new { id });
    }

    public void UpdateTtsVoice(TinyGenerator.Models.TtsVoice v)
    {
        if (v == null || v.Id <= 0) return;
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"UPDATE tts_voices SET name=@Name, model=@Model, language=@Language, gender=@Gender, age=@Age, confidence=@Confidence, score=@Score, tags=@Tags, template_wav=@TemplateWav, archetype=@Archetype, notes=@Notes, updated_at=@UpdatedAt WHERE id = @Id";
        v.UpdatedAt = DateTime.UtcNow.ToString("o");
        conn.Execute(sql, v);
    }

    public void UpsertTtsVoice(TinyGenerator.Services.VoiceInfo v, string? model = null)
    {
        if (v == null || string.IsNullOrWhiteSpace(v.Id)) return;
        using var conn = CreateConnection();
        if (conn is not SqliteConnection sqliteConn)
        {
            throw new InvalidOperationException("Database connection is not SqliteConnection");
        }
        sqliteConn.Open();
        UpsertTtsVoice(sqliteConn, null, v, model);
    }

    private static void UpsertTtsVoice(SqliteConnection conn, SqliteTransaction? tx, TinyGenerator.Services.VoiceInfo v, string? model)
    {
        if (conn == null) throw new ArgumentNullException(nameof(conn));
        if (v == null || string.IsNullOrWhiteSpace(v.Id)) return;
        var now = DateTime.UtcNow.ToString("o");
        // metadata not stored anymore; template_wav is used for sample filename
        string? tagsJson;
        try { tagsJson = v.Tags != null ? JsonSerializer.Serialize(v.Tags) : null; } catch { tagsJson = null; }
        var (archetype, notes) = ExtractArchetypeNotesFromVoiceInfo(v);
        var sql = @"INSERT INTO tts_voices(voice_id, name, model, language, gender, age, confidence, score, tags, template_wav, archetype, notes, created_at, updated_at)
    VALUES(@VoiceId,@Name,@Model,@Language,@Gender,@Age,@Confidence,@Score,@Tags,@TemplateWav,@Archetype,@Notes,@CreatedAt,@UpdatedAt)
    ON CONFLICT(voice_id) DO UPDATE SET name=@Name, model=@Model, language=@Language, gender=@Gender, age=@Age, confidence=@Confidence, score=@Score, tags=@Tags, template_wav=@TemplateWav, archetype=@Archetype, notes=@Notes, updated_at=@UpdatedAt;";

        conn.Execute(sql, new
        {
            VoiceId = v.Id,
            Name = string.IsNullOrWhiteSpace(v.Name) ? v.Id : v.Name,
            Model = model,
            Language = v.Language,
            Gender = v.Gender,
            Age = v.Age,
            Confidence = v.Confidence,
            Tags = tagsJson,
            TemplateWav = v.Tags != null && v.Tags.ContainsKey("template_wav") ? v.Tags["template_wav"] : null,
            Score = GetScoreFromTags(v.Tags, v.Confidence),
            Archetype = archetype,
            Notes = notes,
            CreatedAt = now,
            UpdatedAt = now
        }, transaction: tx);
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

            using var conn = CreateConnection();
            if (conn is not SqliteConnection sqliteConn)
            {
                throw new InvalidOperationException("Database connection is not SqliteConnection");
            }

            sqliteConn.Open();
            using var tx = sqliteConn.BeginTransaction();
            foreach (var v in list)
            {
                if (v == null || string.IsNullOrWhiteSpace(v.Id)) continue;
                try
                {
                    var existing = sqliteConn.ExecuteScalar<string?>(
                        "SELECT voice_id FROM tts_voices WHERE voice_id = @vid LIMIT 1",
                        new { vid = v.Id },
                        transaction: tx);
                    UpsertTtsVoice(sqliteConn, tx, v, null);
                    if (string.IsNullOrWhiteSpace(existing))
                    {
                        result.AddedIds.Add(v.Id);
                    }
                    else
                    {
                        result.UpdatedIds.Add(v.Id);
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Voice {v?.Id}: {ex.Message}");
                }
            }
            try
            {
                tx.Commit();
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Commit failed: {ex.Message}");
                tx.Rollback();
            }
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
        using var conn = CreateConnection();
        conn.Open();
        foreach (var voice in toUpdate)
        {
            conn.Execute(
                "UPDATE tts_voices SET archetype = @Archetype, notes = @Notes WHERE id = @Id",
                new { voice.Archetype, voice.Notes, voice.Id });
        }
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: failed to add temperature/top_p to agents: {ex.Message}");
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
    }

    // Async batch insert for log entries. Will insert all provided entries in a single INSERT statement when possible.
    public async Task InsertLogsAsync(IEnumerable<TinyGenerator.Models.LogEntry> entries)
    {
        var list = entries?.ToList() ?? new List<TinyGenerator.Models.LogEntry>();
        if (list.Count == 0) return;

        using var conn = CreateConnection();
        await ((SqliteConnection)conn).OpenAsync();

        // Build a single INSERT ... VALUES (...),(...),... with uniquely named parameters to avoid collisions
        var cols = new[] { "Ts", "Level", "Category", "Message", "Exception", "State", "ThreadId", "ThreadScope", "AgentName", "Context", "analized", "chat_text", "Result" };
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
            parameters.Add("@ThreadScope" + i, e.ThreadScope);
            parameters.Add("@AgentName" + i, e.AgentName);
            parameters.Add("@Context" + i, e.Context);
            parameters.Add("@analized" + i, e.Analized ? 1 : 0);
            parameters.Add("@chat_text" + i, e.ChatText);
            parameters.Add("@Result" + i, e.Result);
        }

        sb.Append(";");

        await conn.ExecuteAsync(sb.ToString(), parameters);
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
        using var conn = CreateConnection();
        conn.Open();

        var where = new List<string>();
        var parameters = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(level)) { where.Add("Level = @Level"); parameters.Add("Level", level); }
        if (!string.IsNullOrWhiteSpace(category)) { where.Add("Category LIKE @Category"); parameters.Add("Category", "%" + category + "%"); }

        var sql = "SELECT Ts, Level, Category, Message, Exception, State, ThreadId, ThreadScope, AgentName, Context, analized AS Analized, chat_text AS ChatText, Result FROM Log";
        if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
        sql += " ORDER BY Id DESC LIMIT @Limit OFFSET @Offset";
        parameters.Add("Limit", limit);
        parameters.Add("Offset", offset);

        return conn.Query<TinyGenerator.Models.LogEntry>(sql, parameters).ToList();
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

        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT Ts, Level, Category, Message, Exception, State, ThreadId, ThreadScope, AgentName, Context, analized AS Analized, Result
                    FROM Log
                    WHERE ThreadId = @ThreadId
                    ORDER BY Id ASC
                    LIMIT @Limit";
        return conn.Query<TinyGenerator.Models.LogEntry>(sql, new { ThreadId = threadNumericId, Limit = limit }).ToList();
    }

    public void SetLogAnalyzed(string threadId, bool analyzed)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return;
        if (!int.TryParse(threadId, out var threadNumericId)) return;

        using var conn = CreateConnection();
        conn.Open();
        conn.Execute("UPDATE Log SET analized = @val WHERE ThreadId = @tid", new { val = analyzed ? 1 : 0, tid = threadNumericId });
    }

    public void DeleteLogAnalysesByThread(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return;
        using var conn = CreateConnection();
        conn.Open();
        conn.Execute("DELETE FROM Log_analysis WHERE threadId = @tid", new { tid = threadId });
    }

    public void InsertLogAnalysis(TinyGenerator.Models.LogAnalysis analysis)
    {
        if (analysis == null) return;
        using var conn = CreateConnection();
        conn.Open();
        conn.Execute(
            "INSERT INTO Log_analysis (threadId, model_id, run_scope, description, succeeded) VALUES (@threadId, @modelId, @scope, @description, @succeeded)",
            new
            {
                threadId = analysis.ThreadId ?? string.Empty,
                modelId = analysis.ModelId ?? string.Empty,
                scope = analysis.RunScope ?? string.Empty,
                description = analysis.Description ?? string.Empty,
                succeeded = analysis.Succeeded ? 1 : 0
            });
    }

    public List<TinyGenerator.Models.LogAnalysis> GetLogAnalyses(int limit = 200)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, threadId AS ThreadId, model_id AS ModelId, run_scope AS RunScope, description AS Description, succeeded AS Succeeded
                    FROM Log_analysis
                    ORDER BY id DESC
                    LIMIT @Limit";
        return conn.Query<TinyGenerator.Models.LogAnalysis>(sql, new { Limit = limit }).ToList();
    }

    public List<TinyGenerator.Models.LogAnalysis> GetLogAnalysesByThread(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
            return new List<TinyGenerator.Models.LogAnalysis>();

        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, threadId AS ThreadId, model_id AS ModelId, run_scope AS RunScope, description AS Description, succeeded AS Succeeded
                    FROM Log_analysis
                    WHERE threadId = @tid
                    ORDER BY id DESC";
        return conn.Query<TinyGenerator.Models.LogAnalysis>(sql, new { tid = threadId }).ToList();
    }

    public int GetLogCount(string? level = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        var where = new List<string>();
        var parameters = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(level)) { where.Add("Level = @Level"); parameters.Add("Level", level); }
        var sql = "SELECT COUNT(*) FROM Log";
        if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
        var cnt = conn.ExecuteScalar<long>(sql, parameters);
        return (int)cnt;
    }

    public void ClearLogs()
    {
        using var conn = CreateConnection();
        conn.Open();
        // Delete related log_analysis entries first (foreign key constraint)
        conn.Execute("DELETE FROM log_analysis");
        // Then delete all logs
        conn.Execute("DELETE FROM Log");
    }

    public List<LogEntry> GetLogsByThreadId(int threadId)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"
            SELECT id, ts, level, operation, thread_id AS ThreadId, thread_scope AS ThreadScope, 
                   agent_name AS AgentName, message, source, state, context, exception, analized
            FROM log
            WHERE thread_id = @threadId
            ORDER BY ts DESC";
        return conn.Query<LogEntry>(sql, new { threadId }).AsList();
    }

    /// <summary>
    /// Deletes log entries older than a specified number of days if total log count exceeds threshold.
    /// </summary>
    public void CleanupOldLogs(int daysOld = 7, int countThreshold = 1000)
    {
        try
        {
            using var conn = CreateConnection();
            conn.Open();

            // Check current log count
            var count = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Log");
            if (count <= countThreshold) return;

            // Calculate cutoff date (older than daysOld)
            var cutoffDate = DateTime.UtcNow.AddDays(-daysOld);
            
            // Delete logs older than cutoff date
            var deleted = conn.Execute(
                "DELETE FROM Log WHERE Ts < @CutoffDate",
                new { CutoffDate = cutoffDate }
            );
            
            if (deleted > 0)
            {
                var newCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Log");
                System.Diagnostics.Debug.WriteLine($"[DB] Log cleanup: Deleted {deleted} log entries older than {daysOld} days. New count: {newCount}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DB] Log cleanup failed: {ex.Message}");
            // Best-effort: don't throw on cleanup failure
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
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, code AS Code, description AS Description, step AS Step, color AS Color, operation_type AS OperationType, agent_type AS AgentType, function_name AS FunctionName, caption_to_execute AS CaptionToExecute FROM stories_status ORDER BY step, code";
        return conn.Query<StoryStatus>(sql).ToList();
    }

    public StoryStatus? GetStoryStatusById(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, code AS Code, description AS Description, step AS Step, color AS Color, operation_type AS OperationType, agent_type AS AgentType, function_name AS FunctionName, caption_to_execute AS CaptionToExecute FROM stories_status WHERE id = @id LIMIT 1";
        return conn.QueryFirstOrDefault<StoryStatus>(sql, new { id });
    }

    public StoryStatus? GetStoryStatusByCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, code AS Code, description AS Description, step AS Step, color AS Color, operation_type AS OperationType, agent_type AS AgentType, function_name AS FunctionName, caption_to_execute AS CaptionToExecute FROM stories_status WHERE code = @code LIMIT 1";
        return conn.QueryFirstOrDefault<StoryStatus>(sql, new { code });
    }

    public void UpdateStoryFolder(long storyId, string folder)
    {
        if (storyId <= 0 || string.IsNullOrWhiteSpace(folder)) return;
        using var conn = CreateConnection();
        conn.Open();
        conn.Execute("UPDATE stories SET folder = @folder WHERE id = @id", new { folder, id = storyId });
    }

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
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"INSERT INTO stories_status(code, description, step, color, operation_type, agent_type, function_name, caption_to_execute) VALUES(@Code, @Description, @Step, @Color, @OperationType, @AgentType, @FunctionName, @CaptionToExecute); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, status);
        return (int)id;
    }

    public void UpdateStoryStatus(StoryStatus status)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"UPDATE stories_status SET code=@Code, description=@Description, step=@Step, color=@Color, operation_type=@OperationType, agent_type=@AgentType, function_name=@FunctionName, caption_to_execute=@CaptionToExecute WHERE id = @Id";
        conn.Execute(sql, status);
    }

    public void DeleteStoryStatus(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
        conn.Execute("DELETE FROM stories_status WHERE id = @id", new { id });
    }

    // ========== Multi-Step Task Execution Methods ==========

    public long CreateTaskExecution(TinyGenerator.Models.TaskExecution execution)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"
INSERT INTO task_executions (task_type, entity_id, step_prompt, initial_context, current_step, max_step, retry_count, status, executor_agent_id, checker_agent_id, config, created_at, updated_at)
VALUES (@TaskType, @EntityId, @StepPrompt, @InitialContext, @CurrentStep, @MaxStep, @RetryCount, @Status, @ExecutorAgentId, @CheckerAgentId, @Config, @CreatedAt, @UpdatedAt);
SELECT last_insert_rowid();";
        return conn.ExecuteScalar<long>(sql, execution);
    }

    public TinyGenerator.Models.TaskExecution? GetTaskExecutionById(long id)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, task_type AS TaskType, entity_id AS EntityId, step_prompt AS StepPrompt, initial_context AS InitialContext, current_step AS CurrentStep, max_step AS MaxStep, retry_count AS RetryCount, status AS Status, executor_agent_id AS ExecutorAgentId, checker_agent_id AS CheckerAgentId, config AS Config, created_at AS CreatedAt, updated_at AS UpdatedAt FROM task_executions WHERE id = @id LIMIT 1";
        return conn.QueryFirstOrDefault<TinyGenerator.Models.TaskExecution>(sql, new { id });
    }

    public void UpdateTaskExecution(TinyGenerator.Models.TaskExecution execution)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"
UPDATE task_executions 
SET task_type=@TaskType, entity_id=@EntityId, step_prompt=@StepPrompt, initial_context=@InitialContext, current_step=@CurrentStep, max_step=@MaxStep, 
    retry_count=@RetryCount, status=@Status, executor_agent_id=@ExecutorAgentId, checker_agent_id=@CheckerAgentId, 
    config=@Config, updated_at=@UpdatedAt
WHERE id=@Id";
        conn.Execute(sql, execution);
    }

    public TinyGenerator.Models.TaskExecution? GetActiveExecutionForEntity(long? entityId, string taskType)
    {
        if (!entityId.HasValue) return null;
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, task_type AS TaskType, entity_id AS EntityId, step_prompt AS StepPrompt, initial_context AS InitialContext, current_step AS CurrentStep, max_step AS MaxStep, retry_count AS RetryCount, status AS Status, executor_agent_id AS ExecutorAgentId, checker_agent_id AS CheckerAgentId, config AS Config, created_at AS CreatedAt, updated_at AS UpdatedAt FROM task_executions WHERE entity_id = @eid AND task_type = @tt AND status IN ('pending','in_progress') LIMIT 1";
        return conn.QueryFirstOrDefault<TinyGenerator.Models.TaskExecution>(sql, new { eid = entityId.Value, tt = taskType });
    }

    public long CreateTaskExecutionStep(TinyGenerator.Models.TaskExecutionStep step)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"
INSERT INTO task_execution_steps (execution_id, step_number, step_instruction, step_output, validation_result, attempt_count, started_at, completed_at)
VALUES (@ExecutionId, @StepNumber, @StepInstruction, @StepOutput, @ValidationResultJson, @AttemptCount, @StartedAt, @CompletedAt);
SELECT last_insert_rowid();";
        return conn.ExecuteScalar<long>(sql, step);
    }

    public List<TinyGenerator.Models.TaskExecutionStep> GetTaskExecutionSteps(long executionId)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, execution_id AS ExecutionId, step_number AS StepNumber, step_instruction AS StepInstruction, step_output AS StepOutput, validation_result AS ValidationResultJson, attempt_count AS AttemptCount, started_at AS StartedAt, completed_at AS CompletedAt FROM task_execution_steps WHERE execution_id = @eid ORDER BY step_number";
        return conn.Query<TinyGenerator.Models.TaskExecutionStep>(sql, new { eid = executionId }).ToList();
    }

    public TinyGenerator.Models.TaskTypeInfo? GetTaskTypeByCode(string code)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, code AS Code, description AS Description, default_executor_role AS DefaultExecutorRole, default_checker_role AS DefaultCheckerRole, output_merge_strategy AS OutputMergeStrategy, validation_criteria AS ValidationCriteria FROM task_types WHERE code = @code LIMIT 1";
        return conn.QueryFirstOrDefault<TinyGenerator.Models.TaskTypeInfo>(sql, new { code });
    }

    public TinyGenerator.Models.StepTemplate? GetStepTemplateById(long id)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, name AS Name, task_type AS TaskType, step_prompt AS StepPrompt, description AS Description, created_at AS CreatedAt, updated_at AS UpdatedAt FROM step_templates WHERE id = @id LIMIT 1";
        return conn.QueryFirstOrDefault<TinyGenerator.Models.StepTemplate>(sql, new { id });
    }

    public TinyGenerator.Models.StepTemplate? GetStepTemplateByName(string name)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, name AS Name, task_type AS TaskType, step_prompt AS StepPrompt, description AS Description, created_at AS CreatedAt, updated_at AS UpdatedAt FROM step_templates WHERE name = @name LIMIT 1";
        return conn.QueryFirstOrDefault<TinyGenerator.Models.StepTemplate>(sql, new { name });
    }

    public List<TinyGenerator.Models.StepTemplate> ListStepTemplates(string? taskType = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        var baseSql = @"SELECT id AS Id, name AS Name, task_type AS TaskType, step_prompt AS StepPrompt, description AS Description, created_at AS CreatedAt, updated_at AS UpdatedAt FROM step_templates";
        var sql = taskType == null
            ? baseSql + " ORDER BY name"
            : baseSql + " WHERE task_type = @tt ORDER BY name";
        return conn.Query<TinyGenerator.Models.StepTemplate>(sql, new { tt = taskType }).ToList();
    }

    public void UpsertStepTemplate(TinyGenerator.Models.StepTemplate template)
    {
        using var conn = CreateConnection();
        conn.Open();
        var existing = conn.ExecuteScalar<long?>("SELECT id FROM step_templates WHERE name = @name", new { template.Name });
        
        if (existing.HasValue)
        {
            // Update
            var sql = @"UPDATE step_templates SET task_type=@TaskType, step_prompt=@StepPrompt, description=@Description, updated_at=@UpdatedAt WHERE id=@Id";
            conn.Execute(sql, new { template.TaskType, template.StepPrompt, template.Description, UpdatedAt = DateTime.UtcNow.ToString("o"), Id = existing.Value });
        }
        else
        {
            // Insert
            var sql = @"INSERT INTO step_templates (name, task_type, step_prompt, description, created_at, updated_at) VALUES (@Name, @TaskType, @StepPrompt, @Description, @CreatedAt, @UpdatedAt)";
            conn.Execute(sql, template);
        }
    }

    public void UpdateStepTemplate(TinyGenerator.Models.StepTemplate template)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"UPDATE step_templates SET name=@Name, task_type=@TaskType, step_prompt=@StepPrompt, description=@Description, updated_at=@UpdatedAt WHERE id=@Id";
        conn.Execute(sql, template);
    }

    public void DeleteStepTemplate(long id)
    {
        using var conn = CreateConnection();
        conn.Open();
        conn.Execute("DELETE FROM step_templates WHERE id = @id", new { id });
    }

    public void CleanupOldTaskExecutions()
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = "DELETE FROM task_executions WHERE status IN ('completed','failed') AND datetime(updated_at) < datetime('now','-7 days')";
        var deleted = conn.Execute(sql);
        if (deleted > 0)
        {
            Console.WriteLine($"[DB] Cleaned up {deleted} old task executions");
        }
    }

    #region Coherence Evaluation Methods

    /// <summary>
    /// Salva i fatti estratti da un chunk di storia
    /// </summary>
    public void SaveChunkFacts(ChunkFacts facts)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"INSERT INTO story_chunk_facts (story_id, chunk_number, facts_json, ts) 
                    VALUES (@StoryId, @ChunkNumber, @FactsJson, @Ts)";
        conn.Execute(sql, facts);
    }

    /// <summary>
    /// Recupera i fatti di un chunk specifico
    /// </summary>
    public ChunkFacts? GetChunkFacts(int storyId, int chunkNumber)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, story_id AS StoryId, chunk_number AS ChunkNumber, 
                    facts_json AS FactsJson, ts AS Ts 
                    FROM story_chunk_facts 
                    WHERE story_id = @sid AND chunk_number = @cn";
        return conn.QueryFirstOrDefault<ChunkFacts>(sql, new { sid = storyId, cn = chunkNumber });
    }

    /// <summary>
    /// Recupera tutti i fatti di una storia
    /// </summary>
    public List<ChunkFacts> GetAllChunkFacts(int storyId)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, story_id AS StoryId, chunk_number AS ChunkNumber, 
                    facts_json AS FactsJson, ts AS Ts 
                    FROM story_chunk_facts 
                    WHERE story_id = @sid 
                    ORDER BY chunk_number";
        return conn.Query<ChunkFacts>(sql, new { sid = storyId }).ToList();
    }

    /// <summary>
    /// Salva lo score di coerenza di un chunk
    /// </summary>
    public void SaveCoherenceScore(CoherenceScore score)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"INSERT INTO story_coherence_scores 
                    (story_id, chunk_number, local_coherence, global_coherence, errors, ts) 
                    VALUES (@StoryId, @ChunkNumber, @LocalCoherence, @GlobalCoherence, @Errors, @Ts)";
        conn.Execute(sql, score);
    }

    /// <summary>
    /// Recupera tutti gli score di coerenza di una storia
    /// </summary>
    public List<CoherenceScore> GetCoherenceScores(int storyId)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, story_id AS StoryId, chunk_number AS ChunkNumber, 
                    local_coherence AS LocalCoherence, global_coherence AS GlobalCoherence, 
                    errors AS Errors, ts AS Ts 
                    FROM story_coherence_scores 
                    WHERE story_id = @sid 
                    ORDER BY chunk_number";
        return conn.Query<CoherenceScore>(sql, new { sid = storyId }).ToList();
    }

    /// <summary>
    /// Salva la coerenza globale finale di una storia
    /// </summary>
    public void SaveGlobalCoherence(GlobalCoherence coherence)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"INSERT OR REPLACE INTO story_global_coherence 
                    (story_id, global_coherence, chunk_count, notes, ts) 
                    VALUES (@StoryId, @GlobalCoherenceValue, @ChunkCount, @Notes, @Ts)";
        conn.Execute(sql, coherence);
    }

    /// <summary>
    /// Recupera la coerenza globale di una storia
    /// </summary>
    public GlobalCoherence? GetGlobalCoherence(int storyId)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, story_id AS StoryId, global_coherence AS GlobalCoherenceValue, 
                    chunk_count AS ChunkCount, notes AS Notes, ts AS Ts 
                    FROM story_global_coherence 
                    WHERE story_id = @sid";
        return conn.QueryFirstOrDefault<GlobalCoherence>(sql, new { sid = storyId });
    }

    #endregion
}
