using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed class AlwaysOnStorySummariesCommand : ICommand
{
    private readonly BatchSummarizeStoriesEnqueuerCommand _inner;

    public AlwaysOnStorySummariesCommand(
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        ICommandEnqueuer dispatcher,
        ICustomLogger logger,
        IServiceScopeFactory? scopeFactory = null,
        int minScore = 60)
    {
        _inner = new BatchSummarizeStoriesEnqueuerCommand(
            database,
            kernelFactory,
            dispatcher,
            logger,
            scopeFactory,
            minScore);
    }

    public string CommandName => "always_on_story_summaries";
    public int Priority => 2;
    public bool Batch => false; // usa agente: va accodato

    public Task<CommandResult> ExecuteAsync(CancellationToken ct = default) => _inner.ExecuteAsync(ct);
}
