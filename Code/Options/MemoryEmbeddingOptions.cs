namespace TinyGenerator.Services
{
    public class MemoryEmbeddingOptions
    {
        /// <summary>
        /// Embedding model name exposed by Ollama (default: nomic-embed-text:latest).
        /// </summary>
        public string Model { get; set; } = "nomic-embed-text:latest";

        /// <summary>
        /// Optional custom endpoint, falls back to Ollama:endpoint or http://localhost:11434.
        /// </summary>
        public string? Endpoint { get; set; }

        /// <summary>
        /// Number of records processed per batch inside the backfill worker.
        /// </summary>
        public int BackfillBatchSize { get; set; } = 4;

        /// <summary>
        /// HTTP timeout (in seconds) for the embedding request against Ollama.
        /// </summary>
        public int RequestTimeoutSeconds { get; set; } = 120;

        /// <summary>
        /// Dimensionality of the embedding vectors (nomic-embed-text defaults to 768).
        /// </summary>
        public int EmbeddingDimension { get; set; } = 768;

        /// <summary>
        /// Optional absolute path to the sqlite-vec extension (.dylib/.so/.dll). When set, the service will load it and use the vec0 virtual table.
        /// </summary>
        public string? SqliteVecExtensionPath { get; set; }
    }
}
