using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    internal sealed class RepairTtsSchemaAudioMetadataCommand : IStoryCommand, ICommand
    {
        private readonly StoriesService _service;

        public RepairTtsSchemaAudioMetadataCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var story = context.Story;
            var folderPath = context.FolderPath;

            if (string.IsNullOrWhiteSpace(story.StoryTagged))
            {
                return (false, "story_tagged vuoto: impossibile riparare i metadati audio");
            }

            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            if (!File.Exists(schemaPath))
            {
                return (false, "tts_schema.json non trovato");
            }

            JsonObject? currentRoot;
            try
            {
                var currentJson = await File.ReadAllTextAsync(schemaPath, context.CancellationToken).ConfigureAwait(false);
                currentRoot = JsonNode.Parse(currentJson) as JsonObject;
            }
            catch (Exception ex)
            {
                return (false, $"Impossibile leggere tts_schema.json: {ex.Message}");
            }

            if (currentRoot == null)
            {
                return (false, "Formato tts_schema.json non valido");
            }

            if (!(currentRoot["timeline"] is JsonArray currentTimeline))
            {
                currentTimeline = currentRoot["Timeline"] as JsonArray;
            }

            if (currentTimeline == null)
            {
                return (false, "Timeline mancante in tts_schema.json");
            }

            var currentEntries = currentTimeline.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
            if (currentEntries.Count == 0)
            {
                return (false, "La timeline non contiene frasi");
            }

            var generator = new TtsSchemaGenerator(_service._customLogger, _service._database);
            var referenceSchema = generator.GenerateFromStoryText(story.StoryTagged);
            var referenceEntries = referenceSchema.Timeline
                .Select(ToJsonObject)
                .Where(e => e != null && IsPhraseEntry(e))
                .Cast<JsonObject>()
                .ToList();

            if (referenceEntries.Count == 0)
            {
                return (false, "Impossibile ricostruire metadati audio da story_tagged");
            }

            var mergeCount = Math.Min(currentEntries.Count, referenceEntries.Count);
            var updatedAmbient = 0;
            var updatedFx = 0;
            var updatedMusic = 0;

            for (var i = 0; i < mergeCount; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var current = currentEntries[i];
                var reference = referenceEntries[i];

                if (TryMergeAmbient(current, reference))
                {
                    updatedAmbient++;
                }

                if (TryMergeFx(current, reference))
                {
                    updatedFx++;
                }

                if (TryMergeMusic(current, reference))
                {
                    updatedMusic++;
                }
            }

            try
            {
                SanitizeTtsSchemaTextFields(currentRoot);
                await File.WriteAllTextAsync(schemaPath, currentRoot.ToJsonString(SchemaJsonOptions), context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return (false, $"Impossibile salvare tts_schema.json riparato: {ex.Message}");
            }

            var summary = $"Riparazione metadati audio completata: ambient={updatedAmbient}, fx={updatedFx}, music={updatedMusic}, righe elaborate={mergeCount}. Dialoghi e timing invariati.";
            return (true, summary);
        }

        private static JsonObject? ToJsonObject(object? entry)
        {
            var node = JsonSerializer.SerializeToNode(entry, SchemaJsonOptions);
            return node as JsonObject;
        }

        private static bool TryMergeAmbient(JsonObject current, JsonObject reference)
        {
            var changed = false;

            var tags = ReadAmbientSoundsTags(reference);
            if (!string.IsNullOrWhiteSpace(tags))
            {
                changed |= SetStringIfMissing(current, tags, "ambient_sound_tags", "ambientSoundTags");
            }

            var desc = ReadAmbientSoundsDescription(reference);
            if (!string.IsNullOrWhiteSpace(desc))
            {
                changed |= SetStringIfMissing(current, desc, "ambient_sound_description", "ambientSoundDescription", "ambientSounds");
            }

            if (TryReadNumber(reference, "ambientSoundsDuration", out var duration) ||
                TryReadNumber(reference, "ambient_sounds_duration", out duration) ||
                TryReadNumber(reference, "AmbientSoundsDuration", out duration))
            {
                var parsed = (int)Math.Ceiling(duration);
                if (parsed > 0)
                {
                    changed |= SetNumberIfMissing(current, parsed, "ambientSoundsDuration", "ambient_sounds_duration");
                }
            }

            return changed;
        }

        private static bool TryMergeFx(JsonObject current, JsonObject reference)
        {
            var changed = false;

            var tags = ReadFxTags(reference);
            if (!string.IsNullOrWhiteSpace(tags))
            {
                changed |= SetStringIfMissing(current, tags, "fx_tags", "fxTags");
            }

            var desc = ReadString(reference, "fx_description") ??
                       ReadString(reference, "fxDescription") ??
                       ReadString(reference, "FxDescription");
            if (!string.IsNullOrWhiteSpace(desc))
            {
                changed |= SetStringIfMissing(current, desc, "fx_description", "fxDescription");
            }

            if (TryReadNumber(reference, "fxDuration", out var duration) ||
                TryReadNumber(reference, "fx_duration", out duration) ||
                TryReadNumber(reference, "FxDuration", out duration))
            {
                var parsed = (int)Math.Ceiling(duration);
                if (parsed > 0)
                {
                    changed |= SetNumberIfMissing(current, parsed, "fxDuration", "fx_duration");
                }
            }

            return changed;
        }

        private static bool TryMergeMusic(JsonObject current, JsonObject reference)
        {
            var changed = false;

            var tags = ReadMusicTags(reference);
            if (!string.IsNullOrWhiteSpace(tags))
            {
                changed |= SetStringIfMissing(current, tags, "music_tags", "musicTags");
            }

            var desc = ReadString(reference, "music_description") ??
                       ReadString(reference, "musicDescription") ??
                       ReadString(reference, "MusicDescription");
            if (!string.IsNullOrWhiteSpace(desc))
            {
                changed |= SetStringIfMissing(current, desc, "music_description", "musicDescription");
            }

            var preferredSecs = ReadMusicPreferredDurationSeconds(reference);
            if (preferredSecs.HasValue && preferredSecs.Value > 0)
            {
                changed |= SetNumberIfMissing(current, preferredSecs.Value, "musicDurationSecs", "music_duration_secs");
            }

            if (TryReadNumber(reference, "musicDuration", out var duration) ||
                TryReadNumber(reference, "music_duration", out duration) ||
                TryReadNumber(reference, "MusicDuration", out duration))
            {
                var parsed = (int)Math.Ceiling(duration);
                if (parsed > 0)
                {
                    changed |= SetNumberIfMissing(current, parsed, "musicDuration", "music_duration");
                }
            }

            return changed;
        }

        private static bool SetStringIfMissing(JsonObject target, string value, params string[] propertyNames)
        {
            if (string.IsNullOrWhiteSpace(value) || propertyNames.Length == 0)
            {
                return false;
            }

            var currentValue = propertyNames
                .Select(name => ReadString(target, name))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (!string.IsNullOrWhiteSpace(currentValue))
            {
                return false;
            }

            foreach (var propertyName in propertyNames)
            {
                target[propertyName] = value;
            }

            return true;
        }

        private static bool SetNumberIfMissing(JsonObject target, int value, params string[] propertyNames)
        {
            if (value <= 0 || propertyNames.Length == 0)
            {
                return false;
            }

            foreach (var propertyName in propertyNames)
            {
                if (TryReadNumber(target, propertyName, out var current) && current > 0)
                {
                    return false;
                }
            }

            foreach (var propertyName in propertyNames)
            {
                target[propertyName] = value;
            }

            return true;
        }
    }
}
