namespace TinyGenerator.Models;

public interface ISoftDelete
{
    bool IsDeleted { get; set; }
}
