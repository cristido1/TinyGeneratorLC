using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TinyGenerator.Services
{
    // Options for the TTS service; will use HOST/PORT environment values from Program.cs when registering
    public class TtsOptions
    {
        public string Host { get; set; } = "0.0.0.0";
        public int Port { get; set; } = 8004;
        public string BaseUrl => $"http://{Host}:{Port}";
        // Timeout in seconds for TTS HTTP requests (configurable via TTS_TIMEOUT_SECONDS env var)
        public int TimeoutSeconds { get; set; } = 300;
        public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
    }

    // Minimal DTOs matching the localTTS FastAPI responses
    public class VoiceInfo
    {
        // Primary fields from localTTS API
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("speaker")] public string? Speaker { get; set; }
        [JsonPropertyName("gender")] public string? Gender { get; set; }
        [JsonPropertyName("age_range")] public string? AgeRange { get; set; }
        [JsonPropertyName("archetype")] public string? Archetype { get; set; }
        [JsonPropertyName("rating")] public int? Rating { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
        
        // Id field: can be set explicitly, or computed from Speaker/Model
        private string? _id;
        [JsonPropertyName("id")]
        public string Id 
        { 
            get => !string.IsNullOrWhiteSpace(_id) ? _id 
                 : !string.IsNullOrWhiteSpace(Speaker) ? Speaker 
                 : (Model ?? string.Empty);
            set => _id = value;
        }
        
        // Name field: can be set explicitly, or computed from Id
        private string? _name;
        [JsonPropertyName("name")]
        public string Name
        {
            get => !string.IsNullOrWhiteSpace(_name) ? _name : Id;
            set => _name = value;
        }
        
        // Legacy/compatibility fields
        [JsonPropertyName("language")] public string? Language { get; set; }
        [JsonPropertyName("age")] public string? Age { get; set; }
        [JsonPropertyName("confidence")] public double? Confidence { get; set; }
        [JsonPropertyName("tags")] public Dictionary<string,string>? Tags { get; set; }
    }

    public class SynthesisResult
    {
        // Service may return an url or raw base64 audio, accept both
        [JsonPropertyName("audio_url")] public string? AudioUrl { get; set; }
        [JsonPropertyName("audio_base64")] public string? AudioBase64 { get; set; }
        [JsonPropertyName("duration_seconds")] public double? DurationSeconds { get; set; }
        [JsonPropertyName("sentiment")] public string? Sentiment { get; set; }
        [JsonPropertyName("meta")] public Dictionary<string,string>? Meta { get; set; }
    }

    public sealed class TtsService
    {
        private readonly HttpClient _http;
        private readonly TtsOptions _options;
        private readonly ICustomLogger? _logger;
        private static readonly JsonSerializerOptions VoiceJsonOptions = new() { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// Maximum characters per TTS request. XTTS has a 400 token limit (~250 chars for Italian).
        /// Using a conservative value to avoid truncation.
        /// </summary>
        private const int MaxCharsPerRequest = 220;

        public TtsService(HttpClient http, TtsOptions? options = null, ICustomLogger? logger = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _options = options ?? new TtsOptions();
            _logger = logger;
        }

        // GET /voices  => returns list of voices and evaluation fields
        public async Task<List<VoiceInfo>> GetVoicesAsync()
        {
            // try a few common paths used by FastAPI-based TTS services
            var candidates = new[] { 
                "/voices", 
                "/v1/voices", 
                "/api/voices" 
            };

            foreach (var path in candidates)
            {
                try
                {
                    var resp = await _http.GetAsync(path);
                    if (!resp.IsSuccessStatusCode) continue;
                    var payload = await resp.Content.ReadAsStringAsync();
                    var list = ParseVoicesPayload(payload);
                    if (list != null && list.Count > 0) return list;
                }
                catch
                {
                    // ignore and try next
                }
        }

        return new List<VoiceInfo>();
    }

    public async Task<byte[]> DownloadAudioAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("url is required", nameof(url));

        try
        {
            return await _http.GetByteArrayAsync(url);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TtsService] Failed to download audio from {url}: {ex}");
            throw;
        }
    }

        // POST /synthesize (body: { model, speaker, text, language?, emotion? }) -> returns SynthesisResult
        // If no language is provided, default to Italian ("it").
        // The voiceId parameter is the speaker name (e.g., "Dionisio Schuyler")
        // Automatically splits long texts into chunks to respect XTTS 400 token limit.
        public async Task<SynthesisResult?> SynthesizeAsync(string voiceId, string text, string? language = null, string? sentiment = null)
        {
            if (string.IsNullOrWhiteSpace(voiceId)) throw new ArgumentException("voiceId required", nameof(voiceId));
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text required", nameof(text));

            // If text is short enough, synthesize directly
            if (text.Length <= MaxCharsPerRequest)
            {
                return await SynthesizeSingleChunkAsync(voiceId, text, language, sentiment);
            }

            // Split long text into chunks and synthesize each
            Console.WriteLine($"[TtsService] Text too long ({text.Length} chars), splitting into chunks of max {MaxCharsPerRequest} chars");
            var chunks = SplitTextIntoChunks(text, MaxCharsPerRequest);
            Console.WriteLine($"[TtsService] Split into {chunks.Count} chunks");

            var audioSegments = new List<byte[]>();
            double totalDuration = 0;

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                Console.WriteLine($"[TtsService] Synthesizing chunk {i + 1}/{chunks.Count} ({chunk.Length} chars)");
                
                var result = await SynthesizeSingleChunkAsync(voiceId, chunk, language, sentiment);
                if (result == null)
                {
                    throw new InvalidOperationException($"TTS synthesis failed for chunk {i + 1}/{chunks.Count}");
                }

                byte[] audioBytes;
                if (!string.IsNullOrWhiteSpace(result.AudioBase64))
                {
                    audioBytes = Convert.FromBase64String(result.AudioBase64);
                }
                else if (!string.IsNullOrWhiteSpace(result.AudioUrl))
                {
                    audioBytes = await DownloadAudioAsync(result.AudioUrl);
                }
                else
                {
                    throw new InvalidOperationException($"TTS returned no audio for chunk {i + 1}/{chunks.Count}");
                }

                audioSegments.Add(audioBytes);
                if (result.DurationSeconds.HasValue)
                {
                    totalDuration += result.DurationSeconds.Value;
                }
            }

            // Concatenate all WAV segments
            var concatenated = ConcatenateWavFiles(audioSegments);
            return new SynthesisResult
            {
                AudioBase64 = Convert.ToBase64String(concatenated),
                DurationSeconds = totalDuration > 0 ? totalDuration : null
            };
        }

        /// <summary>
        /// Synthesizes a single chunk of text (must be within token limit).
        /// </summary>
        private async Task<SynthesisResult?> SynthesizeSingleChunkAsync(string voiceId, string text, string? language, string? sentiment)
        {
            // Parse voiceId: if it contains ":", split into model:speaker
            string model = "tts_models/multilingual/multi-dataset/xtts_v2";
            string speaker = voiceId;
            
            if (voiceId.Contains(":"))
            {
                var parts = voiceId.Split(':', 2);
                model = parts[0];
                speaker = parts[1];
            }
            
            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["speaker"] = speaker,
                ["text"] = text
            };
            // Ensure we always send a language; default to Italian if not specified
            if (string.IsNullOrWhiteSpace(language))
            {
                language = "it";
            }
            payload["language"] = language;
            // API uses "emotion" field, not "sentiment"
            if (!string.IsNullOrWhiteSpace(sentiment)) payload["emotion"] = sentiment;

            // try common endpoints
            var candidates = new[] { "/synthesize", "/v1/synthesize", "/api/synthesize" };

            foreach (var path in candidates)
            {
                try
                {
                    var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                    var fullUrl = $"{_options.BaseUrl}{path}";
                    
                    // Log request JSON completo
                    _logger?.LogRequestJson("TTS", $"POST {fullUrl}\n{payloadJson}");
                    Console.WriteLine($"[TtsService] POST {fullUrl}");
                    Console.WriteLine($"[TtsService] Request payload: {payloadJson}");

                    var resp = await _http.PostAsJsonAsync(path, payload);

                    var respText = "";
                    try
                    {
                        respText = await resp.Content.ReadAsStringAsync();
                    }
                    catch { /* ignore read errors */ }

                    // Log risultato response
                    if (resp.IsSuccessStatusCode)
                    {
                        _logger?.Log("INFO", "TTS", $"Request succeeded: {fullUrl} -> Status {(int)resp.StatusCode}, BodyLen={respText?.Length ?? 0}");
                        Console.WriteLine($"[TtsService] Response OK: Status {(int)resp.StatusCode}, BodyLen={respText?.Length ?? 0}");
                    }
                    else
                    {
                        var errorMsg = $"Request failed: {fullUrl} -> Status {(int)resp.StatusCode} {resp.StatusCode}";
                        if (!string.IsNullOrWhiteSpace(respText) && respText.Length < 1000)
                        {
                            errorMsg += $"\nError body: {respText}";
                        }
                        _logger?.Log("ERROR", "TTS", errorMsg);
                        Console.WriteLine($"[TtsService] Response ERROR: {errorMsg}");
                    }

                    if (resp.IsSuccessStatusCode)
                    {
                        // Handle JSON or raw audio responses
                        var media = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
                        if (media.StartsWith("application/json") || media.Contains("json"))
                        {
                            var result = await resp.Content.ReadFromJsonAsync<SynthesisResult>();
                            if (result != null) return result;
                            throw new InvalidOperationException("TTS response JSON vuoto/non interpretabile");
                        }

                        if (media.StartsWith("audio/") || media == "application/octet-stream" || string.IsNullOrEmpty(media))
                        {
                            // Treat body as raw audio bytes -> return base64
                            var bytes = await resp.Content.ReadAsByteArrayAsync();
                            var b64 = Convert.ToBase64String(bytes);
                            return new SynthesisResult { AudioBase64 = b64 };
                        }

                        throw new InvalidOperationException($"TTS risposta con media type non gestito: {media}");
                    }

                    // Only probe the next endpoint if the current one is clearly missing (404/405)
                    if (resp.StatusCode != System.Net.HttpStatusCode.NotFound &&
                        resp.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed)
                    {
                        throw new InvalidOperationException($"TTS endpoint {path} ha risposto {(int)resp.StatusCode} {resp.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    var fullUrl = $"{_options.BaseUrl}{path}";
                    _logger?.Log("ERROR", "TTS", $"Exception calling {fullUrl}: {ex.Message}", ex.ToString());
                    Console.WriteLine($"[TtsService] Exception posting to {fullUrl}: {ex.Message}");
                    if (ex is InvalidOperationException)
                    {
                        throw;
                    }
                }
            }

            return null;
        }

    private static List<VoiceInfo>? ParseVoicesPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<VoiceInfo>>(payload, VoiceJsonOptions);
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("voices", out var voicesElement))
                {
                    return JsonSerializer.Deserialize<List<VoiceInfo>>(voicesElement.GetRawText(), VoiceJsonOptions);
                }

                if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                {
                    return JsonSerializer.Deserialize<List<VoiceInfo>>(dataElement.GetRawText(), VoiceJsonOptions);
                }

                var list = new List<VoiceInfo>();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        var voice = prop.Value.Deserialize<VoiceInfo>(VoiceJsonOptions) ?? new VoiceInfo();
                        if (string.IsNullOrWhiteSpace(voice.Id))
                        {
                            voice.Id = prop.Name;
                        }
                        list.Add(voice);
                    }
                }

                if (list.Count > 0)
                    return list;
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }

        /// <summary>
        /// Splits text into chunks respecting sentence boundaries where possible.
        /// </summary>
        private static List<string> SplitTextIntoChunks(string text, int maxChars)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(text))
                return chunks;

            int start = 0;
            while (start < text.Length)
            {
                int remaining = text.Length - start;
                if (remaining <= maxChars)
                {
                    chunks.Add(text.Substring(start).Trim());
                    break;
                }

                // Try to find a sentence boundary within the chunk
                int end = start + maxChars;
                int boundaryIndex = -1;

                // Look backwards from end for sentence boundary
                for (int i = end - 1; i >= start + (maxChars / 2); i--)
                {
                    char c = text[i];
                    if (c == '.' || c == '!' || c == '?' || c == ';' || c == ':' || c == '\n')
                    {
                        boundaryIndex = i + 1;
                        break;
                    }
                }

                // If no sentence boundary, look for comma or space
                if (boundaryIndex < 0)
                {
                    for (int i = end - 1; i >= start + (maxChars / 2); i--)
                    {
                        char c = text[i];
                        if (c == ',' || c == ' ')
                        {
                            boundaryIndex = i + 1;
                            break;
                        }
                    }
                }

                // Fallback: hard cut at maxChars
                if (boundaryIndex < 0 || boundaryIndex <= start)
                {
                    boundaryIndex = end;
                }

                var chunk = text.Substring(start, boundaryIndex - start).Trim();
                if (!string.IsNullOrEmpty(chunk))
                {
                    chunks.Add(chunk);
                }
                start = boundaryIndex;

                // Skip leading whitespace for next chunk
                while (start < text.Length && char.IsWhiteSpace(text[start]))
                {
                    start++;
                }
            }

            return chunks;
        }

        /// <summary>
        /// Concatenates multiple WAV files into a single WAV file.
        /// Assumes all WAV files have the same format (sample rate, channels, bit depth).
        /// </summary>
        private static byte[] ConcatenateWavFiles(List<byte[]> wavFiles)
        {
            if (wavFiles == null || wavFiles.Count == 0)
                return Array.Empty<byte>();

            if (wavFiles.Count == 1)
                return wavFiles[0];

            // Parse first WAV to get format info
            using var firstStream = new MemoryStream(wavFiles[0]);
            using var firstReader = new BinaryReader(firstStream);

            // Read WAV header
            var riff = new string(firstReader.ReadChars(4)); // "RIFF"
            if (riff != "RIFF")
            {
                // Not a valid WAV, just concatenate raw bytes (fallback)
                Console.WriteLine("[TtsService] Warning: First audio is not WAV format, concatenating raw bytes");
                return wavFiles.SelectMany(w => w).ToArray();
            }

            firstReader.ReadInt32(); // File size
            var wave = new string(firstReader.ReadChars(4)); // "WAVE"
            if (wave != "WAVE")
            {
                return wavFiles.SelectMany(w => w).ToArray();
            }

            // Find fmt chunk
            short audioFormat = 1;
            short numChannels = 1;
            int sampleRate = 22050;
            int byteRate = 44100;
            short blockAlign = 2;
            short bitsPerSample = 16;

            while (firstStream.Position < firstStream.Length - 8)
            {
                var chunkId = new string(firstReader.ReadChars(4));
                var chunkSize = firstReader.ReadInt32();

                if (chunkId == "fmt ")
                {
                    audioFormat = firstReader.ReadInt16();
                    numChannels = firstReader.ReadInt16();
                    sampleRate = firstReader.ReadInt32();
                    byteRate = firstReader.ReadInt32();
                    blockAlign = firstReader.ReadInt16();
                    bitsPerSample = firstReader.ReadInt16();

                    // Skip any extra format bytes
                    if (chunkSize > 16)
                    {
                        firstReader.ReadBytes(chunkSize - 16);
                    }
                    break;
                }
                else
                {
                    // Skip unknown chunk
                    if (chunkSize > 0 && firstStream.Position + chunkSize <= firstStream.Length)
                    {
                        firstReader.ReadBytes(chunkSize);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            // Extract data from all WAV files
            var allData = new List<byte>();
            foreach (var wavFile in wavFiles)
            {
                var data = ExtractWavData(wavFile);
                allData.AddRange(data);
            }

            // Build new WAV file
            using var outputStream = new MemoryStream();
            using var writer = new BinaryWriter(outputStream);

            int dataSize = allData.Count;
            int fileSize = 36 + dataSize;

            // RIFF header
            writer.Write("RIFF".ToCharArray());
            writer.Write(fileSize);
            writer.Write("WAVE".ToCharArray());

            // fmt chunk
            writer.Write("fmt ".ToCharArray());
            writer.Write(16); // chunk size
            writer.Write(audioFormat);
            writer.Write(numChannels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);

            // data chunk
            writer.Write("data".ToCharArray());
            writer.Write(dataSize);
            writer.Write(allData.ToArray());

            return outputStream.ToArray();
        }

        /// <summary>
        /// Extracts raw audio data from a WAV file (skipping header).
        /// </summary>
        private static byte[] ExtractWavData(byte[] wavFile)
        {
            if (wavFile == null || wavFile.Length < 44)
                return wavFile ?? Array.Empty<byte>();

            using var stream = new MemoryStream(wavFile);
            using var reader = new BinaryReader(stream);

            // Check for RIFF header
            var riff = new string(reader.ReadChars(4));
            if (riff != "RIFF")
            {
                // Not a WAV file, return as-is
                return wavFile;
            }

            reader.ReadInt32(); // File size
            var wave = new string(reader.ReadChars(4));
            if (wave != "WAVE")
            {
                return wavFile;
            }

            // Find data chunk
            while (stream.Position < stream.Length - 8)
            {
                var chunkId = new string(reader.ReadChars(4));
                var chunkSize = reader.ReadInt32();

                if (chunkId == "data")
                {
                    // Found data chunk, read it
                    var bytesToRead = Math.Min(chunkSize, (int)(stream.Length - stream.Position));
                    return reader.ReadBytes(bytesToRead);
                }
                else
                {
                    // Skip this chunk
                    if (chunkSize > 0 && stream.Position + chunkSize <= stream.Length)
                    {
                        reader.ReadBytes(chunkSize);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            // No data chunk found, return everything after header
            return wavFile.Skip(44).ToArray();
        }
    }
}
