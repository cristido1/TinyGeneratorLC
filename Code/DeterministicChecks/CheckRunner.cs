namespace TinyGenerator.Services;

public static class CheckRunner
{
    public static CommandModelExecutionService.DeterministicValidationResult Execute(
        string? output,
        params IDeterministicCheck[] checks)
    {
        if (checks == null || checks.Length == 0)
        {
            return new CommandModelExecutionService.DeterministicValidationResult(true, null);
        }

        var text = output ?? string.Empty;
        foreach (var check in checks)
        {
            if (check == null)
            {
                continue;
            }

            check.TextToCheck = text;
            var result = check.Execute();
            if (!result.Successed)
            {
                var reason = string.IsNullOrWhiteSpace(result.Message)
                    ? $"Deterministic check failed: {check.Rule}"
                    : result.Message;
                return new CommandModelExecutionService.DeterministicValidationResult(false, reason);
            }
        }

        return new CommandModelExecutionService.DeterministicValidationResult(true, null);
    }
}

