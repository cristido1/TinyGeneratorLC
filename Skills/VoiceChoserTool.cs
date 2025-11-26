using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain tool for assigning voices to TTS schema characters.
    /// Provides helpers to inspect the schema JSON and available TTS voices.
    /// </summary>
    public class VoiceChoserTool : BaseLangChainTool, ITinyTool
    {
        private readonly string _storyFolder;
        private readonly string _schemaPath;
        private readonly TtsService _ttsService;
        private List<VoiceInfo>? _cachedVoices;
        private DateTime _voicesCachedAt = DateTime.MinValue;

        public VoiceChoserTool(string storyFolder, TtsService ttsService, ICustomLogger? logger = null)
            : base("voicechoser", "Utility per leggere e aggiornare il file tts_schema.json scegliendo le voci TTS.", logger)
        {
            if (string.IsNullOrWhiteSpace(storyFolder))
                throw new ArgumentException("storyFolder is required", nameof(storyFolder));

            _storyFolder = storyFolder;
            _schemaPath = Path.Combine(storyFolder, "tts_schema.json");
            _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
        }

        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }

        public override IEnumerable<string> FunctionNames
        {
            get
            {
                yield return Name;
                yield return "read_json";
                yield return "read_voices";
                yield return "set_voice";
            }
        }

        public override Dictionary<string, object> GetSchema() => CreateReadJsonSchema();

        public override IEnumerable<Dictionary<string, object>> GetFunctionSchemas()
        {
            yield return CreateReadJsonSchema();
            yield return CreateReadVoicesSchema();
            yield return CreateSetVoiceSchema();
        }

        public override Task<string> ExecuteAsync(string input)
        {
            var request = ParseInput<VoiceChoserRequest>(input);
            if (request?.Operation == null)
            {
                return Task.FromResult(SerializeResult(new { error = "Operation is required" }));
            }

            return ExecuteFunctionAsync(request.Operation, input);
        }

        public override async Task<string> ExecuteFunctionAsync(string functionName, string input)
        {
            LastFunctionCalled = functionName;
            string result = functionName.ToLowerInvariant() switch
            {
                "read_json" => await HandleReadJsonAsync(),
                "read_voices" => await HandleReadVoicesAsync(),
                "set_voice" => await HandleSetVoiceAsync(input),
                "voicechoser" => SerializeResult(new { result = "Available operations: read_json(), read_voices(), set_voice(character, gender, voice_id)" }),
                _ => SerializeResult(new { error = $"Unknown function: {functionName}" })
            };
            LastFunctionResult = result;
            return result;
        }

        private async Task<string> HandleReadJsonAsync()
        {
            try
            {
                if (!File.Exists(_schemaPath))
                    return SerializeResult(new { error = $"File not found: {_schemaPath}" });

                var content = await File.ReadAllTextAsync(_schemaPath);
                return content;
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "VoiceChoserTool", $"Failed to read schema: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> HandleReadVoicesAsync()
        {
            try
            {
                var voices = await EnsureVoicesAsync();
                var simplified = voices.Select(v => new
                {
                    id = v.Id,
                    name = v.Name,
                    gender = v.Gender,
                    language = v.Language
                });
                return JsonSerializer.Serialize(simplified, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "VoiceChoserTool", $"Failed to read voices: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> HandleSetVoiceAsync(string input)
        {
            try
            {
                var payload = ParseInput<SetVoiceRequest>(input);
                if (payload == null)
                    return SerializeResult(new { error = "Invalid input format" });

                if (string.IsNullOrWhiteSpace(payload.Character))
                    return SerializeResult(new { error = "character is required" });

                if (string.IsNullOrWhiteSpace(payload.VoiceId))
                    return SerializeResult(new { error = "voice_id is required" });

                if (string.IsNullOrWhiteSpace(payload.Gender))
                    return SerializeResult(new { error = "gender is required" });

                if (!File.Exists(_schemaPath))
                    return SerializeResult(new { error = $"File not found: {_schemaPath}" });

                var schema = await LoadSchemaAsync();
                if (schema == null)
                    return SerializeResult(new { error = "Unable to parse tts_schema.json" });

                var character = schema.Characters
                    .FirstOrDefault(c => c.Name != null && c.Name.Equals(payload.Character, StringComparison.OrdinalIgnoreCase));

                if (character == null)
                {
                    return SerializeResult(new { error = $"Character '{payload.Character}' not found" });
                }

                var voices = await EnsureVoicesAsync();
                var voice = voices.FirstOrDefault(v => 
                    (!string.IsNullOrWhiteSpace(v.Id) && v.Id.Equals(payload.VoiceId, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(v.Name) && v.Name.Equals(payload.VoiceId, StringComparison.OrdinalIgnoreCase)));

                if (voice != null && !string.IsNullOrWhiteSpace(voice.Gender))
                {
                    var compareVoiceGender = voice.Gender.Trim();
                    if (!string.IsNullOrWhiteSpace(compareVoiceGender) &&
                        !string.Equals(compareVoiceGender, payload.Gender, StringComparison.OrdinalIgnoreCase))
                    {
                        return SerializeResult(new { error = $"Voice gender '{voice.Gender}' does not match requested gender '{payload.Gender}'" });
                    }
                }

                character.Gender = payload.Gender;
                character.VoiceId = payload.VoiceId;
                character.Voice = voice?.Name ?? payload.VoiceId;

                await SaveSchemaAsync(schema);

                var result = new
                {
                    character = character.Name,
                    gender = character.Gender,
                    voice_id = character.VoiceId,
                    voice_name = character.Voice
                };

                return SerializeResult(new { result, path = _schemaPath });
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "VoiceChoserTool", $"Failed to set voice: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<List<VoiceInfo>> EnsureVoicesAsync()
        {
            if (_cachedVoices != null && (DateTime.UtcNow - _voicesCachedAt).TotalMinutes < 5)
            {
                return _cachedVoices;
            }

            var list = await _ttsService.GetVoicesAsync();
            _cachedVoices = list ?? new List<VoiceInfo>();
            _voicesCachedAt = DateTime.UtcNow;
            return _cachedVoices;
        }

        private async Task<TtsSchemaFile?> LoadSchemaAsync()
        {
            try
            {
                var content = await File.ReadAllTextAsync(_schemaPath);
                var schema = JsonSerializer.Deserialize<TtsSchemaFile>(content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return schema;
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "VoiceChoserTool", $"Failed to load schema: {ex.Message}", ex.ToString());
                return null;
            }
        }

        private async Task SaveSchemaAsync(TtsSchemaFile schema)
        {
            var json = JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_schemaPath, json);
        }

        private Dictionary<string, object> CreateReadJsonSchema()
        {
            return CreateFunctionSchema(
                "read_json",
                "Legge e restituisce il contenuto del file tts_schema.json della storia corrente.",
                new Dictionary<string, object>(),
                new List<string>());
        }

        private Dictionary<string, object> CreateReadVoicesSchema()
        {
            return CreateFunctionSchema(
                "read_voices",
                "Restituisce l'elenco delle voci disponibili dal servizio TTS (id, nome, genere, lingua).",
                new Dictionary<string, object>(),
                new List<string>());
        }

        private Dictionary<string, object> CreateSetVoiceSchema()
        {
            return CreateFunctionSchema(
                "set_voice",
                "Assegna una voce ad un personaggio impostando genere e VoiceId nel tts_schema.json.",
                new Dictionary<string, object>
                {
                    { "character", new Dictionary<string, object> { { "type", "string" }, { "description", "Nome del personaggio (incluso Narratore)" } } },
                    { "gender", new Dictionary<string, object> { { "type", "string" }, { "description", "Genere da assegnare (es. male, female, neutral)" } } },
                    { "voice_id", new Dictionary<string, object> { { "type", "string" }, { "description", "Identificativo della voce da usare" } } }
                },
                new List<string> { "character", "gender", "voice_id" });
        }

        private class VoiceChoserRequest
        {
            public string? Operation { get; set; }
        }

        private class SetVoiceRequest
        {
            public string? Operation { get; set; }
            public string? Character { get; set; }
            public string? Gender { get; set; }
            public string? VoiceId { get; set; }
        }

        private class TtsSchemaFile
        {
            public List<TtsSchemaCharacter> Characters { get; set; } = new();
            public List<JsonElement> Timeline { get; set; } = new();
        }

        private class TtsSchemaCharacter
        {
            public string? Name { get; set; }
            public string? Voice { get; set; }
            public string? VoiceId { get; set; }
            public string? Gender { get; set; }
            public string? EmotionDefault { get; set; }
        }
    }
}
