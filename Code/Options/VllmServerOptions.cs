namespace TinyGenerator.Services;

public sealed class VllmServerOptions
{
    public string Endpoint { get; set; } = "http://127.0.0.1:8000";
    public string HealthEndpoint { get; set; } = "http://127.0.0.1:8000/health";
    public string MetricsEndpoint { get; set; } = "http://127.0.0.1:8000/metrics";
    public string ContainerName { get; set; } = "tinygenerator-vllm";
    public string DockerImage { get; set; } = "vllm/vllm-openai:latest";
    public int HostPort { get; set; } = 8000;
    public int ContainerPort { get; set; } = 8000;
    public string HostModelsPath { get; set; } = @"C:\vllm_models";
    public string ContainerModelsPath { get; set; } = "/models";
    public string DefaultModel { get; set; } = "Qwen/Qwen3.5-9B";
    public string DefaultLocalModelSubdir { get; set; } = "Qwen--Qwen3.5-9B";
    public string? TokenizerLocalSubdir { get; set; }
    public string DefaultServedModelName { get; set; } = "Qwen/Qwen3.5-9B";
    public bool PreferLocalModel { get; set; } = true;
    public bool DefaultDisableThinking { get; set; } = true;
    public bool EnableSleepMode { get; set; } = true;
    public bool TrustRemoteCode { get; set; } = true;
    public double GpuMemoryUtilization { get; set; } = 0.9;
    public int MaxModelLen { get; set; } = 16000;
    public int MaxNumSeqs { get; set; } = 16;
    public int MaxNumBatchedTokens { get; set; } = 8192;
    public int TensorParallelSize { get; set; } = 1;
    public double SwapSpaceGb { get; set; } = 0;
    public double CpuOffloadGb { get; set; } = 0;
    public bool DisableRequestLogging { get; set; } = true;
    public int DispatcherMaxParallel { get; set; } = 6;
    public int DispatcherMaxWaitingRequests { get; set; } = 1;
    public double DispatcherGpuCacheUsageSoftLimit { get; set; } = 0.9;
    public double DispatcherGpuCacheUsageHardLimit { get; set; } = 0.97;
    public long DispatcherMinGpuFreeMemoryMb { get; set; } = 1024;
    public long DispatcherMinSystemFreeMemoryMb { get; set; } = 2048;
    public int DispatcherPollDelayMs { get; set; } = 1500;
    public string DispatcherAdmissionPolicy { get; set; } = "gpu_only";
    public int AbortAfterIdenticalFailures { get; set; } = 3;
}
