using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.Utilities.SQL;

public class IndexModel : PageModel
{
    private readonly DatabaseService _database;

    public IndexModel(DatabaseService database)
    {
        _database = database;
    }

    [BindProperty]
    public string SqlText { get; set; } = string.Empty;

    public void OnGet()
    {
    }

    public IActionResult OnGetSchema()
    {
        var schema = _database.GetSqlUtilitySchema();
        return new JsonResult(schema);
    }

    public IActionResult OnGetSelectTable([FromQuery] string table, [FromQuery] int limit = 500)
    {
        var schema = _database.GetSqlUtilitySchema();
        var exists = schema.Tables.Any(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            return new JsonResult(new { success = false, error = $"Tabella non trovata: {table}" });
        }

        var safeTable = table.Replace("\"", "\"\"");
        var sql = $"SELECT * FROM \"{safeTable}\"";
        return new JsonResult(_database.ExecuteSqlUtility(sql, rowLimit: Math.Max(1, Math.Min(5000, limit))));
    }

    public IActionResult OnPostExecute([FromBody] SqlExecuteRequest? request)
    {
        if (request == null)
        {
            return new JsonResult(new { success = false, error = "Request non valida." });
        }

        var result = _database.ExecuteSqlUtility(request.Sql ?? string.Empty, rowLimit: request.RowLimit <= 0 ? 1000 : request.RowLimit);
        return new JsonResult(result);
    }

    public sealed class SqlExecuteRequest
    {
        public string? Sql { get; set; }
        public int RowLimit { get; set; } = 1000;
    }
}
