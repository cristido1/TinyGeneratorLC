namespace TinyGenerator.Services;

public sealed class CheckTextValidation : CheckBase
{
    public override string Rule => "Validazione testuale narrativa.";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var service = GetOptionRaw("TextValidationService") as TextValidationService;
        if (service == null)
        {
            return Build(false, "TextValidationService non disponibile", started);
        }

        var storyHistory = GetOption("StoryHistory", string.Empty);
        var phase = GetOption("Phase", string.Empty);
        var runId = GetOption("RunId", string.Empty);
        var agentIdentity = GetOption("AgentIdentity", string.Empty);
        var logger = GetOptionRaw("Logger") as ICustomLogger;

        var text = (textToCheck ?? string.Empty).Trim();
        var validation = service.Validate(text, storyHistory);
        if (validation.IsValid)
        {
            return Build(true, "ok", started);
        }

        if (RequiresModelBasedValidation(validation.Reason))
        {
            if (!string.IsNullOrWhiteSpace(runId))
            {
                logger?.Append(
                    runId,
                    $"Text validation delegata al response_checker ({agentIdentity}): {validation.Reason}",
                    "warning");
            }
            return Build(true, "ok", started);
        }

        if (IsRelaxableForCurrentPhase(validation.Reason, phase))
        {
            if (!string.IsNullOrWhiteSpace(runId))
            {
                logger?.Append(
                    runId,
                    $"Text validation relax ({agentIdentity}): {validation.Reason} | phase={phase}",
                    "warning");
            }
            return Build(true, "ok", started);
        }

        return Build(false, $"Text validation ({agentIdentity}): {validation.Reason}", started);
    }

    private static bool IsRelaxableForCurrentPhase(string? reason, string phase)
    {
        _ = reason;
        _ = phase;
        return false;
    }

    private static bool RequiresModelBasedValidation(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.Contains("assenza di eventi reali", StringComparison.OrdinalIgnoreCase);
    }

    private static DeterministicResult Build(bool ok, string message, DateTime started)
        => new()
        {
            Successed = ok,
            Message = message,
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
}
