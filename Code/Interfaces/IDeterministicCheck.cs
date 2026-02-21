namespace TinyGenerator.Services;

public interface IDeterministicResult
{
    bool Successed { get; }
    string Message { get; }
    long CheckDurationMs { get; }
}

public interface IDeterministicCheck
{
    string Rule { get; }
    string GenericErrorDescription { get; }
    Microsoft.Extensions.Options.IOptions<object>? Options { get; set; }
    IDeterministicResult Execute(string textToCheck);
}
