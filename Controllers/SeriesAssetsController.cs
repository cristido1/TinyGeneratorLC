using System;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Hosting;
using TinyGenerator.Data;

namespace TinyGenerator.Controllers
{
    [ApiController]
    public sealed class SeriesAssetsController : ControllerBase
    {
        private readonly TinyGeneratorDbContext _db;
        private readonly IWebHostEnvironment _env;
        private readonly FileExtensionContentTypeProvider _contentTypeProvider = new();

        public SeriesAssetsController(TinyGeneratorDbContext db, IWebHostEnvironment env)
        {
            _db = db;
            _env = env;
        }

        [HttpGet("/series-assets/{seriesId:int}/characters/{fileName}")]
        public IActionResult GetCharacterImage(int seriesId, string fileName)
        {
            if (seriesId <= 0) return BadRequest();
            if (string.IsNullOrWhiteSpace(fileName)) return BadRequest();
            if (fileName.Contains("..") || fileName.Contains("/") || fileName.Contains("\\")) return BadRequest();

            var serie = _db.Series.Find(seriesId);
            if (serie == null) return NotFound();

            var folder = (serie.Folder ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = $"serie_{seriesId:D4}";
            }

            var fullPath = Path.Combine(_env.ContentRootPath, "series_folder", folder, "images_characters", fileName);
            if (!System.IO.File.Exists(fullPath)) return NotFound();

            if (!_contentTypeProvider.TryGetContentType(fullPath, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            // Disable caching for character images so clients always fetch latest
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";

            return PhysicalFile(fullPath, contentType);
        }
    }
}
