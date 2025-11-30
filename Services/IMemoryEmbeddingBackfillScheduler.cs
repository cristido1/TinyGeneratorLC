namespace TinyGenerator.Services
{
    public interface IMemoryEmbeddingBackfillScheduler
    {
        void RequestBackfill(string reason = "manual");
    }
}
