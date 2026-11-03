using SecurityToolkit.Core.Installation;

namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// Shared helpers used by multiple scanners (process run with log, report reading, pinned image
/// lookup) plus small, forgiving config-dictionary accessors over the plain
/// IDictionary&lt;string, object?&gt; tree <see cref="ToolkitJson"/> produces.
/// </summary>
public static class ScannerSupport
{
    /// <summary>
    /// Runs a process and throws if its exit code isn't in <paramref name="allowedExitCodes"/>,
    /// Always writes the combined stdout/stderr log relative to
    /// <paramref name="context"/>'s output directory.
    /// </summary>
    public static async Task<ProcessResult> RunNativeAsync(
        IProcessRunner processRunner,
        ScanContext context,
        string fileName,
        IReadOnlyList<string> arguments,
        string logFileName,
        int[]? allowedExitCodes = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        allowedExitCodes ??= new[] { 0 };
        var logPath = Path.Combine(context.OutputDirectory, logFileName);
        var result = await processRunner.RunAsync(
            fileName,
            arguments,
            workingDirectory ?? context.Root,
            logPath,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!allowedExitCodes.Contains(result.ExitCode))
        {
            throw new InvalidOperationException($"Exit {result.ExitCode}; see {logFileName}");
        }

        return result;
    }

    /// <summary>
    /// MSBuild switch appended to every scanner-driven <c>dotnet restore</c>/<c>dotnet build</c>:
    /// worker nodes exit with the build instead of lingering for reuse. Each <c>scan --tool</c>
    /// pipeline step is a separate CLI process, so reusable nodes never get reused — they only
    /// held memory through all later steps and were killed as orphans at job end.
    /// </summary>
    public const string NoNodeReuseSwitch = "-nodeReuse:false";

    /// <summary>
    /// Best-effort <c>dotnet build-server shutdown</c> after a scanner's builds, so the Roslyn
    /// compiler server (VBCSCompiler), Razor server and any MSBuild nodes don't outlive the scan.
    /// Never throws: a failed shutdown must not turn a completed scan into a failed one.
    /// </summary>
    public static async Task ShutdownBuildServersAsync(
        IProcessRunner processRunner, ScanContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await RunNativeAsync(
                processRunner, context, "dotnet", new[] { "build-server", "shutdown" }, "build-server-shutdown.log",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Intentionally ignored — see summary.
        }
    }

    /// <summary>
    /// Reads and parses a JSON report file from disk as a nested dictionary tree. Throws if the file does not exist.
    /// </summary>
    public static IDictionary<string, object?> ReadReport(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Missing report: {path}", path);
        }

        return ParseJsonObject(File.ReadAllText(path));
    }

    /// <summary>
    /// Parses arbitrary JSON text into a nested dictionary/list/scalar tree (the same shape
    /// <see cref="ToolkitJson.ReadLock"/> produces), for report bodies that aren't security.lock.json.
    /// </summary>
    public static IDictionary<string, object?> ParseJsonObject(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        return ToolkitJsonNodeToHashtable(node) as IDictionary<string, object?>
            ?? throw new InvalidOperationException("Expected a JSON object at the document root.");
    }

    private static object? ToolkitJsonNodeToHashtable(System.Text.Json.Nodes.JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case System.Text.Json.Nodes.JsonObject obj:
                {
                    var dictionary = new Dictionary<string, object?>();
                    foreach (var kvp in obj)
                    {
                        dictionary[kvp.Key] = ToolkitJsonNodeToHashtable(kvp.Value);
                    }
                    return dictionary;
                }
            case System.Text.Json.Nodes.JsonArray array:
                return array.Select(ToolkitJsonNodeToHashtable).ToList();
            case System.Text.Json.Nodes.JsonValue value:
                if (value.TryGetValue<long>(out var longValue)) return longValue;
                if (value.TryGetValue<double>(out var doubleValue)) return doubleValue;
                if (value.TryGetValue<bool>(out var boolValue)) return boolValue;
                if (value.TryGetValue<string>(out var stringValue)) return stringValue;
                return value.ToString();
            default:
                return node.ToString();
        }
    }

    /// <summary>Looks up a required pinned image digest from the parsed lock file.</summary>
    /// <exception cref="InvalidOperationException">The tool isn't marked ready, or its image isn't digest-pinned.</exception>
    public static string RequireImage(IDictionary<string, object?> lockData, string toolName)
    {
        var tools = GetDictionary(lockData, "tools")
            ?? throw new InvalidOperationException($"Initialize pinned {toolName} image first.");
        var tool = GetDictionary(tools, toolName)
            ?? throw new InvalidOperationException($"Initialize pinned {toolName} image first.");

        var ready = GetBool(tool, "ready");
        var image = GetString(tool, "image");

        if (!ready || image is null || !System.Text.RegularExpressions.Regex.IsMatch(image, "@sha256:[a-f0-9]{64}$"))
        {
            throw new InvalidOperationException($"Initialize pinned {toolName} image first.");
        }

        return image;
    }

    public static IDictionary<string, object?>? GetDictionary(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) ? value as IDictionary<string, object?> : null;

    public static IReadOnlyList<object?> GetList(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) && value is IEnumerable<object?> list
            ? list.ToList()
            : Array.Empty<object?>();

    public static string? GetString(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) ? value as string : null;

    public static bool GetBool(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) && value is bool boolValue && boolValue;

    public static int GetInt(IDictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value))
        {
            return 0;
        }

        return value switch
        {
            long longValue => (int)longValue,
            int intValue => intValue,
            double doubleValue => (int)doubleValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => 0,
        };
    }

    /// <summary>
    /// True when security.config.json's tools.&lt;name&gt; entry is enabled.
    /// </summary>
    public static bool IsToolEnabled(ScanContext context, string toolName)
    {
        var tools = GetDictionary(context.Config, "tools");
        return tools is not null && GetBool(tools, toolName);
    }

    public static IReadOnlyList<string> GetConfigTargets(ScanContext context, string key)
        => GetList(context.Config, key).Select(v => v as string ?? string.Empty).Where(v => v.Length > 0).ToList();

    /// <summary>
    /// Deep-merges "security.config.local.json" (an untracked, per-machine override — e.g. a
    /// developer's local Sonar URL/port while the committed security.config.json points at the
    /// team's shared server) over <paramref name="config"/> in place. Unlike the installer's own
    /// config merge, this accepts any key, including ones not already present in
    /// <paramref name="config"/>, since a local override is expected to diverge freely from the
    /// committed baseline. A missing "security.config.local.json" is not an error.
    /// </summary>
    public static void MergeLocalOverride(IDictionary<string, object?> config, string securityDirectory)
    {
        var overridePath = Path.Combine(securityDirectory, SecurityToolkitInstaller.LocalConfigFileName);
        if (!File.Exists(overridePath))
        {
            return;
        }

        var overrideConfig = ParseJsonObject(File.ReadAllText(overridePath));
        MergeInto(config, overrideConfig);
    }

    /// <summary>
    /// Deep-merges <paramref name="source"/> over <paramref name="target"/> in place: nested
    /// dictionaries merge key-by-key (recursively), every other value type is a straight
    /// overwrite. Accepts any key, including ones not already present in <paramref name="target"/>
    /// — the permissive merge <see cref="MergeLocalOverride"/> uses, exposed for callers (like an
    /// install-time local override) that need the same "local wins, no unknown-key restriction"
    /// semantics outside a scan.
    /// </summary>
    public static void MergeInto(IDictionary<string, object?> target, IDictionary<string, object?> source)
    {
        foreach (var (key, value) in source)
        {
            if (target.TryGetValue(key, out var existing) &&
                existing is IDictionary<string, object?> existingNested &&
                value is IDictionary<string, object?> valueNested)
            {
                MergeInto(existingNested, valueNested);
            }
            else
            {
                target[key] = value;
            }
        }
    }
}
