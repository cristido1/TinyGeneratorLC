using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.StepTemplates
{
    public class EditModel : PageModel
    {
        private readonly DatabaseService _database;

        [BindProperty]
        public StepTemplate Template { get; set; } = new();

        public EditModel(DatabaseService database)
        {
            _database = database;
        }

        public IActionResult OnGet(int id)
        {
            var template = _database.GetStepTemplateById(id);
            if (template == null)
            {
                return RedirectToPage("./Index");
            }
            Template = template;
            return Page();
        }

        public IActionResult OnPost()
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            try
            {
                Template.UpdatedAt = DateTime.UtcNow.ToString("o");
                _database.UpdateStepTemplate(Template);
                TempData["ToastMessage"] = "Template updated successfully";
                TempData["ToastLevel"] = "success";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                return Page();
            }
        }
    }
}
