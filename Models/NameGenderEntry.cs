namespace TinyGenerator.Models;

public sealed class NameGenderEntry
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Gender { get; set; } = "unknown";
    public string InsertDate { get; set; } = string.Empty;
    public bool Verified { get; set; }
}
