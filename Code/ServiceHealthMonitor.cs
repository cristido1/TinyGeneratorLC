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
    private readonly ILogger<ServiceHealthMonitor> _logger;
    private readonly string _audioCraftHealthEndpoint;
    private readonly string _ttsHealthEndpoint;

    public ServiceHealthMonitor(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ServiceHealthMonitor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        // Load endpoint from config with fallback
        _audioCraftHealthEndpoint = configuration["Startup:AudioCraft:HealthEndpoint"] 
            ?? "http://localhost:8003/health";
        var ttsHost = Environment.GetEnvironmentVariable("TTS_HOST")
            ?? Environment.GetEnvironmentVariable("HOST")
            ?? "127.0.0.1";
        var ttsPortRaw = Environment.GetEnvironmentVariable("TTS_PORT")
            ?? Environment.GetEnvironmentVariable("PORT")
            ?? "8004";
        if (!int.TryParse(ttsPortRaw, out var ttsPort))
        {
            ttsPort = 8004;
        }
        _ttsHealthEndpoint = configuration["Startup:Tts:HealthEndpoint"]
            ?? $"http://{ttsHost}:{ttsPort}/health";
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

    public async Task<bool> CheckTtsHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("TTS");
            var response = await client.GetAsync(_ttsHealthEndpoint, cancellationToken);
            var isHealthy = response.IsSuccessStatusCode;

            if (isHealthy)
            {
                _logger.LogDebug("TTS health check: OK");
            }
            else
            {
                _logger.LogDebug("TTS health check failed: {StatusCode}", response.StatusCode);
            }

            return isHealthy;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "TTS health check failed: HTTP error");
            return false;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogDebug(ex, "TTS health check failed: timeout");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS health check failed: unexpected error");
            return false;
        }
    }
}
