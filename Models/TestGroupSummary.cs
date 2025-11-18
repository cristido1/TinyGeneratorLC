namespace TinyGenerator.Models
{
    public class TestGroupSummary
    {
        public string Group { get; set; } = string.Empty;
        public int RunId { get; set; }
        public int Score { get; set; }
        public int Passed { get; set; }
        public int Total { get; set; }
        public string? Timestamp { get; set; }
        public bool Success { get; set; }
    }
}
