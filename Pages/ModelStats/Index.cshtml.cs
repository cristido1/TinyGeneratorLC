using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.ModelStats;

public class IndexModel : PageModel
{
    private readonly DatabaseService _database;

    public List<ModelStatsRow> Items { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public IndexModel(DatabaseService database)
    {
        _database = database;
    }

    public void OnGet()
    {
        var stats = _database.GetModelStats();
        Items = stats.Select(s => new ModelStatsRow(s)).ToList();
    }

    public IActionResult OnPostRecalculate()
    {
        try
        {
            _database.RefreshModelStatsFromAllLogs();
            StatusMessage = "Calcolo statistiche completato.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Calcolo statistiche fallito: {ex.Message}";
        }

        return RedirectToPage();
    }

    public sealed class ModelStatsRow
    {
        public string ModelName { get; }
        public string Operation { get; }
        public int CountUsed { get; }
        public int CountSuccessed { get; }
        public int CountFailed { get; }
        public double TotalSuccessTimeSecs { get; }
        public double TotalFailTimeSecs { get; }
        public int DurationTotalCount { get; }
        public double DurationTotalTime { get; }
        public int RuntimeTotalCount { get; }
        public long PromptEvalCountTotal { get; }
        public double PromptEvalDurationTotal { get; }
        public long EvalCountTotal { get; }
        public double EvalDurationTotal { get; }
        public double TotalDurationTotal { get; }
        public double LoadDurationTotal { get; }
        public int DoneStopCount { get; }
        public int DoneLengthCount { get; }
        public int DoneOtherCount { get; }
        public string FirstOperationDate { get; }
        public string LastOperationDate { get; }

        public double SuccessRate => CountUsed > 0 ? (double)CountSuccessed / CountUsed : 0;
        public double AvgSuccessTimeSecs => CountSuccessed > 0 ? TotalSuccessTimeSecs / CountSuccessed : 0;
        public double AvgFailTimeSecs => CountFailed > 0 ? TotalFailTimeSecs / CountFailed : 0;
        public string AvgSuccessDisplay => CountSuccessed > 0 ? AvgSuccessTimeSecs.ToString("F2") : "-";
        public string AvgFailDisplay => CountFailed > 0 ? AvgFailTimeSecs.ToString("F2") : "-";
        public string SuccessTimePerSuccessDisplay => CountSuccessed > 0
            ? $"{TotalSuccessTimeSecs:F2} / {CountSuccessed} = {AvgSuccessTimeSecs:F2}"
            : "-";
        public string FailTimePerFailDisplay => CountFailed > 0
            ? $"{TotalFailTimeSecs:F2} / {CountFailed} = {AvgFailTimeSecs:F2}"
            : "-";
        public double AvgDurationSecs => DurationTotalCount > 0 ? DurationTotalTime / DurationTotalCount : 0;
        public string AvgDurationSecsDisplay => DurationTotalCount > 0 ? AvgDurationSecs.ToString("F2") : "-";
        public string AvgDurationHumanDisplay => DurationTotalCount > 0 ? FormatHumanDuration(AvgDurationSecs) : "-";
        public string RuntimeSamplesDisplay => RuntimeTotalCount > 0 ? RuntimeTotalCount.ToString() : "-";
        public string AvgPromptTokensDisplay => RuntimeTotalCount > 0 ? ((double)PromptEvalCountTotal / RuntimeTotalCount).ToString("F1") : "-";
        public string AvgGenTokensDisplay => RuntimeTotalCount > 0 ? ((double)EvalCountTotal / RuntimeTotalCount).ToString("F1") : "-";
        public string AvgRuntimeTotalSecsDisplay => RuntimeTotalCount > 0 ? (TotalDurationTotal / RuntimeTotalCount).ToString("F2") : "-";
        public string AvgRuntimeLoadSecsDisplay => RuntimeTotalCount > 0 ? (LoadDurationTotal / RuntimeTotalCount).ToString("F2") : "-";
        public string DoneStopRateDisplay => RuntimeTotalCount > 0 ? $"{((double)DoneStopCount / RuntimeTotalCount * 100):F1}%" : "-";
        public string DoneLengthRateDisplay => RuntimeTotalCount > 0 ? $"{((double)DoneLengthCount / RuntimeTotalCount * 100):F1}%" : "-";

        public ModelStatsRow(ModelStatsRecord record)
        {
            ModelName = record.ModelName;
            Operation = record.Operation;
            CountUsed = record.CountUsed ?? 0;
            CountSuccessed = record.CountSuccessed ?? 0;
            CountFailed = record.CountFailed ?? 0;
            TotalSuccessTimeSecs = record.TotalSuccessTimeSecs ?? 0;
            TotalFailTimeSecs = record.TotalFailTimeSecs ?? 0;
            DurationTotalCount = record.DurationTotalCount ?? 0;
            DurationTotalTime = record.DurationTotalTime ?? 0;
            RuntimeTotalCount = record.RuntimeTotalCount ?? 0;
            PromptEvalCountTotal = record.PromptEvalCountTotal ?? 0;
            PromptEvalDurationTotal = record.PromptEvalDurationTotal ?? 0;
            EvalCountTotal = record.EvalCountTotal ?? 0;
            EvalDurationTotal = record.EvalDurationTotal ?? 0;
            TotalDurationTotal = record.TotalDurationTotal ?? 0;
            LoadDurationTotal = record.LoadDurationTotal ?? 0;
            DoneStopCount = record.DoneStopCount ?? 0;
            DoneLengthCount = record.DoneLengthCount ?? 0;
            DoneOtherCount = record.DoneOtherCount ?? 0;
            FirstOperationDate = FormatDate(record.FirstOperationDate);
            LastOperationDate = FormatDate(record.LastOperationDate);
        }

        private static string FormatDate(string? iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return "-";
            if (DateTime.TryParse(iso, out var dt))
            {
                return dt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            }
            return iso!;
        }

        private static string FormatHumanDuration(double seconds)
        {
            if (seconds <= 0) return "0s";
            var totalSeconds = (int)Math.Round(seconds);
            var minutes = totalSeconds / 60;
            var remaining = totalSeconds % 60;
            if (minutes > 0 && remaining > 0) return $"{minutes}m {remaining}s";
            if (minutes > 0) return $"{minutes}m";
            return $"{remaining}s";
        }
    }
}
