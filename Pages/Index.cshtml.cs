using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;
using TinyGenerator.Models;
using System.Text.Json;

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
    public List<KpiCard> AdditionalKpis { get; set; } = new();
    public List<PieSlice> StoriesByStatusSlices { get; set; } = new();
    public List<PieSlice> ScoreBandSlices { get; set; } = new();
    
    // Auto-advancement property
    public bool EnableAutoAdvancement { get; set; }
    public string AutoAdvancementMode { get; set; } = "series";

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

    public class KpiCard
    {
        public string Code { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public class PieSlice
    {
        public string Label { get; set; } = string.Empty;
        public int Count { get; set; }
        public double Percentage { get; set; }
        public string Color { get; set; } = "#6c757d";
    }

    public string StoriesByStatusJson => JsonSerializer.Serialize(StoriesByStatusSlices);
    public string ScoreBandJson => JsonSerializer.Serialize(ScoreBandSlices);

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
            AutoAdvancementMode = _stories.GetAutoAdvancementMode();
            
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

            AdditionalKpis = LoadAdditionalKpis();
            StoriesByStatusSlices = LoadStoriesByStatusSlices();
            ScoreBandSlices = LoadScoreBandSlices();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading KPI data");
        }
    }
    
    public IActionResult OnPostToggleAutoAdvancement(bool enabled, string? mode)
    {
        try
        {
            _stories.SetAutoAdvancementEnabled(enabled);
            _stories.SetAutoAdvancementMode(mode);
            var selectedMode = string.Equals(mode, "nre", StringComparison.OrdinalIgnoreCase)
                ? "nre"
                : (string.Equals(mode, "nre_manual", StringComparison.OrdinalIgnoreCase) ? "nre_manual" : "series");
            TempData["Message"] = enabled
                ? selectedMode == "nre"
                    ? "Avanzamento automatico abilitato in modalita NRE casuale: in inattivita verra accodata una nuova storia NRE (15 step)."
                    : selectedMode == "nre_manual"
                        ? "Avanzamento automatico abilitato in modalita NRE manuale: le storie generate verranno portate fino a valutazione effettuata."
                        : "Avanzamento automatico abilitato in modalita Serie: in inattivita verra avanzata una serie."
                : "Avanzamento automatico disabilitato.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling auto advancement");
            TempData["Error"] = "Errore durante il cambio impostazione.";
        }
        return RedirectToPage();
    }

    private List<KpiCard> LoadAdditionalKpis()
    {
        return _database.WithSqliteConnection(conn =>
        {
            var cards = new List<KpiCard>();

            bool HasRows(string tableName)
            {
                using var existsCmd = conn.CreateCommand();
                existsCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
                existsCmd.Parameters.AddWithValue("@name", tableName);
                var exists = Convert.ToInt64(existsCmd.ExecuteScalar() ?? 0L) > 0;
                if (!exists) return false;

                using var rowsCmd = conn.CreateCommand();
                rowsCmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {tableName} LIMIT 1);";
                return Convert.ToInt64(rowsCmd.ExecuteScalar() ?? 0L) > 0;
            }

            long ScalarLong(string sql)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }

            double? ScalarDoubleNullable(string sql)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                var raw = cmd.ExecuteScalar();
                if (raw == null || raw == DBNull.Value) return null;
                return Convert.ToDouble(raw);
            }

            if (HasRows("stories_evaluations"))
            {
                var storiesWithEval = ScalarLong("SELECT COUNT(DISTINCT story_id) FROM stories_evaluations;");
                var approvedStories = ScalarLong(@"
SELECT COUNT(*) FROM (
    SELECT story_id, AVG(total_score) AS media
    FROM stories_evaluations
    GROUP BY story_id
    HAVING media > 60
);");
                var approvalPct = storiesWithEval > 0 ? (approvedStories * 100.0 / storiesWithEval) : 0.0;
                var avgScore = ScalarDoubleNullable("SELECT AVG(total_score) FROM stories_evaluations;") ?? 0.0;
                var avgLast20 = ScalarDoubleNullable(@"
SELECT AVG(media) FROM (
    SELECT story_id, AVG(total_score) AS media
    FROM stories_evaluations
    GROUP BY story_id
    ORDER BY MAX(ts) DESC
    LIMIT 20
);") ?? 0.0;
                var avgPenalty = ScalarDoubleNullable("SELECT AVG(lenght_penality_percentage_applyed) FROM stories_evaluations;") ?? 0.0;
                var defectsCoherence = ScalarLong("SELECT COUNT(*) FROM stories_evaluations WHERE narrative_coherence_score < 60;");
                var defectsOriginality = ScalarLong("SELECT COUNT(*) FROM stories_evaluations WHERE originality_score < 60;");
                var defectsEmotion = ScalarLong("SELECT COUNT(*) FROM stories_evaluations WHERE emotional_impact_score < 60;");
                var defectsAction = ScalarLong("SELECT COUNT(*) FROM stories_evaluations WHERE action_score < 60;");

                cards.Add(new KpiCard { Code = "KPI_03", Label = "Storie con Valutazione", Value = storiesWithEval.ToString("N0") });
                cards.Add(new KpiCard { Code = "KPI_04", Label = "Storie Approvate (>60)", Value = approvedStories.ToString("N0") });
                cards.Add(new KpiCard { Code = "KPI_05", Label = "Percentuale Approvazione", Value = $"{approvalPct:F1}%" });
                cards.Add(new KpiCard { Code = "KPI_06", Label = "Score Medio Globale", Value = $"{avgScore:F1}" });
                cards.Add(new KpiCard { Code = "KPI_07", Label = "Score Medio Ultime 20", Value = $"{avgLast20:F1}" });
                cards.Add(new KpiCard { Code = "KPI_09", Label = "PenalitÃ  Lunghezza Media", Value = $"{avgPenalty:F2}" });
                cards.Add(new KpiCard { Code = "KPI_10", Label = "Difetti Coerenza (<60)", Value = defectsCoherence.ToString("N0") });
                cards.Add(new KpiCard { Code = "KPI_11", Label = "Difetti OriginalitÃ  (<60)", Value = defectsOriginality.ToString("N0") });
                cards.Add(new KpiCard { Code = "KPI_12", Label = "Difetti Impatto Emotivo (<60)", Value = defectsEmotion.ToString("N0") });
                cards.Add(new KpiCard { Code = "KPI_13", Label = "Difetti Azione (<60)", Value = defectsAction.ToString("N0") });
            }

            if (HasRows("stats_models"))
            {
                var weightedSuccess = ScalarDoubleNullable(@"
SELECT CASE WHEN SUM(count_used) > 0
    THEN SUM(count_successed) * 100.0 / SUM(count_used)
    ELSE NULL END
FROM stats_models;
") ?? 0.0;
                var avgGenTime = ScalarDoubleNullable(@"
SELECT CASE WHEN SUM(duration_total_count) > 0
    THEN SUM(duration_total_time) * 1.0 / SUM(duration_total_count)
    ELSE NULL END
FROM stats_models;
") ?? 0.0;
                cards.Add(new KpiCard { Code = "KPI_14", Label = "Success Rate Modelli", Value = $"{weightedSuccess:F1}%" });
                cards.Add(new KpiCard { Code = "KPI_15", Label = "Tempo Medio Generazione", Value = $"{avgGenTime:F2}s" });
            }

            if (HasRows("model_roles"))
            {
                var writerPrimarySuccess = ScalarDoubleNullable(@"
SELECT CASE WHEN SUM(mr.use_count) > 0
    THEN SUM(mr.use_successed) * 100.0 / SUM(mr.use_count)
    ELSE NULL END
FROM model_roles mr
JOIN roles r ON mr.role_id = r.id
WHERE lower(r.ruolo) = 'writer'
  AND mr.is_primary = 1;
") ?? 0.0;
                cards.Add(new KpiCard { Code = "KPI_16", Label = "Writer Primario Success", Value = $"{writerPrimarySuccess:F1}%" });
            }

            if (HasRows("model_roles_errors"))
            {
                var totalErrors = ScalarLong("SELECT COALESCE(SUM(error_count), 0) FROM model_roles_errors;");
                cards.Add(new KpiCard { Code = "KPI_17", Label = "Errori Totali Modelli", Value = totalErrors.ToString("N0") });
            }

            if (HasRows("sounds"))
            {
                var avgSoundUsage = ScalarDoubleNullable("SELECT AVG(usage_count) FROM sounds WHERE enabled = 1;") ?? 0.0;
                cards.Add(new KpiCard { Code = "KPI_26", Label = "Utilizzo Medio Suoni", Value = $"{avgSoundUsage:F2}" });
            }

            if (HasRows("series"))
            {
                var totalSeries = ScalarLong("SELECT COUNT(*) FROM series;");
                cards.Add(new KpiCard { Code = "KPI_27", Label = "Serie Totali", Value = totalSeries.ToString("N0") });
            }

            if (HasRows("series_episodes"))
            {
                var avgEpisodesPerSeries = ScalarDoubleNullable(@"
SELECT AVG(cnt) FROM (
    SELECT serie_id, COUNT(*) AS cnt
    FROM series_episodes
    GROUP BY serie_id
);") ?? 0.0;
                cards.Add(new KpiCard { Code = "KPI_28", Label = "Episodi Medi per Serie", Value = $"{avgEpisodesPerSeries:F2}" });
            }

            if (HasRows("series_characters"))
            {
                var avgCharactersPerSeries = ScalarDoubleNullable(@"
SELECT AVG(cnt) FROM (
    SELECT serie_id, COUNT(*) AS cnt
    FROM series_characters
    GROUP BY serie_id
);") ?? 0.0;
                cards.Add(new KpiCard { Code = "KPI_29", Label = "Personaggi Medi per Serie", Value = $"{avgCharactersPerSeries:F2}" });
            }

            if (HasRows("model_test_runs"))
            {
                var passRate = ScalarDoubleNullable("SELECT SUM(COALESCE(passed,0)) * 100.0 / COUNT(*) FROM model_test_runs;") ?? 0.0;
                var avgTestDuration = ScalarDoubleNullable("SELECT AVG(duration_ms) FROM model_test_runs;") ?? 0.0;
                cards.Add(new KpiCard { Code = "KPI_30", Label = "Test Run Pass Rate", Value = $"{passRate:F1}%" });
                cards.Add(new KpiCard { Code = "KPI_31", Label = "Durata Media Test", Value = FormatDurationFromMilliseconds(avgTestDuration) });
            }

            if (HasRows("stories"))
            {
                var activeStories = ScalarLong("SELECT COUNT(*) FROM stories WHERE deleted = 0;");
                var withTtsPct = activeStories > 0
                    ? (ScalarLong("SELECT COUNT(*) FROM stories WHERE generated_tts = 1 AND deleted = 0;") * 100.0 / activeStories)
                    : 0.0;
                var fullAudioPct = activeStories > 0
                    ? (ScalarLong(@"
SELECT COUNT(*) FROM stories
WHERE generated_tts = 1
  AND generated_music = 1
  AND generated_ambient = 1
  AND generated_effects = 1
  AND generated_mixed_audio = 1
  AND deleted = 0;") * 100.0 / activeStories)
                    : 0.0;

                cards.Add(new KpiCard { Code = "KPI_23", Label = "% Storie con TTS", Value = $"{withTtsPct:F1}%" });
                cards.Add(new KpiCard { Code = "KPI_24", Label = "% Storie Audio Completo", Value = $"{fullAudioPct:F1}%" });
            }

            if (HasRows("sounds_missing"))
            {
                var openMissingSounds = ScalarLong("SELECT COUNT(*) FROM sounds_missing WHERE lower(status) = 'open';");
                cards.Add(new KpiCard { Code = "KPI_25", Label = "Suoni Mancanti Aperti", Value = openMissingSounds.ToString("N0") });
            }

            // NOTE: KPI_02, KPI_18, KPI_19, KPI_20 non vengono aggiunti:
            // tabella story_runtime_states assente nel DB corrente.
            // KPI_21, KPI_22 non vengono aggiunti se narrative_story_blocks Ã¨ vuota.
            if (HasRows("narrative_story_blocks"))
            {
                var avgBlocksByStory = ScalarDoubleNullable(@"
SELECT AVG(cnt) FROM (
    SELECT story_id, COUNT(*) AS cnt
    FROM narrative_story_blocks
    GROUP BY story_id
);") ?? 0.0;
                var avgBlocksQuality = ScalarDoubleNullable("SELECT AVG(quality_score) FROM narrative_story_blocks;") ?? 0.0;
                cards.Add(new KpiCard { Code = "KPI_21", Label = "Blocchi Medi per Storia", Value = $"{avgBlocksByStory:F2}" });
                cards.Add(new KpiCard { Code = "KPI_22", Label = "QualitÃ  Media Blocchi", Value = $"{avgBlocksQuality:F2}" });
            }

            return cards;
        });
    }

    private List<PieSlice> LoadStoriesByStatusSlices()
    {
        try
        {
            var statuses = _database.ListAllStoryStatuses()
                .Where(s => s != null && s.Id > 0)
                .ToDictionary(
                    s => s.Id,
                    s => !string.IsNullOrWhiteSpace(s.Description) ? s.Description!.Trim()
                        : (!string.IsNullOrWhiteSpace(s.Code) ? s.Code!.Trim() : "Sconosciuto"));

            var grouped = _stories.GetAllStories()
                .GroupBy(story =>
                {
                    if (story.StatusId.HasValue && statuses.TryGetValue(story.StatusId.Value, out var label) && !string.IsNullOrWhiteSpace(label))
                    {
                        return label;
                    }

                    if (!string.IsNullOrWhiteSpace(story.StatusDescription))
                    {
                        return story.StatusDescription.Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(story.Status))
                    {
                        return story.Status.Trim();
                    }

                    return "Sconosciuto";
                })
                .Select(g => (label: g.Key, count: g.Count()))
                .OrderByDescending(x => x.count)
                .ToList();

            return BuildPieSlices(grouped);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Errore caricando KPI 'Storie per Stato'");
            return new List<PieSlice>();
        }
    }

    private List<PieSlice> LoadScoreBandSlices()
    {
        try
        {
            var b90 = 0;
            var b80 = 0;
            var b70 = 0;
            var b60 = 0;
            var blow = 0;

            var stories = _stories.GetAllStories();
            foreach (var story in stories)
            {
                var textLength = !string.IsNullOrWhiteSpace(story.StoryRevised)
                    ? story.StoryRevised!.Length
                    : (story.StoryRaw?.Length ?? 0);
                var effectiveLength = Math.Max(story.CharCount, textLength);
                if (effectiveLength < 1000)
                {
                    continue;
                }

                var evals = _database.GetStoryEvaluations(story.Id) ?? new List<StoryEvaluation>();
                if (evals.Count == 0)
                {
                    continue;
                }

                var avgRaw = evals.Average(e => e.TotalScore);
                var avg = DatabaseService.NormalizeEvaluationScoreTo100(avgRaw);

                if (avg >= 90) b90++;
                else if (avg >= 80) b80++;
                else if (avg >= 70) b70++;
                else if (avg >= 60) b60++;
                else blow++;
            }

            var rows = new List<(string label, int count)>
            {
                ("90/100 - 100/100", b90),
                ("80/100 - 89/100", b80),
                ("70/100 - 79/100", b70),
                ("60/100 - 69/100", b60),
                ("< 60/100", blow)
            };

            return BuildPieSlices(rows);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Errore caricando KPI 'Fasce Valutazione'");
            return new List<PieSlice>();
        }
    }

    private static List<PieSlice> BuildPieSlices(List<(string label, int count)> rows)
    {
        var palette = new[]
        {
            "#198754", "#0d6efd", "#fd7e14", "#6f42c1", "#dc3545",
            "#20c997", "#6610f2", "#adb5bd", "#e67700", "#495057"
        };
        var total = rows.Sum(r => r.count);
        var result = new List<PieSlice>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.count <= 0) continue;
            result.Add(new PieSlice
            {
                Label = row.label,
                Count = row.count,
                Percentage = total > 0 ? row.count * 100.0 / total : 0.0,
                Color = palette[i % palette.Length]
            });
        }
        return result;
    }

    private static string FormatDurationFromMilliseconds(double milliseconds)
    {
        var safeMs = Math.Max(0, (long)Math.Round(milliseconds));
        var ts = TimeSpan.FromMilliseconds(safeMs);
        return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m {ts.Seconds:D2}s";
    }
}
