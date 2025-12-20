using System.Collections.Generic;

public class TtsCharacter
{
    public string Name { get; set; } = "";
    public string Voice { get; set; } = "";
    public string VoiceId { get; set; } = "";
    public string Gender { get; set; } = "";
    public string EmotionDefault { get; set; } = "";
}
public class TtsPhrase
{
    public string Character { get; set; } = "";
    public string Text { get; set; } = "";
    public string Emotion { get; set; } = "";
    public string FileName { get; set; } = "";
    public int? DurationMs { get; set; }
    public int? StartMs { get; set; }
    public int? EndMs { get; set; }
    
    /// <summary>
    /// Background ambient sound/setting description (e.g., "spaceship_hum", "wind", "crowd_murmur").
    /// Used to generate background sounds during audio mixing.
    /// </summary>
    public string? Ambience { get; set; }
    
    /// <summary>
    /// Sound effect description to generate (from [FX, duration, description] tag).
    /// </summary>
    public string? FxDescription { get; set; }
    
    /// <summary>
    /// Duration in seconds for the sound effect.
    /// </summary>
    public int? FxDuration { get; set; }
    
    /// <summary>
    /// Generated sound effect file name.
    /// </summary>
    public string? FxFile { get; set; }
    
    /// <summary>
    /// Music description to generate (from [MUSICA: description] tag).
    /// </summary>
    public string? MusicDescription { get; set; }
    
    /// <summary>
    /// Duration in seconds for the music (default 10 seconds).
    /// </summary>
    public int? MusicDuration { get; set; }
    
    /// <summary>
    /// Generated music file name.
    /// </summary>
    public string? MusicFile { get; set; }
}
public class TtsPause
{
    public int Seconds { get; set; }

    public TtsPause(int sec)
    {
        Seconds = sec;
    }
}

public class TtsSchema
{
    public List<TtsCharacter> Characters { get; set; } = new();
    public List<object> Timeline { get; set; } = new(); 
    // Timeline può contenere TtsPhrase o TtsPause
}
