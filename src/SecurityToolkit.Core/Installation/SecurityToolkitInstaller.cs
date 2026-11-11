using SecurityToolkit.Core.Scanners;
using static SecurityToolkit.Core.Installation.ConfigAccess;

namespace SecurityToolkit.Core.Installation;

/// <summary>Outcome of one <see cref="SecurityToolkitInstaller.InstallAsync"/> run.</summary>
/// <param name="ExitCode">0 success, 2 partial/technical failure.</param>
/// <param name="Messages">Pending-action notes, written to ".security/pending-actions.md".</param>
/// <param name="ManifestPath">Path to this run's transaction manifest.json, or null in DryRun mode.</param>
/// <param name="Config">The final resolved security.config.json tree.</param>
/// <param name="DriftEntries">
/// Populated only for an <see cref="InstallOptions.CheckOnly"/> run: every toolkit-managed file's
/// drift status against what this install would generate. Empty for a real (or <c>DryRun</c>) run.
/// </param>
public sealed record InstallOutcome(
    int ExitCode,
    IReadOnlyList<string> Messages,
    string? ManifestPath,
    IDictionary<string, object?> Config,
    IReadOnlyList<DriftEntry>? DriftEntries = null);

/// <summary>
/// Optional post-provisioning integrations. Any null field is simply skipped, matching that field's config flag having no effect when the
/// corresponding tooling isn't available/selected.
/// </summary>
/// <param name="SemgrepRuleDownloader">Downloads registry Semgrep rulesets.</param>
/// <param name="VsCodeExtensionInstaller">Installs VS Code extensions for the enabled tools.</param>
/// <param name="GitHookInstaller">Installs the chained pre-commit secret-scan hook.</param>
/// <param name="PrerequisiteProvisioner">
/// Ensures git/dotnet/node/docker are available before <see cref="Installation.ToolProvisioner"/>
/// runs; null skips the check entirely, like the other optional integrations.
/// </param>
public sealed record InstallerDependencies(
    SemgrepRuleDownloader? SemgrepRuleDownloader,
    VsCodeExtensionInstaller? VsCodeExtensionInstaller,
    GitHookInstaller? GitHookInstaller,
    PrerequisiteProvisioner? PrerequisiteProvisioner = null,
    ToolkitReleaseResolver? ToolkitReleaseResolver = null);

/// <summary>
/// Project-independent local security toolkit installer. Generates/updates the target project's
/// ".security" directory (MSBuild props, rules, gitleaks config, JSON config/lock), retires
/// files earlier versions generated, wires the project's Directory.Build.props/.editorconfig/.gitignore/.semgrepignore,
/// and (unless <see cref="InstallOptions.ConfigureOnly"/>) provisions the enabled tools via
/// <see cref="ToolProvisioner"/>.
/// </summary>
/// <remarks>
/// Interactive prompting is intentionally kept out of Core:
/// an interactive CLI front-end is responsible for collecting those answers and passing a
/// fully-resolved <see cref="InstallOptions"/> (plus an optional config-file profile) in. The one
/// exception is the dotnet/frontend target selection: that choice directly
/// drives config validation below, so it's exposed as an injectable
/// <see cref="TargetSelector"/> delegate instead — the CLI supplies either its interactive
/// prompt or a non-interactive passthrough (its own default here), rather than Core depending on
/// <see cref="Console"/> or a CLI-layer prompt abstraction.
/// </remarks>
public sealed class SecurityToolkitInstaller
{
    /// <summary>
    /// Resolves a target selection: given a display name, every discovered candidate path, and
    /// the currently-configured selection, returns the paths to actually use. The default (used
    /// when the caller passes null) is non-interactive: the existing selection if non-empty,
    /// otherwise every discovered path.
    /// </summary>
    public delegate IReadOnlyList<string> TargetSelector(string name, IReadOnlyList<string> discovered, IReadOnlyList<string> existing);

    private static readonly TargetSelector DefaultTargetSelector =
        (_, discovered, existing) => existing.Count > 0 ? existing : discovered;

    /// <summary>The committed, project-shared config file name.</summary>
    public const string ConfigFileName = "security.config.json";

    /// <summary>
    /// Where <c>toolkitRelease</c> downloads releases from by default: this toolkit's GitHub
    /// releases (<c>&lt;baseUrl&gt;/v&lt;version&gt;/&lt;asset&gt;</c>). A mirror with the same layout
    /// (e.g. an internal artifact server) can be configured instead.
    /// </summary>
    public const string DefaultToolkitReleaseBaseUrl = "https://github.com/barcsaidominik/SecurityToolkit/releases/download";

    /// <summary>
    /// The gitignored, per-machine override file name. Holds any user-set local overrides (e.g. a
    /// local Sonar URL) — never lock data.
    /// </summary>
    public const string LocalConfigFileName = "security.config.local.json";

    /// <summary>The committed lock file name — the project's CI-relevant tool pins.</summary>
    public const string LockFileName = "security.lock.json";

    /// <summary>Repository-relative path of the generated GitHub Actions workflow (forward slashes).</summary>
    public const string GitHubWorkflowRelativePath = ".github/workflows/security-toolkit.yml";

    /// <summary>Resource-embedded template files copied verbatim into ".security".</summary>
    private readonly IReadOnlyDictionary<string, string> _templateFiles;

    public SecurityToolkitInstaller(IReadOnlyDictionary<string, string> templateFiles)
    {
        _templateFiles = templateFiles;
    }

    /// <summary>
    /// Runs the install. Never throws for expected/handled failures (tool provisioning errors,
    /// etc.) — those are captured in the returned <see cref="InstallOutcome"/>. Programming errors (bad options, invalid config
    /// schema) still throw.
    /// </summary>
    public async Task<InstallOutcome> InstallAsync(
        InstallOptions options,
        IDictionary<string, object?>? configProfile,
        ToolProvisioner? toolProvisioner,
        CancellationToken cancellationToken = default)
        => await InstallAsync(options, configProfile, toolProvisioner, dependencies: null, targetSelector: null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Full overload accepting the optional post-provisioning integrations (Semgrep rule
    /// download, local Sonar Compose, VS Code extensions, the git pre-commit hook) and an
    /// interactive target selector. All are optional: a null <paramref name="dependencies"/>
    /// (or any null field within it) simply skips that integration, exactly like the simpler
    /// overload already does for tool provisioning via a null <paramref name="toolProvisioner"/>.
    /// </summary>
    public async Task<InstallOutcome> InstallAsync(
        InstallOptions options,
        IDictionary<string, object?>? configProfile,
        ToolProvisioner? toolProvisioner,
        InstallerDependencies? dependencies,
        TargetSelector? targetSelector,
        CancellationToken cancellationToken = default)
    {
        dependencies ??= new InstallerDependencies(null, null, null);
        targetSelector ??= DefaultTargetSelector;
        var root = Path.GetFullPath(options.ProjectRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"ProjectRoot must be a directory: {options.ProjectRoot}");
        }

        if (root.Contains(',', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Docker --mount syntax does not support commas in the repository path; move the checkout.");
        }

        var securityDirectory = Path.Combine(root, ".security");
        var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");

        var priorHashes = ReadPriorHashes(securityDirectory);
        var backup = new BackupService(root, securityDirectory, runId, priorHashes, checkOnly: options.CheckOnly);
        var messages = new List<string>();

        var (scan, config) = ResolveConfig(root, configProfile, options.SemgrepScope);

        // Lets an interactive CLI narrow the discovered candidates down to a subset, before that selection feeds config
        // validation below. NonInteractivePrompts/the default selector just keep the existing
        // selection (or every discovered candidate) untouched.
        config["dotnetTargets"] = targetSelector(
            ".NET solution/project targets", scan.SolutionRelativePaths, GetStringList(config, "dotnetTargets"))
            .Cast<object?>().ToList();
        config["frontendTargets"] = targetSelector(
            "Frontend/npm target directories", scan.FrontendRelativeDirectories, GetStringList(config, "frontendTargets"))
            .Cast<object?>().ToList();

        // Validation failures are expected, user-fixable configuration errors (bad Sonar
        // project key/port, missing dotnetTarget, etc.) — they must surface through the
        // same InstallOutcome.Messages/ExitCode contract as every other expected failure
        // below, not as an unhandled exception/stack trace at the CLI boundary.
        SecurityConfig settings;
        try
        {
            SecurityConfigValidator.Validate(config, scan);
            settings = SecurityConfig.From(config);
        }
        catch (InvalidOperationException ex)
        {
            return new InstallOutcome(2, new[] { ex.Message }, null, config);
        }

        if (options.DryRun)
        {
            return new InstallOutcome(0, new[] { "DryRun: no file writes, downloads, package installs or git settings." }, null, config);
        }

        if (!options.CheckOnly)
        {
            Directory.CreateDirectory(securityDirectory);
        }

        var exitCode = 0;

        try
        {
            WriteOwnedTemplates(backup, securityDirectory, settings);
            RemoveRetiredFiles(backup, securityDirectory, messages, options.CheckOnly);
            backup.SaveManagedText(Path.Combine(securityDirectory, ConfigFileName), ToolkitJson.Serialize(config));

            ProjectWiring.WriteIgnoreBlocks(backup, root, settings);
            ProjectWiring.WireDotNetProjects(backup, root, scan, settings);

            var lockData = SecurityLockBuilder.Build(settings, securityDirectory);

            // The release checksums must be pinned before the lock and the pipelines are written: the
            // pipelines embed them. --configure-only/--check never download (the pipeline step then
            // fails with a "not pinned" message until a full install runs).
            if (TryGetDict(lockData, "toolkitRelease") is { } toolkitRelease
                && TryGetDict(toolkitRelease, "sha256") is { Count: 0 } pinnedHashes
                && !options.ConfigureOnly && !options.CheckOnly
                && dependencies.ToolkitReleaseResolver is not null)
            {
                try
                {
                    var hashes = await dependencies.ToolkitReleaseResolver.ResolveAsync(
                        GetString(toolkitRelease, "baseUrl")!, GetString(toolkitRelease, "version")!, cancellationToken).ConfigureAwait(false);
                    foreach (var (rid, hash) in hashes)
                    {
                        pinnedHashes[rid] = hash;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    messages.Add(ex.Message);
                    exitCode = 2;
                }
            }

            backup.SaveManagedText(Path.Combine(securityDirectory, LockFileName), ToolkitJson.Serialize(lockData), owned: true);

            if (settings.GeneratePipeline)
            {
                PipelineGenerator.Write(backup, root, settings, lockData, messages);
            }

            if (options.CheckOnly)
            {
                return new InstallOutcome(0, messages, null, config, backup.DriftEntries);
            }

            var legacySonarComposePath = Path.Combine(securityDirectory, "sonar", "compose.yml");
            if (File.Exists(legacySonarComposePath))
            {
                messages.Add(
                    "An old, project-specific Sonar Docker stack was found (.security/sonar/compose.yml). " +
                    "Sonar now runs on a shared server. If you no longer need the old stack: " +
                    "docker compose -f .security/sonar/compose.yml --env-file .security/sonar/.env " +
                    "down -v, then delete the .security/sonar/ folder.");
            }

            var legacyExecutablePath = Path.Combine(
                securityDirectory, "bin", OperatingSystem.IsWindows() ? "security-toolkit.exe" : "security-toolkit");
            if (File.Exists(legacyExecutablePath))
            {
                messages.Add(
                    "An old, project-specific security-toolkit copy was found (.security/bin/). " +
                    "The git hook now uses the shared installation on PATH (see install.bat / install.sh " +
                    "in the SecurityToolkit source repository); the .security/bin/ copy is no longer needed and can be deleted by hand.");
            }

            if (settings.InstallHook && dependencies.GitHookInstaller is not null)
            {
                var hookMessage = await dependencies.GitHookInstaller.InstallAsync(root, backup, dryRun: false, cancellationToken)
                    .ConfigureAwait(false);
                if (hookMessage is not null)
                {
                    messages.Add(hookMessage);
                }

                if (!dependencies.GitHookInstaller.IsExecutableOnPath())
                {
                    messages.Add(
                        "The 'security-toolkit' command is not on PATH, but the git hook calls it: " +
                        "run install.bat (Windows) or install.sh (Linux) in the SecurityToolkit source repository, then open a new terminal " +
                        "so the hook works.");
                }
            }

            if (!options.ConfigureOnly)
            {
                if (settings.IsToolEnabled("semgrep") && settings.DownloadSemgrepRules && dependencies.SemgrepRuleDownloader is not null)
                {
                    var ruleMessages = await dependencies.SemgrepRuleDownloader.DownloadAsync(
                        root, securityDirectory, settings.SemgrepRulesets, priorHashes ?? new Dictionary<string, string>(StringComparer.Ordinal), backup, dryRun: false, cancellationToken)
                        .ConfigureAwait(false);
                    messages.AddRange(ruleMessages);
                }

                if (dependencies.PrerequisiteProvisioner is not null)
                {
                    var prerequisiteOutcome = await dependencies.PrerequisiteProvisioner.EnsureAsync(
                        GetToolsDict(config),
                        settings.InstallPrerequisites,
                        settings.SdkChannel,
                        settings.NodeWingetId,
                        settings.NodeVersion,
                        cancellationToken).ConfigureAwait(false);
                    messages.AddRange(prerequisiteOutcome.Messages);
                    if (!prerequisiteOutcome.Completed)
                    {
                        exitCode = 2;
                    }
                }

                if (toolProvisioner is not null)
                {
                    var provisionOutcome = await toolProvisioner.ProvisionAsync(securityDirectory, requirePinned: false, cancellationToken)
                        .ConfigureAwait(false);
                    if (!provisionOutcome.Completed)
                    {
                        messages.Add("Tool initialization failed: " + string.Join("; ", provisionOutcome.Errors));
                        exitCode = 2;
                    }
                }

                if (settings.InstallVSCodeExtensions && dependencies.VsCodeExtensionInstaller is not null)
                {
                    await dependencies.VsCodeExtensionInstaller.InstallAsync(
                        settings.IsToolEnabled("sonar"), settings.IsToolEnabled("devskim"), cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                messages.Add("ConfigureOnly: the CLI/image installation and the vendor rule download have not happened yet. Run again without --configure-only.");
            }

            if (settings.IsToolEnabled("sonar"))
            {
                messages.Add("Sonar: log in as admin and change the password, then create a private project and a project analysis token. The token is read only from the SONAR_TOKEN environment variable. Upload: security-toolkit scan --include-sonar. The shared Sonar server is installed once per machine by install.bat / install.sh (security-toolkit infra install --sonar); the project install does not start it.");
            }

            if (settings.GeneratePipeline)
            {
                if (settings.HasPipelineProvider("azure"))
                {
                    messages.Add("Azure: grant the pool/repo/feed permissions and create a pipeline for the generated YAML; the installer does not connect to Azure.");
                }

                if (settings.HasPipelineProvider("github"))
                {
                    messages.Add(
                        $"GitHub: {GitHubWorkflowRelativePath} expects a self-hosted runner (githubRunsOn labels) with security-toolkit on PATH; " +
                        "add SONAR_TOKEN (and the sonar.testEnvironment secrets) to the repository's Actions secrets. The installer does not connect to GitHub.");
                }
            }
        }
        catch (Exception ex)
        {
            messages.Add(ex.Message);
            exitCode = 2;
        }
        finally
        {
            if (!options.CheckOnly)
            {
                foreach (var relative in backup.SkippedOwnedFiles)
                {
                    messages.Add(
                        $"Skipped (manual change detected): {relative}. The file was left unchanged; the rest of the " +
                        "installation ran. To get the toolkit's generated version back, delete or save away " +
                        "the manual change, then run the install again.");
                }

                backup.Finish();
                ToolkitFile.WriteAllText(
                    Path.Combine(securityDirectory, "pending-actions.md"),
                    "# Remaining steps after installation\n\n" + string.Join("\n\n", messages) + "\n");
            }
        }

        return new InstallOutcome(exitCode, messages, options.CheckOnly ? null : backup.ManifestPath, config);
    }

    /// <summary>
    /// Scans <paramref name="root"/> and resolves the config that a real <see cref="InstallAsync"/>
    /// call would start from, before target selection and validation: defaults, merged with any
    /// existing committed config, merged with <paramref name="configProfile"/>, with
    /// <paramref name="semgrepScope"/> applied last. Exposed so a CLI-layer interactive config
    /// builder can present the same starting point <see cref="InstallAsync"/> uses internally —
    /// there is exactly one place this merge order is implemented.
    /// </summary>
    public static (ProjectScanResult Scan, IDictionary<string, object?> Config) ResolveConfig(
        string root, IDictionary<string, object?>? configProfile, SemgrepInstallScope semgrepScope)
    {
        var securityDirectory = Path.Combine(root, ".security");
        var scan = ProjectFileScanner.Scan(root);
        var config = SecurityConfigMerger.BuildDefaultConfig(scan);

        SecurityConfigMerger.MergeExistingConfig(config, securityDirectory);
        if (configProfile is not null)
        {
            SecurityConfigMerger.StripLegacyKeys(configProfile);
            SecurityConfigMerger.MergeConfig(config, configProfile);
        }

        if (semgrepScope != SemgrepInstallScope.Unspecified)
        {
            config["semgrepScope"] = semgrepScope == SemgrepInstallScope.Frontend ? "frontend" : "repository";
        }

        return (scan, config);
    }

    private void WriteOwnedTemplates(BackupService backup, string securityDirectory, SecurityConfig config)
    {
        if (_templateFiles.TryGetValue("SecurityToolkit.props", out var props))
        {
            backup.SaveManagedText(Path.Combine(securityDirectory, "SecurityToolkit.props"), props, owned: true);
        }

        // Not owned: a sample a developer may freely copy to security.config.local.json (gitignored)
        // and edit however they like — an owned hash check would fight that.
        if (_templateFiles.TryGetValue("security.config.local.json.example", out var localExample))
        {
            backup.SaveManagedText(Path.Combine(securityDirectory, $"{LocalConfigFileName}.example"), localExample);
        }

        if (config.IsToolEnabled("gitleaks") && _templateFiles.TryGetValue("gitleaks.toml", out var gitleaksToml))
        {
            backup.SaveManagedText(Path.Combine(securityDirectory, "gitleaks.toml"), gitleaksToml, owned: true);
        }

        if (config.IsToolEnabled("semgrep") && _templateFiles.TryGetValue("semgrep-starter.yml", out var semgrepStarter))
        {
            backup.SaveManagedText(Path.Combine(securityDirectory, "rules", "starter.yml"), semgrepStarter, owned: true);
        }
    }

    /// <summary>
    /// Owned files earlier toolkit versions generated into ".security" and this one no longer
    /// does: the PowerShell provisioning/restore scripts (replaced by <c>security-toolkit
    /// provision</c> and <c>security-toolkit restore</c>) and the older standalone scan script.
    /// </summary>
    public static readonly IReadOnlyList<string> RetiredSecurityFiles = new[]
    {
        "Initialize-SecurityTools.ps1", "Restore-SecurityToolkit.ps1", "Toolkit.Common.ps1", "Invoke-SecurityScan.ps1",
    };

    private static void RemoveRetiredFiles(BackupService backup, string securityDirectory, List<string> messages, bool checkOnly)
    {
        var deleted = RetiredSecurityFiles
            .Where(name => backup.DeleteRetiredFile(Path.Combine(securityDirectory, name)))
            .Select(name => $".security/{name}")
            .ToList();

        if (checkOnly)
        {
            return;
        }

        if (deleted.Count > 0)
        {
            messages.Add(
                "Retired toolkit files deleted (the CLI replaces them; restore undoes the deletion): " +
                string.Join(", ", deleted) + ". Commit the deletion.");
        }

        foreach (var relative in backup.KeptRetiredFiles)
        {
            messages.Add(
                $"Retired toolkit file left in place because it was changed by hand: {relative}. The toolkit no longer " +
                "uses it; delete it by hand if you don't need it.");
        }
    }

    /// <summary>
    /// install-state.json's hash map, or <c>null</c> when this checkout has never been installed into
    /// (fresh clone / new machine; see <see cref="BackupService"/>'s <c>priorHashes</c>).
    /// </summary>
    private static Dictionary<string, string>? ReadPriorHashes(string securityDirectory)
    {
        var statePath = Path.Combine(securityDirectory, "install-state.json");
        if (!File.Exists(statePath))
        {
            return null;
        }

        var parsed = ScannerSupport.ParseJsonObject(File.ReadAllText(statePath));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parsed.TryGetValue("hashes", out var hashesValue) && hashesValue is IDictionary<string, object?> hashes)
        {
            foreach (var (key, value) in hashes)
            {
                if (value is string hash)
                {
                    result[key] = hash;
                }
            }
        }

        return result;
    }

}
