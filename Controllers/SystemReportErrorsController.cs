using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;

namespace TinyGenerator.Controllers;

[ApiController]
[Route("api/errors")]
public sealed class SystemReportErrorsController : ControllerBase
{
    private readonly DatabaseService _database;

    public SystemReportErrorsController(DatabaseService database)
    {
        _database = database;
    }

    [HttpGet]
    public IActionResult List([FromQuery] bool includeResolved = false, [FromQuery] int limit = 500)
    {
        var items = _database.ListSystemReportErrors(includeResolved, limit);
        return Ok(new
        {
            success = true,
            items,
            count = items.Count
        });
    }

    [HttpPost("extract")]
    public IActionResult Extract([FromBody] ExtractErrorsRequest? request)
    {
        var maxRows = request?.MaxRows ?? 500;
        var result = _database.ProcessUnextractedErrors(maxRows);
        return Ok(new
        {
            success = true,
            scanned = result.Scanned,
            linked = result.Linked,
            inserted = result.Inserted,
            updated = result.Updated,
            unknownType = result.UnknownType
        });
    }

    [HttpPost("{id:int}/status")]
    public IActionResult UpdateStatus([FromRoute] int id, [FromBody] UpdateErrorStatusRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Status))
        {
            return BadRequest(new { success = false, error = "Status obbligatorio." });
        }

        var ok = _database.UpdateSystemReportErrorStatus(id, request.Status);
        if (!ok)
        {
            return BadRequest(new { success = false, error = "Status non valido o record non trovato." });
        }

        return Ok(new { success = true, id, status = request.Status.Trim().ToLowerInvariant() });
    }

    [HttpPost("{id:int}/send-to-github")]
    public IActionResult SendToGitHub([FromRoute] int id, [FromBody] SendToGitHubRequest? request)
    {
        // In assenza di una integrazione GitHub centralizzata nel progetto,
        // questo endpoint registra l'IssueId esterno fornito dal chiamante.
        var issueId = request?.GitHubIssueId ?? 0;
        if (issueId <= 0)
        {
            return BadRequest(new { success = false, error = "GitHubIssueId obbligatorio (> 0)." });
        }

        var ok = _database.UpdateSystemReportErrorGitHubIssueId(id, issueId);
        if (!ok)
        {
            return NotFound(new { success = false, error = "Record non trovato." });
        }

        return Ok(new { success = true, id, githubIssueId = issueId });
    }
}

public sealed class ExtractErrorsRequest
{
    public int? MaxRows { get; set; }
}

public sealed class UpdateErrorStatusRequest
{
    public string? Status { get; set; }
}

public sealed class SendToGitHubRequest
{
    public int? GitHubIssueId { get; set; }
}
