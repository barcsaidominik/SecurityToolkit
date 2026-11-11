namespace SecurityToolkit.Core.Installation;

/// <summary>Typed accessors over the JSON-shaped config/lock dictionaries.</summary>
internal static class ConfigAccess
{
    internal static IDictionary<string, object?> GetToolsDict(IDictionary<string, object?> config) => GetDict(config, "tools");

    internal static IDictionary<string, object?>? TryGetDict(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) ? value as IDictionary<string, object?> : null;

    internal static IDictionary<string, object?> GetDict(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) && value is IDictionary<string, object?> dict
            ? dict
            : throw new InvalidOperationException($"Missing config section: {key}");

    internal static bool GetBool(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) && value is bool b && b;

    internal static string? GetString(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) ? value as string : null;

    internal static long GetLong(IDictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value))
        {
            return 0;
        }

        return value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => 0,
        };
    }

    internal static List<string> GetStringList(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) && value is IEnumerable<object?> list
            ? list.Select(v => v as string ?? string.Empty).Where(s => s.Length > 0).ToList()
            : new List<string>();
}
