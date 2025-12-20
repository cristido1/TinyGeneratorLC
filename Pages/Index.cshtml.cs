using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages;

public class IndexModel : PageModel
{
    private readonly ILogger<IndexModel> _logger;
    private readonly DatabaseService _database;
    private readonly StoriesService _stories;

    // KPI properties
    public int TotalStories { get; set; }
    public int EnabledModels { get; set; }
    public int DisabledModels { get; set; }
    public int AudioMasterGeneratedCount { get; set; }
    public List<WriterRanking> TopWriters { get; set; } = new();
    public List<TopStory> TopStories { get; set; } = new();

    public class WriterRanking
    {
        public string AgentName { get; set; } = "";
        public string ModelName { get; set; } = "";
        public double AvgScore { get; set; }
        public int StoryCount { get; set; }
    }

    public class TopStory
    {
        public long Id { get; set; }
        public string Prompt { get; set; } = "";
        public string Model { get; set; } = "";
        public double AvgEvalScore { get; set; }
        public string Timestamp { get; set; } = "";
    }

    public IndexModel(ILogger<IndexModel> logger, DatabaseService database, StoriesService stories)
    {
        _logger = logger;
        _database = database;
        _stories = stories;
    }

    public void OnGet()
    {
        try
        {
            // Total stories
            var allStories = _stories.GetAllStories();
            TotalStories = allStories.Count;

            // Models enabled/disabled
            var models = _database.ListModels();
            EnabledModels = models.Count(m => m.Enabled);
            DisabledModels = models.Count(m => !m.Enabled);

            // Stories in "audio_master_generated" status
            var statuses = _database.ListAllStoryStatuses();
            var audioMasterStatus = statuses.FirstOrDefault(s => 
                s.Code?.Equals("audio_master_generated", StringComparison.OrdinalIgnoreCase) == true ||
                s.Code?.Equals("final_mix_ready", StringComparison.OrdinalIgnoreCase) == true);
            if (audioMasterStatus != null)
            {
                AudioMasterGeneratedCount = allStories.Count(s => s.StatusId == audioMasterStatus.Id);
            }

            // Top writers by average evaluation score (from stories_evaluations)
            var topWritersData = _database.GetTopWritersByEvaluation(10);
            TopWriters = topWritersData.Select(w => new WriterRanking
            {
                AgentName = w.AgentName,
                ModelName = w.ModelName,
                AvgScore = w.AvgScore,
                StoryCount = w.StoryCount
            }).ToList();

            // Top 10 stories by evaluation score (from stories_evaluations)
            var topStoriesData = _database.GetTopStoriesByEvaluation(10);
            TopStories = topStoriesData.Select(s => new TopStory
            {
                Id = s.Id,
                Prompt = s.Prompt.Length > 60 ? s.Prompt.Substring(0, 60) + "..." : s.Prompt,
                Model = s.Model,
                AvgEvalScore = s.AvgScore,
                Timestamp = s.Timestamp
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading KPI data");
        }
    }
}
