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
        var agentHints = await _context.Logs.AsNoTracking()
            .Where(l => l.Message != null
                        && l.Message.Contains(runToken)
                        && l.AgentName != null
                        && l.AgentName != string.Empty)
            .Select(l => l.AgentName!)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var agentIds = await ResolveAgentIdsFromHintsAsync(agentHints, ct).ConfigureAwait(false);
        return await PromoteBestModelsForAgentsAsync(agentIds, $"{source}:{runId}", ct).ConfigureAwait(false);
    }

    public async Task<List<PromotionResult>> PromoteBestModelsForAllRolesAsync(
        string source = "manual",
        CancellationToken ct = default)
    {
        var agentIds = await _context.Agents.AsNoTracking()
            .Where(a => a.IsActive && a.Role != null && a.Role != string.Empty)
            .Select(a => a.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return await PromoteBestModelsForAgentsAsync(agentIds, source, ct).ConfigureAwait(false);
    }

    private async Task<List<int>> ResolveAgentIdsFromHintsAsync(IEnumerable<string> hints, CancellationToken ct)
    {
        var normalizedHints = hints
            .Select(Normalize)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedHints.Count == 0)
        {
            return new List<int>();
        }

        var agents = await _context.Agents.AsNoTracking()
            .Where(a => a.IsActive)
            .Select(a => new { a.Id, a.Name, a.Role })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var outAgents = new HashSet<int>();
        foreach (var hint in normalizedHints)
        {
            var agentsByRole = agents
                .Where(a => !string.IsNullOrWhiteSpace(a.Role) &&
                            string.Equals(Normalize(a.Role), hint, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Id)
                .Distinct()
                .ToList();

            foreach (var a in agents)
            {
                var byName = !string.IsNullOrWhiteSpace(a.Name) &&
                             string.Equals(Normalize(a.Name), hint, StringComparison.OrdinalIgnoreCase);
                if (byName)
                {
                    outAgents.Add(a.Id);
                }
            }

            // Use role hint only when it identifies exactly one active agent.
            if (agentsByRole.Count == 1)
            {
                outAgents.Add(agentsByRole[0]);
            }
        }

        return outAgents.ToList();
    }

    private async Task<List<PromotionResult>> PromoteBestModelsForAgentsAsync(
        IEnumerable<int> agentIds,
        string source,
        CancellationToken ct)
    {
        var normalizedAgentIds = agentIds
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        if (normalizedAgentIds.Count == 0)
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

        foreach (var agentId in normalizedAgentIds)
        {
            ct.ThrowIfCancellationRequested();

            var agent = await _context.Agents
                .FirstOrDefaultAsync(a => a.Id == agentId && a.IsActive, ct)
                .ConfigureAwait(false);
            if (agent == null || string.IsNullOrWhiteSpace(agent.Role))
            {
                continue;
            }

            var roleCode = agent.Role;
            var roleNorm = Normalize(roleCode);
            var roleEntity = await _context.Roles
                .FirstOrDefaultAsync(r => r.Name != null && r.Name.ToLower() == roleNorm, ct)
                .ConfigureAwait(false);
            if (roleEntity == null)
            {
                continue;
            }

            var best = await ResolveBestModelForAgentAsync(roleEntity.Id, agent.Id, ct).ConfigureAwait(false);
            if (best == null)
            {
                continue;
            }

            // Keep model_roles primary aligned with promoted model only for role+agent.
            var roleRows = await _context.ModelRoles
                .Where(mr => mr.RoleId == roleEntity.Id && mr.AgentId == agent.Id)
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

    private async Task<(int ModelId, string ModelName)?> ResolveBestModelForAgentAsync(int roleId, int agentId, CancellationToken ct)
    {
        var candidates = await (from mr in _context.ModelRoles.AsNoTracking()
                                join m in _context.Models.AsNoTracking() on mr.ModelId equals m.Id
                                where mr.RoleId == roleId
                                      && mr.AgentId == agentId
                                      && mr.Enabled
                                      && m.Enabled
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
