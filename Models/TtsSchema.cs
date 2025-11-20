using System.Collections.Generic;

public class TtsCharacter
{
    public string Name { get; set; } = "";
    public string Voice { get; set; } = "";
    public string Gender { get; set; } = "";
    public string EmotionDefault { get; set; } = "";
}
public class TtsPhrase
{
    public string Character { get; set; } = "";
    public string Text { get; set; } = "";
    public string Emotion { get; set; } = "";
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