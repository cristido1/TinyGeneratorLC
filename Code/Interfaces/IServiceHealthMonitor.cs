using System;
using System.Threading;
using System.Threading.Tasks;

namespace TinyGenerator.Services;

/// <summary>
/// Service for checking health status of external services (AudioCraft, Ollama, etc.).
/// Centralizes HTTP health check logic with configurable timeouts.
/// </summary>
public interface IServiceHealthMonitor
{
    /// <summary>
    /// Check if AudioCraft service is healthy and responding.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if service is healthy, false otherwise</returns>
    Task<bool> CheckAudioCraftHealthAsync(CancellationToken cancellationToken = default);
}
