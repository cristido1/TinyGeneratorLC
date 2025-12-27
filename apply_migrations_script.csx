using System.Data;
using Microsoft.Data.Sqlite;

var dbPath = "data/storage.db";
var connection = new SqliteConnection($"Data Source={dbPath}");
connection.Open();

var sql = @"ALTER TABLE stories ADD COLUMN title TEXT;";
var command = connection.CreateCommand();
command.CommandText = sql;
try { command.ExecuteNonQuery(); Console.WriteLine("OK: title"); } catch (Exception ex) { Console.WriteLine($"Skip title: {ex.Message}"); }

sql = @"ALTER TABLE tts_voices ADD COLUMN disabled INTEGER NOT NULL DEFAULT 0;";
command.CommandText = sql;
try { command.ExecuteNonQuery(); Console.WriteLine("OK: disabled"); } catch (Exception ex) { Console.WriteLine($"Skip disabled: {ex.Message}"); }

sql = @"ALTER TABLE step_templates ADD COLUMN agent_id INTEGER;";
command.CommandText = sql;
try { command.ExecuteNonQuery(); Console.WriteLine("OK: agent_id"); } catch (Exception ex) { Console.WriteLine($"Skip agent_id: {ex.Message}"); }

sql = @"ALTER TABLE step_templates ADD COLUMN voice_id INTEGER;";
command.CommandText = sql;
try { command.ExecuteNonQuery(); Console.WriteLine("OK: voice_id"); } catch (Exception ex) { Console.WriteLine($"Skip voice_id: {ex.Message}"); }

sql = @"ALTER TABLE stories ADD COLUMN serie_id INTEGER;";
command.CommandText = sql;
try { command.ExecuteNonQuery(); Console.WriteLine("OK: serie_id"); } catch (Exception ex) { Console.WriteLine($"Skip serie_id: {ex.Message}"); }

sql = @"ALTER TABLE stories ADD COLUMN serie_episode INTEGER;";
command.CommandText = sql;
try { command.ExecuteNonQuery(); Console.WriteLine("OK: serie_episode"); } catch (Exception ex) { Console.WriteLine($"Skip serie_episode: {ex.Message}"); }

sql = @"CREATE INDEX IF NOT EXISTS IX_stories_serie_id ON stories(serie_id);";
command.CommandText = sql;
try { command.ExecuteNonQuery(); Console.WriteLine("OK: index"); } catch (Exception ex) { Console.WriteLine($"Skip index: {ex.Message}"); }

// Update history
var migrations = new[] {
    "20251220195450_AddStoryTitle",
    "20251221075622_AddDisabledToTtsVoices",
    "20251223064452_AddStepTemplateFields",
    "20251227074900_AddSerieFieldsToStories"
};

foreach (var m in migrations) {
    sql = $"INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('{m}', '10.0.0');";
    command.CommandText = sql;
    try { command.ExecuteNonQuery(); Console.WriteLine($"OK: {m}"); } catch (Exception ex) { Console.WriteLine($"Skip {m}: {ex.Message}"); }
}

connection.Close();
Console.WriteLine("All done!");
