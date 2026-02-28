using System.Text.Json;

namespace TinyGenerator.Services;

public sealed class JsonSchemaResponseFormatCheck : IDeterministicCheck
{
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

            var errors = new List<string>();
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

            return Fail(string.Join(" | ", errors));
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

    private static void ValidateAgainstSchema(
        JsonElement data,
        JsonElement schema,
        string path,
        List<string> errors,
        int depth)
    {
        if (depth > 64)
        {
            errors.Add($"{path}: profondita schema eccessiva");
            return;
        }

        var schemaType = GetSchemaType(schema);
        if (!IsTypeCompatible(data, schemaType))
        {
            errors.Add($"{path}: tipo atteso '{schemaType}', ricevuto '{data.ValueKind}'");
            return;
        }

        if (string.Equals(schemaType, "object", StringComparison.OrdinalIgnoreCase))
        {
            ValidateObject(data, schema, path, errors, depth + 1);
            return;
        }

        if (string.Equals(schemaType, "array", StringComparison.OrdinalIgnoreCase))
        {
            ValidateArray(data, schema, path, errors, depth + 1);
        }
    }

    private static void ValidateObject(
        JsonElement data,
        JsonElement schema,
        string path,
        List<string> errors,
        int depth)
    {
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
                    errors.Add($"{path}: manca campo obbligatorio '{propName}'");
                }
            }
        }

        if (!TryGetPropertyIgnoreCase(schema, "properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var propSchema in properties.EnumerateObject())
        {
            if (!TryGetPropertyIgnoreCase(data, propSchema.Name, out var child))
            {
                continue;
            }

            ValidateAgainstSchema(child, propSchema.Value, $"{path}.{propSchema.Name}", errors, depth + 1);
        }
    }

    private static void ValidateArray(
        JsonElement data,
        JsonElement schema,
        string path,
        List<string> errors,
        int depth)
    {
        if (!TryGetPropertyIgnoreCase(schema, "items", out var itemsSchema))
        {
            return;
        }

        var index = 0;
        foreach (var item in data.EnumerateArray())
        {
            ValidateAgainstSchema(item, itemsSchema, $"{path}[{index}]", errors, depth + 1);
            index++;
        }
    }

    private static string GetSchemaType(JsonElement schema)
    {
        if (TryGetPropertyIgnoreCase(schema, "type", out var t))
        {
            if (t.ValueKind == JsonValueKind.String)
            {
                return t.GetString() ?? string.Empty;
            }

            if (t.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in t.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var candidate = item.GetString();
                        if (!string.IsNullOrWhiteSpace(candidate) &&
                            !string.Equals(candidate, "null", StringComparison.OrdinalIgnoreCase))
                        {
                            return candidate!;
                        }
                    }
                }
            }
        }

        if (TryGetPropertyIgnoreCase(schema, "properties", out _))
        {
            return "object";
        }

        if (TryGetPropertyIgnoreCase(schema, "items", out _))
        {
            return "array";
        }

        return string.Empty;
    }

    private static bool IsTypeCompatible(JsonElement data, string schemaType)
    {
        if (string.IsNullOrWhiteSpace(schemaType))
        {
            return true;
        }

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

