namespace TinyGenerator.Models;

public interface ICreateUpdateDate
{
    DateTime? CreatedAt { get; set; }
    DateTime? UpdatedAt { get; set; }
}
