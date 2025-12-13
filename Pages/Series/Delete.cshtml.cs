using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Series
{
    public class DeleteModel : PageModel
    {
        private readonly TinyGeneratorDbContext _context;

        public DeleteModel(TinyGeneratorDbContext context)
        {
            _context = context;
        }

        [BindProperty]
        public TinyGenerator.Models.Series SeriesItem { get; set; } = new();

        public IActionResult OnGet(int? id)
        {
            if (id == null || id.Value <= 0)
            {
                return NotFound();
            }

            var series = _context.Series.FirstOrDefault(s => s.Id == id.Value);
            if (series == null)
            {
                return NotFound();
            }

            SeriesItem = series;
            return Page();
        }

        public IActionResult OnPost(int? id)
        {
            if (id == null || id.Value <= 0)
            {
                return NotFound();
            }

            var series = _context.Series.FirstOrDefault(s => s.Id == id.Value);
            if (series == null)
            {
                return NotFound();
            }

            _context.Series.Remove(series);
            _context.SaveChanges();

            return RedirectToPage("./Index");
        }
    }
}
