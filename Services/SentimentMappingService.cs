using System.Text.Json;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

/// <summary>
/// Servizio per mappare sentimenti liberi ai sentimenti supportati dal TTS.
/// Usa approccio ibrido: 1) mappature dirette, 2) cache DB, 3) embedding, 4) agente LLM.
/// </summary>
public class SentimentMappingService
{
    private readonly DatabaseService _db;
    private readonly ICustomLogger? _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Sentimenti supportati dal TTS
    /// </summary>
    public static readonly string[] SupportedSentiments =
    {
        "neutral", "happy", "sad", "angry", "fearful", "disgusted", "surprised"
    };

    /// <summary>
    /// Mappature dirette italiano → inglese TTS (hardcoded per velocità)
    /// </summary>
    private static readonly Dictionary<string, string> DirectMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        // Neutral
        { "neutrale", "neutral" }, { "normale", "neutral" }, { "calmo", "neutral" },
        { "sereno", "neutral" }, { "tranquillo", "neutral" }, { "pacato", "neutral" },
        { "indifferente", "neutral" }, { "impassibile", "neutral" },
        
        // Happy
        { "felice", "happy" }, { "contento", "happy" }, { "gioioso", "happy" },
        { "euforico", "happy" }, { "allegro", "happy" }, { "entusiasta", "happy" },
        { "eccitato", "happy" }, { "soddisfatto", "happy" }, { "raggiante", "happy" },
        { "esultante", "happy" }, { "trionfante", "happy" }, { "giubilante", "happy" },
        
        // Sad
        { "triste", "sad" }, { "malinconico", "sad" }, { "depresso", "sad" },
        { "abbattuto", "sad" }, { "sconsolato", "sad" }, { "afflitto", "sad" },
        { "dispiaciuto", "sad" }, { "addolorato", "sad" }, { "cupo", "sad" },
        { "desolato", "sad" }, { "mesto", "sad" }, { "affranto", "sad" },
        
        // Angry
        { "arrabbiato", "angry" }, { "furioso", "angry" }, { "irritato", "angry" },
        { "infuriato", "angry" }, { "adirato", "angry" }, { "indignato", "angry" },
        { "esasperato", "angry" }, { "stizzito", "angry" }, { "irato", "angry" },
        { "rabbioso", "angry" }, { "furibondo", "angry" }, { "inviperito", "angry" },
        
        // Fearful
        { "spaventato", "fearful" }, { "terrorizzato", "fearful" }, { "impaurito", "fearful" },
        { "timoroso", "fearful" }, { "ansioso", "fearful" }, { "preoccupato", "fearful" },
        { "angosciato", "fearful" }, { "teso", "fearful" }, { "nervoso", "fearful" },
        { "atterrito", "fearful" }, { "intimorito", "fearful" }, { "allarmato", "fearful" },
        
        // Disgusted
        { "disgustato", "disgusted" }, { "nauseato", "disgusted" }, { "schifato", "disgusted" },
        { "ripugnato", "disgusted" }, { "inorridito", "disgusted" }, { "rivoltato", "disgusted" },
        
        // Surprised
        { "sorpreso", "surprised" }, { "stupito", "surprised" }, { "meravigliato", "surprised" },
        { "sbalordito", "surprised" }, { "scioccato", "surprised" }, { "incredulo", "surprised" },
        { "attonito", "surprised" }, { "allibito", "surprised" }, { "basito", "surprised" },
        { "sbigottito", "surprised" }, { "esterrefatto", "surprised" }
    };

    // Cache in memoria degli embedding dei 7 sentimenti destinazione
    private Dictionary<string, float[]>? _destEmbeddingsCache;
    private readonly SemaphoreSlim _embeddingLock = new(1, 1);
    private bool _embeddingsInitialized;

    public SentimentMappingService(
        DatabaseService db,
        ICustomLogger? logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Inizializza cache embedding dei 7 sentimenti destinazione (chiamato una volta)
    /// </summary>
    public async Task InitializeEmbeddingsAsync(CancellationToken ct = default)
    {
        if (_embeddingsInitialized) return;

        await _embeddingLock.WaitAsync(ct);
        try
        {
            if (_embeddingsInitialized) return;

            _destEmbeddingsCache = new Dictionary<string, float[]>();

            // Prova a caricare dal DB
            var cached = _db.GetAllSentimentEmbeddings();
            if (cached.Count == SupportedSentiments.Length)
            {
                foreach (var se in cached)
                {
                    try
                    {
                        var embedding = JsonSerializer.Deserialize<float[]>(se.Embedding);
                        if (embedding != null)
                            _destEmbeddingsCache[se.Sentiment] = embedding;
                    }
                    catch { /* ignore malformed */ }
                }
                
                if (_destEmbeddingsCache.Count == SupportedSentiments.Length)
                {
                    _logger?.Log("Info", "SentimentMapping",
                        $"Caricati {_destEmbeddingsCache.Count} embedding sentimenti da cache DB");
                    _embeddingsInitialized = true;
                    return;
                }
            }

            // Calcola e salva gli embedding
            _logger?.Log("Info", "SentimentMapping", "Calcolo embedding sentimenti destinazione...");

            var embeddingModel = _configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text:latest";
            var ollamaEndpoint = _configuration["Ollama:Endpoint"] ?? "http://localhost:11434";

            foreach (var sentiment in SupportedSentiments)
            {
                if (_destEmbeddingsCache.ContainsKey(sentiment)) continue;
                
                var embedding = await GetEmbeddingFromOllamaAsync(sentiment, embeddingModel, ollamaEndpoint, ct);
                if (embedding != null)
                {
                    _destEmbeddingsCache[sentiment] = embedding;
                    _db.UpsertSentimentEmbedding(new SentimentEmbedding
                    {
                        Sentiment = sentiment,
                        Embedding = JsonSerializer.Serialize(embedding),
                        Model = embeddingModel,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            _logger?.Log("Info", "SentimentMapping",
                $"Calcolati e salvati {_destEmbeddingsCache.Count} embedding sentimenti");
            _embeddingsInitialized = true;
        }
        catch (Exception ex)
        {
            _logger?.Log("Warning", "SentimentMapping", $"Errore inizializzazione embedding: {ex.Message}");
            _embeddingsInitialized = true; // Procedi comunque, userà agent come fallback
        }
        finally
        {
            _embeddingLock.Release();
        }
    }

    /// <summary>
    /// Mappa un sentimento sorgente al sentimento TTS corrispondente
    /// </summary>
    public async Task<string> MapSentimentAsync(string sourceSentiment, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceSentiment))
            return "neutral";

        var normalized = sourceSentiment.Trim().ToLowerInvariant();

        // 1. Già supportato?
        if (SupportedSentiments.Contains(normalized))
            return normalized;

        // 2. Cache DB
        var cached = _db.GetMappedSentiment(normalized);
        if (cached != null)
            return cached.DestSentiment;

        // 3. Mappatura diretta hardcoded
        if (DirectMappings.TryGetValue(normalized, out var direct))
        {
            SaveMapping(normalized, direct, "direct", 1.0f);
            return direct;
        }

        // 4. Prova con embedding
        var (embeddingResult, confidence) = await TryMapWithEmbeddingAsync(normalized, ct);
        if (embeddingResult != null && confidence >= 0.7f)
        {
            SaveMapping(normalized, embeddingResult, "embedding", confidence);
            _logger?.Log("Info", "SentimentMapping",
                $"Mappato '{normalized}' → '{embeddingResult}' via embedding (conf: {confidence:F2})");
            return embeddingResult;
        }

        // 5. Fallback: agente LLM
        var agentResult = await TryMapWithAgentAsync(normalized, ct);
        if (agentResult != null)
        {
            SaveMapping(normalized, agentResult, "agent", 0.9f);
            _logger?.Log("Info", "SentimentMapping",
                $"Mappato '{normalized}' → '{agentResult}' via agente");
            return agentResult;
        }

        // 6. Default a neutral
        SaveMapping(normalized, "neutral", "default", 0.5f);
        _logger?.Log("Warning", "SentimentMapping",
            $"Sentimento '{normalized}' non mappato, default a 'neutral'");
        return "neutral";
    }

    /// <summary>
    /// Normalizza tutti i sentimenti in uno schema TTS
    /// </summary>
    public async Task<(int normalized, int total)> NormalizeTtsSchemaAsync(
        TtsSchema schema, CancellationToken ct = default)
    {
        await InitializeEmbeddingsAsync(ct);

        int normalized = 0;
        int total = 0;

        // Dobbiamo gestire Timeline che contiene oggetti misti (TtsPhrase, TtsPause, JsonElement)
        for (int i = 0; i < schema.Timeline.Count; i++)
        {
            var item = schema.Timeline[i];
            TtsPhrase? phrase = null;
            
            if (item is TtsPhrase p)
            {
                phrase = p;
            }
            else if (item is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                // Check if it's a phrase (has "character" property)
                if (je.TryGetProperty("character", out _) || je.TryGetProperty("Character", out _))
                {
                    try
                    {
                        phrase = JsonSerializer.Deserialize<TtsPhrase>(je.GetRawText(), new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                        if (phrase != null)
                        {
                            schema.Timeline[i] = phrase; // Replace JsonElement with proper object
                        }
                    }
                    catch { }
                }
            }

            if (phrase == null) continue;

            if (string.IsNullOrEmpty(phrase.Emotion))
            {
                phrase.Emotion = "neutral";
                continue;
            }

            total++;
            var original = phrase.Emotion.ToLowerInvariant();

            if (!SupportedSentiments.Contains(original))
            {
                phrase.Emotion = await MapSentimentAsync(original, ct);
                normalized++;
            }
        }

        return (normalized, total);
    }

    private async Task<(string? sentiment, float confidence)> TryMapWithEmbeddingAsync(
        string sourceSentiment, CancellationToken ct)
    {
        try
        {
            await InitializeEmbeddingsAsync(ct);

            if (_destEmbeddingsCache == null || _destEmbeddingsCache.Count == 0)
                return (null, 0);

            var embeddingModel = _configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text:latest";
            var ollamaEndpoint = _configuration["Ollama:Endpoint"] ?? "http://localhost:11434";

            var sourceEmbedding = await GetEmbeddingFromOllamaAsync(
                sourceSentiment, embeddingModel, ollamaEndpoint, ct);

            if (sourceEmbedding == null)
                return (null, 0);

            string? bestMatch = null;
            float bestSimilarity = 0;

            foreach (var (destSentiment, destEmbedding) in _destEmbeddingsCache)
            {
                var similarity = CosineSimilarity(sourceEmbedding, destEmbedding);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestMatch = destSentiment;
                }
            }

            return (bestMatch, bestSimilarity);
        }
        catch (Exception ex)
        {
            _logger?.Log("Warning", "SentimentMapping",
                $"Embedding fallito per '{sourceSentiment}': {ex.Message}");
            return (null, 0);
        }
    }

    private async Task<string?> TryMapWithAgentAsync(string sourceSentiment, CancellationToken ct)
    {
        try
        {
            // Cerca l'agente SentimentMapper dal DB
            var agent = _db.GetAgentByName("SentimentMapper");
            var modelName = agent?.ModelId != null 
                ? _db.GetModelInfoById(agent.ModelId.Value)?.Name 
                : null;
            
            if (string.IsNullOrWhiteSpace(modelName))
            {
                modelName = "gemma2:2b"; // fallback
            }

            var ollamaEndpoint = _configuration["Ollama:Endpoint"] ?? "http://localhost:11434";

            var prompt = agent?.Prompt?.Replace("{{sentiment}}", sourceSentiment)
                ?? $@"Mappa il sentimento '{sourceSentiment}' a UNO solo di questi valori:
neutral, happy, sad, angry, fearful, disgusted, surprised

Rispondi SOLO con la parola inglese, senza spiegazioni.";

            var client = _httpClientFactory.CreateClient();
            var request = new
            {
                model = modelName,
                prompt = prompt,
                stream = false
            };

            var response = await client.PostAsJsonAsync($"{ollamaEndpoint}/api/generate", request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var result = json.TryGetProperty("response", out var respProp) 
                ? respProp.GetString()?.Trim().ToLowerInvariant() 
                : null;

            if (!string.IsNullOrEmpty(result) && SupportedSentiments.Contains(result))
                return result;

            // Prova a estrarre dalla risposta
            foreach (var s in SupportedSentiments)
            {
                if (result?.Contains(s, StringComparison.OrdinalIgnoreCase) == true)
                    return s;
            }

            return TryFallbackMapping(sourceSentiment);
        }
        catch (Exception ex)
        {
            _logger?.Log("Warning", "SentimentMapping",
                $"Agente fallito per '{sourceSentiment}': {ex.Message}");
            return TryFallbackMapping(sourceSentiment);
        }
    }

    /// <summary>
    /// Pattern matching semplice come ultima risorsa
    /// </summary>
    private static string? TryFallbackMapping(string sentiment)
    {
        var s = sentiment.ToLowerInvariant();
        if (s.Contains("rabbia") || s.Contains("furi") || s.Contains("ira")) return "angry";
        if (s.Contains("paur") || s.Contains("terror") || s.Contains("ansi")) return "fearful";
        if (s.Contains("trist") || s.Contains("dolor") || s.Contains("piant")) return "sad";
        if (s.Contains("felic") || s.Contains("gioi") || s.Contains("content")) return "happy";
        if (s.Contains("sorpres") || s.Contains("stupi") || s.Contains("shock")) return "surprised";
        if (s.Contains("disgust") || s.Contains("schif") || s.Contains("nause")) return "disgusted";
        return null;
    }

    private async Task<float[]?> GetEmbeddingFromOllamaAsync(
        string text, string model, string endpoint, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var request = new { model, prompt = text };
            var response = await client.PostAsJsonAsync($"{endpoint}/api/embeddings", request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (json.TryGetProperty("embedding", out var embeddingProp))
            {
                return embeddingProp.EnumerateArray()
                    .Select(e => e.GetSingle())
                    .ToArray();
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Log("Warning", "SentimentMapping", $"Errore embedding: {ex.Message}");
            return null;
        }
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return (normA == 0 || normB == 0) ? 0 : dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    private void SaveMapping(string source, string dest, string sourceType, float confidence)
    {
        _db.InsertMappedSentiment(new MappedSentiment
        {
            SourceSentiment = source,
            DestSentiment = dest,
            SourceType = sourceType,
            Confidence = confidence,
            CreatedAt = DateTime.UtcNow
        });
    }
}
