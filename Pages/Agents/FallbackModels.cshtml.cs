using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
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
        private readonly ModelPromotionService _promotionService;

        public List<ModelInfo> Models { get; set; } = new();
        public List<RoleModel> AllRoles { get; set; } = new();
        public List<Agent> Agents { get; set; } = new();
        public List<ModelRole> ModelRoles { get; set; } = new();
        public Dictionary<int, int> SpeedIndexByModelRoleId { get; set; } = new();

        public FallbackModelsModel(DatabaseService database, TinyGeneratorDbContext context, ModelPromotionService promotionService)
        {
            _database = database;
            _context = context;
            _promotionService = promotionService;
        }

        public IActionResult OnGet()
        {
            return RedirectToPage("/Shared/Index", new { entity = "model_roles", title = "Fallback Models" });
        }

        public IActionResult OnGetList()
        {
            var modelRoles = LoadModelRoles();
            var speedIndexMap = BuildSpeedIndexMap(modelRoles);
            var rows = modelRoles
                .Select(mr => new
                {
                    id = mr.Id,
                    modelName = mr.Model?.Name ?? string.Empty,
                    roleName = mr.Role?.Name ?? string.Empty,
                    agentName = mr.Agent?.Name ?? string.Empty,
                    isPrimary = mr.IsPrimary,
                    useCount = mr.UseCount,
                    useSuccessed = mr.UseSuccessed,
                    useFailed = mr.UseFailed,
                    totalPromptTokens = mr.TotalPromptTokens,
                    totalOutputTokens = mr.TotalOutputTokens,
                    avgGenTps = mr.AvgGenTps,
                    speedIndex = speedIndexMap.TryGetValue(mr.Id, out var speedIndex) ? speedIndex : 1,
                    avgPromptTps = mr.AvgPromptTps,
                    avgE2eTps = mr.AvgE2eTps,
                    loadRatio = mr.LoadRatio,
                    successRate = (mr.SuccessRate * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                    lastUse = FormatLastUse(mr.LastUse),
                    enabled = mr.Enabled,
                    thinking = mr.Thinking
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

            var roleCode = _context.Roles.AsNoTracking()
                .Where(r => r.Id == input.RoleId)
                .Select(r => r.Name)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(roleCode))
            {
                return new JsonResult(new { ok = false, error = "Ruolo non valido." }) { StatusCode = 400 };
            }

            var resolvedAgentId = input.AgentId;
            if (resolvedAgentId <= 0)
            {
                resolvedAgentId = _context.Agents.AsNoTracking()
                    .Where(a => a.Role != null && a.Role.ToLower() == roleCode.ToLower())
                    .OrderBy(a => a.Id)
                    .Select(a => a.Id)
                    .FirstOrDefault();
            }
            if (resolvedAgentId <= 0)
            {
                return new JsonResult(new { ok = false, error = $"Nessun agente disponibile per il ruolo '{roleCode}'." }) { StatusCode = 400 };
            }

            var now = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            var entity = new ModelRole
            {
                ModelId = input.ModelId,
                RoleId = input.RoleId,
                AgentId = resolvedAgentId,
                Instructions = string.IsNullOrWhiteSpace(input.Instructions) ? null : input.Instructions.Trim(),
                TopP = input.TopP,
                TopK = input.TopK,
                Thinking = input.Thinking,
                Enabled = input.Enabled,
                CreatedAt = now,
                UpdatedAt = now
            };

            _context.ModelRoles.Add(entity);
            _context.SaveChanges();

            var hydrated = _context.ModelRoles
                .Include(mr => mr.Model)
                .Include(mr => mr.Role)
                .Include(mr => mr.Agent)
                .FirstOrDefault(mr => mr.Id == entity.Id);
            var speedIndexMap = BuildSpeedIndexMap(LoadModelRoles());

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
                        roleName = hydrated.Role?.Name ?? string.Empty,
                        agentName = hydrated.Agent?.Name ?? string.Empty,
                        isPrimary = hydrated.IsPrimary,
                        useCount = hydrated.UseCount,
                        useSuccessed = hydrated.UseSuccessed,
                        useFailed = hydrated.UseFailed,
                        totalPromptTokens = hydrated.TotalPromptTokens,
                        totalOutputTokens = hydrated.TotalOutputTokens,
                        avgGenTps = hydrated.AvgGenTps,
                        speedIndex = speedIndexMap.TryGetValue(hydrated.Id, out var speedIndex) ? speedIndex : 1,
                        avgPromptTps = hydrated.AvgPromptTps,
                        avgE2eTps = hydrated.AvgE2eTps,
                        loadRatio = hydrated.LoadRatio,
                        successRate = (hydrated.SuccessRate * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                        lastUse = FormatLastUse(hydrated.LastUse),
                        enabled = hydrated.Enabled,
                        thinking = hydrated.Thinking
                    }
            });
        }

        public IActionResult OnGetDetail(int id)
        {
            var mr = _context.ModelRoles
                .Include(x => x.Model)
                .Include(x => x.Role)
                .Include(x => x.Agent)
                .FirstOrDefault(x => x.Id == id);

            if (mr == null)
            {
                return new JsonResult(new { title = "Fallback non trovato", description = string.Empty }) { StatusCode = 404 };
            }

            var instructions = string.IsNullOrWhiteSpace(mr.Instructions) ? "-" : mr.Instructions!;
            var description =
                $"Modello: {mr.Model?.Name ?? "-"}\n" +
                $"Ruolo: {mr.Role?.Name ?? "-"}\n" +
                $"Agente: {mr.Agent?.Name ?? "-"}\n" +
                $"Primary: {(mr.IsPrimary ? "Yes" : "No")}\n" +
                $"Istruzioni: {instructions}\n" +
                $"Top-P: {(mr.TopP.HasValue ? mr.TopP.Value.ToString("0.00") : "-")}\n" +
                $"Top-K: {(mr.TopK.HasValue ? mr.TopK.Value.ToString() : "-")}\n" +
                $"Thinking: {FormatThinking(mr.Thinking)}\n" +
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
                description,
                id = mr.Id,
                modelId = mr.ModelId,
                roleId = mr.RoleId,
                agentId = mr.AgentId,
                modelName = mr.Model?.Name ?? "-",
                roleName = mr.Role?.Name ?? "-",
                agentName = mr.Agent?.Name ?? "-",
                isPrimary = mr.IsPrimary,
                instructions,
                topP = mr.TopP,
                topK = mr.TopK,
                enabled = mr.Enabled,
                thinking = mr.Thinking
            });
        }

        public IActionResult OnGetErrors(int id, int? modelId = null, int? roleId = null, int? agentId = null)
        {
            if (id <= 0 && (!modelId.HasValue || modelId.Value <= 0 || !roleId.HasValue || roleId.Value <= 0))
            {
                return new JsonResult(new { ok = false, error = "Invalid id", rows = Array.Empty<object>() }) { StatusCode = 400 };
            }

            var rows = (modelId.HasValue && modelId.Value > 0 && roleId.HasValue && roleId.Value > 0)
                ? _database.ListModelRoleErrorsByModelAndRole(modelId.Value, roleId.Value, 200, agentId)
                : _database.ListModelRoleErrors(id, 200);

            var payloadRows = rows
                .Select(e => new
                {
                    id = e.Id,
                    errorType = e.ErrorType,
                    errorText = e.ErrorText,
                    errorCount = e.ErrorCount,
                    dateInsert = e.DateInsert,
                    dateLast = e.DateLast,
                    dateInsertFmt = FormatDateTime(e.DateInsert),
                    dateLastFmt = FormatDateTime(e.DateLast)
                })
                .ToList();

            return new JsonResult(new { ok = true, rows = payloadRows });
        }

        public IActionResult OnPostDeleteError([FromBody] DeleteErrorRequest input)
        {
            if (input is null || input.ErrorId <= 0)
            {
                return new JsonResult(new { ok = false, error = "Invalid error id" }) { StatusCode = 400 };
            }

            var deleted = _database.DeleteModelRoleError(input.ErrorId);
            if (!deleted)
            {
                return new JsonResult(new { ok = false, error = "Error row not found" }) { StatusCode = 404 };
            }

            return new JsonResult(new { ok = true });
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

        public IActionResult OnPostUpdate([FromBody] UpdateRequest input)
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

            entity.Instructions = string.IsNullOrWhiteSpace(input.Instructions) ? null : input.Instructions.Trim();
            entity.TopP = input.TopP;
            entity.TopK = input.TopK;
            entity.Thinking = input.Thinking;
            entity.Enabled = input.Enabled;
            entity.UpdatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            _context.SaveChanges();

            return new JsonResult(new { ok = true });
        }

        public async Task<IActionResult> OnPostPromoteAll()
        {
            var results = await _promotionService.PromoteBestModelsForAllRolesAsync(source: "manual_fallback_models");
            var changed = results.Count(r => r.Changed);
            return new JsonResult(new
            {
                ok = true,
                promoted = changed,
                examined = results.Count
            });
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

        private static string FormatDateTime(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "-";
            if (DateTime.TryParse(value, out var parsed))
            {
                return parsed.ToString("dd/MM/yyyy HH:mm:ss");
            }
            return value;
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

        private static string FormatThinking(bool? thinking)
        {
            return thinking.HasValue ? (thinking.Value ? "true" : "false") : "null";
        }

        private void LoadReferenceData()
        {
            Models = _database.ListModels()
                .Where(m => m.Enabled)
                .OrderBy(m => m.Name)
                .ToList();
            var roles = _context.Roles.ToList();
            var existing = new HashSet<string>(roles.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            var agentRoles = _database.ListAgentRoles();
            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var added = false;

            foreach (var role in agentRoles)
            {
                if (string.IsNullOrWhiteSpace(role)) continue;
                if (existing.Contains(role)) continue;
                _context.Roles.Add(new RoleModel
                {
                    Name = role,
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
                .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(r => r.Id).First())
                .OrderBy(r => r.Name)
                .ToList();

            AllRoles = deduped;
            Agents = _context.Agents
                .AsNoTracking()
                .Where(a => a.IsActive)
                .OrderBy(a => a.Role)
                .ThenBy(a => a.Name)
                .ToList();
        }

        private List<ModelRole> LoadModelRoles()
        {
            return _context.ModelRoles
                .Include(mr => mr.Model)
                .Include(mr => mr.Role)
                .Include(mr => mr.Agent)
                .ToList()
                .OrderByDescending(mr => mr.SuccessRate)
                .ToList();
        }

        public int GetSpeedIndex(int modelRoleId)
        {
            return SpeedIndexByModelRoleId.TryGetValue(modelRoleId, out var value) ? value : 1;
        }

        private static Dictionary<int, int> BuildSpeedIndexMap(IEnumerable<ModelRole> modelRoles)
        {
            var result = new Dictionary<int, int>();
            var rows = modelRoles?.ToList() ?? new List<ModelRole>();
            var groups = rows.GroupBy(r => r.RoleId);

            foreach (var group in groups)
            {
                var speeds = group
                    .Select(r => new
                    {
                        Id = r.Id,
                        Speed = r.AvgGenTps > 0 ? r.AvgGenTps : 0d
                    })
                    .ToList();

                var min = speeds.Min(s => s.Speed);
                var max = speeds.Max(s => s.Speed);

                foreach (var item in speeds)
                {
                    int score;
                    if (item.Speed <= 0)
                    {
                        score = 1;
                    }
                    else if (max <= min)
                    {
                        score = 10;
                    }
                    else
                    {
                        var normalized = (item.Speed - min) / (max - min);
                        score = 1 + (int)Math.Round(normalized * 9d, MidpointRounding.AwayFromZero);
                        score = Math.Clamp(score, 1, 10);
                    }

                    result[item.Id] = score;
                }
            }

            return result;
        }

        public class ModelRoleInput
        {
            public int ModelId { get; set; }
            public int RoleId { get; set; }
            public int AgentId { get; set; }
            public string? Instructions { get; set; }
            public double? TopP { get; set; }
            public int? TopK { get; set; }
            public bool? Thinking { get; set; }
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

        public class UpdateRequest
        {
            public int Id { get; set; }
            public string? Instructions { get; set; }
            public double? TopP { get; set; }
            public int? TopK { get; set; }
            public bool? Thinking { get; set; }
            public bool Enabled { get; set; }
        }

        public class DeleteErrorRequest
        {
            public long ErrorId { get; set; }
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
