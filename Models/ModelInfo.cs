using System;

namespace TinyGenerator.Models;

public class ModelInfo
{
    public int? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public bool IsLocal { get; set; }
    public int MaxContext { get; set; }
    public int ContextToUse { get; set; }
    public int FunctionCallingScore { get; set; }
    public double CostInPerToken { get; set; }
    public double CostOutPerToken { get; set; }
    public long LimitTokensDay { get; set; }
    public long LimitTokensWeek { get; set; }
    public long LimitTokensMonth { get; set; }
    public string Metadata { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
    public double? TestDurationSeconds { get; set; }
    // Indicates the model does NOT support tools/function-calling (true = no tools supported)
    public bool NoTools { get; set; } = false;
    public double WriterScore { get; set; }
    public double BaseScore { get; set; }
    public double TextEvalScore { get; set; }
    public double TtsScore { get; set; }
    public double MusicScore { get; set; }
    public double FxScore { get; set; }
    public double AmbientScore { get; set; }
    public double TotalScore { get; set; }
    // Free-form note for the model (shown in grid and edit form)
    public string? Note { get; set; }
    // JSON-serialized last test results (array of { name, ok, message })
    public string? LastTestResults { get; set; }
    // Last generated test audio files (relative paths under wwwroot)
    public string? LastMusicTestFile { get; set; }
    public string? LastSoundTestFile { get; set; }
    public string? LastTtsTestFile { get; set; }

    // Per-group latest score columns (UI-only, populated at page render)
    public int? LastScore_Base { get; set; }
    public int? LastScore_Tts { get; set; }
    public int? LastScore_Music { get; set; }
    public int? LastScore_Write { get; set; }

    // Per-group last run results JSON (array) for detail view
    public string? LastResults_BaseJson { get; set; }
    public string? LastResults_TtsJson { get; set; }
    public string? LastResults_MusicJson { get; set; }
    public string? LastResults_WriteJson { get; set; }

    // Flexible per-group maps for dynamic UI (group name -> score / json)
    // Populated at page render time by the PageModel.
    public System.Collections.Generic.Dictionary<string, int?>? LastGroupScores { get; set; }
    public System.Collections.Generic.Dictionary<string, string?>? LastGroupResultsJson { get; set; }
}
