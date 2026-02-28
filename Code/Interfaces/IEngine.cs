using System.Threading.Tasks;

namespace TinyGenerator.Services;

public interface IEngine
{
    string EngineName { get; }
    Task<EngineResult> RunAsync(EngineContext ctx);
}
