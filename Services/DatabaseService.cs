using System;
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
    private static readonly string[] SkillColumns =
    {
        // Skill columns removed from models table; kept empty to avoid runtime ALTERs
    };

    private static readonly HashSet<string> SkillColumnSet = new(SkillColumns, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> LegacyColumnMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "Name",
        ["provider"] = "Provider",
        ["endpoint"] = "Endpoint",
        ["is_local"] = "IsLocal",
        ["max_context"] = "MaxContext",
        ["context_to_use"] = "ContextToUse",
        ["function_calling_score"] = "FunctionCallingScore",
        ["cost_in_per_token"] = "CostInPerToken",
        ["cost_out_per_token"] = "CostOutPerToken",
        ["limit_tokens_day"] = "LimitTokensDay",
        ["limit_tokens_week"] = "LimitTokensWeek",
        ["limit_tokens_month"] = "LimitTokensMonth",
        ["metadata"] = "Metadata",
        ["enabled"] = "Enabled",
        ["created_at"] = "CreatedAt",
        ["updated_at"] = "UpdatedAt",
        ["skill_toupper"] = "SkillToUpper",
        ["skill_tolower"] = "SkillToLower",
        ["skill_trim"] = "SkillTrim",
        ["skill_length"] = "SkillLength",
        ["skill_substring"] = "SkillSubstring",
        ["skill_join"] = "SkillJoin",
        ["skill_split"] = "SkillSplit",
        ["skill_add"] = "SkillAdd",
        ["skill_subtract"] = "SkillSubtract",
        ["skill_multiply"] = "SkillMultiply",
        ["skill_divide"] = "SkillDivide",
        ["skill_sqrt"] = "SkillSqrt",
        ["skill_now"] = "SkillNow",
        ["skill_today"] = "SkillToday",
        ["skill_adddays"] = "SkillAddDays",
        ["skill_addhours"] = "SkillAddHours",
        ["skill_remember"] = "SkillRemember",
        ["skill_recall"] = "SkillRecall",
        ["skill_forget"] = "SkillForget",
        ["skill_fileexists"] = "SkillFileExists",
        ["skill_httpget"] = "SkillHttpGet"
    };

    private static readonly IReadOnlyDictionary<string, string> ColumnDefinitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["FunctionCallingScore"] = "INTEGER DEFAULT 0",
        ["CreatedAt"] = "TEXT",
        ["UpdatedAt"] = "TEXT",
        ["TestDurationSeconds"] = "REAL",
        ["NoTools"] = "INTEGER DEFAULT 0",
        // Last* columns removed from this installation; keep core metadata only
    };

    // Add LastTestResults to column definitions (TEXT JSON)
    static DatabaseService()
    {
        // No legacy Last* columns in models table for this installation.
    }

    private readonly string _connectionString;

    public DatabaseService(string dbPath = "data/storage.db")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? ".");
        // Enable foreign key enforcement for SQLite connections
        _connectionString = $"Data Source={dbPath};Foreign Keys=True";
        InitializeSchema();
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
            var sql = @"SELECT id AS RunId, test_code AS TestCode, passed AS Passed, duration_ms AS DurationMs, run_date AS RunDate FROM model_test_runs WHERE model_id = @mid ORDER BY id DESC LIMIT 1";
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
    /// Return the model Name for a given rowid in the models table (best-effort).
    /// </summary>
    public string? GetModelNameById(long rowId)
    {
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            var name = conn.ExecuteScalar<string?>("SELECT Name FROM models WHERE rowid = @id LIMIT 1", new { id = rowId });
            return name;
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
            var modelId = conn.ExecuteScalar<long?>("SELECT Id FROM models WHERE Name = @Name LIMIT 1", new { Name = name });
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
                        while (end < tail.Length && (char.IsLetterOrDigit(tail[end]) || tail[end] == '_' )) end++;
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
        var sql = @"SELECT id AS Id, ts AS Timestamp, model AS Model, provider AS Provider, input_tokens AS InputTokens, output_tokens AS OutputTokens, tokens AS Tokens, cost AS Cost, request AS Request, response AS Response FROM calls ORDER BY id DESC LIMIT @Limit";
        return conn.Query<CallRecord>(sql, new { Limit = limit }).ToList();
    }

    // Agents CRUD
    public List<TinyGenerator.Models.Agent> ListAgents()
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT a.id AS Id, a.voice_rowid AS VoiceId, t.name AS VoiceName, a.name AS Name, a.role AS Role, a.model_id AS ModelId, a.skills AS Skills, a.config AS Config, a.prompt AS Prompt, a.instructions AS Instructions, a.execution_plan AS ExecutionPlan, a.is_active AS IsActive, a.created_at AS CreatedAt, a.updated_at AS UpdatedAt, a.notes AS Notes FROM agents a LEFT JOIN tts_voices t ON a.voice_rowid = t.id ORDER BY a.name";
        return conn.Query<TinyGenerator.Models.Agent>(sql).ToList();
    }

    public TinyGenerator.Models.Agent? GetAgentById(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT a.id AS Id, a.voice_rowid AS VoiceId, t.name AS VoiceName, a.name AS Name, a.role AS Role, a.model_id AS ModelId, a.skills AS Skills, a.config AS Config, a.prompt AS Prompt, a.instructions AS Instructions, a.execution_plan AS ExecutionPlan, a.is_active AS IsActive, a.created_at AS CreatedAt, a.updated_at AS UpdatedAt, a.notes AS Notes FROM agents a LEFT JOIN tts_voices t ON a.voice_rowid = t.id WHERE a.id = @id LIMIT 1";
        return conn.QueryFirstOrDefault<TinyGenerator.Models.Agent>(sql, new { id });
    }

    public int InsertAgent(TinyGenerator.Models.Agent a)
    {
        using var conn = CreateConnection();
        conn.Open();
        var now = DateTime.UtcNow.ToString("o");
        a.CreatedAt ??= now;
        a.UpdatedAt = now;
        var sql = @"INSERT INTO agents(voice_rowid, name, role, model_id, skills, config, prompt, instructions, execution_plan, is_active, created_at, updated_at, notes) VALUES(@VoiceId,@Name,@Role,@ModelId,@Skills,@Config,@Prompt,@Instructions,@ExecutionPlan,@IsActive,@CreatedAt,@UpdatedAt,@Notes); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, a);
        return (int)id;
    }

    public void UpdateAgent(TinyGenerator.Models.Agent a)
    {
        if (a == null) return;
        using var conn = CreateConnection();
        conn.Open();
        a.UpdatedAt = DateTime.UtcNow.ToString("o");
        var sql = @"UPDATE agents SET voice_rowid=@VoiceId, name=@Name, role=@Role, model_id=@ModelId, skills=@Skills, config=@Config, prompt=@Prompt, instructions=@Instructions, execution_plan=@ExecutionPlan, is_active=@IsActive, updated_at=@UpdatedAt, notes=@Notes WHERE id = @Id";
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
            var sql = "SELECT DISTINCT group_name FROM test_definitions WHERE active = 1 ORDER BY group_name";
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
        // Include allowed_plugins column if present in the schema
        var hasAllowed = false;
        try
        {
            using var check = conn.CreateCommand();
            check.CommandText = "PRAGMA table_info('test_definitions');";
            using var rdr = check.ExecuteReader();
            while (rdr.Read()) { var col = rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1); if (string.Equals(col, "allowed_plugins", StringComparison.OrdinalIgnoreCase)) { hasAllowed = true; break; } }
        }
        catch { }

    var sql = @"SELECT id AS Id, group_name AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_ms AS TimeoutMs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue" + (hasAllowed ? ", allowed_plugins AS AllowedPlugins" : "") + @"
FROM test_definitions WHERE group_name = @g AND active = 1 ORDER BY priority, id";
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
                    var sql = @"SELECT id AS Id, group_name AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_ms AS TimeoutMs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue, allowed_plugins AS AllowedPlugins
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
            where.Add("(group_name LIKE @q OR library LIKE @q OR function_name LIKE @q OR prompt LIKE @q)");
            parameters.Add("q", "%" + search + "%");
        }

        var order = "id ASC";
        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            // Whitelist allowed sort columns
            var col = sortBy.ToLowerInvariant();
            if (col == "group" || col == "groupname") col = "group_name";
            else if (col == "library") col = "library";
            else if (col == "function" || col == "functionname") col = "function_name";
            else if (col == "priority") col = "priority";
            else col = "id";
            order = col + (ascending ? " ASC" : " DESC");
        }

    var sql = $@"SELECT id AS Id, group_name AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_ms AS TimeoutMs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue, allowed_plugins AS AllowedPlugins
FROM test_definitions WHERE {string.Join(" AND ", where)} ORDER BY {order}";

        return conn.Query<TestDefinition>(sql, parameters).ToList();
    }

    public TestDefinition? GetTestDefinitionById(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
    var sql = @"SELECT id AS Id, group_name AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_ms AS TimeoutMs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue, allowed_plugins AS AllowedPlugins
FROM test_definitions WHERE id = @id LIMIT 1";
        return conn.QueryFirstOrDefault<TestDefinition>(sql, new { id });
    }

    public int InsertTestDefinition(TestDefinition td)
    {
        using var conn = CreateConnection();
        conn.Open();
    var sql = @"INSERT INTO test_definitions(group_name, library, function_name, expected_behavior, expected_asset, prompt, timeout_ms, priority, valid_score_range, test_type, expected_prompt_value, allowed_plugins, active)
VALUES(@GroupName,@Library,@FunctionName,@ExpectedBehavior,@ExpectedAsset,@Prompt,@TimeoutMs,@Priority,@ValidScoreRange,@TestType,@ExpectedPromptValue,@AllowedPlugins,1); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, td);
        return (int)id;
    }

    public void UpdateTestDefinition(TestDefinition td)
    {
        using var conn = CreateConnection();
        conn.Open();
    var sql = @"UPDATE test_definitions SET group_name=@GroupName, library=@Library, function_name=@FunctionName, expected_behavior=@ExpectedBehavior, expected_asset=@ExpectedAsset, prompt=@Prompt, timeout_ms=@TimeoutMs, priority=@Priority, valid_score_range=@ValidScoreRange, test_type=@TestType, expected_prompt_value=@ExpectedPromptValue, allowed_plugins=@AllowedPlugins WHERE id = @Id";
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
            var runId = conn.ExecuteScalar<int?>("SELECT id FROM model_test_runs WHERE model_id = @mid AND test_code = @g ORDER BY id DESC LIMIT 1", new { mid = modelId.Value, g = groupName });
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
            var runId = conn.ExecuteScalar<int?>("SELECT id FROM model_test_runs WHERE model_id = @mid AND test_code = @g ORDER BY id DESC LIMIT 1", new { mid = modelId.Value, g = groupName });
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
    public int CreateTestRun(string modelName, string testCode, string? description = null, bool passed = false, long? durationMs = null, string? notes = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        // Insert run: resolve model_id from models.Name and store only model_id (model_name column removed)
        var sql = @"INSERT INTO model_test_runs(model_id, test_code, description, passed, duration_ms, notes) VALUES((SELECT Id FROM models WHERE Name = @model_name LIMIT 1), @test_code, @description, @passed, @duration_ms, @notes); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, new { model_name = modelName, test_code = testCode, description, passed = passed ? 1 : 0, duration_ms = durationMs, notes });
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

    public int AddTestAsset(int stepId, string fileType, string filePath, string? description = null, double? durationSec = null, long? sizeBytes = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"INSERT INTO model_test_assets(step_id, file_type, file_path, description, duration_sec, size_bytes) VALUES(@step_id, @file_type, @file_path, @description, @duration_sec, @size_bytes); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, new { step_id = stepId, file_type = fileType, file_path = filePath, description, duration_sec = durationSec, size_bytes = sizeBytes });
        return (int)id;
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
        string tagsJson = null;
        try { tagsJson = v.Tags != null ? JsonSerializer.Serialize(v.Tags) : null; } catch { tagsJson = null; }
        var sql = @"INSERT INTO tts_voices(voice_id, name, model, language, gender, age, confidence, tags, sample_path, template_wav, metadata, created_at, updated_at)
VALUES(@VoiceId,@Name,@Model,@Language,@Gender,@Age,@Confidence,@Tags,@SamplePath,@TemplateWav,@Metadata,@CreatedAt,@UpdatedAt)
ON CONFLICT(voice_id) DO UPDATE SET name=@Name, model=@Model, language=@Language, gender=@Gender, age=@Age, confidence=@Confidence, tags=@Tags, sample_path=@SamplePath, template_wav=@TemplateWav, metadata=@Metadata, updated_at=@UpdatedAt;";

        conn.Execute(sql, new {
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
                        Tags = new System.Collections.Generic.Dictionary<string,string>()
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
        using var conn = CreateConnection();
        conn.Open();

    var skillColumnsSql = SkillColumns.Length > 0 ? ",\n    " + string.Join(",\n    ", SkillColumns.Select(c => $"{c} INTEGER DEFAULT NULL")) : string.Empty;

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
    {skillColumnsSql}
);";
        cmd.ExecuteNonQuery();

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
    prompt TEXT NULL,
    instructions TEXT NULL,
    execution_plan TEXT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL,
    updated_at TEXT NULL,
    notes TEXT NULL,
    FOREIGN KEY (model_id) REFERENCES models(rowid),
    FOREIGN KEY (voice_rowid) REFERENCES tts_voices(id)
);
";
    agentsCmd.ExecuteNonQuery();

    // Ensure agents table has new columns if upgrading from older schema
    try
    {
        using var checkAgents = conn.CreateCommand();
        checkAgents.CommandText = "PRAGMA table_info('agents');";
        using var rdr = checkAgents.ExecuteReader();
        var cols = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        while (rdr.Read()) { var col = rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1); if (!string.IsNullOrWhiteSpace(col)) cols.Add(col); }
        var toAdd = new System.Collections.Generic.List<string>();
        if (!cols.Contains("prompt")) toAdd.Add("ALTER TABLE agents ADD COLUMN prompt TEXT NULL;");
        if (!cols.Contains("instructions")) toAdd.Add("ALTER TABLE agents ADD COLUMN instructions TEXT NULL;");
        if (!cols.Contains("execution_plan")) toAdd.Add("ALTER TABLE agents ADD COLUMN execution_plan TEXT NULL;");
        if (!cols.Contains("voice_rowid") && !cols.Contains("voice_id")) toAdd.Add("ALTER TABLE agents ADD COLUMN voice_rowid INTEGER NULL;");
        foreach (var a in toAdd)
        {
            try { using var cmdAdd = conn.CreateCommand(); cmdAdd.CommandText = a; cmdAdd.ExecuteNonQuery(); } catch { }
        }

        // If there is a legacy text 'voice_id' column, try to migrate values to voice_rowid
        try
        {
            if (cols.Contains("voice_id") && !cols.Contains("voice_rowid"))
            {
                // voice_rowid wasn't added above (if missing) - ensure it's present
                try { using var addCol = conn.CreateCommand(); addCol.CommandText = "ALTER TABLE agents ADD COLUMN voice_rowid INTEGER NULL;"; addCol.ExecuteNonQuery(); } catch { }
            }
            if (cols.Contains("voice_id"))
            {
                try
                {
                    using var migrate = conn.CreateCommand();
                    migrate.CommandText = @"UPDATE agents SET voice_rowid = (SELECT id FROM tts_voices WHERE tts_voices.voice_id = agents.voice_id) WHERE voice_id IS NOT NULL;";
                    migrate.ExecuteNonQuery();
                }
                catch { }
            }
        }
        catch { }
    }
    catch { }

        EnsureModelColumns((SqliteConnection)conn);
        // Seed some commonly used, lower-cost OpenAI chat models so they appear in the models table
        try
        {
            SeedDefaultOpenAiModels();
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

        // TTS voices table - store voices discovered from the local TTS service
        using var ttsCmd = conn.CreateCommand();
        ttsCmd.CommandText = @"CREATE TABLE IF NOT EXISTS tts_voices (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    voice_id TEXT UNIQUE,
    name TEXT,
    model TEXT,
    language TEXT,
    gender TEXT,
    age TEXT,
    confidence REAL,
    tags TEXT,
    sample_path TEXT,
    template_wav TEXT,
    metadata TEXT,
    created_at TEXT,
    updated_at TEXT
);
";
        ttsCmd.ExecuteNonQuery();

        // Migration steps were already executed for this installation. No runtime migration performed.
        // Migrate legacy `logs` table (if present) into the new `Log` table and remove the old table.
        try
        {
            using var check = conn.CreateCommand();
            check.CommandText = "PRAGMA table_info('logs');";
            using var rdr = check.ExecuteReader();
            if (rdr.Read())
            {
                // Found legacy table - copy rows
                try
                {
                    using var copy = conn.CreateCommand();
                    copy.CommandText = @"INSERT INTO Log (Ts, Level, Category, Message, Exception, State)
SELECT ts, level, category, message, exception, state FROM logs;";
                    copy.ExecuteNonQuery();

                    // Drop old table after successful copy
                    using var drop = conn.CreateCommand();
                    drop.CommandText = "DROP TABLE IF EXISTS logs;";
                    drop.ExecuteNonQuery();
                }
                catch
                {
                    // ignore copy failures
                }
            }
        }
        catch
        {
            // ignore migration check errors
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
        // Add a few widely-used lower-cost ChatGPT/OpenAI models to the models table if they do not already exist.
        var defaults = new List<ModelInfo>
        {
            // Seed a few inexpensive ChatGPT-style models with small default costs (USD per 1k tokens).
            new ModelInfo { Name = "gpt-3.5-turbo", Provider = "openai", IsLocal = false, MaxContext = 4096, ContextToUse = 2048, CostInPerToken = 0.002, CostOutPerToken = 0.002 },
            new ModelInfo { Name = "gpt-3.5-turbo-16k", Provider = "openai", IsLocal = false, MaxContext = 16384, ContextToUse = 8192, CostInPerToken = 0.003, CostOutPerToken = 0.003 },
            new ModelInfo { Name = "gpt-4o-mini", Provider = "openai", IsLocal = false, MaxContext = 8192, ContextToUse = 4096, CostInPerToken = 0.005, CostOutPerToken = 0.005 }
        };

        foreach (var m in defaults)
        {
            try
            {
                var exists = GetModelInfo(m.Name);
                if (exists == null)
                {
                    UpsertModel(m);
                }
            }
            catch
            {
                // ignore individual seed failures
            }
        }
    }

    private void EnsureModelColumns(SqliteConnection conn)
    {
        var existingCols = GetExistingColumns(conn);

        foreach (var mapping in LegacyColumnMap)
        {
            if (existingCols.Contains(mapping.Value)) continue;
            if (!existingCols.Contains(mapping.Key)) continue;

            try
            {
                using var rename = conn.CreateCommand();
                rename.CommandText = $"ALTER TABLE models RENAME COLUMN {mapping.Key} TO {mapping.Value};";
                rename.ExecuteNonQuery();
                existingCols.Remove(mapping.Key);
                existingCols.Add(mapping.Value);
            }
            catch
            {
                // ignore failures
            }
        }

    existingCols = GetExistingColumns(conn);

        // Ensure CreatedAt/UpdatedAt present
        foreach (var def in ColumnDefinitions)
        {
            if (existingCols.Contains(def.Key)) continue;
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE models ADD COLUMN {def.Key} {def.Value};";
            alter.ExecuteNonQuery();
        }

        // Ensure skill columns
        // Skill columns removed from models table for this installation; no ALTER needed.

        // Ensure allowed_plugins column exists in test_definitions (best-effort migration)
        try
        {
            using var check = conn.CreateCommand();
            check.CommandText = "PRAGMA table_info('test_definitions');";
            using var rdr = check.ExecuteReader();
            var cols = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            while (rdr.Read()) { var col = rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1); if (!string.IsNullOrWhiteSpace(col)) cols.Add(col); }
            if (!cols.Contains("allowed_plugins"))
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE test_definitions ADD COLUMN allowed_plugins TEXT";
                try { alter.ExecuteNonQuery(); } catch { /* ignore if cannot alter */ }
            }
            if (!cols.Contains("valid_score_range"))
            {
                using var alter2 = conn.CreateCommand();
                alter2.CommandText = "ALTER TABLE test_definitions ADD COLUMN valid_score_range TEXT";
                try { alter2.ExecuteNonQuery(); } catch { /* ignore if cannot alter */ }
            }
        }
        catch { }
    }

    private static HashSet<string> GetExistingColumns(SqliteConnection conn)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(models);";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var colName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (!string.IsNullOrWhiteSpace(colName)) columns.Add(colName);
        }

        return columns;
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
    return string.Join(", ", new[] { "Name","Provider","Endpoint","IsLocal","MaxContext","ContextToUse","FunctionCallingScore","CostInPerToken","CostOutPerToken","LimitTokensDay","LimitTokensWeek","LimitTokensMonth","Metadata","Enabled","CreatedAt","UpdatedAt","TestDurationSeconds","NoTools" });
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
}
