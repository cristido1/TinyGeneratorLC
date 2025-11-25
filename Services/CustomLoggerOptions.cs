namespace TinyGenerator.Services
{
    public class CustomLoggerOptions
    {
        // Number of log entries to buffer before flushing to DB
        public int BatchSize { get; set; } = 50;
        // Flush interval in milliseconds
        public int FlushIntervalMs { get; set; } = 2000;
        // Enable logging of raw request and response JSON
        public bool LogRequestResponse { get; set; } = true;
        // Enable logging of other signals (e.g., progress, info)
        public bool OtherLogs { get; set; } = false;
    }
}
