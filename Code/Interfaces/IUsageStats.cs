namespace TinyGenerator.Models;

public interface IUsageStats
{
    int UseCount { get; set; }
    int UseSuccessed { get; set; }
    int UseFailed { get; set; }
}
