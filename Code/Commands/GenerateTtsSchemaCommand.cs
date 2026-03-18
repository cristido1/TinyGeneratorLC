using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    internal sealed class GenerateTtsSchemaCommand : IStoryCommand, ICommand
    {
        private readonly StoriesService _service;

        public GenerateTtsSchemaCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var story = context.Story;
            var folderPath = context.FolderPath;

            var taggedText = story.StoryTagged;
            if (string.IsNullOrWhiteSpace(taggedText))
            {
                var revisedText = story.StoryRevised;
                if (string.IsNullOrWhiteSpace(revisedText))
                {
                    return (false, "Il testo taggato della storia e vuoto: eseguire prima la revisione (story_revised).");
                }

                taggedText = $"[NARRATORE]{Environment.NewLine}{revisedText.Trim()}";
                try
                {
                    _service._database.UpdateStoryTaggedContent(story.Id, taggedText);
                }
                catch
                {
                    // best-effort: fallback tagging should not block schema generation
                }

                _service._logger?.LogWarning(
                    "StoryTagged vuoto per story {StoryId}: applicato fallback narratore da story_revised prima di generate_tts_schema.",
                    story.Id);
            }

            var deleteCmd = new DeleteTtsSchemaCommand(_service);
            var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
            if (!cleanupOk)
            {
                return (false, cleanupMessage ?? "Impossibile cancellare il precedente schema TTS");
            }

            try
            {
                var generator = new TtsSchemaGenerator(_service._customLogger, _service._database);

                // If story belongs to a series, load characters from series_characters table
                List<StoryCharacter>? characters = null;
                Dictionary<string, string>? voiceAssignments = null;

                if (story.SerieId.HasValue)
                {
                    var seriesChars = _service._database.ListSeriesCharacters(story.SerieId.Value);
                    if (seriesChars.Any())
                    {
                        characters = seriesChars.Select(sc => new StoryCharacter
                        {
                            Name = sc.Name,
                            Gender = sc.Gender,
                            Age = sc.Eta,
                            Role = sc.Profilo,
                            // Add aliases if name contains spaces or special characters for better matching
                            Aliases = sc.Name.Contains(' ')
                                ? new List<string> { sc.Name.Split(' ').Last() }
                                : null
                        }).ToList();

                        // Build voice assignments dictionary from series characters with voice_id set
                        var voicesDb = _service._database.ListTtsVoices(onlyEnabled: false);
                        voiceAssignments = seriesChars
                            .Where(sc => sc.VoiceId.HasValue)
                            .Select(sc => new { sc.Name, sc.VoiceId })
                            .ToDictionary(
                                x => x.Name,
                                x => voicesDb.FirstOrDefault(v => v.Id == x.VoiceId!.Value)?.VoiceId ?? string.Empty,
                                StringComparer.OrdinalIgnoreCase);

                        _service._logger?.LogInformation(
                            "Loaded {CharCount} characters from series_characters for story {StoryId} (serieId={SerieId}), {VoiceCount} with voice assignments",
                            characters.Count, story.Id, story.SerieId.Value, voiceAssignments.Count);
                    }
                }

                var schema = generator.GenerateFromStoryText(taggedText, characters, voiceAssignments);
                context.CancellationToken.ThrowIfCancellationRequested();

                if (schema.Timeline.Count == 0)
                {
                    return (false,
                        "Nessuna frase trovata nel testo. Assicurati che il testo contenga tag come [NARRATORE], [personaggio, emozione] o [PERSONAGGIO: Nome] [EMOZIONE: emozione].");
                }

                generator.AssignVoices(schema);

                var schemaPath = Path.Combine(folderPath, "tts_schema.json");
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var json = JsonSerializer.Serialize(schema, jsonOptions);
                _service.SaveSanitizedTtsSchemaJson(schemaPath, json);

                try { _service._database.UpdateStoryGeneratedTtsJson(story.Id, true); } catch { }

                var normResults = new List<(string Name, bool Success, string? Message)>();
                context.CancellationToken.ThrowIfCancellationRequested();
                var (normCharOk, normCharMsg) = await _service.NormalizeCharacterNamesAsync(story.Id);
                normResults.Add(("NormalizeCharacterNames", normCharOk, normCharMsg));
                context.CancellationToken.ThrowIfCancellationRequested();
                var (assignVoicesOk, assignVoicesMsg) = await _service.AssignVoicesAsync(story.Id);
                normResults.Add(("AssignVoices", assignVoicesOk, assignVoicesMsg));
                context.CancellationToken.ThrowIfCancellationRequested();
                var (normSentOk, normSentMsg) = await _service.NormalizeSentimentsAsync(story.Id);
                normResults.Add(("NormalizeSentiments", normSentOk, normSentMsg));

                var summaryBuilder = new StringBuilder();
                summaryBuilder.AppendLine($"Schema TTS generato: {schema.Characters.Count} personaggi, {schema.Timeline.Count} frasi");
                var schemaSuccess = true;
                foreach (var result in normResults)
                {
                    schemaSuccess &= result.Success;
                    var messageText = result.Message ?? (result.Success ? "ok" : "fallito");
                    summaryBuilder.AppendLine($"{result.Name}: {messageText}");
                }

                // L'assegnazione/copia dei suoni da libreria viene gestita dai comandi separati
                // generate_ambience_audio / generate_fx_audio / generate_music_audio.
                // Qui non facciamo ricerca o download inline per mantenere il flusso accodato.

                var summaryMessage = summaryBuilder.ToString().Trim();

                var allowNext = _service.TryChangeStatus(story.Id, "generate_tts_schema", null);

                _service._logger?.LogInformation(
                    "TTS schema generato per storia {StoryId}: {Characters} personaggi, {Phrases} frasi",
                    story.Id, schema.Characters.Count, schema.Timeline.Count);

                // Requirement: optionally enqueue TTS audio generation after tts_schema.json.
                // This is best-effort and never blocks the schema command.
                var autolaunchTtsAudio = _service._audioGenerationOptions?.CurrentValue?.Tts?.AutolaunchNextCommand
                    ?? _service._ttsSchemaOptions?.CurrentValue?.AutolaunchNextCommand
                    ?? true;
                var runningInStatusChain = _service.IsCurrentDispatcherRunStatusChain();
                if (allowNext && autolaunchTtsAudio && !runningInStatusChain)
                {
                    try
                    {
                        _service.EnqueueGenerateTtsAudioCommand(story.Id, trigger: "tts_schema_generated", priority: 3);
                    }
                    catch
                    {
                        // ignore
                    }
                }
                else if (!allowNext)
                {
                    _service._logger?.LogInformation("TTS schema autolaunch skipped due to delete_next_items for story {StoryId}", story.Id);
                }
                else if (runningInStatusChain)
                {
                    _service._logger?.LogInformation("TTS schema autolaunch skipped in status_chain for story {StoryId}", story.Id);
                }
                else
                {
                    _service._logger?.LogInformation("TTS schema autolaunch disabled for story {StoryId}", story.Id);
                }

                return (schemaSuccess, summaryMessage);
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la generazione del TTS schema per la storia {Id}", story.Id);
                return (false, ex.Message);
            }
        }
    }
}
