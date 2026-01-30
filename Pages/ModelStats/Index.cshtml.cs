using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.ModelStats;

public class IndexModel : PageModel
{
    private readonly DatabaseService _database;

    public List<ModelStatsRow> Items { get; set; } = new();

    public IndexModel(DatabaseService database)
    {
        _database = database;
    }

    public void OnGet()
    {
        var stats = _database.GetModelStats();
        Items = stats.Select(s => new ModelStatsRow(s)).ToList();
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

        public ModelStatsRow(ModelStatsRecord record)
        {
            ModelName = record.ModelName;
            Operation = record.Operation;
            CountUsed = record.CountUsed ?? 0;
            CountSuccessed = record.CountSuccessed ?? 0;
            CountFailed = record.CountFailed ?? 0;
            TotalSuccessTimeSecs = record.TotalSuccessTimeSecs ?? 0;
            TotalFailTimeSecs = record.TotalFailTimeSecs ?? 0;
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
    }
}
