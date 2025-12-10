namespace TinyGenerator.Models
{
    public class AppEventDefinition
    {
        public long? Id { get; set; }
        public string EventType { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool Enabled { get; set; }
        public bool Logged { get; set; }
        public bool Notified { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
    }
}
