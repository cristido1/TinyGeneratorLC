using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Controllers;

[ApiController]
[Route("api/planner-methods")]
public class PlannerMethodsApiController : ControllerBase
{
    private readonly TinyGeneratorDbContext _context;
    private readonly ILogger<PlannerMethodsApiController> _logger;

    public PlannerMethodsApiController(TinyGeneratorDbContext context, ILogger<PlannerMethodsApiController> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get all planner methods
    /// </summary>
    [HttpGet]
    public IActionResult GetAll()
    {
        try
        {
            var methods = _context.PlannerMethods
                .OrderBy(m => m.Code)
                .ToList();
            return Ok(methods);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving planner methods");
            return StatusCode(500, new { error = "Errore durante il recupero dei metodi di pianificazione" });
        }
    }

    /// <summary>
    /// Get active planner methods only
    /// </summary>
    [HttpGet("active")]
    public IActionResult GetActive()
    {
        try
        {
            var methods = _context.PlannerMethods
                .Where(m => m.IsActive)
                .OrderBy(m => m.Code)
                .ToList();
            return Ok(methods);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving active planner methods");
            return StatusCode(500, new { error = "Errore durante il recupero dei metodi attivi" });
        }
    }

    /// <summary>
    /// Get planner method by ID
    /// </summary>
    [HttpGet("{id:int}")]
    public IActionResult GetById(int id)
    {
        try
        {
            var method = _context.PlannerMethods.Find(id);
            if (method == null)
                return NotFound(new { error = "Metodo non trovato" });

            return Ok(method);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving planner method {Id}", id);
            return StatusCode(500, new { error = "Errore durante il recupero del metodo" });
        }
    }

    /// <summary>
    /// Create new planner method
    /// </summary>
    [HttpPost]
    public IActionResult Create([FromBody] PlannerMethod method)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            // Check for duplicate code
            if (_context.PlannerMethods.Any(m => m.Code == method.Code))
                return BadRequest(new { error = $"Esiste già un metodo con codice '{method.Code}'" });

            _context.PlannerMethods.Add(method);
            _context.SaveChanges();

            _logger.LogInformation("Created planner method {Id}: {Code}", method.Id, method.Code);
            return CreatedAtAction(nameof(GetById), new { id = method.Id }, method);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating planner method");
            return StatusCode(500, new { error = "Errore durante la creazione del metodo" });
        }
    }

    /// <summary>
    /// Update existing planner method
    /// </summary>
    [HttpPut("{id:int}")]
    public IActionResult Update(int id, [FromBody] PlannerMethod updatedMethod)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var existingMethod = _context.PlannerMethods.Find(id);
            if (existingMethod == null)
                return NotFound(new { error = "Metodo non trovato" });

            // Check for duplicate code (excluding current record)
            if (_context.PlannerMethods.Any(m => m.Code == updatedMethod.Code && m.Id != id))
                return BadRequest(new { error = $"Esiste già un altro metodo con codice '{updatedMethod.Code}'" });

            // Update properties
            existingMethod.Code = updatedMethod.Code;
            existingMethod.Description = updatedMethod.Description;
            existingMethod.Notes = updatedMethod.Notes;
            existingMethod.IsActive = updatedMethod.IsActive;

            _context.SaveChanges();

            _logger.LogInformation("Updated planner method {Id}: {Code}", id, existingMethod.Code);
            return Ok(existingMethod);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating planner method {Id}", id);
            return StatusCode(500, new { error = "Errore durante l'aggiornamento del metodo" });
        }
    }

    /// <summary>
    /// Delete planner method
    /// </summary>
    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id)
    {
        try
        {
            var method = _context.PlannerMethods.Find(id);
            if (method == null)
                return NotFound(new { error = "Metodo non trovato" });

            _context.PlannerMethods.Remove(method);
            _context.SaveChanges();

            _logger.LogInformation("Deleted planner method {Id}: {Code}", id, method.Code);
            return Ok(new { message = "Metodo eliminato con successo" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting planner method {Id}", id);
            return StatusCode(500, new { error = "Errore durante l'eliminazione del metodo" });
        }
    }

    /// <summary>
    /// Toggle active status
    /// </summary>
    [HttpPost("{id:int}/toggle-active")]
    public IActionResult ToggleActive(int id)
    {
        try
        {
            var method = _context.PlannerMethods.Find(id);
            if (method == null)
                return NotFound(new { error = "Metodo non trovato" });

            method.IsActive = !method.IsActive;
            _context.SaveChanges();

            _logger.LogInformation("Toggled active status for planner method {Id} to {IsActive}", id, method.IsActive);
            return Ok(new { isActive = method.IsActive });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling active status for planner method {Id}", id);
            return StatusCode(500, new { error = "Errore durante il cambio di stato" });
        }
    }
}
