using Microsoft.EntityFrameworkCore;
using TinyGenerator.Models;

namespace TinyGenerator.Data.Repositories;

public class StoryRepository : Repository<StoryRecord>, IStoryRepository
{
    public StoryRepository(TinyGeneratorDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<StoryRecord>> GetStoriesWithEvaluationsAsync()
    {
        // Using raw SQL for now since StoryRecord doesn't have navigation properties yet
        // This can be optimized later with proper EF Core relationships
        var stories = await _dbSet.ToListAsync();
        return stories;
    }

    public async Task<StoryRecord?> GetStoryWithEvaluationsAsync(int storyId)
    {
        return await _dbSet.FirstOrDefaultAsync(s => s.Id == storyId);
    }

    public async Task<IEnumerable<StoryRecord>> GetStoriesByStatusAsync(int statusId)
    {
        return await _dbSet
            .Where(s => s.StatusId == statusId)
            .OrderByDescending(s => s.Timestamp)
            .ToListAsync();
    }

    public async Task<IEnumerable<StoryRecord>> GetStoriesByWriterAsync(string writerId)
    {
        return await _dbSet
            .Where(s => s.Agent == writerId)
            .OrderByDescending(s => s.Timestamp)
            .ToListAsync();
    }

    public async Task<IEnumerable<StoryRecord>> GetRecentStoriesAsync(int count)
    {
        return await _dbSet
            .OrderByDescending(s => s.Timestamp)
            .Take(count)
            .ToListAsync();
    }
}
