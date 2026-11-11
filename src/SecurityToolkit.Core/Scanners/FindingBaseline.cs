using System.Security.Cryptography;
using System.Text;

namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// The committed <c>.security/baseline.json</c>: the accepted (already known) blocking findings of
/// every scanner that can block, as fingerprint → occurrence count. When the file exists, Gate mode
/// (and the --staged hook) only blocks on findings beyond it; without it the gate stays absolute.
/// A fingerprint never contains a raw secret or source text, only a SHA-256 over
/// location-independent parts (rule, file, normalized code, package, advisory), so moving code
/// within a file does not turn a known finding into a new one.
/// </summary>
public static class FindingBaseline
{
    public const string FileName = "baseline.json";

    /// <summary>Loads the baseline; null when the project has none (absolute gate).</summary>
    /// <exception cref="InvalidOperationException">The file exists but is not a valid baseline.</exception>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? Load(string securityDirectory)
    {
        var path = Path.Combine(securityDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        IDictionary<string, object?> document;
        try
        {
            document = ScannerSupport.ParseJsonObject(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Invalid {FileName}: {ex.Message}", ex);
        }

        if (ScannerSupport.GetInt(document, "schemaVersion") != 1 || ScannerSupport.GetDictionary(document, "tools") is not { } tools)
        {
            throw new InvalidOperationException($"Invalid {FileName}: expected schemaVersion 1 and a tools object.");
        }

        var result = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var (tool, value) in tools)
        {
            if (value is not IDictionary<string, object?> entries)
            {
                throw new InvalidOperationException($"Invalid {FileName}: tools.{tool} must be an object.");
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (fingerprint, count) in entries)
            {
                var n = count switch { long l => l, int i => i, double d => (long)d, _ => 0 };
                if (n < 1)
                {
                    throw new InvalidOperationException($"Invalid {FileName}: tools.{tool}.{fingerprint} must be a positive count.");
                }

                counts[fingerprint] = (int)n;
            }

            result[tool] = counts;
        }

        return result;
    }

    /// <summary>SHA-256 (hex) over <paramref name="parts"/>, unit-separator joined.</summary>
    public static string Fingerprint(params string?[] parts)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts.Select(p => p ?? string.Empty)))));

    /// <summary>
    /// How many of <paramref name="fingerprints"/> are not covered by <paramref name="known"/>
    /// (multiset: a fingerprint known twice covers two occurrences).
    /// </summary>
    public static int CountNew(IReadOnlyList<string> fingerprints, IReadOnlyDictionary<string, int>? known)
    {
        if (known is null)
        {
            return fingerprints.Count;
        }

        var remaining = new Dictionary<string, int>(known, StringComparer.Ordinal);
        var added = 0;
        foreach (var fingerprint in fingerprints)
        {
            if (remaining.TryGetValue(fingerprint, out var left) && left > 0)
            {
                remaining[fingerprint] = left - 1;
            }
            else
            {
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// Replaces the baseline entry of every tool in <paramref name="results"/> whose results all
    /// carry fingerprints (i.e. the scanner got far enough to list its findings); other tools keep
    /// their previous entry. Returns one note per tool, for the console.
    /// </summary>
    public static IReadOnlyList<string> Update(string securityDirectory, IReadOnlyList<ScanResult> results)
    {
        var path = Path.Combine(securityDirectory, FileName);
        var existing = Load(securityDirectory);
        var tools = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (existing is not null)
        {
            foreach (var (tool, counts) in existing)
            {
                tools[tool] = counts.ToDictionary(e => e.Key, e => (object?)(long)e.Value, StringComparer.Ordinal);
            }
        }

        var notes = new List<string>();
        foreach (var group in results.GroupBy(r => r.Tool, StringComparer.Ordinal))
        {
            if (group.Any(r => r.Fingerprints is null))
            {
                if (group.Any(r => r.Status == ScanStatus.Failed))
                {
                    notes.Add($"Baseline: {group.Key} not updated (the run did not list its findings); the previous entry stays.");
                }

                continue;
            }

            var counts = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var fingerprint in group.SelectMany(r => r.Fingerprints!))
            {
                counts[fingerprint] = (counts.TryGetValue(fingerprint, out var c) ? (long)c! : 0L) + 1;
            }

            tools[group.Key] = counts;
            notes.Add($"Baseline: {group.Key} → {counts.Values.Sum(v => (long)v!)} accepted finding(s).");
        }

        Directory.CreateDirectory(securityDirectory);
        ToolkitJson.WriteFile(path, new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1L,
            ["tools"] = tools,
        });

        return notes;
    }
}
