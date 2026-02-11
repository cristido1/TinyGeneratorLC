using System.Text.Json;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands;

internal static class SeriesEpisodeExecutionSupport
{
    public static string? BuildConfigOverridesForNextEpisode(
        int serieId,
        int episodeNumber,
        string episodeTitle,
        PlannerMethod? plannerMethod,
        TipoPlanning? tipoPlanning,
        int? charactersStep,
        ICustomLogger logger)
    {
        try
        {
            var cfg = new Dictionary<string, object>
            {
                ["serie_id"] = serieId,
                ["serie_episode"] = episodeNumber,
                ["title"] = episodeTitle
            };

            if (plannerMethod != null)
            {
                cfg["planner_method_id"] = plannerMethod.Id;
                cfg["planner_method_code"] = plannerMethod.Code;
            }

            if (tipoPlanning != null)
            {
                cfg["tipo_planning_id"] = tipoPlanning.Id;
                cfg["tipo_planning_code"] = tipoPlanning.Codice;
                cfg["tipo_planning_successione_stati"] = tipoPlanning.SuccessioneStati;
            }

            if (charactersStep.HasValue)
            {
                cfg["characters_step"] = charactersStep.Value;
            }

            return JsonSerializer.Serialize(cfg);
        }
        catch (Exception ex)
        {
            logger.Log("Warning", "SeriesEpisode", $"Failed to serialize config: {ex.Message}");
            return null;
        }
    }

    public static string? BuildConfigOverridesForExistingEpisode(
        int serieId,
        SeriesEpisode episode,
        PlannerMethod? plannerMethod,
        TipoPlanning? effectiveTipoPlanning,
        TipoPlanning? episodeTipoPlanning,
        TipoPlanning? seriesTipoPlanning,
        int? charactersStep,
        ICustomLogger logger)
    {
        try
        {
            var cfg = new Dictionary<string, object>
            {
                ["serie_id"] = serieId,
                ["serie_episode"] = episode.Number,
                ["title"] = string.IsNullOrWhiteSpace(episode.Title)
                    ? $"Episodio {episode.Number}"
                    : episode.Title!
            };

            if (!string.IsNullOrWhiteSpace(episode.InitialPhase)) cfg["initial_phase"] = episode.InitialPhase!;
            if (!string.IsNullOrWhiteSpace(episode.StartSituation)) cfg["start_situation"] = episode.StartSituation!;
            if (!string.IsNullOrWhiteSpace(episode.EpisodeGoal)) cfg["episode_goal"] = episode.EpisodeGoal!;

            if (plannerMethod != null)
            {
                cfg["planner_method_id"] = plannerMethod.Id;
                cfg["planner_method_code"] = plannerMethod.Code;
            }

            if (effectiveTipoPlanning != null)
            {
                cfg["tipo_planning_id"] = effectiveTipoPlanning.Id;
                cfg["tipo_planning_code"] = effectiveTipoPlanning.Codice;
                cfg["tipo_planning_successione_stati"] = effectiveTipoPlanning.SuccessioneStati;
            }

            if (episodeTipoPlanning != null)
            {
                cfg["tipo_planning_source"] = "episode";
            }
            else if (seriesTipoPlanning != null)
            {
                cfg["tipo_planning_source"] = "series";
            }

            if (charactersStep.HasValue)
            {
                cfg["characters_step"] = charactersStep.Value;
            }

            return JsonSerializer.Serialize(cfg);
        }
        catch (Exception ex)
        {
            logger.Log("Warning", "SeriesEpisode", $"Config serialization failed: {ex.Message}");
            return null;
        }
    }

    public static void EnqueueExecuteMultiStepTask(
        ICommandEnqueuer dispatcher,
        Guid generationId,
        long executionId,
        MultiStepOrchestrationService orchestrator,
        DatabaseService database,
        ICustomLogger logger,
        string agentName,
        string modelName,
        string serieTitle,
        int episodeNumber,
        Func<CancellationToken, Task>? afterSuccess = null)
    {
        var executeRunId = $"{generationId}_exec";
        var executeCommand = new DelegateCommand(
            "ExecuteMultiStepTask",
            async ctx =>
            {
                var executeCmd = new ExecuteMultiStepTaskCommand(
                    executionId,
                    generationId,
                    orchestrator,
                    database,
                    logger,
                    dispatcher: dispatcher
                );
                await executeCmd.ExecuteAsync(ctx.CancellationToken).ConfigureAwait(false);

                if (afterSuccess != null)
                {
                    await afterSuccess(ctx.CancellationToken).ConfigureAwait(false);
                }

                return new CommandResult(true, "Series episode generation completed");
            });

        dispatcher.Enqueue(
            executeCommand,
            runId: executeRunId,
            metadata: new Dictionary<string, string>
            {
                ["agentName"] = agentName,
                ["modelName"] = modelName,
                ["operation"] = "series_episode",
                ["serieTitle"] = serieTitle,
                ["episodeNumber"] = episodeNumber.ToString()
            }
        );
    }
}
