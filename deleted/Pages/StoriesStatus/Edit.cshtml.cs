using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.StoriesStatus
{
    public class EditModel : PageModel
    {
        private readonly DatabaseService _db;

        public EditModel(DatabaseService db)
        {
            _db = db;
        }

        [BindProperty]
        public StoryStatus Status { get; set; } = new StoryStatus();

        public void OnGet(int? id)
        {
            if (id.HasValue && id.Value > 0)
            {
                var status = _db.GetStoryStatusById(id.Value);
                if (status != null) Status = status;
            }
        }

        public IActionResult OnPost()
        {
            if (!ModelState.IsValid) return Page();

            if (Status.Id > 0)
            {
                _db.UpdateStoryStatus(Status);
            }
            else
            {
                _db.InsertStoryStatus(Status);
            }

            return RedirectToPage("./Index");
        }
    }
}