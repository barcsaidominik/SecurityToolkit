namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// Coordinates a full scan run: runs the applicable scanners in their fixed order, sanitizes accidental token echoing from every
/// report/log, writes status.json + latest-status.json, and computes the exit code per the
/// documented contract:
///   0 = the scanners that ran completed successfully in the chosen mode;
///   1 = Gate mode blocking finding;
///   2 = installer/scanner/integration (technical) failure.
/// --staged always runs Gitleaks only and is always blocking, regardless of --mode.
/// </summary>
public sealed class ScanOrchestrator
{
    private readonly IProcessRunner _processRunner;
    private readonly IReadOnlyList<IScanner> _scanners;

    /// <param name="processRunner">Used only for the final "git rev-parse HEAD" commit lookup; individual scanners bring their own.</param>
    /// <param name="scanners">
    /// The full set of scanners to consider, in the order they should run. Callers construct
    /// these (e.g. AnalyzersScanner, NuGetScanner, NpmScanner, SemgrepScanner, GitleaksScanner,
    /// SonarScanner) with whatever IProcessRunner/docker-mount-arguments they need.
    /// </param>
    public ScanOrchestrator(IProcessRunner processRunner, IReadOnlyList<IScanner> scanners)
    {
        _processRunner = processRunner;
        _scanners = scanners;
    }

    /// <summary>Runs every applicable scanner and returns the aggregated outcome (exit code + all results).</summary>
    public async Task<ScanOutcome> RunAsync(ScanContext context, CancellationToken cancellationToken = default)
    {
        if (context.Staged && context.History)
        {
            throw new InvalidOperationException("Staged and History cannot be combined.");
        }

        Directory.CreateDirectory(context.OutputDirectory);

        var results = new List<ScanResult>();
        var secretsOnly = context.Staged;

        foreach (var scanner in _scanners)
        {
            // --staged (and --secrets-only generally) runs Gitleaks only; every other scanner is
            // skipped entirely rather than reported as not-run.
            if (secretsOnly && scanner.Name != "gitleaks")
            {
                continue;
            }

            var scannerResults = await scanner.RunAsync(context, cancellationToken).ConfigureAwait(false);
            results.AddRange(scannerResults);
        }

        SanitizeTokens(context, new[] { context.SonarToken });

        var commit = await TryGetCommitAsync(context, cancellationToken).ConfigureAwait(false);

        var statusPath = Path.Combine(context.OutputDirectory, "status.json");
        var mergedResults = MergeWithExistingStatus(statusPath, results);

        var statusDocument = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["time"] = DateTime.UtcNow.ToString("o"),
            ["commit"] = commit,
            ["mode"] = context.Mode == ScanMode.Gate ? "Gate" : "Report",
            ["semgrepScope"] = context.SemgrepScope == SemgrepScope.Frontend ? "frontend" : "repository",
            ["tools"] = mergedResults.Select(ResultToDictionary).ToList(),
            ["lock"] = context.Lock,
        };

        ToolkitJson.WriteFile(statusPath, statusDocument);

        var latestStatusPath = Path.Combine(context.ToolkitDirectory, "results", "latest-status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(latestStatusPath)!);
        File.Copy(statusPath, latestStatusPath, overwrite: true);

        var summaryMarkdown = ScanReportFormatter.BuildMarkdown(statusDocument, context.OutputDirectory);
        File.WriteAllText(Path.Combine(context.OutputDirectory, "summary.md"), summaryMarkdown);

        // Exit code from THIS invocation's results only: with a shared --run-id every pipeline step
        // merges into one status.json, and judging by the merged list would turn every later
        // step red (or Gate-block it) for an earlier step's failure. The merged file still
        // reports everything.
        var failed = results.Any(r => r.Status == ScanStatus.Failed);
        var blocked = results.Any(r => r.Blocking);

        int exitCode;
        if (failed)
        {
            exitCode = 2;
        }
        else if ((context.Mode == ScanMode.Gate || context.Staged) && blocked)
        {
            exitCode = 1;
        }
        else
        {
            exitCode = 0;
        }

        return new ScanOutcome(exitCode, mergedResults, statusPath);
    }

    /// <summary>
    /// Merges this run's results into any status.json already present at <paramref name="statusPath"/>
    /// (from a prior --tool invocation sharing the same --run-id), replacing entries by tool name
    /// rather than duplicating them, so a pipeline can call `scan --tool X` once per scanner into one
    /// shared results directory and still end up with a single, complete status.json.
    /// </summary>
    private static List<ScanResult> MergeWithExistingStatus(string statusPath, IReadOnlyList<ScanResult> newResults)
    {
        if (!File.Exists(statusPath))
        {
            return newResults.ToList();
        }

        var existingDocument = ScannerSupport.ParseJsonObject(File.ReadAllText(statusPath));
        var existingTools = ScannerSupport.GetList(existingDocument, "tools");
        var newToolNames = newResults.Select(r => r.Tool).ToHashSet(StringComparer.Ordinal);

        var merged = new List<ScanResult>();
        foreach (var toolObj in existingTools)
        {
            if (toolObj is IDictionary<string, object?> tool)
            {
                var name = ScannerSupport.GetString(tool, "tool");
                if (name is not null && !newToolNames.Contains(name))
                {
                    merged.Add(DictionaryToResult(tool));
                }
            }
        }

        merged.AddRange(newResults);
        return merged;
    }

    private static ScanResult DictionaryToResult(IDictionary<string, object?> tool)
    {
        var status = ScannerSupport.GetString(tool, "status") switch
        {
            "completed" => ScanStatus.Completed,
            "failed" => ScanStatus.Failed,
            "not-run" => ScanStatus.NotRun,
            _ => ScanStatus.NotRun,
        };

        var hasFindings = tool.TryGetValue("findings", out var findingsValue) && findingsValue is not null;
        var findings = hasFindings ? ScannerSupport.GetInt(tool, "findings") : (int?)null;

        return new ScanResult(
            ScannerSupport.GetString(tool, "tool") ?? string.Empty,
            ScannerSupport.GetString(tool, "target") ?? string.Empty,
            status,
            findings,
            ScannerSupport.GetBool(tool, "blocking"),
            ScannerSupport.GetString(tool, "message") ?? string.Empty);
    }

    private static Dictionary<string, object?> ResultToDictionary(ScanResult result) => new()
    {
        ["tool"] = result.Tool,
        ["target"] = result.Target,
        ["status"] = result.Status switch
        {
            ScanStatus.Completed => "completed",
            ScanStatus.Failed => "failed",
            ScanStatus.NotRun => "not-run",
            _ => "unknown",
        },
        ["findings"] = result.Findings,
        ["blocking"] = result.Blocking,
        ["message"] = result.Message,
    };

    /// <summary>
    /// Removes any accidental verbatim occurrence of SONAR_TOKEN from every file in the
    /// output directory before it's published.
    /// </summary>
    private static void SanitizeTokens(ScanContext context, IReadOnlyList<string?> secrets)
    {
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(context.OutputDirectory))
            {
                var text = File.ReadAllText(file);
                if (text.Contains(secret, StringComparison.Ordinal))
                {
                    File.WriteAllText(file, text.Replace(secret, "[REDACTED]", StringComparison.Ordinal));
                }
            }
        }
    }

    private async Task<string> TryGetCommitAsync(ScanContext context, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(
                "git", new[] { "rev-parse", "HEAD" }, context.Root, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode == 0 ? result.StdOut.Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>Aggregated outcome of a full <see cref="ScanOrchestrator.RunAsync"/> run.</summary>
/// <param name="ExitCode">0 success, 1 Gate-mode block, 2 technical failure.</param>
/// <param name="Results">Every ScanResult produced across all scanners that ran.</param>
/// <param name="StatusFilePath">Path to the run's own status.json.</param>
public sealed record ScanOutcome(int ExitCode, IReadOnlyList<ScanResult> Results, string StatusFilePath);
