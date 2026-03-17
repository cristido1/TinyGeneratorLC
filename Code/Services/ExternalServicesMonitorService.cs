using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using TinyGenerator.Configuration;

namespace TinyGenerator.Services;

public sealed class ExternalServicesMonitorService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(4) };

    private readonly IConfiguration _configuration;
    private readonly IOllamaMonitorService _ollamaMonitor;
    private readonly LlamaService _llamaService;
    private readonly VllmService _vllmService;
    private readonly IServiceHealthMonitor _healthMonitor;
    private readonly TtsService _ttsService;

    public ExternalServicesMonitorService(
        IConfiguration configuration,
        IOllamaMonitorService ollamaMonitor,
        LlamaService llamaService,
        VllmService vllmService,
        IServiceHealthMonitor healthMonitor,
        TtsService ttsService)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _ollamaMonitor = ollamaMonitor ?? throw new ArgumentNullException(nameof(ollamaMonitor));
        _llamaService = llamaService ?? throw new ArgumentNullException(nameof(llamaService));
        _vllmService = vllmService ?? throw new ArgumentNullException(nameof(vllmService));
        _healthMonitor = healthMonitor ?? throw new ArgumentNullException(nameof(healthMonitor));
        _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
    }

    public sealed record ExternalServiceFact(string Label, string Value);

    public sealed record ExternalServiceStatus(
        string Key,
        string Title,
        string Endpoint,
        bool IsActive,
        string Summary,
        string? Activity,
        string? Error,
        IReadOnlyList<ExternalServiceFact> Facts);

    public async Task<IReadOnlyList<ExternalServiceStatus>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var keys = new[] { "ollama", "llama_cpp", "vllm", "local_tts" };
        var results = new List<ExternalServiceStatus>(keys.Length);
        foreach (var key in keys)
        {
            results.Add(await GetServiceStatusAsync(key, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public Task<ExternalServiceStatus> GetServiceStatusAsync(string key, CancellationToken cancellationToken = default)
    {
        return key switch
        {
            "ollama" => GetOllamaStatusAsync(cancellationToken),
            "llama_cpp" => GetLlamaCppStatusAsync(cancellationToken),
            "vllm" => GetVllmStatusAsync(cancellationToken),
            "local_tts" => GetLocalTtsStatusAsync(cancellationToken),
            _ => Task.FromResult(new ExternalServiceStatus(
                key,
                key,
                string.Empty,
                false,
                "Servizio non riconosciuto.",
                null,
                null,
                Array.Empty<ExternalServiceFact>()))
        };
    }

    private async Task<ExternalServiceStatus> GetOllamaStatusAsync(CancellationToken cancellationToken)
    {
        var endpoint = ExternalServerConfig.GetRequiredValue(_configuration, "Ollama:Endpoint").TrimEnd('/');
        var facts = new List<ExternalServiceFact>();

        try
        {
            var tagsResponse = await HttpClient.GetAsync($"{endpoint}/api/tags", cancellationToken).ConfigureAwait(false);
            var isActive = tagsResponse.IsSuccessStatusCode;

            var running = isActive
                ? await _ollamaMonitor.GetRunningModelsFromHttpAsync().ConfigureAwait(false)
                : new List<OllamaModelInfo>();

            facts.Add(new ExternalServiceFact("Modelli in memoria", running.Count.ToString()));
            if (running.Count > 0)
            {
                facts.Add(new ExternalServiceFact("Modelli", string.Join(", ", running.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Take(3))));
            }

            var activity = BuildOllamaActivity(running);
            var summary = isActive
                ? (running.Count > 0 ? "Server attivo con modelli in memoria." : "Server attivo, nessun modello in memoria.")
                : "Server non raggiungibile.";

            return new ExternalServiceStatus("ollama", "Ollama", endpoint, isActive, summary, activity, null, facts);
        }
        catch (Exception ex)
        {
            return new ExternalServiceStatus("ollama", "Ollama", endpoint, false, "Server non raggiungibile.", null, ex.Message, facts);
        }
    }

    private async Task<ExternalServiceStatus> GetLlamaCppStatusAsync(CancellationToken cancellationToken)
    {
        var host = ExternalServerConfig.GetRequiredValue(_configuration, "LlamaCpp:Host");
        var port = ExternalServerConfig.GetRequiredValue(_configuration, "LlamaCpp:Port");
        var endpoint = $"http://{host}:{port}";
        var facts = new List<ExternalServiceFact>();

        try
        {
            var isActive = await _llamaService.IsServerRunningAsync().ConfigureAwait(false);
            var models = isActive
                ? await GetOpenAiCompatibleModelIdsAsync(endpoint, cancellationToken).ConfigureAwait(false)
                : new List<string>();

            facts.Add(new ExternalServiceFact("Modelli caricati", models.Count.ToString()));
            if (models.Count > 0)
            {
                facts.Add(new ExternalServiceFact("Modelli", string.Join(", ", models.Take(3))));
            }

            var summary = isActive
                ? (models.Count > 0 ? "Server attivo." : "Server attivo, modello non rilevato.")
                : "Server non attivo.";
            var activity = models.Count > 0 ? $"Sta servendo: {string.Join(", ", models.Take(2))}" : null;

            return new ExternalServiceStatus("llama_cpp", "llama.cpp", endpoint, isActive, summary, activity, null, facts);
        }
        catch (Exception ex)
        {
            return new ExternalServiceStatus("llama_cpp", "llama.cpp", endpoint, false, "Server non attivo.", null, ex.Message, facts);
        }
    }

    private async Task<ExternalServiceStatus> GetVllmStatusAsync(CancellationToken cancellationToken)
    {
        var status = await _vllmService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var facts = new List<ExternalServiceFact>
        {
            new("Container", status.ContainerRunning ? "running" : (status.ContainerExists ? "fermato" : "assente")),
            new("Sleep", status.IsSleeping ? "si" : "no")
        };

        if (!string.IsNullOrWhiteSpace(status.ServedModelName))
        {
            facts.Add(new ExternalServiceFact("Served model", status.ServedModelName));
        }

        if (!string.IsNullOrWhiteSpace(status.ActiveModel) &&
            !string.Equals(status.ActiveModel, status.ServedModelName, StringComparison.OrdinalIgnoreCase))
        {
            facts.Add(new ExternalServiceFact("Model root", status.ActiveModel));
        }

        var summary = status.HealthOk
            ? "Endpoint attivo."
            : status.ContainerRunning
                ? "Container attivo, endpoint non healthy."
                : "Server non attivo.";
        var activity = status.IsSleeping
            ? "Server in sleep mode."
            : !string.IsNullOrWhiteSpace(status.ServedModelName)
                ? $"Sta servendo: {status.ServedModelName}"
                : null;

        return new ExternalServiceStatus("vllm", "vLLM", status.Endpoint, status.HealthOk, summary, activity, status.Note, facts);
    }

    private async Task<ExternalServiceStatus> GetLocalTtsStatusAsync(CancellationToken cancellationToken)
    {
        var host = ExternalServerConfig.GetRequiredValue(_configuration, "LocalTts:Host");
        var port = ExternalServerConfig.GetRequiredValue(_configuration, "LocalTts:Port");
        var endpoint = $"http://{host}:{port}";
        var healthEndpoint = ExternalServerConfig.GetRequiredValue(_configuration, "LocalTts:HealthEndpoint");
        var facts = new List<ExternalServiceFact>();

        try
        {
            var isActive = await _healthMonitor.CheckTtsHealthAsync(cancellationToken).ConfigureAwait(false);
            var voices = isActive
                ? await _ttsService.GetVoicesAsync().ConfigureAwait(false)
                : new List<VoiceInfo>();

            facts.Add(new ExternalServiceFact("Voices", voices.Count.ToString()));
            if (voices.Count > 0)
            {
                var voicePreview = string.Join(", ", voices.Select(v => v.Name).Where(v => !string.IsNullOrWhiteSpace(v)).Take(3));
                if (!string.IsNullOrWhiteSpace(voicePreview))
                {
                    facts.Add(new ExternalServiceFact("Voci", voicePreview));
                }
            }

            var summary = isActive ? "Server TTS attivo." : "Server TTS non raggiungibile.";
            var activity = isActive && voices.Count > 0 ? "Pronto per sintesi vocale." : null;

            return new ExternalServiceStatus("local_tts", "localTTS", endpoint, isActive, summary, activity, null, facts.Append(new ExternalServiceFact("Health", healthEndpoint)).ToList());
        }
        catch (Exception ex)
        {
            return new ExternalServiceStatus("local_tts", "localTTS", endpoint, false, "Server TTS non raggiungibile.", null, ex.Message, facts.Append(new ExternalServiceFact("Health", healthEndpoint)).ToList());
        }
    }

    private string? BuildOllamaActivity(IReadOnlyList<OllamaModelInfo> running)
    {
        foreach (var item in running)
        {
            var lastPrompt = _ollamaMonitor.GetLastPrompt(item.Name);
            if (lastPrompt.HasValue)
            {
                return $"Ultima attivita su {item.Name}: {lastPrompt.Value.Ts.ToLocalTime():dd/MM HH:mm}";
            }
        }

        return running.Count > 0 ? $"Sta servendo {running.Count} modello/i." : null;
    }

    private static async Task<List<string>> GetOpenAiCompatibleModelIdsAsync(string endpoint, CancellationToken cancellationToken)
    {
        var response = await HttpClient.GetAsync($"{endpoint.TrimEnd('/')}/v1/models", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new List<string>();
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();
    }
}
