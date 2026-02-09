using System.Threading;
using System.Threading.Tasks;

namespace TinyGenerator.Services
{
    public interface IMemoryEmbeddingGenerator
    {
        Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default);
    }
}
