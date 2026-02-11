using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TinyGenerator.Models;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed class ChunkProcessingService : IChunkProcessingService
{
    private readonly ModelExecutionOrchestrator _modelExecutionOrchestrator;

    public ChunkProcessingService(
        ILangChainKernelFactory kernelFactory,
        IServiceScopeFactory? scopeFactory = null,
        ICustomLogger? logger = null,
        ModelExecutionOrchestrator? modelExecutionOrchestrator = null)
    {
        _modelExecutionOrchestrator = modelExecutionOrchestrator
            ?? new ModelExecutionOrchestrator(
                kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory)),
                scopeFactory,
                logger);
    }

    public async Task<ChunkProcessResult> ProcessAsync(ChunkProcessRequest request, CancellationToken ct)
    {
        var executionResult = await _modelExecutionOrchestrator.ExecuteAsync(
            new ModelExecutionRequest
            {
                RoleCode = request.RoleCode,
                Agent = request.Agent,
                InitialModelId = request.CurrentModelId,
                InitialModelName = request.CurrentModelName,
                TriedModelNames = request.TriedModelNames,
                SystemPrompt = request.SystemPrompt,
                WorkInput = request.ChunkText,
                RunId = request.RunId,
                ChunkIndex = request.ChunkIndex,
                ChunkCount = request.ChunkCount,
                WorkLabel = "Ambient tagging",
                Options = BuildExecutionOptions(request.Tuning),
                WorkAsync = (bridge, token) => ProcessAmbientChunkAsync(request, bridge, token)
            },
            ct).ConfigureAwait(false);

        return new ChunkProcessResult(
            executionResult.OutputText,
            executionResult.ModelId,
            executionResult.ModelName,
            executionResult.UsedFallback);
    }

    private async Task<ModelWorkResult> ProcessAmbientChunkAsync(
        ChunkProcessRequest request,
        LangChainChatBridge bridge,
        CancellationToken ct)
    {
        try
        {
            var messages = new List<ConversationMessage>();
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                messages.Add(new ConversationMessage { Role = "system", Content = request.SystemPrompt });
            }

            messages.Add(new ConversationMessage { Role = "user", Content = request.ChunkText });

            string responseJson;
            using (LogScope.Push(
                       LogScope.Current ?? request.OperationScope,
                       operationId: null,
                       stepNumber: null,
                       maxStep: null,
                       agentName: request.Agent.Name,
                       agentRole: request.RoleCode))
            {
                responseJson = await bridge.CallModelWithToolsAsync(
                    messages,
                    new List<Dictionary<string, object>>(),
                    ct,
                    skipResponseChecker: false).ConfigureAwait(false);
            }

            var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
            var cleaned = textContent?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                request.Telemetry.MarkLatestModelResponseResult("FAILED", "Risposta vuota");
                return ModelWorkResult.Fail("Il testo ritornato è vuoto.");
            }

            var tags = StoryTaggingService.ParseAmbientMapping(cleaned);
            var tagCount = tags.Count;
            if (tagCount == 0)
            {
                request.Telemetry.MarkLatestModelResponseResult("FAILED", "Nessuna riga valida nel formato richiesto");
                return ModelWorkResult.Fail("Non ho trovato righe valide nel formato \"ID descrizione\".", cleaned);
            }

            var minTagsRequired = Math.Max(0, request.Tuning.MinAmbientTagsPerChunkRequirement);
            if (tagCount < minTagsRequired)
            {
                request.Telemetry.MarkLatestModelResponseResult(
                    "FAILED",
                    $"Hai inserito solo {tagCount} tag. Devi inserirne almeno {minTagsRequired}.");
                return ModelWorkResult.Fail(
                    $"Hai inserito solo {tagCount} tag [RUMORI]. Devi inserire ALMENO {minTagsRequired} tag di questo tipo per arricchire l'atmosfera della scena, non ripetere gli stessi tag.",
                    cleaned);
            }

            request.Telemetry.Append(
                request.RunId,
                $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Validated mapping: totalAmbient={tagCount}");
            request.Telemetry.MarkLatestModelResponseResult("SUCCESS", null);
            return ModelWorkResult.Ok(cleaned);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ModelWorkResult.Fail($"Errore durante l'elaborazione: {ex.Message}");
        }
    }

    private static ModelExecutionOptions BuildExecutionOptions(CommandTuningOptions.AmbientExpertTuning tuning)
    {
        return new ModelExecutionOptions
        {
            MaxAttemptsPerModel = Math.Max(1, tuning.MaxAttemptsPerChunk),
            RetryDelayBaseSeconds = Math.Max(0, tuning.RetryDelayBaseSeconds),
            EnableFallback = tuning.EnableFallback,
            EnableDiagnosis = tuning.DiagnoseOnFinalFailure,
            RequestTimeoutSeconds = Math.Max(0, tuning.RequestTimeoutSeconds)
        };
    }
}
