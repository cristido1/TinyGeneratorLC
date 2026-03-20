using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using TinyGenerator.Data;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Controllers;

[ApiController]
[Route("api/crud")]
public class BaseCrudController : ControllerBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 500;
    private readonly TinyGeneratorDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly DatabaseService _database;
    private readonly LangChainTestService _testService;
    private readonly IOllamaManagementService _ollamaService;
    private readonly JsonScoreTestService _jsonScoreTester;
    private readonly InstructionScoreTestService _instructionScoreTester;
    private readonly IntelligenceScoreTestService _intelligenceTestService;
    private readonly IConfiguration _configuration;

    public BaseCrudController(
        TinyGeneratorDbContext db,
        IWebHostEnvironment environment,
        DatabaseService database,
        LangChainTestService testService,
        IOllamaManagementService ollamaService,
        JsonScoreTestService jsonScoreTester,
        InstructionScoreTestService instructionScoreTester,
        IntelligenceScoreTestService intelligenceTestService,
        IConfiguration configuration)
    {
        _db = db;
        _environment = environment;
        _database = database;
        _testService = testService;
        _ollamaService = ollamaService;
        _jsonScoreTester = jsonScoreTester;
        _instructionScoreTester = instructionScoreTester;
        _intelligenceTestService = intelligenceTestService;
        _configuration = configuration;
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

    [HttpPost("metadata/sync-schema")]
    public async Task<IActionResult> SyncMetadataSchema()
    {
        try
        {
            await using var connection = _db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync().ConfigureAwait(false);
            }

            await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

            var tableNames = new List<string>();
            await using (var listTablesCmd = connection.CreateCommand())
            {
                listTablesCmd.Transaction = transaction;
                listTablesCmd.CommandText = @"
SELECT name
FROM sqlite_master
WHERE type = 'table'
  AND name NOT LIKE 'sqlite_%'
  AND name NOT IN ('__EFMigrationsHistory', '__EFMigrationsLock')
ORDER BY name;";

                await using var reader = await listTablesCmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        tableNames.Add(name.Trim());
                    }
                }
            }

            var addedTables = 0;
            var addedFields = 0;

            foreach (var tableName in tableNames)
            {
                var tableId = await EnsureMetadataTableAsync(connection, transaction, tableName).ConfigureAwait(false);
                if (tableId.WasInserted) addedTables++;

                var escapedTableName = tableName.Replace("'", "''", StringComparison.Ordinal);
                var columns = new List<(int Cid, string Name, string Type)>();

                await using (var tableInfoCmd = connection.CreateCommand())
                {
                    tableInfoCmd.Transaction = transaction;
                    tableInfoCmd.CommandText = $"PRAGMA table_info('{escapedTableName}');";
                    await using var columnsReader = await tableInfoCmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await columnsReader.ReadAsync().ConfigureAwait(false))
                    {
                        var cid = columnsReader.IsDBNull(0) ? 0 : columnsReader.GetInt32(0);
                        var fieldName = columnsReader.IsDBNull(1) ? string.Empty : columnsReader.GetString(1);
                        var fieldType = columnsReader.IsDBNull(2) ? string.Empty : columnsReader.GetString(2);
                        if (string.IsNullOrWhiteSpace(fieldName)) continue;
                        columns.Add((cid, fieldName.Trim(), fieldType.Trim()));
                    }
                }

                foreach (var col in columns.OrderBy(c => c.Cid))
                {
                    var inserted = await EnsureMetadataFieldAsync(
                        connection,
                        transaction,
                        tableId.Id,
                        col.Name,
                        col.Type,
                        col.Cid + 1).ConfigureAwait(false);
                    if (inserted) addedFields++;
                }
            }

            await transaction.CommitAsync().ConfigureAwait(false);
            return Ok(new
            {
                success = true,
                addedTables,
                addedFields,
                scannedTables = tableNames.Count
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("metadata/run-crud-smoke")]
    public async Task<IActionResult> RunCrudSmoke([FromBody] MetadataCrudSmokeRequest? request)
    {
        request ??= new MetadataCrudSmokeRequest();
        var failures = new List<string>();
        var skipped = new List<string>();
        var succeeded = new List<string>();

        try
        {
            var tableNames = _db.MetadataTables
                .AsNoTracking()
                .Where(x => !string.IsNullOrWhiteSpace(x.TableName))
                .OrderBy(x => x.Id)
                .Select(x => x.TableName)
                .ToList();

            foreach (var tableName in tableNames)
            {
                if (string.IsNullOrWhiteSpace(tableName))
                {
                    continue;
                }

                await using var tx = await _db.Database.BeginTransactionAsync().ConfigureAwait(false);
                try
                {
                    if (!TryResolveEntity(tableName, out var entityType, out var resolveError))
                    {
                        skipped.Add($"{tableName}: {resolveError}");
                        await tx.RollbackAsync().ConfigureAwait(false);
                        continue;
                    }

                    var pk = entityType.FindPrimaryKey();
                    if (pk == null || pk.Properties.Count != 1 || !string.Equals(pk.Properties[0].Name, "Id", StringComparison.OrdinalIgnoreCase))
                    {
                        skipped.Add($"{tableName}: PK non supportata (richiesta PK singola 'Id').");
                        await tx.RollbackAsync().ConfigureAwait(false);
                        continue;
                    }

                    var fieldMetas = BuildFieldMetadata(entityType);
                    var checkAllowedValues = LoadTableCheckAllowedValues(tableName);
                    var fkMetas = BuildForeignKeyMetadata(entityType)
                        .GroupBy(x => x.DependentField, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    foreach (var dbFk in LoadDatabaseForeignKeys(tableName))
                    {
                        var field = fieldMetas.FirstOrDefault(f =>
                            string.Equals(f.Name, dbFk.DependentField, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(f.ColumnName, dbFk.DependentField, StringComparison.OrdinalIgnoreCase));
                        if (field == null) continue;
                        if (fkMetas.ContainsKey(field.Name)) continue;

                        fkMetas[field.Name] = new CrudForeignKeyMeta
                        {
                            DependentField = field.Name,
                            DescriptionField = string.Empty,
                            PrincipalEntity = dbFk.PrincipalEntity,
                            PrincipalTable = dbFk.PrincipalTable,
                            PrincipalKeyField = dbFk.PrincipalKeyField
                        };
                    }

                    var createPayload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    var uniqueTag = $"{tableName}_{Guid.NewGuid():N}";
                    var now = DateTime.UtcNow;
                    var skipTable = false;
                    var skipReason = string.Empty;

                    foreach (var field in fieldMetas)
                    {
                        if (field.IsPrimaryKey) continue;
                        if (field.IsConcurrencyToken || field.IsStoreGenerated) continue;
                        if (string.Equals(field.ClrType, "Byte[]", StringComparison.OrdinalIgnoreCase)) continue;

                        var requiredNoDefault = !field.Nullable;
                        if (fkMetas.TryGetValue(field.Name, out var fk))
                        {
                            var parentKey = QueryFirstPrincipalKeyValue(fk.PrincipalTable, fk.PrincipalKeyField);
                            if (parentKey is null)
                            {
                                if (requiredNoDefault)
                                {
                                    skipTable = true;
                                    skipReason = $"FK obbligatoria senza record parent: {field.Name} -> {fk.PrincipalTable}.{fk.PrincipalKeyField}";
                                    break;
                                }
                                continue;
                            }

                            createPayload[field.Name] = CoerceValueToClrType(parentKey, field.ClrType);
                            continue;
                        }

                        if (TryResolveCheckConstraintValue(field, checkAllowedValues, out var checkValue))
                        {
                            createPayload[field.Name] = checkValue;
                            continue;
                        }

                        createPayload[field.Name] = GenerateSmokeValue(field.Name, field.ClrType, now, uniqueTag);
                    }

                    if (skipTable)
                    {
                        skipped.Add($"{tableName}: {skipReason}");
                        await tx.RollbackAsync().ConfigureAwait(false);
                        continue;
                    }

                    if (createPayload.Count == 0)
                    {
                        skipped.Add($"{tableName}: nessun campo valorizzabile.");
                        await tx.RollbackAsync().ConfigureAwait(false);
                        continue;
                    }

                    var createExec = await ExecuteSmokeMutationWithConstraintFixesAsync(
                        tableName,
                        createPayload,
                        fieldMetas,
                        fkMetas,
                        payload => Create(tableName, payload)).ConfigureAwait(false);
                    if (!createExec.Success)
                    {
                        failures.Add($"{tableName}: CREATE failed ({createExec.Error ?? "esito non valido"})");
                        await tx.RollbackAsync().ConfigureAwait(false);
                        continue;
                    }

                    var idValue = ExtractIdFromEnvelope(createExec.Envelope);
                    if (idValue is null)
                    {
                        failures.Add($"{tableName}: CREATE ok ma Id non trovato.");
                        await tx.RollbackAsync().ConfigureAwait(false);
                        continue;
                    }

                    var updateField = fieldMetas
                        .FirstOrDefault(f =>
                            !f.IsPrimaryKey &&
                            !f.IsConcurrencyToken &&
                            !f.IsStoreGenerated &&
                            !string.Equals(f.ClrType, "Byte[]", StringComparison.OrdinalIgnoreCase) &&
                            !fkMetas.ContainsKey(f.Name) &&
                            !string.Equals(f.Name, "CreatedAt", StringComparison.OrdinalIgnoreCase));

                    if (updateField != null)
                    {
                        var updateValue = GenerateSmokeUpdateValue(updateField.Name, updateField.ClrType, now, uniqueTag);
                        if (TryResolveCheckConstraintValue(updateField, checkAllowedValues, out var checkUpdateValue))
                        {
                            updateValue = checkUpdateValue;
                        }

                        var updatePayload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            [updateField.Name] = updateValue
                        };

                        var updateExec = await ExecuteSmokeMutationWithConstraintFixesAsync(
                            tableName,
                            updatePayload,
                            fieldMetas,
                            fkMetas,
                            payload => Update(tableName, Convert.ToString(idValue, CultureInfo.InvariantCulture) ?? string.Empty, payload)).ConfigureAwait(false);
                        if (!updateExec.Success)
                        {
                            failures.Add($"{tableName}: UPDATE failed ({updateExec.Error ?? "esito non valido"})");
                            await tx.RollbackAsync().ConfigureAwait(false);
                            continue;
                        }
                    }

                    var deleteResult = await Delete(tableName, Convert.ToString(idValue, CultureInfo.InvariantCulture) ?? string.Empty).ConfigureAwait(false);
                    if (!TryReadEnvelope(deleteResult, out var deleteEnvelope, out var deleteError) || !IsSuccessEnvelope(deleteEnvelope))
                    {
                        failures.Add($"{tableName}: DELETE failed ({deleteError ?? "esito non valido"})");
                        await tx.RollbackAsync().ConfigureAwait(false);
                        continue;
                    }

                    succeeded.Add(tableName);
                    await tx.RollbackAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var message = FlattenExceptionMessage(ex);
                    failures.Add($"{tableName}: {message}");
                    await tx.RollbackAsync().ConfigureAwait(false);
                }
                finally
                {
                    _db.ChangeTracker.Clear();
                }
            }
            var skippedPayload = request.IncludeSkippedDetails ? skipped : new List<string>();
            return Ok(new
            {
                success = failures.Count == 0,
                scanned = succeeded.Count + skipped.Count + failures.Count,
                okCount = succeeded.Count,
                skippedCount = skipped.Count,
                failCount = failures.Count,
                succeeded,
                skipped = skippedPayload,
                failures,
                rolledBack = true
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, error = ex.Message });
        }
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
        var metadataTable = LoadMetadataTableConfig(entityType.GetTableName());
        var metadataFieldOverrides = LoadMetadataFieldOverrides(metadataTable?.TableId);
        var metadataCommands = LoadMetadataCommands(metadataTable?.TableId, viewType: "grid");

        // Default project behavior for aggregated system report errors:
        // hide resolved items unless caller explicitly filters by status.
        var tableNameNormalized = (entityType.GetTableName() ?? string.Empty).Trim().ToLowerInvariant();
        if (tableNameNormalized == "system_reports_errors")
        {
            var hasStatusFilter = (request.Filters ?? Enumerable.Empty<CrudFilter>())
                .Any(f => string.Equals(f.Field?.Trim(), "status", StringComparison.OrdinalIgnoreCase));

            if (!hasStatusFilter)
            {
                var defaultStatusFilter = new CrudFilter
                {
                    Field = "Status",
                    Op = "neq",
                    Value = JsonDocument.Parse("\"resolved\"").RootElement.Clone()
                };
                if (!TryApplyFilter(query, entityType.ClrType, defaultStatusFilter, out var filtered, out error))
                {
                    return BadRequest(new { success = false, error });
                }
                query = filtered;
            }
        }

        var filters = (request.Filters ?? Enumerable.Empty<CrudFilter>()).ToList();
        var deferredFilters = new List<CrudFilter>();
        foreach (var filter in filters)
        {
            if (FindProperty(entityType.ClrType, filter.Field) == null)
            {
                var isFkDescriptionField = foreignKeys.Any(fk =>
                    !string.IsNullOrWhiteSpace(fk.DescriptionField) &&
                    string.Equals(fk.DescriptionField, filter.Field, StringComparison.OrdinalIgnoreCase));
                if (isFkDescriptionField)
                {
                    deferredFilters.Add(filter);
                    continue;
                }
            }

            if (!TryApplyFilter(query, entityType.ClrType, filter, out var filtered, out error))
            {
                return BadRequest(new { success = false, error });
            }
            query = filtered;
        }

        var globalSearch = request.GlobalSearch?.Trim() ?? string.Empty;
        var hasGlobalSearch = !string.IsNullOrWhiteSpace(globalSearch);
        var requiresInMemorySearchOrFilter = hasGlobalSearch || deferredFilters.Count > 0;

        var sorts = (request.Sorts ?? new List<CrudSort>()).Where(s => !string.IsNullOrWhiteSpace(s.Field)).ToList();
        MaybeInjectOrderableSortForLookup(entityType, request, sorts);
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
            if (deferredFilters.Count > 0)
            {
                allRows = ApplyDeferredRowFilters(allRows, deferredFilters);
            }
            if (hasGlobalSearch)
            {
                allRows = ApplyGlobalSearchInMemory(allRows, globalSearch);
            }

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

            totalRows = allRows.Count;
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
                metadataTable,
                metadataFieldOverrides,
                metadataCommands,
                grouped = true,
                groupBy = keyName,
                page,
                pageSize,
                totalRows,
                totalGroups,
                groups = pageGroups
            });
        }

        List<Dictionary<string, object?>> paged;
        if (requiresInMemorySearchOrFilter)
        {
            var allRows = query.Cast<object>().ToList()
                .Select(ToDictionary)
                .ToList();
            EnrichRowsWithRelatedData(entityType.ClrType, allRows);
            EnrichRowsWithImagePreviewData(entityType.ClrType, allRows);
            EnrichRowsWithSoundPreviewData(entityType.ClrType, allRows);
            EnrichRowsWithVideoPreviewData(entityType.ClrType, allRows);
            EnrichRowsWithUsageStatsData(entityType.ClrType, allRows);

            if (deferredFilters.Count > 0)
            {
                allRows = ApplyDeferredRowFilters(allRows, deferredFilters);
            }

            if (hasGlobalSearch)
            {
                allRows = ApplyGlobalSearchInMemory(allRows, globalSearch);
            }

            totalRows = allRows.Count;
            paged = allRows
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();
        }
        else
        {
            paged = query
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
        }

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
            metadataTable,
            metadataFieldOverrides,
            metadataCommands,
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
        var metadataTable = LoadMetadataTableConfig(entityType.GetTableName());
        var metadataFieldOverrides = LoadMetadataFieldOverrides(metadataTable?.TableId);
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
            metadataTable,
            metadataFieldOverrides,
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
        _database.InvalidateEntityCache(entityType.GetTableName() ?? entityType.ClrType.Name);
        var createdRow = ToDictionary(entity);
        var metadataTable = LoadMetadataTableConfig(entityType.GetTableName());
        var metadataFieldOverrides = LoadMetadataFieldOverrides(metadataTable?.TableId);
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
            metadataTable,
            metadataFieldOverrides,
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
        _database.InvalidateEntityCache(entityType.GetTableName() ?? entityType.ClrType.Name);
        var updatedRow = ToDictionary(entity);
        var metadataTable = LoadMetadataTableConfig(entityType.GetTableName());
        var metadataFieldOverrides = LoadMetadataFieldOverrides(metadataTable?.TableId);
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
            metadataTable,
            metadataFieldOverrides,
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

        // Keep aggregated errors aligned with report lifecycle:
        // when reports are deleted, remove orphaned system_reports_errors rows.
        var tableName = (entityType.GetTableName() ?? string.Empty).Trim().ToLowerInvariant();
        if (tableName == "system_reports")
        {
            await DeleteOrphanSystemReportsErrorsAsync().ConfigureAwait(false);
        }

        _database.InvalidateEntityCache(entityType.GetTableName() ?? entityType.ClrType.Name);
        return Ok(new { success = true });
    }

    private async Task DeleteOrphanSystemReportsErrorsAsync()
    {
        await _db.Database.ExecuteSqlRawAsync(@"
DELETE FROM system_reports_errors
WHERE id IN (
    SELECT e.id
    FROM system_reports_errors e
    LEFT JOIN system_reports r
      ON r.error_id = e.id
     AND coalesce(r.deleted, 0) = 0
    WHERE r.id IS NULL
)").ConfigureAwait(false);
    }

    [HttpPost("{table}/commands/{commandCode}")]
    public async Task<IActionResult> ExecuteMetadataCommand(
        [FromRoute] string table,
        [FromRoute] string commandCode,
        [FromBody] CrudCommandExecuteRequest? request)
    {
        if (!TryResolveEntity(table, out var entityType, out var error))
        {
            return NotFound(new { success = false, error });
        }

        var metadataTable = LoadMetadataTableConfig(entityType.GetTableName());
        if (metadataTable?.TableId is null or <= 0)
        {
            return BadRequest(new { success = false, error = "Metadata tabella non configurati." });
        }

        var command = ResolveMetadataCommand(metadataTable.TableId, commandCode, viewType: "grid");
        if (command == null)
        {
            return NotFound(new { success = false, error = $"Comando non configurato o non attivo: '{commandCode}'" });
        }

        var payload = request ?? new CrudCommandExecuteRequest();
        try
        {
            var result = await ExecuteSupportedMetadataCommandAsync(entityType.GetTableName() ?? table, command.Code, payload).ConfigureAwait(false);
            return Ok(new
            {
                success = true,
                command = command.Code,
                runId = result.RunId,
                runIds = result.RunIds,
                message = result.Message
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    private async Task<CrudCommandExecutionResult> ExecuteSupportedMetadataCommandAsync(string tableName, string commandCode, CrudCommandExecuteRequest request)
    {
        var table = (tableName ?? string.Empty).Trim().ToLowerInvariant();
        var code = (commandCode ?? string.Empty).Trim().ToLowerInvariant();

        if (table == "models")
        {
            return await ExecuteModelsMetadataCommandAsync(code, request).ConfigureAwait(false);
        }

        if (table == "system_reports_errors")
        {
            return await ExecuteSystemReportsErrorsMetadataCommandAsync(code, request).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Comando '{commandCode}' non supportato per tabella '{tableName}'.");
    }

    private async Task<CrudCommandExecutionResult> ExecuteModelsMetadataCommandAsync(string commandCode, CrudCommandExecuteRequest request)
    {
        switch (commandCode)
        {
            case "models_run_json_score":
            {
                var handle = _jsonScoreTester.EnqueueJsonScoreForMissingModels();
                return new CrudCommandExecutionResult("JSON score avviato.", runId: handle.RunId);
            }
            case "models_run_instruction_score":
            {
                var handle = _instructionScoreTester.EnqueueInstructionScoreForMissingModels();
                return new CrudCommandExecutionResult("Instruction score avviato.", runId: handle.RunId);
            }
            case "models_run_intelligence_test":
            {
                var handle = _intelligenceTestService.EnqueueIntelligenceScoreForMissingModels();
                return new CrudCommandExecutionResult("Intelligence test avviato.", runId: handle.RunId);
            }
            case "models_add_ollama_models":
            {
                var added = await _database.AddLocalOllamaModelsAsync().ConfigureAwait(false);
                return new CrudCommandExecutionResult($"Discovery completata: {added} modelli aggiornati.");
            }
            case "models_purge_disabled_ollama":
            {
                var results = await _ollamaService.PurgeDisabledModelsAsync().ConfigureAwait(false);
                return new CrudCommandExecutionResult($"Purge completata: {results?.Count ?? 0} operazioni.");
            }
            case "models_refresh_contexts":
            {
                var updated = await _ollamaService.RefreshRunningContextsAsync().ConfigureAwait(false);
                return new CrudCommandExecutionResult($"Contesti aggiornati: {updated}.");
            }
            case "models_recalculate_scores":
            {
                _database.RecalculateAllWriterScores();
                return new CrudCommandExecutionResult("Punteggi ricalcolati.");
            }
            case "models_run_all":
            {
                var selectedGroup = string.IsNullOrWhiteSpace(request.Group)
                    ? (_database.GetTestGroups() ?? new List<string>()).FirstOrDefault()
                    : request.Group.Trim();
                if (string.IsNullOrWhiteSpace(selectedGroup))
                {
                    throw new InvalidOperationException("Nessun gruppo test disponibile.");
                }

                var runIds = new List<string>();
                var models = _database.ListModels().Where(m => m.Enabled).ToList();
                foreach (var model in models)
                {
                    var handle = _testService.EnqueueGroupRun(model.Name, selectedGroup);
                    if (handle != null && !string.IsNullOrWhiteSpace(handle.RunId))
                    {
                        runIds.Add(handle.RunId);
                    }
                }

                return new CrudCommandExecutionResult(
                    $"Run all avviato su gruppo '{selectedGroup}'.",
                    runIds: runIds);
            }
            case "models_run_group":
            {
                var modelName = ResolveModelName(request, out var modelId);
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    throw new InvalidOperationException("Modello non trovato.");
                }

                var group = string.IsNullOrWhiteSpace(request.Group)
                    ? (_database.GetTestGroups() ?? new List<string>()).FirstOrDefault()
                    : request.Group.Trim();
                if (string.IsNullOrWhiteSpace(group))
                {
                    throw new InvalidOperationException("Gruppo test richiesto.");
                }

                var handle = _testService.EnqueueGroupRun(modelName, group);
                if (handle == null || string.IsNullOrWhiteSpace(handle.RunId))
                {
                    throw new InvalidOperationException("Impossibile avviare il test di gruppo.");
                }

                return new CrudCommandExecutionResult(
                    $"Test gruppo '{group}' avviato per modello '{modelName}'.",
                    runId: handle.RunId);
            }
            case "models_run_json_score_model":
            {
                var modelName = ResolveEnabledModelName(request);
                var handle = _jsonScoreTester.EnqueueJsonScoreForModel(modelName);
                return new CrudCommandExecutionResult($"JSON score avviato per '{modelName}'.", runId: handle.RunId);
            }
            case "models_run_instruction_score_model":
            {
                var modelName = ResolveEnabledModelName(request);
                var handle = _instructionScoreTester.EnqueueInstructionScoreForModel(modelName);
                return new CrudCommandExecutionResult($"Instruction score avviato per '{modelName}'.", runId: handle.RunId);
            }
            case "models_run_intelligence_test_model":
            {
                var modelName = ResolveEnabledModelName(request);
                var handle = _intelligenceTestService.EnqueueIntelligenceScoreForModel(modelName);
                return new CrudCommandExecutionResult($"Intelligence test avviato per '{modelName}'.", runId: handle.RunId);
            }
            case "models_delete_model":
            {
                var modelName = ResolveModelName(request, out var modelId);
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    throw new InvalidOperationException("Modello non trovato.");
                }

                if (modelId.HasValue && modelId.Value > 0)
                {
                    var agentsUsing = _database.GetAgentsUsingModel(modelId.Value);
                    if (agentsUsing.Count > 0)
                    {
                        throw new InvalidOperationException($"Impossibile eliminare: usato da agenti {string.Join(", ", agentsUsing)}.");
                    }

                    _database.DeleteModel(modelId.Value.ToString(CultureInfo.InvariantCulture));
                    return new CrudCommandExecutionResult($"Modello '{modelName}' eliminato.");
                }

                _database.DeleteModel(modelName);
                return new CrudCommandExecutionResult($"Modello '{modelName}' eliminato.");
            }
            default:
                throw new InvalidOperationException($"Comando models non supportato: '{commandCode}'.");
        }
    }

    private async Task<CrudCommandExecutionResult> ExecuteSystemReportsErrorsMetadataCommandAsync(string commandCode, CrudCommandExecuteRequest request)
    {
        switch (commandCode)
        {
            case "system_reports_errors_extract":
            {
                var result = _database.ProcessUnextractedErrors();
                return new CrudCommandExecutionResult(
                    $"Extract completato: scanned={result.Scanned}, linked={result.Linked}, inserted={result.Inserted}, updated={result.Updated}, unknown={result.UnknownType}.");
            }
            case "system_reports_errors_set_candidate_resolved":
            case "system_reports_errors_set_resolved":
            case "system_reports_errors_set_ignored":
            {
                if (request.RowId is null or <= 0)
                {
                    throw new InvalidOperationException("RowId obbligatorio per aggiornare lo stato.");
                }

                var status = commandCode switch
                {
                    "system_reports_errors_set_candidate_resolved" => "candidate_resolved",
                    "system_reports_errors_set_resolved" => "resolved",
                    "system_reports_errors_set_ignored" => "ignored",
                    _ => throw new InvalidOperationException($"Comando non supportato: {commandCode}")
                };

                var ok = _database.UpdateSystemReportErrorStatus(request.RowId.Value, status);
                if (!ok)
                {
                    throw new InvalidOperationException("Aggiornamento stato non riuscito.");
                }

                return new CrudCommandExecutionResult($"Stato aggiornato a '{status}'.");
            }
            case "system_reports_errors_send_to_github":
            {
                if (request.RowId is null or <= 0)
                {
                    throw new InvalidOperationException("RowId obbligatorio per l'invio a GitHub.");
                }

                var rowId = request.RowId.Value;
                var errorRow = await _db.SystemReportsErrors
                    .FirstOrDefaultAsync(x => x.Id == rowId)
                    .ConfigureAwait(false);

                if (errorRow == null)
                {
                    throw new InvalidOperationException($"Errore aggregato non trovato: id={rowId}");
                }

                if (errorRow.GitHubIssueId.HasValue && errorRow.GitHubIssueId.Value > 0)
                {
                    return new CrudCommandExecutionResult($"Issue GitHub già presente: #{errorRow.GitHubIssueId.Value}");
                }

                var linkedReports = await _db.SystemReports
                    .AsNoTracking()
                    .CountAsync(x => x.ErrorId == rowId)
                    .ConfigureAwait(false);

                var issueNumber = await CreateGitHubIssueForSystemReportErrorAsync(errorRow, linkedReports).ConfigureAwait(false);
                errorRow.GitHubIssueId = issueNumber;
                await _db.SaveChangesAsync().ConfigureAwait(false);

                return new CrudCommandExecutionResult($"Issue GitHub creata: #{issueNumber}");
            }
            default:
                throw new InvalidOperationException($"Comando system_reports_errors non supportato: '{commandCode}'.");
        }
    }

    private async Task<int> CreateGitHubIssueForSystemReportErrorAsync(SystemReportError row, int linkedReports)
    {
        var token = _configuration["Secrets:GitHub:ApiKey"]?.Trim();
        var owner = _configuration["Secrets:GitHub:Owner"]?.Trim();
        var repo = _configuration["Secrets:GitHub:Repo"]?.Trim();

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            throw new InvalidOperationException("Configurazione GitHub mancante in Secrets:GitHub (ApiKey, Owner, Repo).");
        }

        var title = $"[SystemReportError] {row.ErrorType} ({row.Agent ?? "n/a"} / {row.Step ?? "n/a"})";
        var body = new StringBuilder();
        body.AppendLine("## Aggregated Error");
        body.AppendLine();
        body.AppendLine($"- ErrorType: `{row.ErrorType}`");
        body.AppendLine($"- Agent: `{row.Agent ?? "n/a"}`");
        body.AppendLine($"- Step: `{row.Step ?? "n/a"}`");
        body.AppendLine($"- CheckName: `{row.CheckName ?? "n/a"}`");
        body.AppendLine($"- Occurrences: `{row.Occurrences}`");
        body.AppendLine($"- Linked system_reports: `{linkedReports}`");
        body.AppendLine();

        if (!string.IsNullOrWhiteSpace(row.ErrorSummary))
        {
            body.AppendLine("### Summary");
            body.AppendLine(row.ErrorSummary.Trim());
            body.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(row.FailReason))
        {
            body.AppendLine("### First FailReason");
            body.AppendLine("```text");
            body.AppendLine(row.FailReason.Trim());
            body.AppendLine("```");
        }

        var payload = new
        {
            title,
            body = body.ToString(),
            labels = new[] { "system-report", "bug" }
        };

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TinyGeneratorLC-SystemReports");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        var response = await http.PostAsync(
            $"https://api.github.com/repos/{owner}/{repo}/issues",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub issue create failed: {(int)response.StatusCode} {response.ReasonPhrase} - {json}");
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("number", out var numberEl) || !numberEl.TryGetInt32(out var issueNumber) || issueNumber <= 0)
        {
            throw new InvalidOperationException("Risposta GitHub priva di issue number valido.");
        }

        return issueNumber;
    }

    private string ResolveEnabledModelName(CrudCommandExecuteRequest request)
    {
        var modelName = ResolveModelName(request, out _);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new InvalidOperationException("Modello non trovato.");
        }

        var modelInfo = _database.ListModels()
            .FirstOrDefault(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
        if (modelInfo == null || !modelInfo.Enabled)
        {
            throw new InvalidOperationException("Il modello è disabilitato.");
        }

        return modelName;
    }

    private string ResolveModelName(CrudCommandExecuteRequest request, out int? modelId)
    {
        modelId = request.ModelId;
        var modelName = string.IsNullOrWhiteSpace(request.ModelName) ? null : request.ModelName.Trim();

        if ((modelId is null or <= 0) && request.RowId.HasValue)
        {
            modelId = request.RowId;
        }

        if (!string.IsNullOrWhiteSpace(modelName))
        {
            return modelName;
        }

        if (modelId.HasValue && modelId.Value > 0)
        {
            var resolvedId = modelId.Value;
            var byId = _database.ListModels().FirstOrDefault(m => m.Id == resolvedId);
            if (byId != null)
            {
                return byId.Name;
            }
        }

        return string.Empty;
    }

    private IQueryable CreateEntityQuery(Type clrType)
    {
        var setMethod = typeof(DbContext).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .First(m => m.Name == nameof(DbContext.Set) && m.IsGenericMethod && m.GetParameters().Length == 0)
            .MakeGenericMethod(clrType);

        return (IQueryable)setMethod.Invoke(_db, null)!;
    }

    private object? QueryFirstPrincipalKeyValue(string? principalTable, string? principalKeyField)
    {
        if (string.IsNullOrWhiteSpace(principalTable) || string.IsNullOrWhiteSpace(principalKeyField))
        {
            return null;
        }

        if (!TryResolveEntity(principalTable, out var principalEntityType, out _))
        {
            return null;
        }

        var principalRows = CreateEntityQuery(principalEntityType.ClrType)
            .Cast<object>()
            .ToList()
            .Select(ToDictionary)
            .ToList();

        foreach (var row in principalRows)
        {
            if (!row.TryGetValue(principalKeyField, out var raw) || raw == null) continue;
            return raw;
        }

        return null;
    }

    private async Task<(bool Success, JsonElement Envelope, string? Error)> ExecuteSmokeMutationWithConstraintFixesAsync(
        string tableName,
        Dictionary<string, object?> payload,
        List<CrudFieldMeta> fieldMetas,
        Dictionary<string, CrudForeignKeyMeta> fkMetas,
        Func<JsonElement, Task<IActionResult>> executeAction)
    {
        JsonElement envelope = default;
        string? lastError = null;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var result = await executeAction(ToJsonElement(payload)).ConfigureAwait(false);
            if (!TryReadEnvelope(result, out envelope, out lastError))
            {
                return (false, envelope, lastError ?? "Risultato azione non supportato.");
            }

            if (IsSuccessEnvelope(envelope))
            {
                return (true, envelope, null);
            }

            if (string.IsNullOrWhiteSpace(lastError))
            {
                break;
            }

            var fixedCheck = TryApplyCheckConstraintFix(lastError, payload, fieldMetas);
            var fixedUnique = !fixedCheck && TryApplyUniqueConstraintFix(tableName, lastError, payload, fieldMetas, fkMetas);
            if (!fixedCheck && !fixedUnique)
            {
                break;
            }
        }

        return (false, envelope, lastError);
    }

    private bool TryApplyCheckConstraintFix(string error, Dictionary<string, object?> payload, List<CrudFieldMeta> fieldMetas)
    {
        var marker = "CHECK constraint failed:";
        var markerIdx = error.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx < 0) return false;

        var expression = error[(markerIdx + marker.Length)..].Trim().Trim('\'', '"');
        if (string.IsNullOrWhiteSpace(expression)) return false;

        var candidateField = fieldMetas
            .Where(f =>
                expression.Contains(f.Name, StringComparison.OrdinalIgnoreCase) ||
                expression.Contains(f.ColumnName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.ColumnName.Length)
            .FirstOrDefault();
        if (candidateField == null) return false;

        var inMatch = Regex.Match(expression, @"\bIN\s*\((?<vals>[^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!inMatch.Success) return false;

        var rawValues = inMatch.Groups["vals"].Value;
        var quotedValues = Regex.Matches(rawValues, @"'(?<v>(?:''|[^'])*)'", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(m => m.Groups["v"].Value.Replace("''", "'", StringComparison.Ordinal))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
        if (quotedValues.Count == 0) return false;

        var selected = quotedValues[0];
        var converted = CoerceValueToClrType(selected, candidateField.ClrType);
        if (payload.TryGetValue(candidateField.Name, out var current) &&
            string.Equals(Convert.ToString(current, CultureInfo.InvariantCulture), Convert.ToString(converted, CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return false;
        }

        payload[candidateField.Name] = converted;
        return true;
    }

    private bool TryApplyUniqueConstraintFix(
        string tableName,
        string error,
        Dictionary<string, object?> payload,
        List<CrudFieldMeta> fieldMetas,
        Dictionary<string, CrudForeignKeyMeta> fkMetas)
    {
        var marker = "UNIQUE constraint failed:";
        var markerIdx = error.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx < 0) return false;

        var raw = error[(markerIdx + marker.Length)..].Trim().Trim('\'', '"');
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 1) return false;

        var single = parts[0];
        var dotIdx = single.LastIndexOf('.');
        var columnToken = dotIdx >= 0 ? single[(dotIdx + 1)..].Trim() : single.Trim();
        if (string.IsNullOrWhiteSpace(columnToken)) return false;

        var field = fieldMetas.FirstOrDefault(f =>
            string.Equals(f.ColumnName, columnToken, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Name, columnToken, StringComparison.OrdinalIgnoreCase));
        if (field == null) return false;

        if (fkMetas.TryGetValue(field.Name, out var fk))
        {
            var parentKey = QueryFirstUnusedPrincipalKeyValue(fk.PrincipalTable, fk.PrincipalKeyField, tableName, field.ColumnName);
            if (parentKey != null)
            {
                payload[field.Name] = CoerceValueToClrType(parentKey, field.ClrType);
                return true;
            }
        }

        var currentText = Convert.ToString(payload.GetValueOrDefault(field.Name), CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.Equals(field.ClrType, "String", StringComparison.OrdinalIgnoreCase))
        {
            payload[field.Name] = $"{currentText}_{Guid.NewGuid():N}"[..Math.Min(40, currentText.Length + 33)];
            return true;
        }

        if (field.Nullable)
        {
            payload[field.Name] = null;
            return true;
        }

        return false;
    }

    private object? QueryFirstUnusedPrincipalKeyValue(string? principalTable, string? principalKeyField, string? childTable, string? childColumn)
    {
        if (string.IsNullOrWhiteSpace(principalTable) ||
            string.IsNullOrWhiteSpace(principalKeyField) ||
            string.IsNullOrWhiteSpace(childTable) ||
            string.IsNullOrWhiteSpace(childColumn))
        {
            return null;
        }

        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
        var pTable = QuoteIdentifier(principalTable);
        var pKey = QuoteIdentifier(principalKeyField);
        var cTable = QuoteIdentifier(childTable);
        var cColumn = QuoteIdentifier(childColumn);
        cmd.CommandText = $@"
SELECT p.{pKey}
FROM {pTable} p
WHERE NOT EXISTS (
    SELECT 1
    FROM {cTable} c
    WHERE c.{cColumn} = p.{pKey}
)
LIMIT 1;";

        var value = cmd.ExecuteScalar();
        return value == DBNull.Value ? null : value;
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{(identifier ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private Dictionary<string, IReadOnlyList<string>> LoadTableCheckAllowedValues(string tableName)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(tableName)) return result;

        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
        cmd.CommandText = @"
SELECT sql
FROM sqlite_master
WHERE type = 'table'
  AND lower(name) = lower(@tableName)
LIMIT 1;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@tableName";
        p.Value = tableName;
        cmd.Parameters.Add(p);

        var rawSql = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawSql)) return result;

        var checkPattern = new Regex(@"(?:\blower\s*\(\s*(?<col1>[a-zA-Z0-9_]+)\s*\)|(?<col2>[a-zA-Z0-9_]+))\s+IN\s*\((?<vals>[^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (Match match in checkPattern.Matches(rawSql))
        {
            if (!match.Success) continue;
            var column = match.Groups["col1"].Success ? match.Groups["col1"].Value : match.Groups["col2"].Value;
            if (string.IsNullOrWhiteSpace(column)) continue;

            var valuesRaw = match.Groups["vals"].Value;
            var values = Regex.Matches(valuesRaw, @"'(?<v>(?:''|[^'])*)'", RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(m => m.Groups["v"].Value.Replace("''", "'", StringComparison.Ordinal))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (values.Count == 0) continue;

            result[column] = values;
        }

        return result;
    }

    private static bool TryResolveCheckConstraintValue(
        CrudFieldMeta field,
        Dictionary<string, IReadOnlyList<string>> checkAllowedValues,
        out object? value)
    {
        value = null;
        if (checkAllowedValues.Count == 0) return false;

        if (!checkAllowedValues.TryGetValue(field.ColumnName, out var values) &&
            !checkAllowedValues.TryGetValue(field.Name, out values))
        {
            return false;
        }

        var selected = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return false;
        }

        value = CoerceValueToClrType(selected, field.ClrType);
        return true;
    }

    private static object? GenerateSmokeValue(string fieldName, string clrTypeName, DateTime now, string uniqueTag)
    {
        var name = fieldName.ToLowerInvariant();
        var type = clrTypeName.ToLowerInvariant();
        var nowIso = now.ToString("o", CultureInfo.InvariantCulture);

        // Prefer CLR type first to avoid name-based false positives (es. JsonScore, GeneratedTtsJson).
        switch (type)
        {
            case "byte":
            case "sbyte":
            case "int16":
            case "uint16":
            case "int32":
            case "uint32":
            case "int64":
            case "uint64":
                return 1;
            case "single":
            case "double":
            case "decimal":
                return 1.5;
            case "boolean":
            case "bool":
                return false;
            case "datetime":
            case "datetimeoffset":
                return nowIso;
            case "guid":
                return Guid.NewGuid().ToString();
            case "byte[]":
                return Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        }

        if (name is "isactive" or "enabled") return true;
        if (name is "isdeleted" or "deleted") return false;
        if (name.Contains("createdat") || name.Contains("updatedat") || name.EndsWith("date")) return nowIso;
        if (name.Contains("json") && type == "string") return "{}";
        if (name.Contains("path")) return $"/tmp/{uniqueTag}.dat";
        if (name.Contains("name") || name.Contains("title") || name.Contains("description") || name.Contains("note")) return $"test_{uniqueTag}";

        return $"v_{uniqueTag}";
    }

    private static object? GenerateSmokeUpdateValue(string fieldName, string clrTypeName, DateTime now, string uniqueTag)
    {
        var name = fieldName.ToLowerInvariant();
        var type = clrTypeName.ToLowerInvariant();
        var nowIso = now.ToString("o", CultureInfo.InvariantCulture);

        switch (type)
        {
            case "byte":
            case "sbyte":
            case "int16":
            case "uint16":
            case "int32":
            case "uint32":
            case "int64":
            case "uint64":
                return 2;
            case "single":
            case "double":
            case "decimal":
                return 2.5;
            case "boolean":
            case "bool":
                return true;
            case "datetime":
            case "datetimeoffset":
                return nowIso;
            case "guid":
                return Guid.NewGuid().ToString();
            case "byte[]":
                return Convert.ToBase64String(new byte[] { 5, 6, 7, 8 });
        }

        if (name is "isactive" or "enabled") return false;
        if (name is "isdeleted" or "deleted") return true;
        if (name.Contains("updatedat") || name.EndsWith("date")) return nowIso;
        if (name.Contains("json") && type == "string") return "{\"updated\":true}";
        return $"u_{uniqueTag}";
    }

    private static JsonElement ToJsonElement(object payload)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return doc.RootElement.Clone();
    }

    private static bool TryReadEnvelope(IActionResult actionResult, out JsonElement envelope, out string? error)
    {
        envelope = default;
        error = null;

        if (actionResult is ObjectResult objectResult)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(objectResult.Value));
            envelope = doc.RootElement.Clone();
            if (envelope.TryGetProperty("error", out var errEl))
            {
                error = errEl.ValueKind == JsonValueKind.String ? errEl.GetString() : errEl.ToString();
            }
            return true;
        }

        error = "Risultato azione non supportato.";
        return false;
    }

    private static bool IsSuccessEnvelope(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("success", out var successEl)) return false;
        return successEl.ValueKind == JsonValueKind.True;
    }

    private static object? ExtractIdFromEnvelope(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("item", out var itemEl) || itemEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (itemEl.TryGetProperty("Id", out var idEl) || itemEl.TryGetProperty("id", out idEl))
        {
            return idEl.ValueKind switch
            {
                JsonValueKind.Number when idEl.TryGetInt32(out var i) => i,
                JsonValueKind.Number when idEl.TryGetInt64(out var l) => l,
                JsonValueKind.String => idEl.GetString(),
                _ => idEl.ToString()
            };
        }

        return null;
    }

    private static string FlattenExceptionMessage(Exception ex)
    {
        var parts = new List<string>();
        var current = ex;
        while (current != null)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                parts.Add(current.Message.Trim());
            }
            current = current.InnerException;
        }

        return parts.Count == 0 ? "Errore sconosciuto." : string.Join(" | ", parts.Distinct());
    }

    private static object? CoerceValueToClrType(object? value, string clrTypeName)
    {
        if (value is null) return null;
        var type = (clrTypeName ?? string.Empty).ToLowerInvariant();
        try
        {
            return type switch
            {
                "string" => Convert.ToString(value, CultureInfo.InvariantCulture),
                "int32" or "int" => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                "int64" or "long" => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                "double" => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                "single" or "float" => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                "decimal" => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                "boolean" or "bool" => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                _ => value
            };
        }
        catch
        {
            return value;
        }
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

    private CrudTableMetadataConfig? LoadMetadataTableConfig(string? tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return null;
        }

        var metadata = _db.MetadataTables
            .AsNoTracking()
            .FirstOrDefault(x => x.TableName.ToLower() == tableName.ToLower());
        if (metadata == null)
        {
            return null;
        }

        string? childTableName = null;
        if (metadata.ChildTableId is > 0)
        {
            childTableName = _db.MetadataTables
                .AsNoTracking()
                .Where(x => x.Id == metadata.ChildTableId.Value)
                .Select(x => x.TableName)
                .FirstOrDefault();
        }

        return new CrudTableMetadataConfig
        {
            TableId = metadata.Id,
            TableName = metadata.TableName,
            Title = metadata.Title,
            Note = metadata.Note,
            Icon = metadata.Icon,
            DefaultSortField = metadata.DefaultSortField,
            DefaultSortDirection = metadata.DefaultSortDirection,
            DefaultPageSize = metadata.DefaultPageSize,
            EditMode = metadata.EditMode,
            AllowInsert = metadata.AllowInsert,
            AllowUpdate = metadata.AllowUpdate,
            AllowDelete = metadata.AllowDelete,
            ChildTableId = metadata.ChildTableId,
            ChildTableName = childTableName,
            ChildTableParentIdFieldName = metadata.ChildTableParentIdFieldName
        };
    }

    private List<CrudFieldMetadataOverride> LoadMetadataFieldOverrides(int? tableId)
    {
        if (!tableId.HasValue || tableId.Value <= 0)
        {
            return new List<CrudFieldMetadataOverride>();
        }

        return _db.MetadataFields
            .AsNoTracking()
            .Where(x => x.ParentTableId == tableId.Value)
            .OrderBy(x => x.SortOverride ?? int.MaxValue)
            .ThenBy(x => x.Id)
            .Select(x => new CrudFieldMetadataOverride
            {
                FieldId = x.Id,
                ParentTableId = x.ParentTableId,
                FieldName = x.FieldName,
                Caption = x.Caption,
                EditorType = x.EditorType,
                Width = x.Width,
                Multiline = x.Multiline,
                RequiredOverride = x.RequiredOverride,
                ReadonlyOverride = x.ReadonlyOverride,
                VisibleOverride = x.VisibleOverride,
                SortOverride = x.SortOverride,
                GroupName = x.GroupName
            })
            .ToList();
    }

    private List<CrudMetadataCommandMeta> LoadMetadataCommands(int? tableId, string viewType)
    {
        if (!tableId.HasValue || tableId.Value <= 0)
        {
            return new List<CrudMetadataCommandMeta>();
        }

        var viewTypeNormalized = (viewType ?? "grid").Trim().ToLowerInvariant();

        var query =
            from mc in _db.MetadataCommands.AsNoTracking()
            join c in _db.Commands.AsNoTracking() on mc.CommandId equals c.Id
            where mc.TableId == tableId.Value
                  && (mc.ViewType ?? "grid").ToLower() == viewTypeNormalized
                  && mc.IsActive
                  && mc.Visible
                  && mc.Enabled
                  && c.IsActive
            orderby mc.Position, mc.Id
            select new CrudMetadataCommandMeta
            {
                Id = mc.Id,
                CommandId = c.Id,
                Code = c.Code,
                Description = c.Description ?? c.Code,
                Icon = c.Icon,
                ViewType = mc.ViewType,
                Position = mc.Position,
                RequiresConfirm = mc.RequiresConfirm,
                ConfirmMessage = mc.ConfirmMessage
            };

        return query.ToList();
    }

    private CrudMetadataCommandMeta? ResolveMetadataCommand(int tableId, string commandCode, string viewType)
    {
        if (tableId <= 0 || string.IsNullOrWhiteSpace(commandCode))
        {
            return null;
        }

        var code = commandCode.Trim().ToLowerInvariant();
        var commands = LoadMetadataCommands(tableId, viewType);
        return commands.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(int Id, bool WasInserted)> EnsureMetadataTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName)
    {
        await using var selectCmd = connection.CreateCommand();
        selectCmd.Transaction = transaction;
        selectCmd.CommandText = "SELECT id FROM metadata_tables WHERE lower(table_name) = lower(@tableName) LIMIT 1;";
        var selectParam = selectCmd.CreateParameter();
        selectParam.ParameterName = "@tableName";
        selectParam.Value = tableName;
        selectCmd.Parameters.Add(selectParam);

        var existing = await selectCmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (existing != null && existing != DBNull.Value)
        {
            return (Convert.ToInt32(existing, CultureInfo.InvariantCulture), false);
        }

        await using var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = transaction;
        insertCmd.CommandText = @"
INSERT INTO metadata_tables
(
    table_name,
    title,
    note,
    icon,
    default_sort_field,
    default_sort_direction,
    default_page_size,
    edit_mode,
    allow_insert,
    allow_update,
    allow_delete,
    child_table_id,
    child_table_parent_id_field_name
)
VALUES
(
    @tableName,
    @title,
    '',
    'pi pi-table',
    NULL,
    NULL,
    NULL,
    NULL,
    1,
    1,
    1,
    NULL,
    NULL
);
SELECT last_insert_rowid();";
        var insertParam = insertCmd.CreateParameter();
        insertParam.ParameterName = "@tableName";
        insertParam.Value = tableName;
        insertCmd.Parameters.Add(insertParam);
        var titleParam = insertCmd.CreateParameter();
        titleParam.ParameterName = "@title";
        titleParam.Value = PrettifyFieldName(tableName);
        insertCmd.Parameters.Add(titleParam);

        var insertedId = await insertCmd.ExecuteScalarAsync().ConfigureAwait(false);
        return (Convert.ToInt32(insertedId, CultureInfo.InvariantCulture), true);
    }

    private static async Task<bool> EnsureMetadataFieldAsync(
        DbConnection connection,
        DbTransaction transaction,
        int parentTableId,
        string fieldName,
        string fieldType,
        int sortOverride)
    {
        await using var existsCmd = connection.CreateCommand();
        existsCmd.Transaction = transaction;
        existsCmd.CommandText = @"
SELECT id
FROM metadata_fields
WHERE parent_table_id = @parentTableId
  AND lower(field_name) = lower(@fieldName)
LIMIT 1;";
        var pParent = existsCmd.CreateParameter();
        pParent.ParameterName = "@parentTableId";
        pParent.Value = parentTableId;
        existsCmd.Parameters.Add(pParent);
        var pField = existsCmd.CreateParameter();
        pField.ParameterName = "@fieldName";
        pField.Value = fieldName;
        existsCmd.Parameters.Add(pField);

        var existing = await existsCmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (existing != null && existing != DBNull.Value)
        {
            return false;
        }

        var editorType = InferEditorType(fieldName, fieldType);
        var multiline = editorType is "textarea" or "json";

        await using var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = transaction;
        insertCmd.CommandText = @"
INSERT INTO metadata_fields
(
    parent_table_id,
    field_name,
    caption,
    editor_type,
    width,
    multiline,
    required_override,
    readonly_override,
    visible_override,
    sort_override,
    group_name
)
VALUES
(
    @parentTableId,
    @fieldName,
    @caption,
    @editorType,
    NULL,
    @multiline,
    NULL,
    NULL,
    NULL,
    @sortOverride,
    NULL
);";

        var iParent = insertCmd.CreateParameter();
        iParent.ParameterName = "@parentTableId";
        iParent.Value = parentTableId;
        insertCmd.Parameters.Add(iParent);

        var iField = insertCmd.CreateParameter();
        iField.ParameterName = "@fieldName";
        iField.Value = fieldName;
        insertCmd.Parameters.Add(iField);

        var iCaption = insertCmd.CreateParameter();
        iCaption.ParameterName = "@caption";
        iCaption.Value = PrettifyFieldName(fieldName);
        insertCmd.Parameters.Add(iCaption);

        var iEditor = insertCmd.CreateParameter();
        iEditor.ParameterName = "@editorType";
        iEditor.Value = editorType;
        insertCmd.Parameters.Add(iEditor);

        var iMultiline = insertCmd.CreateParameter();
        iMultiline.ParameterName = "@multiline";
        iMultiline.Value = multiline ? 1 : 0;
        insertCmd.Parameters.Add(iMultiline);

        var iSort = insertCmd.CreateParameter();
        iSort.ParameterName = "@sortOverride";
        iSort.Value = sortOverride;
        insertCmd.Parameters.Add(iSort);

        await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        return true;
    }

    private static string PrettifyFieldName(string fieldName)
    {
        var raw = fieldName ?? string.Empty;
        var normalized = raw.Replace("_", " ", StringComparison.Ordinal).Trim();
        if (normalized.Length == 0) return string.Empty;

        var sb = new StringBuilder(normalized.Length + 8);
        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            if (i > 0 && char.IsUpper(ch) && char.IsLetter(normalized[i - 1]) && char.IsLower(normalized[i - 1]))
            {
                sb.Append(' ');
            }
            sb.Append(ch);
        }

        return sb.ToString().Trim();
    }

    private static string InferEditorType(string fieldName, string fieldType)
    {
        var lowerName = (fieldName ?? string.Empty).Trim().ToLowerInvariant();
        var lowerType = (fieldType ?? string.Empty).Trim().ToLowerInvariant();

        var isBoolName = lowerName.StartsWith("is_", StringComparison.Ordinal)
            || lowerName.StartsWith("has_", StringComparison.Ordinal)
            || lowerName.StartsWith("allow_", StringComparison.Ordinal)
            || lowerName.StartsWith("enabled", StringComparison.Ordinal)
            || lowerName.StartsWith("active", StringComparison.Ordinal)
            || lowerName is "isactive" or "is_active" or "isdeleted" or "is_deleted";
        if (isBoolName && (lowerType.Contains("int", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(lowerType)))
        {
            return "checkbox";
        }

        if (lowerName.Contains("json", StringComparison.Ordinal))
        {
            return "json";
        }

        if (lowerType.Contains("int", StringComparison.Ordinal)
            || lowerType.Contains("real", StringComparison.Ordinal)
            || lowerType.Contains("floa", StringComparison.Ordinal)
            || lowerType.Contains("doub", StringComparison.Ordinal)
            || lowerType.Contains("dec", StringComparison.Ordinal)
            || lowerType.Contains("num", StringComparison.Ordinal))
        {
            return "number";
        }

        if (lowerName.EndsWith("_at", StringComparison.Ordinal)
            || lowerName.Contains("date", StringComparison.Ordinal)
            || lowerName.Contains("time", StringComparison.Ordinal))
        {
            return "datetime";
        }

        if (lowerName.Contains("description", StringComparison.Ordinal)
            || lowerName.Contains("prompt", StringComparison.Ordinal)
            || lowerName.Contains("instruction", StringComparison.Ordinal)
            || lowerName.Contains("notes", StringComparison.Ordinal)
            || lowerName.Contains("content", StringComparison.Ordinal)
            || lowerName.Contains("text", StringComparison.Ordinal))
        {
            return "textarea";
        }

        return "text";
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
        else if (targetType == typeof(string))
        {
            converted = converted?.ToString() ?? string.Empty;
            op = "contains";
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
        var toLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
        var loweredMember = Expression.Call(safeMember, toLower);
        var loweredConstant = Expression.Call(constant, toLower);
        return Expression.AndAlso(
            Expression.NotEqual(safeMember, Expression.Constant(null, typeof(string))),
            Expression.Call(loweredMember, typeof(string).GetMethod(method, new[] { typeof(string) })!, loweredConstant));
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

    private static List<Dictionary<string, object?>> ApplyGlobalSearchInMemory(
        List<Dictionary<string, object?>> rows,
        string searchText)
    {
        if (rows.Count == 0) return rows;
        var needle = (searchText ?? string.Empty).Trim();
        if (needle.Length == 0) return rows;

        return rows
            .Where(row => row.Values.Any(value =>
            {
                if (value == null) return false;
                var text = value.ToString();
                return !string.IsNullOrWhiteSpace(text) &&
                       text.Contains(needle, StringComparison.OrdinalIgnoreCase);
            }))
            .ToList();
    }

    private static List<Dictionary<string, object?>> ApplyDeferredRowFilters(
        List<Dictionary<string, object?>> rows,
        IReadOnlyCollection<CrudFilter> filters)
    {
        if (rows.Count == 0 || filters.Count == 0) return rows;

        var filtered = rows;
        foreach (var filter in filters)
        {
            var field = (filter.Field ?? string.Empty).Trim();
            if (field.Length == 0) continue;
            var value = ReadFilterValueAsString(filter.Value);
            if (string.IsNullOrWhiteSpace(value)) continue;

            filtered = filtered
                .Where(row =>
                {
                    if (!TryGetRowValueCaseInsensitive(row, field, out var raw)) return false;
                    if (raw == null) return false;
                    var text = raw.ToString();
                    return !string.IsNullOrWhiteSpace(text) &&
                           text.Contains(value, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }

        return filtered;
    }

    private static bool TryGetRowValueCaseInsensitive(
        IReadOnlyDictionary<string, object?> row,
        string field,
        out object? value)
    {
        if (row.TryGetValue(field, out value))
        {
            return true;
        }

        foreach (var kvp in row)
        {
            if (string.Equals(kvp.Key, field, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string ReadFilterValueAsString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => element.ToString()
        };
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

    private static void MaybeInjectOrderableSortForLookup(IEntityType entityType, CrudQueryRequest request, List<CrudSort> sorts)
    {
        if (!typeof(IOrderable).IsAssignableFrom(entityType.ClrType))
        {
            return;
        }

        if (FindProperty(entityType.ClrType, nameof(IOrderable.SortOrder)) == null)
        {
            return;
        }

        if (sorts.Any(s => string.Equals(s.Field, nameof(IOrderable.SortOrder), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // Detect generic FK lookup requests emitted by Shared/Index combos:
        // page=1, large page size, no filters/global search, sort by PK asc.
        if (request.Page != 1 || request.PageSize < 500)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.GlobalSearch))
        {
            return;
        }

        if (request.Filters is { Count: > 0 })
        {
            return;
        }

        if (sorts.Count != 1)
        {
            return;
        }

        var pk = entityType.FindPrimaryKey();
        var pkName = pk?.Properties.FirstOrDefault()?.Name;
        if (string.IsNullOrWhiteSpace(pkName))
        {
            return;
        }

        var sort = sorts[0];
        if (!string.Equals(sort.Field, pkName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(sort.Dir, "asc", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        sorts.Insert(0, new CrudSort
        {
            Field = nameof(IOrderable.SortOrder),
            Dir = "asc"
        });
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
        var normalized = field.Trim();
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(p =>
            {
                if (!p.CanRead || !p.CanWrite) return false;
                if (Attribute.IsDefined(p, typeof(NotMappedAttribute))) return false;

                if (string.Equals(p.Name, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var col = p.GetCustomAttribute<ColumnAttribute>();
                return !string.IsNullOrWhiteSpace(col?.Name) &&
                       string.Equals(col!.Name, normalized, StringComparison.OrdinalIgnoreCase);
            });
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
            if (useCount <= 0)
            {
                row["SuccessRatePercent"] = null;
                continue;
            }

            var denominator = useCount;
            var percent = Math.Round((double)success * 100.0 / denominator, 2, MidpointRounding.AwayFromZero);
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

    private List<CrudForeignKeyMeta> LoadDatabaseForeignKeys(string tableName)
    {
        var result = new List<CrudForeignKeyMeta>();
        if (string.IsNullOrWhiteSpace(tableName)) return result;

        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
        var escapedTableName = tableName.Replace("'", "''", StringComparison.Ordinal);
        cmd.CommandText = $"PRAGMA foreign_key_list('{escapedTableName}');";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var principalTable = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var dependentField = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var principalKey = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            if (string.IsNullOrWhiteSpace(principalTable) || string.IsNullOrWhiteSpace(dependentField) || string.IsNullOrWhiteSpace(principalKey))
            {
                continue;
            }

            result.Add(new CrudForeignKeyMeta
            {
                DependentField = dependentField,
                DescriptionField = string.Empty,
                PrincipalEntity = principalTable,
                PrincipalTable = principalTable,
                PrincipalKeyField = principalKey
            });
        }

        return result;
    }

    private static List<CrudFieldMeta> BuildFieldMetadata(IEntityType entityType)
    {
        var tableIdentifier = StoreObjectIdentifier.Table(entityType.GetTableName() ?? string.Empty, entityType.GetSchema());
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
            var columnName = prop.GetColumnName(tableIdentifier) ?? prop.GetColumnBaseName() ?? prop.Name;

            fkByDependent.TryGetValue(prop.Name, out var fk);
            var principalTable = fk?.PrincipalEntityType.GetTableName() ?? fk?.PrincipalEntityType.ClrType.Name;
            var principalKey = fk?.PrincipalKey.Properties.FirstOrDefault()?.Name;

            result.Add(new CrudFieldMeta
            {
                Name = prop.Name,
                ColumnName = columnName,
                ClrType = clrTypeName,
                Nullable = prop.IsNullable,
                IsPrimaryKey = pkNames.Contains(prop.Name),
                IsForeignKey = fk != null,
                IsConcurrencyToken = prop.IsConcurrencyToken,
                IsStoreGenerated = prop.ValueGenerated != ValueGenerated.Never,
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
    public string ColumnName { get; set; } = string.Empty;
    public string ClrType { get; set; } = string.Empty;
    public bool Nullable { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsForeignKey { get; set; }
    public bool IsConcurrencyToken { get; set; }
    public bool IsStoreGenerated { get; set; }
    public string? PrincipalTable { get; set; }
    public string? PrincipalKeyField { get; set; }
}

public sealed class CrudTableMetadataConfig
{
    public int TableId { get; set; }
    public string TableName { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Note { get; set; }
    public string? Icon { get; set; }
    public string? DefaultSortField { get; set; }
    public string? DefaultSortDirection { get; set; }
    public int? DefaultPageSize { get; set; }
    public string? EditMode { get; set; }
    public bool AllowInsert { get; set; } = true;
    public bool AllowUpdate { get; set; } = true;
    public bool AllowDelete { get; set; } = true;
    public int? ChildTableId { get; set; }
    public string? ChildTableName { get; set; }
    public string? ChildTableParentIdFieldName { get; set; }
}

public sealed class CrudFieldMetadataOverride
{
    public int FieldId { get; set; }
    public int ParentTableId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string? Caption { get; set; }
    public string? EditorType { get; set; }
    public int? Width { get; set; }
    public bool Multiline { get; set; }
    public bool? RequiredOverride { get; set; }
    public bool? ReadonlyOverride { get; set; }
    public bool? VisibleOverride { get; set; }
    public int? SortOverride { get; set; }
    public string? GroupName { get; set; }
}

public sealed class CrudMetadataCommandMeta
{
    public int Id { get; set; }
    public int CommandId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string ViewType { get; set; } = "grid";
    public string Position { get; set; } = "row";
    public bool RequiresConfirm { get; set; }
    public string? ConfirmMessage { get; set; }
}

public sealed class CrudCommandExecuteRequest
{
    public int? RowId { get; set; }
    public int? ModelId { get; set; }
    public string? ModelName { get; set; }
    public string? Group { get; set; }
}

public sealed class CrudCommandExecutionResult
{
    public CrudCommandExecutionResult(string message, string? runId = null, IReadOnlyList<string>? runIds = null)
    {
        Message = message;
        RunId = runId;
        RunIds = runIds;
    }

    public string Message { get; }
    public string? RunId { get; }
    public IReadOnlyList<string>? RunIds { get; }
}

public sealed class MetadataCrudSmokeRequest
{
    public bool IncludeSkippedDetails { get; set; } = true;
}
