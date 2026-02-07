namespace TinyGenerator.Services;

public sealed class SeriesGenerationOptions
{
    public SeriesRoleOptions Bible { get; set; } = new();
    public SeriesRoleOptions Characters { get; set; } = new();
    public SeriesRoleOptions Episodes { get; set; } = new();
    public SeriesRoleOptions Validator { get; set; } = new();

    public sealed class SeriesRoleOptions
    {
        public int MaxAttempts { get; set; } = 3;
        public int RetryDelaySeconds { get; set; } = 2;
        public int TimeoutSec { get; set; } = 400;
        public bool DiagnoseOnFinalFailure { get; set; } = true;
        public bool UseResponseChecker { get; set; } = false;
        public int ExplainAfterAttempt { get; set; } = 0;
    }
}
