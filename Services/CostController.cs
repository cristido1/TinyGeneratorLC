using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TinyGenerator.Services;

public sealed class CostController
{
    private readonly object _lock = new();
    private readonly string _dbPath;
    private readonly string _connectionString;

    // per modello costo per 1000 token (esempio). Aggiorna in base al provider usato.
    private readonly Dictionary<string, double> _costPerK = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4"] = 0.06,
        ["gpt-3.5"] = 0.002,
        ["ollama"] = 0.0 // local ollama - zero cost by default
    };

    public long MaxTokensPerRun { get; init; } = 200000;
    public double MaxCostPerMonth { get; init; } = 50.0;

    private readonly ITokenizer? _tokenizer;

    public CostController(ITokenizer? tokenizer = null, string dbPath = "data/storage.db")
    {
        _tokenizer = tokenizer;
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");
        _connectionString = $"Data Source={_dbPath}";
        InitializeDb();
    }

    private void InitializeDb()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS usage_state (
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

-- table for model metadata/configuration
CREATE TABLE IF NOT EXISTS modelli (
  name TEXT PRIMARY KEY,
  provider TEXT,
  is_local INTEGER DEFAULT 1,
  max_context INTEGER DEFAULT 4096,
  context_to_use INTEGER DEFAULT 4096,
  cost_in_per_token REAL DEFAULT 0,
  cost_out_per_token REAL DEFAULT 0,
  limit_tokens_day INTEGER DEFAULT 0,
  limit_tokens_week INTEGER DEFAULT 0,
  limit_tokens_month INTEGER DEFAULT 0,
  metadata TEXT,
  enabled INTEGER DEFAULT 1,
  created_at TEXT,
  updated_at TEXT
);
";
        cmd.ExecuteNonQuery();
    }

    // Simple model info container
    public class ModelInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public bool IsLocal { get; set; }
        public int MaxContext { get; set; }
        public int ContextToUse { get; set; }
        public double CostInPerToken { get; set; }
        public double CostOutPerToken { get; set; }
        public long LimitTokensDay { get; set; }
        public long LimitTokensWeek { get; set; }
        public long LimitTokensMonth { get; set; }
        public string Metadata { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }

    // Lookup model info by exact name first, then by provider fallback
    public ModelInfo? GetModelInfo(string modelOrProvider)
    {
        if (string.IsNullOrWhiteSpace(modelOrProvider)) return null;
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            // try exact name
            cmd.CommandText = "SELECT name, provider, is_local, max_context, context_to_use, cost_in_per_token, cost_out_per_token, limit_tokens_day, limit_tokens_week, limit_tokens_month, metadata, enabled FROM modelli WHERE name = $n LIMIT 1";
            cmd.Parameters.AddWithValue("$n", modelOrProvider);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                return new ModelInfo
                {
                    Name = r.GetString(0),
                    Provider = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                    IsLocal = !r.IsDBNull(2) && r.GetInt32(2) != 0,
                    MaxContext = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                    ContextToUse = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    CostInPerToken = r.IsDBNull(5) ? 0.0 : r.GetDouble(5),
                    CostOutPerToken = r.IsDBNull(6) ? 0.0 : r.GetDouble(6),
                    LimitTokensDay = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                    LimitTokensWeek = r.IsDBNull(8) ? 0 : r.GetInt64(8),
                    LimitTokensMonth = r.IsDBNull(9) ? 0 : r.GetInt64(9),
                    Metadata = r.IsDBNull(10) ? string.Empty : r.GetString(10),
                    Enabled = r.IsDBNull(11) ? true : r.GetInt32(11) != 0
                };
            }

            // fallback: try provider match
            cmd.Parameters.Clear();
            cmd.CommandText = "SELECT name, provider, is_local, max_context, context_to_use, cost_in_per_token, cost_out_per_token, limit_tokens_day, limit_tokens_week, limit_tokens_month, metadata, enabled FROM modelli WHERE provider = $p LIMIT 1";
            cmd.Parameters.AddWithValue("$p", modelOrProvider);
            using var r2 = cmd.ExecuteReader();
            if (r2.Read())
            {
                return new ModelInfo
                {
                    Name = r2.GetString(0),
                    Provider = r2.IsDBNull(1) ? string.Empty : r2.GetString(1),
                    IsLocal = !r2.IsDBNull(2) && r2.GetInt32(2) != 0,
                    MaxContext = r2.IsDBNull(3) ? 0 : r2.GetInt32(3),
                    ContextToUse = r2.IsDBNull(4) ? 0 : r2.GetInt32(4),
                    CostInPerToken = r2.IsDBNull(5) ? 0.0 : r2.GetDouble(5),
                    CostOutPerToken = r2.IsDBNull(6) ? 0.0 : r2.GetDouble(6),
                    LimitTokensDay = r2.IsDBNull(7) ? 0 : r2.GetInt64(7),
                    LimitTokensWeek = r2.IsDBNull(8) ? 0 : r2.GetInt64(8),
                    LimitTokensMonth = r2.IsDBNull(9) ? 0 : r2.GetInt64(9),
                    Metadata = r2.IsDBNull(10) ? string.Empty : r2.GetString(10),
                    Enabled = r2.IsDBNull(11) ? true : r2.GetInt32(11) != 0
                };
            }
            return null;
        }
    }

    // Insert or update a model config
    public void UpsertModel(ModelInfo m)
    {
        if (m == null) return;
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO modelli(name, provider, is_local, max_context, context_to_use, cost_in_per_token, cost_out_per_token, limit_tokens_day, limit_tokens_week, limit_tokens_month, metadata, enabled, created_at, updated_at)
VALUES($name,$provider,$is_local,$max_context,$context_to_use,$cost_in,$cost_out,$limd,$limw,$limm,$meta,$enabled,$now,$now)
ON CONFLICT(name) DO UPDATE SET provider=$provider, is_local=$is_local, max_context=$max_context, context_to_use=$context_to_use, cost_in_per_token=$cost_in, cost_out_per_token=$cost_out, limit_tokens_day=$limd, limit_tokens_week=$limw, limit_tokens_month=$limm, metadata=$meta, enabled=$enabled, updated_at=$now";
            var now = DateTime.UtcNow.ToString("o");
            cmd.Parameters.AddWithValue("$name", m.Name ?? string.Empty);
            cmd.Parameters.AddWithValue("$provider", m.Provider ?? string.Empty);
            cmd.Parameters.AddWithValue("$is_local", m.IsLocal ? 1 : 0);
            cmd.Parameters.AddWithValue("$max_context", m.MaxContext);
            cmd.Parameters.AddWithValue("$context_to_use", m.ContextToUse);
            cmd.Parameters.AddWithValue("$cost_in", m.CostInPerToken);
            cmd.Parameters.AddWithValue("$cost_out", m.CostOutPerToken);
            cmd.Parameters.AddWithValue("$limd", m.LimitTokensDay);
            cmd.Parameters.AddWithValue("$limw", m.LimitTokensWeek);
            cmd.Parameters.AddWithValue("$limm", m.LimitTokensMonth);
            cmd.Parameters.AddWithValue("$meta", m.Metadata ?? string.Empty);
            cmd.Parameters.AddWithValue("$enabled", m.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
    }

    // List all models
    public List<ModelInfo> ListModels()
    {
        var outList = new List<ModelInfo>();
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, provider, is_local, max_context, context_to_use, cost_in_per_token, cost_out_per_token, limit_tokens_day, limit_tokens_week, limit_tokens_month, metadata, enabled FROM modelli";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                outList.Add(new ModelInfo
                {
                    Name = r.GetString(0),
                    Provider = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                    IsLocal = !r.IsDBNull(2) && r.GetInt32(2) != 0,
                    MaxContext = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                    ContextToUse = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    CostInPerToken = r.IsDBNull(5) ? 0.0 : r.GetDouble(5),
                    CostOutPerToken = r.IsDBNull(6) ? 0.0 : r.GetDouble(6),
                    LimitTokensDay = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                    LimitTokensWeek = r.IsDBNull(8) ? 0 : r.GetInt64(8),
                    LimitTokensMonth = r.IsDBNull(9) ? 0 : r.GetInt64(9),
                    Metadata = r.IsDBNull(10) ? string.Empty : r.GetString(10),
                    Enabled = r.IsDBNull(11) ? true : r.GetInt32(11) != 0
                });
            }
        }
        return outList;
    }

    // semplice euristica: 1 token ~ 4 caratteri
    public int EstimateTokensFromText(string text)
    {
        try
        {
            if (_tokenizer != null) return Math.Max(1, _tokenizer.CountTokens(text));
        }
        catch { }
        return Math.Max(1, text?.Length / 4 ?? 1);
    }

    // Estimate cost given explicit input/output token counts. Uses model config if available.
    public double EstimateCost(string model, int inputTokens, int outputTokens)
    {
        // try to read model info (full name or provider)
        var mi = GetModelInfo(model);
        if (mi != null)
        {
            // cost fields are per-token
            return inputTokens * mi.CostInPerToken + outputTokens * mi.CostOutPerToken;
        }

        // fallback: use provider-level rates (per 1000 tokens stored in _costPerK)
        var provider = model.Split(':')[0];
        var rate = _costPerK.ContainsKey(provider) ? _costPerK[provider] : 0.01;
        var total = inputTokens + outputTokens;
        return (total / 1000.0) * rate;
    }

    // Backward-compatible overload: estimate cost given a single tokens value (assume half/half)
    public double EstimateCost(string model, int tokens)
    {
        var half = tokens / 2;
        return EstimateCost(model, half, tokens - half);
    }

    // verifica e riserva tokens per questa chiamata; ritorna false se non consentito
    // Reserve tokens for a call. Accepts input and output tokens counts.
    public bool TryReserve(string model, int inputTokens, int outputTokens)
    {
        lock (_lock)
        {
            var nowMonth = DateTime.UtcNow.ToString("yyyy-MM");
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            // ensure row exists for this month
            using (var ins = conn.CreateCommand())
            {
                ins.CommandText = "INSERT OR IGNORE INTO usage_state(month, tokens_this_run, tokens_this_month, cost_this_month) VALUES($m, 0, 0, 0)";
                ins.Parameters.AddWithValue("$m", nowMonth);
                ins.ExecuteNonQuery();
            }

            long tokensThisRun = 0; long tokensThisMonth = 0; double costThisMonth = 0;
            using (var sel = conn.CreateCommand())
            {
                sel.CommandText = "SELECT tokens_this_run, tokens_this_month, cost_this_month FROM usage_state WHERE month = $m";
                sel.Parameters.AddWithValue("$m", nowMonth);
                using var r = sel.ExecuteReader();
                if (r.Read())
                {
                    tokensThisRun = r.GetInt64(0);
                    tokensThisMonth = r.GetInt64(1);
                    costThisMonth = r.GetDouble(2);
                }
            }

            var estimatedCost = EstimateCost(model, inputTokens, outputTokens);
            var tokens = inputTokens + outputTokens;
            if (tokensThisRun + tokens > MaxTokensPerRun) return false;
            if (costThisMonth + estimatedCost > MaxCostPerMonth) return false;

            // update
            using (var upd = conn.CreateCommand())
            {
                upd.CommandText = "UPDATE usage_state SET tokens_this_run = tokens_this_run + $t, tokens_this_month = tokens_this_month + $t, cost_this_month = cost_this_month + $c WHERE month = $m";
                upd.Parameters.AddWithValue("$t", tokens);
                upd.Parameters.AddWithValue("$c", estimatedCost);
                upd.Parameters.AddWithValue("$m", nowMonth);
                upd.ExecuteNonQuery();
            }

            return true;
        }
    }

    public void RecordCall(string model, int inputTokens, int outputTokens, double cost, string request, string response)
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO calls(ts, model, provider, input_tokens, output_tokens, tokens, cost, request, response) VALUES($ts, $m, $p, $in, $out, $t, $c, $req, $res)";
            var provider = model.Split(':')[0];
            var total = inputTokens + outputTokens;
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$m", model);
            cmd.Parameters.AddWithValue("$p", provider);
            cmd.Parameters.AddWithValue("$in", inputTokens);
            cmd.Parameters.AddWithValue("$out", outputTokens);
            cmd.Parameters.AddWithValue("$t", total);
            cmd.Parameters.AddWithValue("$c", cost);
            cmd.Parameters.AddWithValue("$req", request ?? string.Empty);
            cmd.Parameters.AddWithValue("$res", response ?? string.Empty);
            cmd.ExecuteNonQuery();
        }
    }

    public void ResetRunCounters()
    {
        lock (_lock)
        {
            var nowMonth = DateTime.UtcNow.ToString("yyyy-MM");
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE usage_state SET tokens_this_run = 0 WHERE month = $m";
            cmd.Parameters.AddWithValue("$m", nowMonth);
            cmd.ExecuteNonQuery();
        }
    }

    // utility: read latest usage summary
    public (long tokensThisMonth, double costThisMonth) GetMonthUsage()
    {
        lock (_lock)
        {
            var nowMonth = DateTime.UtcNow.ToString("yyyy-MM");
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT tokens_this_month, cost_this_month FROM usage_state WHERE month = $m";
            sel.Parameters.AddWithValue("$m", nowMonth);
            using var r = sel.ExecuteReader();
            if (r.Read()) return (r.GetInt64(0), r.GetDouble(1));
            return (0, 0.0);
        }
    }

    // Retrieve recent calls (for admin UI)
    public List<CallRecord> GetRecentCalls(int limit = 50)
    {
        var list = new List<CallRecord>();
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, ts, model, provider, input_tokens, output_tokens, tokens, cost, request, response FROM calls ORDER BY id DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new CallRecord
                {
                    Id = r.GetInt64(0),
                    Timestamp = r.GetString(1),
                    Model = r.GetString(2),
                    Provider = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                    InputTokens = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    OutputTokens = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                    Tokens = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                    Cost = r.IsDBNull(7) ? 0.0 : r.GetDouble(7),
                    Request = r.IsDBNull(8) ? string.Empty : r.GetString(8),
                    Response = r.IsDBNull(9) ? string.Empty : r.GetString(9)
                });
            }
        }
        return list;
    }

    public class CallRecord
    {
        public long Id { get; set; }
        public string Timestamp { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int Tokens { get; set; }
        public double Cost { get; set; }
        public string Request { get; set; } = string.Empty;
        public string Response { get; set; } = string.Empty;
    }
}
