namespace TinyGenerator.Services;

public sealed class CheckMinimumGrowthPercent : CheckBase
{
    public override string Rule => "Crescita minima del testo rispetto al sorgente.";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var normalized = (textToCheck ?? string.Empty).Trim();
        var sourceText = GetOption("SourceText", string.Empty)?.Trim() ?? string.Empty;
        var minGrowthPercent = Math.Max(0d, GetOption("MinGrowthPercent", 0d));
        var sourceLength = sourceText.Length;

        var ok = true;
        string message = "ok";

        if (sourceLength > 0 && minGrowthPercent > 0)
        {
            var requiredMinLength = (int)Math.Ceiling(sourceLength * (1d + (minGrowthPercent / 100d)));
            if (normalized.Length < requiredMinLength)
            {
                ok = false;
                message = $"Output troppo corto rispetto al sorgente: richiesti almeno {requiredMinLength} caratteri ({minGrowthPercent:0.##}% oltre i {sourceLength} del sorgente), ottenuti {normalized.Length}";
            }
        }

        return new DeterministicResult
        {
            Successed = ok,
            Message = message,
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
    }
}
