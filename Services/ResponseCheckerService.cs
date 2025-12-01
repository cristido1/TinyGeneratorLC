using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    public class ResponseCheckerService
    {
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly DatabaseService _database;
        private readonly ICustomLogger _logger;
        private readonly HttpClient _httpClient;

        public ResponseCheckerService(
            ILangChainKernelFactory kernelFactory,
            DatabaseService database,
            ICustomLogger logger,
            IHttpClientFactory httpClientFactory)
        {
            _kernelFactory = kernelFactory;
            _database = database;
            _logger = logger;
            _httpClient = httpClientFactory.CreateClient();
        }

        public async Task<ValidationResult> ValidateStepOutputAsync(
            string stepInstruction,
            string modelOutput,
            Dictionary<string, object>? validationCriteria,
            int threadId,
            string? agentName = null,
            string? modelName = null)
        {
            _logger.Log("Information", "MultiStep", $"Starting step validation - Agent: {agentName}, Model: {modelName}");

            // Calculate semantic alignment (best-effort)
            double? semanticScore = null;
            try
            {
                semanticScore = await CalculateSemanticAlignmentAsync(stepInstruction, modelOutput);
            }
            catch (Exception ex)
            {
                _logger.Log("Warning", "MultiStep", $"Semantic alignment calculation failed: {ex.Message}");
            }

            // Check semantic threshold first (if available)
            if (semanticScore.HasValue && validationCriteria != null && validationCriteria.ContainsKey("semantic_threshold"))
            {
                var threshold = Convert.ToDouble(validationCriteria["semantic_threshold"]);
                if (semanticScore.Value < threshold)
                {
                    _logger.Log("Warning", "MultiStep", $"Semantic score {semanticScore.Value:F2} below threshold {threshold:F2}, validation failed");
                    return new ValidationResult
                    {
                        IsValid = false,
                        Reason = $"Semantic alignment score ({semanticScore.Value:F2}) is below required threshold ({threshold:F2}). Output does not sufficiently match the step instruction.",
                        NeedsRetry = true,
                        SemanticScore = semanticScore
                    };
                }
            }

            // Get checker agent
            var checkerAgent = _database.ListAgents()
                .FirstOrDefault(a => a.Role == "response_checker" && a.IsActive);

            if (checkerAgent == null)
            {
                _logger.Log("Warning", "MultiStep", "No active response_checker agent found, skipping validation");
                // Fallback: consider valid if no checker available
                return new ValidationResult
                {
                    IsValid = true,
                    Reason = "No checker agent available",
                    NeedsRetry = false,
                    SemanticScore = semanticScore
                };
            }

            // Build validation prompt
            var criteriaJson = validationCriteria != null ? JsonSerializer.Serialize(validationCriteria) : "{}";
            var semanticInfo = semanticScore.HasValue ? $"\n\nSemantic Alignment Score: {semanticScore.Value:F2} (0-1 scale)" : "";

            var prompt = $@"You are a Response Checker. Validate if the writer's output meets the requirements.

**Step Instruction:**
{stepInstruction}

**Writer Output:**
{modelOutput}

**Validation Criteria:**
{criteriaJson}{semanticInfo}

**Your Task:**
Check if the output:
1. Adheres to the step requirements
2. Contains no questions or requests for clarification
3. Does not anticipate future steps
4. Is complete (not truncated)
5. Meets minimum length if specified

Return ONLY a JSON object with this structure:
{{
  ""is_valid"": true or false,
  ""reason"": ""brief explanation"",
  ""needs_retry"": true or false
}}";

            _logger.Log("Information", "MultiStep", $"Invoking checker agent: {checkerAgent.Name}");

            // Invoke checker agent via ReActLoop
            try
            {
                var modelInfo = _database.GetModelInfoById(checkerAgent.ModelId ?? 0);
                var checkerModelName = modelInfo?.Name ?? "phi3:mini";
                
                var orchestrator = _kernelFactory.CreateOrchestrator(checkerModelName, new List<string>());
                var bridge = _kernelFactory.CreateChatBridge(checkerModelName);
                var loop = new ReActLoopOrchestrator(orchestrator, _logger, maxIterations: 5, modelBridge: bridge);

                var result = await loop.ExecuteAsync(prompt);

                _logger.Log("Information", "MultiStep", $"Checker raw response: {result.FinalResponse}");

                // Parse JSON response
                var validationResult = ParseValidationResponse(result.FinalResponse, semanticScore);
                _logger.Log("Information", "MultiStep", $"Validation result: is_valid={validationResult.IsValid}, reason={validationResult.Reason}");

                return validationResult;
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "MultiStep", $"Checker agent invocation failed: {ex.Message}", ex.ToString());
                // Fallback: consider invalid and needs retry
                return new ValidationResult
                {
                    IsValid = false,
                    Reason = $"Validation error: {ex.Message}",
                    NeedsRetry = true,
                    SemanticScore = semanticScore
                };
            }
        }

        private ValidationResult ParseValidationResponse(string response, double? semanticScore)
        {
            try
            {
                // First, try to extract from Ollama response format (if present)
                if (response.Contains("\"message\"") && response.Contains("\"content\""))
                {
                    try
                    {
                        var ollamaDoc = JsonDocument.Parse(response);
                        if (ollamaDoc.RootElement.TryGetProperty("message", out var message) &&
                            message.TryGetProperty("content", out var content))
                        {
                            response = content.GetString() ?? response;
                        }
                    }
                    catch
                    {
                        // Continue with original response
                    }
                }

                // Try to extract JSON from response
                var jsonStart = response.IndexOf('{');
                var jsonEnd = response.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    
                    // Parse with JsonDocument for flexible property name handling
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    
                    // Support multiple property name formats: IsValid, is_valid, isValid, valid
                    var validFlag = false;
                    string? validReason = null;
                    bool? retryFlag = null;

                    if (root.TryGetProperty("is_valid", out var isValidProp))
                        validFlag = isValidProp.GetBoolean();
                    else if (root.TryGetProperty("IsValid", out isValidProp))
                        validFlag = isValidProp.GetBoolean();
                    else if (root.TryGetProperty("isValid", out isValidProp))
                        validFlag = isValidProp.GetBoolean();
                    else if (root.TryGetProperty("valid", out isValidProp))
                        validFlag = isValidProp.GetBoolean();

                    if (root.TryGetProperty("reason", out var reasonProp))
                        validReason = reasonProp.GetString();
                    else if (root.TryGetProperty("Reason", out reasonProp))
                        validReason = reasonProp.GetString();

                    if (root.TryGetProperty("needs_retry", out var retryProp))
                        retryFlag = retryProp.GetBoolean();
                    else if (root.TryGetProperty("NeedsRetry", out retryProp))
                        retryFlag = retryProp.GetBoolean();
                    else if (root.TryGetProperty("needsRetry", out retryProp))
                        retryFlag = retryProp.GetBoolean();

                    return new ValidationResult
                    {
                        IsValid = validFlag,
                        Reason = validReason ?? "Validation completed",
                        NeedsRetry = retryFlag ?? !validFlag,
                        SemanticScore = semanticScore
                    };
                }

                // Fallback: try to parse natural language response
                // Look for keywords like "valid", "invalid", "meets", "fails", etc.
                var lowerResponse = response.ToLowerInvariant();
                
                bool isValid = false;
                string reason = response;

                // Check for positive indicators
                if (lowerResponse.Contains("is valid") || 
                    lowerResponse.Contains("valido") ||
                    lowerResponse.Contains("meets all") || 
                    lowerResponse.Contains("soddisfa") ||
                    lowerResponse.Contains("rispetta"))
                {
                    isValid = true;
                    reason = "Output appears valid based on natural language response";
                }
                // Check for negative indicators
                else if (lowerResponse.Contains("invalid") || 
                         lowerResponse.Contains("non valido") ||
                         lowerResponse.Contains("does not meet") || 
                         lowerResponse.Contains("non soddisfa") ||
                         lowerResponse.Contains("manca") ||
                         lowerResponse.Contains("missing"))
                {
                    isValid = false;
                    // Try to extract the reason from the response
                    var sentences = response.Split('.', StringSplitOptions.RemoveEmptyEntries);
                    reason = sentences.Length > 0 ? sentences[0].Trim() : response;
                }

                return new ValidationResult
                {
                    IsValid = isValid,
                    Reason = reason,
                    NeedsRetry = !isValid,
                    SemanticScore = semanticScore
                };
            }
            catch
            {
                // Fall through to default
            }

            // Fallback: parse failed, assume needs retry
            return new ValidationResult
            {
                IsValid = false,
                Reason = "Could not parse validation response",
                NeedsRetry = true,
                SemanticScore = semanticScore
            };
        }

        public async Task<double?> CalculateSemanticAlignmentAsync(string text1, string text2)
        {
            try
            {
                // Call Ollama embeddings API for both texts
                var embedding1 = await GetEmbeddingAsync(text1);
                var embedding2 = await GetEmbeddingAsync(text2);

                if (embedding1 == null || embedding2 == null) return null;

                // Calculate cosine similarity
                return CosineSimilarity(embedding1, embedding2);
            }
            catch (Exception)
            {
                return null; // Best-effort: return null on error
            }
        }

        private async Task<float[]?> GetEmbeddingAsync(string text)
        {
            try
            {
                var payload = new
                {
                    model = "nomic-embed-text",
                    prompt = text
                };

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("http://localhost:11434/api/embeddings", content);

                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("embedding", out var embeddingProp))
                {
                    var embedding = new List<float>();
                    foreach (var element in embeddingProp.EnumerateArray())
                    {
                        embedding.Add((float)element.GetDouble());
                    }
                    return embedding.ToArray();
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private double CosineSimilarity(float[] vec1, float[] vec2)
        {
            if (vec1.Length != vec2.Length) return 0;

            double dotProduct = 0;
            double magnitude1 = 0;
            double magnitude2 = 0;

            for (int i = 0; i < vec1.Length; i++)
            {
                dotProduct += vec1[i] * vec2[i];
                magnitude1 += vec1[i] * vec1[i];
                magnitude2 += vec2[i] * vec2[i];
            }

            magnitude1 = Math.Sqrt(magnitude1);
            magnitude2 = Math.Sqrt(magnitude2);

            if (magnitude1 == 0 || magnitude2 == 0) return 0;

            return dotProduct / (magnitude1 * magnitude2);
        }
    }
}
