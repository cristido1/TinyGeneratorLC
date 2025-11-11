using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using TinyGenerator.Models;
using ModelInfo = TinyGenerator.Models.ModelInfo;
using CallRecord = TinyGenerator.Models.CallRecord;

namespace TinyGenerator.Services;

public sealed class DatabaseService
{
    private static readonly string[] SkillColumns =
    {
        "SkillToUpper",
        "SkillToLower",
        "SkillTrim",
        "SkillLength",
        "SkillSubstring",
        "SkillJoin",
        "SkillSplit",
        "SkillAdd",
        "SkillSubtract",
        "SkillMultiply",
        "SkillDivide",
        "SkillSqrt",
        "SkillNow",
        "SkillToday",
        "SkillAddDays",
        "SkillAddHours",
        "SkillRemember",
        "SkillRecall",
        "SkillForget",
        "SkillFileExists",
        "SkillHttpGet"
        ,
        // AudioCraft skill columns
        "SkillAudioCheckHealth",
        "SkillAudioListModels",
        "SkillAudioGenerateMusic",
        "SkillAudioGenerateSound",
        "SkillAudioDownloadFile"
        ,
        // TTS skill columns
        "SkillTtsCheckHealth",
        "SkillTtsListVoices",
        "SkillTtsSynthesize"
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
        ["LastMusicTestFile"] = "TEXT",
        ["LastSoundTestFile"] = "TEXT",
        ["LastTtsTestFile"] = "TEXT"
    };

    // Add LastTestResults to column definitions (TEXT JSON)
    static DatabaseService()
    {
        // ensure ColumnDefinitions includes LastTestResults in case static initializer order matters
        if (!ColumnDefinitions.ContainsKey("LastTestResults"))
        {
            ((Dictionary<string, string>)ColumnDefinitions)["LastTestResults"] = "TEXT";
        }
    }

    private readonly string _connectionString;

    public DatabaseService(string dbPath = "data/storage.db")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? ".");
        _connectionString = $"Data Source={dbPath}";
        InitializeSchema();
    }

    private IDbConnection CreateConnection() => new SqliteConnection(_connectionString);

    public List<ModelInfo> ListModels()
    {
        using var conn = CreateConnection();
        conn.Open();
    var cols = string.Join(", ", new[] { "Name","Provider","Endpoint","IsLocal","MaxContext","ContextToUse","FunctionCallingScore","CostInPerToken","CostOutPerToken","LimitTokensDay","LimitTokensWeek","LimitTokensMonth","Metadata","Enabled","CreatedAt","UpdatedAt","TestDurationSeconds","LastTestResults","LastMusicTestFile","LastSoundTestFile","LastTtsTestFile" }.Concat(SkillColumns));
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

        var sql = @"INSERT INTO models(Name, Provider, Endpoint, IsLocal, MaxContext, ContextToUse, FunctionCallingScore, CostInPerToken, CostOutPerToken, LimitTokensDay, LimitTokensWeek, LimitTokensMonth, Metadata, Enabled, CreatedAt, UpdatedAt)
VALUES(@Name,@Provider,@Endpoint,@IsLocal,@MaxContext,@ContextToUse,@FunctionCallingScore,@CostInPerToken,@CostOutPerToken,@LimitTokensDay,@LimitTokensWeek,@LimitTokensMonth,@Metadata,@Enabled,@CreatedAt,@UpdatedAt)
ON CONFLICT(Name) DO UPDATE SET Provider=@Provider, Endpoint=@Endpoint, IsLocal=@IsLocal, MaxContext=@MaxContext, ContextToUse=@ContextToUse, FunctionCallingScore=@FunctionCallingScore, CostInPerToken=@CostInPerToken, CostOutPerToken=@CostOutPerToken, LimitTokensDay=@LimitTokensDay, LimitTokensWeek=@LimitTokensWeek, LimitTokensMonth=@LimitTokensMonth, Metadata=@Metadata, Enabled=@Enabled, UpdatedAt=@UpdatedAt;";

        conn.Execute(sql, model);
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

    public void UpdateModelTestResults(string modelName, int functionCallingScore, IReadOnlyDictionary<string, bool?> skillFlags, double? testDurationSeconds = null, string? lastTestResultsJson = null, string? lastMusicTestFile = null, string? lastSoundTestFile = null, string? lastTtsTestFile = null)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return;
        using var conn = CreateConnection();
        conn.Open();

        var setList = new List<string> { "FunctionCallingScore = @FunctionCallingScore", "UpdatedAt = @UpdatedAt" };
        var parameters = new DynamicParameters();
        parameters.Add("FunctionCallingScore", functionCallingScore);
        parameters.Add("UpdatedAt", DateTime.UtcNow.ToString("o"));
        if (testDurationSeconds.HasValue)
        {
            setList.Add("TestDurationSeconds = @TestDurationSeconds");
            parameters.Add("TestDurationSeconds", testDurationSeconds.Value);
        }
        if (!string.IsNullOrWhiteSpace(lastTestResultsJson))
        {
            setList.Add("LastTestResults = @LastTestResults");
            parameters.Add("LastTestResults", lastTestResultsJson);
        }
        if (!string.IsNullOrWhiteSpace(lastMusicTestFile))
        {
            setList.Add("LastMusicTestFile = @LastMusicTestFile");
            parameters.Add("LastMusicTestFile", lastMusicTestFile);
        }
        if (!string.IsNullOrWhiteSpace(lastSoundTestFile))
        {
            setList.Add("LastSoundTestFile = @LastSoundTestFile");
            parameters.Add("LastSoundTestFile", lastSoundTestFile);
        }
        if (!string.IsNullOrWhiteSpace(lastTtsTestFile))
        {
            setList.Add("LastTtsTestFile = @LastTtsTestFile");
            parameters.Add("LastTtsTestFile", lastTtsTestFile);
        }
        // Accept optional TTS file path (backwards compatible: method signature unchanged for callers that don't pass it)
        if (skillFlags != null && skillFlags.ContainsKey("SkillTtsSynthesize") && skillFlags.TryGetValue("SkillTtsSynthesize", out var _))
        {
            // noop: presence of flag is not the file; keep compatibility
        }

        // If caller passed a dynamic parameter named LastTtsTestFile, it will be included by callers via the method overload below.

        if (skillFlags != null)
        {
            foreach (var kvp in skillFlags)
            {
                if (!SkillColumnSet.Contains(kvp.Key)) continue;
                setList.Add($"{kvp.Key} = @{kvp.Key}");
                parameters.Add(kvp.Key, kvp.Value.HasValue ? (kvp.Value.Value ? 1 : 0) : (int?)null);
            }
        }

        parameters.Add("Name", modelName);
        var sql = $"UPDATE models SET {string.Join(", ", setList)} WHERE Name = @Name";
        conn.Execute(sql, parameters);

        // Also persist into normalized runs/steps if JSON of per-step results is provided.
        if (!string.IsNullOrWhiteSpace(lastTestResultsJson))
        {
            try
            {
                var runId = CreateTestRun(modelName, "ADHOC", "Ad-hoc test run", true, testDurationSeconds.HasValue ? (long?)(testDurationSeconds.Value * 1000) : null, "Saved from UpdateModelTestResults");
                using var doc = System.Text.Json.JsonDocument.Parse(lastTestResultsJson);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    int idx = 0;
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        idx++;
                        var nameStep = el.TryGetProperty("name", out var pn) ? pn.GetString() ?? string.Empty : string.Empty;
                        var ok = el.TryGetProperty("ok", out var pok) && pok.GetBoolean();
                        var message = el.TryGetProperty("message", out var pm) ? pm.GetString() : null;
                        var dur = el.TryGetProperty("durationSeconds", out var pdur) && pdur.TryGetDouble(out var dd) ? (long?)(dd * 1000) : null;
                        var stepId = AddTestStep(runId, idx, nameStep, null);
                        UpdateTestStepResult(stepId, ok, message != null ? System.Text.Json.JsonSerializer.Serialize(new { message }) : null, ok ? null : message, dur);
                    }
                }
            }
            catch
            {
                // swallow errors - DB update already performed for compatibility
            }
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

    public int AddTestAsset(int stepId, string fileType, string filePath, string? description = null, double? durationSec = null, long? sizeBytes = null)
    {
        using var conn = CreateConnection();
        conn.Open();
        var sql = @"INSERT INTO model_test_assets(step_id, file_type, file_path, description, duration_sec, size_bytes) VALUES(@step_id, @file_type, @file_path, @description, @duration_sec, @size_bytes); SELECT last_insert_rowid();";
        var id = conn.ExecuteScalar<long>(sql, new { step_id = stepId, file_type = fileType, file_path = filePath, description, duration_sec = durationSec, size_bytes = sizeBytes });
        return (int)id;
    }

    private void InitializeSchema()
    {
        using var conn = CreateConnection();
        conn.Open();

        var skillColumnsSql = string.Join(",\n    ", SkillColumns.Select(c => $"{c} INTEGER DEFAULT NULL"));

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
    TestDurationSeconds REAL,
    {skillColumnsSql}
);";
        cmd.ExecuteNonQuery();

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
        foreach (var col in SkillColumns)
        {
            if (existingCols.Contains(col)) continue;
            using var add = conn.CreateCommand();
            add.CommandText = $"ALTER TABLE models ADD COLUMN {col} INTEGER DEFAULT NULL;";
            add.ExecuteNonQuery();
        }
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
    return string.Join(", ", new[] { "Name","Provider","Endpoint","IsLocal","MaxContext","ContextToUse","FunctionCallingScore","CostInPerToken","CostOutPerToken","LimitTokensDay","LimitTokensWeek","LimitTokensMonth","Metadata","Enabled","CreatedAt","UpdatedAt","TestDurationSeconds","LastTestResults","LastMusicTestFile","LastSoundTestFile","LastTtsTestFile" }.Concat(SkillColumns));
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
