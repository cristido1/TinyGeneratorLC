namespace TinyGenerator.Services;

public sealed class CheckEmpty : CheckBase
{
    public override string Rule => "La risposta non deve essere vuota o whitespace.";
    public override string GenericErrorDescription => "Risposta vuota";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var ok = !string.IsNullOrWhiteSpace(textToCheck);
        var failMessage = GetOption("ErrorMessage", "deterministic_empty: risposta vuota");
        return new DeterministicResult
        {
            Successed = ok,
            Message = ok ? "ok" : failMessage,
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
    }
}
