using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;

namespace TinyGenerator.Pages;

public class StoryLiveModel : PageModel
{
    private readonly DatabaseService _database;

    public StoryLiveModel(DatabaseService database)
    {
        _database = database;
    }

    public long StoryId { get; private set; }
    public string StoryTitle { get; private set; } = "Story Live";

    public void OnGet(long? id)
    {
        if (!id.HasValue || id.Value <= 0)
        {
            StoryId = 0;
            StoryTitle = "Seleziona una storia";
            return;
        }

        StoryId = id.Value;
        var story = _database.GetStoryById(id.Value);
        if (story != null && !string.IsNullOrWhiteSpace(story.Title))
        {
            StoryTitle = story.Title.Trim();
        }
        else
        {
            StoryTitle = $"Story {id.Value}";
        }
    }
}
