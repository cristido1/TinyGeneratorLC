using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TinyGenerator.Services;

public sealed class JsonSchemaResponseFormatCheck : IDeterministicCheck
{
    private const int MaxReportedProblems = 5;
    private readonly string _schemaJson;
    private readonly string _schemaName;

    public JsonSchemaResponseFormatCheck(string schemaJson, string schemaName)
    {
        _schemaJson = schemaJson ?? string.Empty;
        _schemaName = string.IsNullOrWhiteSpace(schemaName) ? "schema.json" : schemaName;
    }

    public string Rule => $"La risposta deve rispettare il JSON schema '{_schemaName}'.";
    public string GenericErrorDescription => "Formato di risposta JSON non rispettato";
    public Microsoft.Extensions.Options.IOptions<object>? Options { get; set; }

    public IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        try
        {
            var raw = (textToCheck ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Fail("Risposta vuota: atteso JSON conforme allo schema.");
            }

            using var schemaDoc = JsonDocument.Parse(_schemaJson);
            using var responseDoc = JsonDocument.Parse(raw);

            var errors = new List<string>(MaxReportedProblems);
            ValidateAgainstSchema(responseDoc.RootElement, schemaDoc.RootElement, "$", errors, depth: 0);

            if (errors.Count == 0)
            {
                return new DeterministicResult
                {
                    Successed = true,
                    Message = "ok",
                    CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
                };
            }

            return Fail($"primi {Math.Min(MaxReportedProblems, errors.Count)} problemi: {string.Join(" | ", errors)}");
        }
        catch (JsonException ex)
        {
            return Fail($"Risposta non JSON valida: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Fail($"Errore validazione JSON schema: {ex.Message}");
        }

        DeterministicResult Fail(string message) => new()
        {
            Successed = false,
            Message = $"json_response_format_check: non ha rispettato il formato di risposta JSON richiesto ({_schemaName}): {message}",
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
    }

    private static bool ValidateAgainstSchema(
        JsonElement data,
        JsonElement schema,
        string path,
        List<string> errors,
        int depth)
    {
        if (errors.Count >= MaxReportedProblems)
        {
            return false;
        }

        if (depth > 64)
        {
            return AddError(errors, $"{path}: profondita schema eccessiva");
        }

        var schemaTypes = GetSchemaTypes(schema);
        if (!IsTypeCompatible(data, schemaTypes))
        {
            var expectedType = schemaTypes.Count == 0 ? "(any)" : string.Join("|", schemaTypes);
            return AddError(errors, $"{path}: tipo atteso '{expectedType}', ricevuto '{data.ValueKind}'");
        }

        if (!ValidateEnum(data, schema, path, errors))
        {
            return false;
        }

        if (!ValidateNumericRange(data, schema, path, errors))
        {
            return false;
        }

        if (schemaTypes.Any(t => string.Equals(t, "object", StringComparison.OrdinalIgnoreCase)) &&
            data.ValueKind == JsonValueKind.Object)
        {
            return ValidateObject(data, schema, path, errors, depth + 1);
        }

        if (schemaTypes.Any(t => string.Equals(t, "array", StringComparison.OrdinalIgnoreCase)) &&
            data.ValueKind == JsonValueKind.Array)
        {
            return ValidateArray(data, schema, path, errors, depth + 1);
        }

        return true;
    }

    private static bool ValidateObject(
        JsonElement data,
        JsonElement schema,
        string path,
        List<string> errors,
        int depth)
    {
        if (errors.Count >= MaxReportedProblems)
        {
            return false;
        }

        if (TryGetPropertyIgnoreCase(schema, "required", out var required) &&
            required.ValueKind == JsonValueKind.Array)
        {
            foreach (var req in required.EnumerateArray())
            {
                if (req.ValueKind != JsonValueKind.String) continue;
                var propName = req.GetString();
                if (string.IsNullOrWhiteSpace(propName)) continue;
                if (!TryGetPropertyIgnoreCase(data, propName!, out _))
                {
                    if (!AddError(errors, $"{path}: manca campo obbligatorio '{propName}'"))
                    {
                        return false;
                    }
                }
            }
        }

        var hasProperties = TryGetPropertyIgnoreCase(schema, "properties", out var properties) &&
                            properties.ValueKind == JsonValueKind.Object;

        if (TryGetPropertyIgnoreCase(schema, "additionalProperties", out var additionalProps) &&
            additionalProps.ValueKind == JsonValueKind.False &&
            hasProperties)
        {
            var allowedNames = properties.EnumerateObject().Select(p => p.Name).ToList();
            foreach (var dataProp in data.EnumerateObject())
            {
                if (allowedNames.Any(n => string.Equals(n, dataProp.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!AddError(errors, $"{path}: campo non consentito '{dataProp.Name}'"))
                {
                    return false;
                }
            }
        }

        if (!hasProperties)
        {
            return errors.Count < MaxReportedProblems;
        }

        foreach (var propSchema in properties.EnumerateObject())
        {
            if (!TryGetPropertyIgnoreCase(data, propSchema.Name, out var child))
            {
                continue;
            }

            if (!ValidateAgainstSchema(child, propSchema.Value, $"{path}.{propSchema.Name}", errors, depth + 1))
            {
                return false;
            }
        }

        return errors.Count < MaxReportedProblems;
    }

    private static bool ValidateArray(
        JsonElement data,
        JsonElement schema,
        string path,
        List<string> errors,
        int depth)
    {
        if (errors.Count >= MaxReportedProblems)
        {
            return false;
        }

        if (!TryGetPropertyIgnoreCase(schema, "items", out var itemsSchema))
        {
            return true;
        }

        var index = 0;
        foreach (var item in data.EnumerateArray())
        {
            if (!ValidateAgainstSchema(item, itemsSchema, $"{path}[{index}]", errors, depth + 1))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    private static List<string> GetSchemaTypes(JsonElement schema)
    {
        if (TryGetPropertyIgnoreCase(schema, "type", out var t))
        {
            if (t.ValueKind == JsonValueKind.String)
            {
                var single = t.GetString();
                return string.IsNullOrWhiteSpace(single) ? new List<string>() : new List<string> { single! };
            }

            if (t.ValueKind == JsonValueKind.Array)
            {
                var types = new List<string>();
                foreach (var item in t.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var candidate = item.GetString();
                        if (!string.IsNullOrWhiteSpace(candidate))
                        {
                            types.Add(candidate!);
                        }
                    }
                }

                if (types.Count > 0)
                {
                    return types;
                }
            }
        }

        if (TryGetPropertyIgnoreCase(schema, "properties", out _))
        {
            return new List<string> { "object" };
        }

        if (TryGetPropertyIgnoreCase(schema, "items", out _))
        {
            return new List<string> { "array" };
        }

        return new List<string>();
    }

    private static bool IsTypeCompatible(JsonElement data, IReadOnlyCollection<string> schemaTypes)
    {
        if (schemaTypes == null || schemaTypes.Count == 0)
        {
            return true;
        }

        foreach (var schemaType in schemaTypes)
        {
            if (string.IsNullOrWhiteSpace(schemaType))
            {
                continue;
            }

            if (IsTypeCompatibleSingle(data, schemaType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTypeCompatibleSingle(JsonElement data, string schemaType)
    {
        return schemaType.ToLowerInvariant() switch
        {
            "object" => data.ValueKind == JsonValueKind.Object,
            "array" => data.ValueKind == JsonValueKind.Array,
            "string" => data.ValueKind == JsonValueKind.String,
            "number" => data.ValueKind == JsonValueKind.Number,
            "integer" => data.ValueKind == JsonValueKind.Number && data.TryGetInt64(out _),
            "boolean" => data.ValueKind == JsonValueKind.True || data.ValueKind == JsonValueKind.False,
            "null" => data.ValueKind == JsonValueKind.Null,
            _ => true
        };
    }

    private static bool ValidateEnum(JsonElement data, JsonElement schema, string path, List<string> errors)
    {
        if (!TryGetPropertyIgnoreCase(schema, "enum", out var enumNode) ||
            enumNode.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        foreach (var allowed in enumNode.EnumerateArray())
        {
            if (JsonElementsEqual(data, allowed))
            {
                return true;
            }
        }

        return AddError(errors, $"{path}: valore '{FormatValue(data)}' non ammesso da enum");
    }

    private static bool ValidateNumericRange(JsonElement data, JsonElement schema, string path, List<string> errors)
    {
        if (data.ValueKind != JsonValueKind.Number)
        {
            return true;
        }

        if (!data.TryGetDecimal(out var value))
        {
            return AddError(errors, $"{path}: numero non valido");
        }

        if (TryGetPropertyIgnoreCase(schema, "minimum", out var minNode) &&
            minNode.ValueKind == JsonValueKind.Number &&
            minNode.TryGetDecimal(out var minValue))
        {
            if (value < minValue)
            {
                return AddError(errors, $"{path}: valore {value.ToString(CultureInfo.InvariantCulture)} < minimum {minValue.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        if (TryGetPropertyIgnoreCase(schema, "maximum", out var maxNode) &&
            maxNode.ValueKind == JsonValueKind.Number &&
            maxNode.TryGetDecimal(out var maxValue))
        {
            if (value > maxValue)
            {
                return AddError(errors, $"{path}: valore {value.ToString(CultureInfo.InvariantCulture)} > maximum {maxValue.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        return true;
    }

    private static bool JsonElementsEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => left.TryGetDecimal(out var l) && right.TryGetDecimal(out var r) && l == r,
            JsonValueKind.True => true,
            JsonValueKind.False => true,
            JsonValueKind.Null => true,
            _ => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal)
        };
    }

    private static string FormatValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        };
    }

    private static bool AddError(List<string> errors, string message)
    {
        if (errors.Count >= MaxReportedProblems)
        {
            return false;
        }

        errors.Add(message);
        return errors.Count < MaxReportedProblems;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out value))
            {
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
