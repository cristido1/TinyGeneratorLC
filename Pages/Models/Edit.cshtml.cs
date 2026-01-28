using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.Models
{
    public class EditModel : PageModel
    {
        private readonly DatabaseService _database;

        public EditModel(DatabaseService database)
        {
            _database = database;
        }

        [BindProperty]
        public ModelInfo Model { get; set; } = new ModelInfo();

        public IActionResult OnGet(int? id)
        {
            if (id.HasValue)
            {
                var existing = _database.ListModels().FirstOrDefault(m => m.Id.HasValue && m.Id.Value == id.Value);
                if (existing != null)
                {
                    Model = existing;
                }
                else
                {
                    // if id provided but not found, redirect to Index with error
                    TempData["ErrorMessage"] = $"Model id {id.Value} not found.";
                    return RedirectToPage("Index");
                }
            }
            else
            {
                Model = new ModelInfo { Enabled = true, Provider = "ollama" };
            }
            return Page();
        }

        public IActionResult OnPost()
        {
            if (Model == null)
            {
                ModelState.AddModelError(string.Empty, "Invalid model data");
                return Page();
            }

            if (string.IsNullOrWhiteSpace(Model.Name))
            {
                ModelState.AddModelError("Model.Name", "Name is required");
                return Page();
            }

            // Preserve any existing data not present in the edit form.
            // Prefer lookup by Id when available, fall back to name-based lookup to support older links.
            ModelInfo? existing = null;
            if (Model.Id.HasValue)
            {
                existing = _database.ListModels().FirstOrDefault(m => m.Id.HasValue && m.Id.Value == Model.Id.Value);
            }
            if (existing == null)
            {
                existing = _database.ListModels().FirstOrDefault(m => string.Equals(m.Name, Model.Name, StringComparison.OrdinalIgnoreCase));
            }
            if (existing == null)
            {
                existing = new ModelInfo();
            }

            // Update only allowed fields
            existing.Name = Model.Name;
            existing.Provider = Model.Provider;
            existing.Endpoint = Model.Endpoint;
            existing.MaxContext = Model.MaxContext;
            existing.ContextToUse = Model.ContextToUse;
            existing.CostInPerToken = Model.CostInPerToken;
            existing.CostOutPerToken = Model.CostOutPerToken;
            existing.LimitTokensDay = Model.LimitTokensDay;
            existing.LimitTokensWeek = Model.LimitTokensWeek;
            existing.LimitTokensMonth = Model.LimitTokensMonth;
            existing.Enabled = Model.Enabled;
            existing.NoTools = Model.NoTools;
            existing.Note = Model.Note;

            // Business rule: if provider is local then IsLocal = true
            if (!string.IsNullOrWhiteSpace(existing.Provider) &&
                (existing.Provider.Equals("ollama", System.StringComparison.OrdinalIgnoreCase) ||
                 existing.Provider.Equals("llama.cpp", System.StringComparison.OrdinalIgnoreCase)))
            {
                existing.IsLocal = true;
            }

            _database.UpsertModel(existing);

            TempData["Message"] = "Model saved";
            return RedirectToPage("Index");
        }
    }
}
