using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Series
{
    public class IndexModel : PageModel
    {
        private readonly TinyGeneratorDbContext _context;

        public IndexModel(TinyGeneratorDbContext context)
        {
            _context = context;
        }

        public List<TinyGenerator.Models.Series> SeriesList { get; set; } = new();

        public void OnGet()
        {
            SeriesList = _context.Series.OrderByDescending(s => s.DataInserimento).ToList();
        }
    }
}
