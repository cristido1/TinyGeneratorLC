namespace TinyGenerator.Services;

public sealed class CheckAlwaysSuccess : CheckBase
{
    public override string Rule => "Check deterministico sempre riuscito.";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        _ = textToCheck;
        return new DeterministicResult
        {
            Successed = true,
            Message = "ok",
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
    }
}

