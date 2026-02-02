
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

namespace TinyGenerator.Services.Commands;

public sealed class GenerateNewSerieCommand
{
    private readonly string _prompt;
    private readonly DatabaseService _database;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly ICustomLogger? _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly SeriesGenerationOptions _options;

    public GenerateNewSerieCommand(
        string prompt,
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        IOptionsMonitor<SeriesGenerationOptions>? optionsMonitor = null,
        ICustomLogger? logger = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _prompt = prompt ?? string.Empty;
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = optionsMonitor?.CurrentValue ?? new SeriesGenerationOptions();
    }

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_prompt))
        {
            return new CommandResult(false, "Prompt vuoto");
        }

        var runId = $"generate_new_serie_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        _logger?.Start(runId);

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
            var bibleResult = await CallRoleWithRetriesAsync(
                bibleAgent,
                "serie_bible_agent",
                biblePrompt,
                requiredTags: RequiredBibleTags,
                _options.Bible,
                runId,
                ct);
            if (!bibleResult.Success)
            {
                return new CommandResult(false, bibleResult.Error ?? "Serie bible fallita");
            }
            var bibleTags = bibleResult.Text!;

            var charactersPrompt = BuildCharactersPrompt(bibleTags);
            var charactersResult = await CallRoleWithRetriesAsync(
                characterAgent,
                "serie_character_agent",
                charactersPrompt,
                requiredTags: RequiredCharacterTags,
                _options.Characters,
                runId,
                ct);
            if (!charactersResult.Success)
            {
                return new CommandResult(false, charactersResult.Error ?? "Serie characters fallita");
            }
            var characterTags = charactersResult.Text!;

            var seasonPrompt = BuildSeasonPrompt(bibleTags, characterTags);
            var seasonResult = await CallRoleWithRetriesAsync(
                seasonAgent,
                "serie_season_agent",
                seasonPrompt,
                requiredTags: RequiredSeasonTags,
                _options.Episodes,
                runId,
                ct);
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
            foreach (var episode in baseEpisodes.OrderBy(e => e.Number))
            {
                ct.ThrowIfCancellationRequested();
                var episodePrompt = BuildEpisodePrompt(episode, bibleTags, characterTags);
                var epResult = await CallRoleWithRetriesAsync(
                    episodeAgent,
                    "serie_episode_agent",
                    episodePrompt,
                    requiredTags: RequiredEpisodeStructureTags,
                    _options.Episodes,
                    runId,
                    ct);
                if (!epResult.Success)
                {
                    return new CommandResult(false, epResult.Error ?? $"Serie episode fallita (episodio {episode.Number})");
                }
                episodeStructures.Add(epResult.Text!);
            }

            var validationPrompt = BuildValidatorPrompt(bibleTags, characterTags, seasonTags, episodeStructures);
            var validatorResult = await CallRoleWithRetriesAsync(
                validatorAgent,
                "serie_validator_agent",
                validationPrompt,
                requiredTags: RequiredValidatorTags,
                _options.Validator,
                runId,
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

            _logger?.Append(runId, $"Serie creata: id={serieId}, characters={result.Characters.Count}, episodes={result.Episodes.Count}");
            return new CommandResult(true, $"Serie creata (id {serieId})");
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(false, "Operazione annullata");
        }
        catch (Exception ex)
        {
            _logger?.Append(runId, $"Errore GenerateNewSerie: {ex.Message}");
            return new CommandResult(false, $"Errore GenerateNewSerie: {ex.Message}");
        }
        finally
        {
            _logger?.MarkCompleted(runId);
        }
    }

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
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, options.MaxAttempts);
        var delayMs = Math.Max(0, options.RetryDelaySeconds) * 1000;
        string? lastError = null;
        string? lastText = null;
        var currentPrompt = prompt;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _logger?.Append(runId, $"[{roleCode}] Tentativo {attempt}/{maxAttempts}");

            var response = await CallAgentAsync(agent, roleCode, currentPrompt, ct);
            if (response.Success)
            {
                lastText = response.Text;
                if (HasRequiredTags(lastText ?? string.Empty, requiredTags, out var missingTags))
                {
                    _logger?.MarkLatestModelResponseResult("SUCCESS", null, true);
                    return response;
                }

                var missingText = missingTags.Count > 0 ? string.Join(", ", missingTags) : "tag richiesti";
                lastError = $"Output privo di tag richiesti per {roleCode}: {missingText}";
                _logger?.Append(runId, $"[{roleCode}] {lastError}");
                _logger?.MarkLatestModelResponseResult("FAILED", lastError, true);
                currentPrompt = BuildRetryPrompt(prompt, roleCode, lastError, missingTags);
            }
            else
            {
                lastError = response.Error;
                _logger?.Append(runId, $"[{roleCode}] Errore: {lastError}");
                _logger?.MarkLatestModelResponseResult("FAILED", lastError, true);
            }

            if (attempt < maxAttempts && delayMs > 0)
            {
                await Task.Delay(delayMs, ct);
            }
        }

        if (options.DiagnoseOnFinalFailure)
        {
            await DiagnoseFailureAsync(agent, roleCode, prompt, lastText, runId, ct);
        }

        var fallback = await TryFallbackAsync(agent, roleCode, prompt, requiredTags, runId, ct);
        if (fallback.Success)
        {
            return fallback;
        }

        return AgentResponse.Fail(lastError ?? $"Operazione {roleCode} fallita");
    }

    private async Task<AgentResponse> CallAgentAsync(Agent agent, string roleCode, string prompt, CancellationToken ct)
    {
        var modelName = ResolveModelName(agent);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return AgentResponse.Fail($"Agente {agent.Name} senza modello configurato");
        }

        var systemPrompt = BuildSystemPrompt(agent);
        var response = await StateDrivenPipelineHelpers.CallModelAsync(
            _kernelFactory,
            _scopeFactory,
            modelName,
            agent,
            roleCode,
            systemPrompt,
            prompt,
            ct);

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

            var response = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ct);
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
        CancellationToken ct)
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

        (string? fallbackResult, ModelRole? successfulModelRole) = await fallbackService.ExecuteWithFallbackAsync<string>(
            roleCode,
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

                var response = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ct);
                return response ?? string.Empty;
            },
            validateResult: s => !string.IsNullOrWhiteSpace(s) && HasRequiredTags(s, requiredTags, out _));

        if (!string.IsNullOrWhiteSpace(fallbackResult) && successfulModelRole?.Model != null)
        {
            _logger?.Append(runId, $"[{roleCode}] Fallback model succeeded: {successfulModelRole.Model.Name}");
            return AgentResponse.Ok(fallbackResult!);
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
        if (string.IsNullOrWhiteSpace(text)) return tags;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length < 3) continue;
            if (!line.StartsWith("[", StringComparison.Ordinal)) continue;
            var end = line.IndexOf(']');
            if (end <= 1) continue;
            var tag = line.Substring(1, end - 1).Trim();
            if (string.IsNullOrWhiteSpace(tag)) continue;
            tags.Add(tag);
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
            var line = raw.TrimEnd();
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

            current?.Lines.Add(line.Trim());
        }

        return blocks;
    }

    private static Dictionary<string, string> ParseKeyValues(List<string> lines)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line.Substring(0, idx).Trim();
            var value = line[(idx + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(key)) continue;
            dict[key] = value;
        }
        return dict;
    }

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
        "SERIES_PREMISE",
        "SEASON_LOGLINE"
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
        "CAST"
    };

    private static readonly IReadOnlyCollection<string> RequiredValidatorTags = new[]
    {
        "VALIDATION_OK",
        "VALIDATION_ERROR"
    };

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
