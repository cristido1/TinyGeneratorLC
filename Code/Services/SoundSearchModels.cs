using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed record SoundSearchSourceError(string Source, string Error, string? Detail = null);

public sealed record SoundSearchCandidate(
    string Source,
    string ExternalId,
    string Title,
    string? Description,
    string SourceUrl,
    string DownloadUrl,
    string? License,
    string? Author,
    double? DurationSeconds,
    IReadOnlyList<string> Tags,
    string? FileExtension,
    double Score);

public sealed record SoundSearchInsertedSoundInfo(
    int SoundId,
    string Source,
    string FilePath,
    string FileName,
    double Score);

public sealed class SoundSearchProcessResult
{
    public long MissingId { get; init; }
    public string Status { get; set; } = "failed";
    public int SourcesTried { get; set; }
    public int CandidatesSeen { get; set; }
    public int InsertedCount { get; set; }
    public List<string> TriedSources { get; } = new();
    public List<SoundSearchInsertedSoundInfo> Inserted { get; } = new();
    public List<SoundSearchSourceError> Errors { get; } = new();
    public string? Message { get; set; }
    public SoundMissing? Missing { get; set; }
}
