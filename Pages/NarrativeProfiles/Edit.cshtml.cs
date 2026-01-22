using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.NarrativeProfiles;

public class EditModel : PageModel
{
    private readonly TinyGeneratorDbContext _context;

    public EditModel(TinyGeneratorDbContext context)
    {
        _context = context;
    }

    [BindProperty]
    public NarrativeProfile Item { get; set; } = new();

    public bool IsNew => Item.Id == 0;

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null)
        {
            Item = new NarrativeProfile();
            return Page();
        }

        var existing = await _context.NarrativeProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id.Value);
        if (existing is null)
        {
            return NotFound();
        }

        Item = existing;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveProfileAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (Item.Id == 0)
        {
            _context.NarrativeProfiles.Add(Item);
        }
        else
        {
            var existing = await _context.NarrativeProfiles.FirstOrDefaultAsync(p => p.Id == Item.Id);
            if (existing is null)
            {
                return NotFound();
            }

            existing.Name = Item.Name;
            existing.Description = Item.Description;
            existing.BaseSystemPrompt = Item.BaseSystemPrompt;
            existing.StylePrompt = Item.StylePrompt;
            existing.PovListJson = Item.PovListJson;
        }

        await _context.SaveChangesAsync();
        return RedirectToPage("./Edit", new { id = Item.Id });
    }

    public async Task<IActionResult> OnGetResourcesAsync(int profileId)
    {
        if (profileId <= 0) return new JsonResult(Array.Empty<object>());

        var rows = await _context.NarrativeResources
            .AsNoTracking()
            .Where(r => r.NarrativeProfileId == profileId)
            .OrderBy(r => r.Name)
            .Select(r => new
            {
                id = r.Id,
                narrativeProfileId = r.NarrativeProfileId,
                name = r.Name,
                initialValue = r.InitialValue,
                minValue = r.MinValue,
                maxValue = r.MaxValue
            })
            .ToListAsync();

        return new JsonResult(rows);
    }

    public async Task<IActionResult> OnPostUpsertResourceAsync([FromBody] NarrativeResourceInput input)
    {
        if (input is null) return new JsonResult(new { ok = false, error = "Invalid payload" }) { StatusCode = 400 };
        if (input.NarrativeProfileId <= 0) return new JsonResult(new { ok = false, error = "Missing profileId" }) { StatusCode = 400 };
        if (string.IsNullOrWhiteSpace(input.Name)) return new JsonResult(new { ok = false, error = "Name is required" }) { StatusCode = 400 };
        if (input.MinValue > input.MaxValue) return new JsonResult(new { ok = false, error = "Min cannot be > Max" }) { StatusCode = 400 };
        if (input.InitialValue < input.MinValue || input.InitialValue > input.MaxValue) return new JsonResult(new { ok = false, error = "Initial must be within Min/Max" }) { StatusCode = 400 };

        var profileExists = await _context.NarrativeProfiles.AsNoTracking().AnyAsync(p => p.Id == input.NarrativeProfileId);
        if (!profileExists) return new JsonResult(new { ok = false, error = "Profile not found" }) { StatusCode = 404 };

        if (input.Id <= 0)
        {
            var entity = new NarrativeResource
            {
                NarrativeProfileId = input.NarrativeProfileId,
                Name = input.Name.Trim(),
                InitialValue = input.InitialValue,
                MinValue = input.MinValue,
                MaxValue = input.MaxValue
            };
            _context.NarrativeResources.Add(entity);
            await _context.SaveChangesAsync();
            return new JsonResult(new { ok = true, id = entity.Id });
        }
        else
        {
            var entity = await _context.NarrativeResources.FirstOrDefaultAsync(r => r.Id == input.Id && r.NarrativeProfileId == input.NarrativeProfileId);
            if (entity is null) return new JsonResult(new { ok = false, error = "Resource not found" }) { StatusCode = 404 };

            entity.Name = input.Name.Trim();
            entity.InitialValue = input.InitialValue;
            entity.MinValue = input.MinValue;
            entity.MaxValue = input.MaxValue;
            await _context.SaveChangesAsync();
            return new JsonResult(new { ok = true, id = entity.Id });
        }
    }

    public async Task<IActionResult> OnPostDeleteResourceAsync([FromBody] DeleteInput input)
    {
        if (input is null || input.Id <= 0 || input.NarrativeProfileId <= 0)
        {
            return new JsonResult(new { ok = false, error = "Invalid payload" }) { StatusCode = 400 };
        }

        var entity = await _context.NarrativeResources.FirstOrDefaultAsync(r => r.Id == input.Id && r.NarrativeProfileId == input.NarrativeProfileId);
        if (entity is null) return new JsonResult(new { ok = false, error = "Resource not found" }) { StatusCode = 404 };

        _context.NarrativeResources.Remove(entity);
        await _context.SaveChangesAsync();
        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnGetMicroObjectivesAsync(int profileId)
    {
        if (profileId <= 0) return new JsonResult(Array.Empty<object>());

        var rows = await _context.MicroObjectives
            .AsNoTracking()
            .Where(m => m.NarrativeProfileId == profileId)
            .OrderBy(m => m.Code)
            .Select(m => new
            {
                id = m.Id,
                narrativeProfileId = m.NarrativeProfileId,
                code = m.Code,
                description = m.Description,
                difficulty = m.Difficulty
            })
            .ToListAsync();

        return new JsonResult(rows);
    }

    public async Task<IActionResult> OnPostUpsertMicroObjectiveAsync([FromBody] MicroObjectiveInput input)
    {
        if (input is null) return new JsonResult(new { ok = false, error = "Invalid payload" }) { StatusCode = 400 };
        if (input.NarrativeProfileId <= 0) return new JsonResult(new { ok = false, error = "Missing profileId" }) { StatusCode = 400 };
        if (string.IsNullOrWhiteSpace(input.Code)) return new JsonResult(new { ok = false, error = "Code is required" }) { StatusCode = 400 };
        if (input.Difficulty < 0 || input.Difficulty > 10) return new JsonResult(new { ok = false, error = "Difficulty must be 0..10" }) { StatusCode = 400 };

        var profileExists = await _context.NarrativeProfiles.AsNoTracking().AnyAsync(p => p.Id == input.NarrativeProfileId);
        if (!profileExists) return new JsonResult(new { ok = false, error = "Profile not found" }) { StatusCode = 404 };

        var code = input.Code.Trim();
        if (input.Id <= 0)
        {
            var exists = await _context.MicroObjectives.AsNoTracking().AnyAsync(m => m.NarrativeProfileId == input.NarrativeProfileId && m.Code == code);
            if (exists) return new JsonResult(new { ok = false, error = "Code already exists for this profile" }) { StatusCode = 409 };

            var entity = new MicroObjective
            {
                NarrativeProfileId = input.NarrativeProfileId,
                Code = code,
                Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
                Difficulty = input.Difficulty
            };
            _context.MicroObjectives.Add(entity);
            await _context.SaveChangesAsync();
            return new JsonResult(new { ok = true, id = entity.Id });
        }
        else
        {
            var entity = await _context.MicroObjectives.FirstOrDefaultAsync(m => m.Id == input.Id && m.NarrativeProfileId == input.NarrativeProfileId);
            if (entity is null) return new JsonResult(new { ok = false, error = "Micro objective not found" }) { StatusCode = 404 };

            var exists = await _context.MicroObjectives.AsNoTracking()
                .AnyAsync(m => m.NarrativeProfileId == input.NarrativeProfileId && m.Code == code && m.Id != input.Id);
            if (exists) return new JsonResult(new { ok = false, error = "Code already exists for this profile" }) { StatusCode = 409 };

            entity.Code = code;
            entity.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
            entity.Difficulty = input.Difficulty;
            await _context.SaveChangesAsync();
            return new JsonResult(new { ok = true, id = entity.Id });
        }
    }

    public async Task<IActionResult> OnPostDeleteMicroObjectiveAsync([FromBody] DeleteInput input)
    {
        if (input is null || input.Id <= 0 || input.NarrativeProfileId <= 0)
        {
            return new JsonResult(new { ok = false, error = "Invalid payload" }) { StatusCode = 400 };
        }

        var entity = await _context.MicroObjectives.FirstOrDefaultAsync(m => m.Id == input.Id && m.NarrativeProfileId == input.NarrativeProfileId);
        if (entity is null) return new JsonResult(new { ok = false, error = "Micro objective not found" }) { StatusCode = 404 };

        _context.MicroObjectives.Remove(entity);
        await _context.SaveChangesAsync();
        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnGetFailureRulesAsync(int profileId)
    {
        if (profileId <= 0) return new JsonResult(Array.Empty<object>());

        var rows = await _context.FailureRules
            .AsNoTracking()
            .Where(r => r.NarrativeProfileId == profileId)
            .OrderBy(r => r.TriggerType)
            .ThenBy(r => r.Id)
            .Select(r => new
            {
                id = r.Id,
                narrativeProfileId = r.NarrativeProfileId,
                triggerType = r.TriggerType,
                description = r.Description
            })
            .ToListAsync();

        return new JsonResult(rows);
    }

    public async Task<IActionResult> OnPostUpsertFailureRuleAsync([FromBody] FailureRuleInput input)
    {
        if (input is null) return new JsonResult(new { ok = false, error = "Invalid payload" }) { StatusCode = 400 };
        if (input.NarrativeProfileId <= 0) return new JsonResult(new { ok = false, error = "Missing profileId" }) { StatusCode = 400 };
        if (string.IsNullOrWhiteSpace(input.TriggerType)) return new JsonResult(new { ok = false, error = "Trigger type is required" }) { StatusCode = 400 };

        var profileExists = await _context.NarrativeProfiles.AsNoTracking().AnyAsync(p => p.Id == input.NarrativeProfileId);
        if (!profileExists) return new JsonResult(new { ok = false, error = "Profile not found" }) { StatusCode = 404 };

        var trigger = input.TriggerType.Trim();

        if (input.Id <= 0)
        {
            var entity = new FailureRule
            {
                NarrativeProfileId = input.NarrativeProfileId,
                TriggerType = trigger,
                Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim()
            };
            _context.FailureRules.Add(entity);
            await _context.SaveChangesAsync();
            return new JsonResult(new { ok = true, id = entity.Id });
        }
        else
        {
            var entity = await _context.FailureRules.FirstOrDefaultAsync(r => r.Id == input.Id && r.NarrativeProfileId == input.NarrativeProfileId);
            if (entity is null) return new JsonResult(new { ok = false, error = "Failure rule not found" }) { StatusCode = 404 };

            entity.TriggerType = trigger;
            entity.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
            await _context.SaveChangesAsync();
            return new JsonResult(new { ok = true, id = entity.Id });
        }
    }

    public async Task<IActionResult> OnPostDeleteFailureRuleAsync([FromBody] DeleteInput input)
    {
        if (input is null || input.Id <= 0 || input.NarrativeProfileId <= 0)
        {
            return new JsonResult(new { ok = false, error = "Invalid payload" }) { StatusCode = 400 };
        }

        var entity = await _context.FailureRules.FirstOrDefaultAsync(r => r.Id == input.Id && r.NarrativeProfileId == input.NarrativeProfileId);
        if (entity is null) return new JsonResult(new { ok = false, error = "Failure rule not found" }) { StatusCode = 404 };

        _context.FailureRules.Remove(entity);
        await _context.SaveChangesAsync();
        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnGetConsequenceRulesAsync(int profileId)
    {
        if (profileId <= 0) return new JsonResult(Array.Empty<object>());

        var rows = await _context.ConsequenceRules
            .AsNoTracking()
            .Where(r => r.NarrativeProfileId == profileId)
            .OrderBy(r => r.Id)
            .Select(r => new
            {
                id = r.Id,
                narrativeProfileId = r.NarrativeProfileId,
                description = r.Description,
                impactsCount = _context.ConsequenceImpacts.Count(i => i.ConsequenceRuleId == r.Id)
            })
            .ToListAsync();

        return new JsonResult(rows);
    }

    public async Task<IActionResult> OnPostUpsertConsequenceRuleAsync([FromBody] ConsequenceRuleInput input)
    {
        if (input is null) return new JsonResult(new { ok = false, error = "Invalid payload" }) { StatusCode = 400 };
        if (input.NarrativeProfileId <= 0) return new JsonResult(new { ok = false, error = "Missing profileId" }) { StatusCode = 400 };

        var profileExists = await _context.NarrativeProfiles.AsNoTracking().AnyAsync(p => p.Id == input.NarrativeProfileId);
        if (!profileExists) return new JsonResult(new { ok = false, error = "Profile not found" }) { StatusCode = 404 };

        if (input.Id <= 0)
        {
            var entity = new ConsequenceRule
            {
                NarrativeProfileId = input.NarrativeProfileId,
                Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim()
            };
            _context.ConsequenceRules.Add(entity);
            await _context.SaveChangesAsync();
            return new JsonResult(new { ok = true, id = entity.Id });
        }
        else
        {
            var entity = await _context.ConsequenceRules.FirstOrDefaultAsync(r => r.Id == input.Id && r.NarrativeProfileId == input.NarrativeProfileId);
            if (entity is null) return new JsonResult(new { ok = false, error = "Consequence rule not found" }) { StatusCode = 404 };

            entity.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
            await _context.SaveChangesAsync();
            return new JsonResult(new { ok = true, id = entity.Id });
        }
    }

    public async Task<IActionResult> OnPostDeleteConsequenceRuleAsync([FromBody] DeleteInput input)
    {
        if (input is null || input.Id <= 0 || input.NarrativeProfileId <= 0)
        {
            return new JsonResult(new { ok = false, error = "Invalid payload" }) { StatusCode = 400 };
        }

        var entity = await _context.ConsequenceRules.FirstOrDefaultAsync(r => r.Id == input.Id && r.NarrativeProfileId == input.NarrativeProfileId);
        if (entity is null) return new JsonResult(new { ok = false, error = "Consequence rule not found" }) { StatusCode = 404 };

        var impacts = await _context.ConsequenceImpacts.Where(i => i.ConsequenceRuleId == entity.Id).ToListAsync();
        if (impacts.Count > 0)
        {
            _context.ConsequenceImpacts.RemoveRange(impacts);
        }

        _context.ConsequenceRules.Remove(entity);
        await _context.SaveChangesAsync();
        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnGetConsequenceImpactsAsync(int consequenceRuleId)
    {
        if (consequenceRuleId <= 0) return new JsonResult(Array.Empty<object>());

        var rows = await _context.ConsequenceImpacts
            .AsNoTracking()
            .Where(i => i.ConsequenceRuleId == consequenceRuleId)
            .OrderBy(i => i.ResourceName)
            .Select(i => new
            {
                id = i.Id,
                consequenceRuleId = i.ConsequenceRuleId,
                resourceName = i.ResourceName,
                deltaValue = i.DeltaValue
            })
            .ToListAsync();

        return new JsonResult(rows);
    }

    public async Task<IActionResult> OnPostUpsertConsequenceImpactAsync([FromBody] ConsequenceImpactInput input)
    {
        if (input is null) return new JsonResult(new { ok = false, error = "Invalid payload" }) { StatusCode = 400 };
        if (input.ConsequenceRuleId <= 0) return new JsonResult(new { ok = false, error = "Missing consequenceRuleId" }) { StatusCode = 400 };
        if (string.IsNullOrWhiteSpace(input.ResourceName)) return new JsonResult(new { ok = false, error = "Resource name is required" }) { StatusCode = 400 };

        var ruleExists = await _context.ConsequenceRules.AsNoTracking().AnyAsync(r => r.Id == input.ConsequenceRuleId);
        if (!ruleExists) return new JsonResult(new { ok = false, error = "Consequence rule not found" }) { StatusCode = 404 };

        var resource = input.ResourceName.Trim();

        if (input.Id <= 0)
        {
            var entity = new ConsequenceImpact
            {
                ConsequenceRuleId = input.ConsequenceRuleId,
                ResourceName = resource,
                DeltaValue = input.DeltaValue
            };
            _context.ConsequenceImpacts.Add(entity);
            await _context.SaveChangesAsync();
            return new JsonResult(new { ok = true, id = entity.Id });
        }
        else
        {
            var entity = await _context.ConsequenceImpacts.FirstOrDefaultAsync(i => i.Id == input.Id && i.ConsequenceRuleId == input.ConsequenceRuleId);
            if (entity is null) return new JsonResult(new { ok = false, error = "Impact not found" }) { StatusCode = 404 };

            entity.ResourceName = resource;
            entity.DeltaValue = input.DeltaValue;
            await _context.SaveChangesAsync();
            return new JsonResult(new { ok = true, id = entity.Id });
        }
    }

    public async Task<IActionResult> OnPostDeleteConsequenceImpactAsync([FromBody] DeleteImpactInput input)
    {
        if (input is null || input.Id <= 0 || input.ConsequenceRuleId <= 0)
        {
            return new JsonResult(new { ok = false, error = "Invalid payload" }) { StatusCode = 400 };
        }

        var entity = await _context.ConsequenceImpacts.FirstOrDefaultAsync(i => i.Id == input.Id && i.ConsequenceRuleId == input.ConsequenceRuleId);
        if (entity is null) return new JsonResult(new { ok = false, error = "Impact not found" }) { StatusCode = 404 };

        _context.ConsequenceImpacts.Remove(entity);
        await _context.SaveChangesAsync();
        return new JsonResult(new { ok = true });
    }

    public sealed class NarrativeResourceInput
    {
        public int Id { get; set; }
        public int NarrativeProfileId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int InitialValue { get; set; }
        public int MinValue { get; set; }
        public int MaxValue { get; set; }
    }

    public sealed class MicroObjectiveInput
    {
        public int Id { get; set; }
        public int NarrativeProfileId { get; set; }
        public string Code { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int Difficulty { get; set; }
    }

    public sealed class DeleteInput
    {
        public int Id { get; set; }
        public int NarrativeProfileId { get; set; }
    }

    public sealed class FailureRuleInput
    {
        public int Id { get; set; }
        public int NarrativeProfileId { get; set; }
        public string TriggerType { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    public sealed class ConsequenceRuleInput
    {
        public int Id { get; set; }
        public int NarrativeProfileId { get; set; }
        public string? Description { get; set; }
    }

    public sealed class ConsequenceImpactInput
    {
        public int Id { get; set; }
        public int ConsequenceRuleId { get; set; }
        public string ResourceName { get; set; } = string.Empty;
        public int DeltaValue { get; set; }
    }

    public sealed class DeleteImpactInput
    {
        public int Id { get; set; }
        public int ConsequenceRuleId { get; set; }
    }
}
