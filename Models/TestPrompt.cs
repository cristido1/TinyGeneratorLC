using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("test_prompts")]
public class TestPrompt
{
    [Column("id")]
    public int Id { get; set; }
    [Column("group_name")]
    public string? GroupName { get; set; }
    [Column("library")]
    public string? Library { get; set; }
    [Column("prompt")]
    public string? Prompt { get; set; }
    [Column("active")]
    public bool Active { get; set; }
    [Column("priority")]
    public int Priority { get; set; }
}
