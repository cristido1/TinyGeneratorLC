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
    public List<SystemReportSummary> RecentReports { get; set; } = new();
    
    // Auto-advancement property
    public bool EnableAutoAdvancement { get; set; }

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
        public string Title { get; set; } = "";
        public string Agent { get; set; } = "";
        public bool GeneratedMixedAudio { get; set; }
        public double AvgEvalScore { get; set; }
        public string Timestamp { get; set; } = "";
    }

    public class SystemReportSummary
    {
        public long Id { get; set; }
        public string CreatedAt { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Status { get; set; } = "";
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public string? AgentName { get; set; }
        public string? ModelName { get; set; }
        public string? OperationType { get; set; }
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
            // Load auto-advancement setting from config/state
            EnableAutoAdvancement = _stories.IsAutoAdvancementEnabled();
            
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
                Title = s.Title.Length > 60 ? s.Title.Substring(0, 60) + "..." : s.Title,
                Agent = s.Agent,
                GeneratedMixedAudio = s.GeneratedMixedAudio,
                AvgEvalScore = s.AvgScore,
                Timestamp = s.Timestamp
            }).ToList();

            var reports = _database.GetRecentSystemReports(5);
            RecentReports = reports.Select(r => new SystemReportSummary
            {
                Id = r.Id,
                CreatedAt = r.CreatedAt,
                Severity = r.Severity,
                Status = r.Status,
                Title = r.Title ?? "(senza titolo)",
                Message = r.Message ?? string.Empty,
                AgentName = r.AgentName,
                ModelName = r.ModelName,
                OperationType = r.OperationType
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading KPI data");
        }
    }
    
    public IActionResult OnPostToggleAutoAdvancement(bool enabled)
    {
        try
        {
            _stories.SetAutoAdvancementEnabled(enabled);
            TempData["Message"] = enabled 
                ? "✓ Avanzamento automatico abilitato. Dopo 10 minuti di inattività verrà processata la storia con punteggio più alto."
                : "Avanzamento automatico disabilitato.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling auto advancement");
            TempData["Error"] = "Errore durante il cambio impostazione.";
        }
        return RedirectToPage();
    }
}
