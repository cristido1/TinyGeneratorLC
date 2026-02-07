using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;
using TinyGenerator.Services;
using RoleModel = TinyGenerator.Models.Role;

namespace TinyGenerator.Pages.Agents
{
    public class FallbackModelsModel : PageModel
    {
        private readonly DatabaseService _database;
        private readonly TinyGeneratorDbContext _context;

        public List<ModelInfo> Models { get; set; } = new();
        public List<RoleModel> AllRoles { get; set; } = new();
        public List<ModelRole> ModelRoles { get; set; } = new();

        public FallbackModelsModel(DatabaseService database, TinyGeneratorDbContext context)
        {
            _database = database;
            _context = context;
        }

        public IActionResult OnGet()
        {
            LoadReferenceData();
            ModelRoles = LoadModelRoles();
            return Page();
        }

        public IActionResult OnGetList()
        {
            var rows = LoadModelRoles()
                .Select(mr => new
                {
                    id = mr.Id,
                    modelName = mr.Model?.Name ?? string.Empty,
                    roleName = mr.Role?.Ruolo ?? string.Empty,
                    isPrimary = mr.IsPrimary,
                    useCount = mr.UseCount,
                    useSuccessed = mr.UseSuccessed,
                    useFailed = mr.UseFailed,
                    totalPromptTokens = mr.TotalPromptTokens,
                    totalOutputTokens = mr.TotalOutputTokens,
                    avgGenTps = mr.AvgGenTps,
                    avgPromptTps = mr.AvgPromptTps,
                    avgE2eTps = mr.AvgE2eTps,
                    loadRatio = mr.LoadRatio,
                    successRate = (mr.SuccessRate * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                    lastUse = FormatLastUse(mr.LastUse),
                    enabled = mr.Enabled
                })
                .ToList();

            return new JsonResult(rows);
        }

        public IActionResult OnPostAdd([FromBody] ModelRoleInput input)
        {
            if (input is null)
            {
                return new JsonResult(new { ok = false, error = "Invalid payload" }) { StatusCode = 400 };
            }

            if (input.ModelId <= 0 || input.RoleId <= 0)
            {
                return new JsonResult(new { ok = false, error = "Model e Role sono obbligatori." }) { StatusCode = 400 };
            }

            var now = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            var entity = new ModelRole
            {
                ModelId = input.ModelId,
                RoleId = input.RoleId,
                Instructions = string.IsNullOrWhiteSpace(input.Instructions) ? null : input.Instructions.Trim(),
                TopP = input.TopP,
                TopK = input.TopK,
                Enabled = input.Enabled,
                CreatedAt = now,
                UpdatedAt = now
            };

            _context.ModelRoles.Add(entity);
            _context.SaveChanges();

            var hydrated = _context.ModelRoles
                .Include(mr => mr.Model)
                .Include(mr => mr.Role)
                .FirstOrDefault(mr => mr.Id == entity.Id);

            return new JsonResult(new
            {
                ok = true,
                id = entity.Id,
                row = hydrated == null
                    ? null
                    : new
                    {
                        id = hydrated.Id,
                        modelName = hydrated.Model?.Name ?? string.Empty,
                        roleName = hydrated.Role?.Ruolo ?? string.Empty,
                        isPrimary = hydrated.IsPrimary,
                        useCount = hydrated.UseCount,
                        useSuccessed = hydrated.UseSuccessed,
                        useFailed = hydrated.UseFailed,
                        totalPromptTokens = hydrated.TotalPromptTokens,
                        totalOutputTokens = hydrated.TotalOutputTokens,
                        avgGenTps = hydrated.AvgGenTps,
                        avgPromptTps = hydrated.AvgPromptTps,
                        avgE2eTps = hydrated.AvgE2eTps,
                        loadRatio = hydrated.LoadRatio,
                        successRate = (hydrated.SuccessRate * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                        lastUse = FormatLastUse(hydrated.LastUse),
                        enabled = hydrated.Enabled
                    }
            });
        }

        public IActionResult OnGetDetail(int id)
        {
            var mr = _context.ModelRoles
                .Include(x => x.Model)
                .Include(x => x.Role)
                .FirstOrDefault(x => x.Id == id);

            if (mr == null)
            {
                return new JsonResult(new { title = "Fallback non trovato", description = string.Empty }) { StatusCode = 404 };
            }

            var description =
                $"Modello: {mr.Model?.Name ?? "-"}\n" +
                $"Ruolo: {mr.Role?.Ruolo ?? "-"}\n" +
                $"Primary: {(mr.IsPrimary ? "Yes" : "No")}\n" +
                $"Istruzioni: {(string.IsNullOrWhiteSpace(mr.Instructions) ? "-" : mr.Instructions)}\n" +
                $"Top-P: {(mr.TopP.HasValue ? mr.TopP.Value.ToString("0.00") : "-")}\n" +
                $"Top-K: {(mr.TopK.HasValue ? mr.TopK.Value.ToString() : "-")}\n" +
                $"Enabled: {(mr.Enabled ? "Yes" : "No")}\n" +
                $"Ultimo uso: {FormatLastUse(mr.LastUse)}\n" +
                $"Prompt tokens: {mr.TotalPromptTokens}\n" +
                $"Output tokens: {mr.TotalOutputTokens}\n" +
                $"Avg Gen TPS: {FormatPositiveDouble(mr.AvgGenTps, 2)}\n" +
                $"Avg Prompt TPS: {FormatPositiveDouble(mr.AvgPromptTps, 2)}\n" +
                $"Avg E2E TPS: {FormatPositiveDouble(mr.AvgE2eTps, 2)}\n" +
                $"Load ratio: {FormatPositivePercent(mr.LoadRatio)}";

            return new JsonResult(new
            {
                title = $"Fallback #{mr.Id}",
                description
            });
        }

        public IActionResult OnPostDelete([FromBody] DeleteRequest input)
        {
            if (input is null || input.Id <= 0)
            {
                return new JsonResult(new { ok = false, error = "Invalid id" }) { StatusCode = 400 };
            }

            var entity = _context.ModelRoles.Find(input.Id);
            if (entity == null)
            {
                return new JsonResult(new { ok = false, error = "Not found" }) { StatusCode = 404 };
            }

            _context.ModelRoles.Remove(entity);
            _context.SaveChanges();

            return new JsonResult(new { ok = true });
        }

        public IActionResult OnPostToggleEnabled([FromBody] ToggleEnabledRequest input)
        {
            if (input is null || input.Id <= 0)
            {
                return new JsonResult(new { ok = false, error = "Invalid id" }) { StatusCode = 400 };
            }

            var entity = _context.ModelRoles.Find(input.Id);
            if (entity == null)
            {
                return new JsonResult(new { ok = false, error = "Not found" }) { StatusCode = 404 };
            }

            entity.Enabled = input.Enabled;
            entity.UpdatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            _context.SaveChanges();

            return new JsonResult(new { ok = true, enabled = entity.Enabled });
        }

        public string FormatLastUse(string? lastUse)
        {
            if (string.IsNullOrWhiteSpace(lastUse)) return "-";
            if (DateTime.TryParse(lastUse, out var parsed))
            {
                return parsed.ToString("dd/MM HH:mm");
            }
            return "-";
        }

        private static string FormatPositiveDouble(double value, int decimals)
        {
            if (value <= 0) return "-";
            return value.ToString($"0.{new string('0', decimals)}", CultureInfo.InvariantCulture);
        }

        private static string FormatPositivePercent(double ratio)
        {
            if (ratio <= 0) return "-";
            return (ratio * 100.0).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        private void LoadReferenceData()
        {
            Models = _database.ListModels();
            var roles = _context.Roles.ToList();
            var existing = new HashSet<string>(roles.Select(r => r.Ruolo), StringComparer.OrdinalIgnoreCase);
            var agentRoles = _database.ListAgentRoles();
            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var added = false;

            foreach (var role in agentRoles)
            {
                if (string.IsNullOrWhiteSpace(role)) continue;
                if (existing.Contains(role)) continue;
                _context.Roles.Add(new RoleModel
                {
                    Ruolo = role,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                existing.Add(role);
                added = true;
            }

            if (added)
            {
                _context.SaveChanges();
            }

            var deduped = _context.Roles
                .AsNoTracking()
                .ToList()
                .GroupBy(r => r.Ruolo, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(r => r.Id).First())
                .OrderBy(r => r.Ruolo)
                .ToList();

            AllRoles = deduped;
        }

        private List<ModelRole> LoadModelRoles()
        {
            return _context.ModelRoles
                .Include(mr => mr.Model)
                .Include(mr => mr.Role)
                .ToList()
                .OrderByDescending(mr => mr.SuccessRate)
                .ToList();
        }

        public class ModelRoleInput
        {
            public int ModelId { get; set; }
            public int RoleId { get; set; }
            public string? Instructions { get; set; }
            public double? TopP { get; set; }
            public int? TopK { get; set; }
            public bool Enabled { get; set; } = true;
        }

        public class DeleteRequest
        {
            public int Id { get; set; }
        }

        public class ToggleEnabledRequest
        {
            public int Id { get; set; }
            public bool Enabled { get; set; }
        }

        public record RowAction(string Id, string Title, string Method, string Url, bool Confirm = false);

        public List<RowAction> GetActionsForModelRole(ModelRole modelRole)
        {
            if (modelRole.IsPrimary)
            {
                return new List<RowAction>();
            }

            return new List<RowAction>
            {
                new("delete", "Elimina", "CLIENT", string.Empty, true)
            };
        }
    }
}
