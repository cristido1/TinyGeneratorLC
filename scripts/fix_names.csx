using System;
using System.Data.SQLite;

var connectionString = "Data Source=data/storage.db";
using var connection = new SQLiteConnection(connectionString);
connection.Open();

var command = connection.CreateCommand();
command.CommandText = "UPDATE tts_voices SET name = voice_id WHERE name = 'Claribel Dervia'";
var affected = command.ExecuteNonQuery();

Console.WriteLine($"Fixed {affected} records with duplicate names.");
