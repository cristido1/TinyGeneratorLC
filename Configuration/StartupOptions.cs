namespace TinyGenerator.Configuration;

/// <summary>
/// Configuration model for startup tasks timeouts and settings.
/// Maps to "Startup" section in appsettings.json.
/// </summary>
public class StartupOptions
{
    public TimeoutSettings Timeouts { get; set; } = new();
    public AudioCraftSettings AudioCraft { get; set; } = new();
}

public class TimeoutSettings
{
    /// <summary>
    /// Timeout in milliseconds when terminating llama-server.exe processes.
    /// Default: 3000ms (3 seconds)
    /// </summary>
    public int LlamaServerKillMs { get; set; } = 3000;

    /// <summary>
    /// Interval in milliseconds between AudioCraft health check attempts.
    /// Default: 1000ms (1 second)
    /// </summary>
    public int AudioCraftHealthCheckIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Maximum time in milliseconds to wait for AudioCraft service startup.
    /// Default: 30000ms (30 seconds)
    /// </summary>
    public int AudioCraftMaxStartupWaitMs { get; set; } = 30000;

    /// <summary>
    /// Timeout in seconds for TTS voice seeding operation.
    /// Default: 10 seconds
    /// </summary>
    public int TtsSeedTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Default timeout in milliseconds for killing processes.
    /// Default: 5000ms (5 seconds)
    /// </summary>
    public int DefaultProcessKillMs { get; set; } = 5000;
}

public class AudioCraftSettings
{
    /// <summary>
    /// Health check endpoint for AudioCraft service.
    /// Default: http://localhost:8003/health
    /// </summary>
    public string HealthEndpoint { get; set; } = "http://localhost:8003/health";

    /// <summary>
    /// PowerShell script path for starting AudioCraft (Windows).
    /// Default: start_audiocraft.ps1
    /// </summary>
    public string PowerShellScript { get; set; } = "start_audiocraft.ps1";

    /// <summary>
    /// Python script path for starting AudioCraft (cross-platform).
    /// Default: audiocraft_server.py
    /// </summary>
    public string PythonScript { get; set; } = "audiocraft_server.py";
}
