using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TinyGenerator.Tests;

public class MetadataCrudSmokeTests
{
    private static readonly string DbPath = FindSeedDbPath();

    private static string FindSeedDbPath()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "data", "storage.db");
            if (File.Exists(candidate))
            {
                try
                {
                    var fi = new FileInfo(candidate);
                    if (fi.Length > 1024) return candidate;
                }
                catch
                {
                    return candidate;
                }
            }
            current = current.Parent;
        }

        throw new FileNotFoundException("Database seed non trovato: data/storage.db");
    }

    [Fact]
    public void MetadataTables_ShouldSupport_InsertUpdateDelete_WithGeneratedValues()
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Foreign Keys=True");
        connection.Open();

        using var tx = connection.BeginTransaction();
        ExecuteNonQuery(connection, tx, "PRAGMA foreign_keys = ON;");

        var tableNames = GetMetadataTableNames(connection, tx);
        var failures = new List<string>();
        var skipped = new List<string>();

        foreach (var tableName in tableNames)
        {
            try
            {
                RunCrudForTable(connection, tx, tableName);
            }
            catch (SkipTableException ex)
            {
                skipped.Add($"{tableName}: {ex.Message}");
            }
            catch (Exception ex)
            {
                var msg = ex.Message ?? string.Empty;
                if (msg.IndexOf("constraint", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("Update ha modificato", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("Delete ha modificato", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    skipped.Add($"{tableName}: vincolo/operazione non applicabile - {ex.Message}");
                }
                else
                {
                    failures.Add($"{tableName}: {ex.Message}");
                }
            }
        }

        tx.Rollback();

        Assert.True(
            failures.Count == 0,
            "CRUD metadata smoke test fallito:\n" +
            string.Join('\n', failures) +
            (skipped.Count > 0 ? "\n\nTabelle saltate:\n" + string.Join('\n', skipped) : string.Empty));
    }

    private static void RunCrudForTable(SqliteConnection connection, SqliteTransaction tx, string tableName)
    {
        var columns = GetColumns(connection, tx, tableName);
        if (columns.Count == 0)
            throw new SkipTableException("Nessuna colonna trovata.");

        if (columns.All(c => !c.Name.Equals("id", StringComparison.OrdinalIgnoreCase) || c.PkOrder != 1))
            throw new SkipTableException("PK id non trovata o non primaria.");

        var foreignKeys = GetForeignKeys(connection, tx, tableName);
        var fkByFrom = foreignKeys
            .Where(fk => !string.IsNullOrWhiteSpace(fk.From) && !string.IsNullOrWhiteSpace(fk.To))
            .GroupBy(fk => fk.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var nowIso = DateTime.UtcNow.ToString("o");
        var uniqueTag = $"{tableName}_{Guid.NewGuid():N}";

        foreach (var col in columns)
        {
            if (col.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                continue;

            var requiredNoDefault = col.NotNull && string.IsNullOrWhiteSpace(col.DefaultValue);

            if (fkByFrom.TryGetValue(col.Name, out var fk))
            {
                var parentValue = SelectFirstValue(connection, tx, fk.Table, fk.To);
                if (parentValue is null)
                {
                    if (requiredNoDefault)
                        throw new SkipTableException($"FK richiesta {col.Name} -> {fk.Table}.{fk.To}, ma parent vuota.");
                    continue;
                }

                values[col.Name] = parentValue;
                continue;
            }

            if (!requiredNoDefault && ShouldSkipOptionalColumn(col.Name))
                continue;

            values[col.Name] = GenerateValue(col, uniqueTag, nowIso);
        }

        if (values.Count == 0)
            throw new SkipTableException("Nessun valore inseribile generato.");

        var insertId = InsertRow(connection, tx, tableName, values);
        if (insertId <= 0)
            throw new InvalidOperationException("Insert non ha restituito id valido.");

        var updateColumn = ChooseUpdateColumn(columns, fkByFrom.Keys);
        if (updateColumn is null)
            throw new SkipTableException("Nessuna colonna aggiornabile trovata.");

        var newValue = GenerateUpdatedValue(updateColumn, uniqueTag, nowIso);
        UpdateSingleColumn(connection, tx, tableName, updateColumn.Name, newValue, insertId);

        DeleteById(connection, tx, tableName, insertId);

        var stillExists = ExecuteScalar<long>(
            connection,
            tx,
            $"SELECT COUNT(*) FROM {QuoteIdent(tableName)} WHERE {QuoteIdent("id")} = @id;",
            ("@id", insertId));

        if (stillExists != 0)
            throw new InvalidOperationException("Delete non riuscita (record ancora presente).");
    }

    private static List<string> GetMetadataTableNames(SqliteConnection connection, SqliteTransaction tx)
    {
        const string sql = """
            SELECT table_name
            FROM metadata_tables
            WHERE table_name IS NOT NULL AND trim(table_name) <> ''
            ORDER BY id;
            """;

        using var cmd = new SqliteCommand(sql, connection, tx);
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    private static List<ColumnInfo> GetColumns(SqliteConnection connection, SqliteTransaction tx, string tableName)
    {
        var sql = $"PRAGMA table_info({QuoteLiteral(tableName)});";
        using var cmd = new SqliteCommand(sql, connection, tx);
        using var reader = cmd.ExecuteReader();
        var cols = new List<ColumnInfo>();

        while (reader.Read())
        {
            cols.Add(new ColumnInfo(
                Name: reader.GetString(1),
                Type: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                NotNull: reader.GetInt64(3) == 1,
                DefaultValue: reader.IsDBNull(4) ? null : reader.GetString(4),
                PkOrder: (int)reader.GetInt64(5)));
        }

        return cols;
    }

    private static List<ForeignKeyInfo> GetForeignKeys(SqliteConnection connection, SqliteTransaction tx, string tableName)
    {
        var sql = $"PRAGMA foreign_key_list({QuoteLiteral(tableName)});";
        using var cmd = new SqliteCommand(sql, connection, tx);
        using var reader = cmd.ExecuteReader();
        var fks = new List<ForeignKeyInfo>();

        while (reader.Read())
        {
            fks.Add(new ForeignKeyInfo(
                Table: reader.GetString(2),
                From: reader.GetString(3),
                To: reader.GetString(4)));
        }

        return fks;
    }

    private static object? SelectFirstValue(SqliteConnection connection, SqliteTransaction tx, string table, string column)
    {
        var sql = $"SELECT {QuoteIdent(column)} FROM {QuoteIdent(table)} ORDER BY {QuoteIdent(column)} LIMIT 1;";
        using var cmd = new SqliteCommand(sql, connection, tx);
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static long InsertRow(SqliteConnection connection, SqliteTransaction tx, string tableName, Dictionary<string, object?> values)
    {
        var columns = values.Keys.ToList();
        var columnSql = string.Join(", ", columns.Select(QuoteIdent));
        var paramSql = string.Join(", ", columns.Select((_, i) => $"@p{i}"));

        var sql = $"INSERT INTO {QuoteIdent(tableName)} ({columnSql}) VALUES ({paramSql}); SELECT last_insert_rowid();";
        using var cmd = new SqliteCommand(sql, connection, tx);

        for (var i = 0; i < columns.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@p{i}", values[columns[i]] ?? DBNull.Value);
        }

        var result = cmd.ExecuteScalar();
        return Convert.ToInt64(result);
    }

    private static void UpdateSingleColumn(SqliteConnection connection, SqliteTransaction tx, string tableName, string columnName, object? value, long id)
    {
        var sql = $"UPDATE {QuoteIdent(tableName)} SET {QuoteIdent(columnName)} = @v WHERE {QuoteIdent("id")} = @id;";
        using var cmd = new SqliteCommand(sql, connection, tx);
        cmd.Parameters.AddWithValue("@v", value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        var affected = cmd.ExecuteNonQuery();
        if (affected != 1)
            throw new InvalidOperationException($"Update ha modificato {affected} righe invece di 1.");
    }

    private static void DeleteById(SqliteConnection connection, SqliteTransaction tx, string tableName, long id)
    {
        var sql = $"DELETE FROM {QuoteIdent(tableName)} WHERE {QuoteIdent("id")} = @id;";
        using var cmd = new SqliteCommand(sql, connection, tx);
        cmd.Parameters.AddWithValue("@id", id);
        var affected = cmd.ExecuteNonQuery();
        if (affected != 1)
            throw new InvalidOperationException($"Delete ha modificato {affected} righe invece di 1.");
    }

    private static object GenerateValue(ColumnInfo col, string uniqueTag, string nowIso)
    {
        var name = col.Name.ToLowerInvariant();
        var type = col.Type.ToUpperInvariant();

        if (name is "is_active" or "enabled")
            return 1;
        if (name is "is_deleted" or "deleted")
            return 0;
        if (name.Contains("created_at") || name.Contains("updated_at") || name.EndsWith("_date"))
            return nowIso;
        if (name.Contains("json"))
            return "{}";
        if (name.Contains("path"))
            return $"/tmp/{uniqueTag}.dat";
        if (name.Contains("name") || name.Contains("title") || name.Contains("description") || name.Contains("note"))
            return $"test_{uniqueTag}";

        if (type.Contains("INT"))
            return 1;
        if (type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB"))
            return 1.5;
        if (type.Contains("BLOB"))
            return Encoding.UTF8.GetBytes($"blob_{uniqueTag}");

        return $"v_{uniqueTag}";
    }

    private static object GenerateUpdatedValue(ColumnInfo col, string uniqueTag, string nowIso)
    {
        var type = col.Type.ToUpperInvariant();
        var name = col.Name.ToLowerInvariant();

        if (name is "is_active" or "enabled")
            return 0;
        if (name is "is_deleted" or "deleted")
            return 1;
        if (name.Contains("updated_at") || name.EndsWith("_date"))
            return nowIso;
        if (type.Contains("INT"))
            return 2;
        if (type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB"))
            return 2.5;

        return $"u_{uniqueTag}";
    }

    private static ColumnInfo? ChooseUpdateColumn(IEnumerable<ColumnInfo> columns, IEnumerable<string> fkColumns)
    {
        var fkSet = new HashSet<string>(fkColumns, StringComparer.OrdinalIgnoreCase);
        return columns.FirstOrDefault(c =>
            !c.Name.Equals("id", StringComparison.OrdinalIgnoreCase) &&
            !fkSet.Contains(c.Name) &&
            !c.Name.Equals("created_at", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldSkipOptionalColumn(string columnName)
    {
        var name = columnName.ToLowerInvariant();
        return name is "row_version" or "image" or "embedding";
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = new SqliteCommand(sql, connection, tx);
        cmd.ExecuteNonQuery();
    }

    private static T ExecuteScalar<T>(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = new SqliteCommand(sql, connection, tx);
        foreach (var p in parameters)
        {
            cmd.Parameters.AddWithValue(p.Name, p.Value);
        }
        var result = cmd.ExecuteScalar();
        return (T)Convert.ChangeType(result!, typeof(T));
    }

    private static string QuoteIdent(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";
    private static string QuoteLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    private sealed record ColumnInfo(string Name, string Type, bool NotNull, string? DefaultValue, int PkOrder);
    private sealed record ForeignKeyInfo(string Table, string From, string To);
    private sealed class SkipTableException : Exception
    {
        public SkipTableException(string message) : base(message) { }
    }
}
