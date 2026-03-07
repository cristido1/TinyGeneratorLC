using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;
using System.Linq;

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
    public string NrePlanSummary { get; private set; } = string.Empty;
    public string ApprovedText { get; private set; } = string.Empty;

    public void OnGet(long? id)
    {
        if (!id.HasValue || id.Value <= 0)
        {
            StoryId = 0;
            StoryTitle = "Seleziona una storia";
            NrePlanSummary = string.Empty;
            ApprovedText = string.Empty;
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

        var (plan, approvedText) = BuildLiveStoryState(id.Value);
        NrePlanSummary = plan;
        ApprovedText = approvedText;
    }

    public IActionResult OnGetState(long id)
    {
        if (id <= 0)
        {
            return new JsonResult(new
            {
                ok = false,
                error = "id non valido"
            });
        }

        var story = _database.GetStoryById(id);
        if (story == null)
        {
            return new JsonResult(new
            {
                ok = false,
                error = "storia non trovata"
            });
        }

        var (plan, approvedText) = BuildLiveStoryState(id);
        return new JsonResult(new
        {
            ok = true,
            storyId = id,
            title = string.IsNullOrWhiteSpace(story.Title) ? $"Story {id}" : story.Title.Trim(),
            nrePlanSummary = plan,
            approvedText = approvedText
        });
    }

    private (string PlanSummary, string ApprovedText) BuildLiveStoryState(long storyId)
    {
        var story = _database.GetStoryById(storyId);
        var plan = story?.NrePlanSummary?.Trim() ?? string.Empty;

        var blocks = _database.ListNarrativeStoryBlocks(storyId, limit: 10000)
            .OrderBy(b => b.BlockIndex)
            .ThenBy(b => b.Id)
            .Select(b => b.TextContent?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        var approved = string.Join(Environment.NewLine + Environment.NewLine, blocks!);
        return (plan, approved);
    }
}
