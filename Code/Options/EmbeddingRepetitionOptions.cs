namespace TinyGenerator.Services;

public sealed class EmbeddingRepetitionOptions
{
    public int SentenceWindow { get; set; } = 5;
    public int MemorySize { get; set; } = 50;
    public int ChunkSize { get; set; } = 6;

    public double SentenceHigh { get; set; } = 0.90;
    public double SentenceMedium { get; set; } = 0.86;
    public double SentenceLow { get; set; } = 0.82;

    public double ChunkHigh { get; set; } = 0.88;
    public double HardFail { get; set; } = 0.92;

    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "nomic-embed-text";
    public int MaxParallelRequests { get; set; } = 4;
}
