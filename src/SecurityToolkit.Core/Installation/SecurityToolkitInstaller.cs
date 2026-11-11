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
            ".NET solution/projekt célok", scan.SolutionRelativePaths, GetStringList(config, "dotnetTargets"))
            .Cast<object?>().ToList();
        config["frontendTargets"] = targetSelector(
            "Frontend/npm célkönyvtárak", scan.FrontendRelativeDirectories, GetStringList(config, "frontendTargets"))
            .Cast<object?>().ToList();

        // Validation failures are expected, user-fixable configuration errors (bad Sonar
        // project key/port, missing dotnetTarget, etc.) — they must surface through the
        // same InstallOutcome.Messages/ExitCode contract as every other expected failure
        // below, not as an unhandled exception/stack trace at the CLI boundary.
        try
        {
            SecurityConfigValidator.Validate(config, scan);
        }
        catch (InvalidOperationException ex)
        {
            return new InstallOutcome(2, new[] { ex.Message }, null, config);
        }

        if (options.DryRun)
        {
            return new InstallOutcome(0, new[] { "DryRun: nincs fájlírás, letöltés, csomagtelepítés vagy git-beállítás." }, null, config);
        }

        if (!options.CheckOnly)
        {
            Directory.CreateDirectory(securityDirectory);
        }

        var exitCode = 0;

        try
        {
            WriteOwnedTemplates(backup, securityDirectory, config);
            RemoveRetiredFiles(backup, securityDirectory, messages, options.CheckOnly);
            backup.SaveManagedText(Path.Combine(securityDirectory, ConfigFileName), ToolkitJson.Serialize(config));

            ProjectWiring.WriteIgnoreBlocks(backup, root, config);
            ProjectWiring.WireDotNetProjects(backup, root, scan, config);

            var lockData = SecurityLockBuilder.Build(config, securityDirectory);

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

            if (GetBool(config, "generatePipeline"))
            {
                PipelineGenerator.Write(backup, root, config, lockData, messages);
            }

            if (options.CheckOnly)
            {
                return new InstallOutcome(0, messages, null, config, backup.DriftEntries);
            }

            var legacySonarComposePath = Path.Combine(securityDirectory, "sonar", "compose.yml");
            if (File.Exists(legacySonarComposePath))
            {
                messages.Add(
                    "Régi, projekt-specifikus Sonar Docker stack található (.security/sonar/compose.yml). " +
                    "A Sonar mostantól közös, megosztott szerveren fut. Ha a régi stack-re nincs többé " +
                    "szükség: docker compose -f .security/sonar/compose.yml --env-file .security/sonar/.env " +
                    "down -v, majd törölheted a .security/sonar/ mappát.");
            }

            var legacyExecutablePath = Path.Combine(
                securityDirectory, "bin", OperatingSystem.IsWindows() ? "security-toolkit.exe" : "security-toolkit");
            if (File.Exists(legacyExecutablePath))
            {
                messages.Add(
                    "Régi, projekt-specifikus security-toolkit másolat található (.security/bin/). " +
                    "A git hook mostantól a PATH-ról futtatott, közös telepítést használja (lásd install.bat / install.sh " +
                    "a SecurityToolkit forrás-repóban) — a .security/bin/ másolatra nincs többé szükség, kézzel törölhető.");
            }

            if (GetBool(config, "installHook") && dependencies.GitHookInstaller is not null)
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
                        "A 'security-toolkit' parancs nem található a PATH-on. A git hook ezt hívja majd — " +
                        "futtasd az install.bat-ot (Windows) vagy az install.sh-t (Linux) a SecurityToolkit forrás-repóban, majd nyiss új terminált, " +
                        "hogy a hook működjön.");
                }
            }

            if (!options.ConfigureOnly)
            {
                if (GetBool(GetToolsDict(config), "semgrep") && GetBool(config, "downloadSemgrepRules") && dependencies.SemgrepRuleDownloader is not null)
                {
                    var rulesets = GetStringList(config, "semgrepRulesets");
                    var ruleMessages = await dependencies.SemgrepRuleDownloader.DownloadAsync(
                        root, securityDirectory, rulesets, priorHashes ?? new Dictionary<string, string>(StringComparer.Ordinal), backup, dryRun: false, cancellationToken)
                        .ConfigureAwait(false);
                    messages.AddRange(ruleMessages);
                }

                if (dependencies.PrerequisiteProvisioner is not null)
                {
                    var prerequisiteOutcome = await dependencies.PrerequisiteProvisioner.EnsureAsync(
                        GetToolsDict(config),
                        GetBool(config, "installPrerequisites"),
                        GetString(config, "sdkChannel") ?? string.Empty,
                        GetString(config, "nodeWingetId") ?? string.Empty,
                        GetString(config, "nodeVersion") ?? string.Empty,
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

                if (GetBool(config, "installVSCodeExtensions") && dependencies.VsCodeExtensionInstaller is not null)
                {
                    var tools = GetToolsDict(config);
                    await dependencies.VsCodeExtensionInstaller.InstallAsync(
                        GetBool(tools, "sonar"), GetBool(tools, "devskim"), cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                messages.Add("ConfigureOnly: a CLI/image telepítés és a gyártói szabályletöltés még nem történt meg. Futtasd újra ConfigureOnly nélkül.");
            }

            if (GetBool(GetToolsDict(config), "sonar"))
            {
                messages.Add("Sonar: admin belépés/jelszócsere, private projekt és project analysis token létrehozása szükséges. Token csak SONAR_TOKEN environment változóból. Feltöltés: security-toolkit scan --include-sonar. A közös Sonar-szervert gépenként egyszer az install.bat / install.sh (security-toolkit infra install --sonar) telepíti, a projekt-install nem indítja.");
            }

            if (GetBool(config, "generatePipeline"))
            {
                var providers = GetStringList(config, "pipelineProviders");
                if (providers.Contains("azure"))
                {
                    messages.Add("Azure: pool/repo/feed jogosultságok és a generált YAML-hoz pipeline létrehozása szükséges; a telepítő nem csatlakozik Azure-hoz.");
                }

                if (providers.Contains("github"))
                {
                    messages.Add(
                        $"GitHub: a {GitHubWorkflowRelativePath} egy self-hosted runnert vár (githubRunsOn címkék), amin a security-toolkit a PATH-on van; " +
                        "a SONAR_TOKEN-t (és a sonar.testEnvironment secretjeit) a repó Actions secretjei közé kell felvenni. A telepítő nem csatlakozik GitHubhoz.");
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
                        $"Kihagyva (kézi módosítás észlelve): {relative}. A fájl változatlan maradt; a telepítés " +
                        "többi lépése lefutott. Ha a toolkit generált verzióját szeretnéd vissza, töröld/mentsd el " +
                        "a kézi módosítást, majd futtasd újra az installt.");
                }

                backup.Finish();
                ToolkitFile.WriteAllText(
                    Path.Combine(securityDirectory, "pending-actions.md"),
                    "# Telepítés után fennmaradó lépések\n\n" + string.Join("\n\n", messages) + "\n");
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

    private void WriteOwnedTemplates(BackupService backup, string securityDirectory, IDictionary<string, object?> config)
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

        var tools = GetToolsDict(config);

        if (GetBool(tools, "gitleaks") && _templateFiles.TryGetValue("gitleaks.toml", out var gitleaksToml))
        {
            backup.SaveManagedText(Path.Combine(securityDirectory, "gitleaks.toml"), gitleaksToml, owned: true);
        }

        if (GetBool(tools, "semgrep") && _templateFiles.TryGetValue("semgrep-starter.yml", out var semgrepStarter))
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
                "Kivezetett toolkit-fájlok törölve (a CLI váltja ki őket, a törlés restore-ral visszavonható): " +
                string.Join(", ", deleted) + ". A törlést commitold.");
        }

        foreach (var relative in backup.KeptRetiredFiles)
        {
            messages.Add(
                $"Kivezetett toolkit-fájl, de kézzel módosítva, ezért nem töröltem: {relative}. A toolkit már nem " +
                "használja; ha nincs rá szükséged, töröld kézzel.");
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
