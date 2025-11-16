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

    public long? GetModelIdByName(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;
        using var conn = CreateConnection();
        conn.Open();
        try
        {
            // Use rowid as the numeric identifier for models (table uses Name as primary key)
            var id = conn.ExecuteScalar<long?>("SELECT rowid FROM models WHERE Name = @Name LIMIT 1", new { Name = modelName });
            return id;
        }
        catch { return null; }
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
        var hasExecutionPlan = false;
        try
        {
            using var check = conn.CreateCommand();
            check.CommandText = "PRAGMA table_info('test_definitions');";
            using var rdr = check.ExecuteReader();
            while (rdr.Read()) { var col = rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1); if (string.Equals(col, "allowed_plugins", StringComparison.OrdinalIgnoreCase)) { hasAllowed = true; } if (string.Equals(col, "execution_plan", StringComparison.OrdinalIgnoreCase)) { hasExecutionPlan = true; } }
        }
        catch { }

    var sql = @"SELECT id AS Id, group_name AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_ms AS TimeoutMs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue" + (hasAllowed ? ", allowed_plugins AS AllowedPlugins" : "") + (hasExecutionPlan ? ", execution_plan AS ExecutionPlan" : "") + @"
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

    var sql = $@"SELECT id AS Id, group_name AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_ms AS TimeoutMs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue, allowed_plugins AS AllowedPlugins, json_response_format AS JsonResponseFormat, active AS Active
FROM test_definitions WHERE {string.Join(" AND ", where)} ORDER BY {order}";

        return conn.Query<TestDefinition>(sql, parameters).ToList();
    }

    public TestDefinition? GetTestDefinitionById(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
    var sql = @"SELECT id AS Id, group_name AS GroupName, library AS Library, function_name AS FunctionName, expected_behavior AS ExpectedBehavior, expected_asset AS ExpectedAsset, prompt AS Prompt, timeout_ms AS TimeoutMs, priority AS Priority, valid_score_range AS ValidScoreRange, test_type AS TestType, expected_prompt_value AS ExpectedPromptValue, allowed_plugins AS AllowedPlugins, execution_plan AS ExecutionPlan, json_response_format AS JsonResponseFormat, active AS Active
FROM test_definitions WHERE id = @id LIMIT 1";
        return conn.QueryFirstOrDefault<TestDefinition>(sql, new { id });
    }

    public int InsertTestDefinition(TestDefinition td)
    {
        using var conn = CreateConnection();
        conn.Open();
    var sql = @"INSERT INTO test_definitions(group_name, library, function_name, expected_behavior, expected_asset, prompt, timeout_ms, priority, valid_score_range, test_type, expected_prompt_value, allowed_plugins, execution_plan, json_response_format, active)
VALUES(@GroupName,@Library,@FunctionName,@ExpectedBehavior,@ExpectedAsset,@Prompt,@TimeoutMs,@Priority,@ValidScoreRange,@TestType,@ExpectedPromptValue,@AllowedPlugins,@ExecutionPlan,@JsonResponseFormat,@Active); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, td);
        return (int)id;
    }

    public void UpdateTestDefinition(TestDefinition td)
    {
        using var conn = CreateConnection();
        conn.Open();
    var sql = @"UPDATE test_definitions SET group_name=@GroupName, library=@Library, function_name=@FunctionName, expected_behavior=@ExpectedBehavior, expected_asset=@ExpectedAsset, prompt=@Prompt, timeout_ms=@TimeoutMs, priority=@Priority, valid_score_range=@ValidScoreRange, test_type=@TestType, expected_prompt_value=@ExpectedPromptValue, allowed_plugins=@AllowedPlugins, execution_plan=@ExecutionPlan, json_response_format=@JsonResponseFormat, active=@Active WHERE id = @Id";
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

    public int AddTestAsset(int stepId, string fileType, string filePath, string? description = null, double? durationSec = null, long? sizeBytes = null, long? storyId = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"INSERT INTO model_test_assets(step_id, file_type, file_path, description, duration_sec, size_bytes, story_id) VALUES(@step_id, @file_type, @file_path, @description, @duration_sec, @size_bytes, @story_id); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, new { step_id = stepId, file_type = fileType, file_path = filePath, description, duration_sec = durationSec, size_bytes = sizeBytes, story_id = storyId });
        return (int)id;
    }

    public long AddStoryEvaluation(long storyId, string rawJson, double totalScore, long? modelId = null, int? agentId = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        // Try parse JSON to extract category fields - best effort
        // Parsing helper logic inline below

        // Default values
        var values = new {
            narrative_coherence_score = 0,
            narrative_coherence_defects = string.Empty,
            structure_score = 0,
            structure_defects = string.Empty,
            characterization_score = 0,
            characterization_defects = string.Empty,
            dialogues_score = 0,
            dialogues_defects = string.Empty,
            pacing_score = 0,
            pacing_defects = string.Empty,
            originality_score = 0,
            originality_defects = string.Empty,
            style_score = 0,
            style_defects = string.Empty,
            worldbuilding_score = 0,
            worldbuilding_defects = string.Empty,
            thematic_coherence_score = 0,
            thematic_coherence_defects = string.Empty,
            emotional_impact_score = 0,
            emotional_impact_defects = string.Empty,
            overall_evaluation = string.Empty
        };

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
            return id;
        }
        catch (Exception)
        {
            var sql = @"INSERT INTO stories_evaluations(story_id, total_score, raw_json, model_id, agent_id, ts) VALUES(@story_id, @total, @raw, @model_id, @agent_id, @ts); SELECT last_insert_rowid();";
            var id = conn.ExecuteScalar<long>(sql, new { story_id = storyId, total = totalScore, raw = rawJson, model_id = modelId ?? (long?)null, agent_id = agentId ?? (int?)null, ts = DateTime.UtcNow.ToString("o") });
            return id;
        }
    }

    public List<TinyGenerator.Models.LogEntry> GetStoryEvaluationsByStoryId(long storyId)
    {
        // For now return as a LogEntry-like structure or a dedicated DTO. Simpler approach: return raw rows with JSON in raw_json.
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT se.id AS Id, se.ts AS Ts, COALESCE(m.Name,'') AS Model, COALESCE(a.name,'') AS Category, se.total_score AS Score, se.raw_json AS Message FROM stories_evaluations se LEFT JOIN models m ON se.model_id = m.rowid LEFT JOIN agents a ON se.agent_id = a.id WHERE se.story_id = @sid ORDER BY se.id";
        return conn.Query<TinyGenerator.Models.LogEntry>(sql, new { sid = storyId }).ToList();
    }

    public List<TinyGenerator.Models.StoryEvaluation> GetStoryEvaluations(long storyId)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT id AS Id, story_id AS StoryId, narrative_coherence_score AS NarrativeCoherenceScore, narrative_coherence_defects AS NarrativeCoherenceDefects, structure_score AS StructureScore, structure_defects AS StructureDefects, characterization_score AS CharacterizationScore, characterization_defects AS CharacterizationDefects, dialogues_score AS DialoguesScore, dialogues_defects AS DialoguesDefects, pacing_score AS PacingScore, pacing_defects AS PacingDefects, originality_score AS OriginalityScore, originality_defects AS OriginalityDefects, style_score AS StyleScore, style_defects AS StyleDefects, worldbuilding_score AS WorldbuildingScore, worldbuilding_defects AS WorldbuildingDefects, thematic_coherence_score AS ThematicCoherenceScore, thematic_coherence_defects AS ThematicCoherenceDefects, emotional_impact_score AS EmotionalImpactScore, emotional_impact_defects AS EmotionalImpactDefects, total_score AS TotalScore, overall_evaluation AS OverallEvaluation, raw_json AS RawJson, model_id AS ModelId, agent_id AS AgentId, ts AS Ts FROM stories_evaluations WHERE story_id = @sid ORDER BY id";
        // Also join models and agents for human-friendly names and a 'Score' alias used by UI
        sql = @"SELECT se.id AS Id, se.story_id AS StoryId, se.narrative_coherence_score AS NarrativeCoherenceScore, se.narrative_coherence_defects AS NarrativeCoherenceDefects, se.structure_score AS StructureScore, se.structure_defects AS StructureDefects, se.characterization_score AS CharacterizationScore, se.characterization_defects AS CharacterizationDefects, se.dialogues_score AS DialoguesScore, se.dialogues_defects AS DialoguesDefects, se.pacing_score AS PacingScore, se.pacing_defects AS PacingDefects, se.originality_score AS OriginalityScore, se.originality_defects AS OriginalityDefects, se.style_score AS StyleScore, se.style_defects AS StyleDefects, se.worldbuilding_score AS WorldbuildingScore, se.worldbuilding_defects AS WorldbuildingDefects, se.thematic_coherence_score AS ThematicCoherenceScore, se.thematic_coherence_defects AS ThematicCoherenceDefects, se.emotional_impact_score AS EmotionalImpactScore, se.emotional_impact_defects AS EmotionalImpactDefects, se.total_score AS TotalScore, se.overall_evaluation AS OverallEvaluation, se.raw_json AS RawJson, se.model_id AS ModelId, se.agent_id AS AgentId, se.ts AS Ts, COALESCE(m.Name, '') AS Model, se.total_score AS Score FROM stories_evaluations se LEFT JOIN models m ON se.model_id = m.rowid WHERE se.story_id = @sid ORDER BY se.id";
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
        try { if (!string.IsNullOrWhiteSpace(r.ModelA)) midA = GetModelIdByName(r.ModelA); } catch { }
        try { aidA = GetAgentIdByName("WriterA"); } catch { }
        var sqlA = @"INSERT INTO stories(generation_id, memory_key, ts, prompt, story, eval, score, approved, status, model_id, agent_id) VALUES(@gid,@mk,@ts,@p,@c,@e,@s,@ap,@st,@mid,@aid);";
        conn.Execute(sqlA, new { gid = genId, mk = memoryKey ?? genId, ts = DateTime.UtcNow.ToString("o"), p = prompt ?? string.Empty, c = r.StoryA ?? string.Empty, e = r.EvalA ?? string.Empty, s = r.ScoreA, ap = string.IsNullOrEmpty(r.Approved) ? 0 : 1, st = string.IsNullOrEmpty(r.Approved) ? "rejected" : "approved", mid = midA, aid = aidA });

        var midB = (long?)null;
        var aidB = (int?)null;
        try { if (!string.IsNullOrWhiteSpace(r.ModelB)) midB = GetModelIdByName(r.ModelB); } catch { }
        try { aidB = GetAgentIdByName("WriterB"); } catch { }
        var sqlB = @"INSERT INTO stories(generation_id, memory_key, ts, prompt, story, eval, score, approved, status, model_id, agent_id) VALUES(@gid,@mk,@ts,@p,@c,@e,@s,@ap,@st,@mid,@aid); SELECT last_insert_rowid();";
        var idRowB = conn.ExecuteScalar<long>(sqlB, new { gid = genId, mk = memoryKey ?? genId, ts = DateTime.UtcNow.ToString("o"), p = prompt ?? string.Empty, c = r.StoryB ?? string.Empty, e = r.EvalB ?? string.Empty, s = r.ScoreB, ap = string.IsNullOrEmpty(r.Approved) ? 0 : 1, st = string.IsNullOrEmpty(r.Approved) ? "rejected" : "approved", mid = midB, aid = aidB });

        var midC = (long?)null;
        var aidC = (int?)null;
        try { if (!string.IsNullOrWhiteSpace(r.ModelC)) midC = GetModelIdByName(r.ModelC); } catch { }
        try { aidC = GetAgentIdByName("WriterC"); } catch { }
        var sqlC = @"INSERT INTO stories(generation_id, memory_key, ts, prompt, story, eval, score, approved, status, model_id, agent_id) VALUES(@gid,@mk,@ts,@p,@c,@e,@s,@ap,@st,@mid,@aid); SELECT last_insert_rowid();";
        var idRowC = conn.ExecuteScalar<long>(sqlC, new { gid = genId, mk = memoryKey ?? genId, ts = DateTime.UtcNow.ToString("o"), p = prompt ?? string.Empty, c = r.StoryC ?? string.Empty, e = r.EvalC ?? string.Empty, s = r.ScoreC, ap = string.IsNullOrEmpty(r.Approved) ? 0 : 1, st = string.IsNullOrEmpty(r.Approved) ? "rejected" : "approved", mid = midC, aid = aidC });
        var finalId = idRowC == 0 ? idRowB : idRowC;
        return finalId;
    }

    public List<TinyGenerator.Models.StoryRecord> GetAllStories()
    {
        var list = new List<TinyGenerator.Models.StoryRecord>();
        using var conn = CreateConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT generation_id,
             MAX(s.memory_key) as memory_key,
             MIN(s.id) as min_id,
             MIN(s.ts) as ts,
             MIN(s.prompt) as prompt,
            MAX(CASE WHEN a.name='WriterA' THEN s.story END) as story_a,
            MAX(CASE WHEN a.name='WriterA' THEN s.eval END) as eval_a,
            MAX(CASE WHEN a.name='WriterA' THEN s.score END) as score_a,
            MAX(CASE WHEN a.name='WriterA' THEN m.name END) as model_a,
            MAX(CASE WHEN a.name='WriterB' THEN s.story END) as story_b,
            MAX(CASE WHEN a.name='WriterB' THEN s.eval END) as eval_b,
            MAX(CASE WHEN a.name='WriterB' THEN s.score END) as score_b,
            MAX(CASE WHEN a.name='WriterB' THEN m.name END) as model_b,
        MAX(CASE WHEN a.name='WriterC' THEN s.story END) as story_c,
        MAX(CASE WHEN a.name='WriterC' THEN s.eval END) as eval_c,
        MAX(CASE WHEN a.name='WriterC' THEN s.score END) as score_c,
        MAX(CASE WHEN a.name='WriterC' THEN m.name END) as model_c,
             MAX(s.approved) as approved,
             MAX(s.status) as status
FROM stories s
LEFT JOIN agents a ON s.agent_id = a.id
LEFT JOIN models m ON s.model_id = m.id
GROUP BY generation_id
ORDER BY min_id DESC
";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TinyGenerator.Models.StoryRecord
            {
                Id = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                MemoryKey = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                Timestamp = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                Prompt = r.IsDBNull(4) ? string.Empty : r.GetString(4),
                StoryA = r.IsDBNull(5) ? string.Empty : r.GetString(5),
                EvalA = r.IsDBNull(6) ? string.Empty : r.GetString(6),
                ScoreA = r.IsDBNull(7) ? 0 : r.GetDouble(7),
                ModelA = r.IsDBNull(8) ? string.Empty : r.GetString(8),
                StoryB = r.IsDBNull(9) ? string.Empty : r.GetString(9),
                EvalB = r.IsDBNull(10) ? string.Empty : r.GetString(10),
                ScoreB = r.IsDBNull(11) ? 0 : r.GetDouble(11),
                ModelB = r.IsDBNull(12) ? string.Empty : r.GetString(12),
                StoryC = r.IsDBNull(13) ? string.Empty : r.GetString(13),
                EvalC = r.IsDBNull(14) ? string.Empty : r.GetString(14),
                ScoreC = r.IsDBNull(15) ? 0 : r.GetDouble(15),
                ModelC = r.IsDBNull(16) ? string.Empty : r.GetString(16),
                Approved = !r.IsDBNull(17) && r.GetInt32(17) == 1,
                Status = r.IsDBNull(18) ? string.Empty : r.GetString(18)
            });
        }
        return list;
    }

    public TinyGenerator.Models.StoryRecord? GetStoryById(long id)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"SELECT s.id AS Id, s.generation_id AS GenerationId, s.memory_key AS MemoryKey, s.ts AS Ts, s.prompt AS Prompt, s.story AS Story, m.name AS Model, s.eval AS Eval, s.score AS Score, s.approved AS Approved, s.status AS Status, a.name AS Agent FROM stories s LEFT JOIN models m ON s.model_id = m.id LEFT JOIN agents a ON s.agent_id = a.id WHERE s.id = @id LIMIT 1";
        var row = conn.QueryFirstOrDefault<dynamic>(sql, new { id = id });
        if (row == null) return null;
        return new TinyGenerator.Models.StoryRecord
        {
            Id = (long)row.Id,
            MemoryKey = row.MemoryKey ?? string.Empty,
            Timestamp = row.Ts ?? string.Empty,
            Prompt = row.Prompt ?? string.Empty,
            StoryA = row.Story ?? string.Empty,
            ModelA = row.Model ?? string.Empty,
            EvalA = row.Eval ?? string.Empty,
            ScoreA = row.Score ?? 0.0,
            Approved = (int)(row.Approved ?? 0) == 1,
            Status = row.Status ?? string.Empty
        };
    }

    public void DeleteStoryById(long id)
    {
        using var conn = CreateConnection();
        conn.Open();
        var genId = conn.QueryFirstOrDefault<string>("SELECT generation_id FROM stories WHERE id = @id LIMIT 1", new { id });
        if (!string.IsNullOrEmpty(genId)) conn.Execute("DELETE FROM stories WHERE generation_id = @gid", new { gid = genId });
    }

    public long InsertSingleStory(string prompt, string story, long? modelId = null, int? agentId = null, double score = 0.0, string? eval = null, int approved = 0, string? status = null, string? memoryKey = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        var ts = DateTime.UtcNow.ToString("o");
        var genId = Guid.NewGuid().ToString();
        var sql = @"INSERT INTO stories(generation_id, memory_key, ts, prompt, story, eval, score, approved, status, model_id, agent_id) VALUES(@gid,@mk,@ts,@p,@c,@e,@s,@ap,@st,@mid,@aid); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, new { gid = genId, mk = memoryKey ?? genId, ts = ts, p = prompt ?? string.Empty, c = story ?? string.Empty, mid = modelId, aid = agentId, e = eval ?? string.Empty, s = score, ap = approved, st = status ?? string.Empty });
        return id;
    }

    public bool UpdateStoryById(long id, string? story = null, long? modelId = null, int? agentId = null, string? status = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        var updates = new List<string>();
        var parms = new Dictionary<string, object?>();
        if (story != null) { updates.Add("story = @story"); parms["story"] = story; }
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
        Console.WriteLine("[DB] InitializeSchema start");
        var sw = Stopwatch.StartNew();
        using var conn = CreateConnection();
        var openSw = Stopwatch.StartNew();
        conn.Open();
        openSw.Stop();
        Console.WriteLine($"[DB] Connection opened in {openSw.ElapsedMilliseconds}ms");

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
        Console.WriteLine("[DB] Created usage_state/calls/models tables");

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
    FOREIGN KEY (voice_rowid) REFERENCES tts_voices(id)
);
";
    agentsCmd.ExecuteNonQuery();
    Console.WriteLine("[DB] Created agents table (if not exists)");
    EnsureAgentModelForeignKey((SqliteConnection)conn);

    // Ensure agents table has new columns if upgrading from older schema
    try
    {
        var agentsPragmaSw = Stopwatch.StartNew();
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
        if (toAdd.Count > 0) Console.WriteLine($"[DB] Added {toAdd.Count} missing agent columns");
        agentsPragmaSw.Stop();
        Console.WriteLine($"[DB] PRAGMA agents processed in {agentsPragmaSw.ElapsedMilliseconds}ms");

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
                    var migrateSw = Stopwatch.StartNew();
                    using var migrate = conn.CreateCommand();
                    migrate.CommandText = @"UPDATE agents SET voice_rowid = (SELECT id FROM tts_voices WHERE tts_voices.voice_id = agents.voice_id) WHERE voice_id IS NOT NULL;";
                    migrate.ExecuteNonQuery();
                    migrateSw.Stop();
                    Console.WriteLine($"[DB] Migrated legacy agents.voice_id values to voice_rowid in {migrateSw.ElapsedMilliseconds}ms");
                }
                catch { }
            }
        }
        catch { }
    }
    catch { }

        var ensureModelSw = Stopwatch.StartNew();
        EnsureModelColumns((SqliteConnection)conn);
        ensureModelSw.Stop();
        Console.WriteLine($"[DB] EnsureModelColumns completed in {ensureModelSw.ElapsedMilliseconds}ms");
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
        ts TEXT
    );";
        storiesEvalCmd.ExecuteNonQuery();
        Console.WriteLine("[DB] Created stories_evaluations table (if not exists)");

        // Ensure stories_evaluations has model_id and agent_id columns (if upgrading from older schema)
        try
        {
            using var checkEval = conn.CreateCommand();
            checkEval.CommandText = "PRAGMA table_info('stories_evaluations');";
            using var rdrEval = checkEval.ExecuteReader();
            var cols = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            while (rdrEval.Read()) { var cn = rdrEval.IsDBNull(1) ? string.Empty : rdrEval.GetString(1); if (!string.IsNullOrWhiteSpace(cn)) cols.Add(cn); }
            if (!cols.Contains("model_id"))
            {
                try { using var alter = conn.CreateCommand(); alter.CommandText = "ALTER TABLE stories_evaluations ADD COLUMN model_id INTEGER NULL;"; alter.ExecuteNonQuery(); } catch { }
            }
            if (!cols.Contains("agent_id"))
            {
                try { using var alter = conn.CreateCommand(); alter.CommandText = "ALTER TABLE stories_evaluations ADD COLUMN agent_id INTEGER NULL;"; alter.ExecuteNonQuery(); } catch { }
            }
            // If legacy textual evaluator_model / evaluator_name columns exist, migrate their values into numeric model_id and agent_id
            if (cols.Contains("evaluator_model") || cols.Contains("evaluator_name"))
            {
                try
                {
                    using var checkNew = conn.CreateCommand();
                    checkNew.CommandText = "PRAGMA table_info('stories_evaluations_new');";
                    var hasNew = false;
                    try { using var rdrNew = checkNew.ExecuteReader(); hasNew = rdrNew.Read(); } catch { hasNew = false; }
                    if (!hasNew)
                    {
                        using var createNew = conn.CreateCommand();
                        createNew.CommandText = @"
    CREATE TABLE stories_evaluations_new (
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
        ts TEXT
    );";
                        createNew.ExecuteNonQuery();
                    }
                    // Copy rows converting textual evaluator fields into numeric IDs when possible
                    using var copyCmd = conn.CreateCommand();
                    copyCmd.CommandText = @"INSERT INTO stories_evaluations_new(story_id, narrative_coherence_score, narrative_coherence_defects, structure_score, structure_defects, characterization_score, characterization_defects, dialogues_score, dialogues_defects, pacing_score, pacing_defects, originality_score, originality_defects, style_score, style_defects, worldbuilding_score, worldbuilding_defects, thematic_coherence_score, thematic_coherence_defects, emotional_impact_score, emotional_impact_defects, total_score, overall_evaluation, raw_json, model_id, agent_id, ts)
SELECT se.story_id, se.narrative_coherence_score, se.narrative_coherence_defects, se.structure_score, se.structure_defects, se.characterization_score, se.characterization_defects, se.dialogues_score, se.dialogues_defects, se.pacing_score, se.pacing_defects, se.originality_score, se.originality_defects, se.style_score, se.style_defects, se.worldbuilding_score, se.worldbuilding_defects, se.thematic_coherence_score, se.thematic_coherence_defects, se.emotional_impact_score, se.emotional_impact_defects, se.total_score, se.overall_evaluation, se.raw_json, (CASE WHEN se.model_id IS NOT NULL THEN se.model_id WHEN se.evaluator_model IS NOT NULL THEN (SELECT rowid FROM models WHERE Name = se.evaluator_model LIMIT 1) ELSE NULL END) AS model_id, (CASE WHEN se.agent_id IS NOT NULL THEN se.agent_id WHEN se.evaluator_name IS NOT NULL THEN (SELECT id FROM agents WHERE Name = se.evaluator_name LIMIT 1) ELSE NULL END) AS agent_id, se.ts FROM stories_evaluations se";
                    copyCmd.ExecuteNonQuery();
                    // Replace old table
                    using var dropOld = conn.CreateCommand();
                    dropOld.CommandText = "DROP TABLE IF EXISTS stories_evaluations";
                    dropOld.ExecuteNonQuery();
                    using var renameNew = conn.CreateCommand();
                    renameNew.CommandText = "ALTER TABLE stories_evaluations_new RENAME TO stories_evaluations";
                    renameNew.ExecuteNonQuery();
                }
                catch { }
            }
        }
        catch { }

        // Ensure model_test_* tables exist (basic schema). These are used by the test runner.
        using var runsCmd = conn.CreateCommand();
        runsCmd.CommandText = @"
    CREATE TABLE IF NOT EXISTS model_test_runs (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        model_id INTEGER NULL,
        test_code TEXT,
        description TEXT,
        passed INTEGER DEFAULT 0,
        duration_ms INTEGER NULL,
        notes TEXT,
        run_date TEXT
    );
    ";
        runsCmd.ExecuteNonQuery();
        Console.WriteLine("[DB] Ensured model_test_runs table exists");

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
        duration_ms INTEGER NULL
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
        story_id INTEGER NULL
    );
    ";
        assetsCmd2.ExecuteNonQuery();
        Console.WriteLine("[DB] Ensured model_test_assets table exists");

        // Ensure model_test_assets.story_id column exists (add if missing) to link assets to saved stories
        try
        {
            using var checkAssetsPost = conn.CreateCommand();
            checkAssetsPost.CommandText = "PRAGMA table_info('model_test_assets');";
            using var rdrA = checkAssetsPost.ExecuteReader();
            var colsA = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            while (rdrA.Read()) { var cn = rdrA.IsDBNull(1) ? string.Empty : rdrA.GetString(1); if (!string.IsNullOrWhiteSpace(cn)) colsA.Add(cn); }
            if (!colsA.Contains("story_id"))
            {
                try
                {
                    using var alterA = conn.CreateCommand();
                    alterA.CommandText = "ALTER TABLE model_test_assets ADD COLUMN story_id INTEGER NULL;";
                    alterA.ExecuteNonQuery();
                    Console.WriteLine("[DB] Added missing column model_test_assets.story_id (post-create)");
                }
                catch { }
            }
        }
        catch { }

        // NOTE: The `tts_voices` table is expected to exist in this installation
        // (the table may be created by an earlier migration step or manually). To
        // prevent accidental re-creation or schema drift we no longer create the
        // table at runtime here. Migration/creation should be performed via a
        // dedicated migration step or SQL script if needed.

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
        // Ensure model_test_assets table has story_id column if the table exists
        try
        {
            using var checkAssets = conn.CreateCommand();
            checkAssets.CommandText = "PRAGMA table_info('model_test_assets');";
            using var rdrAssets = checkAssets.ExecuteReader();
            var cols = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            while (rdrAssets.Read()) { var cn = rdrAssets.IsDBNull(1) ? string.Empty : rdrAssets.GetString(1); if (!string.IsNullOrWhiteSpace(cn)) cols.Add(cn); }
            if (!cols.Contains("story_id"))
            {
                try
                {
                    using var alter = conn.CreateCommand();
                    alter.CommandText = "ALTER TABLE model_test_assets ADD COLUMN story_id INTEGER NULL;";
                    alter.ExecuteNonQuery();
                    Console.WriteLine("[DB] Added missing column model_test_assets.story_id");
                }
                catch { }
            }
        }
        catch { }
        sw.Stop();
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

    private static void EnsureAgentModelForeignKey(SqliteConnection conn)
    {
        bool needsRebuild;
        using (var fkCmd = conn.CreateCommand())
        {
            fkCmd.CommandText = "PRAGMA foreign_key_list('agents');";
            using var reader = fkCmd.ExecuteReader();
            needsRebuild = false;
            while (reader.Read())
            {
                var table = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                if (string.Equals(table, "models", StringComparison.OrdinalIgnoreCase))
                {
                    needsRebuild = true;
                    break;
                }
            }
        }

        if (!needsRebuild)
        {
            return;
        }

        using var trx = conn.BeginTransaction();
        try
        {
            conn.Execute("ALTER TABLE agents RENAME TO agents_old;", transaction: trx);
            conn.Execute(@"CREATE TABLE agents (
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
    FOREIGN KEY (voice_rowid) REFERENCES tts_voices(id)
);
", transaction: trx);
            conn.Execute(@"INSERT INTO agents (id, voice_rowid, name, role, model_id, skills, config, prompt, instructions, execution_plan, is_active, created_at, updated_at, notes)
SELECT id, voice_rowid, name, role, model_id, skills, config, prompt, instructions, execution_plan, is_active, created_at, updated_at, notes
FROM agents_old;
", transaction: trx);
            conn.Execute("DROP TABLE agents_old;", transaction: trx);
            trx.Commit();
        }
        catch
        {
            trx.Rollback();
        }
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
            if (!cols.Contains("execution_plan"))
            {
                using var alter3 = conn.CreateCommand();
                alter3.CommandText = "ALTER TABLE test_definitions ADD COLUMN execution_plan TEXT";
                try { alter3.ExecuteNonQuery(); } catch { /* ignore if cannot alter */ }
            }
            if (!cols.Contains("json_response_format"))
            {
                using var alter4 = conn.CreateCommand();
                alter4.CommandText = "ALTER TABLE test_definitions ADD COLUMN json_response_format TEXT";
                try { alter4.ExecuteNonQuery(); } catch { /* ignore if cannot alter */ }
            }

            // Populate allowed_plugins for texteval or evaluator libraries when empty
            try
            {
                using var upd = conn.CreateCommand();
                upd.CommandText = @"UPDATE test_definitions SET allowed_plugins = 'evaluator' WHERE (allowed_plugins IS NULL OR trim(allowed_plugins)='') AND (lower(group_name) = 'texteval' OR lower(library) IN ('coerenza_narrativa','struttura','caratterizzazione_personaggi','dialoghi','ritmo','originalita','stile','worldbuilding','coerenza_tematica','impatto_emotivo'));";
                upd.ExecuteNonQuery();
            }
            catch { }
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
