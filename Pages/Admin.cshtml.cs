using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;
using CallRecord = TinyGenerator.Services.CostController.CallRecord;

namespace TinyGenerator.Pages
{
    public class AdminModel : PageModel
    {
        private readonly CostController _cost;

        public AdminModel(CostController cost)
        {
            _cost = cost;
        }

    public long TokensThisMonth { get; private set; }
    public double CostThisMonth { get; private set; }
    public IEnumerable<CallRecord> Calls { get; private set; } = Enumerable.Empty<CallRecord>();

        public void OnGet()
        {
            var usage = _cost.GetMonthUsage();
            TokensThisMonth = usage.tokensThisMonth;
            CostThisMonth = usage.costThisMonth;
            Calls = _cost.GetRecentCalls(50);
        }
    }
}
