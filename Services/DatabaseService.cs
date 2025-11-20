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


    private readonly string _connectionString;

    public DatabaseService(string dbPath = "data/storage.db")
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
            InitializeSchema();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Initialize() error: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
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
            var modelId = conn.ExecuteScalar<long?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
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

        var sql = @"INSERT INTO models(Name, Provider, Endpoint, IsLocal, MaxContext, ContextToUse, FunctionCallingScore, CostInPerToken, CostOutPerToken, LimitTokensDay, LimitTokensWeek, LimitTokensMonth, Metadata, Enabled, CreatedAt, UpdatedAt, NoTools)
VALUES(@Name,@Provider,@Endpoint,@IsLocal,@MaxContext,@ContextToUse,@FunctionCallingScore,@CostInPerToken,@CostOutPerToken,@LimitTokensDay,@LimitTokensWeek,@LimitTokensMonth,@Metadata,@Enabled,@CreatedAt,@UpdatedAt,@NoTools)
ON CONFLICT(Name) DO UPDATE SET Provider=@Provider, Endpoint=@Endpoint, IsLocal=@IsLocal, MaxContext=@MaxContext, ContextToUse=@ContextToUse, FunctionCallingScore=@FunctionCallingScore, CostInPerToken=@CostInPerToken, CostOutPerToken=@CostOutPerToken, LimitTokensDay=@LimitTokensDay, LimitTokensWeek=@LimitTokensWeek, LimitTokensMonth=@LimitTokensMonth, Metadata=@Metadata, Enabled=@Enabled, UpdatedAt=@UpdatedAt, NoTools=@NoTools;";

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
        var sql = @"SELECT id, ts, model, provider, input_tokens, output_tokens, tokens, cost, request, response FROM calls ORDER BY id DESC LIMIT @Limit";
        var results = conn.Query<dynamic>(sql, new { Limit = limit });
        return results.Select(r => new CallRecord
        {
            Id = (long)r.id,
            Timestamp = r.ts ?? string.Empty,
            Model = r.model ?? string.Empty,
            Provider = r.provider ?? string.Empty,
            InputTokens = (int)(r.input_tokens ?? 0),
            OutputTokens = (int)(r.output_tokens ?? 0),
            Tokens = (int)(r.tokens ?? 0),
            Cost = (double)(r.cost ?? 0.0),
            Request = r.request ?? string.Empty,
            Response = r.response ?? string.Empty
        }).ToList();
    }

    // Agents CRUD
    public List<TinyGenerator.Models.Agent> ListAgents()
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT a.id AS Id, a.voice_rowid AS VoiceId, t.name AS VoiceName, a.name AS Name, a.role AS Role, a.model_id AS ModelId, a.skills AS Skills, a.config AS Config, a.json_response_format AS JsonResponseFormat, a.prompt AS Prompt, a.instructions AS Instructions, a.execution_plan AS ExecutionPlan, a.is_active AS IsActive, a.created_at AS CreatedAt, a.updated_at AS UpdatedAt, a.notes AS Notes FROM agents a LEFT JOIN tts_voices t ON a.voice_rowid = t.id ORDER BY a.name";
        return conn.Query<TinyGenerator.Models.Agent>(sql).ToList();
    }

    public TinyGenerator.Models.Agent? GetAgentById(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT a.id AS Id, a.voice_rowid AS VoiceId, t.name AS VoiceName, a.name AS Name, a.role AS Role, a.model_id AS ModelId, a.skills AS Skills, a.config AS Config, a.json_response_format AS JsonResponseFormat, a.prompt AS Prompt, a.instructions AS Instructions, a.execution_plan AS ExecutionPlan, a.is_active AS IsActive, a.created_at AS CreatedAt, a.updated_at AS UpdatedAt, a.notes AS Notes FROM agents a LEFT JOIN tts_voices t ON a.voice_rowid = t.id WHERE a.id = @id LIMIT 1";
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

    public int InsertAgent(TinyGenerator.Models.Agent a)
    {
        using var conn = CreateConnection();
        conn.Open();
        var now = DateTime.UtcNow.ToString("o");
        a.CreatedAt ??= now;
        a.UpdatedAt = now;
        var sql = @"INSERT INTO agents(voice_rowid, name, role, model_id, skills, config, json_response_format, prompt, instructions, execution_plan, is_active, created_at, updated_at, notes) VALUES(@VoiceId,@Name,@Role,@ModelId,@Skills,@Config,@JsonResponseFormat,@Prompt,@Instructions,@ExecutionPlan,@IsActive,@CreatedAt,@UpdatedAt,@Notes); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, a);
        return (int)id;
    }

    public void UpdateAgent(TinyGenerator.Models.Agent a)
    {
        if (a == null) return;
        using var conn = CreateConnection();
        conn.Open();
        a.UpdatedAt = DateTime.UtcNow.ToString("o");
        var sql = @"UPDATE agents SET voice_rowid=@VoiceId, name=@Name, role=@Role, model_id=@ModelId, skills=@Skills, config=@Config, json_response_format=@JsonResponseFormat, prompt=@Prompt, instructions=@Instructions, execution_plan=@ExecutionPlan, is_active=@IsActive, updated_at=@UpdatedAt, notes=@Notes WHERE id = @Id";
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

        var sql = @"SELECT id AS Id, test_group AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_ms AS TimeoutMs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue, allowed_plugins AS AllowedPlugins, execution_plan AS ExecutionPlan, json_response_format AS JsonResponseFormat, files_to_copy AS FilesToCopy
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
                var sql = @"SELECT id AS Id, group_name AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_ms AS TimeoutMs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue, allowed_plugins AS AllowedPlugins, json_response_format AS JsonResponseFormat, active AS Active
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

        var sql = $@"SELECT id AS Id, test_group AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_ms AS TimeoutMs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue, allowed_plugins AS AllowedPlugins, json_response_format AS JsonResponseFormat, files_to_copy AS FilesToCopy, active AS Active
FROM test_definitions WHERE {string.Join(" AND ", where)} ORDER BY {order}";

        return conn.Query<TestDefinition>(sql, parameters).ToList();
    }

    public TestDefinition? GetTestDefinitionById(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, test_group AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_ms AS TimeoutMs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue, allowed_plugins AS AllowedPlugins, execution_plan AS ExecutionPlan, json_response_format AS JsonResponseFormat, files_to_copy AS FilesToCopy, active AS Active
FROM test_definitions WHERE id = @id LIMIT 1";
        return conn.QueryFirstOrDefault<TestDefinition>(sql, new { id });
    }

    public int InsertTestDefinition(TestDefinition td)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"INSERT INTO test_definitions(test_group, library, function_name, expected_behavior, expected_asset, prompt, timeout_ms, priority, valid_score_range, test_type, expected_prompt_value, allowed_plugins, execution_plan, json_response_format, files_to_copy, active)
VALUES(@GroupName,@Library,@FunctionName,@ExpectedBehavior,@ExpectedAsset,@Prompt,@TimeoutMs,@Priority,@ValidScoreRange,@TestType,@ExpectedPromptValue,@AllowedPlugins,@ExecutionPlan,@JsonResponseFormat,@FilesToCopy,@Active); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, td);
        return (int)id;
    }

    public void UpdateTestDefinition(TestDefinition td)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"UPDATE test_definitions SET test_group=@GroupName, library=@Library, function_name=@FunctionName, expected_behavior=@ExpectedBehavior, expected_asset=@ExpectedAsset, prompt=@Prompt, timeout_ms=@TimeoutMs, priority=@Priority, valid_score_range=@ValidScoreRange, test_type=@TestType, expected_prompt_value=@ExpectedPromptValue, allowed_plugins=@AllowedPlugins, execution_plan=@ExecutionPlan, json_response_format=@JsonResponseFormat, files_to_copy=@FilesToCopy, active=@Active WHERE id = @Id";
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
            var modelId = conn.ExecuteScalar<long?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
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
            var modelId = conn.ExecuteScalar<long?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
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
    /// </summary>
    public int CreateTestRun(string modelName, string testCode, string? description = null, bool passed = false, long? durationMs = null, string? notes = null, string? testFolder = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        // Insert run: resolve model_id from models.Name and store only model_id (model_name column removed)
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
            var list = await OllamaMonitorService.GetInstalledModelsAsync();
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
        int structureScore, string structureDefects,
        int characterizationScore, string characterizationDefects,
        int dialoguesScore, string dialoguesDefects,
        int pacingScore, string pacingDefects,
        int originalityScore, string originalityDefects,
        int styleScore, string styleDefects,
        int worldbuildingScore, string worldbuildingDefects,
        int thematicScore, string thematicDefects,
        int emotionalScore, string emotionalDefects,
        double totalScore,
        string overallEvaluation,
        string rawJson,
        long? modelId = null,
        int? agentId = null)
    {
        using var conn = CreateConnection();
        conn.Open();

        var sql = @"INSERT INTO stories_evaluations(story_id, narrative_coherence_score, narrative_coherence_defects, structure_score, structure_defects, characterization_score, characterization_defects, dialogues_score, dialogues_defects, pacing_score, pacing_defects, originality_score, originality_defects, style_score, style_defects, worldbuilding_score, worldbuilding_defects, thematic_coherence_score, thematic_coherence_defects, emotional_impact_score, emotional_impact_defects, total_score, overall_evaluation, raw_json, model_id, agent_id, ts) 
                    VALUES(@story_id, @ncs, @ncd, @ss, @sd, @chs, @chd, @dlg, @dlgdef, @pc, @pcdef, @org, @orgdef, @stl, @stldef, @wb, @wbdef, @th, @thdef, @em, @emdef, @total, @overall, @raw, @model_id, @agent_id, @ts); 
                    SELECT last_insert_rowid();";
        
        var id = conn.ExecuteScalar<long>(sql, new
        {
            story_id = storyId,
            ncs = narrativeScore,
            ncd = narrativeDefects,
            ss = structureScore,
            sd = structureDefects,
            chs = characterizationScore,
            chd = characterizationDefects,
            dlg = dialoguesScore,
            dlgdef = dialoguesDefects,
            pc = pacingScore,
            pcdef = pacingDefects,
            org = originalityScore,
            orgdef = originalityDefects,
            stl = styleScore,
            stldef = styleDefects,
            wb = worldbuildingScore,
            wbdef = worldbuildingDefects,
            th = thematicScore,
            thdef = thematicDefects,
            em = emotionalScore,
            emdef = emotionalDefects,
            total = totalScore,
            overall = overallEvaluation,
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
        
        return id;
    }

    public long AddStoryEvaluation(long storyId, string rawJson, double totalScore, int? modelId = null, int? agentId = null)
    {
        using var conn = CreateConnection();
        conn.Open();
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
            var st = GetScoreFromCategory("structure");
            var stdef = GetDefectsFromCategory("structure");
            var ch = GetScoreFromCategory("characterization");
            var chdef = GetDefectsFromCategory("characterization");
            var dlg = GetScoreFromCategory("dialogues");
            var dlgdef = GetDefectsFromCategory("dialogues");
            var pc = GetScoreFromCategory("pacing");
            var pcdef = GetDefectsFromCategory("pacing");
            var org = GetScoreFromCategory("originality");
            var orgdef = GetDefectsFromCategory("originality");
            var stl = GetScoreFromCategory("style");
            var stldef = GetDefectsFromCategory("style");
            var wb = GetScoreFromCategory("worldbuilding");
            var wbdef = GetDefectsFromCategory("worldbuilding");
            var th = GetScoreFromCategory("thematic_coherence");
            var thdef = GetDefectsFromCategory("thematic_coherence");
            var em = GetScoreFromCategory("emotional_impact");
            var emdef = GetDefectsFromCategory("emotional_impact");
            string overall = string.Empty;
            try { if (root.TryGetProperty("overall_evaluation", out var ov) && ov.ValueKind == System.Text.Json.JsonValueKind.String) overall = ov.GetString() ?? string.Empty; } catch { }

            var sql = @"INSERT INTO stories_evaluations(story_id, narrative_coherence_score, narrative_coherence_defects, structure_score, structure_defects, characterization_score, characterization_defects, dialogues_score, dialogues_defects, pacing_score, pacing_defects, originality_score, originality_defects, style_score, style_defects, worldbuilding_score, worldbuilding_defects, thematic_coherence_score, thematic_coherence_defects, emotional_impact_score, emotional_impact_defects, total_score, overall_evaluation, raw_json, model_id, agent_id, ts) VALUES(@story_id, @ncs, @ncd, @ss, @sd, @chs, @chd, @dlg, @dlgdef, @pc, @pcdef, @org, @orgdef, @stl, @stldef, @wb, @wbdef, @th, @thdef, @em, @emdef, @total, @overall, @raw, @model_id, @agent_id, @ts); SELECT last_insert_rowid();";
            var id = conn.ExecuteScalar<long>(sql, new { story_id = storyId, ncs = nc, ncd = ncdef, ss = st, sd = stdef, chs = ch, chd = chdef, dlg = dlg, dlgdef = dlgdef, pc = pc, pcdef = pcdef, org = org, orgdef = orgdef, stl = stl, stldef = stldef, wb = wb, wbdef = wbdef, th = th, thdef = thdef, em = em, emdef = emdef, total = totalScore, overall = overall, raw = rawJson, model_id = modelId ?? (long?)null, agent_id = agentId ?? (int?)null, ts = DateTime.UtcNow.ToString("o") });
            
            // Recalculate writer score for the model
            if (modelId.HasValue)
            {
                RecalculateWriterScore(modelId.Value);
            }
            
            return id;
        }
        catch (Exception)
        {
            var sql = @"INSERT INTO stories_evaluations(story_id, total_score, raw_json, model_id, agent_id, ts) VALUES(@story_id, @total, @raw, @model_id, @agent_id, @ts); SELECT last_insert_rowid();";
            var id = conn.ExecuteScalar<long>(sql, new { story_id = storyId, total = totalScore, raw = rawJson, model_id = modelId ?? (long?)null, agent_id = agentId ?? (int?)null, ts = DateTime.UtcNow.ToString("o") });
            
            // Recalculate writer score for the model
            if (modelId.HasValue)
            {
                RecalculateWriterScore(modelId.Value);
            }
            
            return id;
        }
    }

    public void RecalculateWriterScore(long modelId)
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

    public void RecalculateAllWriterScores()
    {
        using var conn = CreateConnection();
        conn.Open();
        
        // First, reset all WriterScore to 0
        conn.Execute("UPDATE models SET WriterScore = 0;");
        
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
);";
        
        conn.Execute(sql);
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
        var sql = @"SELECT id AS Id, story_id AS StoryId, narrative_coherence_score AS NarrativeCoherenceScore, narrative_coherence_defects AS NarrativeCoherenceDefects, structure_score AS StructureScore, structure_defects AS StructureDefects, characterization_score AS CharacterizationScore, characterization_defects AS CharacterizationDefects, dialogues_score AS DialoguesScore, dialogues_defects AS DialoguesDefects, pacing_score AS PacingScore, pacing_defects AS PacingDefects, originality_score AS OriginalityScore, originality_defects AS OriginalityDefects, style_score AS StyleScore, style_defects AS StyleDefects, worldbuilding_score AS WorldbuildingScore, worldbuilding_defects AS WorldbuildingDefects, thematic_coherence_score AS ThematicCoherenceScore, thematic_coherence_defects AS ThematicCoherenceDefects, emotional_impact_score AS EmotionalImpactScore, emotional_impact_defects AS EmotionalImpactDefects, total_score AS TotalScore, overall_evaluation AS OverallEvaluation, raw_json AS RawJson, model_id AS ModelId, agent_id AS AgentId, ts AS Ts FROM stories_evaluations WHERE story_id = @sid ORDER BY id";
        // Also join models and agents for human-friendly names and a 'Score' alias used by UI
        sql = @"SELECT se.id AS Id, se.story_id AS StoryId, se.narrative_coherence_score AS NarrativeCoherenceScore, se.narrative_coherence_defects AS NarrativeCoherenceDefects, se.structure_score AS StructureScore, se.structure_defects AS StructureDefects, se.characterization_score AS CharacterizationScore, se.characterization_defects AS CharacterizationDefects, se.dialogues_score AS DialoguesScore, se.dialogues_defects AS DialoguesDefects, se.pacing_score AS PacingScore, se.pacing_defects AS PacingDefects, se.originality_score AS OriginalityScore, se.originality_defects AS OriginalityDefects, se.style_score AS StyleScore, se.style_defects AS StyleDefects, se.worldbuilding_score AS WorldbuildingScore, se.worldbuilding_defects AS WorldbuildingDefects, se.thematic_coherence_score AS ThematicCoherenceScore, se.thematic_coherence_defects AS ThematicCoherenceDefects, se.emotional_impact_score AS EmotionalImpactScore, se.emotional_impact_defects AS EmotionalImpactDefects, se.total_score AS TotalScore, se.overall_evaluation AS OverallEvaluation, se.raw_json AS RawJson, se.model_id AS ModelId, se.agent_id AS AgentId, se.ts AS Ts, COALESCE(m.Name, '') AS Model, se.total_score AS Score FROM stories_evaluations se LEFT JOIN models m ON se.model_id = m.Id WHERE se.story_id = @sid ORDER BY se.id";
        return conn.Query<TinyGenerator.Models.StoryEvaluation>(sql, new { sid = storyId }).ToList();
    }

    // Stories CRUD operations
    public long SaveGeneration(string prompt, TinyGenerator.Services.StoryGeneratorService.GenerationResult r, string? memoryKey = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        var genId = Guid.NewGuid().ToString();

        var midA = (long?)null;
        var aidA = (int?)null;
        try { aidA = GetAgentIdByName("WriterA"); } catch { }
        var charCountA = (r.StoryA ?? string.Empty).Length;
        var sqlA = @"INSERT INTO stories(generation_id, memory_key, ts, prompt, story, char_count, eval, score, approved, status, model_id, agent_id) VALUES(@gid,@mk,@ts,@p,@c,@cc,@e,@s,@ap,@st,@mid,@aid);";
        conn.Execute(sqlA, new { gid = genId, mk = memoryKey ?? genId, ts = DateTime.UtcNow.ToString("o"), p = prompt ?? string.Empty, c = r.StoryA ?? string.Empty, cc = charCountA, e = r.EvalA ?? string.Empty, s = r.ScoreA, ap = string.IsNullOrEmpty(r.Approved) ? 0 : 1, st = string.IsNullOrEmpty(r.Approved) ? "rejected" : "approved", mid = midA, aid = aidA });

        var midB = (long?)null;
        var aidB = (int?)null;
        try { aidB = GetAgentIdByName("WriterB"); } catch { }
        var charCountB = (r.StoryB ?? string.Empty).Length;
        var sqlB = @"INSERT INTO stories(generation_id, memory_key, ts, prompt, story, char_count, eval, score, approved, status, model_id, agent_id) VALUES(@gid,@mk,@ts,@p,@c,@cc,@e,@s,@ap,@st,@mid,@aid); SELECT last_insert_rowid();";
        var idRowB = conn.ExecuteScalar<long>(sqlB, new { gid = genId, mk = memoryKey ?? genId, ts = DateTime.UtcNow.ToString("o"), p = prompt ?? string.Empty, c = r.StoryB ?? string.Empty, cc = charCountB, e = r.EvalB ?? string.Empty, s = r.ScoreB, ap = string.IsNullOrEmpty(r.Approved) ? 0 : 1, st = string.IsNullOrEmpty(r.Approved) ? "rejected" : "approved", mid = midB, aid = aidB });

        var midC = (long?)null;
        var aidC = (int?)null;
        try { aidC = GetAgentIdByName("WriterC"); } catch { }
        var charCountC = (r.StoryC ?? string.Empty).Length;
        var sqlC = @"INSERT INTO stories(generation_id, memory_key, ts, prompt, story, char_count, eval, score, approved, status, model_id, agent_id) VALUES(@gid,@mk,@ts,@p,@c,@cc,@e,@s,@ap,@st,@mid,@aid); SELECT last_insert_rowid();";
        var idRowC = conn.ExecuteScalar<long>(sqlC, new { gid = genId, mk = memoryKey ?? genId, ts = DateTime.UtcNow.ToString("o"), p = prompt ?? string.Empty, c = r.StoryC ?? string.Empty, cc = charCountC, e = r.EvalC ?? string.Empty, s = r.ScoreC, ap = string.IsNullOrEmpty(r.Approved) ? 0 : 1, st = string.IsNullOrEmpty(r.Approved) ? "rejected" : "approved", mid = midC, aid = aidC });
        var finalId = idRowC == 0 ? idRowB : idRowC;
        return finalId;
    }

    public List<TinyGenerator.Models.StoryRecord> GetAllStories()
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT s.id AS Id, s.generation_id AS GenerationId, s.memory_key AS MemoryKey, s.ts AS Timestamp, s.prompt AS Prompt, s.story AS Story, s.char_count AS CharCount, m.name AS Model, a.name AS Agent, s.eval AS Eval, s.score AS Score, s.approved AS Approved, s.status AS Status, s.folder AS Folder 
                    FROM stories s 
                    LEFT JOIN models m ON s.model_id = m.id 
                    LEFT JOIN agents a ON s.agent_id = a.id 
                    ORDER BY s.id DESC";
        return conn.Query<TinyGenerator.Models.StoryRecord>(sql).ToList();
    }

    public TinyGenerator.Models.StoryRecord? GetStoryById(long id)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT s.id AS Id, s.generation_id AS GenerationId, s.memory_key AS MemoryKey, s.ts AS Timestamp, s.prompt AS Prompt, s.story AS Story, s.char_count AS CharCount, m.name AS Model, a.name AS Agent, s.eval AS Eval, s.score AS Score, s.approved AS Approved, s.status AS Status, s.folder AS Folder FROM stories s LEFT JOIN models m ON s.model_id = m.id LEFT JOIN agents a ON s.agent_id = a.id WHERE s.id = @id LIMIT 1";
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

    public long InsertSingleStory(string prompt, string story, int? modelId = null, int? agentId = null, double score = 0.0, string? eval = null, int approved = 0, string? status = null, string? memoryKey = null)
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
        var sql = @"INSERT INTO stories(generation_id, memory_key, ts, prompt, story, char_count, eval, score, approved, status, folder, model_id, agent_id) VALUES(@gid,@mk,@ts,@p,@c,@cc,@e,@s,@ap,@st,@folder,@mid,@aid); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, new { gid = genId, mk = memoryKey ?? genId, ts = ts, p = prompt ?? string.Empty, c = story ?? string.Empty, cc = charCount, mid = modelId, aid = agentId, e = eval ?? string.Empty, s = score, ap = approved, st = status ?? string.Empty, folder = folder });
        return id;
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

    public bool UpdateStoryById(long id, string? story = null, int? modelId = null, int? agentId = null, string? status = null)
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
        if (status != null) { updates.Add("status = @status"); parms["status"] = status; }
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
        var sql = "SELECT id AS Id, voice_id AS VoiceId, name AS Name, model AS Model, language AS Language, gender AS Gender, age AS Age, confidence AS Confidence, tags AS Tags, sample_path AS SamplePath, template_wav AS TemplateWav, metadata AS Metadata, created_at AS CreatedAt, updated_at AS UpdatedAt FROM tts_voices ORDER BY name";
        return conn.Query<TinyGenerator.Models.TtsVoice>(sql).ToList();
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
        var sql = "SELECT id AS Id, voice_id AS VoiceId, name AS Name, model AS Model, language AS Language, gender AS Gender, age AS Age, confidence AS Confidence, tags AS Tags, sample_path AS SamplePath, template_wav AS TemplateWav, metadata AS Metadata, created_at AS CreatedAt, updated_at AS UpdatedAt FROM tts_voices WHERE voice_id = @vid LIMIT 1";
        return conn.QueryFirstOrDefault<TinyGenerator.Models.TtsVoice>(sql, new { vid = voiceId });
    }

    public void UpsertTtsVoice(TinyGenerator.Services.VoiceInfo v, string? model = null)
    {
        if (v == null || string.IsNullOrWhiteSpace(v.Id)) return;
        using var conn = CreateConnection();
        conn.Open();
        var now = DateTime.UtcNow.ToString("o");
        var metadata = JsonSerializer.Serialize(v);
        string? tagsJson = null;
        try { tagsJson = v.Tags != null ? JsonSerializer.Serialize(v.Tags) : null; } catch { tagsJson = null; }
        var sql = @"INSERT INTO tts_voices(voice_id, name, model, language, gender, age, confidence, tags, sample_path, template_wav, metadata, created_at, updated_at)
VALUES(@VoiceId,@Name,@Model,@Language,@Gender,@Age,@Confidence,@Tags,@SamplePath,@TemplateWav,@Metadata,@CreatedAt,@UpdatedAt)
ON CONFLICT(voice_id) DO UPDATE SET name=@Name, model=@Model, language=@Language, gender=@Gender, age=@Age, confidence=@Confidence, tags=@Tags, sample_path=@SamplePath, template_wav=@TemplateWav, metadata=@Metadata, updated_at=@UpdatedAt;";

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
            SamplePath = v.Tags != null && v.Tags.ContainsKey("sample") ? v.Tags["sample"] : null,
            TemplateWav = v.Tags != null && v.Tags.ContainsKey("template_wav") ? v.Tags["template_wav"] : null,
            Metadata = metadata,
            CreatedAt = now,
            UpdatedAt = now
        });
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

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"CREATE TABLE IF NOT EXISTS usage_state (
    month TEXT PRIMARY KEY,
    tokens_this_run INTEGER DEFAULT 0,
    tokens_this_month INTEGER DEFAULT 0,
    cost_this_month REAL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS calls (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts TEXT,
    model TEXT,
    provider TEXT,
    input_tokens INTEGER DEFAULT 0,
    output_tokens INTEGER DEFAULT 0,
    tokens INTEGER,
    cost REAL,
    request TEXT,
    response TEXT
);

CREATE TABLE IF NOT EXISTS models (
    Name TEXT PRIMARY KEY,
    Provider TEXT,
    Endpoint TEXT,
    IsLocal INTEGER DEFAULT 1,
    MaxContext INTEGER DEFAULT 4096,
    ContextToUse INTEGER DEFAULT 4096,
    FunctionCallingScore INTEGER DEFAULT 0,
    WriterScore REAL DEFAULT 0,
    CostInPerToken REAL DEFAULT 0,
    CostOutPerToken REAL DEFAULT 0,
    LimitTokensDay INTEGER DEFAULT 0,
    LimitTokensWeek INTEGER DEFAULT 0,
    LimitTokensMonth INTEGER DEFAULT 0,
    Metadata TEXT,
    Enabled INTEGER DEFAULT 1,
    CreatedAt TEXT,
    UpdatedAt TEXT,
    TestDurationSeconds REAL
);";
        cmd.ExecuteNonQuery();
        Console.WriteLine("[DB] Created usage_state/calls/models tables");

        // TTS Voices table (before agents, as agents has FK to tts_voices)
        using var voicesCmd = conn.CreateCommand();
        voicesCmd.CommandText = @"CREATE TABLE IF NOT EXISTS tts_voices (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    voice_id TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    model TEXT,
    language TEXT,
    gender TEXT,
    age TEXT,
    confidence REAL DEFAULT 0,
    tags TEXT,
    sample_path TEXT,
    template_wav TEXT,
    metadata TEXT,
    created_at TEXT,
    updated_at TEXT
);";
        voicesCmd.ExecuteNonQuery();
        Console.WriteLine("[DB] Created tts_voices table (if not exists)");

        // Test definitions table (independent, no FK dependencies)
        using var testDefsCmd = conn.CreateCommand();
        testDefsCmd.CommandText = @"CREATE TABLE IF NOT EXISTS test_definitions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    test_group TEXT NOT NULL,
    test_type TEXT NOT NULL,
    function_name TEXT,
    prompt TEXT NOT NULL,
    expected_prompt_value TEXT,
    valid_score_range TEXT,
    expected_behavior TEXT,
    json_response_format TEXT,
    allowed_plugins TEXT,
    execution_plan TEXT,
    timeout_ms INTEGER DEFAULT 30000,
    enabled INTEGER DEFAULT 1,
    files_to_copy TEXT,
    created_at TEXT,
    updated_at TEXT
);";
        testDefsCmd.ExecuteNonQuery();
        Console.WriteLine("[DB] Created test_definitions table (if not exists)");

        // Migration: Add files_to_copy column if not exists
        var hasFilesToCopy = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('test_definitions') WHERE name='files_to_copy'");
        if (hasFilesToCopy == 0)
        {
            Console.WriteLine("[DB] Adding files_to_copy column to test_definitions");
            conn.Execute("ALTER TABLE test_definitions ADD COLUMN files_to_copy TEXT");
        }

        // Migration: Rename group_name to test_group in test_definitions if needed
        var hasGroupName = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('test_definitions') WHERE name='group_name'");
        var hasTestGroupInDefs = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('test_definitions') WHERE name='test_group'");
        
        if (hasGroupName > 0 && hasTestGroupInDefs == 0)
        {
            Console.WriteLine("[DB] Migrating test_definitions: renaming group_name to test_group");
            conn.Execute("ALTER TABLE test_definitions RENAME COLUMN group_name TO test_group");
        }

        // Stories table (after models, before stories_evaluations)
        using var storiesCmd = conn.CreateCommand();
        storiesCmd.CommandText = @"CREATE TABLE IF NOT EXISTS stories (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    generation_id TEXT,
    memory_key TEXT,
    ts TEXT,
    prompt TEXT,
    story TEXT,
    char_count INTEGER DEFAULT 0,
    eval TEXT,
    score REAL DEFAULT 0,
    approved INTEGER DEFAULT 0,
    status TEXT,
    folder TEXT NULL,
    model_id INTEGER NULL,
    agent_id INTEGER NULL,
    FOREIGN KEY (model_id) REFERENCES models(Id),
    FOREIGN KEY (agent_id) REFERENCES agents(id)
);";
        storiesCmd.ExecuteNonQuery();
        Console.WriteLine("[DB] Created stories table (if not exists)");

        // Agents table for reusable agent configurations
        using var agentsCmd = conn.CreateCommand();
        agentsCmd.CommandText = @"CREATE TABLE IF NOT EXISTS agents (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    voice_rowid INTEGER NULL,
    name TEXT NOT NULL,
    role TEXT NOT NULL,
    model_id INTEGER NULL,
    skills TEXT NULL,
    config TEXT NULL,
    json_response_format TEXT NULL,
    prompt TEXT NULL,
    instructions TEXT NULL,
    execution_plan TEXT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL,
    updated_at TEXT NULL,
    notes TEXT NULL,
    FOREIGN KEY (voice_rowid) REFERENCES tts_voices(id)
);
";
        agentsCmd.ExecuteNonQuery();
        Console.WriteLine("[DB] Created agents table (if not exists)");

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

        // Ensure Log table exists
        using var logCmd = conn.CreateCommand();
        logCmd.CommandText = @"CREATE TABLE IF NOT EXISTS Log (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Ts TEXT,
    Level TEXT,
    Category TEXT,
    Message TEXT,
    Exception TEXT,
    State TEXT,
    ThreadId INTEGER DEFAULT 0,
    AgentName TEXT,
    Context TEXT
);
";
        logCmd.ExecuteNonQuery();
        Console.WriteLine("[DB] Created Log table");

        // Ensure stories_evaluations table exists (newly added feature to persist story evaluations)
        using var storiesEvalCmd = conn.CreateCommand();
        storiesEvalCmd.CommandText = @"
    CREATE TABLE IF NOT EXISTS stories_evaluations (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        story_id INTEGER NOT NULL,
        narrative_coherence_score INTEGER DEFAULT 0,
        narrative_coherence_defects TEXT,
        structure_score INTEGER DEFAULT 0,
        structure_defects TEXT,
        characterization_score INTEGER DEFAULT 0,
        characterization_defects TEXT,
        dialogues_score INTEGER DEFAULT 0,
        dialogues_defects TEXT,
        pacing_score INTEGER DEFAULT 0,
        pacing_defects TEXT,
        originality_score INTEGER DEFAULT 0,
        originality_defects TEXT,
        style_score INTEGER DEFAULT 0,
        style_defects TEXT,
        worldbuilding_score INTEGER DEFAULT 0,
        worldbuilding_defects TEXT,
        thematic_coherence_score INTEGER DEFAULT 0,
        thematic_coherence_defects TEXT,
        emotional_impact_score INTEGER DEFAULT 0,
        emotional_impact_defects TEXT,
        total_score REAL DEFAULT 0,
        overall_evaluation TEXT,
        raw_json TEXT,
        model_id INTEGER NULL,
        agent_id INTEGER NULL,
        ts TEXT,
        FOREIGN KEY (story_id) REFERENCES stories(id) ON DELETE CASCADE,
        FOREIGN KEY (model_id) REFERENCES models(Id) ON DELETE SET NULL,
        FOREIGN KEY (agent_id) REFERENCES agents(id) ON DELETE SET NULL
    );";
        storiesEvalCmd.ExecuteNonQuery();
        Console.WriteLine("[DB] Created stories_evaluations table (if not exists)");

        // Ensure model_test_* tables exist (basic schema). These are used by the test runner.
        using var runsCmd = conn.CreateCommand();
        runsCmd.CommandText = @"
    CREATE TABLE IF NOT EXISTS model_test_runs (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        model_id INTEGER NULL,
        test_group TEXT,
        description TEXT,
        passed INTEGER DEFAULT 0,
        duration_ms INTEGER NULL,
        notes TEXT,
        run_date TEXT,
        test_folder TEXT,
        FOREIGN KEY (model_id) REFERENCES models(Id)
    );
    ";
        runsCmd.ExecuteNonQuery();
        Console.WriteLine("[DB] Ensured model_test_runs table exists");

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

        // Migration: Rename test_code to test_group if needed
        var hasTestCode = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('model_test_runs') WHERE name='test_code'");
        var hasTestGroup = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('model_test_runs') WHERE name='test_group'");
        
        if (hasTestCode > 0 && hasTestGroup == 0)
        {
            Console.WriteLine("[DB] Migrating model_test_runs: renaming test_code to test_group");
            conn.Execute("ALTER TABLE model_test_runs RENAME COLUMN test_code TO test_group");
        }

        using var stepsCmd = conn.CreateCommand();
        stepsCmd.CommandText = @"
    CREATE TABLE IF NOT EXISTS model_test_steps (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        run_id INTEGER,
        step_number INTEGER,
        step_name TEXT,
        input_json TEXT,
        output_json TEXT,
        passed INTEGER DEFAULT 0,
        error TEXT,
        duration_ms INTEGER NULL,
        FOREIGN KEY (run_id) REFERENCES model_test_runs(id)
    );
    ";
        stepsCmd.ExecuteNonQuery();
        Console.WriteLine("[DB] Ensured model_test_steps table exists");

        using var assetsCmd2 = conn.CreateCommand();
        assetsCmd2.CommandText = @"
    CREATE TABLE IF NOT EXISTS model_test_assets (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        step_id INTEGER,
        file_type TEXT,
        file_path TEXT,
        description TEXT,
        duration_sec REAL,
        size_bytes INTEGER,
        story_id INTEGER NULL,
        FOREIGN KEY (step_id) REFERENCES model_test_steps(id),
        FOREIGN KEY (story_id) REFERENCES stories(id)
    );
    ";
        assetsCmd2.ExecuteNonQuery();
        Console.WriteLine("[DB] Ensured model_test_assets table exists");

        // NOTE: The `tts_voices` table is expected to exist in this installation
        Console.WriteLine($"[DB] InitializeSchema completed in {sw.ElapsedMilliseconds}ms");
    }

    // Async batch insert for log entries. Will insert all provided entries in a single INSERT statement when possible.
    public async Task InsertLogsAsync(IEnumerable<TinyGenerator.Models.LogEntry> entries)
    {
        var list = entries?.ToList() ?? new List<TinyGenerator.Models.LogEntry>();
        if (list.Count == 0) return;

        using var conn = CreateConnection();
        await ((SqliteConnection)conn).OpenAsync();

        // Build a single INSERT ... VALUES (...),(...),... with uniquely named parameters to avoid collisions
        var cols = new[] { "Ts", "Level", "Category", "Message", "Exception", "State", "ThreadId", "AgentName", "Context" };
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
            parameters.Add("@AgentName" + i, e.AgentName);
            parameters.Add("@Context" + i, e.Context);
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
        // Return only core model columns (Skill* and Last* columns removed)
        return string.Join(", ", new[] { "Name", "Provider", "Endpoint", "IsLocal", "MaxContext", "ContextToUse", "FunctionCallingScore", "WriterScore", "CostInPerToken", "CostOutPerToken", "LimitTokensDay", "LimitTokensWeek", "LimitTokensMonth", "Metadata", "Enabled", "CreatedAt", "UpdatedAt", "TestDurationSeconds", "NoTools" });
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

        var sql = "SELECT Ts, Level, Category, Message, Exception, State, ThreadId, AgentName, Context FROM Log";
        if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
        sql += " ORDER BY Id DESC LIMIT @Limit OFFSET @Offset";
        parameters.Add("Limit", limit);
        parameters.Add("Offset", offset);

        return conn.Query<TinyGenerator.Models.LogEntry>(sql, parameters).ToList();
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
        conn.Execute("DELETE FROM Log");
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

        var modelId = conn.ExecuteScalar<long?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
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

        var modelId = conn.ExecuteScalar<long?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
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

        var modelId = conn.ExecuteScalar<long?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
        if (!modelId.HasValue) return;

        // Get all unique test groups for this model
        var groups = conn.Query<string>("SELECT DISTINCT test_group FROM model_test_runs WHERE model_id = @mid", new { mid = modelId.Value }).ToList();

        double totalScore = 0;
        int groupCount = 0;

        foreach (var group in groups)
        {
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
    }
}


