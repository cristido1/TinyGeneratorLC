using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;
using TinyGenerator.Services;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Pages.Series
{
    public class EditModel : PageModel
    {
        private readonly TinyGeneratorDbContext _context;
        private readonly ICommandDispatcher? _dispatcher;
        private readonly DatabaseService? _database;
        private readonly ImageService? _images;
        private readonly ICustomLogger? _logger;
        private readonly IWebHostEnvironment? _env;
        private const string CharacterImageFolder = "series_characters";
        private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp"
        };

        public EditModel(
            TinyGeneratorDbContext context,
            ICommandDispatcher? dispatcher = null,
            DatabaseService? database = null,
            ImageService? images = null,
            ICustomLogger? logger = null,
            IWebHostEnvironment? env = null)
        {
            _context = context;
            _dispatcher = dispatcher;
            _database = database;
            _images = images;
            _logger = logger;
            _env = env;
        }

        [BindProperty]
        public TinyGenerator.Models.Series SeriesItem { get; set; } = new();

        public List<SeriesCharacter> SeriesCharacters { get; set; } = new();

        public List<SeriesEpisode> SeriesEpisodes { get; set; } = new();

        public List<TtsVoice> Voices { get; set; } = new();

        [BindProperty]
        public SeriesCharacterInput CharacterInput { get; set; } = new();

        [BindProperty]
        public IFormFile? CharacterImage { get; set; }

        [BindProperty]
        public SeriesEpisodeInput EpisodeInput { get; set; } = new();

        public bool IsEditMode => SeriesItem?.Id > 0;
        public string ActiveTab { get; set; } = "series";

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

            SetActiveTab(Request.Query["tab"]);
            LoadPageData(SeriesItem.Id);
            return Page();
        }

        public IActionResult OnPost()
        {
            if (!ModelState.IsValid)
            {
                LoadPageData(SeriesItem.Id);
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
                existing.ImagesStyle = SeriesItem.ImagesStyle;
                existing.EpisodiGenerati = SeriesItem.EpisodiGenerati;
                existing.DataInserimento = SeriesItem.DataInserimento;

                // Force overwrite: bypass optimistic concurrency and update the row directly.
                try
                {
                    var affected = _context.Database.ExecuteSqlInterpolated($@"
UPDATE series SET
    titolo = {existing.Titolo},
    genere = {existing.Genere},
    sottogenere = {existing.Sottogenere},
    periodo_narrativo = {existing.PeriodoNarrativo},
    tono_base = {existing.TonoBase},
    target = {existing.Target},
    lingua = {existing.Lingua},
    ambientazione_base = {existing.AmbientazioneBase},
    premessa_serie = {existing.PremessaSerie},
    arco_narrativo_serie = {existing.ArcoNarrativoSerie},
    stile_scrittura = {existing.StileScrittura},
    regole_narrative = {existing.RegoleNarrative},
    note_ai = {existing.NoteAI},
    images_style = {existing.ImagesStyle},
    episodi_generati = {existing.EpisodiGenerati},
    data_inserimento = {existing.DataInserimento}
WHERE id = {existing.Id};");

                    if (affected <= 0)
                    {
                        TempData["ErrorMessage"] = "Salvataggio non riuscito: serie non trovata.";
                        LoadPageData(SeriesItem.Id);
                        return Page();
                    }
                }
                catch (Exception ex)
                {
                    TempData["ErrorMessage"] = "Errore durante il salvataggio: " + ex.Message;
                    LoadPageData(SeriesItem.Id);
                    return Page();
                }
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

        public IActionResult OnPostAddCharacter()
        {
            ModelState.Clear();
            SetActiveTab("characters");
            var seriesId = CharacterInput.SerieId;
            if (seriesId <= 0) return BadRequest();

            if (string.IsNullOrWhiteSpace(CharacterInput.Name))
            {
                ModelState.AddModelError("CharacterInput.Name", "Nome obbligatorio.");
            }

            var gender = NormalizeGender(CharacterInput.Gender);
            if (gender == null)
            {
                ModelState.AddModelError("CharacterInput.Gender", "Gender non valido.");
            }

            if (CharacterImage != null && !IsAllowedImage(CharacterImage.FileName))
            {
                ModelState.AddModelError("CharacterImage", "Formato immagine non supportato.");
            }

            if (!ModelState.IsValid)
            {
                LoadPageData(seriesId);
                SeriesItem = _context.Series.FirstOrDefault(s => s.Id == seriesId) ?? new TinyGenerator.Models.Series();
                return Page();
            }

            if (!_context.Series.Any(s => s.Id == seriesId)) return NotFound();

            var normalizedGender = gender ?? "other";
            var character = new SeriesCharacter
            {
                SerieId = seriesId,
                Name = CharacterInput.Name?.Trim() ?? string.Empty,
                Gender = normalizedGender,
                Description = CharacterInput.Description?.Trim(),
                Aspect = CharacterInput.Aspect?.Trim(),
                Eta = CharacterInput.Eta?.Trim(),
                Formazione = CharacterInput.Formazione?.Trim(),
                Specializzazione = CharacterInput.Specializzazione?.Trim(),
                Profilo = CharacterInput.Profilo?.Trim(),
                ConflittoInterno = CharacterInput.ConflittoInterno?.Trim(),
                VoiceId = CharacterInput.VoiceId,
                EpisodeIn = CharacterInput.EpisodeIn,
                EpisodeOut = CharacterInput.EpisodeOut
            };

            if (CharacterImage != null)
            {
                character.Image = SaveCharacterImage(CharacterImage, seriesId);
            }

            try
            {
                _context.SeriesCharacters.Add(character);
                _context.SaveChanges();
                TempData["StatusMessage"] = "Personaggio salvato.";
                return RedirectToPage(new { id = seriesId, tab = "characters" });
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore salvataggio personaggio: " + ex.Message;
                LoadPageData(seriesId);
                SeriesItem = _context.Series.FirstOrDefault(s => s.Id == seriesId) ?? new TinyGenerator.Models.Series();
                return Page();
            }
        }

        public IActionResult OnPostUpdateCharacter()
        {
            ModelState.Clear();
            SetActiveTab("characters");
            var seriesId = CharacterInput.SerieId;
            if (seriesId <= 0) return BadRequest();

            if (string.IsNullOrWhiteSpace(CharacterInput.Name))
            {
                ModelState.AddModelError("CharacterInput.Name", "Nome obbligatorio.");
            }

            var gender = NormalizeGender(CharacterInput.Gender);
            if (gender == null)
            {
                ModelState.AddModelError("CharacterInput.Gender", "Gender non valido.");
            }

            if (CharacterImage != null && !IsAllowedImage(CharacterImage.FileName))
            {
                ModelState.AddModelError("CharacterImage", "Formato immagine non supportato.");
            }

            if (!ModelState.IsValid)
            {
                LoadPageData(seriesId);
                SeriesItem = _context.Series.FirstOrDefault(s => s.Id == seriesId) ?? new TinyGenerator.Models.Series();
                return Page();
            }

            var existing = _context.SeriesCharacters.FirstOrDefault(c => c.Id == CharacterInput.Id && c.SerieId == seriesId);
            if (existing == null) return NotFound();

            var normalizedGender = gender ?? "other";
            existing.Name = CharacterInput.Name?.Trim() ?? string.Empty;
            existing.Gender = normalizedGender;
            existing.Description = CharacterInput.Description?.Trim();
            existing.Aspect = CharacterInput.Aspect?.Trim();
            existing.Eta = CharacterInput.Eta?.Trim();
            existing.Formazione = CharacterInput.Formazione?.Trim();
            existing.Specializzazione = CharacterInput.Specializzazione?.Trim();
            existing.Profilo = CharacterInput.Profilo?.Trim();
            existing.ConflittoInterno = CharacterInput.ConflittoInterno?.Trim();
            existing.VoiceId = CharacterInput.VoiceId;
            existing.EpisodeIn = CharacterInput.EpisodeIn;
            existing.EpisodeOut = CharacterInput.EpisodeOut;

            string? oldImage = existing.Image;
            if (CharacterImage != null)
            {
                existing.Image = SaveCharacterImage(CharacterImage, seriesId);
            }

            try
            {
                _context.SaveChanges();

                if (CharacterImage != null && !string.IsNullOrWhiteSpace(oldImage))
                {
                    TryDeleteCharacterImage(oldImage);
                }

                TempData["StatusMessage"] = "Personaggio aggiornato.";
                return RedirectToPage(new { id = seriesId, tab = "characters" });
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore aggiornamento personaggio: " + ex.Message;
                LoadPageData(seriesId);
                SeriesItem = _context.Series.FirstOrDefault(s => s.Id == seriesId) ?? new TinyGenerator.Models.Series();
                return Page();
            }
        }

        public IActionResult OnPostDeleteCharacter(int id, int seriesId)
        {
            if (id <= 0 || seriesId <= 0) return BadRequest();

            var existing = _context.SeriesCharacters.FirstOrDefault(c => c.Id == id && c.SerieId == seriesId);
            if (existing == null) return NotFound();

            var oldImage = existing.Image;
            try
            {
                _context.SeriesCharacters.Remove(existing);
                _context.SaveChanges();

                if (!string.IsNullOrWhiteSpace(oldImage))
                {
                    TryDeleteCharacterImage(oldImage);
                }

                TempData["StatusMessage"] = "Personaggio eliminato.";
                return RedirectToPage(new { id = seriesId, tab = "characters" });
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore eliminazione personaggio: " + ex.Message;
                LoadPageData(seriesId);
                SeriesItem = _context.Series.FirstOrDefault(s => s.Id == seriesId) ?? new TinyGenerator.Models.Series();
                return Page();
            }
        }

        public IActionResult OnPostAssignCharacterVoices(int seriesId)
        {
            if (seriesId <= 0) return BadRequest();
            SetActiveTab("characters");

            try
            {
                var characters = _context.SeriesCharacters
                    .Where(c => c.SerieId == seriesId)
                    .OrderBy(c => c.Name)
                    .ToList();

                var voices = _context.TtsVoices
                    .Where(v => !v.Disabled)
                    .ToList()
                    .Where(v => !IsNarratorVoice(v))
                    .OrderByDescending(v => v.Score ?? 0)
                    .ThenByDescending(v => v.Confidence ?? 0)
                    .ToList();

                var usedVoiceIds = new HashSet<int>(
                    characters.Where(c => c.VoiceId.HasValue && c.VoiceId.Value > 0)
                        .Select(c => c.VoiceId!.Value));

                var updated = 0;
                foreach (var character in characters)
                {
                    if (character.VoiceId.HasValue && character.VoiceId.Value > 0) continue;

                    var gender = NormalizeGender(character.Gender) ?? "other";
                    var match = voices.FirstOrDefault(v =>
                        !usedVoiceIds.Contains(v.Id) &&
                        string.Equals(NormalizeGender(v.Gender), gender, StringComparison.OrdinalIgnoreCase));

                    if (match == null)
                    {
                        match = voices.FirstOrDefault(v => !usedVoiceIds.Contains(v.Id));
                    }

                    if (match == null) continue;

                    character.VoiceId = match.Id;
                    usedVoiceIds.Add(match.Id);
                    updated++;
                }

                if (updated > 0)
                {
                    _context.SaveChanges();
                }

                TempData["StatusMessage"] = updated > 0
                    ? $"Assegnate {updated} voci ai personaggi."
                    : "Nessuna voce disponibile da assegnare.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore assegnazione voci: " + ex.Message;
            }

            return RedirectToPage(new { id = seriesId, tab = "characters" });
        }

        public IActionResult OnPostGenerateCharacterImages(int seriesId)
        {
            if (seriesId <= 0) return BadRequest();
            SetActiveTab("characters");

            if (_dispatcher == null || _database == null || _images == null || _logger == null)
            {
                TempData["ErrorMessage"] = "Dispatcher/servizi immagini non disponibili.";
                return RedirectToPage(new { id = seriesId, tab = "characters" });
            }

            var contentRoot = _env?.ContentRootPath;
            if (string.IsNullOrWhiteSpace(contentRoot))
            {
                TempData["ErrorMessage"] = "ContentRootPath non disponibile (ambiente hosting).";
                return RedirectToPage(new { id = seriesId, tab = "characters" });
            }

            try
            {
                var metadata = new Dictionary<string, string>
                {
                    // The command panel shows storyId badge; use seriesId as requested.
                    ["storyId"] = seriesId.ToString(),
                    ["seriesId"] = seriesId.ToString()
                };

                var handle = _dispatcher.Enqueue(
                    operationName: "generate_character_images",
                    handler: async ctx =>
                    {
                        try
                        {
                            var cmd = new GenerateSeriesCharacterImagesCommand(seriesId, contentRoot, _database, _images, _dispatcher, _logger);
                            var summary = await cmd.ExecuteAsync(ctx.RunId, ctx.CancellationToken);
                            return new CommandResult(true, $"Immagini personaggi generate per serie {seriesId} (generate: {summary.ImagesGenerated})");
                        }
                        catch (Exception ex)
                        {
                            // Returning failure ensures the command panel shows FAILED with the error.
                            return new CommandResult(false, ex.Message);
                        }
                    },
                    threadScope: "series",
                    metadata: metadata,
                    priority: 2);

                TempData["StatusMessage"] = $"Generazione immagini avviata (runId: {handle.RunId}).";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore avvio generazione immagini: " + ex.Message;
            }

            return RedirectToPage(new { id = seriesId, tab = "characters" });
        }

        public IActionResult OnPostGenerateCharacterImage(int seriesId, int characterId)
        {
            if (seriesId <= 0 || characterId <= 0) return BadRequest();
            SetActiveTab("characters");

            if (_dispatcher == null || _database == null || _images == null || _logger == null)
            {
                TempData["ErrorMessage"] = "Dispatcher/servizi immagini non disponibili.";
                return RedirectToPage(new { id = seriesId, tab = "characters" });
            }

            var contentRoot = _env?.ContentRootPath;
            if (string.IsNullOrWhiteSpace(contentRoot))
            {
                TempData["ErrorMessage"] = "ContentRootPath non disponibile (ambiente hosting).";
                return RedirectToPage(new { id = seriesId, tab = "characters" });
            }

            // Do not generate for narrator
            try
            {
                var ch = _context.SeriesCharacters.FirstOrDefault(c => c.Id == characterId && c.SerieId == seriesId);
                if (ch == null)
                {
                    TempData["ErrorMessage"] = "Personaggio non trovato.";
                    return RedirectToPage(new { id = seriesId, tab = "characters" });
                }

                var name = (ch.Name ?? string.Empty).Trim();
                if (string.Equals(name, "narratore", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "narrator", StringComparison.OrdinalIgnoreCase))
                {
                    TempData["StatusMessage"] = "Personaggio narratore: immagine non generata.";
                    return RedirectToPage(new { id = seriesId, tab = "characters" });
                }
            }
            catch
            {
                // best-effort
            }

            try
            {
                var metadata = new Dictionary<string, string>
                {
                    ["storyId"] = seriesId.ToString(),
                    ["seriesId"] = seriesId.ToString(),
                    ["characterId"] = characterId.ToString()
                };

                var handle = _dispatcher.Enqueue(
                    operationName: "generate_character_image",
                    handler: async ctx =>
                    {
                        try
                        {
                            var cmd = new GenerateSeriesCharacterImagesCommand(seriesId, contentRoot, _database, _images, _dispatcher, _logger, characterId);
                            var summary = await cmd.ExecuteAsync(ctx.RunId, ctx.CancellationToken);
                            var kb = summary.TotalBytes / 1024.0;
                            var prompt = summary.LastPrompt ?? string.Empty;
                            var folder = summary.SaveFolder ?? string.Empty;
                            var file = summary.LastFileName ?? string.Empty;
                            return new CommandResult(true, $"Immagine personaggio generata (serie {seriesId}, character {characterId}), folder=\"{folder}\", file=\"{file}\", size={kb:F1} KB, prompt=\"{prompt}\"");
                        }
                        catch (Exception ex)
                        {
                            return new CommandResult(false, ex.Message);
                        }
                    },
                    threadScope: "series",
                    metadata: metadata,
                    priority: 2);

                TempData["StatusMessage"] = $"Generazione immagine avviata (runId: {handle.RunId}).";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore avvio generazione immagine: " + ex.Message;
            }

            return RedirectToPage(new { id = seriesId, tab = "characters" });
        }

        public IActionResult OnGetCharacterJson(int id)
        {
            if (id <= 0) return BadRequest();

            var ch = _context.SeriesCharacters.FirstOrDefault(c => c.Id == id);
            if (ch == null) return NotFound();

            // Return a simple JSON payload used by client-side refresh
            return new JsonResult(new
            {
                success = true,
                character = new
                {
                    id = ch.Id,
                    serieId = ch.SerieId,
                    name = ch.Name,
                    gender = ch.Gender,
                    description = ch.Description,
                    aspect = ch.Aspect,
                    eta = ch.Eta,
                    formazione = ch.Formazione,
                    specializzazione = ch.Specializzazione,
                    profilo = ch.Profilo,
                    conflittoInterno = ch.ConflittoInterno,
                    voiceId = ch.VoiceId,
                    episodeIn = ch.EpisodeIn,
                    episodeOut = ch.EpisodeOut,
                    image = ch.Image
                }
            });
        }

        public IActionResult OnPostUpdateCharacterAjax()
        {
            // Use existing bound properties: CharacterInput and CharacterImage
            ModelState.Clear();
            var seriesId = CharacterInput.SerieId;
            if (seriesId <= 0) return BadRequest(new { success = false, message = "Serie non valida" });

            if (string.IsNullOrWhiteSpace(CharacterInput.Name))
            {
                return BadRequest(new { success = false, message = "Nome obbligatorio" });
            }

            var gender = NormalizeGender(CharacterInput.Gender);
            if (gender == null)
            {
                return BadRequest(new { success = false, message = "Gender non valido" });
            }

            if (CharacterImage != null && !IsAllowedImage(CharacterImage.FileName))
            {
                return BadRequest(new { success = false, message = "Formato immagine non supportato" });
            }

            var existing = _context.SeriesCharacters.FirstOrDefault(c => c.Id == CharacterInput.Id && c.SerieId == seriesId);
            if (existing == null) return NotFound(new { success = false, message = "Personaggio non trovato" });

            var normalizedGender = gender ?? "other";
            existing.Name = CharacterInput.Name?.Trim() ?? string.Empty;
            existing.Gender = normalizedGender;
            existing.Description = CharacterInput.Description?.Trim();
            existing.Aspect = CharacterInput.Aspect?.Trim();
            existing.Eta = CharacterInput.Eta?.Trim();
            existing.Formazione = CharacterInput.Formazione?.Trim();
            existing.Specializzazione = CharacterInput.Specializzazione?.Trim();
            existing.Profilo = CharacterInput.Profilo?.Trim();
            existing.ConflittoInterno = CharacterInput.ConflittoInterno?.Trim();
            existing.VoiceId = CharacterInput.VoiceId;
            existing.EpisodeIn = CharacterInput.EpisodeIn;
            existing.EpisodeOut = CharacterInput.EpisodeOut;

            string? oldImage = existing.Image;
            if (CharacterImage != null)
            {
                existing.Image = SaveCharacterImage(CharacterImage, seriesId);
            }

            try
            {
                _context.SaveChanges();

                if (CharacterImage != null && !string.IsNullOrWhiteSpace(oldImage))
                {
                    TryDeleteCharacterImage(oldImage);
                }

                var imageUrl = existing.Image ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(imageUrl) && !imageUrl.StartsWith('/'))
                {
                    imageUrl = $"/series-assets/{existing.SerieId}/characters/{Uri.EscapeDataString(imageUrl)}";
                }

                return new JsonResult(new
                {
                    success = true,
                    character = new
                    {
                        id = existing.Id,
                        serieId = existing.SerieId,
                        name = existing.Name,
                        description = existing.Description,
                        aspect = existing.Aspect,
                        voiceId = existing.VoiceId,
                        image = imageUrl
                    },
                    message = "Personaggio aggiornato"
                });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { success = false, message = "Errore aggiornamento: " + ex.Message });
            }
        }

        public IActionResult OnPostGenerateCharacterImageAjax(int seriesId, int characterId)
        {
            if (seriesId <= 0 || characterId <= 0) return BadRequest(new { success = false, message = "Parametri non validi" });

            if (_dispatcher == null || _database == null || _images == null || _logger == null)
            {
                return BadRequest(new { success = false, message = "Dispatcher/servizi non disponibili" });
            }

            var contentRoot = _env?.ContentRootPath;
            if (string.IsNullOrWhiteSpace(contentRoot))
            {
                return BadRequest(new { success = false, message = "ContentRootPath non disponibile" });
            }

            try
            {
                var ch = _context.SeriesCharacters.FirstOrDefault(c => c.Id == characterId && c.SerieId == seriesId);
                if (ch == null) return NotFound(new { success = false, message = "Personaggio non trovato" });

                var name = (ch.Name ?? string.Empty).Trim();
                if (string.Equals(name, "narratore", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "narrator", StringComparison.OrdinalIgnoreCase))
                {
                    return new JsonResult(new { success = false, message = "Personaggio narratore: immagine non generata." });
                }

                var metadata = new Dictionary<string, string>
                {
                    ["storyId"] = seriesId.ToString(),
                    ["seriesId"] = seriesId.ToString(),
                    ["characterId"] = characterId.ToString()
                };

                var handle = _dispatcher.Enqueue(
                    operationName: "generate_character_image",
                    handler: async ctx =>
                    {
                        try
                        {
                            var cmd = new GenerateSeriesCharacterImagesCommand(seriesId, contentRoot, _database, _images, _dispatcher, _logger, characterId);
                            var summary = await cmd.ExecuteAsync(ctx.RunId, ctx.CancellationToken);
                            return new CommandResult(true, $"Immagine generata: {summary.LastFileName}");
                        }
                        catch (Exception ex)
                        {
                            return new CommandResult(false, ex.Message);
                        }
                    },
                    threadScope: "series",
                    metadata: metadata,
                    priority: 2);

                return new JsonResult(new { success = true, runId = handle.RunId, message = "Generazione avviata" });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public IActionResult OnPostAddEpisode()
        {
            ModelState.Clear();
            SetActiveTab("episodes");
            var seriesId = EpisodeInput.SerieId;
            if (seriesId <= 0) return BadRequest();
            if (EpisodeInput.Number <= 0)
            {
                ModelState.AddModelError("EpisodeInput.Number", "Numero episodio non valido.");
            }

            if (!ModelState.IsValid)
            {
                LoadPageData(seriesId);
                SeriesItem = _context.Series.FirstOrDefault(s => s.Id == seriesId) ?? new TinyGenerator.Models.Series();
                return Page();
            }

            if (!_context.Series.Any(s => s.Id == seriesId)) return NotFound();

            var episode = new SeriesEpisode
            {
                SerieId = seriesId,
                Number = EpisodeInput.Number,
                Title = EpisodeInput.Title?.Trim(),
                Trama = EpisodeInput.Trama?.Trim()
            };

            try
            {
                _context.SeriesEpisodes.Add(episode);
                _context.SaveChanges();

                TempData["StatusMessage"] = "Episodio salvato.";
                return RedirectToPage(new { id = seriesId, tab = "episodes" });
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore salvataggio episodio: " + ex.Message;
                LoadPageData(seriesId);
                SeriesItem = _context.Series.FirstOrDefault(s => s.Id == seriesId) ?? new TinyGenerator.Models.Series();
                return Page();
            }
        }

        public IActionResult OnPostUpdateEpisode()
        {
            ModelState.Clear();
            SetActiveTab("episodes");
            var seriesId = EpisodeInput.SerieId;
            if (seriesId <= 0) return BadRequest();
            if (EpisodeInput.Number <= 0)
            {
                ModelState.AddModelError("EpisodeInput.Number", "Numero episodio non valido.");
            }

            if (!ModelState.IsValid)
            {
                LoadPageData(seriesId);
                SeriesItem = _context.Series.FirstOrDefault(s => s.Id == seriesId) ?? new TinyGenerator.Models.Series();
                return Page();
            }

            var existing = _context.SeriesEpisodes.FirstOrDefault(e => e.Id == EpisodeInput.Id && e.SerieId == seriesId);
            if (existing == null) return NotFound();

            existing.Number = EpisodeInput.Number;
            existing.Title = EpisodeInput.Title?.Trim();
            existing.Trama = EpisodeInput.Trama?.Trim();

            try
            {
                _context.SaveChanges();
                TempData["StatusMessage"] = "Episodio aggiornato.";
                return RedirectToPage(new { id = seriesId, tab = "episodes" });
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore aggiornamento episodio: " + ex.Message;
                LoadPageData(seriesId);
                SeriesItem = _context.Series.FirstOrDefault(s => s.Id == seriesId) ?? new TinyGenerator.Models.Series();
                return Page();
            }
        }

        public IActionResult OnPostDeleteEpisode(int id, int seriesId)
        {
            if (id <= 0 || seriesId <= 0) return BadRequest();

            var existing = _context.SeriesEpisodes.FirstOrDefault(e => e.Id == id && e.SerieId == seriesId);
            if (existing == null) return NotFound();

            try
            {
                _context.SeriesEpisodes.Remove(existing);
                _context.SaveChanges();

                TempData["StatusMessage"] = "Episodio eliminato.";
                return RedirectToPage(new { id = seriesId, tab = "episodes" });
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore eliminazione episodio: " + ex.Message;
                LoadPageData(seriesId);
                SeriesItem = _context.Series.FirstOrDefault(s => s.Id == seriesId) ?? new TinyGenerator.Models.Series();
                return Page();
            }
        }

        private void LoadPageData(int seriesId)
        {
            Voices = _context.TtsVoices.OrderBy(v => v.Name).ToList();
            if (seriesId > 0)
            {
                SeriesCharacters = _context.SeriesCharacters
                    .Where(c => c.SerieId == seriesId)
                    .OrderBy(c => c.Name)
                    .ToList();
                SeriesEpisodes = _context.SeriesEpisodes
                    .Where(e => e.SerieId == seriesId)
                    .OrderBy(e => e.Number)
                    .ToList();
            }
            else
            {
                SeriesCharacters = new List<SeriesCharacter>();
                SeriesEpisodes = new List<SeriesEpisode>();
            }
        }

        private static string? NormalizeGender(string? gender)
        {
            if (string.IsNullOrWhiteSpace(gender)) return null;
            var g = gender.Trim().ToLowerInvariant();
            return g switch
            {
                "male" => "male",
                "female" => "female",
                "alien" => "alien",
                "robot" => "robot",
                "other" => "other",
                _ => null
            };
        }

        private static bool IsAllowedImage(string fileName)
        {
            var ext = Path.GetExtension(fileName ?? string.Empty);
            return AllowedImageExtensions.Contains(ext);
        }

        private static bool IsNarratorVoice(TtsVoice voice)
        {
            if (voice == null) return false;
            if (!string.IsNullOrWhiteSpace(voice.Archetype) && voice.Archetype.Equals("narratore", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(voice.Name))
            {
                var name = voice.Name.ToLowerInvariant();
                if (name.Contains("narrator") || name.Contains("narratore")) return true;
            }
            if (!string.IsNullOrWhiteSpace(voice.Tags))
            {
                var tags = voice.Tags.ToLowerInvariant();
                if (tags.Contains("narrator") || tags.Contains("narratore")) return true;
            }
            return false;
        }

        private void SetActiveTab(string? tab)
        {
            var t = (tab ?? string.Empty).Trim().ToLowerInvariant();
            ActiveTab = t switch
            {
                "characters" => "characters",
                "episodes" => "episodes",
                _ => "series"
            };
        }

        private void ClearSeriesModelState()
        {
            var keys = ModelState.Keys.Where(k => k.StartsWith("SeriesItem.", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var key in keys)
            {
                ModelState.Remove(key);
            }
        }

        private static string SaveCharacterImage(IFormFile file, int seriesId)
        {
            var ext = Path.GetExtension(file.FileName ?? string.Empty);
            if (!AllowedImageExtensions.Contains(ext)) throw new InvalidOperationException("Formato immagine non supportato.");

            var baseName = Path.GetFileNameWithoutExtension(file.FileName ?? string.Empty);
            var safeBase = string.Concat(baseName.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')).Trim();
            if (string.IsNullOrWhiteSpace(safeBase)) safeBase = "character";

            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var fileName = $"serie_{seriesId}_{timestamp}_{safeBase}{ext.ToLowerInvariant()}";
            var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", CharacterImageFolder);
            Directory.CreateDirectory(dir);

            var fullPath = Path.Combine(dir, fileName);
            using (var stream = new FileStream(fullPath, FileMode.Create))
            {
                file.CopyTo(stream);
            }

            return "/" + CharacterImageFolder + "/" + fileName;
        }

        private static void TryDeleteCharacterImage(string relativePath)
        {
            var trimmed = relativePath.TrimStart('/', '\\');
            if (!trimmed.StartsWith(CharacterImageFolder, StringComparison.OrdinalIgnoreCase)) return;

            var fullPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", trimmed);
            if (System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
            }
        }

        public sealed class SeriesCharacterInput
        {
            public int Id { get; set; }
            public int SerieId { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Gender { get; set; } = "other";
            public string? Description { get; set; }
            public string? Aspect { get; set; }
            public string? Eta { get; set; }
            public string? Formazione { get; set; }
            public string? Specializzazione { get; set; }
            public string? Profilo { get; set; }
            public string? ConflittoInterno { get; set; }
            public int? VoiceId { get; set; }
            public int? EpisodeIn { get; set; }
            public int? EpisodeOut { get; set; }
        }

        public sealed class SeriesEpisodeInput
        {
            public int Id { get; set; }
            public int SerieId { get; set; }
            public int Number { get; set; }
            public string? Title { get; set; }
            public string? Trama { get; set; }
        }
    }
}
