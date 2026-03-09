using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Controllers;

[ApiController]
[Route("api/crud")]
public class BaseCrudController : ControllerBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 500;
    private readonly TinyGeneratorDbContext _db;
    private readonly IWebHostEnvironment _environment;

    public BaseCrudController(TinyGeneratorDbContext db, IWebHostEnvironment environment)
    {
        _db = db;
        _environment = environment;
    }

    [HttpGet("tables")]
    public IActionResult GetTables()
    {
        var tables = _db.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && !string.IsNullOrWhiteSpace(e.GetTableName()))
            .Select(e => new
            {
                entity = e.ClrType.Name,
                table = e.GetTableName()
            })
            .OrderBy(x => x.table)
            .ToList();
        return Ok(tables);
    }

    [HttpPost("{table}/query")]
    public IActionResult QueryTable([FromRoute] string table, [FromBody] CrudQueryRequest? request)
    {
        request ??= new CrudQueryRequest();
        if (!TryResolveEntity(table, out var entityType, out var error))
        {
            return NotFound(new { success = false, error });
        }

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? DefaultPageSize : request.PageSize, 1, MaxPageSize);
        IQueryable query = CreateEntityQuery(entityType.ClrType);
        query = ApplySoftDeleteFilterIfNeeded(query, entityType.ClrType);
        var foreignKeys = BuildForeignKeyMetadata(entityType);
        var imageField = ResolveImageField(entityType.ClrType);
        var imageNameField = ResolveImageNameField(entityType.ClrType);
        var soundField = ResolveSoundField(entityType.ClrType);
        var soundNameField = ResolveSoundNameField(entityType.ClrType);
        var videoField = ResolveVideoField(entityType.ClrType);
        var videoNameField = ResolveVideoNameField(entityType.ClrType);
        var usageStatsField = ResolveUsageStatsField(entityType.ClrType);
        var fields = BuildFieldMetadata(entityType);

        foreach (var filter in request.Filters ?? Enumerable.Empty<CrudFilter>())
        {
            if (!TryApplyFilter(query, entityType.ClrType, filter, out var filtered, out error))
            {
                return BadRequest(new { success = false, error });
            }
            query = filtered;
        }

        if (!string.IsNullOrWhiteSpace(request.GlobalSearch))
        {
            if (!TryApplyGlobalSearch(query, entityType.ClrType, request.GlobalSearch!, out var searched, out error))
            {
                return BadRequest(new { success = false, error });
            }
            query = searched;
        }

        var sorts = (request.Sorts ?? new List<CrudSort>()).Where(s => !string.IsNullOrWhiteSpace(s.Field)).ToList();
        var fkSortMap = foreignKeys
            .Where(fk => !string.IsNullOrWhiteSpace(fk.DescriptionField) && !string.IsNullOrWhiteSpace(fk.DependentField))
            .GroupBy(fk => fk.DescriptionField, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DependentField, StringComparer.OrdinalIgnoreCase);

        foreach (var sort in sorts)
        {
            if (fkSortMap.TryGetValue(sort.Field, out var dependentField) && !string.IsNullOrWhiteSpace(dependentField))
            {
                sort.Field = dependentField;
                continue;
            }
        }
        if (sorts.Count == 0)
        {
            var pk = entityType.FindPrimaryKey();
            var defaultSort = pk?.Properties.FirstOrDefault()?.Name;
            if (!string.IsNullOrWhiteSpace(defaultSort))
            {
                sorts.Add(new CrudSort { Field = defaultSort!, Dir = "asc" });
            }
        }

        for (var i = 0; i < sorts.Count; i++)
        {
            if (!TryApplySort(query, entityType.ClrType, sorts[i], i > 0, out var sorted, out error))
            {
                return BadRequest(new { success = false, error });
            }
            query = sorted;
        }

        var totalRows = QueryableCount(query);

        // Grouping mode (materialized on filtered/sorted rows)
        if (!string.IsNullOrWhiteSpace(request.GroupBy))
        {
            var groupProp = FindProperty(entityType.ClrType, request.GroupBy!);
            if (groupProp == null)
            {
                return BadRequest(new { success = false, error = $"Campo grouping non valido: '{request.GroupBy}'" });
            }

            var allRows = query.Cast<object>().ToList()
                .Select(ToDictionary)
                .ToList();
            EnrichRowsWithRelatedData(entityType.ClrType, allRows);
            EnrichRowsWithImagePreviewData(entityType.ClrType, allRows);
            EnrichRowsWithSoundPreviewData(entityType.ClrType, allRows);
            EnrichRowsWithVideoPreviewData(entityType.ClrType, allRows);
            EnrichRowsWithUsageStatsData(entityType.ClrType, allRows);

            var keyName = groupProp.Name;
            var groups = allRows
                .GroupBy(r => r.TryGetValue(keyName, out var v) ? v : null)
                .Select(g => new
                {
                    key = g.Key,
                    count = g.Count(),
                    items = request.IncludeGroupItems
                        ? g.Take(Math.Max(1, request.GroupItemsLimit)).ToList()
                        : null
                })
                .ToList();

            var totalGroups = groups.Count;
            var pageGroups = groups
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return Ok(new
            {
                success = true,
                table = entityType.GetTableName(),
                entity = entityType.ClrType.Name,
                foreignKeys,
                fields,
                imageField,
                imageNameField,
                soundField,
                soundNameField,
                videoField,
                videoNameField,
                usageStatsField,
                grouped = true,
                groupBy = keyName,
                page,
                pageSize,
                totalRows,
                totalGroups,
                groups = pageGroups
            });
        }

        var paged = query
            .Cast<object>()
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList()
            .Select(ToDictionary)
            .ToList();
        EnrichRowsWithRelatedData(entityType.ClrType, paged);
        EnrichRowsWithImagePreviewData(entityType.ClrType, paged);
        EnrichRowsWithSoundPreviewData(entityType.ClrType, paged);
        EnrichRowsWithVideoPreviewData(entityType.ClrType, paged);
        EnrichRowsWithUsageStatsData(entityType.ClrType, paged);

        return Ok(new
        {
            success = true,
            table = entityType.GetTableName(),
            entity = entityType.ClrType.Name,
            foreignKeys,
            fields,
            imageField,
            imageNameField,
            soundField,
            soundNameField,
            videoField,
            videoNameField,
            usageStatsField,
            grouped = false,
            page,
            pageSize,
            totalRows,
            items = paged
        });
    }

    [HttpGet("{table}/{id}")]
    public async Task<IActionResult> GetById([FromRoute] string table, [FromRoute] string id)
    {
        if (!TryResolveEntity(table, out var entityType, out var error))
        {
            return NotFound(new { success = false, error });
        }

        if (!TryParseSingleKey(entityType, id, out var keyValue, out error))
        {
            return BadRequest(new { success = false, error });
        }

        var entity = await _db.FindAsync(entityType.ClrType, new[] { keyValue }).ConfigureAwait(false);
        if (entity == null) return NotFound(new { success = false, error = "Record non trovato" });
        var row = ToDictionary(entity);
        EnrichRowWithRelatedData(entityType.ClrType, row);
        EnrichRowWithImagePreviewData(entityType.ClrType, row);
        EnrichRowWithSoundPreviewData(entityType.ClrType, row);
        EnrichRowWithVideoPreviewData(entityType.ClrType, row);
        EnrichRowWithUsageStatsData(entityType.ClrType, row);
        return Ok(new
        {
            success = true,
            fields = BuildFieldMetadata(entityType),
            imageField = ResolveImageField(entityType.ClrType),
            imageNameField = ResolveImageNameField(entityType.ClrType),
            soundField = ResolveSoundField(entityType.ClrType),
            soundNameField = ResolveSoundNameField(entityType.ClrType),
            videoField = ResolveVideoField(entityType.ClrType),
            videoNameField = ResolveVideoNameField(entityType.ClrType),
            usageStatsField = ResolveUsageStatsField(entityType.ClrType),
            item = row
        });
    }

    [HttpPost("{table}")]
    public async Task<IActionResult> Create([FromRoute] string table, [FromBody] JsonElement payload)
    {
        if (!TryResolveEntity(table, out var entityType, out var error))
        {
            return NotFound(new { success = false, error });
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(new { success = false, error = "Payload JSON oggetto obbligatorio" });
        }

        var entity = Activator.CreateInstance(entityType.ClrType);
        if (entity == null)
        {
            return BadRequest(new { success = false, error = "Impossibile creare istanza entità" });
        }

        if (!TryApplyPayload(entityType, entity, payload, ignorePrimaryKey: true, out error))
        {
            return BadRequest(new { success = false, error });
        }

        ApplyTimeStampedFields(entity, isCreate: true);

        _db.Add(entity);
        await _db.SaveChangesAsync().ConfigureAwait(false);
        var createdRow = ToDictionary(entity);
        EnrichRowWithRelatedData(entityType.ClrType, createdRow);
        EnrichRowWithImagePreviewData(entityType.ClrType, createdRow);
        EnrichRowWithSoundPreviewData(entityType.ClrType, createdRow);
        EnrichRowWithVideoPreviewData(entityType.ClrType, createdRow);
        EnrichRowWithUsageStatsData(entityType.ClrType, createdRow);
        return Ok(new
        {
            success = true,
            fields = BuildFieldMetadata(entityType),
            imageField = ResolveImageField(entityType.ClrType),
            imageNameField = ResolveImageNameField(entityType.ClrType),
            soundField = ResolveSoundField(entityType.ClrType),
            soundNameField = ResolveSoundNameField(entityType.ClrType),
            videoField = ResolveVideoField(entityType.ClrType),
            videoNameField = ResolveVideoNameField(entityType.ClrType),
            usageStatsField = ResolveUsageStatsField(entityType.ClrType),
            item = createdRow
        });
    }

    [HttpPut("{table}/{id}")]
    public async Task<IActionResult> Update([FromRoute] string table, [FromRoute] string id, [FromBody] JsonElement payload)
    {
        if (!TryResolveEntity(table, out var entityType, out var error))
        {
            return NotFound(new { success = false, error });
        }
        if (!TryParseSingleKey(entityType, id, out var keyValue, out error))
        {
            return BadRequest(new { success = false, error });
        }
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(new { success = false, error = "Payload JSON oggetto obbligatorio" });
        }

        var entity = await _db.FindAsync(entityType.ClrType, new[] { keyValue }).ConfigureAwait(false);
        if (entity == null) return NotFound(new { success = false, error = "Record non trovato" });

        if (!TryApplyPayload(entityType, entity, payload, ignorePrimaryKey: true, out error))
        {
            return BadRequest(new { success = false, error });
        }

        ApplyTimeStampedFields(entity, isCreate: false);

        await _db.SaveChangesAsync().ConfigureAwait(false);
        var updatedRow = ToDictionary(entity);
        EnrichRowWithRelatedData(entityType.ClrType, updatedRow);
        EnrichRowWithImagePreviewData(entityType.ClrType, updatedRow);
        EnrichRowWithSoundPreviewData(entityType.ClrType, updatedRow);
        EnrichRowWithVideoPreviewData(entityType.ClrType, updatedRow);
        EnrichRowWithUsageStatsData(entityType.ClrType, updatedRow);
        return Ok(new
        {
            success = true,
            fields = BuildFieldMetadata(entityType),
            imageField = ResolveImageField(entityType.ClrType),
            imageNameField = ResolveImageNameField(entityType.ClrType),
            soundField = ResolveSoundField(entityType.ClrType),
            soundNameField = ResolveSoundNameField(entityType.ClrType),
            videoField = ResolveVideoField(entityType.ClrType),
            videoNameField = ResolveVideoNameField(entityType.ClrType),
            usageStatsField = ResolveUsageStatsField(entityType.ClrType),
            item = updatedRow
        });
    }

    [HttpDelete("{table}/{id}")]
    public async Task<IActionResult> Delete([FromRoute] string table, [FromRoute] string id)
    {
        if (!TryResolveEntity(table, out var entityType, out var error))
        {
            return NotFound(new { success = false, error });
        }
        if (!TryParseSingleKey(entityType, id, out var keyValue, out error))
        {
            return BadRequest(new { success = false, error });
        }

        var entity = await _db.FindAsync(entityType.ClrType, new[] { keyValue }).ConfigureAwait(false);
        if (entity == null) return NotFound(new { success = false, error = "Record non trovato" });

        if (entity is ISoftDelete softDelete)
        {
            softDelete.IsDeleted = true;
            if (entity is IActiveFlag activeFlag)
            {
                activeFlag.IsActive = false;
            }
            ApplyTimeStampedFields(entity, isCreate: false);
            _db.Update(entity);
        }
        else
        {
            _db.Remove(entity);
        }
        await _db.SaveChangesAsync().ConfigureAwait(false);
        return Ok(new { success = true });
    }

    private IQueryable CreateEntityQuery(Type clrType)
    {
        var setMethod = typeof(DbContext).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .First(m => m.Name == nameof(DbContext.Set) && m.IsGenericMethod && m.GetParameters().Length == 0)
            .MakeGenericMethod(clrType);

        return (IQueryable)setMethod.Invoke(_db, null)!;
    }

    private static void ApplyTimeStampedFields(object entity, bool isCreate)
    {
        if (entity is not ITimeStamped)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var nowIso = now.ToString("o", CultureInfo.InvariantCulture);

        var createdProp = entity.GetType().GetProperty("CreatedAt", BindingFlags.Instance | BindingFlags.Public);
        var updatedProp = entity.GetType().GetProperty("UpdatedAt", BindingFlags.Instance | BindingFlags.Public)
                        ?? entity.GetType().GetProperty("LastUpdate", BindingFlags.Instance | BindingFlags.Public);

        if (isCreate && createdProp is { CanWrite: true })
        {
            SetTimestamp(entity, createdProp, now, nowIso);
        }

        if (updatedProp is { CanWrite: true })
        {
            SetTimestamp(entity, updatedProp, now, nowIso);
        }
    }

    private static void SetTimestamp(object entity, PropertyInfo property, DateTime now, string nowIso)
    {
        if (property.PropertyType == typeof(string))
        {
            property.SetValue(entity, nowIso);
            return;
        }

        if (property.PropertyType == typeof(DateTime))
        {
            property.SetValue(entity, now);
            return;
        }

        if (property.PropertyType == typeof(DateTime?))
        {
            property.SetValue(entity, now);
        }
    }

    private bool TryResolveEntity(string table, out IEntityType entityType, out string error)
    {
        var lookup = NormalizeEntityLookup((table ?? string.Empty).Trim());
        entityType = _db.Model.GetEntityTypes()
            .Where(e => !e.IsOwned())
            .FirstOrDefault(e =>
                string.Equals(e.GetTableName(), lookup, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.ClrType.Name, lookup, StringComparison.OrdinalIgnoreCase));
        if (entityType == null)
        {
            error = $"Tabella/entità non trovata: '{lookup}'";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static string NormalizeEntityLookup(string lookup)
    {
        if (string.Equals(lookup, "genericlookup", StringComparison.OrdinalIgnoreCase))
        {
            return "GenericLookup";
        }

        if (string.Equals(lookup, "models_role_errors", StringComparison.OrdinalIgnoreCase))
        {
            return "model_roles_errors";
        }

        return lookup;
    }

    private static string? ResolveImageField(Type clrType)
    {
        if (typeof(IImageFile).IsAssignableFrom(clrType))
        {
            return nameof(IImageFile.ImagePath);
        }

        return null;
    }

    private static string? ResolveImageNameField(Type clrType)
    {
        if (typeof(IImageFile).IsAssignableFrom(clrType))
        {
            return nameof(IImageFile.ImageName);
        }

        return null;
    }

    private static string? ResolveSoundField(Type clrType)
    {
        if (typeof(ISoundFile).IsAssignableFrom(clrType))
        {
            return nameof(ISoundFile.SoundPath);
        }

        return null;
    }

    private static string? ResolveSoundNameField(Type clrType)
    {
        if (typeof(ISoundFile).IsAssignableFrom(clrType))
        {
            return nameof(ISoundFile.SoundName);
        }

        return null;
    }

    private static string? ResolveVideoField(Type clrType)
    {
        if (typeof(IVideoFile).IsAssignableFrom(clrType))
        {
            return nameof(IVideoFile.VideoPath);
        }

        return null;
    }

    private static string? ResolveVideoNameField(Type clrType)
    {
        if (typeof(IVideoFile).IsAssignableFrom(clrType))
        {
            return nameof(IVideoFile.VideoName);
        }

        return null;
    }

    private static string? ResolveUsageStatsField(Type clrType)
    {
        if (typeof(IUsageStats).IsAssignableFrom(clrType))
        {
            return "SuccessRatePercent";
        }

        return null;
    }

    private static bool TryApplyFilter(IQueryable source, Type type, CrudFilter filter, out IQueryable result, out string error)
    {
        result = source;
        error = string.Empty;
        var prop = FindProperty(type, filter.Field);
        if (prop == null)
        {
            error = $"Campo filtro non valido: '{filter.Field}'";
            return false;
        }

        var param = Expression.Parameter(type, "x");
        var member = Expression.Property(param, prop);
        var nullableUnderlying = Nullable.GetUnderlyingType(prop.PropertyType);
        var targetType = nullableUnderlying ?? prop.PropertyType;
        var op = (filter.Op ?? "eq").Trim().ToLowerInvariant();

        if ((op == "isnull" || op == "notnull"))
        {
            var nullExpr = Expression.Constant(null, prop.PropertyType);
            var body = op == "isnull" ? Expression.Equal(member, nullExpr) : Expression.NotEqual(member, nullExpr);
            var lambda = Expression.Lambda(body, param);
            result = ApplyWhere(source, type, lambda);
            return true;
        }

        if (!TryConvertJson(filter.Value, targetType, out var converted, out error))
        {
            return false;
        }

        if (op is "contains" or "startswith" or "endswith")
        {
            converted = converted?.ToString() ?? string.Empty;
            targetType = typeof(string);
        }

        Expression memberForCompare = member;
        if (nullableUnderlying != null)
        {
            memberForCompare = Expression.Convert(member, targetType);
        }
        var constant = Expression.Constant(converted, targetType);

        Expression bodyExpr = op switch
        {
            "eq" => Expression.Equal(memberForCompare, constant),
            "neq" => Expression.NotEqual(memberForCompare, constant),
            "gt" => Expression.GreaterThan(memberForCompare, constant),
            "gte" => Expression.GreaterThanOrEqual(memberForCompare, constant),
            "lt" => Expression.LessThan(memberForCompare, constant),
            "lte" => Expression.LessThanOrEqual(memberForCompare, constant),
            "contains" => BuildStringCall(memberForCompare, nameof(string.Contains), constant),
            "startswith" => BuildStringCall(memberForCompare, nameof(string.StartsWith), constant),
            "endswith" => BuildStringCall(memberForCompare, nameof(string.EndsWith), constant),
            _ => null!
        };

        if (bodyExpr == null)
        {
            error = $"Operatore filtro non supportato: '{filter.Op}'";
            return false;
        }

        var lambdaExpr = Expression.Lambda(bodyExpr, param);
        result = ApplyWhere(source, type, lambdaExpr);
        return true;
    }

    private static Expression BuildStringCall(Expression member, string method, Expression constant)
    {
        var safeMember = member.Type == typeof(string) ? member : Expression.Call(member, "ToString", Type.EmptyTypes);
        return Expression.AndAlso(
            Expression.NotEqual(safeMember, Expression.Constant(null, typeof(string))),
            Expression.Call(safeMember, typeof(string).GetMethod(method, new[] { typeof(string) })!, constant));
    }

    private static IQueryable ApplyWhere(IQueryable source, Type type, LambdaExpression predicate)
    {
        var where = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(type);
        return (IQueryable)where.Invoke(null, new object[] { source, predicate })!;
    }

    private static IQueryable ApplySoftDeleteFilterIfNeeded(IQueryable source, Type type)
    {
        if (!typeof(ISoftDelete).IsAssignableFrom(type))
        {
            return source;
        }

        var prop = type.GetProperty(nameof(ISoftDelete.IsDeleted), BindingFlags.Instance | BindingFlags.Public);
        if (prop == null || prop.PropertyType != typeof(bool))
        {
            return source;
        }

        var param = Expression.Parameter(type, "x");
        var member = Expression.Property(param, prop);
        var body = Expression.Equal(member, Expression.Constant(false, typeof(bool)));
        var lambda = Expression.Lambda(body, param);
        return ApplyWhere(source, type, lambda);
    }

    private static bool TryApplyGlobalSearch(IQueryable source, Type type, string searchText, out IQueryable result, out string error)
    {
        result = source;
        error = string.Empty;

        var text = searchText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var stringProps = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.CanWrite && p.PropertyType == typeof(string) &&
                        !Attribute.IsDefined(p, typeof(NotMappedAttribute)))
            .ToList();

        if (stringProps.Count == 0)
        {
            return true;
        }

        var param = Expression.Parameter(type, "x");
        var constant = Expression.Constant(text, typeof(string));
        Expression? body = null;

        foreach (var prop in stringProps)
        {
            var member = Expression.Property(param, prop);
            var contains = BuildStringCall(member, nameof(string.Contains), constant);
            body = body == null ? contains : Expression.OrElse(body, contains);
        }

        if (body == null)
        {
            return true;
        }

        var lambda = Expression.Lambda(body, param);
        result = ApplyWhere(source, type, lambda);
        return true;
    }

    private static bool TryApplySort(IQueryable source, Type type, CrudSort sort, bool thenBy, out IQueryable result, out string error)
    {
        result = source;
        error = string.Empty;
        if (typeof(IUsageStats).IsAssignableFrom(type) &&
            string.Equals(sort.Field, "SuccessRatePercent", StringComparison.OrdinalIgnoreCase))
        {
            var paramUsage = Expression.Parameter(type, "x");
            var useCountExpr = Expression.Property(paramUsage, nameof(IUsageStats.UseCount));
            var useSuccessedExpr = Expression.Property(paramUsage, nameof(IUsageStats.UseSuccessed));
            var useFailedExpr = Expression.Property(paramUsage, nameof(IUsageStats.UseFailed));

            var useCountDouble = Expression.Convert(useCountExpr, typeof(double));
            var successDouble = Expression.Convert(useSuccessedExpr, typeof(double));
            var successPlusFailed = Expression.Add(useSuccessedExpr, useFailedExpr);
            var successPlusFailedDouble = Expression.Convert(successPlusFailed, typeof(double));
            var hundred = Expression.Constant(100.0, typeof(double));
            var zeroInt = Expression.Constant(0, typeof(int));
            var zeroDouble = Expression.Constant(0.0, typeof(double));
            var denominatorExpr = Expression.Condition(
                Expression.GreaterThan(useCountExpr, zeroInt),
                useCountDouble,
                successPlusFailedDouble);
            var usageExpr = Expression.Condition(
                Expression.GreaterThan(denominatorExpr, zeroDouble),
                Expression.Multiply(Expression.Divide(successDouble, denominatorExpr), hundred),
                zeroDouble);

            var usageLambda = Expression.Lambda(usageExpr, paramUsage);
            var usageDesc = string.Equals(sort.Dir, "desc", StringComparison.OrdinalIgnoreCase);
            var usageMethodName = thenBy
                ? (usageDesc ? "ThenByDescending" : "ThenBy")
                : (usageDesc ? "OrderByDescending" : "OrderBy");
            var usageMethod = typeof(Queryable).GetMethods()
                .Where(m => m.Name == usageMethodName)
                .First(m => m.GetParameters().Length == 2)
                .MakeGenericMethod(type, typeof(double));
            result = (IQueryable)usageMethod.Invoke(null, new object[] { source, usageLambda })!;
            return true;
        }

        var prop = FindProperty(type, sort.Field);
        if (prop == null)
        {
            error = $"Campo sort non valido: '{sort.Field}'";
            return false;
        }

        var param = Expression.Parameter(type, "x");
        var body = Expression.Property(param, prop);
        var lambda = Expression.Lambda(body, param);
        var isDesc = string.Equals(sort.Dir, "desc", StringComparison.OrdinalIgnoreCase);
        var methodName = thenBy
            ? (isDesc ? "ThenByDescending" : "ThenBy")
            : (isDesc ? "OrderByDescending" : "OrderBy");

        var method = typeof(Queryable).GetMethods()
            .Where(m => m.Name == methodName)
            .First(m => m.GetParameters().Length == 2)
            .MakeGenericMethod(type, prop.PropertyType);

        result = (IQueryable)method.Invoke(null, new object[] { source, lambda })!;
        return true;
    }

    private static int QueryableCount(IQueryable query)
    {
        var method = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Count" && m.GetParameters().Length == 1)
            .MakeGenericMethod(query.ElementType);
        return (int)method.Invoke(null, new object[] { query })!;
    }

    private static PropertyInfo? FindProperty(Type type, string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return null;
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(p => p.CanRead && p.CanWrite &&
                                 !Attribute.IsDefined(p, typeof(NotMappedAttribute)) &&
                                 string.Equals(p.Name, field.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseSingleKey(IEntityType entityType, string rawId, out object keyValue, out string error)
    {
        keyValue = null!;
        var pk = entityType.FindPrimaryKey();
        if (pk == null || pk.Properties.Count != 1)
        {
            error = "Sono supportate solo tabelle con chiave primaria singola";
            return false;
        }

        var pkType = pk.Properties[0].ClrType;
        if (!TryConvertString(rawId, pkType, out keyValue, out error))
        {
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryApplyPayload(IEntityType entityType, object entity, JsonElement payload, bool ignorePrimaryKey, out string error)
    {
        error = string.Empty;
        var pkNames = new HashSet<string>(
            (entityType.FindPrimaryKey()?.Properties ?? Array.Empty<IProperty>())
            .Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var jsonProp in payload.EnumerateObject())
        {
            var prop = FindProperty(entity.GetType(), jsonProp.Name);
            if (prop == null || !prop.CanWrite) continue;
            if (ignorePrimaryKey && pkNames.Contains(prop.Name)) continue;
            if (!TryConvertJson(jsonProp.Value, prop.PropertyType, out var converted, out error))
            {
                error = $"Campo '{jsonProp.Name}': {error}";
                return false;
            }
            prop.SetValue(entity, converted);
        }
        return true;
    }

    private static bool TryConvertJson(JsonElement value, Type targetType, out object? result, out string error)
    {
        var underlying = Nullable.GetUnderlyingType(targetType);
        var effectiveType = underlying ?? targetType;

        if (value.ValueKind == JsonValueKind.Null)
        {
            result = null;
            error = string.Empty;
            return true;
        }

        try
        {
            if (effectiveType == typeof(string))
            {
                result = value.GetString();
                error = string.Empty;
                return true;
            }
            if (effectiveType == typeof(int))
            {
                result = value.GetInt32();
                error = string.Empty;
                return true;
            }
            if (effectiveType == typeof(long))
            {
                result = value.GetInt64();
                error = string.Empty;
                return true;
            }
            if (effectiveType == typeof(double))
            {
                result = value.GetDouble();
                error = string.Empty;
                return true;
            }
            if (effectiveType == typeof(decimal))
            {
                result = value.GetDecimal();
                error = string.Empty;
                return true;
            }
            if (effectiveType == typeof(bool))
            {
                result = value.GetBoolean();
                error = string.Empty;
                return true;
            }
            if (effectiveType == typeof(DateTime))
            {
                result = value.ValueKind == JsonValueKind.String
                    ? DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    : value.Deserialize<DateTime>();
                error = string.Empty;
                return true;
            }

            result = value.Deserialize(effectiveType);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            result = null;
            error = ex.Message;
            return false;
        }
    }

    private static bool TryConvertString(string raw, Type targetType, out object result, out string error)
    {
        var underlying = Nullable.GetUnderlyingType(targetType);
        var effectiveType = underlying ?? targetType;
        try
        {
            if (effectiveType == typeof(string))
            {
                result = raw;
                error = string.Empty;
                return true;
            }
            if (effectiveType == typeof(int))
            {
                result = int.Parse(raw, CultureInfo.InvariantCulture);
                error = string.Empty;
                return true;
            }
            if (effectiveType == typeof(long))
            {
                result = long.Parse(raw, CultureInfo.InvariantCulture);
                error = string.Empty;
                return true;
            }
            if (effectiveType == typeof(Guid))
            {
                result = Guid.Parse(raw);
                error = string.Empty;
                return true;
            }
            result = Convert.ChangeType(raw, effectiveType, CultureInfo.InvariantCulture)!;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            result = null!;
            error = ex.Message;
            return false;
        }
    }

    private static Dictionary<string, object?> ToDictionary(object entity)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var props = entity.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && IsSimpleType(p.PropertyType) &&
                        !Attribute.IsDefined(p, typeof(NotMappedAttribute)));
        foreach (var prop in props)
        {
            dict[prop.Name] = prop.GetValue(entity);
        }
        return dict;
    }

    private static bool IsSimpleType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t.IsEnum) return true;
        return t.IsPrimitive
               || t == typeof(string)
               || t == typeof(decimal)
               || t == typeof(DateTime)
               || t == typeof(Guid)
               || t == typeof(TimeSpan);
    }

    private void EnrichRowsWithRelatedData(Type clrType, List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return;

        var entityType = _db.Model.FindEntityType(clrType);
        if (entityType != null)
        {
            EnrichRowsWithForeignKeyDescriptions(entityType, rows);
        }

        // FK description fields are the canonical display values for related entities.
        // For Agent->Model this is "ModelDescription", aligned with generic FK combos.
    }

    private void EnrichRowsWithImagePreviewData(Type clrType, List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return;
        if (!typeof(IImageFile).IsAssignableFrom(clrType)) return;

        var imagePathField = ResolveImageField(clrType);
        var imageNameField = ResolveImageNameField(clrType);
        if (string.IsNullOrWhiteSpace(imagePathField)) return;

        foreach (var row in rows)
        {
            var rawPath = row.TryGetValue(imagePathField!, out var pathValue)
                ? Convert.ToString(pathValue, CultureInfo.InvariantCulture)
                : null;
            if (string.IsNullOrWhiteSpace(rawPath)) continue;

            var rawName = !string.IsNullOrWhiteSpace(imageNameField) && row.TryGetValue(imageNameField!, out var nameValue)
                ? Convert.ToString(nameValue, CultureInfo.InvariantCulture)
                : null;

            var normalizedPath = NormalizeImagePath(rawPath!);
            if (string.IsNullOrWhiteSpace(normalizedPath) || !System.IO.File.Exists(normalizedPath))
            {
                continue;
            }

            if (TryEnsureImageCacheAssets(normalizedPath, rawName, out var thumbUrl, out var fullUrl))
            {
                row["ImageThumbnailUrl"] = thumbUrl;
                row["ImagePreviewUrl"] = fullUrl;
            }
        }
    }

    private void EnrichRowWithImagePreviewData(Type clrType, Dictionary<string, object?> row)
    {
        EnrichRowsWithImagePreviewData(clrType, new List<Dictionary<string, object?>> { row });
    }

    private void EnrichRowsWithSoundPreviewData(Type clrType, List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return;
        if (!typeof(ISoundFile).IsAssignableFrom(clrType)) return;

        var soundPathField = ResolveSoundField(clrType);
        var soundNameField = ResolveSoundNameField(clrType);
        if (string.IsNullOrWhiteSpace(soundPathField)) return;

        foreach (var row in rows)
        {
            var rawPath = row.TryGetValue(soundPathField!, out var pathValue)
                ? Convert.ToString(pathValue, CultureInfo.InvariantCulture)
                : null;
            if (string.IsNullOrWhiteSpace(rawPath)) continue;

            var rawName = !string.IsNullOrWhiteSpace(soundNameField) && row.TryGetValue(soundNameField!, out var nameValue)
                ? Convert.ToString(nameValue, CultureInfo.InvariantCulture)
                : null;

            var normalizedPath = NormalizeSoundPath(rawPath!);
            if (string.IsNullOrWhiteSpace(normalizedPath) || !System.IO.File.Exists(normalizedPath))
            {
                continue;
            }

            if (TryEnsureSoundCacheAsset(normalizedPath, rawName, out var previewUrl))
            {
                row["SoundPreviewUrl"] = previewUrl;
            }
        }
    }

    private void EnrichRowWithSoundPreviewData(Type clrType, Dictionary<string, object?> row)
    {
        EnrichRowsWithSoundPreviewData(clrType, new List<Dictionary<string, object?>> { row });
    }

    private void EnrichRowsWithVideoPreviewData(Type clrType, List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return;
        if (!typeof(IVideoFile).IsAssignableFrom(clrType)) return;

        var videoPathField = ResolveVideoField(clrType);
        var videoNameField = ResolveVideoNameField(clrType);
        if (string.IsNullOrWhiteSpace(videoPathField)) return;

        foreach (var row in rows)
        {
            var rawPath = row.TryGetValue(videoPathField!, out var pathValue)
                ? Convert.ToString(pathValue, CultureInfo.InvariantCulture)
                : null;
            if (string.IsNullOrWhiteSpace(rawPath)) continue;

            var rawName = !string.IsNullOrWhiteSpace(videoNameField) && row.TryGetValue(videoNameField!, out var nameValue)
                ? Convert.ToString(nameValue, CultureInfo.InvariantCulture)
                : null;

            var normalizedPath = NormalizeVideoPath(rawPath!);
            if (string.IsNullOrWhiteSpace(normalizedPath) || !System.IO.File.Exists(normalizedPath))
            {
                continue;
            }

            if (TryEnsureVideoCacheAsset(normalizedPath, rawName, out var previewUrl))
            {
                row["VideoPreviewUrl"] = previewUrl;
            }
        }
    }

    private void EnrichRowWithVideoPreviewData(Type clrType, Dictionary<string, object?> row)
    {
        EnrichRowsWithVideoPreviewData(clrType, new List<Dictionary<string, object?>> { row });
    }

    private static void EnrichRowsWithUsageStatsData(Type clrType, List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return;
        if (!typeof(IUsageStats).IsAssignableFrom(clrType)) return;

        foreach (var row in rows)
        {
            var useCount = TryGetIntValue(row, nameof(IUsageStats.UseCount));
            var success = TryGetIntValue(row, nameof(IUsageStats.UseSuccessed));
            var failed = TryGetIntValue(row, nameof(IUsageStats.UseFailed));
            var denominator = useCount > 0 ? useCount : (success + failed);
            var percent = denominator > 0
                ? Math.Round((double)success * 100.0 / denominator, 2, MidpointRounding.AwayFromZero)
                : 0.0;
            row["SuccessRatePercent"] = percent;
        }
    }

    private static void EnrichRowWithUsageStatsData(Type clrType, Dictionary<string, object?> row)
    {
        EnrichRowsWithUsageStatsData(clrType, new List<Dictionary<string, object?>> { row });
    }

    private void EnrichRowsWithForeignKeyDescriptions(IEntityType entityType, List<Dictionary<string, object?>> rows)
    {
        foreach (var fk in entityType.GetForeignKeys())
        {
            if (fk.Properties.Count != 1 || fk.PrincipalKey.Properties.Count != 1) continue;

            var dependentPropertyName = fk.Properties[0].Name;
            var principalKeyName = fk.PrincipalKey.Properties[0].Name;
            var principalClrType = fk.PrincipalEntityType.ClrType;
            var descriptionFieldName = BuildFkDescriptionFieldName(dependentPropertyName, fk.DependentToPrincipal?.Name);

            var rawValues = rows
                .Where(r => r.TryGetValue(dependentPropertyName, out var value) && value != null)
                .Select(r => r[dependentPropertyName]!)
                .ToList();

            if (rawValues.Count == 0) continue;

            if (!TryBuildPrincipalDescriptionMap(principalClrType, principalKeyName, rawValues, out var descriptionMap))
            {
                continue;
            }

            foreach (var row in rows)
            {
                if (!row.TryGetValue(dependentPropertyName, out var raw) || raw == null) continue;
                var keyToken = ToKeyToken(raw);
                if (string.IsNullOrWhiteSpace(keyToken)) continue;
                if (!descriptionMap.TryGetValue(keyToken, out var description)) continue;
                row[descriptionFieldName] = description;
            }
        }
    }

    private bool TryBuildPrincipalDescriptionMap(
        Type principalClrType,
        string principalKeyName,
        List<object> dependentRawValues,
        out Dictionary<string, string> descriptionMap)
    {
        descriptionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var keySet = dependentRawValues
            .Select(ToKeyToken)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (keySet.Count == 0) return false;

        var principalRows = CreateEntityQuery(principalClrType)
            .Cast<object>()
            .ToList()
            .Select(ToDictionary);

        foreach (var principalRow in principalRows)
        {
            if (!principalRow.TryGetValue(principalKeyName, out var keyRaw) || keyRaw == null) continue;
            var keyToken = ToKeyToken(keyRaw);
            if (!keySet.Contains(keyToken)) continue;

            var description = BuildDescriptionFromPrincipalRow(principalRow, principalKeyName);
            if (string.IsNullOrWhiteSpace(description)) continue;

            descriptionMap[keyToken] = description;
        }

        return descriptionMap.Count > 0;
    }

    private static string BuildFkDescriptionFieldName(string dependentPropertyName, string? navigationName)
    {
        if (!string.IsNullOrWhiteSpace(navigationName))
        {
            return $"{navigationName}Description";
        }

        return dependentPropertyName.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
            ? $"{dependentPropertyName[..^2]}Description"
            : $"{dependentPropertyName}Description";
    }

    private static string BuildDescriptionFromPrincipalRow(Dictionary<string, object?> row, string principalKeyName)
    {
        var name = GetStringValue(row, "Name");
        var provider = GetStringValue(row, "Provider");
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(provider))
        {
            return $"{name} ({provider})";
        }

        foreach (var field in new[] { "Description", "Name", "Title", "Code", "Label" })
        {
            var value = GetStringValue(row, field);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return TrimForGrid(value);
            }
        }

        foreach (var kvp in row)
        {
            if (string.Equals(kvp.Key, principalKeyName, StringComparison.OrdinalIgnoreCase)) continue;
            if (kvp.Value == null) continue;
            if (!IsSimpleType(kvp.Value.GetType())) continue;

            var value = Convert.ToString(kvp.Value, CultureInfo.InvariantCulture)?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return TrimForGrid(value);
            }
        }

        return string.Empty;
    }

    private static string GetStringValue(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value == null) return string.Empty;
        return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    }

    private static string ToKeyToken(object value)
    {
        return value switch
        {
            DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
        };
    }

    private static string TrimForGrid(string value)
    {
        const int max = 180;
        return value.Length <= max ? value : $"{value[..max]}…";
    }

    private static List<CrudForeignKeyMeta> BuildForeignKeyMetadata(IEntityType entityType)
    {
        var list = new List<CrudForeignKeyMeta>();
        foreach (var fk in entityType.GetForeignKeys())
        {
            if (fk.Properties.Count != 1 || fk.PrincipalKey.Properties.Count != 1) continue;

            var dependentPropertyName = fk.Properties[0].Name;
            var principalKeyName = fk.PrincipalKey.Properties[0].Name;
            var principalEntity = fk.PrincipalEntityType.ClrType.Name;
            var principalTable = fk.PrincipalEntityType.GetTableName() ?? principalEntity;
            var descriptionField = BuildFkDescriptionFieldName(dependentPropertyName, fk.DependentToPrincipal?.Name);

            list.Add(new CrudForeignKeyMeta
            {
                DependentField = dependentPropertyName,
                DescriptionField = descriptionField,
                PrincipalEntity = principalEntity,
                PrincipalTable = principalTable,
                PrincipalKeyField = principalKeyName
            });
        }

        return list;
    }

    private static List<CrudFieldMeta> BuildFieldMetadata(IEntityType entityType)
    {
        var pk = entityType.FindPrimaryKey();
        var pkNames = new HashSet<string>(
            (pk?.Properties ?? Array.Empty<IProperty>()).Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        var fkByDependent = entityType.GetForeignKeys()
            .Where(fk => fk.Properties.Count == 1 && fk.PrincipalKey.Properties.Count == 1)
            .GroupBy(fk => fk.Properties[0].Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<CrudFieldMeta>();
        foreach (var prop in entityType.GetProperties())
        {
            var clrType = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
            var clrTypeName = clrType.Name;

            fkByDependent.TryGetValue(prop.Name, out var fk);
            var principalTable = fk?.PrincipalEntityType.GetTableName() ?? fk?.PrincipalEntityType.ClrType.Name;
            var principalKey = fk?.PrincipalKey.Properties.FirstOrDefault()?.Name;

            result.Add(new CrudFieldMeta
            {
                Name = prop.Name,
                ClrType = clrTypeName,
                Nullable = prop.IsNullable,
                IsPrimaryKey = pkNames.Contains(prop.Name),
                IsForeignKey = fk != null,
                PrincipalTable = principalTable,
                PrincipalKeyField = principalKey
            });
        }

        return result
            .OrderByDescending(f => f.IsPrimaryKey)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void EnrichRowWithRelatedData(Type clrType, Dictionary<string, object?> row)
    {
        EnrichRowsWithRelatedData(clrType, new List<Dictionary<string, object?>> { row });
    }

    private static int TryGetIntValue(Dictionary<string, object?> row, string fieldName)
    {
        if (!row.TryGetValue(fieldName, out var raw) || raw == null) return 0;
        return raw switch
        {
            int i => i,
            long l when l <= int.MaxValue && l >= int.MinValue => (int)l,
            short s => s,
            byte b => b,
            string str when int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static string? NormalizeImagePath(string rawPath)
    {
        var trimmed = (rawPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        try
        {
            if (!Path.IsPathRooted(trimmed))
            {
                trimmed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), trimmed));
            }
            else
            {
                trimmed = Path.GetFullPath(trimmed);
            }
        }
        catch
        {
            return null;
        }

        var extension = Path.GetExtension(trimmed)?.ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif"))
        {
            return null;
        }

        return trimmed;
    }

    private static string? NormalizeSoundPath(string rawPath)
    {
        var trimmed = (rawPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        try
        {
            if (!Path.IsPathRooted(trimmed))
            {
                trimmed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), trimmed));
            }
            else
            {
                trimmed = Path.GetFullPath(trimmed);
            }
        }
        catch
        {
            return null;
        }

        var extension = Path.GetExtension(trimmed)?.ToLowerInvariant();
        if (extension is not (".mp3" or ".wav" or ".ogg" or ".m4a" or ".aac" or ".flac" or ".webm"))
        {
            return null;
        }

        return trimmed;
    }

    private static string? NormalizeVideoPath(string rawPath)
    {
        var trimmed = (rawPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        try
        {
            if (!Path.IsPathRooted(trimmed))
            {
                trimmed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), trimmed));
            }
            else
            {
                trimmed = Path.GetFullPath(trimmed);
            }
        }
        catch
        {
            return null;
        }

        var extension = Path.GetExtension(trimmed)?.ToLowerInvariant();
        if (extension is not (".mp4" or ".webm" or ".mov" or ".mkv" or ".avi" or ".m4v"))
        {
            return null;
        }

        return trimmed;
    }

    private bool TryEnsureImageCacheAssets(string sourcePath, string? imageName, out string thumbUrl, out string fullUrl)
    {
        thumbUrl = string.Empty;
        fullUrl = string.Empty;

        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            var cacheRoot = _environment.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var thumbsDir = Path.Combine(cacheRoot, "images_cache", "thumbs");
            var fullDir = Path.Combine(cacheRoot, "images_cache", "full");
            Directory.CreateDirectory(thumbsDir);
            Directory.CreateDirectory(fullDir);

            var hashInput = $"{sourceInfo.FullName}|{sourceInfo.LastWriteTimeUtc.Ticks}|{sourceInfo.Length}|{imageName}";
            var hash = ComputeSha256(hashInput);
            var sourceExt = sourceInfo.Extension.ToLowerInvariant();
            var fullFileName = $"{hash}{sourceExt}";
            var thumbFileName = $"{hash}_thumb{sourceExt}";
            var fullPath = Path.Combine(fullDir, fullFileName);
            var thumbPath = Path.Combine(thumbsDir, thumbFileName);

            if (!System.IO.File.Exists(fullPath))
            {
                System.IO.File.Copy(sourceInfo.FullName, fullPath, overwrite: true);
            }

            if (!System.IO.File.Exists(thumbPath))
            {
                // Thumbnail cache file: reuse original bytes, UI enforces 50x50 rendering.
                System.IO.File.Copy(sourceInfo.FullName, thumbPath, overwrite: true);
            }

            thumbUrl = $"/images_cache/thumbs/{thumbFileName}";
            fullUrl = $"/images_cache/full/{fullFileName}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryEnsureSoundCacheAsset(string sourcePath, string? soundName, out string previewUrl)
    {
        previewUrl = string.Empty;

        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            var cacheRoot = _environment.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var audioDir = Path.Combine(cacheRoot, "sounds_cache");
            Directory.CreateDirectory(audioDir);

            var hashInput = $"{sourceInfo.FullName}|{sourceInfo.LastWriteTimeUtc.Ticks}|{sourceInfo.Length}|{soundName}";
            var hash = ComputeSha256(hashInput);
            var sourceExt = sourceInfo.Extension.ToLowerInvariant();
            var fileName = $"{hash}{sourceExt}";
            var cachedPath = Path.Combine(audioDir, fileName);

            if (!System.IO.File.Exists(cachedPath))
            {
                System.IO.File.Copy(sourceInfo.FullName, cachedPath, overwrite: true);
            }

            previewUrl = $"/sounds_cache/{fileName}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryEnsureVideoCacheAsset(string sourcePath, string? videoName, out string previewUrl)
    {
        previewUrl = string.Empty;

        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            var cacheRoot = _environment.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var videoDir = Path.Combine(cacheRoot, "videos_cache");
            Directory.CreateDirectory(videoDir);

            var hashInput = $"{sourceInfo.FullName}|{sourceInfo.LastWriteTimeUtc.Ticks}|{sourceInfo.Length}|{videoName}";
            var hash = ComputeSha256(hashInput);
            var sourceExt = sourceInfo.Extension.ToLowerInvariant();
            var fileName = $"{hash}{sourceExt}";
            var cachedPath = Path.Combine(videoDir, fileName);

            if (!System.IO.File.Exists(cachedPath))
            {
                System.IO.File.Copy(sourceInfo.FullName, cachedPath, overwrite: true);
            }

            previewUrl = $"/videos_cache/{fileName}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed class CrudQueryRequest
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public List<CrudSort>? Sorts { get; set; } = new();
    public List<CrudFilter>? Filters { get; set; } = new();
    public string? GlobalSearch { get; set; }
    public string? GroupBy { get; set; }
    public bool IncludeGroupItems { get; set; } = true;
    public int GroupItemsLimit { get; set; } = 20;
}

public sealed class CrudSort
{
    public string Field { get; set; } = string.Empty;
    public string Dir { get; set; } = "asc";
}

public sealed class CrudFilter
{
    public string Field { get; set; } = string.Empty;
    public string Op { get; set; } = "eq";
    public JsonElement Value { get; set; }
}

public sealed class CrudForeignKeyMeta
{
    public string DependentField { get; set; } = string.Empty;
    public string DescriptionField { get; set; } = string.Empty;
    public string PrincipalEntity { get; set; } = string.Empty;
    public string PrincipalTable { get; set; } = string.Empty;
    public string PrincipalKeyField { get; set; } = string.Empty;
}

public sealed class CrudFieldMeta
{
    public string Name { get; set; } = string.Empty;
    public string ClrType { get; set; } = string.Empty;
    public bool Nullable { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsForeignKey { get; set; }
    public string? PrincipalTable { get; set; }
    public string? PrincipalKeyField { get; set; }
}
