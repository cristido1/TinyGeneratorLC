using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;

namespace TinyGenerator.Pages;

[IgnoreAntiforgeryToken]
public sealed class ExternalServicesMonitorModel : PageModel
{
    private readonly ExternalServicesMonitorService _monitor;

    public ExternalServicesMonitorModel(ExternalServicesMonitorService monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    public IReadOnlyList<ExternalServicesMonitorService.ExternalServiceStatus> Services { get; private set; }
        = Array.Empty<ExternalServicesMonitorService.ExternalServiceStatus>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Services = await _monitor.GetAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IActionResult> OnGetStatusesAsync(CancellationToken cancellationToken)
    {
        var services = await _monitor.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return new JsonResult(services);
    }
}
