using System;
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

        public IReadOnlyList<TinyGenerator.Models.Series> Items { get; set; } = Array.Empty<TinyGenerator.Models.Series>();
        public int PageIndex { get; set; } = 1;
        public int PageSize { get; set; } = 25;
        public int TotalCount { get; set; }
        public string? Search { get; set; }
        public string? OrderBy { get; set; }

        public void OnGet()
        {
            if (int.TryParse(Request.Query["page"], out var p) && p > 0) PageIndex = p;
            if (int.TryParse(Request.Query["pageSize"], out var ps) && ps > 0) PageSize = ps;
            Search = string.IsNullOrWhiteSpace(Request.Query["search"]) ? null : Request.Query["search"].ToString();
            OrderBy = string.IsNullOrWhiteSpace(Request.Query["orderBy"]) ? null : Request.Query["orderBy"].ToString();

            LoadData();
        }

        public IEnumerable<RowAction> GetActionsForSeries(TinyGenerator.Models.Series s)
        {
            if (s == null) yield break;
            yield return new RowAction("edit", "Modifica", "GET", Url.Page("./Edit", new { id = s.Id }) ?? $"/Series/Edit?id={s.Id}");
            yield return new RowAction("delete", "Elimina", "GET", Url.Page("./Delete", new { id = s.Id }) ?? $"/Series/Delete?id={s.Id}", confirm: true);
        }

        private void LoadData()
        {
            IQueryable<TinyGenerator.Models.Series> query = _context.Series
                .AsNoTracking()
                .Include(s => s.Characters)
                .Include(s => s.Episodes);

            if (!string.IsNullOrWhiteSpace(Search))
            {
                var q = Search.Trim();
                query = query.Where(s =>
                    s.Titolo.Contains(q) ||
                    (s.Genere ?? string.Empty).Contains(q) ||
                    (s.Sottogenere ?? string.Empty).Contains(q) ||
                    (s.PeriodoNarrativo ?? string.Empty).Contains(q) ||
                    (s.TonoBase ?? string.Empty).Contains(q) ||
                    (s.Target ?? string.Empty).Contains(q) ||
                    (s.Lingua ?? string.Empty).Contains(q));
            }

            TotalCount = query.Count();

            var order = OrderBy?.Trim();
            bool desc = false;
            if (!string.IsNullOrWhiteSpace(order) && order.EndsWith("_desc", StringComparison.OrdinalIgnoreCase))
            {
                desc = true;
                order = order.Substring(0, order.Length - 5);
            }

            query = (order ?? string.Empty).ToLowerInvariant() switch
            {
                "titolo" => desc ? query.OrderByDescending(s => s.Titolo) : query.OrderBy(s => s.Titolo),
                "genere" => desc ? query.OrderByDescending(s => s.Genere) : query.OrderBy(s => s.Genere),
                "sottogenere" => desc ? query.OrderByDescending(s => s.Sottogenere) : query.OrderBy(s => s.Sottogenere),
                "periodo" => desc ? query.OrderByDescending(s => s.PeriodoNarrativo) : query.OrderBy(s => s.PeriodoNarrativo),
                "tono" => desc ? query.OrderByDescending(s => s.TonoBase) : query.OrderBy(s => s.TonoBase),
                "target" => desc ? query.OrderByDescending(s => s.Target) : query.OrderBy(s => s.Target),
                "lingua" => desc ? query.OrderByDescending(s => s.Lingua) : query.OrderBy(s => s.Lingua),
                "episodi" => desc ? query.OrderByDescending(s => s.EpisodiGenerati) : query.OrderBy(s => s.EpisodiGenerati),
                "data" => desc ? query.OrderByDescending(s => s.DataInserimento) : query.OrderBy(s => s.DataInserimento),
                _ => query.OrderByDescending(s => s.DataInserimento)
            };

            var skip = (PageIndex - 1) * PageSize;
            Items = query.Skip(skip).Take(PageSize).ToList();
        }
    }

    public sealed class RowAction
    {
        public RowAction(string id, string title, string method, string url, bool confirm = false)
        {
            Id = id;
            Title = title;
            Method = method;
            Url = url;
            Confirm = confirm;
        }

        public string Id { get; }
        public string Title { get; }
        public string Method { get; }
        public string Url { get; }
        public bool Confirm { get; }
    }
}
