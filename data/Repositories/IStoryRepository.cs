using TinyGenerator.Models;

namespace TinyGenerator.Data.Repositories;

public interface IStoryRepository : IRepository<StoryRecord>
{
    Task<IEnumerable<StoryRecord>> GetStoriesWithEvaluationsAsync();
    Task<StoryRecord?> GetStoryWithEvaluationsAsync(int storyId);
    Task<IEnumerable<StoryRecord>> GetStoriesByStatusAsync(int statusId);
    Task<IEnumerable<StoryRecord>> GetStoriesByWriterAsync(string writerId);
    Task<IEnumerable<StoryRecord>> GetRecentStoriesAsync(int count);
}
