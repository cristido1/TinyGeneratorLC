using System.Text.Json;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands
{
    public class StartMultiStepStoryCommand
    {
        private readonly string _theme;
        private readonly int _writerAgentId;
        private readonly Guid _generationId;
        private readonly DatabaseService _database;
        private readonly MultiStepOrchestrationService _orchestrator;
        private readonly CommandDispatcher _dispatcher;
        private readonly ICustomLogger _logger;

        public StartMultiStepStoryCommand(
            string theme,
            int writerAgentId,
            Guid generationId,
            DatabaseService database,
            MultiStepOrchestrationService orchestrator,
            CommandDispatcher dispatcher,
            ICustomLogger logger)
        {
            _theme = theme;
            _writerAgentId = writerAgentId;
            _generationId = generationId;
            _database = database;
            _orchestrator = orchestrator;
            _dispatcher = dispatcher;
            _logger = logger;
        }

        public async Task ExecuteAsync(CancellationToken ct = default)
        {
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

                // Create story record first  
                var storyId = _database.InsertSingleStory(_theme, "[Multi-step generation in progress...]", agentId: _writerAgentId);

                _logger.Log("Information", "MultiStep", $"Created story record {storyId}");

                // Start task execution
                var executionId = await _orchestrator.StartTaskExecutionAsync(
                    taskType: "story",
                    entityId: storyId,
                    stepPrompt: template.StepPrompt,
                    executorAgentId: agent.Id,
                    checkerAgentId: null, // Use default checker from task_types
                    configOverrides: null,
                    initialContext: _theme, // ← Pass user theme as initial context!
                    threadId: threadId,
                    templateInstructions: string.IsNullOrWhiteSpace(template.Instructions) ? null : template.Instructions
                );

                _logger.Log("Information", "MultiStep", $"Started task execution {executionId}");

                // Enqueue execution command
                _dispatcher.Enqueue(
                    "ExecuteMultiStepTask",
                    async ctx => {
                        var executeCmd = new ExecuteMultiStepTaskCommand(
                            executionId,
                            _generationId,
                            _orchestrator,
                            _database,
                            _logger
                        );
                        await executeCmd.ExecuteAsync(ctx.CancellationToken);
                        return new CommandResult(true, "Task execution completed");
                    },
                    runId: _generationId.ToString()
                );

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
