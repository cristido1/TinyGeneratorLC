
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;
using TinyGenerator.Services;
using TinyGenerator.Services.Text;

namespace TinyGenerator.Services.Commands;

public sealed class GenerateNewSerieCommand
{
    private readonly string _prompt;
    private readonly DatabaseService _database;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly ICustomLogger? _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ICommandDispatcher? _dispatcher;
    private readonly CommandModelExecutionService? _modelExecution;
    private readonly SeriesGenerationOptions _options;

    public GenerateNewSerieCommand(
        string prompt,
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        IOptionsMonitor<SeriesGenerationOptions>? optionsMonitor = null,
        ICustomLogger? logger = null,
        IServiceScopeFactory? scopeFactory = null,
        ICommandDispatcher? dispatcher = null,
        CommandModelExecutionService? modelExecution = null)
    {
        _prompt = prompt ?? string.Empty;
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _logger = logger;
        _scopeFactory = scopeFactory;
        _dispatcher = dispatcher;
        _modelExecution = modelExecution;
        _options = optionsMonitor?.CurrentValue ?? new SeriesGenerationOptions();
    }

    public async Task<CommandResult> ExecuteAsync(string? runId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_prompt))
        {
            return new CommandResult(false, "Prompt vuoto");
        }

        var effectiveRunId = string.IsNullOrWhiteSpace(runId)
            ? $"generate_new_serie_{DateTime.UtcNow:yyyyMMddHHmmssfff}"
            : runId.Trim();
        _logger?.Start(effectiveRunId);

        try
        {
            var bibleAgent = GetActiveAgent("serie_bible_agent");
            var characterAgent = GetActiveAgent("serie_character_agent");
            var seasonAgent = GetActiveAgent("serie_season_agent");
            var episodeAgent = GetActiveAgent("serie_episode_agent");
            var validatorAgent = GetActiveAgent("serie_validator_agent");

            if (bibleAgent == null) return new CommandResult(false, "Nessun agente attivo con ruolo serie_bible_agent");
            if (characterAgent == null) return new CommandResult(false, "Nessun agente attivo con ruolo serie_character_agent");
            if (seasonAgent == null) return new CommandResult(false, "Nessun agente attivo con ruolo serie_season_agent");
            if (episodeAgent == null) return new CommandResult(false, "Nessun agente attivo con ruolo serie_episode_agent");
            if (validatorAgent == null) return new CommandResult(false, "Nessun agente attivo con ruolo serie_validator_agent");

            var biblePrompt = BuildBiblePrompt(_prompt);
            ReportStep(effectiveRunId, 1, 5, "Serie Bible", bibleAgent);
            var bibleResult = await CallRoleWithRetriesAsync(
                bibleAgent,
                "serie_bible_agent",
                biblePrompt,
                requiredTags: RequiredBibleTags,
                _options.Bible,
                effectiveRunId,
                ct,
                validateText: ValidateBibleOutput);
            if (!bibleResult.Success)
            {
                return new CommandResult(false, bibleResult.Error ?? "Serie bible fallita");
            }
            var bibleTags = bibleResult.Text!;

            var charactersPrompt = BuildCharactersPrompt(bibleTags);
            ReportStep(effectiveRunId, 2, 5, "Serie Characters", characterAgent);
            var charactersResult = await CallRoleWithRetriesAsync(
                characterAgent,
                "serie_character_agent",
                charactersPrompt,
                requiredTags: RequiredCharacterTags,
                _options.Characters,
                effectiveRunId,
                ct,
                validateText: ValidateCharactersOutput);
            if (!charactersResult.Success)
            {
                return new CommandResult(false, charactersResult.Error ?? "Serie characters fallita");
            }
            var characterTags = charactersResult.Text!;

            var seasonPrompt = BuildSeasonPrompt(bibleTags, characterTags);
            ReportStep(effectiveRunId, 3, 5, "Serie Season", seasonAgent);
            var seasonResult = await CallRoleWithRetriesAsync(
                seasonAgent,
                "serie_season_agent",
                seasonPrompt,
                requiredTags: RequiredSeasonTags,
                _options.Episodes,
                effectiveRunId,
                ct,
                validateText: ValidateSeasonOutput);
            if (!seasonResult.Success)
            {
                return new CommandResult(false, seasonResult.Error ?? "Serie season fallita");
            }
            var seasonTags = seasonResult.Text!;

            var baseEpisodes = ParseEpisodeBlocks(seasonTags);
            if (baseEpisodes.Count == 0)
            {
                return new CommandResult(false, "Nessun episodio base generato dal serie_season_agent");
            }

            var episodeStructures = new List<string>();
            var totalSteps = 4 + baseEpisodes.Count;
            var episodeStep = 4;
            foreach (var episode in baseEpisodes.OrderBy(e => e.Number))
            {
                ct.ThrowIfCancellationRequested();
                ReportStep(effectiveRunId, episodeStep, totalSteps, $"Serie Episode {episode.Number:00}", episodeAgent);
                var episodePrompt = BuildEpisodePrompt(episode, bibleTags, characterTags);
                var epResult = await CallRoleWithRetriesAsync(
                    episodeAgent,
                    "serie_episode_agent",
                    episodePrompt,
                    requiredTags: RequiredEpisodeStructureTags,
                    _options.Episodes,
                    effectiveRunId,
                    ct,
                    validateText: t => ValidateEpisodeStructureOutput(t, episode.Number));
                if (!epResult.Success)
                {
                    return new CommandResult(false, epResult.Error ?? $"Serie episode fallita (episodio {episode.Number})");
                }
                episodeStructures.Add(epResult.Text!);
                episodeStep++;
            }

            var validationPrompt = BuildValidatorPrompt(bibleTags, characterTags, seasonTags, episodeStructures);
            ReportStep(effectiveRunId, totalSteps, totalSteps, "Serie Validator", validatorAgent);
            var validatorResult = await CallRoleWithRetriesAsync(
                validatorAgent,
                "serie_validator_agent",
                validationPrompt,
                requiredTags: RequiredValidatorTags,
                _options.Validator,
                effectiveRunId,
                ct);
            if (!validatorResult.Success)
            {
                return new CommandResult(false, validatorResult.Error ?? "Validazione serie fallita");
            }

            var validationText = validatorResult.Text ?? string.Empty;
            if (!validationText.Contains("[VALIDATION_OK]", StringComparison.OrdinalIgnoreCase))
            {
                return new CommandResult(false, $"Validazione serie non OK: {validationText}");
            }

            var allEpisodeStructures = string.Join("\n\n", episodeStructures);
            var result = BuildSeriesData(bibleTags, characterTags, seasonTags, allEpisodeStructures);
            if (result.Series == null)
            {
                return new CommandResult(false, "Impossibile costruire dati serie");
            }

            var serieId = _database.InsertSeries(result.Series);
            _database.InsertSeriesCharacters(serieId, result.Characters);
            _database.InsertSeriesEpisodes(serieId, result.Episodes);

            _logger?.Append(effectiveRunId, $"Serie creata: id={serieId}, characters={result.Characters.Count}, episodes={result.Episodes.Count}");
            return new CommandResult(true, $"Serie creata (id {serieId})");
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(false, "Operazione annullata");
        }
        catch (Exception ex)
        {
            _logger?.Append(effectiveRunId, $"Errore GenerateNewSerie: {ex.Message}");
            return new CommandResult(false, $"Errore GenerateNewSerie: {ex.Message}");
        }
        finally
        {
            _logger?.MarkCompleted(effectiveRunId);
        }
    }

    public Task<CommandResult> ExecuteAsync(CancellationToken ct)
        => ExecuteAsync(runId: null, ct: ct);

    private Agent? GetActiveAgent(string role)
        => _database.ListAgents()
            .FirstOrDefault(a => a.IsActive && a.Role != null && a.Role.Equals(role, StringComparison.OrdinalIgnoreCase));

    private async Task<AgentResponse> CallRoleWithRetriesAsync(
        Agent agent,
        string roleCode,
        string prompt,
        IReadOnlyCollection<string> requiredTags,
        SeriesGenerationOptions.SeriesRoleOptions options,
        string runId,
        CancellationToken ct,
        Func<string, string?>? validateText = null)
    {
        if (_modelExecution == null)
        {
            return AgentResponse.Fail("CommandModelExecutionService non disponibile");
        }

        var hasDeterministicChecks = validateText != null || (requiredTags != null && requiredTags.Count > 0);
        var effectiveUseResponseChecker = options.UseResponseChecker && !hasDeterministicChecks;

        var execution = await _modelExecution.ExecuteAsync(
            new CommandModelExecutionService.Request
            {
                CommandKey = "generate_new_serie",
                Agent = agent,
                RoleCode = roleCode,
                Prompt = prompt,
                SystemPrompt = BuildSystemPrompt(agent),
                MaxAttempts = Math.Max(1, options.MaxAttempts),
                RetryDelaySeconds = Math.Max(0, options.RetryDelaySeconds),
                StepTimeoutSec = Math.Max(1, options.TimeoutSec),
                UseResponseChecker = effectiveUseResponseChecker,
                EnableFallback = true,
                DiagnoseOnFinalFailure = options.DiagnoseOnFinalFailure,
                ExplainAfterAttempt = Math.Max(0, options.ExplainAfterAttempt),
                RunId = runId,
                DeterministicValidator = output =>
                {
                    if (!HasRequiredTags(output ?? string.Empty, requiredTags, out var missingTags))
                    {
                        var missingText = missingTags.Count > 0 ? string.Join(", ", missingTags) : "tag richiesti";
                        return new CommandModelExecutionService.DeterministicValidationResult(
                            false,
                            $"Output privo di tag richiesti per {roleCode}: {missingText}");
                    }

                    if (validateText != null)
                    {
                        var extra = validateText(output ?? string.Empty);
                        if (!string.IsNullOrWhiteSpace(extra))
                        {
                            return new CommandModelExecutionService.DeterministicValidationResult(false, extra);
                        }
                    }

                    return new CommandModelExecutionService.DeterministicValidationResult(true, null);
                },
                RetryPromptFactory = (originalPrompt, reason) =>
                    BuildRetryPrompt(originalPrompt, roleCode, reason, new List<string>())
            },
            ct).ConfigureAwait(false);

        if (execution.Success && !string.IsNullOrWhiteSpace(execution.Text))
        {
            return AgentResponse.Ok(execution.Text);
        }

        var error = execution.Error ?? $"Operazione {roleCode} fallita";
        _logger?.Append(runId, $"[{roleCode}] Errore: {error}");
        return AgentResponse.Fail(error);
    }

    private async Task<AgentResponse> CallAgentAsync(Agent agent, string roleCode, string prompt, CancellationToken ct, bool useResponseChecker)
    {
        var modelName = ResolveModelName(agent);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return AgentResponse.Fail($"Agente {agent.Name} senza modello configurato");
        }

        var systemPrompt = BuildSystemPrompt(agent);
        AgentResponse response;
        using (LogScope.Push(
                   LogScope.Current ?? $"series/{roleCode}",
                   operationId: null,
                   stepNumber: null,
                   maxStep: null,
                   agentName: agent.Name ?? roleCode,
                   agentRole: roleCode))
        {
            response = await StateDrivenPipelineHelpers.CallModelAsync(
                _kernelFactory,
                _scopeFactory,
                modelName,
                agent,
                roleCode,
                systemPrompt,
                prompt,
                ct,
                allowInternalFallback: false,
                skipResponseChecker: !useResponseChecker);
        }

        return response;
    }

    private async Task DiagnoseFailureAsync(Agent agent, string roleCode, string prompt, string? lastText, string runId, CancellationToken ct)
    {
        try
        {
            var modelName = ResolveModelName(agent);
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return;
            }

            var systemPrompt = "Sei in modalita diagnostica. Rispondi SOLO in testo libero.";
            var sb = new StringBuilder();
            sb.AppendLine("Hai fallito nel produrre i tag richiesti.");
            sb.AppendLine("Spiega perche hai fallito e cosa non era chiaro nelle istruzioni.");
            sb.AppendLine("Indica cosa cambieresti per rispettare le regole.");
            sb.AppendLine();
            sb.AppendLine("PROMPT ORIGINALE:");
            sb.AppendLine(prompt);
            if (!string.IsNullOrWhiteSpace(lastText))
            {
                sb.AppendLine();
                sb.AppendLine("ULTIMO OUTPUT:");
                sb.AppendLine(lastText);
            }

            var bridge = _kernelFactory.CreateChatBridge(
                modelName,
                agent.Temperature,
                agent.TopP,
                agent.RepeatPenalty,
                agent.TopK,
                agent.RepeatLastN,
                agent.NumPredict);

            var messages = new List<ConversationMessage>
            {
                new ConversationMessage { Role = "system", Content = systemPrompt },
                new ConversationMessage { Role = "user", Content = sb.ToString() }
            };

            var response = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ct, skipResponseChecker: true);
            var text = response ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(text))
            {
                _logger?.Append(runId, $"[{roleCode}] Diagnosi: {text}");
            }
        }
        catch
        {
            // best-effort diagnostics
        }
    }

    private async Task<AgentResponse> TryFallbackAsync(
        Agent agent,
        string roleCode,
        string prompt,
        IReadOnlyCollection<string> requiredTags,
        string runId,
        CancellationToken ct,
        bool useResponseChecker)
    {
        if (_scopeFactory == null)
        {
            return AgentResponse.Fail("ModelFallbackService non disponibile");
        }

        using var scope = _scopeFactory.CreateScope();
        var fallbackService = scope.ServiceProvider.GetService<ModelFallbackService>();
        if (fallbackService == null)
        {
            return AgentResponse.Fail("ModelFallbackService non disponibile");
        }

        foreach (var fallbackRoleCode in BuildFallbackRoleCandidates(roleCode, agent.Role))
        {
            _logger?.Append(runId, $"[{roleCode}] Tentativo fallback sul ruolo '{fallbackRoleCode}'");
            (string? fallbackResult, ModelRole? successfulModelRole) = await fallbackService.ExecuteWithFallbackAsync<string>(
                fallbackRoleCode,
                agent.ModelId,
                async modelRole =>
                {
                    var fallbackName = modelRole.Model?.Name;
                    if (string.IsNullOrWhiteSpace(fallbackName))
                    {
                        throw new InvalidOperationException("Fallback ModelRole has no Model.Name");
                    }

                    var systemPrompt = !string.IsNullOrWhiteSpace(modelRole.Instructions)
                        ? modelRole.Instructions
                        : BuildSystemPrompt(agent);

                    var bridge = _kernelFactory.CreateChatBridge(
                        fallbackName,
                        agent.Temperature,
                        modelRole.TopP ?? agent.TopP,
                        agent.RepeatPenalty,
                        modelRole.TopK ?? agent.TopK,
                        agent.RepeatLastN,
                        agent.NumPredict);

                    var messages = new List<ConversationMessage>
                    {
                        new ConversationMessage { Role = "system", Content = systemPrompt },
                        new ConversationMessage { Role = "user", Content = prompt }
                    };

                    using var fallbackScopeHandle = LogScope.Push(
                        LogScope.Current ?? $"series/{roleCode}/fallback",
                        operationId: null,
                        stepNumber: null,
                        maxStep: null,
                        agentName: agent.Name ?? roleCode,
                        agentRole: fallbackRoleCode);
                    var response = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ct, skipResponseChecker: !useResponseChecker);
                    return response ?? string.Empty;
                },
                validateResult: s => !string.IsNullOrWhiteSpace(s) && HasRequiredTags(s, requiredTags, out _));

            if (!string.IsNullOrWhiteSpace(fallbackResult) && successfulModelRole?.Model != null)
            {
                _logger?.Append(runId, $"[{roleCode}] Fallback model succeeded: {successfulModelRole.Model.Name}");
                return AgentResponse.Ok(fallbackResult!);
            }
        }

        return AgentResponse.Fail("Fallback fallito");
    }

    private static string BuildSystemPrompt(Agent agent)
        => !string.IsNullOrWhiteSpace(agent.Instructions) ? agent.Instructions! :
           !string.IsNullOrWhiteSpace(agent.Prompt) ? agent.Prompt! : "Sei un assistente esperto.";

    private string? ResolveModelName(Agent agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.ModelName)) return agent.ModelName;
        if (!agent.ModelId.HasValue) return null;
        return _database.GetModelInfoById(agent.ModelId.Value)?.Name;
    }

    private static bool HasRequiredTags(string text, IReadOnlyCollection<string> requiredTags, out List<string> missingTags)
    {
        missingTags = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (requiredTags == null || requiredTags.Count == 0) return true;
        if (requiredTags.Contains("VALIDATION_OK") && requiredTags.Contains("VALIDATION_ERROR"))
        {
            return text.Contains("[VALIDATION_OK]", StringComparison.OrdinalIgnoreCase)
                || text.Contains("[VALIDATION_ERROR]", StringComparison.OrdinalIgnoreCase);
        }
        var tags = CollectTags(text);
        foreach (var tag in requiredTags)
        {
            if (!tags.Contains(tag))
            {
                missingTags.Add(tag);
            }
        }
        return missingTags.Count == 0;
    }

    private void ReportStep(string runId, int current, int max, string phase, Agent agent)
    {
        var modelName = ResolveModelName(agent) ?? "n/a";
        _dispatcher?.UpdateStep(runId, current, max, $"{phase} | {agent.Name ?? agent.Role ?? "agent"} | {modelName}");
    }

    private static List<string> BuildFallbackRoleCandidates(string roleCode, string? agentRole)
    {
        var candidates = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            if (!candidates.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(trimmed);
            }
        }

        static string? TrimAgentSuffix(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (trimmed.EndsWith("_agent", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^6];
            }

            return null;
        }

        Add(roleCode);
        Add(agentRole);
        Add(TrimAgentSuffix(roleCode));
        Add(TrimAgentSuffix(agentRole));
        return candidates;
    }

    private static string BuildRetryPrompt(string originalPrompt, string roleCode, string reason, List<string> missingTags)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ATTENZIONE: il tuo output precedente NON era valido.");
        sb.AppendLine("Motivo: " + reason);
        if (missingTags.Count > 0)
        {
            sb.AppendLine("TAG MANCANTI (obbligatori): " + string.Join(", ", missingTags));
        }
        sb.AppendLine("Rigenera la risposta COMPLETA rispettando tutti i tag richiesti.");
        sb.AppendLine();
        sb.AppendLine("PROMPT ORIGINALE:");
        sb.AppendLine(originalPrompt.Trim());
        return sb.ToString();
    }

    private static HashSet<string> CollectTags(string text)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in ParseTagBlocks(text))
        {
            if (!string.IsNullOrWhiteSpace(block.Tag))
            {
                tags.Add(block.Tag.Trim());
            }
        }
        return tags;
    }

    private static string BuildBiblePrompt(string prompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Genera la bibbia della serie. Usa SOLO TAG.");
        sb.AppendLine();
        sb.AppendLine("PROMPT:");
        sb.AppendLine(prompt.Trim());
        return sb.ToString();
    }

    private static string BuildCharactersPrompt(string bibleTags)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Genera personaggi e relazioni. Usa SOLO TAG.");
        sb.AppendLine();
        sb.AppendLine("SERIE_BIBLE:");
        sb.AppendLine(bibleTags.Trim());
        return sb.ToString();
    }

    private static string BuildSeasonPrompt(string bibleTags, string characterTags)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Genera la struttura base degli episodi. Usa SOLO TAG.");
        sb.AppendLine();
        sb.AppendLine("SERIE_BIBLE:");
        sb.AppendLine(bibleTags.Trim());
        sb.AppendLine();
        sb.AppendLine("CHARACTERS:");
        sb.AppendLine(characterTags.Trim());
        return sb.ToString();
    }

    private static string BuildEpisodePrompt(EpisodeBase episode, string bibleTags, string characterTags)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Genera la struttura dettagliata dell'episodio. Usa SOLO TAG.");
        sb.AppendLine();
        sb.AppendLine("EPISODE_BASE:");
        sb.AppendLine(episode.RawBlock.Trim());
        sb.AppendLine();
        sb.AppendLine("SERIE_BIBLE:");
        sb.AppendLine(bibleTags.Trim());
        sb.AppendLine();
        sb.AppendLine("CHARACTERS:");
        sb.AppendLine(characterTags.Trim());
        return sb.ToString();
    }

    private static string BuildValidatorPrompt(string bibleTags, string characterTags, string seasonTags, IEnumerable<string> episodeStructures)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Valida i tag della serie. Usa SOLO TAG. Output [VALIDATION_OK] o [VALIDATION_ERROR].");
        sb.AppendLine();
        sb.AppendLine("SERIE_BIBLE:");
        sb.AppendLine(bibleTags.Trim());
        sb.AppendLine();
        sb.AppendLine("CHARACTERS:");
        sb.AppendLine(characterTags.Trim());
        sb.AppendLine();
        sb.AppendLine("EPISODES_BASE:");
        sb.AppendLine(seasonTags.Trim());
        sb.AppendLine();
        sb.AppendLine("EPISODES_STRUCT:");
        foreach (var ep in episodeStructures)
        {
            sb.AppendLine(ep.Trim());
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static SeriesBuildResult BuildSeriesData(string bibleTags, string characterTags, string seasonTags, string episodeStructures)
    {
        var bibleBlocks = ParseTagBlocks(bibleTags);
        var characterBlocks = ParseTagBlocks(characterTags);
        var seasonBlocks = ParseTagBlocks(seasonTags);
        var episodeBlocks = ParseTagBlocks(episodeStructures);

        var series = new Series
        {
            Titolo = GetSingleTag(bibleBlocks, "SERIES_TITLE") ?? "Untitled Series",
            Genere = JoinList(GetListTag(bibleBlocks, "SERIES_GENRE")),
            TonoBase = GetSingleTag(bibleBlocks, "SERIES_TONE"),
            Target = GetSingleTag(bibleBlocks, "SERIES_AUDIENCE"),
            PremessaSerie = GetSingleTag(bibleBlocks, "SERIES_PREMISE"),
            AmbientazioneBase = BuildAmbientazione(GetSingleTag(bibleBlocks, "SETTING_PLACE"), GetSingleTag(bibleBlocks, "SETTING_TIME")),
            PeriodoNarrativo = GetSingleTag(bibleBlocks, "SETTING_TIME"),
            LivelloTecnologicoMedio = GetSingleTag(bibleBlocks, "SETTING_TECH_LEVEL"),
            RegoleNarrative = JoinList(GetListTag(bibleBlocks, "SETTING_WORLD_RULES")) ?? GetSingleTag(bibleBlocks, "SETTING_WORLD_RULES"),
            TemiObbligatori = JoinList(GetListTag(bibleBlocks, "SERIES_THEMES")),
            CosaNonDeveMaiSuccedere = JoinList(GetListTag(bibleBlocks, "SERIES_FORBIDDEN_TOPICS")),
            ArcoNarrativoSerie = BuildSeasonArc(bibleBlocks),
            SerieFinalGoal = GetSingleTag(bibleBlocks, "SEASON_FINALE_PAYOFF"),
            NoteAI = BuildNoteAi(bibleTags, seasonTags)
        };

        var characters = ParseCharacters(characterBlocks);
        ApplyCharacterArcs(characters, characterBlocks);
        ApplyRelationships(characters, characterBlocks);

        var baseEpisodes = ParseEpisodeBlocksFromBlocks(seasonBlocks);
        var structureMap = ParseEpisodeStructures(episodeBlocks);

        var episodes = new List<SeriesEpisode>();
        foreach (var baseEp in baseEpisodes)
        {
            var structure = structureMap.TryGetValue(baseEp.Number, out var s) ? s : null;
            var payload = new
            {
                base_episode = baseEp,
                structure
            };
            var tramaJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            episodes.Add(new SeriesEpisode
            {
                Number = baseEp.Number,
                Title = baseEp.Title,
                EpisodeGoal = baseEp.APlot,
                StartSituation = baseEp.BPlot,
                Trama = tramaJson
            });
        }

        return new SeriesBuildResult(series, characters, episodes);
    }

    private static string? BuildAmbientazione(string? place, string? time)
    {
        if (string.IsNullOrWhiteSpace(place) && string.IsNullOrWhiteSpace(time)) return null;
        if (string.IsNullOrWhiteSpace(time)) return place;
        if (string.IsNullOrWhiteSpace(place)) return time;
        return $"{place}. {time}";
    }

    private static string? BuildSeasonArc(List<TagBlock> bibleBlocks)
    {
        var logline = GetSingleTag(bibleBlocks, "SEASON_LOGLINE");
        var antagonist = GetSingleTag(bibleBlocks, "SEASON_MAIN_ANTAGONISM");
        var twist = GetSingleTag(bibleBlocks, "SEASON_MID_TWIST");
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(logline)) sb.AppendLine($"Logline: {logline}");
        if (!string.IsNullOrWhiteSpace(antagonist)) sb.AppendLine($"Antagonism: {antagonist}");
        if (!string.IsNullOrWhiteSpace(twist)) sb.AppendLine($"Mid twist: {twist}");
        var result = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string? BuildNoteAi(string bibleTags, string seasonTags)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BIBLE_TAGS:");
        sb.AppendLine(bibleTags.Trim());
        sb.AppendLine();
        sb.AppendLine("SEASON_TAGS:");
        sb.AppendLine(seasonTags.Trim());
        return sb.ToString();
    }

    private static List<SeriesCharacter> ParseCharacters(List<TagBlock> blocks)
    {
        var result = new List<SeriesCharacter>();
        foreach (var block in blocks.Where(b => b.Tag.Equals("CHARACTER", StringComparison.OrdinalIgnoreCase)))
        {
            var kv = ParseKeyValues(block.Lines);
            var name = kv.TryGetValue("NAME", out var n) ? n : "Unnamed";
            var gender = kv.TryGetValue("GENDER", out var g) ? g : "other";
            var bio = kv.TryGetValue("BIO_SHORT", out var b) ? b : null;
            var role = kv.TryGetValue("ROLE", out var r) ? r : null;
            var internalNeed = kv.TryGetValue("INTERNAL_NEED", out var i) ? i : null;
            var externalGoal = kv.TryGetValue("EXTERNAL_GOAL", out var e) ? e : null;
            var flaws = kv.TryGetValue("FLAWS", out var f) ? f : null;
            var skills = kv.TryGetValue("SKILLS", out var s) ? s : null;
            var limits = kv.TryGetValue("LIMITS", out var l) ? l : null;
            var voiceStyle = kv.TryGetValue("VOICE_STYLE", out var v) ? v : null;

            var profile = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(externalGoal)) profile.AppendLine($"ExternalGoal: {externalGoal}");
            if (!string.IsNullOrWhiteSpace(internalNeed)) profile.AppendLine($"InternalNeed: {internalNeed}");
            if (!string.IsNullOrWhiteSpace(flaws)) profile.AppendLine($"Flaws: {flaws}");
            if (!string.IsNullOrWhiteSpace(skills)) profile.AppendLine($"Skills: {skills}");
            if (!string.IsNullOrWhiteSpace(limits)) profile.AppendLine($"Limits: {limits}");
            if (!string.IsNullOrWhiteSpace(voiceStyle)) profile.AppendLine($"VoiceStyle: {voiceStyle}");

            result.Add(new SeriesCharacter
            {
                Name = name,
                Gender = string.IsNullOrWhiteSpace(gender) ? "other" : gender,
                Description = bio,
                RuoloNarrativo = role,
                ConflittoInterno = internalNeed,
                Profilo = profile.ToString().Trim()
            });
        }
        return result;
    }

    private static void ApplyCharacterArcs(List<SeriesCharacter> characters, List<TagBlock> blocks)
    {
        var byId = BuildCharacterIdMap(blocks, characters);
        foreach (var block in blocks.Where(b => b.Tag.Equals("CHARACTER_SEASON_ARC", StringComparison.OrdinalIgnoreCase)))
        {
            var kv = ParseKeyValues(block.Lines);
            if (!kv.TryGetValue("ID", out var id)) continue;
            if (!byId.TryGetValue(id, out var character)) continue;

            var from = kv.TryGetValue("FROM", out var f) ? f : null;
            var to = kv.TryGetValue("TO", out var t) ? t : null;
            var turns = kv.TryGetValue("KEY_TURNS", out var k) ? k : null;
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(from)) sb.AppendLine($"From: {from}");
            if (!string.IsNullOrWhiteSpace(to)) sb.AppendLine($"To: {to}");
            if (!string.IsNullOrWhiteSpace(turns)) sb.AppendLine($"KeyTurns: {turns}");
            character.ArcoPersonale = sb.ToString().Trim();
        }
    }

    private static void ApplyRelationships(List<SeriesCharacter> characters, List<TagBlock> blocks)
    {
        var byId = BuildCharacterIdMap(blocks, characters);
        var relationsByChar = new Dictionary<SeriesCharacter, List<string>>();
        foreach (var block in blocks.Where(b => b.Tag.Equals("RELATIONSHIP", StringComparison.OrdinalIgnoreCase)))
        {
            var kv = ParseKeyValues(block.Lines);
            if (!kv.TryGetValue("FROM", out var fromId)) continue;
            if (!kv.TryGetValue("TO", out var toId)) continue;
            var type = kv.TryGetValue("TYPE", out var t) ? t : "relation";
            var notes = kv.TryGetValue("NOTES", out var n) ? n : null;

            if (byId.TryGetValue(fromId, out var fromChar))
            {
                if (!relationsByChar.TryGetValue(fromChar, out var list))
                {
                    list = new List<string>();
                    relationsByChar[fromChar] = list;
                }
                list.Add($"{fromId} -> {toId} ({type}){(string.IsNullOrWhiteSpace(notes) ? "" : $": {notes}")}");
            }
        }

        foreach (var kvp in relationsByChar)
        {
            kvp.Key.AlleanzaRelazione = string.Join("\n", kvp.Value);
        }
    }

    private static Dictionary<string, SeriesCharacter> BuildCharacterIdMap(List<TagBlock> blocks, List<SeriesCharacter> characters)
    {
        var ids = blocks.Where(b => b.Tag.Equals("CHARACTER", StringComparison.OrdinalIgnoreCase))
            .Select(b => ParseKeyValues(b.Lines))
            .Select(kv => new
            {
                Id = kv.TryGetValue("ID", out var id) ? id : null,
                Name = kv.TryGetValue("NAME", out var name) ? name : null
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Name))
            .ToList();

        var map = new Dictionary<string, SeriesCharacter>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ids)
        {
            var match = characters.FirstOrDefault(c => c.Name.Equals(item.Name!, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                map[item.Id!] = match;
            }
        }

        return map;
    }

    private static List<EpisodeBase> ParseEpisodeBlocks(string seasonTags)
        => ParseEpisodeBlocksFromBlocks(ParseTagBlocks(seasonTags));

    private static List<EpisodeBase> ParseEpisodeBlocksFromBlocks(List<TagBlock> blocks)
    {
        var list = new List<EpisodeBase>();
        foreach (var block in blocks.Where(b => b.Tag.Equals("EPISODE", StringComparison.OrdinalIgnoreCase)))
        {
            var kv = ParseKeyValues(block.Lines);
            var number = TryParseInt(kv.TryGetValue("NUMBER", out var n) ? n : null);
            if (number <= 0) continue;
            list.Add(new EpisodeBase
            {
                Number = number,
                Title = kv.TryGetValue("TITLE", out var t) ? t : null,
                Logline = kv.TryGetValue("LOGLINE", out var l) ? l : null,
                APlot = kv.TryGetValue("A_PLOT", out var a) ? a : null,
                BPlot = kv.TryGetValue("B_PLOT", out var b) ? b : null,
                Theme = kv.TryGetValue("THEME", out var th) ? th : null,
                RawBlock = block.Raw
            });
        }
        return list;
    }

    private static Dictionary<int, EpisodeStructure> ParseEpisodeStructures(List<TagBlock> blocks)
    {
        var map = new Dictionary<int, EpisodeStructure>();
        EpisodeStructure? current = null;
        foreach (var block in blocks)
        {
            if (block.Tag.Equals("EPISODE_STRUCTURE", StringComparison.OrdinalIgnoreCase))
            {
                var kv = ParseKeyValues(block.Lines);
                var number = TryParseInt(kv.TryGetValue("NUMBER", out var n) ? n : null);
                if (number <= 0) continue;
                current = new EpisodeStructure { Number = number };
                map[number] = current;
                continue;
            }

            if (current == null) continue;

            if (block.Tag.Equals("BEAT", StringComparison.OrdinalIgnoreCase))
            {
                var kv = ParseKeyValues(block.Lines);
                current.Beats.Add(new Beat
                {
                    Type = kv.TryGetValue("TYPE", out var t) ? t : null,
                    Summary = kv.TryGetValue("SUMMARY", out var s) ? s : null
                });
                continue;
            }

            if (block.Tag.Equals("CAST", StringComparison.OrdinalIgnoreCase))
            {
                current.Cast.AddRange(ParseList(block.Lines));
                continue;
            }

            if (block.Tag.Equals("LOCATIONS", StringComparison.OrdinalIgnoreCase))
            {
                current.Locations.AddRange(ParseList(block.Lines));
                continue;
            }

            if (block.Tag.Equals("SETUP", StringComparison.OrdinalIgnoreCase))
            {
                current.Setup.AddRange(ParseList(block.Lines));
                continue;
            }

            if (block.Tag.Equals("PAYOFF", StringComparison.OrdinalIgnoreCase))
            {
                current.Payoff.AddRange(ParseList(block.Lines));
            }
        }
        return map;
    }

    private static List<TagBlock> ParseTagBlocks(string text)
    {
        var blocks = new List<TagBlock>();
        if (string.IsNullOrWhiteSpace(text)) return blocks;

        TagBlock? current = null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("[") && line.Contains("]"))
            {
                var end = line.IndexOf(']');
                var tag = line.Substring(1, end - 1).Trim();
                var content = line[(end + 1)..].Trim();
                current = new TagBlock(tag);
                blocks.Add(current);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    current.Lines.Add(content);
                }
                continue;
            }

            // Keep common "KEY: value" field lines inside current block (ID/NAME/FROM/...),
            // so they are not misinterpreted as new top-level tags.
            if (current != null && FlexibleTagParser.TryParseKeyValueLine(line, out var fieldKey, out var fieldValue))
            {
                current.Lines.Add($"{fieldKey}: {fieldValue}");
                continue;
            }

            if (FlexibleTagParser.TryParseTagHeaderLine(line.Trim(), out var inlineTag, out var inlineValue) && IsLikelyTopLevelTagName(inlineTag))
            {
                current = new TagBlock(inlineTag);
                blocks.Add(current);
                if (!string.IsNullOrWhiteSpace(inlineValue))
                {
                    current.Lines.Add(inlineValue);
                }
                continue;
            }

            current?.Lines.Add(line.Trim());
        }

        return blocks;
    }

    private static Dictionary<string, string> ParseKeyValues(List<string> lines)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (!FlexibleTagParser.TryParseKeyValueLine(line, out var key, out var value)) continue;
            dict[key] = value;
        }
        return dict;
    }

    private static bool IsLikelyTopLevelTagName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (KnownNonTopLevelKeys.Contains(value))
        {
            return false;
        }

        return true;
    }

    private static readonly HashSet<string> KnownNonTopLevelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ID",
        "NAME",
        "ROLE",
        "BIO_SHORT",
        "EXTERNAL_GOAL",
        "INTERNAL_NEED",
        "FLAWS",
        "SKILLS",
        "LIMITS",
        "VOICE_STYLE",
        "FROM",
        "TO",
        "TYPE",
        "NOTES",
        "NUMBER",
        "TITLE",
        "LOGLINE",
        "A_PLOT",
        "B_PLOT",
        "THEME",
        "SUMMARY"
    };

    private static List<string> ParseList(List<string> lines)
    {
        var list = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- "))
            {
                list.Add(trimmed.Substring(2).Trim());
            }
            else if (!string.IsNullOrWhiteSpace(trimmed))
            {
                list.Add(trimmed);
            }
        }
        return list;
    }

    private static string? GetSingleTag(List<TagBlock> blocks, string tag)
    {
        var block = blocks.FirstOrDefault(b => b.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (block == null) return null;
        if (block.Lines.Count == 0) return null;
        if (block.Lines.Count == 1) return block.Lines[0];
        return string.Join(" ", block.Lines);
    }

    private static List<string> GetListTag(List<TagBlock> blocks, string tag)
    {
        var block = blocks.FirstOrDefault(b => b.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (block == null) return new List<string>();
        return ParseList(block.Lines);
    }

    private static string? JoinList(List<string> list)
        => list.Count == 0 ? null : string.Join(", ", list);

    private static int TryParseInt(string? value)
    {
        if (int.TryParse(value, out var n)) return n;
        return 0;
    }

    private static readonly IReadOnlyCollection<string> RequiredBibleTags = new[]
    {
        "SERIES_TITLE",
        "SERIES_GENRE",
        "SERIES_TONE",
        "SERIES_AUDIENCE",
        "SERIES_RATING",
        "SERIES_PREMISE",
        "SERIES_THEMES",
        "SERIES_FORBIDDEN_TOPICS",
        "SETTING_PLACE",
        "SETTING_TIME",
        "SETTING_TECH_LEVEL",
        "SETTING_WORLD_RULES",
        "FORMAT_EPISODE_DURATION",
        "FORMAT_SEASON_EPISODES",
        "FORMAT_STRUCTURE",
        "FORMAT_SERIALIZED_LEVEL",
        "CANON_FACTS",
        "RECURRING_LOCATIONS",
        "SEASON_LOGLINE",
        "SEASON_MAIN_ANTAGONISM",
        "SEASON_MID_TWIST",
        "SEASON_FINALE_PAYOFF"
    };

    private static readonly IReadOnlyCollection<string> RequiredCharacterTags = new[]
    {
        "CHARACTER",
        "RELATIONSHIP",
        "CHARACTER_SEASON_ARC"
    };

    private static readonly IReadOnlyCollection<string> RequiredSeasonTags = new[]
    {
        "EPISODE"
    };

    private static readonly IReadOnlyCollection<string> RequiredEpisodeStructureTags = new[]
    {
        "EPISODE_STRUCTURE",
        "BEAT",
        "CAST",
        "LOCATIONS",
        "SETUP",
        "PAYOFF"
    };

    private static readonly IReadOnlyCollection<string> RequiredValidatorTags = new[]
    {
        "VALIDATION_OK",
        "VALIDATION_ERROR"
    };

    private static string? ValidateBibleOutput(string text)
    {
        var blocks = ParseTagBlocks(text);
        foreach (var tag in RequiredBibleTags)
        {
            var block = blocks.FirstOrDefault(b => b.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
            if (block == null || block.Lines.Count == 0 || block.Lines.All(l => string.IsNullOrWhiteSpace(l)))
            {
                return $"Output bible incompleto: tag [{tag}] mancante o vuoto";
            }
        }

        if (GetListTag(blocks, "SERIES_GENRE").Count == 0)
        {
            return "Output bible incompleto: [SERIES_GENRE] deve contenere almeno 1 valore";
        }
        if (GetListTag(blocks, "SERIES_THEMES").Count == 0)
        {
            return "Output bible incompleto: [SERIES_THEMES] deve contenere almeno 1 valore";
        }

        return null;
    }

    private static string? ValidateCharactersOutput(string text)
    {
        var blocks = ParseTagBlocks(text);
        var characterKvs = blocks.Where(b => b.Tag.Equals("CHARACTER", StringComparison.OrdinalIgnoreCase))
            .Select(b => ParseKeyValues(b.Lines))
            .ToList();

        if (characterKvs.Count == 0)
        {
            return "Output characters incompleto: nessun blocco [CHARACTER]";
        }

        var ids = new List<string>();
        foreach (var kv in characterKvs)
        {
            if (!kv.TryGetValue("ID", out var id) || string.IsNullOrWhiteSpace(id))
            {
                return "Output characters incompleto: ogni [CHARACTER] deve avere ID:";
            }
            if (!kv.TryGetValue("NAME", out var name) || string.IsNullOrWhiteSpace(name))
            {
                return "Output characters incompleto: ogni [CHARACTER] deve avere NAME:";
            }
            ids.Add(id.Trim());
        }

        var dupId = ids.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (!string.IsNullOrWhiteSpace(dupId))
        {
            return $"Output characters non valido: ID personaggio duplicato: {dupId}";
        }

        var idSet = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        foreach (var rel in blocks.Where(b => b.Tag.Equals("RELATIONSHIP", StringComparison.OrdinalIgnoreCase)))
        {
            var kv = ParseKeyValues(rel.Lines);
            if (!kv.TryGetValue("FROM", out var from) || string.IsNullOrWhiteSpace(from)) return "Output relationship incompleto: FROM:";
            if (!kv.TryGetValue("TO", out var to) || string.IsNullOrWhiteSpace(to)) return "Output relationship incompleto: TO:";
            if (!idSet.Contains(from.Trim())) return $"Output relationship non valido: FROM id sconosciuto: {from}";
            if (!idSet.Contains(to.Trim())) return $"Output relationship non valido: TO id sconosciuto: {to}";
        }

        foreach (var arc in blocks.Where(b => b.Tag.Equals("CHARACTER_SEASON_ARC", StringComparison.OrdinalIgnoreCase)))
        {
            var kv = ParseKeyValues(arc.Lines);
            if (!kv.TryGetValue("ID", out var id) || string.IsNullOrWhiteSpace(id)) return "Output season arc incompleto: ID:";
            var idRef = id.Trim();
            var fromRef = kv.TryGetValue("FROM", out var fromValue) ? fromValue?.Trim() : null;

            // Flexible parser/validator:
            // - Some models use ID as character id
            // - Others use ID as arc id and refer the character in FROM
            var idMatchesCharacter = !string.IsNullOrWhiteSpace(idRef) && idSet.Contains(idRef);
            var fromMatchesCharacter = !string.IsNullOrWhiteSpace(fromRef) && idSet.Contains(fromRef!);
            if (!idMatchesCharacter && !fromMatchesCharacter)
            {
                return $"Output season arc non valido: riferimento personaggio sconosciuto (ID={id}, FROM={fromRef})";
            }
        }

        return null;
    }

    private static string? ValidateSeasonOutput(string text)
    {
        var blocks = ParseTagBlocks(text);
        var episodes = blocks.Where(b => b.Tag.Equals("EPISODE", StringComparison.OrdinalIgnoreCase)).ToList();
        if (episodes.Count == 0)
        {
            return "Output season incompleto: nessun blocco [EPISODE]";
        }

        var numbers = new HashSet<int>();
        foreach (var ep in episodes)
        {
            var kv = ParseKeyValues(ep.Lines);
            if (!kv.TryGetValue("NUMBER", out var n) || string.IsNullOrWhiteSpace(n)) return "Output season incompleto: EPISODE senza NUMBER:";
            var number = TryParseInt(n);
            if (number <= 0) return "Output season non valido: EPISODE NUMBER deve essere un intero > 0";
            if (!numbers.Add(number)) return $"Output season non valido: EPISODE NUMBER duplicato: {number}";

            if (!kv.TryGetValue("TITLE", out var title) || string.IsNullOrWhiteSpace(title)) return $"Output season incompleto: EPISODE {number} senza TITLE:";
            if (!kv.TryGetValue("LOGLINE", out var logline) || string.IsNullOrWhiteSpace(logline)) return $"Output season incompleto: EPISODE {number} senza LOGLINE:";
            if (!kv.TryGetValue("A_PLOT", out var a) || string.IsNullOrWhiteSpace(a)) return $"Output season incompleto: EPISODE {number} senza A_PLOT:";
            if (!kv.TryGetValue("B_PLOT", out var b) || string.IsNullOrWhiteSpace(b)) return $"Output season incompleto: EPISODE {number} senza B_PLOT:";
            if (!kv.TryGetValue("THEME", out var th) || string.IsNullOrWhiteSpace(th)) return $"Output season incompleto: EPISODE {number} senza THEME:";
        }

        return null;
    }

    private static string? ValidateEpisodeStructureOutput(string text, int expectedEpisodeNumber)
    {
        var blocks = ParseTagBlocks(text);
        var map = ParseEpisodeStructures(blocks);
        if (!map.TryGetValue(expectedEpisodeNumber, out var structure))
        {
            return $"Output episode_structure incompleto: [EPISODE_STRUCTURE] NUMBER deve essere {expectedEpisodeNumber}";
        }

        if (structure.Beats.Count < 3)
        {
            return $"Output episode_structure incompleto: episodio {expectedEpisodeNumber} deve avere almeno 3 [BEAT]";
        }

        if (structure.Beats.Any(b => string.IsNullOrWhiteSpace(b.Type) || string.IsNullOrWhiteSpace(b.Summary)))
        {
            return $"Output episode_structure incompleto: ogni [BEAT] deve avere TYPE e SUMMARY (episodio {expectedEpisodeNumber})";
        }

        if (structure.Cast.Count == 0)
        {
            return $"Output episode_structure incompleto: [CAST] vuoto (episodio {expectedEpisodeNumber})";
        }
        if (structure.Locations.Count == 0)
        {
            return $"Output episode_structure incompleto: [LOCATIONS] vuoto (episodio {expectedEpisodeNumber})";
        }
        if (structure.Setup.Count == 0)
        {
            return $"Output episode_structure incompleto: [SETUP] vuoto (episodio {expectedEpisodeNumber})";
        }
        if (structure.Payoff.Count == 0)
        {
            return $"Output episode_structure incompleto: [PAYOFF] vuoto (episodio {expectedEpisodeNumber})";
        }

        return null;
    }

    private sealed record SeriesBuildResult(Series Series, List<SeriesCharacter> Characters, List<SeriesEpisode> Episodes);

    private sealed class TagBlock
    {
        public string Tag { get; }
        public List<string> Lines { get; } = new();
        public string Raw => $"[{Tag}]\n{string.Join("\n", Lines)}";

        public TagBlock(string tag)
        {
            Tag = tag;
        }
    }

    private sealed class EpisodeBase
    {
        public int Number { get; set; }
        public string? Title { get; set; }
        public string? Logline { get; set; }
        public string? APlot { get; set; }
        public string? BPlot { get; set; }
        public string? Theme { get; set; }
        public string RawBlock { get; set; } = string.Empty;
    }

    private sealed class EpisodeStructure
    {
        public int Number { get; set; }
        public List<Beat> Beats { get; } = new();
        public List<string> Cast { get; } = new();
        public List<string> Locations { get; } = new();
        public List<string> Setup { get; } = new();
        public List<string> Payoff { get; } = new();
    }

    private sealed class Beat
    {
        public string? Type { get; set; }
        public string? Summary { get; set; }
    }
}
