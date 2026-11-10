namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// Semgrep CE scan via the pinned Docker image. Supports the "repository" (everything, including C#) and "frontend" (configured
/// frontend targets only, *.cs excluded) scopes. Strict-mode parse errors (exit 3) can coexist
/// with useful findings — those are preserved and a semgrep-coverage.json sidecar is written,
/// but the overall result stays Failed (incomplete analysis) in both Report and Gate mode; ERROR
/// severity is scanner rule severity, not a universal High/Critical label, and only blocks Gate
/// mode when at least one ERROR-severity hit exists.
/// </summary>
public sealed class SemgrepScanner : IScanner
{
    private readonly IProcessRunner _processRunner;
    private readonly IReadOnlyList<string> _dockerMountArguments;

    public SemgrepScanner(IProcessRunner processRunner, IReadOnlyList<string> dockerMountArguments)
    {
        _processRunner = processRunner;
        _dockerMountArguments = dockerMountArguments;
    }

    public string Name => "semgrep";

    public async Task<IReadOnlyList<ScanResult>> RunAsync(ScanContext context, CancellationToken cancellationToken = default)
    {
        if (!ScannerSupport.IsToolEnabled(context, "semgrep"))
        {
            return Array.Empty<ScanResult>();
        }

        try
        {
            var image = ScannerSupport.RequireImage(context.Lock, "semgrep");

            var targets = new List<string> { "." };
            var scopeArguments = new List<string>();

            if (context.SemgrepScope == SemgrepScope.Frontend)
            {
                var frontendTargets = ScannerSupport.GetConfigTargets(context, "frontendTargets");
                if (frontendTargets.Count == 0)
                {
                    throw new InvalidOperationException("Frontend Semgrep scope requires selected frontend targets.");
                }

                foreach (var target in frontendTargets)
                {
                    ToolkitPaths.GetToolkitPath(context.Root, target);
                }

                targets = frontendTargets.ToList();
                scopeArguments.AddRange(new[] { "--exclude", "*.cs" });
            }

            var arguments = new List<string> { "run" };
            arguments.AddRange(_dockerMountArguments);
            arguments.AddRange(new[]
            {
                image, "semgrep", "scan", "--config", ".security/rules", "--metrics=off", "--disable-version-check",
                "--strict",
                "--json-output", $"{context.ContainerOutputDirectory}/semgrep.json",
                "--sarif-output", $"{context.ContainerOutputDirectory}/semgrep.sarif",
            });
            arguments.AddRange(scopeArguments);
            arguments.AddRange(targets);

            // Strict parsing errors (exit 3) can coexist with useful findings. Preserve those
            // findings, but keep incomplete analysis a failure in both modes.
            var runResult = await ScannerSupport.RunNativeAsync(
                _processRunner, context, "docker", arguments, "semgrep.log",
                allowedExitCodes: new[] { 0, 3 }, cancellationToken: cancellationToken).ConfigureAwait(false);

            var data = ScannerSupport.ReadReport(Path.Combine(context.OutputDirectory, "semgrep.json"));
            if (!data.ContainsKey("results") || !data.ContainsKey("errors"))
            {
                throw new InvalidOperationException("Invalid Semgrep JSON schema.");
            }

            var hits = ScannerSupport.GetList(data, "results");
            var errorHits = hits.Count(hit => hit is IDictionary<string, object?> h &&
                ScannerSupport.GetDictionary(h, "extra") is { } extra &&
                ScannerSupport.GetString(extra, "severity") == "ERROR");
            var scanErrors = ScannerSupport.GetList(data, "errors");
            var fingerprints = ErrorFingerprints(context.Root, hits);

            if (scanErrors.Count > 0 || runResult.ExitCode == 3)
            {
                var paths = scanErrors
                    .Select(e => e is IDictionary<string, object?> d ? ScannerSupport.GetString(d, "path") : null)
                    .Where(p => p is not null)
                    .Select(p => p!)
                    .Distinct()
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToArray();

                ToolkitJson.WriteFile(Path.Combine(context.OutputDirectory, "semgrep-coverage.json"), new Dictionary<string, object?>
                {
                    ["schemaVersion"] = 1,
                    ["complete"] = false,
                    ["exitCode"] = runResult.ExitCode,
                    ["analysisErrors"] = scanErrors.Count,
                    ["affectedFiles"] = paths,
                    ["findings"] = hits.Count,
                });

                var scopeLabel = context.SemgrepScope == SemgrepScope.Frontend ? "frontend" : "repository";
                var blocking = context.Mode == ScanMode.Gate && errorHits > 0;
                return new[]
                {
                    new ScanResult(
                        "semgrep", scopeLabel, ScanStatus.Failed, hits.Count, blocking,
                        $"Incomplete analysis: {scanErrors.Count} errors in {paths.Length} files; {hits.Count} findings preserved. " +
                        "Review semgrep.json and semgrep-coverage.json. Parser errors do not prove invalid application code.",
                        fingerprints),
                };
            }

            var completedScopeLabel = context.SemgrepScope == SemgrepScope.Frontend ? "frontend" : "repository";
            var completedBlocking = context.Mode == ScanMode.Gate && errorHits > 0;
            return new[]
            {
                new ScanResult(
                    "semgrep", completedScopeLabel, ScanStatus.Completed, hits.Count, completedBlocking,
                    "ERROR is rule severity; not a universal High/Critical risk label.",
                    fingerprints),
            };
        }
        catch (Exception ex)
        {
            return new[] { new ScanResult("semgrep", "repository", ScanStatus.Failed, null, Blocking: false, ex.Message) };
        }
    }

    /// <summary>
    /// One baseline fingerprint per ERROR-severity hit: rule + path + the matched source lines with
    /// surrounding whitespace trimmed, read from the working tree (Semgrep CE's own extra.lines and
    /// extra.fingerprint are "requires login" placeholders). Line numbers are deliberately left out,
    /// so code moving within the file keeps a known hit known.
    /// </summary>
    private static List<string> ErrorFingerprints(string root, IReadOnlyList<object?> hits)
    {
        var fingerprints = new List<string>();
        var fileCache = new Dictionary<string, string[]?>(StringComparer.Ordinal);

        foreach (var hit in hits)
        {
            if (hit is not IDictionary<string, object?> h
                || ScannerSupport.GetDictionary(h, "extra") is not { } extra
                || ScannerSupport.GetString(extra, "severity") != "ERROR")
            {
                continue;
            }

            var path = (ScannerSupport.GetString(h, "path") ?? string.Empty).Replace('\\', '/');
            var startLine = ScannerSupport.GetDictionary(h, "start") is { } start ? ScannerSupport.GetInt(start, "line") : 0;
            var endLine = ScannerSupport.GetDictionary(h, "end") is { } end ? ScannerSupport.GetInt(end, "line") : startLine;

            if (!fileCache.TryGetValue(path, out var lines))
            {
                lines = ReadLines(root, path);
                fileCache[path] = lines;
            }

            var code = lines is not null && startLine >= 1 && endLine >= startLine && endLine <= lines.Length
                ? string.Join('\n', lines[(startLine - 1)..endLine].Select(l => l.Trim()))
                : string.Empty;

            fingerprints.Add(FindingBaseline.Fingerprint("semgrep", ScannerSupport.GetString(h, "check_id"), path, code));
        }

        return fingerprints;
    }

    private static string[]? ReadLines(string root, string relativePath)
    {
        try
        {
            return relativePath.Length == 0 ? null : File.ReadAllLines(ToolkitPaths.GetToolkitPath(root, relativePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
