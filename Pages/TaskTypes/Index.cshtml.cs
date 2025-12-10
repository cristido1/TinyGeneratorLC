using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.TaskTypes
{
    public class IndexModel : PageModel
    {
        private readonly DatabaseService _database;

        public IndexModel(DatabaseService database)
        {
            _database = database;
        }

        public List<TaskTypeInfo> TaskTypes { get; set; } = new List<TaskTypeInfo>();

        [BindProperty]
        public TaskTypeInfo? FormModel { get; set; }

        public void OnGet()
        {
            TaskTypes = _database.ListTaskTypes();
        }

        public IActionResult OnPostSave()
        {
            if (FormModel == null || string.IsNullOrWhiteSpace(FormModel.Code))
            {
                TempData["Error"] = "Invalid task type code.";
                return RedirectToPage();
            }

            try
            {
                _database.UpsertTaskType(FormModel);
                TempData["Message"] = "Task type saved.";
            }
            catch (System.Exception ex)
            {
                TempData["Error"] = "Save failed: " + ex.Message;
            }
            return RedirectToPage();
        }

        public IActionResult OnPostDelete(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                TempData["Error"] = "Invalid code";
                return RedirectToPage();
            }

            try
            {
                _database.DeleteTaskTypeByCode(code);
                TempData["Message"] = "Task type deleted.";
            }
            catch (System.Exception ex)
            {
                TempData["Error"] = "Delete failed: " + ex.Message;
            }
            return RedirectToPage();
        }
    }
}
