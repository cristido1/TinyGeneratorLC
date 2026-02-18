namespace TinyGenerator.Services;

public sealed class CheckMinLength : CheckBase
{
    public override string Rule => "Lunghezza minima testo.";

    public override IDeterministicResult Execute()
    {
        var started = DateTime.UtcNow;
        var minLength = Math.Max(0, GetOption("MinLength", 0));
        var normalized = (TextToCheck ?? string.Empty).Trim();
        var ok = normalized.Length >= minLength;
        var defaultMessage = $"Output troppo corto: richiesti almeno {minLength} caratteri, ottenuti {normalized.Length}";
        var failMessage = GetOption("ErrorMessage", defaultMessage);

        return new DeterministicResult
        {
            Successed = ok,
            Message = ok ? "ok" : failMessage,
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
    }
}

