using SecurityToolkit.Core;
using SecurityToolkit.Core.Installation;
using SecurityToolkit.Core.Scanners;
using System.CommandLine;

namespace SecurityToolkit.Cli.Commands;

/// <summary>
/// Builds the "install" command on top of <see cref="SecurityToolkit.Core.Installation.SecurityToolkitInstaller"/>.
/// </summary>
public static class InstallCommandFactory
{
    public static Command Create()
    {
        var projectRootOption = new Option<string>("--project-root")
        {
            Description = "Target project/repository root directory.",
            Required = true,
        };
        var configFileOption = new Option<string?>("--config-file")
        {
            Description = "Optional JSON profile merged over defaults/existing toolkit.json.",
        };
        var nonInteractiveOption = new Option<bool>("--non-interactive")
        {
            Description = "Accept every default/current target selection instead of prompting on the console.",
        };
        var configureOnlyOption = new Option<bool>("--configure-only")
        {
            Description = "Generate/update project files only; skip prerequisite installation and tool provisioning.",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Print the resolved configuration; write nothing to disk.",
        };
        var runAfterInstallOption = new Option<bool>("--run-after-install")
        {
            Description = "Run a full Report-mode scan immediately after a successful install.",
        };
        var semgrepScopeOption = new Option<string?>("--semgrep-scope")
        {
            Description = "Override the persisted Semgrep scope for this run: 'repository' or 'frontend'.",
        };
        semgrepScopeOption.AcceptOnlyFromAmong("repository", "frontend");
        var checkOption = new Option<bool>("--check")
        {
            Description = "Report whether the project's toolkit-managed files (gitleaks.toml, pipeline YAML, " +
                          "ignore blocks, etc.) match what this install would generate; write nothing and " +
                          "never fail on manual edits. Exit code 0 = in sync, 1 = drift detected.",
        };

        var command = new Command("install", "Install/update the security toolkit into a project.")
        {
            projectRootOption, configFileOption, nonInteractiveOption,
            configureOnlyOption, dryRunOption, runAfterInstallOption, semgrepScopeOption, checkOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var configFile = parseResult.GetValue(configFileOption);
            var scopeText = parseResult.GetValue(semgrepScopeOption);

            var options = new InstallOptions(
                ProjectRoot: parseResult.GetValue(projectRootOption)!,
                ConfigFile: configFile,
                NonInteractive: parseResult.GetValue(nonInteractiveOption),
                ConfigureOnly: parseResult.GetValue(configureOnlyOption),
                DryRun: parseResult.GetValue(dryRunOption),
                RunAfterInstall: parseResult.GetValue(runAfterInstallOption),
                SemgrepScope: scopeText switch
                {
                    "repository" => SemgrepInstallScope.Repository,
                    "frontend" => SemgrepInstallScope.Frontend,
                    _ => SemgrepInstallScope.Unspecified,
                },
                CheckOnly: parseResult.GetValue(checkOption));

            IDictionary<string, object?>? configProfile = null;
            if (!string.IsNullOrEmpty(configFile))
            {
                configProfile = SecurityToolkit.Core.Scanners.ScannerSupport.ParseJsonObject(
                    await File.ReadAllTextAsync(configFile, cancellationToken).ConfigureAwait(false));
            }

            var installer = new SecurityToolkitInstaller(InstallerTemplates.LoadAll());
            var processRunner = new ProcessRunner();
            var toolProvisioner = options.ConfigureOnly
                ? null
                : new ToolProvisioner(processRunner, new NuGetVersionResolver(new HttpClient()));

            var dependencies = new InstallerDependencies(
                new SemgrepRuleDownloader(new HttpClient()),
                new VsCodeExtensionInstaller(processRunner),
                new GitHookInstaller(processRunner),
                ToolkitReleaseResolver: new ToolkitReleaseResolver(new HttpClient()));

            // --check and --dry-run never prompt, even without --non-interactive: InstallAsync would
            // otherwise ask for target selection and block on stdin (e.g. in CI or over ssh).
            var interactive = !options.NonInteractive && !options.CheckOnly && !options.DryRun;
            IConsolePrompts prompts = interactive ? new ConsolePrompts() : new NonInteractivePrompts();
            SecurityToolkitInstaller.TargetSelector targetSelector = prompts.SelectTargets;

            var securityDirectoryForOverride = Path.Combine(Path.GetFullPath(options.ProjectRoot), ".security");
            var globalConfigPath = Path.Combine(securityDirectoryForOverride, SecurityToolkitInstaller.ConfigFileName);
            var localConfigPath = Path.Combine(securityDirectoryForOverride, SecurityToolkitInstaller.LocalConfigFileName);
            var globalExists = File.Exists(globalConfigPath);
            var localExists = File.Exists(localConfigPath);

            var runGlobalWizard = !globalExists;
            var runLocalWizard = !localExists;

            if (interactive)
            {
                if (globalExists)
                {
                    runGlobalWizard = prompts.ReadYesNo(
                        "An existing shared configuration was found. Run the wizard on it again?", defaultValue: false);
                }

                if (localExists)
                {
                    runLocalWizard = prompts.ReadYesNo(
                        "An existing local (machine-specific) configuration was found. Run the wizard on it again?", defaultValue: false);
                }
                else if (!globalExists)
                {
                    Console.WriteLine("No existing configuration: running the full wizard (shared + local).");
                }
            }

            // The local override (.security/security.config.local.json) is never merged into
            // configProfile: configProfile flows straight into the committed security.config.json
            // InstallAsync writes, so merging it would leak local-only settings into the committed
            // file (this shipped once, by mistake, and had to be reverted). The shared Sonar server
            // itself is machine-level infra (security-toolkit infra install), not started here.

            // Interactive runs collect every project-specific answer (tool selection, Sonar
            // URL/key/port, Azure pool/feeds, ...) up front via the same ResolveConfig starting
            // point InstallAsync uses internally, then hand the fully-built result back in as a
            // configProfile with NonInteractive forced — so InstallAsync never re-prompts for
            // target selection, and Core stays untouched by any of this.
            if (interactive && (runGlobalWizard || runLocalWizard))
            {
                Console.WriteLine("Security Toolkit - tool selection and project installation");
                Console.WriteLine("Existing feeds, deploy pipelines and application passwords are never rewritten.");

                var root = Path.GetFullPath(options.ProjectRoot);
                var (scan, resolved) = SecurityToolkitInstaller.ResolveConfig(root, configProfile, options.SemgrepScope);

                if (runGlobalWizard)
                {
                    Console.WriteLine();
                    Console.WriteLine("=== SHARED configuration (committed: security.config.json) ===");
                }

                Console.WriteLine();
                Console.WriteLine("--- Targets ---");
                resolved["dotnetTargets"] = targetSelector(
                    ".NET solution/project targets", scan.SolutionRelativePaths,
                    ScannerSupport.GetList(resolved, "dotnetTargets")
                        .Select(v => v as string ?? string.Empty).Where(v => v.Length > 0).ToList())
                    .Cast<object?>().ToList();
                resolved["frontendTargets"] = targetSelector(
                    "Frontend/npm target directories", scan.FrontendRelativeDirectories,
                    ScannerSupport.GetList(resolved, "frontendTargets")
                        .Select(v => v as string ?? string.Empty).Where(v => v.Length > 0).ToList())
                    .Cast<object?>().ToList();

                if (runGlobalWizard)
                {
                    resolved = InteractiveConfigBuilder.Build(prompts, scan, resolved);
                }

                if (runLocalWizard)
                {
                    Console.WriteLine();
                    Console.WriteLine("=== LOCAL configuration (machine-specific, gitignored: security.config.local.json) ===");

                    var resolvedTools = ScannerSupport.GetDictionary(resolved, "tools");
                    var sonarToolEnabled = resolvedTools is not null && ScannerSupport.GetBool(resolvedTools, "sonar");
                    var wantsLocal = sonarToolEnabled
                        && prompts.ReadYesNo(
                            "Override the Sonar URL locally (e.g. for the Sonar installed on this machine with infra install)?",
                            defaultValue: false);
                    if (wantsLocal)
                    {
                        var localUrl = prompts.ReadValue("Local Sonar URL", "http://127.0.0.1:9000");
                        var localOverrideToWrite = new Dictionary<string, object?>
                        {
                            ["sonar"] = new Dictionary<string, object?> { ["url"] = localUrl },
                        };
                        Directory.CreateDirectory(securityDirectoryForOverride);
                        await ToolkitFile.WriteAllTextAsync(
                            localConfigPath, ToolkitJson.Serialize(localOverrideToWrite), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                configProfile = resolved;
                options = options with { NonInteractive = true };

                Console.WriteLine();
                var runAfterInstall = prompts.ReadYesNo("Run a full Report scan after the installation?", options.RunAfterInstall);
                options = options with { RunAfterInstall = runAfterInstall };

                // Target selection already happened above; InstallAsync must not prompt again.
                targetSelector = new NonInteractivePrompts().SelectTargets;
            }

            var outcome = await installer.InstallAsync(options, configProfile, toolProvisioner, dependencies, targetSelector, cancellationToken)
                .ConfigureAwait(false);

            foreach (var message in outcome.Messages)
            {
                Console.Error.WriteLine(message);
            }

            if (options.CheckOnly)
            {
                var entries = outcome.DriftEntries ?? Array.Empty<DriftEntry>();
                foreach (var entry in entries)
                {
                    Console.WriteLine($"{entry.Status,-24} {entry.RelativePath}");
                }

                var drifted = entries.Where(e => e.Status != DriftStatus.Unchanged).ToList();
                if (drifted.Count == 0)
                {
                    Console.WriteLine("In sync: every managed file matches the current toolkit source.");
                    return 0;
                }

                Console.WriteLine($"Drift: {drifted.Count} file(s) differ from the current toolkit source.");
                return 1;
            }

            Console.WriteLine($"Project configuration: {Path.Combine(Path.GetFullPath(options.ProjectRoot), ".security")}");
            if (outcome.ManifestPath is not null)
            {
                Console.WriteLine($"Restore manifest: {outcome.ManifestPath}");
            }

            return outcome.ExitCode;
        });

        return command;
    }
}
