using System.Threading;
using System.Threading.Tasks;

namespace TinyGenerator.Services.Commands;

public interface IChunkProcessingService
{
    Task<ChunkProcessResult> ProcessAsync(ChunkProcessRequest request, CancellationToken ct);
}

