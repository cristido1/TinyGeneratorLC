using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Controllers;

[ApiController]
[Route("api/tipo-planning")]
public class TipoPlanningApiController : ControllerBase
{
    private static readonly HashSet<string> AllowedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "AZIONE",
        "STASI",
        "ERRORE",
        "EFFETTO"
    };

    private readonly TinyGeneratorDbContext _context;
    private readonly ILogger<TipoPlanningApiController> _logger;

    public TipoPlanningApiController(TinyGeneratorDbContext context, ILogger<TipoPlanningApiController> logger)
    {
        _context = context;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        try
        {
            var items = _context.TipoPlannings
                .OrderByDescending(t => t.IsActive)
                .ThenBy(t => t.Nome)
                .ToList();
            return Ok(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving tipo_planning");
            return StatusCode(500, new { error = "Errore durante il recupero dei tipi planning" });
        }
    }

    [HttpGet("active")]
    public IActionResult GetActive()
    {
        try
        {
            var items = _context.TipoPlannings
                .Where(t => t.IsActive)
                .OrderBy(t => t.Nome)
                .ToList();
            return Ok(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving active tipo_planning");
            return StatusCode(500, new { error = "Errore durante il recupero dei tipi planning attivi" });
        }
    }

    [HttpGet("{id:int}")]
    public IActionResult GetById(int id)
    {
        try
        {
            var item = _context.TipoPlannings.Find(id);
            if (item == null) return NotFound(new { error = "Tipo planning non trovato" });
            return Ok(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving tipo_planning {Id}", id);
            return StatusCode(500, new { error = "Errore durante il recupero del tipo planning" });
        }
    }

    [HttpPost]
    public IActionResult Create([FromBody] TipoPlanning item)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            NormalizeAndValidate(item, out var error);
            if (error != null) return BadRequest(new { error });

            if (_context.TipoPlannings.Any(t => t.Codice == item.Codice))
                return BadRequest(new { error = $"Esiste già un tipo planning con codice '{item.Codice}'" });

            _context.TipoPlannings.Add(item);
            _context.SaveChanges();

            _logger.LogInformation("Created tipo_planning {Id}: {Nome}", item.Id, item.Nome);
            return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating tipo_planning");
            return StatusCode(500, new { error = "Errore durante la creazione del tipo planning" });
        }
    }

    [HttpPut("{id:int}")]
    public IActionResult Update(int id, [FromBody] TipoPlanning updated)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            NormalizeAndValidate(updated, out var error);
            if (error != null) return BadRequest(new { error });

            var existing = _context.TipoPlannings.Find(id);
            if (existing == null) return NotFound(new { error = "Tipo planning non trovato" });

            if (_context.TipoPlannings.Any(t => t.Codice == updated.Codice && t.Id != id))
                return BadRequest(new { error = $"Esiste già un altro tipo planning con codice '{updated.Codice}'" });

            existing.Codice = updated.Codice;
            existing.Nome = updated.Nome;
            existing.Descrizione = updated.Descrizione;
            existing.SuccessioneStati = updated.SuccessioneStati;
            existing.IsActive = updated.IsActive;

            _context.SaveChanges();

            _logger.LogInformation("Updated tipo_planning {Id}: {Nome}", id, existing.Nome);
            return Ok(existing);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating tipo_planning {Id}", id);
            return StatusCode(500, new { error = "Errore durante l'aggiornamento del tipo planning" });
        }
    }

    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id)
    {
        try
        {
            var existing = _context.TipoPlannings.Find(id);
            if (existing == null) return NotFound(new { error = "Tipo planning non trovato" });

            _context.TipoPlannings.Remove(existing);
            _context.SaveChanges();

            _logger.LogInformation("Deleted tipo_planning {Id}: {Nome}", id, existing.Nome);
            return Ok(new { message = "Tipo planning eliminato con successo" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting tipo_planning {Id}", id);
            return StatusCode(500, new { error = "Errore durante l'eliminazione del tipo planning" });
        }
    }

    [HttpPost("{id:int}/toggle-active")]
    public IActionResult ToggleActive(int id)
    {
        try
        {
            var existing = _context.TipoPlannings.Find(id);
            if (existing == null) return NotFound(new { error = "Tipo planning non trovato" });

            existing.IsActive = !existing.IsActive;
            _context.SaveChanges();

            _logger.LogInformation("Toggled active status for tipo_planning {Id} to {IsActive}", id, existing.IsActive);
            return Ok(new { isActive = existing.IsActive });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling active status for tipo_planning {Id}", id);
            return StatusCode(500, new { error = "Errore durante il cambio di stato" });
        }
    }

    private static void NormalizeAndValidate(TipoPlanning item, out string? error)
    {
        error = null;

        item.Codice = (item.Codice ?? string.Empty).Trim();
        item.Nome = (item.Nome ?? string.Empty).Trim();
        item.Descrizione = string.IsNullOrWhiteSpace(item.Descrizione) ? null : item.Descrizione.Trim();
        item.SuccessioneStati = (item.SuccessioneStati ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(item.Codice))
        {
            error = "Il codice è obbligatorio";
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Nome))
        {
            error = "Il nome è obbligatorio";
            return;
        }

        if (string.IsNullOrWhiteSpace(item.SuccessioneStati))
        {
            error = "La successione stati è obbligatoria";
            return;
        }

        var parts = item.SuccessioneStati
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant())
            .ToList();

        if (parts.Count == 0)
        {
            error = "La successione stati è vuota";
            return;
        }

        var invalid = parts.FirstOrDefault(p => !AllowedStates.Contains(p));
        if (invalid != null)
        {
            error = $"Stato non ammesso nella successione: '{invalid}'. Ammessi: AZIONE, STASI, ERRORE, EFFETTO";
            return;
        }

        item.SuccessioneStati = string.Join(",", parts);
    }
}
