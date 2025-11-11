using System;

namespace TinyGenerator.Models
{
    public class LogEntry
    {
        public long? Id { get; set; }
        public string Ts { get; set; } = string.Empty; // ISO 8601 with millis
        public string Level { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Exception { get; set; }
        public string? State { get; set; }
        // Optional metadata
        public int ThreadId { get; set; }
        public string? AgentName { get; set; }
        public string? Context { get; set; }
    }
}
