using TinyGenerator.Models;
using System.Text.RegularExpressions;

namespace TinyGenerator.Services;

public class TestCallCenter : ITestCallCenter
{
    private readonly ICallCenter _callCenter;
    private readonly DatabaseService _database;

    public TestCallCenter(ICallCenter callCenter, DatabaseService database)
    {
        _callCenter = callCenter ?? throw new ArgumentNullException(nameof(callCenter));
        _database = database ?? throw new ArgumentNullException(nameof(database));
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
            SystemPrompt = request.SystemPrompt,
            Temperature = request.Temperature,
            TopP = request.TopP,
            NumPredict = request.NumPredict,
            Thinking = request.Thinking ?? _database.ResolveEffectiveThinking(
                agent: null,
                modelName: request.ModelName)
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
            ResponseText = SanitizeTestResponse(result.ResponseText),
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

    private static string SanitizeTestResponse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw;

        // Remove <think>...</think> blocks (case-insensitive, multiline)
        text = Regex.Replace(
            text,
            @"<\s*think\s*>[\s\S]*?<\s*/\s*think\s*>",
            string.Empty,
            RegexOptions.IgnoreCase);

        // Remove fenced blocks explicitly marked as thinking/reasoning
        text = Regex.Replace(
            text,
            @"```(?:thinking|reasoning|analysis)?\s*[\s\S]*?```",
            m =>
            {
                var header = m.Value.Length >= 3 ? m.Value[..Math.Min(30, m.Value.Length)] : string.Empty;
                return Regex.IsMatch(header, @"```(?:thinking|reasoning|analysis)\b", RegexOptions.IgnoreCase)
                    ? string.Empty
                    : m.Value;
            },
            RegexOptions.IgnoreCase);

        return text.Trim();
    }
}

// Alias compatibile con la richiesta utente "TestCallCanter".
public sealed class TestCallCanter : TestCallCenter
{
    public TestCallCanter(ICallCenter callCenter, DatabaseService database)
        : base(callCenter, database)
    {
    }
}
