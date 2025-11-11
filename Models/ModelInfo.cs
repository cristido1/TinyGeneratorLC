using System;

namespace TinyGenerator.Models;

public class ModelInfo
{
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
    public bool? SkillToUpper { get; set; }
    public bool? SkillToLower { get; set; }
    public bool? SkillTrim { get; set; }
    public bool? SkillLength { get; set; }
    public bool? SkillSubstring { get; set; }
    public bool? SkillJoin { get; set; }
    public bool? SkillSplit { get; set; }
    public bool? SkillAdd { get; set; }
    public bool? SkillSubtract { get; set; }
    public bool? SkillMultiply { get; set; }
    public bool? SkillDivide { get; set; }
    public bool? SkillSqrt { get; set; }
    public bool? SkillNow { get; set; }
    public bool? SkillToday { get; set; }
    public bool? SkillAddDays { get; set; }
    public bool? SkillAddHours { get; set; }
    public bool? SkillRemember { get; set; }
    public bool? SkillRecall { get; set; }
    public bool? SkillForget { get; set; }
    public bool? SkillFileExists { get; set; }
    public bool? SkillHttpGet { get; set; }
    // AudioCraft skill test flags
    public bool? SkillAudioCheckHealth { get; set; }
    public bool? SkillAudioListModels { get; set; }
    public bool? SkillAudioGenerateMusic { get; set; }
    public bool? SkillAudioGenerateSound { get; set; }
    public bool? SkillAudioDownloadFile { get; set; }

    // TTS skill test flags
    public bool? SkillTtsCheckHealth { get; set; }
    public bool? SkillTtsListVoices { get; set; }
    public bool? SkillTtsSynthesize { get; set; }

    // Duration in seconds taken to run the full battery of tests
    public double? TestDurationSeconds { get; set; }
    // JSON-serialized last test results (array of { name, ok, message })
    public string? LastTestResults { get; set; }
    // Last generated test audio files (relative paths under wwwroot)
    public string? LastMusicTestFile { get; set; }
    public string? LastSoundTestFile { get; set; }
    public string? LastTtsTestFile { get; set; }
}
