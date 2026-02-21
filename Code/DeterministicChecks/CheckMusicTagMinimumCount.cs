namespace TinyGenerator.Services;

public sealed class CheckMusicTagMinimumCount : CheckBase
{
    public override string Rule => "Numero minimo di tag music per chunk.";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var requiredTags = Math.Max(0, GetOption("RequiredTags", 0));
        var minLineDistance = Math.Max(0, GetOption("MinLineDistance", 20));
        var tags = StoryTaggingService.ParseMusicMapping(textToCheck ?? string.Empty);
        tags = StoryTaggingService.FilterMusicTagsByProximity(tags, minLineDistance);
        var count = tags.Count;
        if (count < requiredTags)
        {
            var template = GetOption(
                "ErrorMessage",
                "Hai inserito solo {count} righe valide. Devi inserire ALMENO {required} indicazioni musicali (formato: ID emozione [secondi]).");
            var message = template
                .Replace("{count}", count.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{required}", requiredTags.ToString(), StringComparison.OrdinalIgnoreCase);
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
