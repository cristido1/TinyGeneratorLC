namespace TinyGenerator.Services;

public sealed class TtsSchemaGenerationOptions
{
    public double PhraseGapSeconds { get; set; } = 2;
    public bool AutolaunchNextCommand { get; set; } = true;
    public int CharacterExtractionMaxAttempts { get; set; } = 3;
}
