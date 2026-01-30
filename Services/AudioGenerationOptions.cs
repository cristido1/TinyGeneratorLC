namespace TinyGenerator.Services;

public sealed class AudioGenerationOptions
{
    public AudioGenerationCommandOptions Tts { get; set; } = new();
    public AudioGenerationCommandOptions Ambience { get; set; } = new();
    public AudioGenerationCommandOptions Fx { get; set; } = new();
}

public sealed class AudioGenerationCommandOptions
{
    public bool AutolaunchNextCommand { get; set; } = true;
}
