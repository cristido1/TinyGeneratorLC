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
  tokens INTEGER,
  cost REAL,
  request TEXT,
  response TEXT
);
";
        cmd.ExecuteNonQuery();
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

    public double EstimateCost(string model, int tokens)
    {
        var rate = _costPerK.ContainsKey(model) ? _costPerK[model] : 0.01;
        return (tokens / 1000.0) * rate;
    }

    // verifica e riserva tokens per questa chiamata; ritorna false se non consentito
    public bool TryReserve(string model, int tokens)
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

            var estimatedCost = EstimateCost(model, tokens);
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

    public void RecordCall(string model, int tokens, double cost, string request, string response)
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO calls(ts, model, tokens, cost, request, response) VALUES($ts, $m, $t, $c, $req, $res)";
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$m", model);
            cmd.Parameters.AddWithValue("$t", tokens);
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
            cmd.CommandText = "SELECT id, ts, model, tokens, cost, request, response FROM calls ORDER BY id DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new CallRecord
                {
                    Id = r.GetInt64(0),
                    Timestamp = r.GetString(1),
                    Model = r.GetString(2),
                    Tokens = r.GetInt32(3),
                    Cost = r.GetDouble(4),
                    Request = r.IsDBNull(5) ? string.Empty : r.GetString(5),
                    Response = r.IsDBNull(6) ? string.Empty : r.GetString(6)
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
        public int Tokens { get; set; }
        public double Cost { get; set; }
        public string Request { get; set; } = string.Empty;
        public string Response { get; set; } = string.Empty;
    }
}
