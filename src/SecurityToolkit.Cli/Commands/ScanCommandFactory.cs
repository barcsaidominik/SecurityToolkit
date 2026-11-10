using SecurityToolkit.Core;
using SecurityToolkit.Core.Scanners;
using System.CommandLine;

namespace SecurityToolkit.Cli.Commands;

/// <summary>
/// Builds the "scan" command, which wires the configured scanners into a
/// <see cref="ScanOrchestrator"/> run.
/// </summary>
public static class ScanCommandFactory
{
    public static Command Create()
    {
        var projectRootOption = new Option<string>("--project-root")
        {
            Description = "Repository root containing .security/.",
            Required = true,
        };
        var modeOption = new Option<string>("--mode")
        {
            Description = "Report (default; never blocks on findings) or Gate (blocking thresholds; with .security/baseline.json only findings not in the baseline block).",
            DefaultValueFactory = _ => "Report",
        };
        modeOption.AcceptOnlyFromAmong("Report", "Gate");

        var historyOption = new Option<bool>("--history")
        {
            Description = "Gitleaks scans reachable git history instead of the working tree.",
        };
        var stagedOption = new Option<bool>("--staged")
        {
            Description = "Gitleaks scans the git index (pre-commit) instead of the working tree. Always blocking; runs Gitleaks only.",
        };
        var secretsOnlyOption = new Option<bool>("--secrets-only")
        {
            Description = "Run Gitleaks only (same effect as --staged on scanner selection, without requiring the git index).",
        };
        var includeSonarOption = new Option<bool>("--include-sonar")
        {
            Description = "Opt in to the Sonar server upload step.",
        };
        var sonarOnlyOption = new Option<bool>("--sonar-only")
        {
            Description = "Run only the Sonar upload step (implies --include-sonar).",
        };
        var configFileOption = new Option<string?>("--config-file")
        {
            Description = "Override path to security.config.json (defaults to .security/security.config.json under --project-root).",
        };
        var semgrepScopeOption = new Option<string?>("--semgrep-scope")
        {
            Description = "One-time override of the persisted Semgrep scope: 'repository' or 'frontend'.",
        };
        semgrepScopeOption.AcceptOnlyFromAmong("repository", "frontend");

        var toolOption = new Option<string?>("--tool")
        {
            Description = "Run only the named scanner instead of every configured tool.",
        };
        toolOption.AcceptOnlyFromAmong(
            "analyzers", "nuget", "npm", "semgrep", "gitleaks", "osv", "devskim", "sonar");

        var runIdOption = new Option<string?>("--run-id")
        {
            Description = "Use this run id (and its existing results/<run-id>/status.json, if any) " +
                "instead of generating a new timestamp-based one. Lets multiple --tool invocations " +
                "share one results directory and merge into a single status.json.",
        };

        var updateBaselineOption = new Option<bool>("--update-baseline")
        {
            Description = "Run in Report mode and record the current blocking findings of the scanners that ran " +
                "as accepted in .security/baseline.json (per tool; combine with --tool to update one).",
        };

        var command = new Command("scan", "Run the configured security scanners.")
        {
            projectRootOption, modeOption, historyOption, stagedOption, secretsOnlyOption,
            includeSonarOption, sonarOnlyOption, configFileOption, semgrepScopeOption,
            toolOption, runIdOption, updateBaselineOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // Checked before anything is read: a baseline update always runs in Report mode, and the
            // staged hook must never rewrite what the team accepted.
            var updateBaseline = parseResult.GetValue(updateBaselineOption);
            if (updateBaseline
                && (parseResult.GetValue(stagedOption) || parseResult.GetValue(secretsOnlyOption)
                    || string.Equals(parseResult.GetValue(modeOption), "Gate", StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine("--update-baseline cannot be combined with --staged/--secrets-only or --mode Gate.");
                return 2;
            }

            var root = Path.GetFullPath(parseResult.GetValue(projectRootOption)!);
            var securityDirectory = Path.Combine(root, ".security");
            var configPath = parseResult.GetValue(configFileOption)
                ?? Path.Combine(securityDirectory, SecurityToolkit.Core.Installation.SecurityToolkitInstaller.ConfigFileName);

            var config = ScannerSupport.ParseJsonObject(await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false));
            ScannerSupport.MergeLocalOverride(config, securityDirectory);
            var lockData = ToolkitJson.ReadLock(securityDirectory);

            var mode = string.Equals(parseResult.GetValue(modeOption), "Gate", StringComparison.OrdinalIgnoreCase)
                ? ScanMode.Gate
                : ScanMode.Report;

            var scopeOverride = parseResult.GetValue(semgrepScopeOption);
            var configuredScope = ScannerSupport.GetString(config, "semgrepScope");
            var semgrepScope = (scopeOverride ?? configuredScope) == "frontend" ? SemgrepScope.Frontend : SemgrepScope.Repository;

            var staged = parseResult.GetValue(stagedOption) || parseResult.GetValue(secretsOnlyOption);

            var history = parseResult.GetValue(historyOption);
            var includeSonar = parseResult.GetValue(includeSonarOption) || parseResult.GetValue(sonarOnlyOption);

            var runId = parseResult.GetValue(runIdOption) ?? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            var outputDirectory = Path.Combine(securityDirectory, "results", runId);
            var containerOutputDirectory = $".security/results/{runId}";

            var context = new ScanContext(
                Root: root,
                ToolkitDirectory: securityDirectory,
                OutputDirectory: outputDirectory,
                ContainerOutputDirectory: containerOutputDirectory,
                Config: config,
                Lock: lockData,
                Mode: mode,
                SemgrepScope: semgrepScope,
                History: history,
                Staged: staged,
                IncludeSonar: includeSonar,
                SonarToken: Environment.GetEnvironmentVariable("SONAR_TOKEN"),
                UpdateBaseline: updateBaseline);

            var processRunner = new ProcessRunner();
            var dockerMountArguments = DockerRunArguments.Build(root);
            using var httpClient = new HttpClient();

            var sonarOnly = parseResult.GetValue(sonarOnlyOption);
            var toolFilter = sonarOnly ? "sonar" : parseResult.GetValue(toolOption);

            IReadOnlyList<IScanner> allScanners = new IScanner[]
            {
                new AnalyzersScanner(processRunner),
                new NuGetScanner(processRunner),
                new NpmScanner(processRunner),
                new SemgrepScanner(processRunner, dockerMountArguments),
                new GitleaksScanner(processRunner, dockerMountArguments),
                new OsvScanner(processRunner, dockerMountArguments),
                new DevSkimScanner(processRunner),
                new SonarScanner(processRunner, httpClient),
            };

            var scanners = toolFilter is null
                ? allScanners
                : allScanners.Where(s => s.Name == toolFilter).ToArray();

            var orchestrator = new ScanOrchestrator(processRunner, scanners);
            var outcome = await orchestrator.RunAsync(context, cancellationToken).ConfigureAwait(false);

            foreach (var result in outcome.Results)
            {
                Console.WriteLine($"{result.Tool} [{result.Target}]: {result.Status} findings={result.Findings?.ToString() ?? "n/a"} blocking={result.Blocking}"
                    + (string.IsNullOrEmpty(result.Message) ? string.Empty : $" - {result.Message}"));
            }

            foreach (var note in outcome.Notes ?? Array.Empty<string>())
            {
                Console.WriteLine(note);
            }

            Console.WriteLine($"Riportok: {outcome.StatusFilePath}");
            return outcome.ExitCode;
        });

        return command;
    }
}
