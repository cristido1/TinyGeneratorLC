namespace TinyGenerator.Models
{
    public class LogAnalysis
    {
        public int Id { get; set; }
        public string ThreadId { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public string RunScope { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool Succeeded { get; set; }
    }
}
