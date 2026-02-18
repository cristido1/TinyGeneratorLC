namespace TinyGenerator.Services;

public sealed class CheckMaxLength : CheckBase
{
    public override string Rule => "Lunghezza massima testo.";

    public override IDeterministicResult Execute()
    {
        var started = DateTime.UtcNow;
        var maxLength = Math.Max(0, GetOption("MaxLength", int.MaxValue));
        var normalized = (TextToCheck ?? string.Empty).Trim();
        var ok = normalized.Length <= maxLength;
        var defaultMessage = $"Output troppo lungo: massimo {maxLength} caratteri, ottenuti {normalized.Length}";
        var failMessage = GetOption("ErrorMessage", defaultMessage);

        return new DeterministicResult
        {
            Successed = ok,
            Message = ok ? "ok" : failMessage,
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
    }
}

