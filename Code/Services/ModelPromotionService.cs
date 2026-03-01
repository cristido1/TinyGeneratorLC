using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed class ModelPromotionService
{
    public sealed record PromotionResult(
        string RoleCode,
        int AgentId,
        string AgentName,
        int? OldModelId,
        string? OldModelName,
        int NewModelId,
        string NewModelName,
        bool Changed,
        string Reason);

    private readonly TinyGeneratorDbContext _context;
    private readonly ICustomLogger? _logger;

    public ModelPromotionService(TinyGeneratorDbContext context, ICustomLogger? logger = null)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<List<PromotionResult>> PromoteBestModelsForRunAsync(
        string runId,
        string source = "dispatcher",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return new List<PromotionResult>();
        }

        var runToken = $"[{runId.Trim()}]";
        // Use only logs that explicitly belong to this run-id to avoid cross-run contamination.
        var agentHints = await _context.Logs.AsNoTracking()
            .Where(l => l.Message != null
                        && l.Message.Contains(runToken)
                        && l.AgentName != null
                        && l.AgentName != string.Empty)
            .Select(l => l.AgentName!)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var roles = await ResolveRolesFromHintsAsync(agentHints, ct).ConfigureAwait(false);
        return await PromoteBestModelsForRolesAsync(roles, $"{source}:{runId}", ct).ConfigureAwait(false);
    }

    public async Task<List<PromotionResult>> PromoteBestModelsForAllRolesAsync(
        string source = "manual",
        CancellationToken ct = default)
    {
        var roles = await _context.Agents.AsNoTracking()
            .Where(a => a.IsActive && a.Role != null && a.Role != string.Empty)
            .Select(a => a.Role!)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return await PromoteBestModelsForRolesAsync(roles, source, ct).ConfigureAwait(false);
    }

    private async Task<List<string>> ResolveRolesFromHintsAsync(IEnumerable<string> hints, CancellationToken ct)
    {
        var normalizedHints = hints
            .Select(Normalize)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedHints.Count == 0)
        {
            return new List<string>();
        }

        var roles = await _context.Roles.AsNoTracking()
            .Where(r => r.Ruolo != null && r.Ruolo != string.Empty)
            .Select(r => r.Ruolo)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var agents = await _context.Agents.AsNoTracking()
            .Where(a => a.IsActive)
            .Select(a => new { a.Name, a.Role })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var outRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hint in normalizedHints)
        {
            var roleDirect = roles.FirstOrDefault(r => string.Equals(Normalize(r), hint, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(roleDirect))
            {
                outRoles.Add(roleDirect!);
            }

            foreach (var a in agents)
            {
                if (string.IsNullOrWhiteSpace(a.Role))
                {
                    continue;
                }

                var byName = !string.IsNullOrWhiteSpace(a.Name) &&
                             string.Equals(Normalize(a.Name), hint, StringComparison.OrdinalIgnoreCase);
                var byRole = string.Equals(Normalize(a.Role), hint, StringComparison.OrdinalIgnoreCase);
                if (byName || byRole)
                {
                    outRoles.Add(a.Role!);
                }
            }
        }

        return outRoles.ToList();
    }

    private async Task<List<PromotionResult>> PromoteBestModelsForRolesAsync(
        IEnumerable<string> roleCodes,
        string source,
        CancellationToken ct)
    {
        var normalizedRoles = roleCodes
            .Select(Normalize)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedRoles.Count == 0)
        {
            return new List<PromotionResult>();
        }

        var results = new List<PromotionResult>();
        var now = DateTime.UtcNow.ToString("o");
        var anyChange = false;
        var touchedModels = new Dictionary<int, ModelInfo>();

        ModelInfo? GetTrackedModel(int modelId)
        {
            if (modelId <= 0)
            {
                return null;
            }

            if (touchedModels.TryGetValue(modelId, out var tracked))
            {
                return tracked;
            }

            var loaded = _context.Models.FirstOrDefault(m => m.Id == modelId);
            if (loaded != null)
            {
                touchedModels[modelId] = loaded;
            }

            return loaded;
        }

        foreach (var roleNorm in normalizedRoles)
        {
            ct.ThrowIfCancellationRequested();

            var roleEntity = await _context.Roles
                .FirstOrDefaultAsync(r => r.Ruolo != null && r.Ruolo.ToLower() == roleNorm, ct)
                .ConfigureAwait(false);
            if (roleEntity == null)
            {
                continue;
            }

            var roleCode = roleEntity.Ruolo;
            var best = await ResolveBestModelForRoleAsync(roleEntity.Id, ct).ConfigureAwait(false);
            if (best == null)
            {
                continue;
            }

            var agents = await _context.Agents
                .Where(a => a.IsActive && a.Role != null && a.Role.ToLower() == roleNorm)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (agents.Count == 0)
            {
                continue;
            }

            // Keep model_roles primary aligned with promoted model for this role.
            var roleRows = await _context.ModelRoles
                .Where(mr => mr.RoleId == roleEntity.Id)
                .OrderBy(mr => mr.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var primaryRow = roleRows.FirstOrDefault(mr => mr.ModelId == best.Value.ModelId);
            if (primaryRow != null)
            {
                foreach (var row in roleRows)
                {
                    var shouldBePrimary = row.Id == primaryRow.Id;
                    if (row.IsPrimary != shouldBePrimary)
                    {
                        row.IsPrimary = shouldBePrimary;
                        row.UpdatedAt = now;
                        anyChange = true;
                    }
                }
            }

            foreach (var agent in agents)
            {
                var oldModelId = agent.ModelId;
                string? oldModelName = null;
                if (oldModelId.HasValue && oldModelId.Value > 0)
                {
                    oldModelName = await _context.Models.AsNoTracking()
                        .Where(m => m.Id == oldModelId.Value)
                        .Select(m => m.Name)
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false);
                }

                var changed = !oldModelId.HasValue || oldModelId.Value != best.Value.ModelId;
                if (changed)
                {
                    agent.ModelId = best.Value.ModelId;
                    agent.UpdatedAt = now;
                    anyChange = true;

                    var promotedModel = GetTrackedModel(best.Value.ModelId);
                    if (promotedModel != null)
                    {
                        promotedModel.Promotions += 1;
                        promotedModel.UpdatedAt = now;
                        anyChange = true;
                    }

                    if (oldModelId.HasValue && oldModelId.Value > 0 && oldModelId.Value != best.Value.ModelId)
                    {
                        var demotedModel = GetTrackedModel(oldModelId.Value);
                        if (demotedModel != null)
                        {
                            demotedModel.Demotions += 1;
                            demotedModel.UpdatedAt = now;
                            anyChange = true;
                        }
                    }
                }

                results.Add(new PromotionResult(
                    roleCode ?? string.Empty,
                    agent.Id,
                    agent.Name ?? $"agent_{agent.Id}",
                    oldModelId,
                    oldModelName,
                    best.Value.ModelId,
                    best.Value.ModelName,
                    changed,
                    changed ? "best_success_rate" : "already_best"));

                if (changed)
                {
                    _logger?.Log(
                        "Information",
                        "PROMOTION",
                        $"PROMOTION source={source}; role={roleCode}; agent_id={agent.Id}; agent={agent.Name}; old_model={oldModelName ?? "(none)"}; new_model={best.Value.ModelName}",
                        result: "SUCCESS");
                }
            }
        }

        if (anyChange)
        {
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _logger?.Log(
            "Information",
            "PROMOTION",
            $"PROMOTION summary: source={source}; examined={results.Count}; changed={results.Count(r => r.Changed)}",
            result: "SUCCESS");

        return results;
    }

    private async Task<(int ModelId, string ModelName)?> ResolveBestModelForRoleAsync(int roleId, CancellationToken ct)
    {
        var candidates = await (from mr in _context.ModelRoles.AsNoTracking()
                                join m in _context.Models.AsNoTracking() on mr.ModelId equals m.Id
                                where mr.RoleId == roleId && mr.Enabled && m.Enabled
                                select new
                                {
                                    mr.Id,
                                    mr.ModelId,
                                    ModelName = m.Name,
                                    mr.UseCount,
                                    mr.UseSuccessed,
                                    mr.IsPrimary
                                })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return null;
        }

        var best = candidates
            .OrderByDescending(c => c.UseCount > 0)
            .ThenByDescending(c => c.UseCount > 0 ? (double)c.UseSuccessed / c.UseCount : 0.0)
            .ThenByDescending(c => c.UseCount)
            .ThenByDescending(c => c.IsPrimary)
            .ThenBy(c => c.Id)
            .First();

        return (best.ModelId, best.ModelName ?? string.Empty);
    }

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();
}
