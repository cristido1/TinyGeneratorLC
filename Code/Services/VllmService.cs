using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace TinyGenerator.Services;

public sealed class VllmService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly IOptionsMonitor<VllmServerOptions> _optionsMonitor;
    private readonly IOptionsMonitor<MonomodelModeOptions>? _monomodelOptions;
    private readonly IConfiguration _configuration;
    private readonly ICustomLogger? _logger;
    private readonly DatabaseService? _database;
    private readonly object _metricsCacheLock = new();
    private readonly object _healthEnsureLock = new();
    private readonly SemaphoreSlim _startEnsureSemaphore = new(1, 1);
    private VllmRuntimeSnapshot? _cachedSnapshot;
    private DateTime _cachedSnapshotAtUtc = DateTime.MinValue;
    private DateTime _lastHealthEnsureAttemptUtc = DateTime.MinValue;

    public VllmService(
        IOptionsMonitor<VllmServerOptions> optionsMonitor,
        IConfiguration configuration,
        ICustomLogger? logger = null,
        IOptionsMonitor<MonomodelModeOptions>? monomodelOptions = null,
        DatabaseService? database = null)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _monomodelOptions = monomodelOptions;
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger;
        _database = database;
    }

    public sealed record VllmServerStatus(
        bool HealthOk,
        bool ContainerExists,
        bool ContainerRunning,
        bool SleepModeEnabled,
        bool IsSleeping,
        string Endpoint,
        string ContainerName,
        string? ActiveModel,
        string? ServedModelName,
        string? Note);

    public sealed record VllmOperationResult(
        bool Success,
        string Message,
        string? Details = null,
        VllmServerStatus? Status = null);

    public sealed record VllmRuntimeSnapshot(
        bool Healthy,
        double? KvCacheUsagePercent,
        double? GpuCacheUsagePercent,
        int? RunningRequests,
        int? WaitingRequests,
        long? NumPreemptionsTotal,
        long? GpuFreeMemoryMb,
        long? GpuTotalMemoryMb,
        long? SystemFreeMemoryMb,
        long? SystemTotalMemoryMb,
        string? Note = null);

    public async Task<VllmServerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;
        var container = await GetContainerStateAsync(options.ContainerName, cancellationToken).ConfigureAwait(false);
        var health = await CheckHealthInternalAsync(options, cancellationToken).ConfigureAwait(false);
        var (activeModel, servedModelName) = await TryGetActiveModelAsync(options, cancellationToken).ConfigureAwait(false);
        var isSleeping = await TryIsSleepingAsync(options, cancellationToken).ConfigureAwait(false);

        return new VllmServerStatus(
            HealthOk: health,
            ContainerExists: container.Exists,
            ContainerRunning: container.Running,
            SleepModeEnabled: options.EnableSleepMode,
            IsSleeping: isSleeping,
            Endpoint: options.Endpoint,
            ContainerName: options.ContainerName,
            ActiveModel: activeModel,
            ServedModelName: servedModelName,
            Note: options.DefaultDisableThinking
                ? "thinking disabilitato lato request"
                : null);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        return await CheckHealthInternalAsync(_optionsMonitor.CurrentValue, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VllmOperationResult> EnsureHealthyAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[vLLM] EnsureHealthyAsync invoked at {DateTime.UtcNow:O}");
        try
        {
            var options = _optionsMonitor.CurrentValue;
            Console.WriteLine("[vLLM] EnsureHealthyAsync: checking health...");
            var healthy = await CheckHealthInternalAsync(options, cancellationToken).ConfigureAwait(false);
            if (healthy)
            {
                Console.WriteLine("[vLLM] EnsureHealthyAsync: already healthy.");
                return new VllmOperationResult(
                    true,
                    "vLLM healthy.",
                    Status: await GetStatusAsync(cancellationToken).ConfigureAwait(false));
            }

            var now = DateTime.UtcNow;
            lock (_healthEnsureLock)
            {
                if ((now - _lastHealthEnsureAttemptUtc) < TimeSpan.FromSeconds(10))
                {
                    Console.WriteLine("[vLLM] EnsureHealthyAsync: skipped (cooldown in corso).");
                    return new VllmOperationResult(
                        false,
                        "vLLM non healthy (cooldown avvio in corso).",
                        Status: null);
                }

                _lastHealthEnsureAttemptUtc = now;
            }

            Console.WriteLine("[vLLM] EnsureHealthyAsync: start requested...");
            var ensure = await EnsureStartedAsync(
                model: null,
                servedModelName: null,
                requestedMaxModelLen: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[vLLM] EnsureHealthyAsync: start completed success={ensure.Success} message=\"{ensure.Message}\"");
            if (!ensure.Success)
            {
                Console.WriteLine($"[vLLM] EnsureHealthyAsync: start failed ({ensure.Message}).");
                return new VllmOperationResult(
                    false,
                    $"vLLM non healthy e avvio fallito: {ensure.Message}",
                    ensure.Details,
                    ensure.Status);
            }

            healthy = await CheckHealthInternalAsync(options, cancellationToken).ConfigureAwait(false);
            if (!healthy)
            {
                Console.WriteLine("[vLLM] EnsureHealthyAsync: still not healthy after start attempt.");
                return new VllmOperationResult(
                    false,
                    "vLLM non healthy dopo tentativo di avvio.",
                    ensure.Details,
                    await GetStatusAsync(cancellationToken).ConfigureAwait(false));
            }

            Console.WriteLine("[vLLM] EnsureHealthyAsync: healthy after auto-start.");
            return new VllmOperationResult(
                true,
                "vLLM healthy dopo auto-avvio.",
                ensure.Details,
                await GetStatusAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[vLLM] EnsureHealthyAsync: cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[vLLM] EnsureHealthyAsync: exception {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public async Task<VllmRuntimeSnapshot> GetRuntimeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        lock (_metricsCacheLock)
        {
            if (_cachedSnapshot != null && (DateTime.UtcNow - _cachedSnapshotAtUtc) < TimeSpan.FromSeconds(1))
            {
                return _cachedSnapshot;
            }
        }

        var options = _optionsMonitor.CurrentValue;
        var healthy = await CheckHealthInternalAsync(options, cancellationToken).ConfigureAwait(false);
        var prometheus = await TryReadPrometheusMetricsAsync(options, cancellationToken).ConfigureAwait(false);
        var gpu = await TryReadGpuMemoryAsync(cancellationToken).ConfigureAwait(false);
        var system = await TryReadSystemMemoryAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = new VllmRuntimeSnapshot(
            Healthy: healthy,
            KvCacheUsagePercent: ReadPrometheusDouble(prometheus, "vllm:kv_cache_usage_perc", "vllm_kv_cache_usage_perc"),
            GpuCacheUsagePercent: ReadPrometheusDouble(prometheus, "vllm:gpu_cache_usage_perc", "vllm_gpu_cache_usage_perc"),
            RunningRequests: ReadPrometheusInt(prometheus, "vllm:num_requests_running", "vllm_num_requests_running"),
            WaitingRequests: ReadPrometheusInt(prometheus, "vllm:num_requests_waiting", "vllm_num_requests_waiting"),
            NumPreemptionsTotal: ReadPrometheusLong(prometheus, "vllm:num_preemptions_total", "vllm_num_preemptions_total"),
            GpuFreeMemoryMb: gpu.FreeMb,
            GpuTotalMemoryMb: gpu.TotalMb,
            SystemFreeMemoryMb: system.FreeMb,
            SystemTotalMemoryMb: system.TotalMb,
            Note: healthy ? null : "endpoint non healthy");

        lock (_metricsCacheLock)
        {
            _cachedSnapshot = snapshot;
            _cachedSnapshotAtUtc = DateTime.UtcNow;
        }

        return snapshot;
    }

    public async Task<VllmOperationResult> EnsureStartedAsync(
        string? model = null,
        string? servedModelName = null,
        int? requestedMaxModelLen = null,
        CancellationToken cancellationToken = default)
    {
        await _startEnsureSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var options = _optionsMonitor.CurrentValue;
            var requestedModel = ResolveModelArgument(options, model);
            var requestedServedModel = string.IsNullOrWhiteSpace(servedModelName)
                ? options.DefaultServedModelName.Trim()
                : servedModelName.Trim();
            var effectiveRequestedMaxModelLen = requestedMaxModelLen.GetValueOrDefault() > 0
                ? Math.Max(1, requestedMaxModelLen!.Value)
                : (int?)null;
            var isHealthEnsureCall = string.IsNullOrWhiteSpace(model)
                                     && string.IsNullOrWhiteSpace(servedModelName)
                                     && !effectiveRequestedMaxModelLen.HasValue;

            var currentStatus = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var requiresBiggerContextThanConfig = effectiveRequestedMaxModelLen.HasValue &&
                                                  effectiveRequestedMaxModelLen.Value > Math.Max(1, options.MaxModelLen);
            var sameServedModel =
                string.IsNullOrWhiteSpace(currentStatus.ServedModelName) ||
                string.Equals(currentStatus.ServedModelName, requestedServedModel, StringComparison.OrdinalIgnoreCase);

            // For health-only ensure calls, never restart a running healthy container:
            // this avoids unnecessary vLLM restarts at TinyGenerator startup.
            if (isHealthEnsureCall && currentStatus.ContainerRunning && currentStatus.HealthOk)
            {
                return new VllmOperationResult(
                    true,
                    "vLLM gia attivo e healthy (skip restart).",
                    Status: currentStatus);
            }

            if (!requiresBiggerContextThanConfig &&
                currentStatus.HealthOk &&
                string.Equals(currentStatus.ServedModelName, requestedServedModel, StringComparison.OrdinalIgnoreCase))
            {
                return new VllmOperationResult(
                    true,
                    $"vLLM gia attivo con modello {requestedServedModel}.",
                    Status: currentStatus);
            }

            // Se il container è già in avvio per lo stesso modello, aspetta invece di riavviare.
            if (!requiresBiggerContextThanConfig && currentStatus.ContainerRunning && sameServedModel)
            {
                Console.WriteLine("[vLLM] EnsureStartedAsync: container running but not healthy, waiting warmup...");
                var warmed = await WaitForHealthyAsync(options, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
                var warmedStatus = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (warmed && warmedStatus.HealthOk)
                {
                    return new VllmOperationResult(
                        true,
                        $"vLLM healthy dopo warmup ({requestedServedModel}).",
                        Status: warmedStatus);
                }
            }

            await StopAndRemoveContainerIfExistsAsync(options.ContainerName, cancellationToken).ConfigureAwait(false);

            var args = BuildDockerRunArguments(options, requestedModel, requestedServedModel, effectiveRequestedMaxModelLen);
            var runResult = await RunProcessAsync("docker", args, cancellationToken, timeoutMs: 120_000).ConfigureAwait(false);
            if (!runResult.Success)
            {
                return new VllmOperationResult(
                    false,
                    "Avvio container vLLM fallito.",
                    runResult.Output,
                    await GetStatusAsync(cancellationToken).ConfigureAwait(false));
            }

            var ready = await WaitForHealthyAsync(options, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
            var finalStatus = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                return new VllmOperationResult(
                    false,
                    "Container vLLM avviato ma endpoint non ancora healthy.",
                    runResult.Output,
                    finalStatus);
            }

            _logger?.Log("Info", "vllm", $"vLLM started: model={requestedModel}; served_model={requestedServedModel}; endpoint={options.Endpoint}");
            return new VllmOperationResult(
                true,
                $"vLLM avviato con modello {requestedServedModel}.",
                runResult.Output,
                finalStatus);
        }
        finally
        {
            _startEnsureSemaphore.Release();
        }
    }

    public async Task<VllmOperationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;
        var stop = await StopAndRemoveContainerIfExistsAsync(options.ContainerName, cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return new VllmOperationResult(
            stop.Success,
            stop.Success ? "Container vLLM fermato." : "Stop vLLM fallito.",
            stop.Output,
            status);
    }

    public async Task<VllmOperationResult> SleepAsync(int level = 1, CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.EnableSleepMode)
        {
            return new VllmOperationResult(false, "Sleep mode non abilitato in configurazione.", Status: await GetStatusAsync(cancellationToken).ConfigureAwait(false));
        }

        level = Math.Clamp(level, 1, 2);
        var response = await PostDevEndpointAsync($"{options.Endpoint.TrimEnd('/')}/sleep?level={level}", cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return new VllmOperationResult(
            response.Success,
            response.Success ? $"vLLM in sleep level {level}." : "Sleep vLLM fallito.",
            response.Output,
            status);
    }

    public async Task<VllmOperationResult> WakeUpAsync(string[]? tags = null, CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.EnableSleepMode)
        {
            return new VllmOperationResult(false, "Sleep mode non abilitato in configurazione.", Status: await GetStatusAsync(cancellationToken).ConfigureAwait(false));
        }

        var query = string.Empty;
        if (tags != null && tags.Length > 0)
        {
            query = "?" + string.Join("&", tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => $"tags={Uri.EscapeDataString(t.Trim())}"));
        }

        var response = await PostDevEndpointAsync($"{options.Endpoint.TrimEnd('/')}/wake_up{query}", cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return new VllmOperationResult(
            response.Success,
            response.Success ? "vLLM riattivato." : "Wake up vLLM fallito.",
            response.Output,
            status);
    }

    public async Task<VllmOperationResult> FreeVramAsync(bool keepServerActive = true, int sleepLevel = 1, CancellationToken cancellationToken = default)
    {
        if (keepServerActive)
        {
            return await SleepAsync(sleepLevel, cancellationToken).ConfigureAwait(false);
        }

        return await StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private string ResolveModelArgument(VllmServerOptions options, string? model)
    {
        var requested = string.IsNullOrWhiteSpace(model) ? options.DefaultModel.Trim() : model.Trim();
        if (!options.PreferLocalModel)
        {
            return requested;
        }

        var localSubdir = options.DefaultLocalModelSubdir.Trim();
        if (string.IsNullOrWhiteSpace(localSubdir))
        {
            return requested;
        }

        var localHostPath = Path.Combine(options.HostModelsPath, localSubdir);
        if (!Directory.Exists(localHostPath))
        {
            return requested;
        }

        var containerModelsPath = options.ContainerModelsPath.TrimEnd('/');
        return $"{containerModelsPath}/{localSubdir}";
    }

    private string BuildDockerRunArguments(VllmServerOptions options, string modelArgument, string servedModelName)
    {
        return BuildDockerRunArguments(options, modelArgument, servedModelName, null);
    }

    private string BuildDockerRunArguments(
        VllmServerOptions options,
        string modelArgument,
        string servedModelName,
        int? maxModelLenOverride)
    {
        var useNightlyCli = (options.DockerImage ?? string.Empty).Contains("nightly", StringComparison.OrdinalIgnoreCase);
        var applyMonomodelProfile = ShouldApplyMonomodelVllmHighThroughputProfile();
        // Do not force an higher gpu-memory-utilization in monomodel mode:
        // on partially busy GPUs this can prevent startup and trigger restart loops.
        var effectiveGpuMemoryUtilization = options.GpuMemoryUtilization;
        var effectiveMaxModelLenBase = options.MaxModelLen;
        var effectiveMaxModelLen = Math.Max(
            Math.Max(1, effectiveMaxModelLenBase),
            maxModelLenOverride.GetValueOrDefault() > 0 ? maxModelLenOverride.Value : 0);
        var effectiveMaxNumSeqs = options.MaxNumSeqs;
        var effectiveMaxNumBatchedTokens = options.MaxNumBatchedTokens;
        var effectiveTensorParallel = applyMonomodelProfile
            ? 1
            : Math.Max(1, options.TensorParallelSize);
        var effectiveTrustRemoteCode = applyMonomodelProfile || options.TrustRemoteCode;

        if (applyMonomodelProfile)
        {
            _logger?.Log(
                "Information",
                "vllm",
                $"Applying vLLM monomodel throughput profile: gpu_mem_util={effectiveGpuMemoryUtilization:0.00}; max_num_seqs={effectiveMaxNumSeqs}; max_model_len={effectiveMaxModelLen}; max_num_batched_tokens={effectiveMaxNumBatchedTokens}; tensor_parallel={effectiveTensorParallel}");
        }
        else if (maxModelLenOverride.GetValueOrDefault() > 0)
        {
            _logger?.Log(
                "Information",
                "vllm",
                $"Applying vLLM runtime context override: max_model_len={effectiveMaxModelLen} (requested={maxModelLenOverride.Value}, config={options.MaxModelLen})");
        }

        var sb = new StringBuilder();
        sb.Append("run -d --restart unless-stopped");
        sb.Append($" --name \"{options.ContainerName}\"");
        sb.Append(" --gpus all");
        sb.Append($" -p {options.HostPort}:{options.ContainerPort}");
        sb.Append($" -v \"{options.HostModelsPath}:{options.ContainerModelsPath}\"");

        var apiKey = ResolveConfiguredApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            sb.Append($" -e \"VLLM_API_KEY={apiKey}\"");
        }

        if (options.EnableSleepMode)
        {
            sb.Append(" -e \"VLLM_SERVER_DEV_MODE=1\"");
        }

        sb.Append($" \"{options.DockerImage}\"");
        if (useNightlyCli)
        {
            sb.Append($" \"{modelArgument}\"");
        }
        sb.Append($" --host 0.0.0.0 --port {options.ContainerPort}");
        if (!useNightlyCli)
        {
            sb.Append($" --model \"{modelArgument}\"");
        }
        if (!string.IsNullOrWhiteSpace(options.TokenizerLocalSubdir))
        {
            var tokenizerPath = $"{options.ContainerModelsPath.TrimEnd('/')}/{options.TokenizerLocalSubdir.Trim()}";
            sb.Append($" --tokenizer \"{tokenizerPath}\"");
        }
        sb.Append($" --served-model-name \"{servedModelName}\"");
        sb.Append($" --gpu-memory-utilization {effectiveGpuMemoryUtilization.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.Append($" --max-model-len {Math.Max(1, effectiveMaxModelLen)}");
        sb.Append($" --max-num-seqs {Math.Max(1, effectiveMaxNumSeqs)}");
        sb.Append($" --max-num-batched-tokens {Math.Max(1, effectiveMaxNumBatchedTokens)}");
        sb.Append($" --tensor-parallel-size {Math.Max(1, effectiveTensorParallel)}");
        if (!useNightlyCli)
        {
            sb.Append($" --swap-space {options.SwapSpaceGb.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        sb.Append($" --cpu-offload-gb {options.CpuOffloadGb.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (effectiveTrustRemoteCode)
        {
            sb.Append(" --trust-remote-code");
        }
        if (options.EnableSleepMode)
        {
            sb.Append(" --enable-sleep-mode");
        }
        if (options.DisableRequestLogging)
        {
            sb.Append(" --no-enable-log-requests");
        }

        return sb.ToString();
    }

    private bool ShouldApplyMonomodelVllmHighThroughputProfile()
    {
        try
        {
            var mono = _monomodelOptions?.CurrentValue;
            if (mono?.Enabled != true)
            {
                return false;
            }

            var fixedModelDescription = (mono.ModelDescription ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(fixedModelDescription))
            {
                return false;
            }

            if (_database == null)
            {
                return true;
            }

            var model = _database.ListModels().FirstOrDefault(m =>
                string.Equals((m.Name ?? string.Empty).Trim(), fixedModelDescription, StringComparison.OrdinalIgnoreCase) ||
                string.Equals((m.CallName ?? string.Empty).Trim(), fixedModelDescription, StringComparison.OrdinalIgnoreCase));
            return string.Equals(model?.Provider, "vllm", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<(bool Exists, bool Running, string Output)> GetContainerStateAsync(string containerName, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
            "docker",
            $"ps -a --filter \"name=^/{containerName}$\" --format \"{{{{.Names}}}}|{{{{.Status}}}}\"",
            cancellationToken,
            timeoutMs: 20_000).ConfigureAwait(false);

        if (!result.Success && string.IsNullOrWhiteSpace(result.Output))
        {
            return (false, false, result.Output);
        }

        var line = (result.Output ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line))
        {
            return (false, false, result.Output);
        }

        var parts = line.Split('|', 2);
        var running = parts.Length > 1 &&
            parts[1].Trim().StartsWith("Up", StringComparison.OrdinalIgnoreCase);
        return (true, running, result.Output);
    }

    private async Task<(bool Success, string Output)> StopAndRemoveContainerIfExistsAsync(string containerName, CancellationToken cancellationToken)
    {
        var state = await GetContainerStateAsync(containerName, cancellationToken).ConfigureAwait(false);
        if (!state.Exists)
        {
            return (true, "Container non presente.");
        }

        var stop = await RunProcessAsync("docker", $"rm -f \"{containerName}\"", cancellationToken, timeoutMs: 60_000).ConfigureAwait(false);
        return (stop.Success, stop.Output);
    }

    private async Task<bool> CheckHealthInternalAsync(VllmServerOptions options, CancellationToken cancellationToken)
    {
        // Prefer OpenAI models endpoint as readiness signal (model-aware),
        // then fallback to /health (liveness-style check).
        var modelsUrl = $"{options.Endpoint.TrimEnd('/')}/v1/models";
        var healthUrl = options.HealthEndpoint;
        var apiKey = ResolveConfiguredApiKey();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TryGetOkAsync(modelsUrl, apiKey, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (await TryGetOkAsync(healthUrl, apiKey, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (attempt < 3)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }

        return false;
    }

    private static async Task<bool> TryGetOkAsync(string url, string? apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private string? ResolveConfiguredApiKey()
    {
        var configured = _configuration["Secrets:vLLM:ApiKey"] ?? Environment.GetEnvironmentVariable("VLLM_API_KEY");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var trimmed = configured.Trim();
        if (string.Equals(trimmed, "EMPTY", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    private async Task<(string? ActiveModel, string? ServedModelName)> TryGetActiveModelAsync(VllmServerOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var response = await HttpClient.GetAsync($"{options.Endpoint.TrimEnd('/')}/v1/models", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, null);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return (null, null);
            }

            var first = data.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var modelId = first.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var root = first.TryGetProperty("root", out var rootProp) ? rootProp.GetString() : null;
            return (root ?? modelId, modelId);
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task<bool> TryIsSleepingAsync(VllmServerOptions options, CancellationToken cancellationToken)
    {
        if (!options.EnableSleepMode)
        {
            return false;
        }

        try
        {
            var response = await HttpClient.GetAsync($"{options.Endpoint.TrimEnd('/')}/is_sleeping", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (bool.TryParse(body, out var sleeping))
            {
                return sleeping;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("is_sleeping", out var value) &&
                (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
            {
                return value.GetBoolean();
            }
        }
        catch
        {
            // best-effort
        }

        return false;
    }

    private async Task<(bool Success, string Output)> PostDevEndpointAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (response.IsSuccessStatusCode, body);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<bool> WaitForHealthyAsync(VllmServerOptions options, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await CheckHealthInternalAsync(options, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<Dictionary<string, double>> TryReadPrometheusMetricsAsync(VllmServerOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var response = await HttpClient.GetAsync(options.MetricsEndpoint, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separator = line.LastIndexOf(' ');
                if (separator <= 0 || separator >= line.Length - 1)
                {
                    continue;
                }

                var key = line[..separator].Trim();
                var labelsIndex = key.IndexOf('{');
                if (labelsIndex >= 0)
                {
                    key = key[..labelsIndex];
                }

                if (double.TryParse(line[(separator + 1)..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    values[key] = parsed;
                }
            }

            return values;
        }
        catch
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static double? ReadPrometheusDouble(IReadOnlyDictionary<string, double> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? ReadPrometheusInt(IReadOnlyDictionary<string, double> values, params string[] keys)
    {
        var value = ReadPrometheusDouble(values, keys);
        return value.HasValue ? (int)Math.Round(value.Value) : null;
    }

    private static long? ReadPrometheusLong(IReadOnlyDictionary<string, double> values, params string[] keys)
    {
        var value = ReadPrometheusDouble(values, keys);
        return value.HasValue ? (long)Math.Round(value.Value) : null;
    }

    private static async Task<(long? FreeMb, long? TotalMb)> TryReadGpuMemoryAsync(CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
            "nvidia-smi",
            "--query-gpu=memory.free,memory.total --format=csv,noheader,nounits",
            cancellationToken,
            timeoutMs: 10_000).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            return (null, null);
        }

        long free = 0;
        long total = 0;
        foreach (var line in result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            if (long.TryParse(parts[0], out var freePart))
            {
                free += freePart;
            }

            if (long.TryParse(parts[1], out var totalPart))
            {
                total += totalPart;
            }
        }

        return (free > 0 ? free : null, total > 0 ? total : null);
    }

    private static async Task<(long? FreeMb, long? TotalMb)> TryReadSystemMemoryAsync(CancellationToken cancellationToken)
    {
        const string command = "(Get-CimInstance Win32_OperatingSystem | Select-Object FreePhysicalMemory,TotalVisibleMemorySize | ConvertTo-Json -Compress)";
        var result = await RunProcessAsync(
            "powershell",
            $"-NoProfile -Command \"{command}\"",
            cancellationToken,
            timeoutMs: 10_000).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Output);
            var root = doc.RootElement;
            if (!root.TryGetProperty("FreePhysicalMemory", out var freeKbProp) ||
                !root.TryGetProperty("TotalVisibleMemorySize", out var totalKbProp))
            {
                return (null, null);
            }

            var freeKb = ParseJsonLong(freeKbProp);
            var totalKb = ParseJsonLong(totalKbProp);
            return (freeKb.HasValue ? freeKb.Value / 1024 : null, totalKb.HasValue ? totalKb.Value / 1024 : null);
        }
        catch
        {
            return (null, null);
        }
    }

    private static long? ParseJsonLong(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var numeric) => numeric,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static async Task<(bool Success, string Output)> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken,
        int timeoutMs)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return (false, "Impossibile avviare il processo.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(timeoutMs, cancellationToken);

            var completed = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);
            if (completed == timeoutTask)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best-effort
                }

                return (false, $"Timeout eseguendo: {fileName} {arguments}");
            }

            await waitTask.ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            var combined = string.IsNullOrWhiteSpace(error)
                ? output
                : string.Join(Environment.NewLine, new[] { output, error }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return (process.ExitCode == 0, combined.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
