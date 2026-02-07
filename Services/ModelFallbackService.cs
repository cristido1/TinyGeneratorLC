using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

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

    public ModelFallbackService(TinyGeneratorDbContext context, ICustomLogger? logger = null)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get fallback models for a specific role, ordered by success rate (descending).
    /// Returns only enabled model_roles.
    /// </summary>
    public List<ModelRole> GetFallbackModelsForRole(string roleCode, int? excludeModelId = null)
    {
        var role = _context.Roles.FirstOrDefault(r => r.Ruolo == roleCode);
        if (role == null)
        {
            _logger?.Log("Warning", "ModelFallback", $"Role '{roleCode}' not found in database.");
            return new List<ModelRole>();
        }

        var query = _context.ModelRoles
            .Include(mr => mr.Model)
            .Include(mr => mr.Role)
            .Where(mr => mr.RoleId == role.Id && mr.Enabled && !mr.IsPrimary);

        // Exclude the currently failing model if provided
        if (excludeModelId.HasValue)
        {
            query = query.Where(mr => mr.ModelId != excludeModelId.Value);
        }

        // Order by success rate (descending), then by use_count (descending) for stability
        var fallbacks = query.ToList()
            .OrderByDescending(mr => mr.SuccessRate)
            .ThenByDescending(mr => mr.UseCount)
            .ToList();

        _logger?.Log("Info", "ModelFallback", $"Found {fallbacks.Count} fallback models for role '{roleCode}' (excluding model {excludeModelId})");
        return fallbacks;
    }

    /// <summary>
    /// Record usage for the primary model of a role. This auto-creates a ModelRole row with is_primary=1 if missing.
    /// Primary rows are excluded from fallback selection.
    /// </summary>
    public void RecordPrimaryModelUsage(string roleCode, int modelId, bool success)
    {
        if (string.IsNullOrWhiteSpace(roleCode) || modelId <= 0)
        {
            return;
        }

        var role = _context.Roles.FirstOrDefault(r => r.Ruolo == roleCode);
        if (role == null)
        {
            _logger?.Log("Warning", "ModelFallback", $"Role '{roleCode}' not found in database. Cannot record primary usage.");
            return;
        }

        var mr = _context.ModelRoles
            .Where(x => x.RoleId == role.Id && x.ModelId == modelId)
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.Id)
            .FirstOrDefault();
        if (mr == null)
        {
            var now = DateTime.UtcNow.ToString("o");
            mr = new ModelRole
            {
                ModelId = modelId,
                RoleId = role.Id,
                IsPrimary = true,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.ModelRoles.Add(mr);
            _context.SaveChanges();
        }

        EnsureSinglePrimaryForRole(role.Id, modelId);

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
        mr.UpdatedAt = DateTime.UtcNow.ToString("o");
        _context.SaveChanges();

        _logger?.Log("Info", "ModelFallback", 
            $"Recorded usage for ModelRole {modelRoleId} (model={mr.ModelId}, role={mr.RoleId}): success={success}, total={mr.UseCount}, rate={mr.SuccessRate:P1}");
    }

    /// <summary>
    /// Aggregate model performance counters (tokens/durations) on model_roles for a specific role+model pair.
    /// If the row does not exist, it is auto-created as primary+enabled.
    /// </summary>
    public void RecordPrimaryModelPerformance(string roleCode, int modelId, ModelRolePerformanceDelta delta)
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

        var role = _context.Roles.FirstOrDefault(r => r.Ruolo == roleCode);
        if (role == null)
        {
            _logger?.Log("Warning", "ModelFallback", $"Role '{roleCode}' not found in database. Cannot record performance.");
            return;
        }

        // Prefer an existing primary row; fallback to any row for same model+role.
        var mr = _context.ModelRoles
            .Where(x => x.RoleId == role.Id && x.ModelId == modelId)
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.Id)
            .FirstOrDefault();

        if (mr == null)
        {
            var now = DateTime.UtcNow.ToString("o");
            mr = new ModelRole
            {
                ModelId = modelId,
                RoleId = role.Id,
                IsPrimary = true,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.ModelRoles.Add(mr);
            _context.SaveChanges();
        }

        EnsureSinglePrimaryForRole(role.Id, modelId);

        mr.TotalPromptTokens += Math.Max(0, delta.PromptTokens);
        mr.TotalOutputTokens += Math.Max(0, delta.OutputTokens);
        mr.TotalPromptTimeNs += Math.Max(0, delta.PromptTimeNs);
        mr.TotalGenTimeNs += Math.Max(0, delta.GenTimeNs);
        mr.TotalLoadTimeNs += Math.Max(0, delta.LoadTimeNs);
        mr.TotalTotalTimeNs += Math.Max(0, delta.TotalTimeNs);
        mr.UpdatedAt = DateTime.UtcNow.ToString("o");
        _context.SaveChanges();
    }

    private void EnsureSinglePrimaryForRole(int roleId, int primaryModelId)
    {
        if (roleId <= 0 || primaryModelId <= 0)
        {
            return;
        }

        var now = DateTime.UtcNow.ToString("o");
        var rows = _context.ModelRoles
            .Where(x => x.RoleId == roleId)
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
        Func<ModelRole, bool>? shouldTryModelRole = null)
    {
        var fallbacks = GetFallbackModelsForRole(roleCode, primaryModelId);
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

            _logger?.Log("Info", "ModelFallback", 
                $"Trying fallback model: {modelRole.Model?.Name} (role={roleCode}, success_rate={modelRole.SuccessRate:P1})");

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
}
