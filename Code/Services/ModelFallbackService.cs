using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;
using TinyGenerator.Configuration;
using Microsoft.Extensions.Options;

namespace TinyGenerator.Services;

/// <summary>
/// Service that manages model fallback logic for agent operations.
/// When an agent fails after all retries, this service finds alternative models
/// with the same role and tries them in order of success rate.
/// </summary>
public class ModelFallbackService
{
    public sealed record ModelRolePerformanceDelta(
        long PromptTokens,
        long OutputTokens,
        long PromptTimeNs,
        long GenTimeNs,
        long LoadTimeNs,
        long TotalTimeNs);

    private readonly TinyGeneratorDbContext _context;
    private readonly ICustomLogger? _logger;
    private readonly ModelFallbackOptions _options;

    public ModelFallbackService(
        TinyGeneratorDbContext context,
        ICustomLogger? logger = null,
        IOptions<ModelFallbackOptions>? options = null)
    {
        _context = context;
        _logger = logger;
        _options = options?.Value ?? new ModelFallbackOptions();
    }

    /// <summary>
    /// Get fallback models for a specific role, ordered by UCB-like score:
    /// success_rate + C * sqrt(log(T + 1) / (trials + 1))
    /// where trials = success + fail for the model, T = total trials in the role.
    /// Returns only active model_roles.
    /// </summary>
    public List<ModelRole> GetFallbackModelsForRole(string roleCode, int? excludeModelId = null, int? agentId = null)
    {
        var role = _context.Roles.FirstOrDefault(r => r.Name == roleCode);
        if (role == null)
        {
            _logger?.Log("Warning", "ModelFallback", $"Role '{roleCode}' not found in database.");
            return new List<ModelRole>();
        }

        var resolvedAgentId = ResolveAgentIdForRole(roleCode, agentId);
        if (!resolvedAgentId.HasValue || resolvedAgentId.Value <= 0)
        {
            _logger?.Log("Warning", "ModelFallback", $"No agent found for role '{roleCode}'.");
            return new List<ModelRole>();
        }

        var query = _context.ModelRoles
            .Include(mr => mr.Model)
            .Include(mr => mr.Role)
            .Include(mr => mr.Agent)
            .Where(mr => mr.RoleId == role.Id && mr.AgentId == resolvedAgentId.Value && mr.IsActive && !mr.IsPrimary);

        // Exclude the currently failing model if provided
        if (excludeModelId.HasValue)
        {
            query = query.Where(mr => mr.ModelId != excludeModelId.Value);
        }

        var candidates = query.ToList();
        var roleTotalTrials = _context.ModelRoles
            .Where(mr => mr.RoleId == role.Id && mr.AgentId == resolvedAgentId.Value)
            .Sum(mr => (double?)(mr.UseSuccessed + mr.UseFailed)) ?? 0d;
        var c = Math.Max(0d, _options.ExplorationConstant);
        var logTerm = Math.Log(roleTotalTrials + 1d);

        double Score(ModelRole mr)
        {
            var successes = Math.Max(0, mr.UseSuccessed);
            var failures = Math.Max(0, mr.UseFailed);
            var denom = successes + failures + 1d;
            var successRate = successes / denom;
            var explorationBonus = c * Math.Sqrt(logTerm / denom);
            return successRate + explorationBonus;
        }

        var fallbacks = candidates
            .OrderByDescending(Score)
            .ThenByDescending(mr => Math.Max(0, mr.UseSuccessed) / (Math.Max(0, mr.UseSuccessed) + Math.Max(0, mr.UseFailed) + 1d))
            .ThenByDescending(mr => mr.UseCount)
            .ToList();

        _logger?.Log(
            "Info",
            "ModelFallback",
            $"Found {fallbacks.Count} fallback models for role '{roleCode}' (agent={resolvedAgentId.Value}, excluding model {excludeModelId}, C={c:0.###}, T={roleTotalTrials:0})");
        return fallbacks;
    }

    /// <summary>
    /// Record usage for the primary model of a role. This auto-creates a ModelRole row with is_primary=1 if missing.
    /// Primary rows are excluded from fallback selection.
    /// </summary>
    public void RecordPrimaryModelUsage(string roleCode, int modelId, bool success, int? agentId = null)
    {
        if (string.IsNullOrWhiteSpace(roleCode) || modelId <= 0)
        {
            return;
        }

        var role = _context.Roles.FirstOrDefault(r => r.Name == roleCode);
        if (role == null)
        {
            _logger?.Log("Warning", "ModelFallback", $"Role '{roleCode}' not found in database. Cannot record primary usage.");
            return;
        }

        var resolvedAgentId = ResolveAgentIdForRole(roleCode, agentId);
        if (!resolvedAgentId.HasValue || resolvedAgentId.Value <= 0)
        {
            _logger?.Log("Warning", "ModelFallback", $"No agent found for role '{roleCode}'. Cannot record primary usage.");
            return;
        }

        var mr = _context.ModelRoles
            .Where(x => x.RoleId == role.Id && x.ModelId == modelId && x.AgentId == resolvedAgentId.Value)
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.Id)
            .FirstOrDefault();
        if (mr == null)
        {
            var now = DateTime.UtcNow;
            mr = new ModelRole
            {
                ModelId = modelId,
                RoleId = role.Id,
                AgentId = resolvedAgentId.Value,
                IsPrimary = true,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.ModelRoles.Add(mr);
            _context.SaveChanges();
        }

        EnsureSinglePrimaryForRole(role.Id, resolvedAgentId.Value, modelId);

        RecordModelRoleUsage(mr.Id, success);
    }

    /// <summary>
    /// Record a model role usage (success or failure).
    /// Updates use_count, use_successed/use_failed, and last_use timestamp.
    /// </summary>
    public void RecordModelRoleUsage(int modelRoleId, bool success)
    {
        var mr = _context.ModelRoles.Find(modelRoleId);
        if (mr == null)
        {
            _logger?.Log("Warning", "ModelFallback", $"ModelRole ID {modelRoleId} not found.");
            return;
        }

        mr.UseCount++;
        if (success)
        {
            mr.UseSuccessed++;
        }
        else
        {
            mr.UseFailed++;
        }
        mr.LastUse = DateTime.UtcNow.ToString("o");
        mr.UpdatedAt = DateTime.UtcNow;
        _context.SaveChanges();

        _logger?.Log("Info", "ModelFallback", 
            $"Recorded usage for ModelRole {modelRoleId} (model={mr.ModelId}, role={mr.RoleId}): success={success}, total={mr.UseCount}, rate={mr.SuccessRate:P1}");
    }

    /// <summary>
    /// Aggregate model performance counters (tokens/durations) on model_roles for a specific role+model pair.
    /// If the row does not exist, it is auto-created as primary+active.
    /// </summary>
    public void RecordPrimaryModelPerformance(string roleCode, int modelId, ModelRolePerformanceDelta delta, int? agentId = null)
    {
        if (string.IsNullOrWhiteSpace(roleCode) || modelId <= 0)
        {
            return;
        }

        var hasAnyDelta = delta.PromptTokens > 0 ||
                          delta.OutputTokens > 0 ||
                          delta.PromptTimeNs > 0 ||
                          delta.GenTimeNs > 0 ||
                          delta.LoadTimeNs > 0 ||
                          delta.TotalTimeNs > 0;
        if (!hasAnyDelta)
        {
            return;
        }

        var role = _context.Roles.FirstOrDefault(r => r.Name == roleCode);
        if (role == null)
        {
            _logger?.Log("Warning", "ModelFallback", $"Role '{roleCode}' not found in database. Cannot record performance.");
            return;
        }

        var resolvedAgentId = ResolveAgentIdForRole(roleCode, agentId);
        if (!resolvedAgentId.HasValue || resolvedAgentId.Value <= 0)
        {
            _logger?.Log("Warning", "ModelFallback", $"No agent found for role '{roleCode}'. Cannot record performance.");
            return;
        }

        // Prefer an existing primary row; fallback to any row for same model+role+agent.
        var mr = _context.ModelRoles
            .Where(x => x.RoleId == role.Id && x.ModelId == modelId && x.AgentId == resolvedAgentId.Value)
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.Id)
            .FirstOrDefault();

        if (mr == null)
        {
            var now = DateTime.UtcNow;
            mr = new ModelRole
            {
                ModelId = modelId,
                RoleId = role.Id,
                AgentId = resolvedAgentId.Value,
                IsPrimary = true,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.ModelRoles.Add(mr);
            _context.SaveChanges();
        }

        EnsureSinglePrimaryForRole(role.Id, resolvedAgentId.Value, modelId);

        mr.TotalPromptTokens += Math.Max(0, delta.PromptTokens);
        mr.TotalOutputTokens += Math.Max(0, delta.OutputTokens);
        mr.TotalPromptTimeNs += Math.Max(0, delta.PromptTimeNs);
        mr.TotalGenTimeNs += Math.Max(0, delta.GenTimeNs);
        mr.TotalLoadTimeNs += Math.Max(0, delta.LoadTimeNs);
        mr.TotalTotalTimeNs += Math.Max(0, delta.TotalTimeNs);
        mr.UpdatedAt = DateTime.UtcNow;
        _context.SaveChanges();
    }

    private void EnsureSinglePrimaryForRole(int roleId, int agentId, int primaryModelId)
    {
        if (roleId <= 0 || agentId <= 0 || primaryModelId <= 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var rows = _context.ModelRoles
            .Where(x => x.RoleId == roleId && x.AgentId == agentId)
            .OrderBy(x => x.Id)
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var primaryRow = rows.FirstOrDefault(x => x.ModelId == primaryModelId);
        if (primaryRow == null)
        {
            return;
        }

        var changed = false;
        foreach (var row in rows)
        {
            var shouldBePrimary = row.Id == primaryRow.Id;
            if (row.IsPrimary != shouldBePrimary)
            {
                row.IsPrimary = shouldBePrimary;
                row.UpdatedAt = now;
                changed = true;
            }
        }

        if (changed)
        {
            _context.SaveChanges();
        }
    }

    /// <summary>
    /// Execute an operation with fallback support. If the primary operation fails,
    /// tries alternative models from model_roles ordered by success rate.
    /// </summary>
    /// <typeparam name="T">Result type</typeparam>
    /// <param name="roleCode">Role code (e.g. "formatter", "writer")</param>
    /// <param name="primaryModelId">Primary model ID that failed</param>
    /// <param name="operationAsync">Async operation that takes a ModelRole and returns result</param>
    /// <param name="validateResult">Optional validation function - returns true if result is acceptable</param>
    /// <param name="shouldTryModelRole">Optional predicate to skip certain ModelRole entries (e.g. already tried in this command). Skipped entries are not recorded as failures.</param>
    /// <returns>Result and the ModelRole that succeeded, or null if all failed</returns>
    public async Task<(T? result, ModelRole? successfulModelRole)> ExecuteWithFallbackAsync<T>(
        string roleCode,
        int? primaryModelId,
        Func<ModelRole, Task<T>> operationAsync,
        Func<T, bool>? validateResult = null,
        Func<ModelRole, bool>? shouldTryModelRole = null,
        int? agentId = null)
    {
        var fallbacks = GetFallbackModelsForRole(roleCode, primaryModelId, agentId);
        if (!fallbacks.Any())
        {
            _logger?.Log("Warning", "ModelFallback", $"No fallback models available for role '{roleCode}'.");
            return (default(T), null);
        }

        foreach (var modelRole in fallbacks)
        {
            if (shouldTryModelRole != null)
            {
                bool ok;
                try
                {
                    ok = shouldTryModelRole(modelRole);
                }
                catch (Exception ex)
                {
                    _logger?.Log("Warning", "ModelFallback", $"shouldTryModelRole threw for {modelRole.Model?.Name} (role={roleCode}): {ex.Message}");
                    ok = false;
                }

                if (!ok)
                {
                    _logger?.Log("Info", "ModelFallback", $"Skipping fallback model: {modelRole.Model?.Name} (role={roleCode})");
                    continue;
                }
            }

            var succ = Math.Max(0, modelRole.UseSuccessed);
            var fail = Math.Max(0, modelRole.UseFailed);
            var denom = succ + fail + 1d;
            var successRate = succ / denom;
            var roleTotalTrials = _context.ModelRoles
                .Where(mr => mr.RoleId == modelRole.RoleId)
                .Sum(mr => (double?)(mr.UseSuccessed + mr.UseFailed)) ?? 0d;
            var c = Math.Max(0d, _options.ExplorationConstant);
            var explorationBonus = c * Math.Sqrt(Math.Log(roleTotalTrials + 1d) / denom);
            var score = successRate + explorationBonus;

            _logger?.Log(
                "Info",
                "ModelFallback",
                $"Trying fallback model: {modelRole.Model?.Name} (role={roleCode}, score={score:0.####}, success_rate={successRate:P2}, exploration_bonus={explorationBonus:0.####}, C={c:0.###}, T={roleTotalTrials:0})");

            try
            {
                var result = await operationAsync(modelRole);
                
                // Validate if validator provided
                bool isValid = validateResult?.Invoke(result) ?? true;
                
                if (isValid)
                {
                    RecordModelRoleUsage(modelRole.Id, success: true);
                    _logger?.Log("Info", "ModelFallback", 
                        $"Fallback model {modelRole.Model?.Name} succeeded for role '{roleCode}'.");
                    return (result, modelRole);
                }
                else
                {
                    RecordModelRoleUsage(modelRole.Id, success: false);
                    _logger?.Log("Warning", "ModelFallback", 
                        $"Fallback model {modelRole.Model?.Name} produced invalid result for role '{roleCode}'.");
                }
            }
            catch (Exception ex)
            {
                RecordModelRoleUsage(modelRole.Id, success: false);
                _logger?.Log("Error", "ModelFallback", 
                    $"Fallback model {modelRole.Model?.Name} failed for role '{roleCode}': {ex.Message}");
            }
        }

        _logger?.Log("Error", "ModelFallback", $"All fallback models exhausted for role '{roleCode}'. Operation failed definitively.");
        return (default(T), null);
    }

    private int? ResolveAgentIdForRole(string roleCode, int? preferredAgentId)
    {
        if (preferredAgentId.HasValue && preferredAgentId.Value > 0)
        {
            var preferredExists = _context.Agents.Any(a => a.Id == preferredAgentId.Value);
            if (preferredExists)
            {
                return preferredAgentId.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(roleCode))
        {
            var roleNorm = roleCode.Trim().ToLower();
            var byRole = _context.Agents
                .Where(a => a.Role != null && a.Role.ToLower() == roleNorm)
                .OrderBy(a => a.Id)
                .Select(a => (int?)a.Id)
                .FirstOrDefault();
            if (byRole.HasValue && byRole.Value > 0)
            {
                return byRole.Value;
            }
        }

        return _context.Agents
            .OrderBy(a => a.Id)
            .Select(a => (int?)a.Id)
            .FirstOrDefault();
    }
}
