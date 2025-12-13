using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Series
{
    public class EditModel : PageModel
    {
        private readonly TinyGeneratorDbContext _context;

        public EditModel(TinyGeneratorDbContext context)
        {
            _context = context;
        }

        [BindProperty]
        public TinyGenerator.Models.Series SeriesItem { get; set; } = new();

        public bool IsEditMode => SeriesItem?.Id > 0;

        public IActionResult OnGet(int? id)
        {
            if (id.HasValue && id.Value > 0)
            {
                var series = _context.Series.FirstOrDefault(s => s.Id == id.Value);
                if (series == null)
                {
                    return NotFound();
                }
                SeriesItem = series;
            }
            else
            {
                // New series
                SeriesItem = new TinyGenerator.Models.Series
                {
                    DataInserimento = DateTime.UtcNow,
                    EpisodiGenerati = 0,
                    Lingua = "Italiano"
                };
            }

            return Page();
        }

        public IActionResult OnPost()
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            if (SeriesItem.Id > 0)
            {
                // Update
                var existing = _context.Series.FirstOrDefault(s => s.Id == SeriesItem.Id);
                if (existing == null)
                {
                    return NotFound();
                }
                existing.Titolo = SeriesItem.Titolo;
                existing.Genere = SeriesItem.Genere;
                existing.Sottogenere = SeriesItem.Sottogenere;
                existing.PeriodoNarrativo = SeriesItem.PeriodoNarrativo;
                existing.TonoBase = SeriesItem.TonoBase;
                existing.Target = SeriesItem.Target;
                existing.Lingua = SeriesItem.Lingua;
                existing.AmbientazioneBase = SeriesItem.AmbientazioneBase;
                existing.PremessaSerie = SeriesItem.PremessaSerie;
                existing.ArcoNarrativoSerie = SeriesItem.ArcoNarrativoSerie;
                existing.StileScrittura = SeriesItem.StileScrittura;
                existing.RegoleNarrative = SeriesItem.RegoleNarrative;
                existing.NoteAI = SeriesItem.NoteAI;
                existing.EpisodiGenerati = SeriesItem.EpisodiGenerati;
                existing.DataInserimento = SeriesItem.DataInserimento;
                existing.DataInserimento = SeriesItem.DataInserimento;

                _context.SaveChanges();
            }
            else
            {
                // Insert
                SeriesItem.DataInserimento = DateTime.UtcNow;
                _context.Series.Add(SeriesItem);
                _context.SaveChanges();
            }

            return RedirectToPage("./Index");
        }
    }
}
