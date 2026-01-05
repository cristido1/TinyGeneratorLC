using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands
{
    public sealed class GenerateSeriesCharacterImagesCommand
    {
        public sealed record ImageGenerationSummary(
            int ImagesGenerated,
            long TotalBytes,
            string? LastPrompt,
            string? SaveFolder,
            string? LastFileName);

        private readonly int _serieId;
        private readonly int? _characterId;
        private readonly string _contentRootPath;
        private readonly DatabaseService _database;
        private readonly ImageService _images;
        private readonly ICommandDispatcher _dispatcher;
        private readonly ICustomLogger _logger;

        public GenerateSeriesCharacterImagesCommand(
            int serieId,
            string contentRootPath,
            DatabaseService database,
            ImageService images,
            ICommandDispatcher dispatcher,
            ICustomLogger logger,
            int? characterId = null)
        {
            _serieId = serieId;
            _characterId = characterId;
            _contentRootPath = contentRootPath;
            _database = database;
            _images = images;
            _dispatcher = dispatcher;
            _logger = logger;
        }

        public async Task<ImageGenerationSummary> ExecuteAsync(string runId, CancellationToken ct)
        {
            var serie = _database.GetSeriesById(_serieId);
            if (serie == null)
            {
                throw new InvalidOperationException($"Serie {_serieId} non trovata.");
            }

            var folder = (serie.Folder ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = $"serie_{_serieId:D4}";
            }

            var baseDir = Path.Combine(_contentRootPath, "series_folder", folder, "images_characters");
            Directory.CreateDirectory(baseDir);

            if (!Directory.Exists(baseDir))
            {
                throw new IOException($"Impossibile creare/cartella mancante: {baseDir}");
            }

            var characters = _database.ListSeriesCharacters(_serieId)
                .Where(c => !IsNarratorCharacter(c.Name))
                .OrderBy(c => c.Name)
                .ToList();

            if (_characterId.HasValue)
            {
                characters = characters.Where(c => c.Id == _characterId.Value).ToList();
            }

            var total = characters.Count;
            if (total == 0)
            {
                // Either no characters in series, or the requested character is narrator / missing.
                _dispatcher.UpdateStep(runId, 0, 0, "Nessuna immagine da generare");
                if (_characterId.HasValue)
                {
                    _logger.Append(runId, $"[{_serieId}] Nessuna immagine da generare per characterId={_characterId.Value}. Cartella: {baseDir}");
                }
                else
                {
                    _logger.Append(runId, $"[{_serieId}] Nessun personaggio generabile. Cartella: {baseDir}");
                }
                return new ImageGenerationSummary(0, 0, null, baseDir, null);
            }

            var imagesGenerated = 0;
            long totalBytes = 0;
            string? lastPrompt = null;
            string? lastFileName = null;

            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();

                var character = characters[i];
                var current = i + 1;
                _dispatcher.UpdateStep(runId, current, total, $"[{current}]/[{total}] Genera immagini personaggi");
                _logger.Append(runId, $"[{_serieId}] Generazione immagine per '{character.Name}' ({current}/{total})");

                var promptDescription = BuildPromptDescription(serie, character);
                lastPrompt = promptDescription;

                var req = new GenerateCharactersRequest
                {
                    StoryId = _serieId.ToString(),
                    Characters =
                    {
                        new CharacterImageRequest
                        {
                            Name = character.Name ?? string.Empty,
                            Emotion = "neutral",
                            Description = promptDescription,
                            Style = "realistic",
                            OutputSize = new[] { 512, 512 },
                            Background = "transparent"
                        }
                    }
                };

                var resp = await _images.GenerateCharactersAsync(req);
                if (!string.Equals(resp.Status, "success", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Scene Renderer ha risposto status='{resp.Status ?? ""}' per '{character.Name}'.");
                }

                var item = resp.Characters.FirstOrDefault();
                var b64 = item?.Data;
                if (string.IsNullOrWhiteSpace(b64))
                {
                    throw new InvalidOperationException($"Risposta senza immagine per '{character.Name}'.");
                }

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(b64);
                }
                catch
                {
                    // Some APIs prefix data URLs: data:image/png;base64,...
                    var comma = b64.IndexOf(',');
                    if (comma > 0)
                    {
                        bytes = Convert.FromBase64String(b64[(comma + 1)..]);
                    }
                    else
                    {
                        throw;
                    }
                }

                var safeName = MakeSafeFilePart(character.Name);
                var fileName = $"{safeName}.png";
                var fullPath = Path.Combine(baseDir, fileName);
                await File.WriteAllBytesAsync(fullPath, bytes, ct);

                if (!File.Exists(fullPath))
                {
                    throw new IOException($"Scrittura immagine fallita (file non trovato dopo write): {fullPath}");
                }

                _logger.Append(runId, $"[{_serieId}] Immagine salvata: cartella='{baseDir}', file='{fileName}'");

                // Store filename in DB (UI can compose the public URL).
                _database.UpdateSeriesCharacterImage(character.Id, fileName);

                lastFileName = fileName;

                imagesGenerated++;
                totalBytes += bytes.LongLength;
            }

            _dispatcher.UpdateStep(runId, total, total, $"[{total}]/[{total}] Completato");
            _logger.Append(runId, $"[{_serieId}] Immagini salvate in: {baseDir}");

            return new ImageGenerationSummary(imagesGenerated, totalBytes, lastPrompt, baseDir, lastFileName);
        }

        private static string BuildPromptDescription(Series serie, SeriesCharacter character)
        {
            var serieGenere = (serie.Genere ?? string.Empty).Trim();
            var serieSottogenere = (serie.Sottogenere ?? string.Empty).Trim();
            var ambientazione = (serie.AmbientazioneBase ?? string.Empty).Trim();
            var descr = (!string.IsNullOrWhiteSpace(character.Aspect)
                ? character.Aspect
                : character.Description) ?? string.Empty;
            descr = descr.Trim();

            return
                $"Genere: {serieGenere}; " +
                $"Sottogenere: {serieSottogenere}; " +
                $"Ambientazione: {ambientazione}; " +
                $"Descrizione personaggio: {descr}";
        }

        private static bool IsNarratorCharacter(string? name)
        {
            var n = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(n)) return false;
            return string.Equals(n, "narratore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(n, "narrator", StringComparison.OrdinalIgnoreCase);
        }

        private static string MakeSafeFilePart(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "character";
            var trimmed = input.Trim();

            var sb = new StringBuilder(trimmed.Length);
            var lastWasUnderscore = false;

            foreach (var ch in trimmed)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    lastWasUnderscore = false;
                    continue;
                }

                if (!lastWasUnderscore)
                {
                    sb.Append('_');
                    lastWasUnderscore = true;
                }
            }

            var safe = sb.ToString().Trim('_');
            return string.IsNullOrWhiteSpace(safe) ? "character" : safe;
        }
    }
}
