namespace TinyGenerator.Services;

public sealed class CheckAmbientTagMinimumCount : CheckBase
{
    public override string Rule => "Numero minimo di tag ambient per chunk.";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var minTags = Math.Max(0, GetOption("MinAmbientTags", 0));
        var tags = StoryTaggingService.ParseAmbientMapping(textToCheck ?? string.Empty);
        var count = tags.Count;
        var ok = count >= minTags;
        var defaultMessage = $"Hai inserito solo {count} tag [RUMORI]. Devi inserirne almeno {minTags}.";
        var failMessageTemplate = GetOption("ErrorMessage", defaultMessage);
        var failMessage = failMessageTemplate
            .Replace("{count}", count.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{min}", minTags.ToString(), StringComparison.OrdinalIgnoreCase);

        return new DeterministicResult
        {
            Successed = ok,
            Message = ok ? "ok" : failMessage,
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
    }
}
