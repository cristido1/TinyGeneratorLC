using System.Collections.Generic;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands;

public sealed record ResolvedAgent(
    Agent Agent,
    int ModelId,
    string ModelName,
    string? BaseSystemPrompt,
    HashSet<string> TriedModelNames);

public sealed record StoryTaggingPreparationResult(
    StoryRecord Story,
    string SourceText,
    string StoryRows,
    IReadOnlyList<StoryTaggingService.StoryRow> Rows,
    IReadOnlyList<StoryTaggingService.RowChunk> Chunks);

public sealed record ChunkProcessRequest(
    Agent Agent,
    string RoleCode,
    string? SystemPrompt,
    string ChunkText,
    int ChunkIndex,
    int ChunkCount,
    string RunId,
    int CurrentModelId,
    string CurrentModelName,
    HashSet<string> TriedModelNames,
    CommandTuningOptions.AmbientExpertTuning Tuning,
    ICommandTelemetry Telemetry,
    string OperationScope);

public sealed record ChunkProcessResult(
    string MappingText,
    int ModelId,
    string ModelName,
    bool UsedFallback);

