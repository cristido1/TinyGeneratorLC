using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TinyGenerator.Services;

public sealed class StoriesService
{
    private readonly object _lock = new();
    private readonly string _dbPath;
    private readonly string _connectionString;

    public StoriesService(string dbPath = "data/storage.db")
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");
        _connectionString = $"Data Source={_dbPath}";
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
                    // if old-style columns exist, perform migration
                    if (cols.Contains("story_a") || cols.Contains("story_b"))
                    {
                        // create new table with desired schema
                        using var create = conn.CreateCommand();
                        create.CommandText = @"
CREATE TABLE IF NOT EXISTS stories_new (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    generation_id TEXT,
    ts TEXT,
    prompt TEXT,
    writer TEXT,
    story TEXT,
    model TEXT,
    eval TEXT,
    score REAL,
    approved INTEGER,
    status TEXT
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
                            insA.CommandText = "INSERT INTO stories_new(generation_id, ts, prompt, writer, story, model, eval, score, approved, status) VALUES($gid,$ts,$p,'A',$c,$m,$e,$s,$ap,$st);";
                            insA.Parameters.AddWithValue("$gid", genId);
                            insA.Parameters.AddWithValue("$ts", oldTs);
                            insA.Parameters.AddWithValue("$p", oldPrompt);
                            insA.Parameters.AddWithValue("$c", storyA);
                            insA.Parameters.AddWithValue("$m", string.Empty);
                            insA.Parameters.AddWithValue("$e", evalA);
                            insA.Parameters.AddWithValue("$s", scoreA);
                            insA.Parameters.AddWithValue("$ap", approved);
                            insA.Parameters.AddWithValue("$st", status);
                            insA.ExecuteNonQuery();

                            using var insB = conn.CreateCommand();
                            insB.CommandText = "INSERT INTO stories_new(generation_id, ts, prompt, writer, story, model, eval, score, approved, status) VALUES($gid,$ts,$p,'B',$c,$m,$e,$s,$ap,$st);";
                            insB.Parameters.AddWithValue("$gid", genId);
                            insB.Parameters.AddWithValue("$ts", oldTs);
                            insB.Parameters.AddWithValue("$p", oldPrompt);
                            insB.Parameters.AddWithValue("$c", storyB);
                            insB.Parameters.AddWithValue("$m", string.Empty);
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
                        using var add2 = conn.CreateCommand();
                        add2.CommandText = "ALTER TABLE stories ADD COLUMN model TEXT;";
                        add2.ExecuteNonQuery();
                        using var upd = conn.CreateCommand();
                        upd.CommandText = "UPDATE stories SET story = content WHERE story IS NULL OR story = ''";
                        upd.ExecuteNonQuery();
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
    ts TEXT,
    prompt TEXT,
    writer TEXT,
    story TEXT,
    model TEXT,
    eval TEXT,
    score REAL,
    approved INTEGER,
    status TEXT
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
    public long SaveGeneration(string prompt, StoryGeneratorService.GenerationResult r)
    {
        lock (_lock)
        {
            var genId = Guid.NewGuid().ToString();
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            // insert WriterA row
            using var cmdA = conn.CreateCommand();
            cmdA.CommandText = "INSERT INTO stories(generation_id, ts, prompt, writer, story, model, eval, score, approved, status) VALUES($gid,$ts,$p,$w,$c,$m,$e,$s,$ap,$st);";
            cmdA.Parameters.AddWithValue("$gid", genId);
            cmdA.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmdA.Parameters.AddWithValue("$p", prompt ?? string.Empty);
            cmdA.Parameters.AddWithValue("$w", "A");
            cmdA.Parameters.AddWithValue("$c", r.StoryA ?? string.Empty);
            cmdA.Parameters.AddWithValue("$m", r.ModelA ?? string.Empty);
            cmdA.Parameters.AddWithValue("$e", r.EvalA ?? string.Empty);
            cmdA.Parameters.AddWithValue("$s", r.ScoreA);
            cmdA.Parameters.AddWithValue("$ap", string.IsNullOrEmpty(r.Approved) ? 0 : 1);
            cmdA.Parameters.AddWithValue("$st", string.IsNullOrEmpty(r.Approved) ? "rejected" : "approved");
            cmdA.ExecuteNonQuery();

            // insert WriterB row
            using var cmdB = conn.CreateCommand();
            cmdB.CommandText = "INSERT INTO stories(generation_id, ts, prompt, writer, story, model, eval, score, approved, status) VALUES($gid,$ts,$p,$w,$c,$m,$e,$s,$ap,$st); SELECT last_insert_rowid();";
            cmdB.Parameters.AddWithValue("$gid", genId);
            cmdB.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmdB.Parameters.AddWithValue("$p", prompt ?? string.Empty);
            cmdB.Parameters.AddWithValue("$w", "B");
            cmdB.Parameters.AddWithValue("$c", r.StoryB ?? string.Empty);
            cmdB.Parameters.AddWithValue("$m", r.ModelB ?? string.Empty);
            cmdB.Parameters.AddWithValue("$e", r.EvalB ?? string.Empty);
            cmdB.Parameters.AddWithValue("$s", r.ScoreB);
            cmdB.Parameters.AddWithValue("$ap", string.IsNullOrEmpty(r.Approved) ? 0 : 1);
            cmdB.Parameters.AddWithValue("$st", string.IsNullOrEmpty(r.Approved) ? "rejected" : "approved");
            var scalar = cmdB.ExecuteScalar();
            var id = scalar == null ? 0L : Convert.ToInt64(scalar);
            return id;
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
       MIN(id) as min_id,
       MIN(ts) as ts,
       MIN(prompt) as prompt,
      MAX(CASE WHEN writer='A' THEN story END) as story_a,
      MAX(CASE WHEN writer='A' THEN eval END) as eval_a,
      MAX(CASE WHEN writer='A' THEN score END) as score_a,
      MAX(CASE WHEN writer='A' THEN model END) as model_a,
      MAX(CASE WHEN writer='B' THEN story END) as story_b,
      MAX(CASE WHEN writer='B' THEN eval END) as eval_b,
      MAX(CASE WHEN writer='B' THEN score END) as score_b,
      MAX(CASE WHEN writer='B' THEN model END) as model_b,
       MAX(approved) as approved,
       MAX(status) as status
FROM stories
GROUP BY generation_id
ORDER BY min_id DESC
";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new StoryRecord
                {
                    Id = r.IsDBNull(1) ? 0 : r.GetInt64(1),
                    Timestamp = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                    Prompt = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                    StoryA = r.IsDBNull(4) ? string.Empty : r.GetString(4),
                    EvalA = r.IsDBNull(5) ? string.Empty : r.GetString(5),
                    ScoreA = r.IsDBNull(6) ? 0 : r.GetDouble(6),
                    ModelA = r.IsDBNull(7) ? string.Empty : r.GetString(7),
                    StoryB = r.IsDBNull(7) ? string.Empty : r.GetString(7),
                    EvalB = r.IsDBNull(8) ? string.Empty : r.GetString(8),
                    ScoreB = r.IsDBNull(9) ? 0 : r.GetDouble(9),
                    ModelB = r.IsDBNull(10) ? string.Empty : r.GetString(10),
                    Approved = !r.IsDBNull(11) && r.GetInt32(11) == 1,
                    Status = r.IsDBNull(12) ? string.Empty : r.GetString(12)
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

    public class StoryRecord
    {
        public long Id { get; set; }
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
        public bool Approved { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
