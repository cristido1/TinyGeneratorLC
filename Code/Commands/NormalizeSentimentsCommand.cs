using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    /// <summary>
    /// Command that normalizes emotions/sentiments in tts_schema.json to TTS-supported values.
    /// Supported sentiments: neutral, happy, sad, angry, fearful, disgusted, surprised
    /// </summary>
    private sealed class NormalizeSentimentsCommand : IStoryCommand, ICommand
    {
        private readonly StoriesService _service;

        public NormalizeSentimentsCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var story = context.Story;
                var folderPath = context.FolderPath;

                var schemaPath = Path.Combine(folderPath, "tts_schema.json");
                if (!File.Exists(schemaPath))
                    return (false, "File tts_schema.json non trovato. Genera prima lo schema TTS.");

                // Load TTS schema
                var schemaJson = await File.ReadAllTextAsync(schemaPath, context.CancellationToken);
                var schema = JsonSerializer.Deserialize<TtsSchema>(schemaJson, SchemaJsonOptions);
                if (schema == null)
                    return (false, "Impossibile deserializzare tts_schema.json");

                if (schema.Timeline == null || schema.Timeline.Count == 0)
                    return (false, "Schema TTS vuoto o senza timeline.");

                // Get or create SentimentMappingService
                if (_service._sentimentMappingService == null)
                    return (false, "SentimentMappingService non disponibile");

                // Normalize sentiments
                var (normalized, total) = await _service._sentimentMappingService.NormalizeTtsSchemaAsync(schema);

                // Save updated schema
                var outputOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var updatedJson = JsonSerializer.Serialize(schema, outputOptions);
                await _service.SaveSanitizedTtsSchemaJsonAsync(schemaPath, updatedJson);
                context.CancellationToken.ThrowIfCancellationRequested();

                _service._logger?.LogInformation(
                    "Normalized {Normalized}/{Total} sentiments in tts_schema.json for story {StoryId}",
                    normalized, total, story.Id);

                return (true, $"Normalizzati {normalized} sentimenti su {total} frasi.");
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la normalizzazione dei sentimenti per la storia {Id}", context.Story.Id);
                return (false, ex.Message);
            }
        }
    }
}
