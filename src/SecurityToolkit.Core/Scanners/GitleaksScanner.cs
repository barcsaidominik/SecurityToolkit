namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// Gitleaks secret scan via the pinned Docker image. Supports working-tree ("dir"), full-history
/// (--history), and staged/pre-commit (--staged) modes. Defense in depth: the Secret/Match/Line
/// fields are always redacted before the report is written to disk, even though Gitleaks is
/// invoked with --redact=100 — this redaction is a mandatory security property, never optional.
/// </summary>
public sealed class GitleaksScanner : IScanner
{
    private static readonly string[] SensitiveFields = { "Secret", "Match", "Line" };

    private readonly IProcessRunner _processRunner;
    private readonly IReadOnlyList<string> _dockerMountArguments;

    public GitleaksScanner(IProcessRunner processRunner, IReadOnlyList<string> dockerMountArguments)
    {
        _processRunner = processRunner;
        _dockerMountArguments = dockerMountArguments;
    }

    public string Name => "gitleaks";

    public async Task<IReadOnlyList<ScanResult>> RunAsync(ScanContext context, CancellationToken cancellationToken = default)
    {
        var enabled = ScannerSupport.IsToolEnabled(context, "gitleaks");
        var secretsOnly = context.Staged;

        if (!enabled)
        {
            if (secretsOnly)
            {
                return new[]
                {
                    new ScanResult("gitleaks", "repository", ScanStatus.Failed, null, Blocking: false,
                        "Secret scan requested but Gitleaks is disabled."),
                };
            }

            return Array.Empty<ScanResult>();
        }

        try
        {
            var image = ScannerSupport.RequireImage(context.Lock, "gitleaks");

            if (context.History || context.Staged)
            {
                var checkResult = await ScannerSupport.RunNativeAsync(
                    _processRunner, context, "git", new[] { "rev-parse", "--is-inside-work-tree" }, "git-check.log",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (checkResult.StdOut.Trim() != "true")
                {
                    throw new InvalidOperationException("History/staged scan requires a git worktree.");
                }
            }

            var scanMode = context.History || context.Staged ? "git" : "dir";
            var arguments = new List<string> { "run" };
            arguments.AddRange(_dockerMountArguments);
            arguments.AddRange(new[]
            {
                image, scanMode, ".", "--config", ".security/gitleaks.toml", "--redact=100", "--exit-code", "10",
                "--report-format", "json", "--report-path", $"{context.ContainerOutputDirectory}/gitleaks.json",
            });
            if (context.Staged)
            {
                arguments.AddRange(new[] { "--pre-commit", "--staged" });
            }

            await ScannerSupport.RunNativeAsync(
                _processRunner, context, "docker", arguments, "gitleaks.log",
                allowedExitCodes: new[] { 0, 10 }, cancellationToken: cancellationToken).ConfigureAwait(false);

            var reportPath = Path.Combine(context.OutputDirectory, "gitleaks.json");
            if (!File.Exists(reportPath))
            {
                throw new FileNotFoundException($"Missing report: {reportPath}", reportPath);
            }

            var fingerprints = Fingerprints(reportPath);
            var redacted = RedactReport(reportPath);
            ToolkitJson.WriteFile(reportPath, redacted);

            var sarifPath = Path.Combine(context.OutputDirectory, "gitleaks.sarif");
            ToolkitJson.WriteFile(sarifPath, BuildSarif(redacted));

            // --staged (the pre-commit hook) blocks in every mode: the hook runs without --mode.
            var blocking = (context.Mode == ScanMode.Gate || context.Staged) && redacted.Count > 0;
            return new[] { new ScanResult("gitleaks", scanMode, ScanStatus.Completed, redacted.Count, blocking, Fingerprints: fingerprints) };
        }
        catch (Exception ex)
        {
            return new[] { new ScanResult("gitleaks", "repository", ScanStatus.Failed, null, Blocking: false, ex.Message) };
        }
    }

    /// <summary>
    /// One baseline fingerprint per hit: rule + file + commit (empty outside --history) + Gitleaks'
    /// own Match, which --redact=100 has already stripped of the secret value (e.g.
    /// <c>"ApiKey": "REDACTED"</c>), so the fingerprint is computed without the raw secret ever
    /// being on disk. The trade-off: a different secret written to exactly the same key in the
    /// same file counts as the known finding.
    /// </summary>
    private static List<string> Fingerprints(string path)
    {
        var fingerprints = new List<string>();
        if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is not System.Text.Json.Nodes.JsonArray array)
        {
            return fingerprints;
        }

        foreach (var item in array)
        {
            if (item is not System.Text.Json.Nodes.JsonObject hit)
            {
                continue;
            }

            var file = (hit["File"]?.ToString() ?? string.Empty).Replace('\\', '/');
            if (file.StartsWith("./", StringComparison.Ordinal))
            {
                file = file[2..];
            }

            fingerprints.Add(FindingBaseline.Fingerprint(
                "gitleaks", hit["RuleID"]?.ToString(), file, hit["Commit"]?.ToString(), hit["Match"]?.ToString()?.Trim()));
        }

        return fingerprints;
    }

    /// <summary>
    /// Gitleaks' JSON report is a top-level array of hit objects (not an object like the other
    /// scanners' reports). Only the Secret/Match/Line fields are overwritten (with a redacted
    /// string); every other field keeps its original JSON node/type untouched, so numeric fields
    /// like StartLine/EndLine are never coerced to strings.
    /// </summary>
    private static System.Text.Json.Nodes.JsonArray RedactReport(string path)
    {
        var json = File.ReadAllText(path);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        var result = new System.Text.Json.Nodes.JsonArray();

        if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not System.Text.Json.Nodes.JsonObject obj)
                {
                    result.Add(item?.DeepClone());
                    continue;
                }

                var hit = (System.Text.Json.Nodes.JsonObject)obj.DeepClone();
                foreach (var field in SensitiveFields)
                {
                    if (hit.ContainsKey(field))
                    {
                        hit[field] = System.Text.Json.Nodes.JsonValue.Create("[REDACTED]");
                    }
                }

                result.Add(hit);
            }
        }

        return result;
    }

    /// <summary>
    /// Hand-builds a minimal SARIF 2.1.0 document from the already-redacted Gitleaks findings, for
    /// display in the Azure DevOps "SARIF SAST Scans Tab" extension. Never derived from Gitleaks'
    /// own --sarif output: that would bypass <see cref="RedactReport"/> and leak raw secret values
    /// into a report intended for a shared/public viewer. Every text field funnels through the
    /// already-redacted <paramref name="redacted"/> array, so "[REDACTED]" is what a viewer sees,
    /// never the original Secret/Match/Line values.
    /// </summary>
    private static Dictionary<string, object?> BuildSarif(System.Text.Json.Nodes.JsonArray redacted)
    {
        var results = new List<object?>();

        foreach (var item in redacted)
        {
            if (item is not System.Text.Json.Nodes.JsonObject hit)
            {
                continue;
            }

            var ruleId = hit["RuleID"]?.ToString() ?? hit["Description"]?.ToString() ?? "gitleaks-secret";
            var description = hit["Description"]?.ToString() ?? "Potential secret detected";
            var file = hit["File"]?.ToString() ?? "unknown";
            var startLine = TryGetInt(hit["StartLine"]) ?? 1;

            results.Add(new Dictionary<string, object?>
            {
                ["ruleId"] = ruleId,
                ["level"] = "error",
                ["message"] = new Dictionary<string, object?> { ["text"] = description },
                ["locations"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["physicalLocation"] = new Dictionary<string, object?>
                        {
                            ["artifactLocation"] = new Dictionary<string, object?> { ["uri"] = file },
                            ["region"] = new Dictionary<string, object?> { ["startLine"] = startLine },
                        },
                    },
                },
            });
        }

        return new Dictionary<string, object?>
        {
            ["$schema"] = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["tool"] = new Dictionary<string, object?>
                    {
                        ["driver"] = new Dictionary<string, object?>
                        {
                            ["name"] = "gitleaks",
                            ["informationUri"] = "https://github.com/gitleaks/gitleaks",
                            ["rules"] = Array.Empty<object?>(),
                        },
                    },
                    ["results"] = results,
                },
            },
        };
    }

    private static int? TryGetInt(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<System.Text.Json.JsonElement>().TryGetInt32(out var value) ? value : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
