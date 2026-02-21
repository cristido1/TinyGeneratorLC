using System.Collections.Generic;

namespace TinyGenerator.Services;

public sealed class CheckDialogueLineSpeakerEmotion : CheckBase
{
    public override string Rule => "Per una riga dialogo deve esserci PERSONAGGIO ed EMOZIONE.";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var lineId = GetOption("LineId", 0);
        if (lineId <= 0)
        {
            return Build(false, "LineId non valido per CheckDialogueLineSpeakerEmotion.", started);
        }

        var mapping = FormatterV2.ParseIdToTagsMapping(textToCheck ?? string.Empty);
        if (!mapping.TryGetValue(lineId, out var tags) || string.IsNullOrWhiteSpace(tags))
        {
            return Build(false, $"Riga {lineId:000}: mapping mancante.", started);
        }

        var hasCharacter = tags.IndexOf("[PERSONAGGIO:", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasEmotion = tags.IndexOf("[EMOZIONE:", StringComparison.OrdinalIgnoreCase) >= 0;

        if (!hasCharacter || !hasEmotion)
        {
            var missing = new List<string>(2);
            if (!hasCharacter) missing.Add("PERSONAGGIO");
            if (!hasEmotion) missing.Add("EMOZIONE");
            return Build(false, $"Riga {lineId:000}: tag mancanti -> {string.Join(", ", missing)}.", started);
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
