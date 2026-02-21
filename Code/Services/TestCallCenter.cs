using TinyGenerator.Models;

namespace TinyGenerator.Services;

public class TestCallCenter : ITestCallCenter
{
    private readonly ICallCenter _callCenter;

    public TestCallCenter(ICallCenter callCenter)
    {
        _callCenter = callCenter ?? throw new ArgumentNullException(nameof(callCenter));
    }

    public async Task<TestCallResult> CallAsync(TestCallRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.ModelName)) throw new ArgumentNullException(nameof(request.ModelName));

        var history = request.History ?? new ChatHistory();
        if (history.Messages.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                history.AddSystem(request.SystemPrompt);
            }

            history.AddUser(request.Prompt ?? string.Empty);
        }

        var options = BuildOptions(request);
        EnsureMandatoryChecks(options);

        var agent = new Agent
        {
            Name = "test_call_center",
            Role = "test_runner",
            ModelName = request.ModelName.Trim(),
            Instructions = request.SystemPrompt,
            Temperature = request.Temperature,
            TopP = request.TopP,
            NumPredict = request.NumPredict
        };

        var result = await _callCenter.CallAgentAsync(
            request.StoryId,
            request.ThreadId,
            agent,
            history,
            options,
            cancellationToken).ConfigureAwait(false);

        return new TestCallResult
        {
            Success = result.Success,
            ResponseText = result.ResponseText,
            FailureReason = result.FailureReason,
            Duration = result.Duration,
            Attempts = result.Attempts,
            ModelUsed = result.ModelUsed,
            UpdatedHistory = result.UpdatedHistory
        };
    }

    private static CallOptions BuildOptions(TestCallRequest request)
    {
        var options = new CallOptions
        {
            Operation = string.IsNullOrWhiteSpace(request.Operation) ? "model_test" : request.Operation.Trim(),
            Timeout = request.Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(60) : request.Timeout,
            MaxRetries = Math.Max(0, request.MaxRetries),
            UseResponseChecker = request.UseResponseChecker,
            AskFailExplanation = request.AskFailExplanation,
            AllowFallback = request.AllowFallback,
            ResponseFormat = request.ResponseFormat
        };

        foreach (var check in request.DeterministicChecks)
        {
            options.DeterministicChecks.Add(check);
        }

        return options;
    }

    private static void EnsureMandatoryChecks(CallOptions options)
    {
        if (options.DeterministicChecks.Any(c => c is CheckEmpty || c is NonEmptyResponseCheck))
        {
            return;
        }

        options.DeterministicChecks.Insert(0, new CheckEmpty());
    }
}

// Alias compatibile con la richiesta utente "TestCallCanter".
public sealed class TestCallCanter : TestCallCenter
{
    public TestCallCanter(ICallCenter callCenter)
        : base(callCenter)
    {
    }
}
