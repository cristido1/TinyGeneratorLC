using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TinyGenerator.Configuration;

namespace TinyGenerator.Services;

/// <summary>
/// Implementation of IServiceHealthMonitor using IHttpClientFactory for efficient HTTP calls.
/// </summary>
public class ServiceHealthMonitor : IServiceHealthMonitor
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ServiceHealthMonitor> _logger;
    private readonly string _audioCraftHealthEndpoint;

    public ServiceHealthMonitor(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ServiceHealthMonitor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;

        // Load endpoint from config with fallback
        _audioCraftHealthEndpoint = configuration["Startup:AudioCraft:HealthEndpoint"] 
            ?? "http://localhost:8003/health";
    }

    public async Task<bool> CheckAudioCraftHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("AudioCraft");
            var response = await client.GetAsync(_audioCraftHealthEndpoint, cancellationToken);
            
            var isHealthy = response.IsSuccessStatusCode;
            
            if (isHealthy)
            {
                _logger.LogDebug("AudioCraft health check: OK");
            }
            else
            {
                _logger.LogDebug("AudioCraft health check failed: {StatusCode}", response.StatusCode);
            }
            
            return isHealthy;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "AudioCraft health check failed: HTTP error");
            return false;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogDebug(ex, "AudioCraft health check failed: timeout");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AudioCraft health check failed: unexpected error");
            return false;
        }
    }
}
