using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;

namespace TinyGenerator.Controllers;

[ApiController]
[Route("api/sounds")]
public sealed class SoundsApiController : ControllerBase
{
    private readonly DatabaseService _database;

    public SoundsApiController(DatabaseService database)
    {
        _database = database;
    }

    [HttpPost("datatable")]
    public IActionResult DataTablePage()
    {
        try
        {
            var form = Request.HasFormContentType ? Request.Form : null;
            var draw = ParseInt(form?["draw"], 0);
            var start = ParseInt(form?["start"], 0);
            var length = ParseInt(form?["length"], 25);
            var search = (form?["search[value]"].ToString() ?? string.Empty).Trim();
            var typeFilter = (form?["typeFilter"].ToString() ?? string.Empty).Trim();

            var orderColumnIndex = ParseInt(form?["order[0][column]"], -1);
            var orderDirRaw = (form?["order[0][dir]"].ToString() ?? "desc").Trim();
            var sortDesc = !string.Equals(orderDirRaw, "asc", StringComparison.OrdinalIgnoreCase);

            string? sortColumn = null;
            if (orderColumnIndex >= 0)
            {
                sortColumn = (form?[$"columns[{orderColumnIndex}][data]"].ToString() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(sortColumn))
                {
                    sortColumn = (form?[$"columns[{orderColumnIndex}][name]"].ToString() ?? string.Empty).Trim();
                }
            }

            var normalizedType = NormalizeType(typeFilter);
            var page = _database.ListSoundsPaged(
                start: start,
                length: length,
                search: string.IsNullOrWhiteSpace(search) ? null : search,
                type: normalizedType,
                sortColumn: sortColumn,
                sortDesc: sortDesc);

            return Ok(new
            {
                draw,
                recordsTotal = page.Total,
                recordsFiltered = page.Filtered,
                data = page.Items
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                error = ex.Message,
                draw = ParseInt(Request.HasFormContentType ? Request.Form["draw"] : default, 0),
                recordsTotal = 0,
                recordsFiltered = 0,
                data = Array.Empty<object>()
            });
        }
    }

    private static int ParseInt(Microsoft.Extensions.Primitives.StringValues? value, int fallback)
    {
        if (!value.HasValue || value.Value.Count == 0) return fallback;
        return int.TryParse(value.Value.ToString(), out var i) ? i : fallback;
    }

    private static string? NormalizeType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim().ToLowerInvariant();
        return t is "fx" or "amb" or "music" ? t : null;
    }
}
