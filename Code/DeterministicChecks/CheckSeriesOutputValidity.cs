using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed class CheckSeriesOutputValidity : CheckBase
{
    public override string Rule => "Output serie con tag richiesti e validazioni custom.";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var roleCode = GetOption("RoleCode", "series_agent");
        var requiredTags = GetOptionRaw("RequiredTags") as IReadOnlyCollection<string> ?? Array.Empty<string>();
        var validationRules = GetOptionRaw("ValidationRules") as SeriesValidationRules;
        var validationFunc = GetOptionRaw("ValidationFunc") as Func<string, string?>;

        var text = textToCheck ?? string.Empty;
        if (validationRules != null && requiredTags.Count > 0 &&
            !validationRules.HasRequiredTags(text, requiredTags, out var missingTags))
        {
            var missingText = missingTags.Count > 0 ? string.Join(", ", missingTags) : "tag richiesti";
            return Build(false, $"Output privo di tag richiesti per {roleCode}: {missingText}", started);
        }

        if (validationFunc != null)
        {
            var error = validationFunc(text);
            if (!string.IsNullOrWhiteSpace(error))
            {
                return Build(false, error!, started);
            }
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
