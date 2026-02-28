using System.Text.RegularExpressions;

namespace TinyGenerator.Services;

[Obsolete("Check non affidabile per valutazione semantica in italiano. Obsoleto per NRE: usare IAgentChecker (nre_evaluator).")]
public sealed class CheckIrreversibleConsequence : CheckBase
{
    public override string Rule => "Conseguenza irreversibile coerente con CostSeverity (military_strict).";
    public override string GenericErrorDescription => "Conseguenza irreversibile insufficiente per CostSeverity";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var severity = NormalizeSeverity(GetOption("CostSeverity", "medium"));
        var intensity = NormalizeIntensity(GetOption("CombatIntensity", "normal"));
        var text = Normalize(textToCheck);

        if (string.IsNullOrWhiteSpace(text))
        {
            return Build(true, "ok", started);
        }

        if (severity == "low")
        {
            // low severity does not force irreversible loss. Intensity constraints may still apply.
            return EvaluateIntensity(text, intensity, started);
        }

        if (severity == "medium")
        {
            var mediumSignals = new[]
            {
                "danno permanente",
                "danneggiamento permanente",
                "compromissione irreversibile",
                "sistema compromesso",
                "non recuperabile",
                "ritirata forzata",
                "perdita posizione strategica",
                "asset compromesso"
            };

            var mediumOk = mediumSignals.Any(s => text.Contains(s, StringComparison.Ordinal));
            if (!mediumOk)
            {
                return Build(false, "CostSeverity=medium richiede almeno danno/compromissione permanente o perdita operativa concreta.", started);
            }

            return EvaluateIntensity(text, intensity, started);
        }

        var highSignals = new[]
        {
            "morto",
            "ucciso",
            "distrutto",
            "esploso",
            "perdita totale",
            "non recuperabile"
        };

        var severityHighOk = highSignals.Any(s => text.Contains(s, StringComparison.Ordinal));
        if (!severityHighOk)
        {
            return Build(false, "CostSeverity=high richiede almeno una conseguenza grave esplicita (morte/distruzione/perdita totale/non recuperabile).", started);
        }

        return EvaluateIntensity(text, intensity, started);
    }

    private static string NormalizeSeverity(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v switch
        {
            "low" => "low",
            "high" => "high",
            _ => "medium"
        };
    }

    private static string NormalizeIntensity(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v switch
        {
            "low" => "low",
            "high" => "high",
            "total_war" => "total_war",
            _ => "normal"
        };
    }

    private static string Normalize(string? value)
    {
        var s = (value ?? string.Empty).ToLowerInvariant().Trim();
        s = Regex.Replace(s, @"\s+", " ");
        return s;
    }

    private static DeterministicResult EvaluateIntensity(string text, string intensity, DateTime started)
    {
        if (intensity == "high")
        {
            var highSignals = new[]
            {
                "distrutto", "esploso", "frantumato", "perdita totale", "irreparabile", "morti", "cadaveri"
            };
            if (!highSignals.Any(s => text.Contains(s, StringComparison.Ordinal)))
            {
                return Build(false, "CombatIntensity=high richiede almeno una distruzione o morte multipla.", started);
            }
        }

        if (intensity == "total_war")
        {
            var shipDestroyedSignals = new[]
            {
                "nave distrutta", "navi distrutte", "perdita totale", "irrecuperabile", "irreparabile", "esploso", "esplosa"
            };
            var multiLossSignals = new[]
            {
                "morti", "cadaveri", "perdite elevate", "perdite multiple"
            };
            var boardingSignals = new[]
            {
                "abbordaggio", "incursione", "sfondamento", "breccia nello scafo", "scontro ravvicinato"
            };

            var hasShipDestroyed = shipDestroyedSignals.Any(s => text.Contains(s, StringComparison.Ordinal));
            var hasMultiLoss = multiLossSignals.Any(s => text.Contains(s, StringComparison.Ordinal));
            var hasBoarding = boardingSignals.Any(s => text.Contains(s, StringComparison.Ordinal));

            if (!(hasShipDestroyed && hasMultiLoss && hasBoarding))
            {
                return Build(false, "CombatIntensity=total_war richiede nave distrutta/irrecuperabile + perdite multiple + abbordaggio/scontro ravvicinato.", started);
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
