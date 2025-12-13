using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("chapters")]
public class Chapter
{
    public int Id { get; set; }
    [Column("memory_key")]
    public string MemoryKey { get; set; } = string.Empty;
    [Column("chapter_number")]
    public int ChapterNumber { get; set; }
    [Column("content")]
    public string Content { get; set; } = string.Empty;
    [Column("ts")]
    public string Ts { get; set; } = DateTime.UtcNow.ToString("o");
}
