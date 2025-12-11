using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models
{
    [Table("Log")]
    public class LogEntry
    {
        [Column("Id")]
        public long? Id { get; set; }
        
        // Backing ISO timestamp (if log source uses string timestamps)
        [Column("Ts")]
        public string Ts { get; set; } = string.Empty; // ISO 8601 with millis

        // Exposed convenience property used by the UI
        [NotMapped]
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

        [Column("Level")]
        public string Level { get; set; } = string.Empty;
        
        // Category/source of the log (alias Source for UI)
        [Column("Category")]
        public string Category { get; set; } = string.Empty;
        
        [NotMapped]
        public string Source
        {
            get => Category;
            set => Category = value;
        }

        [Column("Message")]
        public string Message { get; set; } = string.Empty;
        
        [Column("Exception")]
        public string? Exception { get; set; }
        
        [Column("State")]
        public string? State { get; set; }
        
        // Optional metadata
        [Column("ThreadId")]
        public int ThreadId { get; set; }
        
        [Column("ThreadScope")]
        public string? ThreadScope { get; set; }
        
        [Column("AgentName")]
        public string? AgentName { get; set; }
        
        [Column("Context")]
        public string? Context { get; set; }
        
        [Column("analized")]
        public bool Analized { get; set; }
        
        [Column("chat_text")]
        public string? ChatText { get; set; } // Chat-style formatted text for UI
        
        [Column("Result")]
        public string? Result { get; set; } // Optional outcome: SUCCESS / FAILED / null
        
        // Concurrency token for optimistic locking
        [Timestamp]
        public byte[]? RowVersion { get; set; }
    }
}
