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

        public IReadOnlyList<TaskTypeInfo> Items { get; set; } = Array.Empty<TaskTypeInfo>();
        public int PageIndex { get; set; } = 1;
        public int PageSize { get; set; } = 25;
        public int TotalCount { get; set; }
        public string? Search { get; set; }
        public string? OrderBy { get; set; }

        [BindProperty]
        public TaskTypeInfo? FormModel { get; set; }

        public void OnGet()
        {
            if (int.TryParse(Request.Query["page"], out var p) && p > 0) PageIndex = p;
            if (int.TryParse(Request.Query["pageSize"], out var ps) && ps > 0) PageSize = ps;
            Search = string.IsNullOrWhiteSpace(Request.Query["search"]) ? null : Request.Query["search"].ToString();
            OrderBy = string.IsNullOrWhiteSpace(Request.Query["orderBy"]) ? null : Request.Query["orderBy"].ToString();

            var (items, total) = _database.GetPagedTaskTypes(PageIndex, PageSize, Search, OrderBy);
            Items = items;
            TotalCount = total;
        }

        public IEnumerable<object> GetActionsForTaskType(TaskTypeInfo item)
        {
            return GetActionList(item);
        }

        public sealed class RowAction
        {
            public string Id { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Method { get; set; } = "GET";
            public string Url { get; set; } = string.Empty;
            public bool Confirm { get; set; }
            public Dictionary<string, string> Fields { get; set; } = new();
        }

        public IEnumerable<RowAction> GetActionList(TaskTypeInfo item)
        {
            return new List<RowAction>
            {
                new RowAction { Id = "details", Title = "Details", Method = "CLIENT", Url = "#" },
                new RowAction { Id = "edit", Title = "Edit", Method = "CLIENT", Url = "#" },
                new RowAction
                {
                    Id = "delete",
                    Title = "Delete",
                    Method = "POST",
                    Url = "?handler=Delete",
                    Confirm = true,
                    Fields = new Dictionary<string, string> { ["code"] = item.Code ?? string.Empty }
                }
            };
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
