namespace TinyGenerator.Services;

[Obsolete("Check non affidabile per valutazione semantica in italiano. Obsoleto per NRE: usare IAgentChecker (nre_evaluator).")]
public sealed class CheckCliffhangerEnding : CheckBase
{
    public override string Rule => "Il chunk deve terminare in tensione aperta (cliffhanger).";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var agentIdentity = GetOption("AgentIdentity", string.Empty);
        var text = (textToCheck ?? string.Empty).Trim();

        if (EndsInTension(text, out var reason))
        {
            return new DeterministicResult
            {
                Successed = true,
                Message = "ok",
                CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
            };
        }

        var prefix = string.IsNullOrWhiteSpace(agentIdentity) ? string.Empty : $" ({agentIdentity})";
        return new DeterministicResult
        {
            Successed = false,
            Message = $"Cliffhanger validation{prefix}: {reason}",
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
    }

    private static bool EndsInTension(string text, out string reason)
    {
        reason = string.Empty;
        var t = (text ?? string.Empty).Trim();
        if (t.Length < 40)
        {
            reason = "Il chunk e troppo corto.";
            return false;
        }

        var lower = t.ToLowerInvariant();
        var forbiddenEndings = new[]
        {
            "fine.", "the end", "e vissero felici", "epilogo", "conclusione"
        };
        if (forbiddenEndings.Any(f => lower.EndsWith(f)))
        {
            reason = "Il chunk sembra una conclusione (vietato).";
            return false;
        }

        if (t.EndsWith("...") || t.EndsWith("...\"") || t.EndsWith("?") || t.EndsWith("!") || t.EndsWith("-") || t.EndsWith(":"))
        {
            return true;
        }

        //if (t.EndsWith("."))
        //{
        //    reason = "Il chunk termina con un punto fermo (serve tensione aperta).";
        //    return false;
        //}

        var lastLine = t.Split('\n').LastOrDefault()?.Trim() ?? t;
        if (lastLine.EndsWith("...") || lastLine.EndsWith("?") || lastLine.EndsWith("!") || lastLine.EndsWith("-") || lastLine.EndsWith(":"))
        {
            return true;
        }

        //reason = "Il chunk non termina in tensione aperta (usa ? / ... / ! / -).";
        return true;
    }
}
