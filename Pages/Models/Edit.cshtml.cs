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

        public IActionResult OnGet(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                var existing = _database.GetModelInfo(name);
                if (existing != null)
                {
                    Model = existing;
                }
                else
                {
                    // prefill provider default
                    Model = new ModelInfo { Name = name, Provider = "ollama", Enabled = true };
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

            // Preserve any existing data not present in the edit form
            var existing = _database.GetModelInfo(Model.Name);
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

            // Business rule: if provider == "ollama" then IsLocal = true
            if (!string.IsNullOrWhiteSpace(existing.Provider) && existing.Provider.Equals("ollama", System.StringComparison.OrdinalIgnoreCase))
            {
                existing.IsLocal = true;
            }

            _database.UpsertModel(existing);

            TempData["Message"] = "Model saved";
            return RedirectToPage("Index");
        }
    }
}
