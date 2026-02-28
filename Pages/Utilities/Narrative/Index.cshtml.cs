using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.Utilities.Narrative;

public class IndexModel : PageModel
{
    private readonly DatabaseService _database;

    public IndexModel(DatabaseService database)
    {
        _database = database;
    }

    [BindProperty(SupportsGet = true)]
    public long? StoryId { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Limit { get; set; } = 200;

    public IReadOnlyList<DatabaseService.NarrativeContinuityStateDto> ContinuityStates { get; private set; } = Array.Empty<DatabaseService.NarrativeContinuityStateDto>();
    public IReadOnlyList<DatabaseService.NarrativeStoryBlockDto> StoryBlocks { get; private set; } = Array.Empty<DatabaseService.NarrativeStoryBlockDto>();
    public IReadOnlyList<DatabaseService.NarrativeAgentCallLogDto> AgentCalls { get; private set; } = Array.Empty<DatabaseService.NarrativeAgentCallLogDto>();
    public IReadOnlyList<DatabaseService.NarrativePlanningStateDto> PlanningStates { get; private set; } = Array.Empty<DatabaseService.NarrativePlanningStateDto>();

    public void OnGet()
    {
        var lim = Math.Max(20, Math.Min(2000, Limit));
        ContinuityStates = _database.ListNarrativeContinuityStates(StoryId, lim);
        StoryBlocks = _database.ListNarrativeStoryBlocks(StoryId, lim);
        AgentCalls = _database.ListNarrativeAgentCallLogs(StoryId, lim);
        PlanningStates = _database.ListNarrativePlanningStates(null, Math.Min(lim, 500));
        Limit = lim;
    }

    public IActionResult OnPostRefresh([FromForm] long? storyId, [FromForm] int? limit)
    {
        return RedirectToPage(new
        {
            StoryId = storyId,
            Limit = limit.GetValueOrDefault(200)
        });
    }
}

