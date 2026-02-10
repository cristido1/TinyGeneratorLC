using System.Text.Json;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands
{
    public class StartMultiStepStoryCommand : ICommand
    {
        private readonly string _theme;
        private readonly string? _title;
        private readonly int _writerAgentId;
        private readonly Guid _generationId;
        private readonly DatabaseService _database;
        private readonly MultiStepOrchestrationService _orchestrator;
        private readonly ICommandEnqueuer _dispatcher;
        private readonly ICustomLogger _logger;

        public StartMultiStepStoryCommand(
            string theme,
            int writerAgentId,
            Guid generationId,
            DatabaseService database,
            MultiStepOrchestrationService orchestrator,
            ICommandEnqueuer dispatcher,
            ICustomLogger logger,
            string? title = null)
        {
            _theme = theme;
            _writerAgentId = writerAgentId;
            _generationId = generationId;
            _database = database;
            _orchestrator = orchestrator;
            _dispatcher = dispatcher;
            _logger = logger;
            _title = title;
        }

        public async Task ExecuteAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var threadId = _generationId.GetHashCode();

            _logger.Log("Information", "MultiStep", $"Starting multi-step story generation - Agent: {_writerAgentId}, Theme: {_theme}");

            try
            {
                // Load agent
                var agent = _database.GetAgentById(_writerAgentId);
                if (agent == null)
                {
                    _logger.Log("Error", "MultiStep", $"Agent {_writerAgentId} not found, aborting");
                    return;
                }

                // Check if agent has multi-step template
                if (!agent.MultiStepTemplateId.HasValue)
                {
                    _logger.Log("Warning", "MultiStep", $"Agent {agent.Name} has no multi-step template, falling back to single-step generation");

                    // TODO: Fallback to single-step StoryGeneratorService
                    // For now, just log and return
                    _logger.Log("Error", "MultiStep", $"Single-step fallback not yet implemented");
                    return;
                }

                // Load template
                var template = _database.GetStepTemplateById(agent.MultiStepTemplateId.Value);
                if (template == null)
                {
                    _logger.Log("Error", "MultiStep", $"Template {agent.MultiStepTemplateId} not found, aborting");
                    return;
                }

                _logger.Log("Information", "MultiStep", $"Using template: {template.Name}");

                // Build config with characters_step (and title if provided) so it persists across reloads
                string? configOverrides = null;
                try
                {
                    var cfg = new Dictionary<string, object>();
                    if (template.CharactersStep.HasValue)
                    {
                        cfg["characters_step"] = template.CharactersStep.Value;
                    }
                    if (!string.IsNullOrWhiteSpace(_title))
                    {
                        cfg["title"] = _title;
                    }
                    if (cfg.Count > 0)
                    {
                        configOverrides = JsonSerializer.Serialize(cfg);
                    }
                }
                catch
                {
                    configOverrides = null;
                }

                // Don't create story record yet - will be created after successful generation
                // Store model info for later use (id-only)
                int? modelId = agent.ModelId;

                // Start task execution without entity (story will be created on success)
                var executionId = await _orchestrator.StartTaskExecutionAsync(
                    taskType: "story",
                    entityId: null, // No entity yet - will be created on success
                    stepPrompt: template.StepPrompt,
                    executorAgentId: agent.Id,
                    checkerAgentId: null, // Use default checker from task_types
                    configOverrides: configOverrides,
                    initialContext: _theme, // â† Pass user theme as initial context!
                    threadId: threadId,
                    templateInstructions: string.IsNullOrWhiteSpace(template.Instructions) ? null : template.Instructions
                );

                _logger.Log("Information", "MultiStep", $"Started task execution {executionId} (story will be created on success)");

                // Enqueue execution command with different runId suffix to avoid conflict
                var executeRunId = $"{_generationId}_exec";
                var executeCommand = new DelegateCommand(
                    "ExecuteMultiStepTask",
                    async ctx =>
                    {
                        var executeCmd = new ExecuteMultiStepTaskCommand(
                            executionId,
                            _generationId,
                            _orchestrator,
                            _database,
                            _logger,
                            dispatcher: _dispatcher
                        );
                        await executeCmd.ExecuteAsync(ctx.CancellationToken);
                        return new CommandResult(true, "Task execution completed");
                    });

                _dispatcher.Enqueue(
                    executeCommand,
                    runId: executeRunId,
                    metadata: new Dictionary<string, string>
                    {
                        ["agentName"] = agent.Name ?? string.Empty,
                        ["modelName"] = modelId.HasValue ? (_database.GetModelInfoById(modelId.Value)?.Name ?? string.Empty) : string.Empty,
                        ["operation"] = "story_multi_step"
                    }
                );
                ct.ThrowIfCancellationRequested();

                _logger.Log("Information", "MultiStep", $"Enqueued ExecuteMultiStepTaskCommand");
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "MultiStep", $"Failed to start multi-step story: {ex.Message}");
                throw;
            }
        }
    }
}


