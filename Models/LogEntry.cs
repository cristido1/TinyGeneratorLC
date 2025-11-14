using System;

namespace TinyGenerator.Models
{
    public class LogEntry
    {
        public long? Id { get; set; }
        // Backing ISO timestamp (if log source uses string timestamps)
        public string Ts { get; set; } = string.Empty; // ISO 8601 with millis

        // Exposed convenience property used by the UI
        public DateTime Timestamp
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Ts) && DateTime.TryParse(Ts, out var dt)) return dt;
                return DateTime.UtcNow;
            }
            set
            {
                Ts = value.ToString("o");
            }
        }

        public string Level { get; set; } = string.Empty;
        // Category/source of the log (alias Source for UI)
        public string Category { get; set; } = string.Empty;
        public string Source
        {
            get => Category;
            set => Category = value;
        }

        public string Message { get; set; } = string.Empty;
        public string? Exception { get; set; }
        public string? State { get; set; }
        // Optional metadata
        public int ThreadId { get; set; }
        public string? AgentName { get; set; }
        public string? Context { get; set; }
    }
}
