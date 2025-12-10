using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages
{
    public class AdminModel : PageModel
    {
        private readonly DatabaseService _database;

        public AdminModel(DatabaseService database)
        {
            _database = database;
        }

    public long TokensThisMonth { get; private set; }
    public double CostThisMonth { get; private set; }
        public void OnGet()
        {
            var usage = _database.GetMonthUsage(DateTime.UtcNow.ToString("yyyy-MM"));
            TokensThisMonth = usage.tokensThisMonth;
            CostThisMonth = usage.costThisMonth;
        }
    }
}
