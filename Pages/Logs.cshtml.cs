using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TinyGenerator.Pages
{
    public class LogsModel : PageModel
    {
        private const string DbPath = "data/storage.db";

        public List<LogEntry> Logs { get; set; } = new List<LogEntry>();
        public int CurrentPage { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages => PageSize > 0 ? (int)System.Math.Ceiling((double)TotalCount / PageSize) : 1;

        public async Task OnGetAsync(int page = 1, int pageSize = 50, bool onlyErrors = false)
        {
            if (page < 1) page = 1;
            if (pageSize <= 0) pageSize = 50;
            CurrentPage = page;
            PageSize = pageSize;
            OnlyErrors = onlyErrors;
            await LoadLogsAsync(page, pageSize, onlyErrors);
        }

        public bool OnlyErrors { get; set; }

        public async Task<IActionResult> OnPostClearAsync()
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM logs;";
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // ignore errors
            }

            return RedirectToPage();
        }

        private async Task LoadLogsAsync(int page, int pageSize, bool onlyErrors)
        {
            Logs.Clear();
            try
            {
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                // compute total count for pagination
                using (var cntCmd = conn.CreateCommand())
                {
                    if (onlyErrors)
                    {
                        cntCmd.CommandText = "SELECT COUNT(*) FROM logs WHERE level = 'Error' OR level = 'ERROR' OR exception <> ''";
                    }
                    else
                    {
                        cntCmd.CommandText = "SELECT COUNT(*) FROM logs";
                    }
                    var cnt = await cntCmd.ExecuteScalarAsync();
                    TotalCount = cnt != null && cnt != System.DBNull.Value ? System.Convert.ToInt32(cnt) : 0;
                }

                var offset = (page - 1) * pageSize;
                if (onlyErrors)
                {
                    cmd.CommandText = "SELECT id, ts, level, category, message, exception, state FROM logs WHERE level = 'Error' OR level = 'ERROR' OR exception <> '' ORDER BY id DESC LIMIT @limit OFFSET @offset";
                }
                else
                {
                    cmd.CommandText = "SELECT id, ts, level, category, message, exception, state FROM logs ORDER BY id DESC LIMIT @limit OFFSET @offset";
                }
                cmd.Parameters.AddWithValue("@limit", pageSize);
                cmd.Parameters.AddWithValue("@offset", offset);
                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    Logs.Add(new LogEntry
                    {
                        Id = rdr.GetInt64(0),
                        Ts = FormatTimestamp(rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1)),
                        Level = rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2),
                        Category = rdr.IsDBNull(3) ? string.Empty : rdr.GetString(3),
                        Message = rdr.IsDBNull(4) ? string.Empty : rdr.GetString(4),
                        Exception = rdr.IsDBNull(5) ? string.Empty : rdr.GetString(5),
                        State = rdr.IsDBNull(6) ? string.Empty : rdr.GetString(6)
                    });
                }
            }
            catch
            {
                // ignore errors and leave Logs empty
            }
        }

        private string FormatTimestamp(string ts)
        {
            if (string.IsNullOrWhiteSpace(ts)) return string.Empty;
            // stored as ISO 'o' format in UTC (e.g. 2025-10-31T12:34:56.789Z)
            if (DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            {
                return dt.ToString("dd/MM/yyyy HH:mm:ss");
            }
            return ts;
        }

        public class LogEntry
        {
            public long Id { get; set; }
            public string Ts { get; set; } = string.Empty;
            public string Level { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public string Exception { get; set; } = string.Empty;
            public string State { get; set; } = string.Empty;
        }
    }
}
