using System.Globalization;
using System.Text.Json;
using DotnetRag.Ingestor.Models;

namespace DotnetRag.Ingestor.Services;

public sealed record MetadataValidationResult(
    bool IsValid,
    List<string> Errors,
    Dictionary<string, object?> NormalizedMetadata);

public static class MetadataSchemaValidator
{
    private static readonly HashSet<string> ReservedFields =
        ["type", "subtype", "location"];

    private static readonly Dictionary<string, string> TypeAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["str"] = "string",
            ["int"] = "integer",
            ["double"] = "float",
            ["bool"] = "boolean"
        };

    private static readonly HashSet<string> ValidTypes =
        ["string", "datetime", "number", "integer", "float", "boolean", "array"];

    private static readonly HashSet<string> ValidArrayTypes =
        ["string", "number", "integer", "float", "boolean"];

    public static IReadOnlyList<Dictionary<string, object?>> NormalizeSchema(
        IEnumerable<MetadataField> fields)
    {
        var normalized = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields)
        {
            var name = field.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Metadata field name cannot be empty.");
            }

            if (ReservedFields.Contains(name))
            {
                throw new ArgumentException(
                    $"Field name '{name}' is reserved and cannot be used as a custom metadata field.");
            }

            if (!seen.Add(name))
            {
                throw new ArgumentException($"Duplicate metadata field name '{name}'.");
            }

            var type = NormalizeType(field.Type);
            if (!ValidTypes.Contains(type))
            {
                throw new ArgumentException($"Invalid metadata field type '{field.Type}'.");
            }

            var arrayType = field.ArrayType is null ? null : NormalizeType(field.ArrayType);
            if (type == "array")
            {
                if (string.IsNullOrWhiteSpace(arrayType) || !ValidArrayTypes.Contains(arrayType))
                {
                    throw new ArgumentException(
                        $"Metadata array field '{name}' requires a valid array_type.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(arrayType))
            {
                throw new ArgumentException(
                    $"Metadata field '{name}' can only set array_type when type is array.");
            }

            if (field.MaxLength is <= 0)
            {
                throw new ArgumentException(
                    $"Metadata field '{name}' max_length must be positive when provided.");
            }

            normalized.Add(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["type"] = type,
                ["description"] = field.Description,
                ["required"] = field.Required,
                ["user_defined"] = field.UserDefined,
                ["support_dynamic_filtering"] = field.SupportDynamicFiltering,
                ["array_type"] = arrayType,
                ["max_length"] = field.MaxLength
            });
        }

        EnsureSystemField(normalized, "filename", "string", true);
        EnsureSystemField(normalized, "page_number", "integer", false);
        EnsureSystemField(normalized, "start_time", "integer", false);
        EnsureSystemField(normalized, "end_time", "integer", false);

        return normalized;
    }

    public static MetadataValidationResult ValidateAndNormalize(
        IReadOnlyList<Dictionary<string, object?>> schema,
        IReadOnlyDictionary<string, object?> metadata)
    {
        var errors = new List<string>();
        var normalized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var fields = schema
            .Select(FieldFromDictionary)
            .Where(field => field.UserDefined)
            .ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var key in metadata.Keys)
        {
            if (!fields.ContainsKey(key))
            {
                errors.Add($"unexpected metadata field '{key}'.");
            }
        }

        foreach (var field in fields.Values)
        {
            if (!metadata.TryGetValue(field.Name, out var raw) || raw is null)
            {
                if (field.Required)
                {
                    errors.Add($"required metadata field '{field.Name}' is missing.");
                }
                continue;
            }

            if (!TryNormalizeValue(field, raw, out var value, out var error))
            {
                errors.Add(error);
                continue;
            }

            normalized[field.Name] = value;
        }

        return new MetadataValidationResult(errors.Count == 0, errors, normalized);
    }

    public static string NormalizeType(string? type)
    {
        var value = string.IsNullOrWhiteSpace(type) ? "string" : type.Trim();
        return TypeAliases.TryGetValue(value, out var alias) ? alias : value.ToLowerInvariant();
    }

    private static void EnsureSystemField(
        List<Dictionary<string, object?>> schema,
        string name,
        string type,
        bool userDefined)
    {
        if (schema.Any(field => string.Equals(field["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        schema.Add(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["type"] = type,
            ["description"] = name == "filename" ? "Name of the uploaded file" : string.Empty,
            ["required"] = false,
            ["user_defined"] = userDefined,
            ["support_dynamic_filtering"] = userDefined || name == "page_number",
            ["array_type"] = null,
            ["max_length"] = null
        });
    }

    private static MetadataField FieldFromDictionary(Dictionary<string, object?> field)
    {
        return new MetadataField
        {
            Name = field.GetValueOrDefault("name")?.ToString() ?? string.Empty,
            Type = NormalizeType(field.GetValueOrDefault("type")?.ToString()),
            Description = field.GetValueOrDefault("description")?.ToString() ?? string.Empty,
            Required = ToBool(field.GetValueOrDefault("required")),
            UserDefined = !field.TryGetValue("user_defined", out var userDefined)
                || ToBool(userDefined, defaultValue: true),
            SupportDynamicFiltering = !field.TryGetValue("support_dynamic_filtering", out var filtering)
                || ToBool(filtering, defaultValue: true),
            ArrayType = field.GetValueOrDefault("array_type")?.ToString(),
            MaxLength = ToNullableInt(field.GetValueOrDefault("max_length"))
        };
    }

    private static bool TryNormalizeValue(
        MetadataField field,
        object raw,
        out object? normalized,
        out string error)
    {
        normalized = null;
        error = string.Empty;
        raw = UnwrapJsonElement(raw);

        switch (NormalizeType(field.Type))
        {
            case "string":
                normalized = raw.ToString() ?? string.Empty;
                if (field.MaxLength is { } maxString && normalized.ToString()!.Length > maxString)
                {
                    error = $"field '{field.Name}' exceeds max_length {maxString}.";
                    return false;
                }
                return true;
            case "datetime":
                if (raw is DateTime dt)
                {
                    normalized = NormalizeDateTime(dt);
                    return true;
                }
                if (DateTimeOffset.TryParse(
                        raw.ToString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var dto))
                {
                    normalized = NormalizeDateTime(dto.UtcDateTime);
                    return true;
                }
                error = $"field '{field.Name}' expected a valid datetime.";
                return false;
            case "integer":
                if (long.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    normalized = integer;
                    return true;
                }
                error = $"field '{field.Name}' expected type integer.";
                return false;
            case "float":
            case "number":
                if (double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    normalized = number;
                    return true;
                }
                error = $"field '{field.Name}' expected type number.";
                return false;
            case "boolean":
                if (TryParseBoolean(raw, out var boolean))
                {
                    normalized = boolean;
                    return true;
                }
                error = $"field '{field.Name}' expected type boolean.";
                return false;
            case "array":
                if (!TryGetArray(raw, out var items))
                {
                    error = $"field '{field.Name}' expected type array.";
                    return false;
                }
                if (field.MaxLength is { } maxArray && items.Count > maxArray)
                {
                    error = $"field '{field.Name}' exceeds max_length {maxArray}.";
                    return false;
                }
                var itemField = new MetadataField
                {
                    Name = field.Name,
                    Type = field.ArrayType ?? "string"
                };
                var normalizedItems = new List<object?>();
                foreach (var item in items)
                {
                    if (!TryNormalizeValue(itemField, item!, out var normalizedItem, out error))
                    {
                        return false;
                    }
                    normalizedItems.Add(normalizedItem);
                }
                normalized = normalizedItems;
                return true;
            default:
                normalized = raw;
                return true;
        }
    }

    private static object UnwrapJsonElement(object value)
    {
        if (value is not JsonElement json)
        {
            return value;
        }

        return json.ValueKind switch
        {
            JsonValueKind.String => json.GetString() ?? string.Empty,
            JsonValueKind.Number when json.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when json.TryGetDouble(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => json.EnumerateArray()
                .Select(item => UnwrapJsonElement(item))
                .ToList(),
            _ => json.ToString()
        };
    }

    private static bool TryGetArray(object raw, out List<object?> items)
    {
        raw = UnwrapJsonElement(raw);
        if (raw is IEnumerable<object?> objItems && raw is not string)
        {
            items = objItems.ToList();
            return true;
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.Array } json)
        {
            items = json.EnumerateArray().Select(item => (object?)UnwrapJsonElement(item)).ToList();
            return true;
        }

        items = [];
        return false;
    }

    private static bool TryParseBoolean(object raw, out bool value)
    {
        raw = UnwrapJsonElement(raw);
        if (raw is bool boolean)
        {
            value = boolean;
            return true;
        }

        switch (raw.ToString()?.Trim().ToLowerInvariant())
        {
            case "true":
            case "1":
            case "on":
                value = true;
                return true;
            case "false":
            case "0":
            case "off":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static string NormalizeDateTime(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static bool ToBool(object? value, bool defaultValue = false)
    {
        value = value is null ? null : UnwrapJsonElement(value);
        return value switch
        {
            null => defaultValue,
            bool b => b,
            _ when bool.TryParse(value.ToString(), out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static int? ToNullableInt(object? value)
    {
        value = value is null ? null : UnwrapJsonElement(value);
        return int.TryParse(value?.ToString(), out var parsed) ? parsed : null;
    }
}
