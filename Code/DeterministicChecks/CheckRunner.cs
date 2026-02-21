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

            var result = check.Execute(text);
            if (!result.Successed)
            {
                var generic = string.IsNullOrWhiteSpace(check.GenericErrorDescription)
                    ? check.Rule
                    : check.GenericErrorDescription;
                var detail = string.IsNullOrWhiteSpace(result.Message)
                    ? "failed"
                    : result.Message;
                var reason = $"GENERIC_ERROR: {generic} | DETAIL: {detail}";
                return new CommandModelExecutionService.DeterministicValidationResult(false, reason);
            }
        }

        return new CommandModelExecutionService.DeterministicValidationResult(true, null);
    }
}
