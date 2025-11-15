using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.TestDefinitions
{
    public class EditModel : PageModel
    {
        private readonly DatabaseService _db;
        public List<string> PlanFiles { get; set; } = new List<string>();
        public string[] AvailablePlugins { get; set; } = new string[] { "text", "math", "time", "filesystem", "http", "memory", "audiocraft", "audioevaluator", "tts", "evaluator", "story" };
        public string[] TestTypes { get; set; } = new string[] { "functioncall", "writer", "question" };
        public EditModel(DatabaseService db)
        {
            _db = db;
        }

        [BindProperty]
        public TestDefinition Definition { get; set; } = new TestDefinition();

        [BindProperty]
        public string[] SelectedAllowedPlugins { get; set; } = new string[] { };

        public void OnGet(int? id)
        {
            try
            {
                var dir = Path.Combine(Directory.GetCurrentDirectory(), "execution_plans");
                if (Directory.Exists(dir))
                {
                    var files = Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).ToList();
                    PlanFiles = files;
                }
            }
            catch { }
            if (id.HasValue && id.Value > 0)
            {
                var td = _db.GetTestDefinitionById(id.Value);
                if (td != null) Definition = td;
                // populate selected allowed plugins array for the multi-select
                if (!string.IsNullOrWhiteSpace(Definition.AllowedPlugins)) SelectedAllowedPlugins = Definition.AllowedPlugins.Split(',').Select(p => p.Trim()).ToArray();
            }
            else
            {
                // set defaults for new
                Definition.TestType ??= "functioncall";
            }
        }

        public IActionResult OnPost()
        {
            if (!ModelState.IsValid) return Page();

            // Convert SelectedAllowedPlugins into comma-separated string (or null if none selected)
            if (SelectedAllowedPlugins != null && SelectedAllowedPlugins.Any())
            {
                Definition.AllowedPlugins = string.Join(",", SelectedAllowedPlugins.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
            }
            else
            {
                Definition.AllowedPlugins = null;
            }

            if (Definition.Id > 0)
            {
                _db.UpdateTestDefinition(Definition);
            }
            else
            {
                _db.InsertTestDefinition(Definition);
            }

            return RedirectToPage("./Index");
        }
    }
}
