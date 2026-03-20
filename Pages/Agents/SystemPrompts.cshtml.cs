using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Agents;

public class SystemPromptsModel : PageModel
{
    private readonly TinyGeneratorDbContext _context;
    private readonly IWebHostEnvironment _env;

    public SystemPromptsModel(TinyGeneratorDbContext context, IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
    }

    public List<Agent> Agents { get; set; } = new();

    public async Task OnGetAsync()
    {
        Agents = await _context.Agents
            .OrderBy(a => a.Description)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<IActionResult> OnGetJsonSchemaAsync(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return BadRequest("Filename mancante.");

        // Security: allow only .json files with no path traversal
        var safeName = Path.GetFileName(filename);
        if (!safeName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Tipo file non supportato.");

        var filePath = Path.Combine(_env.ContentRootPath, "response_formats", safeName);
        if (!System.IO.File.Exists(filePath))
            return NotFound("Schema non trovato.");

        var content = await System.IO.File.ReadAllTextAsync(filePath);
        return Content(content, "application/json");
    }
}
