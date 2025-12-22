using System;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands
{
    public interface IStoryCommand
    {
        Task<CommandResult> ExecuteAsync(CancellationToken ct = default);
    }

    public sealed class CreateStoryCommand : IStoryCommand
    {
        private readonly StoriesService _stories;
        private readonly string _prompt;
        private readonly string _storyText;
        private readonly string? _title;
        private readonly int? _agentId;
        private readonly int? _statusId;
        private readonly int? _modelId;

        public CreateStoryCommand(StoriesService stories, string prompt, string storyText, int? agentId = null, int? statusId = null, string? title = null, int? modelId = null)
        {
            _stories = stories ?? throw new ArgumentNullException(nameof(stories));
            _prompt = prompt ?? string.Empty;
            _storyText = storyText ?? string.Empty;
            _agentId = agentId;
            _statusId = statusId;
            _title = title;
            _modelId = modelId;
        }

        public Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
        {
            var id = _stories.InsertSingleStory(_prompt, _storyText, modelId: _modelId, agentId: _agentId, statusId: _statusId, title: _title);
            return Task.FromResult(new CommandResult(true, $"Storia creata (id={id})"));
        }
    }

    public sealed class EvaluateStoryCommand : IStoryCommand
    {
        private readonly StoriesService _stories;
        private readonly long _storyId;
        private readonly int _agentId;

        public EvaluateStoryCommand(StoriesService stories, long storyId, int agentId)
        {
            _stories = stories ?? throw new ArgumentNullException(nameof(stories));
            _storyId = storyId;
            _agentId = agentId;
        }

        public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
        {
            var (success, score, error) = await _stories.EvaluateStoryWithAgentAsync(_storyId, _agentId);
            return new CommandResult(success, success ? $"Valutazione completata. Score medio: {score:F1}" : error);
        }
    }

    public sealed class EvaluateCoherenceCommand : IStoryCommand
    {
        private readonly StoriesService _stories;
        private readonly long _storyId;
        private readonly int _agentId;

        public EvaluateCoherenceCommand(StoriesService stories, long storyId, int agentId)
        {
            _stories = stories ?? throw new ArgumentNullException(nameof(stories));
            _storyId = storyId;
            _agentId = agentId;
        }

        public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
        {
            var (success, score, error) = await _stories.EvaluateStoryWithAgentAsync(_storyId, _agentId);
            return new CommandResult(success, success ? $"Valutazione coerenza completata. Score: {score:F2}" : error);
        }
    }

    public sealed class EvaluateActionPacingCommand : IStoryCommand
    {
        private readonly StoriesService _stories;
        private readonly long _storyId;
        private readonly int _agentId;

        public EvaluateActionPacingCommand(StoriesService stories, long storyId, int agentId)
        {
            _stories = stories ?? throw new ArgumentNullException(nameof(stories));
            _storyId = storyId;
            _agentId = agentId;
        }

        public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
        {
            var (success, score, error) = await _stories.EvaluateActionPacingWithAgentAsync(_storyId, _agentId);
            return new CommandResult(success, success ? "Valutazione azione/ritmo completata" : error);
        }
    }

    public sealed class GenerateTtsSchemaCommand : IStoryCommand
    {
        private readonly StoriesService _stories;
        private readonly long _storyId;

        public GenerateTtsSchemaCommand(StoriesService stories, long storyId)
        {
            _stories = stories ?? throw new ArgumentNullException(nameof(stories));
            _storyId = storyId;
        }

        public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
        {
            var (success, message) = await _stories.GenerateTtsSchemaJsonAsync(_storyId);
            return new CommandResult(success, message ?? (success ? "Schema TTS generato" : "Errore generazione schema TTS"));
        }
    }

    public sealed class GenerateTtsVoiceCommand : IStoryCommand
    {
        private readonly StoriesService _stories;
        private readonly long _storyId;

        public GenerateTtsVoiceCommand(StoriesService stories, long storyId)
        {
            _stories = stories ?? throw new ArgumentNullException(nameof(stories));
            _storyId = storyId;
        }

        public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
        {
            var (success, message) = await _stories.AssignVoicesAsync(_storyId);
            return new CommandResult(success, message ?? (success ? "Assegnazione voci completata" : "Errore assegnazione voci"));
        }
    }

    /// <summary>
    /// Stub commands for future operations (music/effects/ambient/mixer).
    /// They currently return not implemented to keep the dispatcher contract.
    /// </summary>
    public sealed class StoryStubCommand : IStoryCommand
    {
        private readonly string _operation;

        public StoryStubCommand(string operation)
        {
            _operation = operation;
        }

        public Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new CommandResult(false, $"{_operation}: non implementato"));
        }
    }
}
