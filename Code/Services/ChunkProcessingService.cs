using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class ChunkProcessingService : IChunkProcessingService
{
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly IServiceScopeFactory? _scopeFactory;

    public ChunkProcessingService(ILangChainKernelFactory kernelFactory, IServiceScopeFactory? scopeFactory = null)
    {
        _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _scopeFactory = scopeFactory;
    }

    public async Task<ChunkProcessResult> ProcessAsync(ChunkProcessRequest request, CancellationToken ct)
    {
        var bridge = CreateBridge(request.CurrentModelName, request.Agent);

        try
        {
            var mappingText = await ProcessChunkWithRetriesAsync(request, bridge, ct).ConfigureAwait(false);
            return new ChunkProcessResult(mappingText, request.CurrentModelId, request.CurrentModelName, UsedFallback: false);
        }
        catch (Exception ex) when (_scopeFactory != null)
        {
            request.Telemetry.Append(
                request.RunId,
                $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Primary {request.RoleCode} model '{request.CurrentModelName}' failed: {ex.Message}. Attempting fallback models...",
                "warn");

            var fallback = await TryChunkWithFallbackAsync(request, ct).ConfigureAwait(false);
            if (fallback == null)
            {
                throw;
            }

            return fallback;
        }
    }

    private LangChainChatBridge CreateBridge(string modelName, Agent agent)
    {
        return _kernelFactory.CreateChatBridge(
            modelName,
            agent.Temperature,
            agent.TopP,
            agent.RepeatPenalty,
            agent.TopK,
            agent.RepeatLastN,
            agent.NumPredict);
    }

    private async Task<ChunkProcessResult?> TryChunkWithFallbackAsync(ChunkProcessRequest request, CancellationToken ct)
    {
        if (_scopeFactory == null)
        {
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var fallbackService = scope.ServiceProvider.GetService<ModelFallbackService>();
        if (fallbackService == null)
        {
            request.Telemetry.Append(request.RunId, "ModelFallbackService not available in DI scope; cannot fallback.", "warn");
            return null;
        }

        var (result, successfulModelRole) = await fallbackService.ExecuteWithFallbackAsync(
            request.RoleCode,
            request.CurrentModelId,
            async modelRole =>
            {
                var modelName = modelRole.Model?.Name;
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    throw new InvalidOperationException("Fallback ModelRole has no Model.Name");
                }

                var candidateBridge = CreateBridge(modelName, request.Agent);
                return await ProcessChunkWithRetriesAsync(request, candidateBridge, ct).ConfigureAwait(false);
            },
            validateResult: s => !string.IsNullOrWhiteSpace(s),
            shouldTryModelRole: mr =>
            {
                var name = mr.Model?.Name;
                return !string.IsNullOrWhiteSpace(name) && request.TriedModelNames.Add(name);
            }).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result) || successfulModelRole?.Model == null || string.IsNullOrWhiteSpace(successfulModelRole.Model.Name))
        {
            request.Telemetry.Append(request.RunId, $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Fallback models exhausted for role '{request.RoleCode}'.", "error");
            return null;
        }

        // TODO: expose fallback diagnostics/metrics through a dedicated telemetry sink.
        return new ChunkProcessResult(result, successfulModelRole.ModelId, successfulModelRole.Model.Name, UsedFallback: true);
    }

    private async Task<string> ProcessChunkWithRetriesAsync(ChunkProcessRequest request, LangChainChatBridge bridge, CancellationToken ct)
    {
        string? lastError = null;
        string? lastAssistantText = null;
        List<ConversationMessage>? lastRequestMessages = null;

        var maxAttempts = Math.Max(1, request.Tuning.MaxAttemptsPerChunk);
        var minTagsRequired = Math.Max(0, request.Tuning.MinAmbientTagsPerChunkRequirement);
        var retryDelayBaseSeconds = Math.Max(0, request.Tuning.RetryDelayBaseSeconds);
        var diagnoseOnFinalFailure = request.Tuning.DiagnoseOnFinalFailure;
        var requestTimeoutSeconds = Math.Max(0, request.Tuning.RequestTimeoutSeconds);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            request.Telemetry.Append(request.RunId, $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Ambient tagging attempt {attempt}/{maxAttempts}");
            request.Telemetry.ReportProgress(
                request.ChunkIndex,
                request.ChunkCount,
                $"Adding ambient tags chunk {request.ChunkIndex}/{request.ChunkCount} (attempt {attempt}/{maxAttempts})");

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (requestTimeoutSeconds > 0)
            {
                attemptCts.CancelAfter(TimeSpan.FromSeconds(requestTimeoutSeconds));
            }

            try
            {
                var messages = new List<ConversationMessage>();
                if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
                {
                    messages.Add(new ConversationMessage { Role = "system", Content = request.SystemPrompt });
                }
                messages.Add(new ConversationMessage { Role = "user", Content = request.ChunkText });

                lastRequestMessages = messages;

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
                        attemptCts.Token,
                        skipResponseChecker: false).ConfigureAwait(false);
                }

                var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
                var cleaned = textContent?.Trim() ?? string.Empty;
                lastAssistantText = cleaned;

                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    request.Telemetry.Append(request.RunId, $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Empty response on attempt {attempt}", "warn");
                    request.Telemetry.MarkLatestModelResponseResult("FAILED", "Risposta vuota");
                    lastError = "Il testo ritornato è vuoto.";
                    continue;
                }

                var tags = StoryTaggingService.ParseAmbientMapping(cleaned);
                var tagCount = tags.Count;
                if (tagCount == 0)
                {
                    request.Telemetry.MarkLatestModelResponseResult("FAILED", "Nessuna riga valida nel formato richiesto");
                    lastError = "Non ho trovato righe valide nel formato \"ID descrizione\".";
                    continue;
                }

                if (tagCount < minTagsRequired)
                {
                    request.Telemetry.Append(
                        request.RunId,
                        $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Not enough ambient tags: {tagCount} found, minimum {minTagsRequired} required",
                        "warn");
                    request.Telemetry.MarkLatestModelResponseResult("FAILED", $"Hai inserito solo {tagCount} tag. Devi inserirne almeno {minTagsRequired}.");
                    lastError = $"Hai inserito solo {tagCount} tag [RUMORI]. Devi inserire ALMENO {minTagsRequired} tag di questo tipo per arricchire l'atmosfera della scena, non ripetere gli stessi tag.";
                    continue;
                }

                request.Telemetry.Append(request.RunId, $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Validated mapping: totalAmbient={tagCount}");
                request.Telemetry.MarkLatestModelResponseResult("SUCCESS", null);
                return cleaned;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && requestTimeoutSeconds > 0 && attemptCts.IsCancellationRequested)
            {
                lastError = $"Timeout richiesta modello dopo {requestTimeoutSeconds}s";
                request.Telemetry.Append(
                    request.RunId,
                    $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Request timeout on attempt {attempt}/{maxAttempts} after {requestTimeoutSeconds}s",
                    "warn");
                request.Telemetry.MarkLatestModelResponseResult("FAILED", lastError);
                continue;
            }
            catch (Exception ex)
            {
                request.Telemetry.Append(request.RunId, $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Error on attempt {attempt}: {ex.Message}", "error");
                lastError = $"Errore durante l'elaborazione: {ex.Message}";

                if (attempt == maxAttempts)
                {
                    throw;
                }

                await Task.Delay(TimeSpan.FromSeconds(retryDelayBaseSeconds * attempt), ct).ConfigureAwait(false);
            }
        }

        if (diagnoseOnFinalFailure)
        {
            try
            {
                var diagnosis = await DiagnoseFailureAsync(
                    bridge,
                    request.SystemPrompt,
                    lastRequestMessages,
                    request.ChunkText,
                    lastAssistantText,
                    lastError,
                    request.ChunkIndex,
                    request.ChunkCount,
                    ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(diagnosis))
                {
                    request.Telemetry.Append(request.RunId, $"[chunk {request.ChunkIndex}/{request.ChunkCount}] ambient_expert self-diagnosis: {diagnosis}", "warn");
                }
            }
            catch (Exception ex)
            {
                request.Telemetry.Append(request.RunId, $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Failed to collect ambient_expert self-diagnosis: {ex.Message}", "warn");
            }
        }

        throw new InvalidOperationException($"Failed to process chunk {request.ChunkIndex}/{request.ChunkCount} after {maxAttempts} attempts. Last error: {lastError}");
    }

    private static async Task<string?> DiagnoseFailureAsync(
        LangChainChatBridge bridge,
        string? originalSystemPrompt,
        List<ConversationMessage>? lastRequestMessages,
        string chunkText,
        string? lastAssistantText,
        string? lastFailureReason,
        int chunkIndex,
        int chunkCount,
        CancellationToken ct)
    {
        var auditSystem =
            "Sei un auditor tecnico per l'agente ambient_expert. " +
            "Devi spiegare in modo conciso perché l'output non ha superato la validazione o perché è fallito. " +
            "Non inventare contenuti; basati sui dati forniti.";

        var sb = new StringBuilder();
        sb.AppendLine($"DIAGNOSI ambient_expert - chunk {chunkIndex}/{chunkCount}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(lastFailureReason))
        {
            sb.AppendLine("=== MOTIVO FALLIMENTO (validazione/errore) ===");
            sb.AppendLine(ClipForPrompt(lastFailureReason, 2000));
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(lastAssistantText))
        {
            sb.AppendLine("=== ULTIMO OUTPUT MODELLO (estratto) ===");
            sb.AppendLine(ClipForPrompt(lastAssistantText, 2000));
            sb.AppendLine();
        }

        sb.AppendLine("=== INPUT (chunk) ===");
        sb.AppendLine(ClipForPrompt(chunkText, 2500));
        sb.AppendLine();

        if (lastRequestMessages != null && lastRequestMessages.Count > 0)
        {
            sb.AppendLine("=== ULTIMA CONVERSAZIONE (ruoli) ===");
            foreach (var m in lastRequestMessages)
            {
                sb.AppendLine($"- {m.Role}: {ClipForPrompt(m.Content, 250)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Output richiesto: 5-10 righe, punti elenco se utile, senza proporre un 'nuovo tentativo'.");

        var diagMessages = new List<ConversationMessage>();
        if (!string.IsNullOrWhiteSpace(originalSystemPrompt))
        {
            diagMessages.Add(new ConversationMessage { Role = "system", Content = originalSystemPrompt });
            diagMessages.Add(new ConversationMessage
            {
                Role = "user",
                Content = "ISTRUZIONI DIAGNOSTICHE:\n" + auditSystem + "\n\n" + sb
            });
        }
        else
        {
            diagMessages.Add(new ConversationMessage { Role = "system", Content = auditSystem });
            diagMessages.Add(new ConversationMessage { Role = "user", Content = sb.ToString() });
        }

        var responseJson = await bridge.CallModelWithToolsAsync(diagMessages, new List<Dictionary<string, object>>(), ct, skipResponseChecker: true).ConfigureAwait(false);
        var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
        return string.IsNullOrWhiteSpace(textContent) ? null : textContent.Trim();
    }

    private static string ClipForPrompt(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var t = text.Trim();
        if (t.Length <= maxChars)
        {
            return t;
        }

        return t.Substring(0, maxChars) + "...";
    }
}

