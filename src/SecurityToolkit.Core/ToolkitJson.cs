using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SecurityToolkit.Core;

/// <summary>
/// Deterministic, canonical JSON serialization: object keys sorted ordinally (case-sensitive),
/// stable indentation, UTF-8 without a BOM, and a trailing newline. Determinism matters because
/// security.lock.json is committed to git and must diff cleanly across reruns that don't actually
/// change state. The generated ".security/Toolkit.Common.ps1" rewrites the same lock file in the
/// pipeline (ConvertTo-ToolkitJson, Sort-Object -CaseSensitive); keep the two formats identical.
/// </summary>
public static class ToolkitJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerOptions.Default)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Serializes <paramref name="value"/> to canonical JSON text (object keys sorted, 2-space
    /// indentation, trailing newline). Accepts:
    /// <see cref="IDictionary{TKey,TValue}"/>-like objects, <see cref="IEnumerable{T}"/> (excluding
    /// strings), and scalar leaf values (including <see langword="null"/>).
    /// </summary>
    public static string Serialize(object? value)
    {
        var node = ToCanonicalNode(value);
        var json = node is null
            ? "null"
            : node.ToJsonString(SerializerOptions);
        return json + "\n";
    }

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="path"/> as canonical JSON, UTF-8
    /// without a byte-order mark.
    /// </summary>
    public static void WriteFile(string path, object? value)
    {
        var json = Serialize(value);
        ToolkitFile.WriteAllText(path, json);
    }

    /// <summary>
    /// Reads and parses "security.lock.json" from <paramref name="directory"/> as a nested
    /// dictionary tree, and validates its schema.
    /// </summary>
    /// <param name="directory">Directory containing "security.lock.json".</param>
    /// <exception cref="FileNotFoundException">"security.lock.json" does not exist in <paramref name="directory"/>.</exception>
    /// <exception cref="InvalidOperationException">The lock file's schemaVersion is not 1.</exception>
    public static IDictionary<string, object?> ReadLock(string directory)
    {
        var path = Path.Combine(directory, "security.lock.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Missing security.lock.json", path);
        }

        var json = File.ReadAllText(path);
        var node = JsonNode.Parse(json);
        var lockData = ToHashtable(node) as IDictionary<string, object?>
            ?? throw new InvalidOperationException("security.lock.json must contain a JSON object.");

        var schemaVersion = lockData.TryGetValue("schemaVersion", out var value) ? value : null;
        var schemaVersionAsLong = schemaVersion switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => (long?)null,
        };

        if (schemaVersionAsLong != 1)
        {
            throw new InvalidOperationException("Unsupported toolkit lock schema.");
        }

        return lockData;
    }

    /// <summary>
    /// Recursively converts a parsed <see cref="JsonNode"/> tree into plain
    /// <see cref="Dictionary{TKey,TValue}"/>/<see cref="List{T}"/>/scalar values
    /// (as opposed to strongly-typed POCOs).
    /// </summary>
    private static object? ToHashtable(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                {
                    var dictionary = new Dictionary<string, object?>();
                    foreach (var kvp in obj)
                    {
                        dictionary[kvp.Key] = ToHashtable(kvp.Value);
                    }
                    return dictionary;
                }
            case JsonArray array:
                return array.Select(ToHashtable).ToList();
            case JsonValue jsonValue:
                if (jsonValue.TryGetValue<long>(out var longValue))
                {
                    return longValue;
                }
                if (jsonValue.TryGetValue<double>(out var doubleValue))
                {
                    return doubleValue;
                }
                if (jsonValue.TryGetValue<bool>(out var boolValue))
                {
                    return boolValue;
                }
                if (jsonValue.TryGetValue<string>(out var stringValue))
                {
                    return stringValue;
                }
                return jsonValue.ToString();
            default:
                return node.ToString();
        }
    }

    /// <summary>
    /// Recursively rebuilds <paramref name="value"/> as a <see cref="JsonNode"/> tree with every
    /// object's keys sorted ordinally.
    /// </summary>
    public static JsonNode? ToCanonicalNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonNode existingNode:
                return CanonicalizeExistingNode(existingNode);
            case string s:
                return JsonValue.Create(s);
            case IDictionary<string, object?> dictionary:
                return CanonicalizeDictionary(dictionary.Keys, key => dictionary[key]);
            case System.Collections.IDictionary legacyDictionary:
                {
                    var keys = legacyDictionary.Keys.Cast<object>().Select(k => k.ToString() ?? string.Empty).ToList();
                    return CanonicalizeDictionary(keys, key => legacyDictionary[key]);
                }
            case System.Collections.IEnumerable enumerable:
                {
                    var array = new JsonArray();
                    foreach (var item in enumerable)
                    {
                        array.Add(ToCanonicalNode(item));
                    }
                    return array;
                }
            default:
                return JsonValue.Create(value);
        }
    }

    private static JsonObject CanonicalizeDictionary(IEnumerable<string> keys, Func<string, object?> getValue)
    {
        var result = new JsonObject();
        foreach (var key in keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            result[key] = ToCanonicalNode(getValue(key));
        }
        return result;
    }

    private static JsonNode? CanonicalizeExistingNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    var result = new JsonObject();
                    foreach (var key in obj.Select(kvp => kvp.Key).OrderBy(k => k, StringComparer.Ordinal))
                    {
                        var child = obj[key];
                        result[key] = child is null ? null : CanonicalizeExistingNode(child);
                    }
                    return result;
                }
            case JsonArray array:
                {
                    var result = new JsonArray();
                    foreach (var item in array)
                    {
                        result.Add(item is null ? null : CanonicalizeExistingNode(item));
                    }
                    return result;
                }
            default:
                return node.DeepClone();
        }
    }
}
