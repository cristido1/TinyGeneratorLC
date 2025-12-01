using System.Data;
using System.Linq;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Globalization;
using System.Threading;
using Microsoft.Extensions.Options;

namespace TinyGenerator.Services
{
    public class PersistentMemoryService
    {
        private readonly DatabaseService? _dbService;
        private readonly string _dbPath;
        private const int DefaultSearchCandidateLimit = 512;
        private readonly MemoryEmbeddingOptions? _embeddingOptions;
        private readonly ICustomLogger? _logger;
        private readonly string? _vecExtensionPath;
        private readonly int _embeddingDimensions;
        private bool _vecExtensionDisabled;
        private bool _vecTableCreated;
        private readonly object _vecLock = new();

        public event EventHandler<MemorySavedEventArgs>? MemorySaved;

        public PersistentMemoryService(
            string dbPath = "data/storage.db",
            IOptions<MemoryEmbeddingOptions>? embeddingOptions = null,
            ICustomLogger? logger = null,
            DatabaseService? database = null)
        {
            _dbPath = dbPath;
            _embeddingOptions = embeddingOptions?.Value;
            _logger = logger;
            _dbService = database;
            _vecExtensionPath = _embeddingOptions?.SqliteVecExtensionPath;
            _embeddingDimensions = Math.Max(1, _embeddingOptions?.EmbeddingDimension ?? 768);
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            Initialize();
        }

        private void Initialize()
        {
            if (_dbService == null) throw new InvalidOperationException("DatabaseService is required for PersistentMemoryService initialization");

            _dbService.WithSqliteConnection(conn =>
            {
                try
                {
                    using var fkCmd = conn.CreateCommand();
                    fkCmd.CommandText = "PRAGMA foreign_keys = ON;";
                    fkCmd.ExecuteNonQuery();
                }
                catch { }

                var cmd = conn.CreateCommand();
                cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Memory (
                Id TEXT PRIMARY KEY,
                Collection TEXT NOT NULL,
                TextValue TEXT NOT NULL,
                Metadata TEXT,
                model_id INTEGER NULL,
                agent_id INTEGER NULL,
                Embedding BLOB NULL,
                CreatedAt TEXT NOT NULL
            );
            ";
                cmd.ExecuteNonQuery();

                // Ensure required columns exist and perform best-effort migration if legacy 'agent' column exists.
                var cols = new List<string>();
                using (var colCmd = conn.CreateCommand())
                {
                    colCmd.CommandText = "PRAGMA table_info(Memory);";
                    using var rdr = colCmd.ExecuteReader();
                    while (rdr.Read()) cols.Add(rdr.GetString(1));
                }

                // If model_id or agent_id missing, add them
                var colsLower = cols.Select(c => c.ToLowerInvariant()).ToList();
                if (!colsLower.Contains("model_id"))
                {
                    try
                    {
                        using var addModel = conn.CreateCommand();
                        addModel.CommandText = "ALTER TABLE Memory ADD COLUMN model_id INTEGER NULL;";
                        addModel.ExecuteNonQuery();
                    }
                    catch { }
                }
                if (!colsLower.Contains("agent_id"))
                {
                    try
                    {
                        using var addAgentId = conn.CreateCommand();
                        addAgentId.CommandText = "ALTER TABLE Memory ADD COLUMN agent_id INTEGER NULL;";
                        addAgentId.ExecuteNonQuery();
                    }
                    catch { }
                }
                if (!colsLower.Contains("embedding"))
                {
                    try
                    {
                        using var addEmbedding = conn.CreateCommand();
                        addEmbedding.CommandText = "ALTER TABLE Memory ADD COLUMN Embedding BLOB NULL;";
                        addEmbedding.ExecuteNonQuery();
                    }
                    catch { }
                }

                // If legacy 'agent' column exists, migrate rows into a new table without 'agent' column.
                // Detect legacy 'agent' column case-insensitively and migrate it out
                if (colsLower.Contains("agent"))
                {
                    try
                    {
                        using var create = conn.CreateCommand();
                        create.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Memory_new (
                        Id TEXT PRIMARY KEY,
                        Collection TEXT NOT NULL,
                        TextValue TEXT NOT NULL,
                        Metadata TEXT,
                        model_id INTEGER NULL,
                        agent_id INTEGER NULL,
                        CreatedAt TEXT NOT NULL,
                        FOREIGN KEY (agent_id) REFERENCES agents(id) ON DELETE SET NULL ON UPDATE CASCADE
                    );
                    ";
                        create.ExecuteNonQuery();

                        // Copy data; attempt to convert agent to integer if possible, otherwise set NULL. (agent is legacy column)
                        using var copy = conn.CreateCommand();
                        copy.CommandText = @"
                    INSERT OR REPLACE INTO Memory_new (Id, Collection, TextValue, Metadata, model_id, agent_id, CreatedAt)
                    SELECT Id, Collection, TextValue, Metadata, model_id, CASE WHEN CAST(agent AS INTEGER) > 0 THEN CAST(agent AS INTEGER) ELSE NULL END, CreatedAt FROM Memory;
                    ";
                        copy.ExecuteNonQuery();

                        using var drop = conn.CreateCommand();
                        drop.CommandText = "DROP TABLE IF EXISTS Memory;";
                        drop.ExecuteNonQuery();

                        using var rename = conn.CreateCommand();
                        rename.CommandText = "ALTER TABLE Memory_new RENAME TO Memory;";
                        rename.ExecuteNonQuery();
                    }
                    catch { }
                }

                // Ensure the Memory table's foreign keys reference the current `agents` table
                try
                {
                    using var fkCheck = conn.CreateCommand();
                    fkCheck.CommandText = "PRAGMA foreign_key_list('Memory');";
                    using var fkR = fkCheck.ExecuteReader();
                    var needsFix = false;
                    while (fkR.Read())
                    {
                        var refTable = fkR.IsDBNull(2) ? string.Empty : fkR.GetString(2);
                        if (!string.Equals(refTable, "agents", StringComparison.OrdinalIgnoreCase)) { needsFix = true; break; }
                    }

                    if (needsFix)
                    {
                        try
                        {
                            using var off = conn.CreateCommand(); off.CommandText = "PRAGMA foreign_keys = OFF;"; off.ExecuteNonQuery();
                            using var createFix = conn.CreateCommand();
                            createFix.CommandText = @"CREATE TABLE IF NOT EXISTS Memory_new_fix (
                            Id TEXT PRIMARY KEY,
                            Collection TEXT NOT NULL,
                            TextValue TEXT NOT NULL,
                            Metadata TEXT,
                            model_id INTEGER NULL,
                            agent_id INTEGER NULL,
                            CreatedAt TEXT NOT NULL,
                            FOREIGN KEY (agent_id) REFERENCES agents(id) ON DELETE SET NULL ON UPDATE CASCADE
                        );";
                            createFix.ExecuteNonQuery();

                            using var copyFix = conn.CreateCommand();
                            copyFix.CommandText = @"INSERT OR REPLACE INTO Memory_new_fix (Id, Collection, TextValue, Metadata, model_id, agent_id, CreatedAt)
                            SELECT Id, Collection, TextValue, Metadata, model_id, agent_id, CreatedAt FROM Memory;";
                            copyFix.ExecuteNonQuery();

                            using var dropOld = conn.CreateCommand(); dropOld.CommandText = "DROP TABLE IF EXISTS Memory;"; dropOld.ExecuteNonQuery();
                            using var renameFix = conn.CreateCommand(); renameFix.CommandText = "ALTER TABLE Memory_new_fix RENAME TO Memory;"; renameFix.ExecuteNonQuery();
                            using var on = conn.CreateCommand(); on.CommandText = "PRAGMA foreign_keys = ON;"; on.ExecuteNonQuery();
                        }
                        catch { /* best-effort: ignore failures here */ }
                    }
                }
                catch { }

                TryEnsureVectorReady(conn);

                return true;
            });
        }

        private static string ComputeHash(string text)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }

        // ➕ Salva informazione testuale
        public async Task SaveAsync(string collection, string text, object? metadata = null, long? modelId = null, int? agentId = null)
        {
            var id = ComputeHash(collection + text);
            var json = metadata != null ? JsonSerializer.Serialize(metadata) : null;

            if (_dbService == null) throw new InvalidOperationException("DatabaseService is required for PersistentMemoryService");

            await _dbService.WithSqliteConnectionAsync<bool>(async connection =>
            {
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                INSERT OR REPLACE INTO Memory (Id, Collection, TextValue, Metadata, model_id, agent_id, Embedding, CreatedAt)
                VALUES ($id, $collection, $text, $metadata, $model_id, $agent_id, $embedding, datetime('now'))
            ";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$collection", collection);
                cmd.Parameters.AddWithValue("$text", text);
                cmd.Parameters.AddWithValue("$metadata", json ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$model_id", modelId.HasValue ? (object)modelId.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$agent_id", agentId.HasValue ? (object)agentId.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$embedding", DBNull.Value);

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                MemorySaved?.Invoke(this, new MemorySavedEventArgs(id, collection, text, modelId, agentId));
                return true;
            }).ConfigureAwait(false);
        }

        // 🔍 Cerca testo simile (ricerca semplice full-text LIKE)
        public async Task<List<string>> SearchAsync(string collection, string query, int limit = 5, long? modelId = null, int? agentId = null)
        {
            if (_dbService == null) throw new InvalidOperationException("DatabaseService is required for PersistentMemoryService");

            return await _dbService.WithSqliteConnectionAsync<List<string>>(async connection =>
            {
                var cmd = connection.CreateCommand();

                var idFilter = BuildIdFilter(modelId, agentId);
                cmd.CommandText = $@"
                SELECT TextValue FROM Memory
                WHERE Collection = $collection
                AND TextValue LIKE $query
                {idFilter}
                ORDER BY CreatedAt DESC
                LIMIT $limit;
            ";

                cmd.Parameters.AddWithValue("$collection", collection);
                cmd.Parameters.AddWithValue("$query", $"%{query}%");
                cmd.Parameters.AddWithValue("$limit", limit);
                if (modelId.HasValue) cmd.Parameters.AddWithValue("$model_id", modelId.Value);
                if (agentId.HasValue) cmd.Parameters.AddWithValue("$agent_id", agentId.Value);

                var results = new List<string>();
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    results.Add(reader.GetString(0));
                }

                return results;
            }).ConfigureAwait(false);
        }

        // 🧹 Cancella una voce
        public async Task DeleteAsync(string collection, string text, long? modelId = null, int? agentId = null)
        {
            var id = ComputeHash(collection + text);
            if (_dbService == null) throw new InvalidOperationException("DatabaseService is required for PersistentMemoryService");

            await _dbService.WithSqliteConnectionAsync<bool>(async connection =>
            {
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"DELETE FROM Memory WHERE Id = $id";
                cmd.Parameters.AddWithValue("$id", id);

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }

        // 🧾 Elenca tutte le collezioni
        public async Task<List<string>> GetCollectionsAsync()
        {
            if (_dbService == null) throw new InvalidOperationException("DatabaseService is required for PersistentMemoryService");

            return await _dbService.WithSqliteConnectionAsync<List<string>>(async connection =>
            {
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"SELECT DISTINCT Collection FROM Memory ORDER BY Collection;";
                var collections = new List<string>();
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    collections.Add(reader.GetString(0));
                }

                return collections;
            }).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<MemoryRecord>> GetMemoriesMissingEmbeddingAsync(int batchSize = 8)
        {
            if (batchSize <= 0) return Array.Empty<MemoryRecord>();
            if (_dbService == null) throw new InvalidOperationException("DatabaseService is required for PersistentMemoryService");

            return await _dbService.WithSqliteConnectionAsync<IReadOnlyList<MemoryRecord>>(async connection =>
            {
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                SELECT Id, Collection, TextValue, Metadata, CreatedAt
                FROM Memory
                WHERE Embedding IS NULL
                ORDER BY CreatedAt ASC
                LIMIT $limit;
            ";
                cmd.Parameters.AddWithValue("$limit", batchSize);

                var items = new List<MemoryRecord>();
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    items.Add(new MemoryRecord(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        ParseCreatedAt(reader, 4),
                        null));
                }

                return (IReadOnlyList<MemoryRecord>)items;
            }).ConfigureAwait(false);
        }

        public async Task UpdateEmbeddingAsync(string id, float[] embedding, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id required", nameof(id));
            if (embedding == null || embedding.Length == 0) throw new ArgumentException("Embedding required", nameof(embedding));
            if (_dbService == null) throw new InvalidOperationException("DatabaseService is required for PersistentMemoryService");

            await _dbService.WithSqliteConnectionAsync<bool>(async connection =>
            {
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"UPDATE Memory SET Embedding = $embedding WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$embedding", SerializeEmbedding(embedding));

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await TryUpsertVectorIndexAsync(connection, id, embedding, cancellationToken).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<MemorySearchResult>> SearchWithEmbeddingsAsync(
            string collection,
            string query,
            float[]? queryEmbedding,
            int limit = 5,
            long? modelId = null,
            int? agentId = null,
            int? maxCandidates = null)
        {
            if (string.IsNullOrWhiteSpace(collection)) throw new ArgumentException("Collection required", nameof(collection));
            if (limit <= 0) return Array.Empty<MemorySearchResult>();

            var candidateLimit = maxCandidates ?? Math.Max(DefaultSearchCandidateLimit, limit * 16);
            var normalizedQuery = query?.Trim() ?? string.Empty;

            if (queryEmbedding != null)
            {
                var vecResults = await TryVectorSearchAsync(collection, normalizedQuery, queryEmbedding, limit, modelId, agentId);
                if (vecResults.Count > 0)
                {
                    return vecResults;
                }
            }

            var candidates = await LoadCandidatesAsync(collection, modelId, agentId, candidateLimit);
            var results = new List<MemorySearchResult>(Math.Min(limit, candidates.Count));

            foreach (var candidate in candidates)
            {
                double embeddingScore = 0;
                if (queryEmbedding != null && candidate.Embedding != null)
                {
                    var cosine = CosineSimilarity(queryEmbedding, candidate.Embedding);
                    embeddingScore = (cosine + 1d) / 2d; // Normalize 0..1
                }

                var textScore = ComputeTextScore(candidate.TextValue, normalizedQuery);
                if (embeddingScore <= 0 && textScore <= 0 && !string.IsNullOrEmpty(normalizedQuery))
                {
                    // Candidate has no useful signal for this query, skip it.
                    continue;
                }

                var combinedScore = embeddingScore + textScore;
                results.Add(new MemorySearchResult(
                    candidate.Id,
                    candidate.TextValue,
                    candidate.Metadata,
                    candidate.Collection,
                    candidate.CreatedAt,
                    combinedScore,
                    embeddingScore,
                    textScore));
            }

            return results
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => r.CreatedAt)
                .Take(limit)
                .ToList();
        }

        private async Task<List<MemoryRecord>> LoadCandidatesAsync(string collection, long? modelId, int? agentId, int maxCandidates)
        {
            if (_dbService == null) throw new InvalidOperationException("DatabaseService is required for PersistentMemoryService");

            return await _dbService.WithSqliteConnectionAsync<List<MemoryRecord>>(async connection =>
            {
                var items = new List<MemoryRecord>();
                var cmd = connection.CreateCommand();
                var idFilter = BuildIdFilter(modelId, agentId);

                cmd.CommandText = $@"
                SELECT Id, Collection, TextValue, Metadata, Embedding, CreatedAt
                FROM Memory
                WHERE Collection = $collection
                {idFilter}
                ORDER BY CreatedAt DESC
                LIMIT $limit;
            ";
                cmd.Parameters.AddWithValue("$collection", collection);
                cmd.Parameters.AddWithValue("$limit", maxCandidates);
                if (modelId.HasValue) cmd.Parameters.AddWithValue("$model_id", modelId.Value);
                if (agentId.HasValue) cmd.Parameters.AddWithValue("$agent_id", agentId.Value);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    items.Add(new MemoryRecord(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        ParseCreatedAt(reader, 5),
                        TryReadEmbedding(reader, 4)));
                }

                return items;
            }).ConfigureAwait(false);
        }

        private async Task TryUpsertVectorIndexAsync(SqliteConnection connection, string id, float[] embedding, CancellationToken cancellationToken)
        {
            if (!TryEnsureVectorReady(connection))
            {
                return;
            }

            if (embedding.Length != _embeddingDimensions)
            {
                _logger?.Log("Warn", "MemoryEmbedding", $"Embedding dimension mismatch for {id}: expected {_embeddingDimensions}, got {embedding.Length}");
                return;
            }

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO MemoryVec(id, embedding) VALUES($id, vec_f32($vector));";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$vector", SerializeEmbeddingAsJson(embedding));

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task<IReadOnlyList<MemorySearchResult>> TryVectorSearchAsync(
            string collection,
            string normalizedQuery,
            float[] queryEmbedding,
            int limit,
            long? modelId,
            int? agentId)
        {
            if (_dbService == null) throw new InvalidOperationException("DatabaseService is required for PersistentMemoryService");

            return await _dbService.WithSqliteConnectionAsync<IReadOnlyList<MemorySearchResult>>(async connection =>
            {
                if (!TryEnsureVectorReady(connection))
                {
                    return Array.Empty<MemorySearchResult>();
                }

                if (queryEmbedding.Length != _embeddingDimensions)
                {
                    _logger?.Log("Warn", "MemoryEmbedding", $"Query embedding dimension mismatch: expected {_embeddingDimensions}, got {queryEmbedding.Length}");
                    return Array.Empty<MemorySearchResult>();
                }

                var results = new List<MemorySearchResult>();
                var cmd = connection.CreateCommand();
                var idFilter = BuildIdFilter(modelId, agentId, "m.");
                cmd.CommandText = $@"
                SELECT m.Id, m.TextValue, m.Metadata, m.Collection, m.CreatedAt, v.distance
                FROM MemoryVec v
                JOIN Memory m ON m.Id = v.id
                WHERE m.Collection = $collection
                {idFilter}
                AND v.embedding MATCH vec_f32($queryVector)
                ORDER BY v.distance ASC
                LIMIT $limit;
            ";
                cmd.Parameters.AddWithValue("$collection", collection);
                cmd.Parameters.AddWithValue("$queryVector", SerializeEmbeddingAsJson(queryEmbedding));
                cmd.Parameters.AddWithValue("$limit", limit);
                if (modelId.HasValue) cmd.Parameters.AddWithValue("$model_id", modelId.Value);
                if (agentId.HasValue) cmd.Parameters.AddWithValue("$agent_id", agentId.Value);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var id = reader.GetString(0);
                    var text = reader.GetString(1);
                    var metadata = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var collectionName = reader.GetString(3);
                    var createdAt = ParseCreatedAt(reader, 4);
                    var distanceRaw = reader.IsDBNull(5) ? 0d : reader.GetDouble(5);
                    var embeddingScore = 1d - Math.Clamp(distanceRaw, 0d, 2d);
                    var textScore = ComputeTextScore(text, normalizedQuery);
                    var combined = embeddingScore + textScore;
                    results.Add(new MemorySearchResult(
                        id,
                        text,
                        metadata,
                        collectionName,
                        createdAt,
                        combined,
                        embeddingScore,
                        textScore));
                }

                return results
                    .OrderByDescending(r => r.Score)
                    .ThenByDescending(r => r.CreatedAt)
                    .Take(limit)
                    .ToList();
            }).ConfigureAwait(false);
        }

        private bool TryEnsureVectorReady(SqliteConnection connection)
        {
            if (string.IsNullOrWhiteSpace(_vecExtensionPath) || _vecExtensionDisabled)
            {
                return false;
            }

            lock (_vecLock)
            {
                if (_vecExtensionDisabled)
                {
                    return false;
                }

                try
                {
                    connection.EnableExtensions(true);
                    connection.LoadExtension(_vecExtensionPath);
                    if (!_vecTableCreated)
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = $@"
                            CREATE VIRTUAL TABLE IF NOT EXISTS MemoryVec
                            USING vec0(
                                id TEXT PRIMARY KEY,
                                embedding FLOAT[{_embeddingDimensions}]
                            );
                        ";
                        cmd.ExecuteNonQuery();
                        _vecTableCreated = true;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    _vecExtensionDisabled = true;
                    _logger?.Log("Warn", "MemoryEmbedding", $"sqlite-vec unavailable: {ex.Message}", ex.ToString());
                    return false;
                }
            }
        }

        private static string BuildIdFilter(long? modelId, int? agentId, string? tableAlias = null)
        {
            var prefix = string.IsNullOrWhiteSpace(tableAlias) ? string.Empty : tableAlias;
            if (modelId.HasValue && agentId.HasValue)
            {
                return $"AND ({prefix}model_id = $model_id OR {prefix}agent_id = $agent_id) ";
            }
            if (modelId.HasValue)
            {
                return $"AND {prefix}model_id = $model_id ";
            }
            if (agentId.HasValue)
            {
                return $"AND {prefix}agent_id = $agent_id ";
            }
            return string.Empty;
        }

        private static byte[] SerializeEmbedding(float[] embedding)
        {
            var buffer = new byte[embedding.Length * sizeof(float)];
            Buffer.BlockCopy(embedding, 0, buffer, 0, buffer.Length);
            return buffer;
        }

        private static string SerializeEmbeddingAsJson(IReadOnlyList<float> embedding)
        {
            var sb = new StringBuilder(embedding.Count * 10);
            sb.Append('[');
            for (var i = 0; i < embedding.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(embedding[i].ToString("G9", CultureInfo.InvariantCulture));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static float[]? TryReadEmbedding(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal)) return null;
            var bytes = reader.GetFieldValue<byte[]>(ordinal);
            return DeserializeEmbedding(bytes);
        }

        private static float[] DeserializeEmbedding(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return Array.Empty<float>();
            var arr = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
            return arr;
        }

        private static DateTime ParseCreatedAt(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal)) return DateTime.MinValue;
            var raw = reader.GetString(ordinal);
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            {
                return dt;
            }
            if (DateTime.TryParse(raw, out dt))
            {
                return dt;
            }
            return DateTime.MinValue;
        }

        private static double CosineSimilarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
        {
            if (a.Count == 0 || b.Count == 0 || a.Count != b.Count) return 0;
            double dot = 0, magA = 0, magB = 0;
            for (var i = 0; i < a.Count; i++)
            {
                var av = a[i];
                var bv = b[i];
                dot += av * bv;
                magA += av * av;
                magB += bv * bv;
            }
            if (magA <= double.Epsilon || magB <= double.Epsilon) return 0;
            return dot / Math.Sqrt(magA * magB);
        }

        private static double ComputeTextScore(string text, string query)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(text)) return 0;
            var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 0;
            // Provide a small boost for results whose surface text matches the query
            var proximity = 1d - Math.Min(idx, Math.Max(0, text.Length - 1)) / Math.Max(1d, text.Length);
            return 0.15 + 0.25 * proximity;
        }
    }

    public sealed class MemorySavedEventArgs : EventArgs
    {
        public MemorySavedEventArgs(string id, string collection, string text, long? modelId, int? agentId)
        {
            Id = id;
            Collection = collection;
            Text = text;
            ModelId = modelId;
            AgentId = agentId;
        }

        public string Id { get; }
        public string Collection { get; }
        public string Text { get; }
        public long? ModelId { get; }
        public int? AgentId { get; }
    }

    public sealed record MemoryRecord(
        string Id,
        string Collection,
        string TextValue,
        string? Metadata,
        DateTime CreatedAt,
        float[]? Embedding);

    public sealed record MemorySearchResult(
        string Id,
        string Text,
        string? Metadata,
        string Collection,
        DateTime CreatedAt,
        double Score,
        double EmbeddingScore,
        double TextScore);
}
