using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Dapper;

namespace TinyGenerator.Services;

public sealed class StoriesService
{
    private readonly object _lock = new();
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly DatabaseService? _database;

    public StoriesService(string dbPath = "data/storage.db", DatabaseService? database = null)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");
        _connectionString = $"Data Source={_dbPath}";
        _database = database;
        InitializeDb();
    }

    private void InitializeDb()
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            // Check for existing 'stories' table schema and migrate if needed
            bool needsCreate = true;
            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='stories'";
                var exists = check.ExecuteScalar();
                if (exists != null)
                {
                    // inspect columns
                    using var colCmd = conn.CreateCommand();
                    colCmd.CommandText = "PRAGMA table_info(stories);";
                    using var reader = colCmd.ExecuteReader();
                    var cols = new List<string>();
                    while (reader.Read()) cols.Add(reader.GetString(1));
                    // if old-style 'split by writer' columns exist, perform migration
                    if (cols.Contains("story_a") || cols.Contains("story_b"))
                    {
                        // create new table with desired schema
                        using var create = conn.CreateCommand();
                        create.CommandText = @"
    CREATE TABLE IF NOT EXISTS stories_new (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        generation_id TEXT,
        memory_key TEXT,
        ts TEXT,
        prompt TEXT,
        story TEXT,
        eval TEXT,
        score REAL,
        approved INTEGER,
        status TEXT,
        model_id INTEGER NULL,
        agent_id INTEGER NULL
    );
    ";
                        create.ExecuteNonQuery();

                        // migrate old rows into new structure: for each old row, create two rows A and B
                        using var sel = conn.CreateCommand();
                        sel.CommandText = "SELECT id, ts, prompt, story_a, eval_a, score_a, story_b, eval_b, score_b, approved, status FROM stories";
                        using var r = sel.ExecuteReader();
                            while (r.Read())
                        {
                            var oldTs = r.IsDBNull(1) ? DateTime.UtcNow.ToString("o") : r.GetString(1);
                            var oldPrompt = r.IsDBNull(2) ? string.Empty : r.GetString(2);
                            var storyA = r.IsDBNull(3) ? string.Empty : r.GetString(3);
                            var evalA = r.IsDBNull(4) ? string.Empty : r.GetString(4);
                            var scoreA = r.IsDBNull(5) ? 0.0 : r.GetDouble(5);
                            var storyB = r.IsDBNull(6) ? string.Empty : r.GetString(6);
                            var evalB = r.IsDBNull(7) ? string.Empty : r.GetString(7);
                            var scoreB = r.IsDBNull(8) ? 0.0 : r.GetDouble(8);
                            var approved = r.IsDBNull(9) ? 0 : r.GetInt32(9);
                            var status = r.IsDBNull(10) ? string.Empty : r.GetString(10);

                            var genId = Guid.NewGuid().ToString();

                            using var insA = conn.CreateCommand();
                            insA.CommandText = "INSERT INTO stories_new(generation_id, memory_key, ts, prompt, story, eval, score, approved, status, model_id, agent_id) VALUES($gid,$mk,$ts,$p,$c,$e,$s,$ap,$st,$mid,$aid);";
                            insA.Parameters.AddWithValue("$gid", genId);
                            insA.Parameters.AddWithValue("$mk", genId);
                            insA.Parameters.AddWithValue("$ts", oldTs);
                            insA.Parameters.AddWithValue("$p", oldPrompt);
                            insA.Parameters.AddWithValue("$c", storyA);
                            // Attempt to convert model string to numeric model_id using DatabaseService if available
                            long? midA = null;
                            int? aidA = null;
                            try { if (!string.IsNullOrWhiteSpace(r.GetString(3)) && _database != null) midA = _database.GetModelIdByName(r.GetString(3)); } catch { }
                            insA.Parameters.AddWithValue("$mid", midA.HasValue ? (object)midA.Value : (object)DBNull.Value);
                            insA.Parameters.AddWithValue("$aid", aidA.HasValue ? (object)aidA.Value : (object)DBNull.Value);
                            insA.Parameters.AddWithValue("$e", evalA);
                            insA.Parameters.AddWithValue("$s", scoreA);
                            insA.Parameters.AddWithValue("$ap", approved);
                            insA.Parameters.AddWithValue("$st", status);
                            insA.ExecuteNonQuery();

                            using var insB = conn.CreateCommand();
                            insB.CommandText = "INSERT INTO stories_new(generation_id, memory_key, ts, prompt, story, eval, score, approved, status, model_id, agent_id) VALUES($gid,$mk,$ts,$p,$c,$e,$s,$ap,$st,$mid,$aid);";
                            insB.Parameters.AddWithValue("$gid", genId);
                            insB.Parameters.AddWithValue("$mk", genId);
                            insB.Parameters.AddWithValue("$ts", oldTs);
                            insB.Parameters.AddWithValue("$p", oldPrompt);
                            insB.Parameters.AddWithValue("$c", storyB);
                            long? midB = null;
                            int? aidB = null;
                            try { if (!string.IsNullOrWhiteSpace(r.GetString(6)) && _database != null) midB = _database.GetModelIdByName(r.GetString(6)); } catch { }
                            insB.Parameters.AddWithValue("$mid", midB.HasValue ? (object)midB.Value : (object)DBNull.Value);
                            insB.Parameters.AddWithValue("$aid", aidB.HasValue ? (object)aidB.Value : (object)DBNull.Value);
                            insB.Parameters.AddWithValue("$e", evalB);
                            insB.Parameters.AddWithValue("$s", scoreB);
                            insB.Parameters.AddWithValue("$ap", approved);
                            insB.Parameters.AddWithValue("$st", status);
                            insB.ExecuteNonQuery();
                        }

                        // drop old table and rename new
                        using var drop = conn.CreateCommand();
                        drop.CommandText = "DROP TABLE IF EXISTS stories;";
                        drop.ExecuteNonQuery();
                        using var rename = conn.CreateCommand();
                        rename.CommandText = "ALTER TABLE stories_new RENAME TO stories;";
                        rename.ExecuteNonQuery();
                    }
                    // if the table uses a 'writer' column (single row per writer per generation), migrate to model_id/agent_id
                    else if (cols.Contains("writer"))
                    {
                        using var createW = conn.CreateCommand();
                        createW.CommandText = @"
CREATE TABLE IF NOT EXISTS stories_new (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    generation_id TEXT,
    memory_key TEXT,
    ts TEXT,
    prompt TEXT,
    story TEXT,
    eval TEXT,
    score REAL,
    approved INTEGER,
    status TEXT,
    model_id INTEGER NULL,
    agent_id INTEGER NULL
);
";
                        createW.ExecuteNonQuery();

                        using var selW = conn.CreateCommand();
                        selW.CommandText = "SELECT id, ts, prompt, story, eval, score, writer, model, approved, status FROM stories";
                        using var rw = selW.ExecuteReader();
                        while (rw.Read())
                        {
                            var oldTs = rw.IsDBNull(1) ? DateTime.UtcNow.ToString("o") : rw.GetString(1);
                            var oldPrompt = rw.IsDBNull(2) ? string.Empty : rw.GetString(2);
                            var storyText = rw.IsDBNull(3) ? string.Empty : rw.GetString(3);
                            var evalText = rw.IsDBNull(4) ? string.Empty : rw.GetString(4);
                            var scoreVal = rw.IsDBNull(5) ? 0.0 : rw.GetDouble(5);
                            var writerFlag = rw.IsDBNull(6) ? string.Empty : rw.GetString(6);
                            var modelName = rw.IsDBNull(7) ? string.Empty : rw.GetString(7);
                            var approved = rw.IsDBNull(8) ? 0 : rw.GetInt32(8);
                            var status = rw.IsDBNull(9) ? string.Empty : rw.GetString(9);

                            var modelId = (long?)null;
                            var agentId = (int?)null;
                            try { if (!string.IsNullOrWhiteSpace(modelName) && _database != null) modelId = _database.GetModelIdByName(modelName); } catch { }
                            try {
                                if (!string.IsNullOrWhiteSpace(writerFlag) && _database != null)
                                {
                                    // map legacy writer letter 'A' -> agent name 'WriterA'
                                    string agentName = writerFlag switch { "A" => "WriterA", "B" => "WriterB", "C" => "WriterC", _ => null };
                                    if (!string.IsNullOrWhiteSpace(agentName)) agentId = _database.GetAgentIdByName(agentName);
                                }
                            } catch { }

                            using var ins = conn.CreateCommand();
                            ins.CommandText = "INSERT INTO stories_new(generation_id, memory_key, ts, prompt, story, eval, score, approved, status, model_id, agent_id) VALUES($gid,$mk,$ts,$p,$c,$e,$s,$ap,$st,$mid,$aid);";
                            ins.Parameters.AddWithValue("$gid", Guid.NewGuid().ToString());
                            ins.Parameters.AddWithValue("$mk", Guid.NewGuid().ToString());
                            ins.Parameters.AddWithValue("$ts", oldTs);
                            ins.Parameters.AddWithValue("$p", oldPrompt);
                            ins.Parameters.AddWithValue("$c", storyText);
                            ins.Parameters.AddWithValue("$e", evalText);
                            ins.Parameters.AddWithValue("$s", scoreVal);
                            ins.Parameters.AddWithValue("$ap", approved);
                            ins.Parameters.AddWithValue("$st", status);
                            ins.Parameters.AddWithValue("$mid", modelId.HasValue ? (object)modelId.Value : (object)DBNull.Value);
                            ins.Parameters.AddWithValue("$aid", agentId.HasValue ? (object)agentId.Value : (object)DBNull.Value);
                            ins.ExecuteNonQuery();
                        }

                        using var dropW = conn.CreateCommand();
                        dropW.CommandText = "DROP TABLE IF EXISTS stories;";
                        dropW.ExecuteNonQuery();
                        using var renameW = conn.CreateCommand();
                        renameW.CommandText = "ALTER TABLE stories_new RENAME TO stories;";
                        renameW.ExecuteNonQuery();
                    }
                    else
                    {
                        // existing table doesn't look like old schema; we'll leave it as-is and create if missing
                        needsCreate = false;
                    }
                }
            }

                // If an older version created a 'content' column (one row per writer already), add 'story' and 'model' columns and copy data over
                using (var check2 = conn.CreateCommand())
                {
                    check2.CommandText = "PRAGMA table_info(stories);";
                    using var reader2 = check2.ExecuteReader();
                    var cols2 = new List<string>();
                    while (reader2.Read()) cols2.Add(reader2.GetString(1));
                    if (cols2.Contains("content") && !cols2.Contains("story"))
                    {
                        using var add1 = conn.CreateCommand();
                        add1.CommandText = "ALTER TABLE stories ADD COLUMN story TEXT;";
                        add1.ExecuteNonQuery();
                        // Add numeric model_id column instead of text model
                        if (!cols2.Contains("model_id"))
                        {
                            try { using var add2 = conn.CreateCommand(); add2.CommandText = "ALTER TABLE stories ADD COLUMN model_id INTEGER NULL;"; add2.ExecuteNonQuery(); } catch { }
                        }
                        using var upd = conn.CreateCommand();
                        upd.CommandText = "UPDATE stories SET story = content WHERE story IS NULL OR story = ''";
                        upd.ExecuteNonQuery();
                    }
                    // Ensure memory_key column exists for linking to chapters. If missing, add it and populate with generation_id.
                    if (!cols2.Contains("memory_key"))
                    {
                        try
                        {
                            using var addMk = conn.CreateCommand();
                            addMk.CommandText = "ALTER TABLE stories ADD COLUMN memory_key TEXT;";
                            addMk.ExecuteNonQuery();
                        }
                        catch { }
                        try
                        {
                            using var updMk = conn.CreateCommand();
                            updMk.CommandText = "UPDATE stories SET memory_key = generation_id WHERE memory_key IS NULL OR memory_key = '';";
                            updMk.ExecuteNonQuery();
                        }
                        catch { }
                    }
                }

            if (needsCreate)
            {
                using var cmd = conn.CreateCommand();
                // New schema: one row per writer per generation. Rows are grouped by generation_id.
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS stories (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    generation_id TEXT,
    memory_key TEXT,
    ts TEXT,
    prompt TEXT,
    story TEXT,
    eval TEXT,
    score REAL,
    approved INTEGER,
    status TEXT,
    model_id INTEGER NULL,
    agent_id INTEGER NULL
);
";
                cmd.ExecuteNonQuery();
            }

            // chapters table for intermediate chapter storage
            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = @"
CREATE TABLE IF NOT EXISTS chapters (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  memory_key TEXT,
  chapter_number INTEGER,
  content TEXT,
  ts TEXT
);
";
            cmd2.ExecuteNonQuery();
        }
    }

    public void SaveChapter(string memoryKey, int chapterNumber, string content)
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO chapters(memory_key, chapter_number, content, ts) VALUES($mk,$cn,$c,$ts);";
            cmd.Parameters.AddWithValue("$mk", memoryKey ?? string.Empty);
            cmd.Parameters.AddWithValue("$cn", chapterNumber);
            cmd.Parameters.AddWithValue("$c", content ?? string.Empty);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    // SaveGeneration: create one generation_id and insert one row per writer (A and B) to preserve compatibility
    public long SaveGeneration(string prompt, StoryGeneratorService.GenerationResult r, string? memoryKey = null)
    {
        lock (_lock)
        {
            var genId = Guid.NewGuid().ToString();
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            // insert WriterA row (mapped to agent 'WriterA' where possible)
            using var cmdA = conn.CreateCommand();
            cmdA.CommandText = "INSERT INTO stories(generation_id, memory_key, ts, prompt, story, eval, score, approved, status, model_id, agent_id) VALUES($gid,$mk,$ts,$p,$c,$e,$s,$ap,$st,$mid,$aid);";
            cmdA.Parameters.AddWithValue("$gid", genId);
            cmdA.Parameters.AddWithValue("$mk", memoryKey ?? genId);
            cmdA.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmdA.Parameters.AddWithValue("$p", prompt ?? string.Empty);
            cmdA.Parameters.AddWithValue("$c", r.StoryA ?? string.Empty);
            var midA = (long?)null;
            var aidA = (int?)null;
            try { if (!string.IsNullOrWhiteSpace(r.ModelA) && _database != null) midA = _database.GetModelIdByName(r.ModelA); } catch { }
            try { if (_database != null) aidA = _database.GetAgentIdByName("WriterA"); } catch { }
            cmdA.Parameters.AddWithValue("$mid", midA.HasValue ? (object)midA.Value : (object)DBNull.Value);
            cmdA.Parameters.AddWithValue("$aid", aidA.HasValue ? (object)aidA.Value : (object)DBNull.Value);
            cmdA.Parameters.AddWithValue("$e", r.EvalA ?? string.Empty);
            cmdA.Parameters.AddWithValue("$s", r.ScoreA);
            cmdA.Parameters.AddWithValue("$ap", string.IsNullOrEmpty(r.Approved) ? 0 : 1);
            cmdA.Parameters.AddWithValue("$st", string.IsNullOrEmpty(r.Approved) ? "rejected" : "approved");
            cmdA.ExecuteNonQuery();

            // insert WriterB row (mapped to agent 'WriterB' where possible)
            using var cmdB = conn.CreateCommand();
            cmdB.CommandText = "INSERT INTO stories(generation_id, memory_key, ts, prompt, story, eval, score, approved, status, model_id, agent_id) VALUES($gid,$mk,$ts,$p,$c,$e,$s,$ap,$st,$mid,$aid); SELECT last_insert_rowid();";
            cmdB.Parameters.AddWithValue("$gid", genId);
            cmdB.Parameters.AddWithValue("$mk", memoryKey ?? genId);
            cmdB.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmdB.Parameters.AddWithValue("$p", prompt ?? string.Empty);
            cmdB.Parameters.AddWithValue("$c", r.StoryB ?? string.Empty);
            var midB = (long?)null;
            var aidB = (int?)null;
            try { if (!string.IsNullOrWhiteSpace(r.ModelB) && _database != null) midB = _database.GetModelIdByName(r.ModelB); } catch { }
            try { if (_database != null) aidB = _database.GetAgentIdByName("WriterB"); } catch { }
            cmdB.Parameters.AddWithValue("$mid", midB.HasValue ? (object)midB.Value : (object)DBNull.Value);
            cmdB.Parameters.AddWithValue("$aid", aidB.HasValue ? (object)aidB.Value : (object)DBNull.Value);
            cmdB.Parameters.AddWithValue("$e", r.EvalB ?? string.Empty);
            cmdB.Parameters.AddWithValue("$s", r.ScoreB);
            cmdB.Parameters.AddWithValue("$ap", string.IsNullOrEmpty(r.Approved) ? 0 : 1);
            cmdB.Parameters.AddWithValue("$st", string.IsNullOrEmpty(r.Approved) ? "rejected" : "approved");
            var scalar = cmdB.ExecuteScalar();
            var id = scalar == null ? 0L : Convert.ToInt64(scalar);
            // insert WriterC row if present in GenerationResult (WriterC is a single-shot writer)
            using var cmdC = conn.CreateCommand();
            cmdC.CommandText = "INSERT INTO stories(generation_id, memory_key, ts, prompt, story, eval, score, approved, status, model_id, agent_id) VALUES($gid,$mk,$ts,$p,$c,$e,$s,$ap,$st,$mid,$aid); SELECT last_insert_rowid();";
            cmdC.Parameters.AddWithValue("$gid", genId);
            cmdC.Parameters.AddWithValue("$mk", memoryKey ?? genId);
            cmdC.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmdC.Parameters.AddWithValue("$p", prompt ?? string.Empty);
            cmdC.Parameters.AddWithValue("$c", r.StoryC ?? string.Empty);
            var midC = (long?)null;
            var aidC = (int?)null;
            try { if (!string.IsNullOrWhiteSpace(r.ModelC) && _database != null) midC = _database.GetModelIdByName(r.ModelC); } catch { }
            try { if (_database != null) aidC = _database.GetAgentIdByName("WriterC"); } catch { }
            cmdC.Parameters.AddWithValue("$mid", midC.HasValue ? (object)midC.Value : (object)DBNull.Value);
            cmdC.Parameters.AddWithValue("$aid", aidC.HasValue ? (object)aidC.Value : (object)DBNull.Value);
            cmdC.Parameters.AddWithValue("$e", r.EvalC ?? string.Empty);
            cmdC.Parameters.AddWithValue("$s", r.ScoreC);
            cmdC.Parameters.AddWithValue("$ap", string.IsNullOrEmpty(r.Approved) ? 0 : 1);
            cmdC.Parameters.AddWithValue("$st", string.IsNullOrEmpty(r.Approved) ? "rejected" : "approved");
            var scalarC = cmdC.ExecuteScalar();
            var idC = scalarC == null ? id : Convert.ToInt64(scalarC);
            return idC;
        }
    }

    public List<StoryRecord> GetAllStories()
    {
        var list = new List<StoryRecord>();
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            // Aggregate rows by generation_id to reconstruct the old ViewModel: one record per generation
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
                list.Add(new StoryRecord
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
        }
        return list;
    }

    public void Delete(long id)
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            // find generation_id for this id, then delete all rows for that generation
            string? generationId = null;
            using (var cmdFind = conn.CreateCommand())
            {
                cmdFind.CommandText = "SELECT generation_id FROM stories WHERE id = $id LIMIT 1";
                cmdFind.Parameters.AddWithValue("$id", id);
                var scalar = cmdFind.ExecuteScalar();
                generationId = scalar == null ? null : Convert.ToString(scalar);
            }
            if (!string.IsNullOrEmpty(generationId))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM stories WHERE generation_id = $gid";
                cmd.Parameters.AddWithValue("$gid", generationId);
                cmd.ExecuteNonQuery();
            }
        }
    }

    public long InsertSingleStory(string prompt, string story, long? modelId = null, int? agentId = null, double score = 0.0, string? eval = null, int approved = 0, string? status = null, string? memoryKey = null)
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            var ts = DateTime.UtcNow.ToString("o");
            var genId = Guid.NewGuid().ToString();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO stories(generation_id, memory_key, ts, prompt, story, eval, score, approved, status, model_id, agent_id) VALUES($gid,$mk,$ts,$p,$c,$e,$s,$ap,$st,$mid,$aid); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$gid", genId);
            cmd.Parameters.AddWithValue("$mk", memoryKey ?? genId);
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$p", prompt ?? string.Empty);
            cmd.Parameters.AddWithValue("$c", story ?? string.Empty);
            cmd.Parameters.AddWithValue("$mid", modelId.HasValue ? (object)modelId.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$aid", agentId.HasValue ? (object)agentId.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$e", eval ?? string.Empty);
            cmd.Parameters.AddWithValue("$s", score);
            cmd.Parameters.AddWithValue("$ap", approved);
            cmd.Parameters.AddWithValue("$st", status ?? string.Empty);
            var scalar = cmd.ExecuteScalar();
            var id = scalar == null ? 0L : Convert.ToInt64(scalar);
            return id;
        }
    }

    public bool UpdateStoryById(long id, string? story = null, long? modelId = null, int? agentId = null, string? status = null)
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            // Build an UPDATE statement that only updates provided (non-null) fields
            var updates = new List<string>();
            if (story != null) updates.Add("story = $story");
            if (modelId.HasValue) updates.Add("model_id = $model_id");
            if (agentId.HasValue) updates.Add("agent_id = $agent_id");
            if (status != null) updates.Add("status = $status");
            if (updates.Count == 0) return false; // nothing to update
            var sql = $"UPDATE stories SET {string.Join(", ", updates)} WHERE id = $id";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            if (story != null) cmd.Parameters.AddWithValue("$story", story);
            if (modelId.HasValue) cmd.Parameters.AddWithValue("$model_id", modelId.Value);
            if (agentId.HasValue) cmd.Parameters.AddWithValue("$agent_id", agentId.Value);
            if (status != null) cmd.Parameters.AddWithValue("$status", status);
            var rows = cmd.ExecuteNonQuery();
            return rows > 0;
        }
    }

    public StoryRecord? GetStoryById(long id)
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT s.id AS Id, s.generation_id AS GenerationId, s.memory_key AS MemoryKey, s.ts AS Ts, s.prompt AS Prompt, s.story AS Story, m.name AS Model, s.eval AS Eval, s.score AS Score, s.approved AS Approved, s.status AS Status, a.name AS Agent FROM stories s LEFT JOIN models m ON s.model_id = m.id LEFT JOIN agents a ON s.agent_id = a.id WHERE s.id = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", id);
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read())
            {
                return new StoryRecord
                {
                    Id = rdr.IsDBNull(0) ? 0 : rdr.GetInt64(0),
                    MemoryKey = rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2),
                    Timestamp = rdr.IsDBNull(3) ? string.Empty : rdr.GetString(3),
                    Prompt = rdr.IsDBNull(4) ? string.Empty : rdr.GetString(4),
                    StoryA = rdr.IsDBNull(6) ? string.Empty : rdr.GetString(6),
                    ModelA = rdr.IsDBNull(7) ? string.Empty : rdr.GetString(7),
                    EvalA = rdr.IsDBNull(8) ? string.Empty : rdr.GetString(8),
                    ScoreA = rdr.IsDBNull(9) ? 0 : rdr.GetDouble(9),
                    Approved = !rdr.IsDBNull(10) && rdr.GetInt32(10) == 1,
                    Status = rdr.IsDBNull(11) ? string.Empty : rdr.GetString(11)
                };
            }
            return null;
        }
    }

    public class StoryRecord
    {
        public long Id { get; set; }
        public string MemoryKey { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string StoryA { get; set; } = string.Empty;
        public string EvalA { get; set; } = string.Empty;
        public double ScoreA { get; set; }
        public string ModelA { get; set; } = string.Empty;
        public string StoryB { get; set; } = string.Empty;
        public string EvalB { get; set; } = string.Empty;
        public double ScoreB { get; set; }
        public string ModelB { get; set; } = string.Empty;
        public string StoryC { get; set; } = string.Empty;
        public string EvalC { get; set; } = string.Empty;
        public double ScoreC { get; set; }
        public string ModelC { get; set; } = string.Empty;
        public bool Approved { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public class StoryEvaluationRecord
    {
        public long Id { get; set; }
        public string Ts { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string EvaluatorName { get; set; } = string.Empty;
        public double Score { get; set; }
        public string RawJson { get; set; } = string.Empty;
    }

    public List<StoryEvaluationRecord> GetEvaluationsForStory(long storyId)
    {
        var list = new List<StoryEvaluationRecord>();
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            try
            {
                var sql = "SELECT se.id AS Id, se.ts AS Ts, COALESCE(m.Name, '') AS Model, COALESCE(a.name, '') AS EvaluatorName, se.total_score AS Score, se.raw_json AS RawJson FROM stories_evaluations se LEFT JOIN models m ON se.model_id = m.rowid LEFT JOIN agents a ON se.agent_id = a.id WHERE se.story_id = @sid ORDER BY se.id";
                var rows = conn.Query(sql, new { sid = storyId });
                foreach (var r in rows)
                {
                    list.Add(new StoryEvaluationRecord
                    {
                        Id = (long)r.Id,
                        Ts = r.Ts ?? string.Empty,
                        Model = r.Model ?? string.Empty,
                        EvaluatorName = r.EvaluatorName ?? string.Empty,
                        Score = r.Score ?? 0.0,
                        RawJson = r.RawJson ?? string.Empty
                    });
                }
            }
            catch { }
        }
        return list;
    }
}
