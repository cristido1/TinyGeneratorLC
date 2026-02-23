using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed class SoundScoringService
{
    private readonly DatabaseService _database;
    private readonly SoundScoringOptions _options;
    private readonly ILogger<SoundScoringService>? _logger;

    public SoundScoringService(
        DatabaseService database,
        IOptions<SoundScoringOptions>? options = null,
        ILogger<SoundScoringService>? logger = null)
    {
        _database = database;
        _options = options?.Value ?? new SoundScoringOptions();
        _logger = logger;
    }

    public sealed record BatchResult(int Processed, int Updated, int Failed, List<string> Errors);
    public sealed record DurationBatchResult(int Processed, int Updated, int Failed, List<string> Errors);

    public double GetWavDurationSeconds(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("FilePath vuoto");
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File WAV non trovato", filePath);
        }

        var wav = ReadWav(filePath);
        return Math.Round(Math.Max(0d, wav.DurationSeconds), 3);
    }

    public DurationBatchResult BackfillMissingDurations(int? limit = null)
    {
        var sounds = _database.ListSoundsMissingDuration(limit);
        var errors = new List<string>();
        var updated = 0;
        var failed = 0;

        foreach (var sound in sounds)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var durationSec = GetWavDurationSeconds(sound.FilePath);
                _database.UpdateSoundDurationSeconds(sound.Id, durationSec);
                sw.Stop();
                _logger?.LogInformation(
                    "Sound duration backfill soundId={SoundId} file={FileName} durationSec={DurationSec:0.###} elapsedMs={ElapsedMs}",
                    sound.Id,
                    sound.FileName ?? string.Empty,
                    durationSec,
                    sw.ElapsedMilliseconds);
                updated++;
            }
            catch (Exception ex)
            {
                sw.Stop();
                failed++;
                var msg = $"sound {sound.Id} ({sound.FileName}): {ex.Message}";
                errors.Add(msg);
                _logger?.LogWarning(ex, "Sound duration backfill failed for sound {SoundId}", sound.Id);
            }
        }

        return new DurationBatchResult(sounds.Count, updated, failed, errors);
    }

    public BatchResult RecalculateScores(bool onlyMissingFinal = false, int? limit = null)
    {
        var sounds = _database.ListSoundsForScoring(onlyMissingFinal, limit);
        var errors = new List<string>();
        var updated = 0;
        var failed = 0;

        foreach (var sound in sounds)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var score = ComputeScores(sound);
                _database.UpdateSoundScores(
                    sound.Id,
                    score.ScoreLoudness,
                    score.ScoreDynamic,
                    score.ScoreClipping,
                    score.ScoreNoise,
                    score.ScoreDuration,
                    score.ScoreFormat,
                    score.ScoreConsistency,
                    score.ScoreTagMatch,
                    score.ScoreFinal,
                    DateTime.UtcNow.ToString("o"),
                    _options.ScoreVersion);
                sw.Stop();
                LogPerSoundScore(sound, score, sw.ElapsedMilliseconds);
                updated++;
            }
            catch (Exception ex)
            {
                sw.Stop();
                failed++;
                var msg = $"sound {sound.Id} ({sound.FileName}): {ex.Message}";
                errors.Add(msg);
                _logger?.LogWarning(ex, "Sound scoring failed for sound {SoundId}", sound.Id);
            }
        }

        return new BatchResult(sounds.Count, updated, failed, errors);
    }

    public void RecalculateScoreForSound(int soundId)
    {
        var sound = _database.GetSoundById(soundId) ?? throw new InvalidOperationException($"Sound not found: {soundId}");
        var sw = Stopwatch.StartNew();
        var score = ComputeScores(sound);
        _database.UpdateSoundScores(
            sound.Id,
            score.ScoreLoudness,
            score.ScoreDynamic,
            score.ScoreClipping,
            score.ScoreNoise,
            score.ScoreDuration,
            score.ScoreFormat,
            score.ScoreConsistency,
            score.ScoreTagMatch,
            score.ScoreFinal,
            DateTime.UtcNow.ToString("o"),
            _options.ScoreVersion);
        sw.Stop();
        LogPerSoundScore(sound, score, sw.ElapsedMilliseconds);
    }

    private SoundScoreResult ComputeScores(Sound sound)
    {
        if (sound == null) throw new ArgumentNullException(nameof(sound));
        if (string.IsNullOrWhiteSpace(sound.FilePath)) throw new InvalidOperationException("FilePath vuoto");
        if (!File.Exists(sound.FilePath)) throw new FileNotFoundException("File WAV non trovato", sound.FilePath);

        var wav = ReadWav(sound.FilePath);
        if (wav.Samples.Length == 0) throw new InvalidOperationException("Nessun sample audio letto");

        var scoreLoudness = ScoreLoudness(wav);
        var scoreDynamic = ScoreDynamic(wav);
        var scoreClipping = ScoreClipping(wav);
        var scoreNoise = ScoreNoise(wav);
        var scoreDuration = ScoreDuration(sound, wav.DurationSeconds);
        var scoreFormat = ScoreFormat(wav);
        var scoreConsistency = ScoreConsistency(wav);
        var scoreTagMatch = ScoreTagMatch(sound);

        var final = WeightedFinal(
            scoreLoudness, scoreDynamic, scoreClipping, scoreNoise, scoreDuration,
            scoreFormat, scoreConsistency, scoreTagMatch, sound.ScoreHuman);

        return new SoundScoreResult(
            scoreLoudness, scoreDynamic, scoreClipping, scoreNoise, scoreDuration,
            scoreFormat, scoreConsistency, scoreTagMatch, final, wav.DurationSeconds);
    }

    private void LogPerSoundScore(Sound sound, SoundScoreResult score, long elapsedMs)
    {
        if (!_options.LogPerSoundScoreDetailsAndDuration)
        {
            return;
        }

        _logger?.LogInformation(
            "SoundScoring detail soundId={SoundId} file={FileName} type={Type} elapsedMs={ElapsedMs} audioSec={AudioSec:0.###} " +
            "loud={Loud:0.##} dyn={Dyn:0.##} clip={Clip:0.##} noise={Noise:0.##} dur={Dur:0.##} fmt={Fmt:0.##} cons={Cons:0.##} tag={Tag:0.##} human={Human:0.##} final={Final:0.##} version={Version}",
            sound.Id,
            sound.FileName ?? string.Empty,
            sound.Type ?? string.Empty,
            elapsedMs,
            score.AudioDurationSeconds,
            score.ScoreLoudness ?? 0,
            score.ScoreDynamic ?? 0,
            score.ScoreClipping ?? 0,
            score.ScoreNoise ?? 0,
            score.ScoreDuration ?? 0,
            score.ScoreFormat ?? 0,
            score.ScoreConsistency ?? 0,
            score.ScoreTagMatch ?? 0,
            sound.ScoreHuman ?? 0,
            score.ScoreFinal ?? 0,
            _options.ScoreVersion);
    }

    private double? ScoreLoudness(WavData wav)
    {
        if (wav.Rms <= 0) return 0;
        var rmsDb = 20.0 * Math.Log10(wav.Rms);
        var diff = Math.Abs(rmsDb - _options.TargetRmsDbFs);
        return ScoreFromDistance(diff, _options.LoudnessToleranceDb);
    }

    private double? ScoreDynamic(WavData wav)
    {
        if (wav.Rms <= 0 || wav.Peak <= 0) return 0;
        var crestDb = 20.0 * Math.Log10(Math.Max(wav.Peak, 1e-9) / Math.Max(wav.Rms, 1e-9));
        var diff = Math.Abs(crestDb - _options.DynamicTargetDb);
        return ScoreFromDistance(diff, _options.DynamicToleranceDb);
    }

    private double? ScoreClipping(WavData wav)
    {
        if (wav.SampleCount <= 0) return 0;
        var ratio = (double)wav.ClippedSamples / wav.SampleCount;
        if (ratio <= 0) return 100;
        if (ratio >= _options.ClippingFailRatio) return 0;
        return Clamp01(1.0 - (ratio / _options.ClippingFailRatio)) * 100.0;
    }

    private double? ScoreNoise(WavData wav)
    {
        if (wav.WindowRms.Count == 0) return 0;
        var sorted = wav.WindowRms.OrderBy(x => x).ToList();
        var take = Math.Max(1, sorted.Count / 10);
        var quietAvg = sorted.Take(take).Average();
        if (quietAvg <= 0) return 100;
        var db = 20.0 * Math.Log10(quietAvg);
        if (db <= _options.NoiseFloorGoodDbFs) return 100;
        if (db >= _options.NoiseFloorBadDbFs) return 0;
        var t = (db - _options.NoiseFloorGoodDbFs) / (_options.NoiseFloorBadDbFs - _options.NoiseFloorGoodDbFs);
        return (1.0 - Clamp01(t)) * 100.0;
    }

    private static double? ScoreDuration(Sound sound, double durationSec)
    {
        if (durationSec <= 0) return 0;

        var t = (sound.Type ?? string.Empty).Trim().ToLowerInvariant();
        (double min, double max, bool oneSidedMin) range = t switch
        {
            "amb" => (5.0, 1800.0, true),
            "music" => (3.0, 600.0, false),
            _ => (0.1, 8.0, false)
        };

        if (range.oneSidedMin)
        {
            if (durationSec >= range.min) return 100;
            var frac = durationSec / Math.Max(range.min, 0.001);
            return Clamp01(frac) * 100.0;
        }

        if (durationSec >= range.min && durationSec <= range.max) return 100;
        if (durationSec < range.min)
        {
            return Clamp01(durationSec / Math.Max(range.min, 0.001)) * 100.0;
        }

        var over = durationSec - range.max;
        var tol = Math.Max(range.max * 1.5, 1.0);
        return (1.0 - Clamp01(over / tol)) * 100.0;
    }

    private static double? ScoreFormat(WavData wav)
    {
        var score = 0.0;
        // WAV already guaranteed by source table usage, but keep score component explicit.
        score += 40;
        score += wav.SampleRate >= 48000 ? 35 : wav.SampleRate >= 44100 ? 25 : 10;
        score += wav.BitsPerSample >= 24 ? 25 : wav.BitsPerSample >= 16 ? 18 : 8;
        return Math.Min(100.0, score);
    }

    private static double? ScoreConsistency(WavData wav)
    {
        if (wav.WindowRms.Count < 2) return 50;
        var avg = wav.WindowRms.Average();
        if (avg <= 1e-9) return 50;
        var variance = wav.WindowRms.Select(v => (v - avg) * (v - avg)).Average();
        var std = Math.Sqrt(Math.Max(0, variance));
        var cv = std / avg; // high variance => less consistency
        // Penalize extremes, but not too harsh for FX.
        return (1.0 - Clamp01(cv / 1.5)) * 100.0;
    }

    private static double? ScoreTagMatch(Sound sound)
    {
        var type = (sound.Type ?? string.Empty).Trim().ToLowerInvariant();
        var text = $"{sound.Library} {sound.FileName} {sound.Description} {sound.Tags}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var score = 40.0; // base if metadata exists
        if (!string.IsNullOrWhiteSpace(sound.Tags)) score += 20;
        if (!string.IsNullOrWhiteSpace(sound.Description)) score += 10;

        string[] ambWords = ["amb", "ambient", "ambience", "rain", "wind", "water", "roomtone", "walla", "forest", "city"];
        string[] musicWords = ["music", "theme", "cue", "melody", "bass", "arp", "score", "soundtrack"];
        string[] fxWords = ["fx", "impact", "hit", "shot", "click", "door", "weapon", "ui", "transition", "glitch"];

        var words = type switch
        {
            "amb" => ambWords,
            "music" => musicWords,
            _ => fxWords
        };

        var hits = words.Count(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        score += Math.Min(30, hits * 8);

        // Small penalty if metadata clearly suggests another category.
        if (type == "amb" && musicWords.Any(w => text.Contains(w))) score -= 10;
        if (type == "music" && ambWords.Any(w => text.Contains(w))) score -= 10;

        return Math.Clamp(score, 0, 100);
    }

    private double? WeightedFinal(
        double? scoreLoudness,
        double? scoreDynamic,
        double? scoreClipping,
        double? scoreNoise,
        double? scoreDuration,
        double? scoreFormat,
        double? scoreConsistency,
        double? scoreTagMatch,
        double? scoreHuman)
    {
        var weighted =
            (scoreLoudness ?? 0) * _options.WeightLoudness +
            (scoreDynamic ?? 0) * _options.WeightDynamic +
            (scoreClipping ?? 0) * _options.WeightClipping +
            (scoreNoise ?? 0) * _options.WeightNoise +
            (scoreDuration ?? 0) * _options.WeightDuration +
            (scoreFormat ?? 0) * _options.WeightFormat +
            (scoreConsistency ?? 0) * _options.WeightConsistency +
            (scoreTagMatch ?? 0) * _options.WeightTagMatch +
            (scoreHuman ?? 0) * _options.WeightHuman;

        var totalWeight = _options.WeightLoudness + _options.WeightDynamic + _options.WeightClipping +
                          _options.WeightNoise + _options.WeightDuration + _options.WeightFormat +
                          _options.WeightConsistency + _options.WeightTagMatch + _options.WeightHuman;
        if (totalWeight <= 0) return null;
        return Math.Round(Math.Clamp(weighted / totalWeight, 0, 100), 2);
    }

    private static double ScoreFromDistance(double diff, double tolerance)
    {
        if (tolerance <= 0) return diff <= 0 ? 100 : 0;
        return (1.0 - Clamp01(diff / tolerance)) * 100.0;
    }

    private static double Clamp01(double x) => Math.Max(0, Math.Min(1, x));

    private sealed record SoundScoreResult(
        double? ScoreLoudness,
        double? ScoreDynamic,
        double? ScoreClipping,
        double? ScoreNoise,
        double? ScoreDuration,
        double? ScoreFormat,
        double? ScoreConsistency,
        double? ScoreTagMatch,
        double? ScoreFinal,
        double AudioDurationSeconds);

    private sealed record WavData(
        int SampleRate,
        int BitsPerSample,
        int Channels,
        long SampleCount,
        double Peak,
        double Rms,
        long ClippedSamples,
        double DurationSeconds,
        IReadOnlyList<double> WindowRms,
        float[] Samples);

    private WavData ReadWav(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        if (br.ReadUInt32() != 0x46464952) throw new InvalidOperationException("Invalid RIFF");
        br.ReadUInt32();
        if (br.ReadUInt32() != 0x45564157) throw new InvalidOperationException("Invalid WAVE");

        ushort audioFormat = 0;
        ushort channels = 0;
        int sampleRate = 0;
        ushort bitsPerSample = 0;
        byte[]? data = null;

        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var chunkId = br.ReadUInt32();
            var chunkSize = br.ReadInt32();
            if (chunkSize < 0 || br.BaseStream.Position + chunkSize > br.BaseStream.Length) break;

            if (chunkId == 0x20746d66) // fmt 
            {
                audioFormat = br.ReadUInt16();
                channels = br.ReadUInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32(); // byteRate
                br.ReadUInt16(); // blockAlign
                bitsPerSample = br.ReadUInt16();
                var remaining = chunkSize - 16;
                if (remaining > 0) br.ReadBytes(remaining);
            }
            else if (chunkId == 0x61746164) // data
            {
                data = br.ReadBytes(chunkSize);
            }
            else
            {
                br.BaseStream.Seek(chunkSize, SeekOrigin.Current);
            }

            if ((chunkSize & 1) == 1 && br.BaseStream.Position < br.BaseStream.Length)
            {
                br.BaseStream.Seek(1, SeekOrigin.Current);
            }
        }

        if (channels == 0 || sampleRate <= 0 || bitsPerSample == 0 || data == null || data.Length == 0)
        {
            throw new InvalidOperationException("WAV incompleto (fmt/data)");
        }

        var samples = DecodeSamples(data, audioFormat, channels, bitsPerSample);
        if (samples.Length == 0) throw new InvalidOperationException("Formato WAV non supportato");

        long clipped = 0;
        double sumSq = 0;
        double peak = 0;
        foreach (var s in samples)
        {
            var a = Math.Abs((double)s);
            if (a > peak) peak = a;
            sumSq += a * a;
            if (a >= _options.ClippingThresholdAbs) clipped++;
        }

        var rms = Math.Sqrt(sumSq / Math.Max(1, samples.Length));
        var durationSeconds = (double)samples.Length / Math.Max(1, sampleRate);
        var windowRms = ComputeWindowRms(samples, sampleRate);

        return new WavData(sampleRate, bitsPerSample, channels, samples.Length, peak, rms, clipped, durationSeconds, windowRms, samples);
    }

    private static List<double> ComputeWindowRms(float[] samples, int sampleRate)
    {
        var result = new List<double>();
        if (samples.Length == 0 || sampleRate <= 0) return result;

        var window = Math.Max(256, sampleRate / 20); // ~50 ms
        for (var i = 0; i < samples.Length; i += window)
        {
            var end = Math.Min(samples.Length, i + window);
            double sumSq = 0;
            for (var j = i; j < end; j++)
            {
                var v = samples[j];
                sumSq += v * v;
            }
            var len = Math.Max(1, end - i);
            result.Add(Math.Sqrt(sumSq / len));
        }

        return result;
    }

    private static float[] DecodeSamples(byte[] data, ushort audioFormat, ushort channels, ushort bitsPerSample)
    {
        // Convert to mono by averaging channels.
        var bytesPerSamplePerChannel = bitsPerSample / 8;
        if (bytesPerSamplePerChannel <= 0 || channels <= 0) return Array.Empty<float>();
        var frameSize = bytesPerSamplePerChannel * channels;
        if (frameSize <= 0) return Array.Empty<float>();
        var frameCount = data.Length / frameSize;
        var output = new float[frameCount];

        var offset = 0;
        for (var i = 0; i < frameCount; i++)
        {
            double mono = 0;
            for (var ch = 0; ch < channels; ch++)
            {
                mono += ReadNormalizedSample(data, offset, audioFormat, bitsPerSample);
                offset += bytesPerSamplePerChannel;
            }
            output[i] = (float)(mono / channels);
        }

        return output;
    }

    private static double ReadNormalizedSample(byte[] data, int offset, ushort audioFormat, ushort bitsPerSample)
    {
        // PCM=1, IEEE float=3
        if (audioFormat == 3 && bitsPerSample == 32)
        {
            return Math.Clamp(BitConverter.ToSingle(data, offset), -1f, 1f);
        }

        if (audioFormat != 1)
        {
            return 0;
        }

        return bitsPerSample switch
        {
            8 => ((data[offset] - 128) / 128.0),
            16 => Math.Clamp(BitConverter.ToInt16(data, offset) / 32768.0, -1.0, 1.0),
            24 => Math.Clamp(ReadInt24(data, offset) / 8388608.0, -1.0, 1.0),
            32 => Math.Clamp(BitConverter.ToInt32(data, offset) / 2147483648.0, -1.0, 1.0),
            _ => 0
        };
    }

    private static int ReadInt24(byte[] data, int offset)
    {
        var b0 = data[offset];
        var b1 = data[offset + 1];
        var b2 = data[offset + 2];
        var value = b0 | (b1 << 8) | (b2 << 16);
        if ((value & 0x800000) != 0)
        {
            value |= unchecked((int)0xFF000000);
        }
        return value;
    }
}
