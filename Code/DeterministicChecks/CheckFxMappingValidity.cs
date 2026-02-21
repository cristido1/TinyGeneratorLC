namespace TinyGenerator.Services;

public sealed class CheckFxMappingValidity : CheckBase
{
    public override string Rule => "Formato mapping FX valido e numero minimo tag.";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var tags = StoryTaggingService.ParseFxMapping(textToCheck ?? string.Empty, out var invalidLines);
        if (invalidLines > 0)
        {
            var template = GetOption("InvalidLinesErrorMessage", "Formato FX non valido: {invalid} righe non rispettano il formato richiesto.");
            var message = template.Replace("{invalid}", invalidLines.ToString(), StringComparison.OrdinalIgnoreCase);
            return Build(false, message, started);
        }

        var minFxTags = Math.Max(0, GetOption("MinFxTags", 0));
        if (tags.Count < minFxTags)
        {
            var template = GetOption(
                "MinFxTagsErrorMessage",
                "Hai inserito {count} righe valide. Devi inserire ALMENO {min} effetti sonori (formato: ID descrizione [secondi]).");
            var message = template
                .Replace("{count}", tags.Count.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{min}", minFxTags.ToString(), StringComparison.OrdinalIgnoreCase);
            return Build(false, message, started);
        }

        return Build(true, "ok", started);
    }

    private static DeterministicResult Build(bool ok, string message, DateTime started)
        => new()
        {
            Successed = ok,
            Message = message,
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
}
