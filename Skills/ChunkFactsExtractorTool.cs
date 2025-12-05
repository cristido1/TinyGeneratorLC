using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// Tool per estrarre fatti oggettivi da chunk di storia.
    /// Utilizzato nel primo passo della valutazione di coerenza chunk-by-chunk.
    /// </summary>
    public class ChunkFactsExtractorTool : BaseLangChainTool, ITinyTool
    {
        private readonly DatabaseService _database;
        private readonly HashSet<int> _processedChunks = new();

        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }
        public string? LastResult { get; set; }
        public long? CurrentStoryId { get; set; }

        private readonly int _storyChunkSize = 1500;

        public ChunkFactsExtractorTool(DatabaseService database, ICustomLogger? logger = null) 
            : base("extract_chunk_facts", "Extract objective facts from story chunks", logger)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public override Dictionary<string, object> GetSchema()
        {
            return CreateFunctionSchema(
                "extract_chunk_facts",
                "Extract and save objective facts (characters, locations, events, timeline) from a story chunk.",
                new Dictionary<string, object>
                {
                    { "chunk_number", new Dictionary<string, object> { { "type", "integer" }, { "description", "0-based chunk number" } } },
                    { "facts", new Dictionary<string, object> 
                        { 
                            { "type", "object" }, 
                            { "description", "JSON object with keys: characters (array), locations (array), events (array), timeline (array)" },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "characters", new Dictionary<string, object> { { "type", "array" }, { "items", new Dictionary<string, object> { { "type", "string" } } } } },
                                    { "locations", new Dictionary<string, object> { { "type", "array" }, { "items", new Dictionary<string, object> { { "type", "string" } } } } },
                                    { "events", new Dictionary<string, object> { { "type", "array" }, { "items", new Dictionary<string, object> { { "type", "string" } } } } },
                                    { "timeline", new Dictionary<string, object> { { "type", "array" }, { "items", new Dictionary<string, object> { { "type", "string" } } } } }
                                }
                            }
                        } 
                    }
                },
                new List<string> { "chunk_number", "facts" });
        }

        public override IEnumerable<Dictionary<string, object>> GetFunctionSchemas()
        {
            yield return GetSchema();
            yield return CreateFunctionSchema(
                "read_story_part",
                "Reads a segment of the story for facts extraction.",
                new Dictionary<string, object>
                {
                    { "part_index", new Dictionary<string, object> { { "type", "integer" }, { "description", "0-based segment index" } } }
                },
                new List<string> { "part_index" });
        }

        public override IEnumerable<string> FunctionNames
            => new[] { "extract_chunk_facts", "read_story_part" };

        public override Task<string> ExecuteAsync(string jsonInput)
        {
            return ExtractChunkFactsAsync(jsonInput);
        }

        public override Task<string> ExecuteFunctionAsync(string functionName, string input)
        {
            CustomLogger?.Log("Info", "ChunkFactsExtractor", $"[ExecuteFunctionAsync] Called with function: {functionName}, input length: {input?.Length ?? 0}");
            
            if (string.Equals(functionName, "extract_chunk_facts", StringComparison.OrdinalIgnoreCase))
            {
                CustomLogger?.Log("Info", "ChunkFactsExtractor", "[ExecuteFunctionAsync] Routing to ExtractChunkFactsAsync");
                return ExtractChunkFactsAsync(input);
            }
            if (string.Equals(functionName, "read_story_part", StringComparison.OrdinalIgnoreCase))
            {
                CustomLogger?.Log("Info", "ChunkFactsExtractor", "[ExecuteFunctionAsync] Routing to ReadStoryPartAsync");
                return ReadStoryPartAsync(input);
            }
            
            var error = $"Unknown function: {functionName}";
            CustomLogger?.Log("Error", "ChunkFactsExtractor", $"[ExecuteFunctionAsync] {error}");
            return Task.FromResult(JsonSerializer.Serialize(new { error }));
        }

        private async Task<string> ExtractChunkFactsAsync(string jsonInput)
        {
            CustomLogger?.Log("Info", "ChunkFactsExtractor", $"[ExtractChunkFactsAsync] START - StoryId: {CurrentStoryId}, Input: {jsonInput?.Substring(0, Math.Min(200, jsonInput?.Length ?? 0))}");
            
            try
            {
                LastFunctionCalled = "extract_chunk_facts";
                
                if (!CurrentStoryId.HasValue)
                {
                    var error = "No CurrentStoryId set on ChunkFactsExtractorTool";
                    CustomLogger?.Log("Error", "ChunkFactsExtractor", $"[ExtractChunkFactsAsync] {error}");
                    return JsonSerializer.Serialize(new { error });
                }

                CustomLogger?.Log("Info", "ChunkFactsExtractor", $"[ExtractChunkFactsAsync] Deserializing input...");
                var input = JsonSerializer.Deserialize<ExtractChunkFactsInput>(jsonInput);
                
                CustomLogger?.Log("Info", "ChunkFactsExtractor", $"[ExtractChunkFactsAsync] Deserialized - ChunkNumber: {input?.ChunkNumber}, Facts null: {input?.Facts == null}");
                
                if (input == null || input.Facts == null)
                {
                    var error = "Invalid input: missing facts";
                    CustomLogger?.Log("Error", "ChunkFactsExtractor", $"[ExtractChunkFactsAsync] {error}");
                    return JsonSerializer.Serialize(new { error });
                }

                CustomLogger?.Log("Info", "ChunkFactsExtractor", $"[ExtractChunkFactsAsync] Facts received - Characters: {input.Facts.Characters?.Count ?? 0}, Locations: {input.Facts.Locations?.Count ?? 0}, Events: {input.Facts.Events?.Count ?? 0}");

                // Verifica se il chunk è già stato processato
                if (_processedChunks.Contains(input.ChunkNumber))
                {
                    CustomLogger?.Log("Warn", "ChunkFactsExtractor", $"[ExtractChunkFactsAsync] Chunk {input.ChunkNumber} already processed");
                    return JsonSerializer.Serialize(new { 
                        warning = $"Chunk {input.ChunkNumber} already processed", 
                        chunk_number = input.ChunkNumber 
                    });
                }

                // Salva i fatti nel database
                CustomLogger?.Log("Info", "ChunkFactsExtractor", $"[ExtractChunkFactsAsync] Creating ChunkFacts object for story {CurrentStoryId}, chunk {input.ChunkNumber}");
                var chunkFacts = new ChunkFacts
                {
                    StoryId = (int)CurrentStoryId.Value,
                    ChunkNumber = input.ChunkNumber,
                    FactsJson = JsonSerializer.Serialize(input.Facts),
                    Ts = DateTime.UtcNow.ToString("o")
                };

                CustomLogger?.Log("Info", "ChunkFactsExtractor", $"[ExtractChunkFactsAsync] Calling _database.SaveChunkFacts...");
                _database.SaveChunkFacts(chunkFacts);
                CustomLogger?.Log("Info", "ChunkFactsExtractor", $"[ExtractChunkFactsAsync] Database save completed");
                
                _processedChunks.Add(input.ChunkNumber);
                CustomLogger?.Log("Info", "ChunkFactsExtractor", $"[ExtractChunkFactsAsync] Added chunk {input.ChunkNumber} to processed chunks. Total processed: {_processedChunks.Count}");

                CustomLogger?.Log("Info", "ChunkFactsExtractor", $"[ExtractChunkFactsAsync] ✅ SUCCESS - Saved facts for chunk {input.ChunkNumber} of story {CurrentStoryId}");

                var result = JsonSerializer.Serialize(new { 
                    success = true, 
                    chunk_number = input.ChunkNumber,
                    message = $"Facts extracted and saved for chunk {input.ChunkNumber}"
                });

                LastFunctionResult = result;
                LastResult = result;
                CustomLogger?.Log("Info", "ChunkFactsExtractor", $"[ExtractChunkFactsAsync] Returning result: {result}");
                return result;
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "ChunkFactsExtractor", $"[ChunkFactsExtractor] Error: {ex.Message}");
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        private async Task<string> ReadStoryPartAsync(string jsonInput)
        {
            CustomLogger?.Log("Info", "ChunkFactsExtractor", $"[ReadStoryPartAsync] START - Input: {jsonInput}");
            
            try
            {
                LastFunctionCalled = "read_story_part";

                if (!CurrentStoryId.HasValue)
                {
                    var error = "No CurrentStoryId set on ChunkFactsExtractorTool";
                    CustomLogger?.Log("Error", "ChunkFactsExtractor", error);
                    return JsonSerializer.Serialize(new { error });
                }

                var input = JsonSerializer.Deserialize<ReadStoryPartInput>(jsonInput);
                if (input == null)
                {
                    return JsonSerializer.Serialize(new { error = "Invalid input" });
                }

                var story = _database.GetStoryById(CurrentStoryId.Value);
                if (story == null)
                {
                    return JsonSerializer.Serialize(new { error = $"Story {CurrentStoryId} not found" });
                }

                var fullText = story.Story ?? string.Empty;
                var totalChunks = (int)Math.Ceiling((double)fullText.Length / _storyChunkSize);

                if (input.PartIndex < 0 || input.PartIndex >= totalChunks)
                {
                    return JsonSerializer.Serialize(new { error = $"Invalid part_index {input.PartIndex}. Story has {totalChunks} parts (0-{totalChunks - 1})" });
                }

                var start = input.PartIndex * _storyChunkSize;
                var length = Math.Min(_storyChunkSize, fullText.Length - start);
                var chunk = fullText.Substring(start, length);

                CustomLogger?.Log("Info", "ChunkFactsExtractor", $"[ChunkFactsExtractor] Read story part {input.PartIndex} for story {CurrentStoryId}: chunk_size={_storyChunkSize}, start={start}, length={length}, content_length={chunk.Length}");

                var result = JsonSerializer.Serialize(new
                {
                    part_index = input.PartIndex,
                    total_parts = totalChunks,
                    content = chunk
                });

                LastFunctionResult = result;
                return result;
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "ChunkFactsExtractor", $"[ChunkFactsExtractor] ReadStoryPart error: {ex.Message}");
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        public bool HasProcessedChunk(int chunkNumber) => _processedChunks.Contains(chunkNumber);

        public IReadOnlyCollection<int> ProcessedChunks => _processedChunks;

        public void Reset()
        {
            _processedChunks.Clear();
        }

        private class ReadStoryPartInput
        {
            public int PartIndex { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("part_index")]
            public int PartIndexAlt { set => PartIndex = value; }
        }

        private class ExtractChunkFactsInput
        {
            public int ChunkNumber { get; set; }
            public ChunkFactsData? Facts { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("chunk_number")]
            public int ChunkNumberAlt { set => ChunkNumber = value; }
            
            [System.Text.Json.Serialization.JsonPropertyName("facts")]
            public ChunkFactsData? FactsAlt { set => Facts = value; }
        }

        private class ChunkFactsData
        {
            [System.Text.Json.Serialization.JsonPropertyName("characters")]
            public List<string> Characters { get; set; } = new();
            
            [System.Text.Json.Serialization.JsonPropertyName("locations")]
            public List<string> Locations { get; set; } = new();
            
            [System.Text.Json.Serialization.JsonPropertyName("events")]
            public List<string> Events { get; set; } = new();
            
            [System.Text.Json.Serialization.JsonPropertyName("timeline")]
            public List<string> Timeline { get; set; } = new();
        }
    }
}
