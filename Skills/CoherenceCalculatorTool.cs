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
    /// Tool per calcolare la coerenza locale e globale di chunk di storia.
    /// Utilizzato nel secondo passo della valutazione di coerenza chunk-by-chunk.
    /// </summary>
    public class CoherenceCalculatorTool : BaseLangChainTool, ITinyTool
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

        public CoherenceCalculatorTool(DatabaseService database, ICustomLogger? logger = null) 
            : base("calculate_coherence", "Calculate local and global coherence for story chunks", logger)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public override Dictionary<string, object> GetSchema()
        {
            return CreateFunctionSchema(
                "calculate_coherence",
                "Calculate and save local coherence (with previous chunk) and global coherence (with all previous chunks) for a chunk.",
                new Dictionary<string, object>
                {
                    { "chunk_number", new Dictionary<string, object> { { "type", "integer" }, { "description", "0-based chunk number being evaluated" } } },
                    { "local_coherence", new Dictionary<string, object> { { "type", "number" }, { "description", "Coherence score with previous chunk (0.0-1.0)" } } },
                    { "global_coherence", new Dictionary<string, object> { { "type", "number" }, { "description", "Coherence score with entire story so far (0.0-1.0)" } } },
                    { "errors", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional: description of detected inconsistencies" } } }
                },
                // Keep chunk_number/local_coherence required, make global_coherence optional to allow the tool to default it
                new List<string> { "chunk_number", "local_coherence" });
        }

        public override IEnumerable<Dictionary<string, object>> GetFunctionSchemas()
        {
            yield return GetSchema();
            yield return CreateFunctionSchema(
                "get_chunk_facts",
                "Retrieve previously extracted facts for a specific chunk.",
                new Dictionary<string, object>
                {
                    { "chunk_number", new Dictionary<string, object> { { "type", "integer" }, { "description", "0-based chunk number" } } }
                },
                new List<string> { "chunk_number" });
            yield return CreateFunctionSchema(
                "get_all_previous_facts",
                "Retrieve all facts extracted from all previous chunks (0 to chunk_number-1).",
                new Dictionary<string, object>
                {
                    { "up_to_chunk", new Dictionary<string, object> { { "type", "integer" }, { "description", "Retrieve facts up to this chunk (exclusive)" } } }
                },
                new List<string> { "up_to_chunk" });
            yield return CreateFunctionSchema(
                "finalize_global_coherence",
                "Calculate and save the final global coherence score for the entire story.",
                new Dictionary<string, object>
                {
                    { "global_coherence", new Dictionary<string, object> { { "type", "number" }, { "description", "Final global coherence score (0.0-1.0)" } } },
                    { "notes", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional: summary of coherence analysis" } } }
                },
                new List<string> { "global_coherence" });
        }

        public override IEnumerable<string> FunctionNames
            => new[] { "calculate_coherence", "get_chunk_facts", "get_all_previous_facts", "finalize_global_coherence" };

        public override Task<string> ExecuteAsync(string jsonInput)
        {
            return CalculateCoherenceAsync(jsonInput);
        }

        public override Task<string> ExecuteFunctionAsync(string functionName, string input)
        {
            return functionName.ToLowerInvariant() switch
            {
                "calculate_coherence" => CalculateCoherenceAsync(input),
                "get_chunk_facts" => GetChunkFactsAsync(input),
                "get_all_previous_facts" => GetAllPreviousFactsAsync(input),
                "finalize_global_coherence" => FinalizeGlobalCoherenceAsync(input),
                _ => Task.FromResult(JsonSerializer.Serialize(new { error = $"Unknown function: {functionName}" }))
            };
        }

        private async Task<string> CalculateCoherenceAsync(string jsonInput)
        {
            try
            {
                await Task.CompletedTask;
                LastFunctionCalled = "calculate_coherence";
                
                if (!CurrentStoryId.HasValue)
                {
                    var error = "No CurrentStoryId set on CoherenceCalculatorTool";
                    CustomLogger?.Log("Error", "CoherenceCalculator", error);
                    return JsonSerializer.Serialize(new { error });
                }

                var input = JsonSerializer.Deserialize<CalculateCoherenceInput>(jsonInput);
                if (input == null)
                {
                    return JsonSerializer.Serialize(new { error = "Invalid input" });
                }

                if (!input.LocalCoherence.HasValue)
                {
                    return JsonSerializer.Serialize(new { error = "local_coherence is required" });
                }

                // Permettiamo al modello di omettere global_coherence: usiamo local_coherence come default
                var local = input.LocalCoherence.Value;
                var global = input.GlobalCoherence ?? local;

                if (local < 0 || local > 1 || global < 0 || global > 1)
                {
                    return JsonSerializer.Serialize(new { error = "Coherence scores must be between 0.0 and 1.0" });
                }

                // Verifica se il chunk è già stato processato
                if (_processedChunks.Contains(input.ChunkNumber))
                {
                    return JsonSerializer.Serialize(new { 
                        warning = $"Chunk {input.ChunkNumber} already processed", 
                        chunk_number = input.ChunkNumber 
                    });
                }

                // Salva gli score nel database
                var coherenceScore = new CoherenceScore
                {
                    StoryId = (int)CurrentStoryId.Value,
                    ChunkNumber = input.ChunkNumber,
                    LocalCoherence = local,
                    GlobalCoherence = global,
                    Errors = input.Errors,
                    Ts = DateTime.UtcNow.ToString("o")
                };

                _database.SaveCoherenceScore(coherenceScore);
                _processedChunks.Add(input.ChunkNumber);

                CustomLogger?.Log("Info", "CoherenceCalculator", $"[CoherenceCalculator] Saved coherence for chunk {input.ChunkNumber} of story {CurrentStoryId}: local={input.LocalCoherence:F2}, global={input.GlobalCoherence:F2}");

                var result = JsonSerializer.Serialize(new { 
                    success = true, 
                    chunk_number = input.ChunkNumber,
                    local_coherence = input.LocalCoherence,
                    global_coherence = input.GlobalCoherence,
                    message = $"Coherence calculated and saved for chunk {input.ChunkNumber}"
                });

                LastFunctionResult = result;
                LastResult = result;
                return result;
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "CoherenceCalculator", $"[CoherenceCalculator] Error: {ex.Message}");
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        private async Task<string> GetChunkFactsAsync(string jsonInput)
        {
            try
            {
                await Task.CompletedTask;
                LastFunctionCalled = "get_chunk_facts";

                if (!CurrentStoryId.HasValue)
                {
                    var error = "No CurrentStoryId set on CoherenceCalculatorTool";
                    CustomLogger?.Log("Error", "CoherenceCalculator", error);
                    return JsonSerializer.Serialize(new { error });
                }

                var input = JsonSerializer.Deserialize<GetChunkFactsInput>(jsonInput);
                if (input == null)
                {
                    return JsonSerializer.Serialize(new { error = "Invalid input" });
                }

                var facts = _database.GetChunkFacts((int)CurrentStoryId.Value, input.ChunkNumber);
                if (facts == null)
                {
                    return JsonSerializer.Serialize(new { 
                        error = $"No facts found for chunk {input.ChunkNumber}",
                        chunk_number = input.ChunkNumber
                    });
                }

                CustomLogger?.Log("Info", "CoherenceCalculator", $"[CoherenceCalculator] Retrieved facts for chunk {input.ChunkNumber} of story {CurrentStoryId}");

                var result = JsonSerializer.Serialize(new
                {
                    chunk_number = facts.ChunkNumber,
                    facts = JsonSerializer.Deserialize<object>(facts.FactsJson)
                });

                LastFunctionResult = result;
                return result;
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "CoherenceCalculator", $"[CoherenceCalculator] GetChunkFacts error: {ex.Message}");
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        private async Task<string> GetAllPreviousFactsAsync(string jsonInput)
        {
            try
            {
                await Task.CompletedTask;
                LastFunctionCalled = "get_all_previous_facts";

                if (!CurrentStoryId.HasValue)
                {
                    var error = "No CurrentStoryId set on CoherenceCalculatorTool";
                    CustomLogger?.Log("Error", "CoherenceCalculator", error);
                    return JsonSerializer.Serialize(new { error });
                }

                var input = JsonSerializer.Deserialize<GetAllPreviousFactsInput>(jsonInput);
                if (input == null)
                {
                    return JsonSerializer.Serialize(new { error = "Invalid input" });
                }

                var allFacts = _database.GetAllChunkFacts((int)CurrentStoryId.Value)
                    .Where(f => f.ChunkNumber < input.UpToChunk)
                    .OrderBy(f => f.ChunkNumber)
                    .ToList();

                CustomLogger?.Log("Info", "CoherenceCalculator", $"[CoherenceCalculator] Retrieved {allFacts.Count} previous facts (up to chunk {input.UpToChunk}) for story {CurrentStoryId}");

                var factsArray = allFacts.Select(f => new
                {
                    chunk_number = f.ChunkNumber,
                    facts = JsonSerializer.Deserialize<object>(f.FactsJson)
                }).ToArray();

                var result = JsonSerializer.Serialize(new
                {
                    count = factsArray.Length,
                    chunks = factsArray
                });

                LastFunctionResult = result;
                return result;
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "CoherenceCalculator", $"[CoherenceCalculator] GetAllPreviousFacts error: {ex.Message}");
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        private async Task<string> FinalizeGlobalCoherenceAsync(string jsonInput)
        {
            try
            {
                await Task.CompletedTask;
                LastFunctionCalled = "finalize_global_coherence";

                if (!CurrentStoryId.HasValue)
                {
                    var error = "No CurrentStoryId set on CoherenceCalculatorTool";
                    CustomLogger?.Log("Error", "CoherenceCalculator", error);
                    return JsonSerializer.Serialize(new { error });
                }

                var input = JsonSerializer.Deserialize<FinalizeGlobalCoherenceInput>(jsonInput);
                if (input == null)
                {
                    return JsonSerializer.Serialize(new { error = "Invalid input" });
                }

                if (input.GlobalCoherence < 0 || input.GlobalCoherence > 1)
                {
                    return JsonSerializer.Serialize(new { error = "Global coherence must be between 0.0 and 1.0" });
                }

                var chunkCount = _database.GetCoherenceScores((int)CurrentStoryId.Value).Count;

                var globalCoherence = new GlobalCoherence
                {
                    StoryId = (int)CurrentStoryId.Value,
                    GlobalCoherenceValue = input.GlobalCoherence,
                    ChunkCount = chunkCount,
                    Notes = input.Notes,
                    Ts = DateTime.UtcNow.ToString("o")
                };

                _database.SaveGlobalCoherence(globalCoherence);

                CustomLogger?.Log("Info", "CoherenceCalculator", $"[CoherenceCalculator] Finalized global coherence for story {CurrentStoryId}: {input.GlobalCoherence:F2} ({chunkCount} chunks)");

                var result = JsonSerializer.Serialize(new
                {
                    success = true,
                    story_id = CurrentStoryId.Value,
                    global_coherence = input.GlobalCoherence,
                    chunk_count = chunkCount,
                    message = "Global coherence finalized"
                });

                LastFunctionResult = result;
                LastResult = result;
                return result;
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "CoherenceCalculator", $"[CoherenceCalculator] FinalizeGlobalCoherence error: {ex.Message}");
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        public bool HasProcessedChunk(int chunkNumber) => _processedChunks.Contains(chunkNumber);

        public IReadOnlyCollection<int> ProcessedChunks => _processedChunks;

        public void Reset()
        {
            _processedChunks.Clear();
        }

        private class CalculateCoherenceInput
        {
            public int ChunkNumber { get; set; }
            public double? LocalCoherence { get; set; }
            public double? GlobalCoherence { get; set; }
            public string? Errors { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("chunk_number")]
            public int ChunkNumberAlt { set => ChunkNumber = value; }

            [System.Text.Json.Serialization.JsonPropertyName("local_coherence")]
            public double? LocalCoherenceAlt { set => LocalCoherence = value; }

            [System.Text.Json.Serialization.JsonPropertyName("global_coherence")]
            public double? GlobalCoherenceAlt { set => GlobalCoherence = value; }

            [System.Text.Json.Serialization.JsonPropertyName("errors")]
            public string? ErrorsAlt { set => Errors = value; }
        }

        private class GetChunkFactsInput
        {
            public int ChunkNumber { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("chunk_number")]
            public int ChunkNumberAlt { set => ChunkNumber = value; }
        }

        private class GetAllPreviousFactsInput
        {
            public int UpToChunk { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("up_to_chunk")]
            public int UpToChunkAlt { set => UpToChunk = value; }
        }

        private class FinalizeGlobalCoherenceInput
        {
            public double GlobalCoherence { get; set; }
            public string? Notes { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("global_coherence")]
            public double GlobalCoherenceAlt { set => GlobalCoherence = value; }

            [System.Text.Json.Serialization.JsonPropertyName("notes")]
            public string? NotesAlt { set => Notes = value; }
        }
    }
}
