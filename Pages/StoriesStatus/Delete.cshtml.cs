using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.StoriesStatus
{
    public class DeleteModel : PageModel
    {
        private readonly DatabaseService _db;
        public DeleteModel(DatabaseService db)
        {
            _db = db;
        }

        [BindProperty]
        public StoryStatus? Status { get; set; }

        public void OnGet(int id)
        {
            Status = _db.GetStoryStatusById(id);
        }

        public IActionResult OnPost(int id)
        {
            _db.DeleteStoryStatus(id);
            return RedirectToPage("./Index");
        }
    }
}