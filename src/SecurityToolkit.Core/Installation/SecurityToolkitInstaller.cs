using SecurityToolkit.Core.Scanners;

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

    private static readonly string[] AnalyzerDiagnostics =
    {
        "CA3001", "CA3002", "CA3003", "CA3006", "CA3007", "CA3009", "CA3075",
    };

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

    /// <summary>
    /// Config keys of retired features: the project-level Sonar stack (moved to machine-level infra,
    /// <c>security-toolkit infra install</c>) and Snyk (removed: SaaS-only, it uploads the source
    /// code). Existing configs and profiles may still carry them: they are dropped
    /// before merging instead of failing as unknown keys.
    /// </summary>
    private static readonly string[][] LegacyConfigPaths =
    {
        new[] { "startSonarServer" },
        new[] { "sonar", "port" },
        new[] { "images", "sonarServer" },
        new[] { "images", "postgres" },
        new[] { "tools", "snyk" },
        new[] { "versions", "snyk" },
    };

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
            ValidateConfig(config, scan);
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

            WriteIgnoreBlocks(backup, root, config);
            WireDotNetProjects(backup, root, scan, config);

            var lockData = BuildLock(config, securityDirectory);

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
                WritePipelineFiles(backup, root, config, lockData, messages);
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

            if (!options.ConfigureOnly)
            {
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
        var config = BuildDefaultConfig(scan);

        MergeExistingConfig(config, securityDirectory);
        if (configProfile is not null)
        {
            StripLegacyKeys(configProfile);
            MergeConfig(config, configProfile);
        }

        if (semgrepScope != SemgrepInstallScope.Unspecified)
        {
            config["semgrepScope"] = semgrepScope == SemgrepInstallScope.Frontend ? "frontend" : "repository";
        }

        return (scan, config);
    }

    private static IDictionary<string, object?> BuildDefaultConfig(ProjectScanResult scan)
    {
        var hasProjects = scan.CsprojFiles.Count > 0;
        var hasFrontends = scan.FrontendRelativeDirectories.Count > 0;

        return new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1L,
            ["dotnetTargets"] = scan.SolutionRelativePaths.Cast<object?>().ToList(),
            ["frontendTargets"] = scan.FrontendRelativeDirectories.Cast<object?>().ToList(),
            ["semgrepScope"] = "repository",
            ["tools"] = new Dictionary<string, object?>
            {
                ["analyzers"] = hasProjects,
                ["semgrep"] = true,
                ["gitleaks"] = true,
                ["nuget"] = hasProjects,
                ["npm"] = hasFrontends,
                ["osv"] = false,
                ["devskim"] = false,
                ["sonar"] = false,
            },
            ["installPrerequisites"] = false,
            ["sdkChannel"] = "10.0",
            ["nodeWingetId"] = "OpenJS.NodeJS.LTS",
            ["nodeVersion"] = string.Empty,
            ["downloadSemgrepRules"] = true,
            ["semgrepRulesets"] = new List<object?> { "p/csharp", "p/typescript" },
            ["installHook"] = false,
            ["installVSCodeExtensions"] = false,
            ["generatePipeline"] = true,
            ["pipelineHistory"] = true,
            ["agentPool"] = "SecurityToolkit",
            ["azureVmImage"] = string.Empty,
            ["privateNuGet"] = false,
            ["privateNpmRc"] = string.Empty,
            ["pipelineProviders"] = new List<object?> { "azure" },
            ["githubRunsOn"] = new List<object?> { "self-hosted" },
            ["githubCodeScanning"] = false,
            ["githubSchedule"] = string.Empty,
            // Empty version: the pipelines expect the CLI on the agent's PATH (install.bat/install.sh).
            // Set: they download this release first (ephemeral/hosted agents), verified against the
            // checksums pinned in the lock.
            ["toolkitRelease"] = new Dictionary<string, object?>
            {
                ["version"] = string.Empty,
                ["baseUrl"] = DefaultToolkitReleaseBaseUrl,
            },
            ["sonar"] = new Dictionary<string, object?>
            {
                ["url"] = "http://localhost:9000",
                ["projectKey"] = "security-" + System.Text.RegularExpressions.Regex.Replace(
                    Path.GetFileName(Path.GetFullPath(scan.SolutionRelativePaths.FirstOrDefault() ?? "project")) ?? "project",
                    "[^A-Za-z0-9_.-]", "-"),
                ["dotnetTarget"] = string.Empty,
                ["coverage"] = false,
                ["testFilter"] = string.Empty,
                ["testEnvironment"] = new Dictionary<string, object?>(),
            },
            // Noise reduction for DevSkim: tests and a few dev-only rules tend to make up most hits.
            ["devskim"] = new Dictionary<string, object?>
            {
                ["ignoreGlobs"] = new List<object?>(),
                ["ignoreRuleIds"] = new List<object?>(),
            },
            ["images"] = new Dictionary<string, object?>
            {
                ["semgrep"] = "semgrep/semgrep:latest",
                ["gitleaks"] = "zricethezav/gitleaks:latest",
                ["osv"] = "ghcr.io/google/osv-scanner:latest",
            },
            ["versions"] = new Dictionary<string, object?>
            {
                ["devskim"] = string.Empty,
                ["sonarScanner"] = string.Empty,
                ["dotnetCoverage"] = string.Empty,
            },
        };
    }

    /// <summary>Merges the project's existing committed config over <paramref name="config"/>, if any.</summary>
    private static void MergeExistingConfig(IDictionary<string, object?> config, string securityDirectory)
    {
        var existingPath = Path.Combine(securityDirectory, ConfigFileName);
        if (!File.Exists(existingPath))
        {
            return;
        }

        var existing = ScannerSupport.ParseJsonObject(File.ReadAllText(existingPath));
        StripLegacyKeys(existing);
        MergeConfig(config, existing);
    }

    /// <summary>Removes every <see cref="LegacyConfigPaths"/> entry from <paramref name="config"/> in place.</summary>
    internal static void StripLegacyKeys(IDictionary<string, object?> config)
    {
        foreach (var path in LegacyConfigPaths)
        {
            var parent = config;
            for (var i = 0; i < path.Length - 1 && parent is not null; i++)
            {
                parent = TryGetDict(parent, path[i]);
            }

            parent?.Remove(path[^1]);
        }
    }

    /// <summary>
    /// Config maps whose keys are user-chosen (not a fixed schema), so they replace the default
    /// wholesale instead of being key-checked by <see cref="MergeConfig"/>; their content is
    /// validated in <see cref="ValidateConfig"/> instead.
    /// </summary>
    private static readonly HashSet<string> FreeFormMapKeys = new(StringComparer.Ordinal) { "testEnvironment" };

    /// <summary>Deep merge: every key in <paramref name="extra"/> must already exist in <paramref name="baseConfig"/>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="extra"/> has an unknown key.</exception>
    private static void MergeConfig(IDictionary<string, object?> baseConfig, IDictionary<string, object?> extra)
    {
        foreach (var (key, value) in extra)
        {
            if (!baseConfig.ContainsKey(key))
            {
                throw new InvalidOperationException($"Unknown config key: {key}");
            }

            if (FreeFormMapKeys.Contains(key))
            {
                baseConfig[key] = value;
            }
            else if (baseConfig[key] is IDictionary<string, object?> baseNested && value is IDictionary<string, object?> extraNested)
            {
                MergeConfig(baseNested, extraNested);
            }
            else
            {
                baseConfig[key] = value;
            }
        }
    }

    private static void ValidateConfig(IDictionary<string, object?> config, ProjectScanResult scan)
    {
        if (GetLong(config, "schemaVersion") != 1)
        {
            throw new InvalidOperationException("Unsupported configuration schema.");
        }

        var tools = GetToolsDict(config);
        foreach (var (key, value) in tools)
        {
            if (value is not bool)
            {
                throw new InvalidOperationException($"tools.{key} must be a JSON boolean.");
            }
        }

        var dotnetTargets = GetStringList(config, "dotnetTargets");
        var frontendTargets = GetStringList(config, "frontendTargets");

        if ((GetBool(tools, "analyzers") || GetBool(tools, "nuget")) && dotnetTargets.Count == 0)
        {
            throw new InvalidOperationException("Select at least one .NET target.");
        }

        if (GetBool(tools, "npm") && frontendTargets.Count == 0)
        {
            throw new InvalidOperationException("Select at least one npm target.");
        }

        var semgrepScope = GetString(config, "semgrepScope");
        if (semgrepScope is not ("repository" or "frontend"))
        {
            throw new InvalidOperationException("semgrepScope must be repository or frontend.");
        }

        if (GetBool(tools, "semgrep") && semgrepScope == "frontend" && frontendTargets.Count == 0)
        {
            throw new InvalidOperationException("Frontend Semgrep scope requires at least one selected frontend target.");
        }

        var sdkChannel = GetString(config, "sdkChannel");
        if (sdkChannel is not ("8.0" or "9.0" or "10.0"))
        {
            throw new InvalidOperationException("Unsupported SDK channel. Install the actual project SDK manually and disable prerequisite installation.");
        }

        var sonar = GetDict(config, "sonar");
        var projectKey = GetString(sonar, "projectKey") ?? string.Empty;
        if (!System.Text.RegularExpressions.Regex.IsMatch(projectKey, "^[A-Za-z0-9_.:-]+$"))
        {
            throw new InvalidOperationException("Invalid Sonar project key.");
        }

        if (GetBool(tools, "sonar"))
        {
            if (dotnetTargets.Count == 0)
            {
                throw new InvalidOperationException("This installer Sonar upload requires a .NET target; standalone frontend Sonar uses a separate CLI setup.");
            }

            var dotnetTarget = GetString(sonar, "dotnetTarget");
            if (string.IsNullOrEmpty(dotnetTarget))
            {
                throw new InvalidOperationException("A Sonar .NET target is required.");
            }
        }

        ValidateSonarCoverage(sonar);
        ValidateDevSkim(GetDict(config, "devskim"));
        ValidatePipeline(config);
        ValidateToolkitRelease(GetDict(config, "toolkitRelease"));

        if (config.TryGetValue("azureVmImage", out var vmImage)
            && (vmImage is not string image || !System.Text.RegularExpressions.Regex.IsMatch(image, "^[A-Za-z0-9._-]*$")))
        {
            throw new InvalidOperationException("azureVmImage must be empty or a hosted image name such as ubuntu-latest.");
        }

        if (!GetBool(tools, "gitleaks"))
        {
            config["installHook"] = false;
        }

        foreach (var key in new[] { "installPrerequisites", "downloadSemgrepRules", "installHook", "installVSCodeExtensions", "generatePipeline", "pipelineHistory", "privateNuGet", "githubCodeScanning" })
        {
            if (config[key] is not bool)
            {
                throw new InvalidOperationException($"{key} must be a JSON boolean.");
            }
        }

        _ = scan;
    }

    /// <summary>
    /// toolkitRelease.version / baseUrl end up verbatim in the generated pipeline scripts (download
    /// URL), so both are restricted to characters that are inert in bash, cmd and YAML.
    /// </summary>
    private static void ValidateToolkitRelease(IDictionary<string, object?> toolkitRelease)
    {
        if (toolkitRelease.TryGetValue("version", out var versionValue) && versionValue is not (null or string))
        {
            throw new InvalidOperationException("toolkitRelease.version must be a string.");
        }

        var version = GetString(toolkitRelease, "version") ?? string.Empty;
        if (version.Length > 0
            && !System.Text.RegularExpressions.Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$"))
        {
            throw new InvalidOperationException("toolkitRelease.version must be empty or a release version such as 1.2.0 (without the leading v).");
        }

        var baseUrl = GetString(toolkitRelease, "baseUrl") ?? string.Empty;
        if (!System.Text.RegularExpressions.Regex.IsMatch(baseUrl, "^https://[A-Za-z0-9.-]+(:[0-9]+)?(/[A-Za-z0-9._~%+-]+)*/?$"))
        {
            throw new InvalidOperationException("toolkitRelease.baseUrl must be an https URL (letters, digits and . _ ~ % + - in the path).");
        }
    }

    /// <summary>
    /// pipelineProviders selects which CI definitions <c>generatePipeline</c> writes. The GitHub
    /// keys are only checked when the GitHub workflow is actually generated; every value checked
    /// here is written verbatim (quoted) into the workflow, hence the strict patterns.
    /// </summary>
    private static void ValidatePipeline(IDictionary<string, object?> config)
    {
        if (!config.TryGetValue("pipelineProviders", out var rawProviders)
            || rawProviders is not IEnumerable<object?> providerValues
            || providerValues.Any(p => p is not string))
        {
            throw new InvalidOperationException("pipelineProviders must be a JSON array of strings.");
        }

        var providers = GetStringList(config, "pipelineProviders");
        if (providers.Any(p => !SupportedPipelineProviders.Contains(p, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                $"pipelineProviders entries must be one of: {string.Join(", ", SupportedPipelineProviders)}.");
        }

        if (providers.Distinct(StringComparer.Ordinal).Count() != providers.Count)
        {
            throw new InvalidOperationException("pipelineProviders must not contain duplicates.");
        }

        if (!GetBool(config, "generatePipeline"))
        {
            return;
        }

        if (providers.Count == 0)
        {
            throw new InvalidOperationException("pipelineProviders must list at least one provider when generatePipeline is true.");
        }

        if (!providers.Contains("github"))
        {
            return;
        }

        if (!config.TryGetValue("githubRunsOn", out var rawLabels)
            || rawLabels is not IEnumerable<object?> labelValues
            || labelValues.Any(l => l is not string label || !System.Text.RegularExpressions.Regex.IsMatch(label, "^[A-Za-z0-9_.-]+$"))
            || GetStringList(config, "githubRunsOn").Count == 0)
        {
            throw new InvalidOperationException(
                "githubRunsOn must be a non-empty JSON array of runner labels (letters, digits, '_', '.' and '-').");
        }

        if (config.TryGetValue("githubSchedule", out var rawSchedule) && rawSchedule is not null)
        {
            if (rawSchedule is not string schedule
                || (schedule.Length > 0
                    && !System.Text.RegularExpressions.Regex.IsMatch(schedule, "^[0-9*/,-]+( [0-9*/,-]+){4}$")))
            {
                throw new InvalidOperationException(
                    "githubSchedule must be empty or a five-field cron expression (digits, '*', '/', ',' and '-').");
            }
        }

        // GitHub secret names are stricter than Azure variable names: no '.', and the GITHUB_
        // prefix is reserved (such a secret cannot exist, so the mapping would silently be empty).
        var sonar = GetDict(config, "sonar");
        if (GetBool(sonar, "coverage") && TryGetDict(sonar, "testEnvironment") is { } testEnvironment)
        {
            foreach (var (name, variable) in testEnvironment)
            {
                if (variable is not string secretName
                    || !System.Text.RegularExpressions.Regex.IsMatch(secretName, "^[A-Za-z_][A-Za-z0-9_]*$")
                    || secretName.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"sonar.testEnvironment.{name} must name a GitHub secret (letters, digits and '_', not starting with GITHUB_).");
                }
            }
        }
    }

    /// <summary>
    /// devskim.ignoreGlobs / ignoreRuleIds end up comma-joined on the DevSkim command line, so a
    /// glob must not contain a comma, and a rule id must look like one (DS + digits).
    /// </summary>
    private static void ValidateDevSkim(IDictionary<string, object?> devskim)
    {
        foreach (var glob in GetStringList(devskim, "ignoreGlobs"))
        {
            if (glob.Length == 0 || glob.Any(c => char.IsControl(c) || c is ',' or '"'))
            {
                throw new InvalidOperationException($"Invalid devskim.ignoreGlobs entry: {glob}");
            }
        }

        foreach (var ruleId in GetStringList(devskim, "ignoreRuleIds"))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(ruleId, "^DS[0-9]+$"))
            {
                throw new InvalidOperationException($"Invalid devskim.ignoreRuleIds entry: {ruleId}");
            }
        }
    }

    /// <summary>
    /// sonar.coverage / testFilter / testEnvironment. testEnvironment maps an env var name (as the
    /// test process reads it) to an Azure pipeline variable NAME — never a value, so a secret
    /// (e.g. a test DB connection string) stays in the pipeline's secret store. Both sides are
    /// written verbatim into the generated YAML, hence the strict identifier patterns.
    /// </summary>
    private static void ValidateSonarCoverage(IDictionary<string, object?> sonar)
    {
        if (sonar.TryGetValue("coverage", out var coverage) && coverage is not bool)
        {
            throw new InvalidOperationException("sonar.coverage must be a JSON boolean.");
        }

        if (sonar.TryGetValue("testFilter", out var filterValue) && filterValue is not null)
        {
            if (filterValue is not string filter || filter.Any(c => char.IsControl(c) || c == '"'))
            {
                throw new InvalidOperationException("sonar.testFilter must be a string without quotes or control characters.");
            }
        }

        if (!sonar.TryGetValue("testEnvironment", out var environmentValue) || environmentValue is null)
        {
            return;
        }

        if (environmentValue is not IDictionary<string, object?> environment)
        {
            throw new InvalidOperationException("sonar.testEnvironment must be a JSON object.");
        }

        foreach (var (name, variable) in environment)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$")
                || string.Equals(name, "SONAR_TOKEN", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Invalid sonar.testEnvironment variable name: {name}");
            }

            if (variable is not string pipelineVariable
                || !System.Text.RegularExpressions.Regex.IsMatch(pipelineVariable, "^[A-Za-z_][A-Za-z0-9_.]*$"))
            {
                throw new InvalidOperationException(
                    $"sonar.testEnvironment.{name} must name an Azure pipeline variable (letters, digits, '_' and '.').");
            }
        }
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

    private static void WriteIgnoreBlocks(BackupService backup, string root, IDictionary<string, object?> config)
    {
        var gitignorePath = Path.Combine(root, ".gitignore");
        var gitignoreBody = string.Join('\n', new[]
        {
            ".security/bin/",
            ".security/npm-tools/",
            ".security/results/",
            ".security/backups/",
            ".security/install-state.json",
            ".security/pending-actions.md",
            $".security/{LocalConfigFileName}",
            // Leftover of older installs (machine-local Sonar pins); kept ignored so a stale copy is
            // never committed by accident.
            ".security/security.lock.local.json",
            ".security/vendor/",
            ".sonarqube/",
        });
        var existingGitignore = File.Exists(gitignorePath) ? File.ReadAllText(gitignorePath) : string.Empty;
        backup.SaveManagedText(gitignorePath, ManagedBlockWriter.UpdateBlock(existingGitignore, gitignoreBody));

        if (GetBool(GetToolsDict(config), "semgrep"))
        {
            var semgrepIgnorePath = Path.Combine(root, ".semgrepignore");
            var semgrepIgnoreBody = string.Join('\n', new[]
            {
                "**/bin/**", "**/obj/**", "**/node_modules/**", "**/dist/**", "**/coverage/**", ".security/**",
            });
            var existingSemgrepIgnore = File.Exists(semgrepIgnorePath) ? File.ReadAllText(semgrepIgnorePath) : string.Empty;
            backup.SaveManagedText(semgrepIgnorePath, ManagedBlockWriter.UpdateBlock(existingSemgrepIgnore, semgrepIgnoreBody));
        }
    }

    private static void WireDotNetProjects(BackupService backup, string root, ProjectScanResult scan, IDictionary<string, object?> config)
    {
        var tools = GetToolsDict(config);
        if (!GetBool(tools, "analyzers") && !GetBool(tools, "nuget"))
        {
            return;
        }

        var securityDirectory = Path.Combine(root, ".security");
        var propsPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var editorConfigPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in scan.CsprojFiles)
        {
            var directory = Path.GetDirectoryName(project.FullPath)!;
            string? nearest = null;
            var cursor = directory;
            while (true)
            {
                var candidate = Path.Combine(cursor, "Directory.Build.props");
                if (File.Exists(candidate))
                {
                    nearest = candidate;
                    break;
                }

                if (string.Equals(cursor, root, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                cursor = Path.GetDirectoryName(cursor) ?? root;
            }

            nearest ??= Path.Combine(root, "Directory.Build.props");
            propsPaths.Add(nearest);

            if (GetBool(tools, "analyzers"))
            {
                editorConfigPaths.Add(Path.Combine(directory, ".editorconfig"));
            }
        }

        foreach (var propsPath in propsPaths)
        {
            var updated = ManagedBlockWriter.AddPropsImport(propsPath, securityDirectory);
            if (updated is not null)
            {
                backup.SaveManagedText(propsPath, updated);
            }
        }

        var body = "[*.cs]\n" + string.Join('\n', AnalyzerDiagnostics.Select(code => $"dotnet_diagnostic.{code}.severity = warning"));
        foreach (var editorConfigPath in editorConfigPaths)
        {
            var existing = File.Exists(editorConfigPath) ? File.ReadAllText(editorConfigPath) : string.Empty;
            backup.SaveManagedText(editorConfigPath, ManagedBlockWriter.UpdateBlock(existing, body));
        }
    }

    /// <summary>
    /// Builds the committed lock's tool entries — CI-relevant tools only (semgrep, gitleaks, osv,
    /// devskim, sonarScanner, dotnetCoverage). The shared Sonar/PostgreSQL server images are
    /// machine-level infra, pinned in that stack's own .env (<c>security-toolkit infra install</c>),
    /// never here. Reads its own prior state directly (not passed in) so callers don't have to
    /// remember to read/thread it.
    /// </summary>
    private static IDictionary<string, object?> BuildLock(IDictionary<string, object?> config, string securityDirectory)
    {
        var tools = GetToolsDict(config);
        var images = GetDict(config, "images");
        var versions = GetDict(config, "versions");
        var oldLock = ReadExistingLock(securityDirectory);
        var oldTools = oldLock is not null ? GetDict(oldLock, "tools") : new Dictionary<string, object?>();

        var lockTools = new Dictionary<string, object?>();

        foreach (var name in new[] { "semgrep", "gitleaks", "osv" })
        {
            var enabled = GetBool(tools, name);
            var image = GetString(images, name) ?? string.Empty;
            var wasReady = false;

            if (TryGetDict(oldTools, name) is { } oldTool
                && string.Equals(GetString(oldTool, "sourceImage"), image, StringComparison.Ordinal)
                && GetBool(oldTool, "ready"))
            {
                image = GetString(oldTool, "image") ?? image;
                wasReady = true;
            }

            lockTools[name] = new Dictionary<string, object?>
            {
                ["enabled"] = enabled,
                ["image"] = image,
                ["sourceImage"] = GetString(images, name) ?? string.Empty,
                ["ready"] = enabled && wasReady,
            };
        }

        foreach (var name in new[] { "devskim", "sonarScanner", "dotnetCoverage" })
        {
            var enabled = name switch
            {
                "sonarScanner" => GetBool(tools, "sonar"),
                "dotnetCoverage" => GetBool(tools, "sonar") && GetBool(GetDict(config, "sonar"), "coverage"),
                _ => GetBool(tools, name),
            };
            var package = name switch
            {
                "devskim" => "Microsoft.CST.DevSkim.CLI",
                "sonarScanner" => "dotnet-sonarscanner",
                _ => "dotnet-coverage",
            };

            var version = GetString(versions, name) ?? string.Empty;
            if (string.IsNullOrEmpty(version) && TryGetDict(oldTools, name) is { } oldTool2)
            {
                version = GetString(oldTool2, "version") ?? string.Empty;
            }

            var wasReady = TryGetDict(oldTools, name) is { } oldTool3
                && GetBool(oldTool3, "ready")
                && string.Equals(GetString(oldTool3, "version"), version, StringComparison.Ordinal);

            lockTools[name] = new Dictionary<string, object?>
            {
                ["enabled"] = enabled,
                ["package"] = package,
                ["version"] = version,
                ["ready"] = enabled && wasReady,
            };
        }

        var lockData = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1L,
            ["tools"] = lockTools,
        };

        // Only present when enabled, so a project that never uses it keeps a byte-identical lock.
        var toolkitRelease = GetDict(config, "toolkitRelease");
        var releaseVersion = GetString(toolkitRelease, "version") ?? string.Empty;
        if (releaseVersion.Length > 0)
        {
            var baseUrl = (GetString(toolkitRelease, "baseUrl") ?? DefaultToolkitReleaseBaseUrl).TrimEnd('/');
            var hashes = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (oldLock is not null
                && TryGetDict(oldLock, "toolkitRelease") is { } oldRelease
                && string.Equals(GetString(oldRelease, "version"), releaseVersion, StringComparison.Ordinal)
                && string.Equals(GetString(oldRelease, "baseUrl"), baseUrl, StringComparison.Ordinal)
                && TryGetDict(oldRelease, "sha256") is { } oldHashes
                && ToolkitReleaseResolver.AssetNames.Keys.All(rid => GetString(oldHashes, rid) is { Length: 64 }))
            {
                foreach (var rid in ToolkitReleaseResolver.AssetNames.Keys)
                {
                    hashes[rid] = GetString(oldHashes, rid);
                }
            }

            lockData["toolkitRelease"] = new Dictionary<string, object?>
            {
                ["version"] = releaseVersion,
                ["baseUrl"] = baseUrl,
                ["sha256"] = hashes,
            };
        }

        return lockData;
    }

    /// <summary>Reads the committed lock, if it exists yet.</summary>
    private static IDictionary<string, object?>? ReadExistingLock(string securityDirectory)
    {
        try
        {
            return ToolkitJson.ReadLock(securityDirectory);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every scanner the pipeline can generate a step for, in the order
    /// <c>ScanCommandFactory</c>'s scanner list runs them. Each entry's display name is used both
    /// in the generated step's <c>displayName</c> and (lowercased key) as the "tools.&lt;key&gt;"
    /// config lookup and the CLI's <c>--tool</c> value.
    /// </summary>
    private static readonly string[] PipelineToolOrder =
        { "analyzers", "nuget", "npm", "semgrep", "gitleaks", "osv", "devskim", "sonar" };

    private static readonly string[] SupportedPipelineProviders = { "azure", "github" };

    private const string AzurePipelineFileName = "azure-security-toolkit.yml";

    /// <summary>Repository-relative path of the generated GitHub Actions workflow (forward slashes).</summary>
    public const string GitHubWorkflowRelativePath = ".github/workflows/security-toolkit.yml";

    // Actions are pinned to a full commit SHA (a tag can be moved), the same reason the scanner
    // images are digest-pinned; the trailing comment lets Dependabot/humans see the version.
    private const string CheckoutAction = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1";
    private const string UploadArtifactAction = "actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9 # v7.0.2";
    private const string UploadSarifAction = "github/codeql-action/upload-sarif@1fc58d04a4d4374f921bdac573c3811c240f9c26 # v4.39.2";

    private static void WritePipelineFiles(
        BackupService backup, string root, IDictionary<string, object?> config,
        IDictionary<string, object?> lockData, List<string> messages)
    {
        var providers = GetStringList(config, "pipelineProviders");
        var release = ToolkitReleasePin.FromLock(lockData);
        if (release is { IsPinned: false })
        {
            messages.Add(
                $"toolkitRelease: a security-toolkit v{release.Version} checksumjai még nincsenek rögzítve; a generált pipeline " +
                "telepítő lépése ezért hibával áll meg. Futtass egy teljes (--configure-only nélküli) installt.");
        }

        if (providers.Contains("azure"))
        {
            WriteAzurePipelineYaml(backup, root, config, release);
        }
        else if (File.Exists(Path.Combine(root, AzurePipelineFileName)))
        {
            messages.Add($"A korábban generált {AzurePipelineFileName} már nem frissül (az azure nincs a pipelineProviders-ben). Ha nincs rá szükség, töröld kézzel.");
        }

        if (providers.Contains("github"))
        {
            WriteGitHubWorkflow(backup, root, config, release);

            if (GetBool(config, "privateNuGet") || !string.IsNullOrEmpty(GetString(config, "privateNpmRc")))
            {
                messages.Add("GitHub: a privateNuGet/privateNpmRc csak az Azure pipeline-ba ír hitelesítő lépést; a GitHub workflow-ban a privát feed elérését a runneren kell megoldani.");
            }
        }
        else if (File.Exists(Path.Combine(root, GitHubWorkflowRelativePath)))
        {
            messages.Add($"A korábban generált {GitHubWorkflowRelativePath} már nem frissül (a github nincs a pipelineProviders-ben). Ha nincs rá szükség, töröld kézzel.");
        }
    }

    /// <summary>One scan step per enabled tool, in <see cref="PipelineToolOrder"/>, shared by every provider.</summary>
    /// <param name="TrailingArguments">CLI arguments written after --run-id (keeps the historical Azure order).</param>
    /// <param name="SecretEnvironment">Step env var name → pipeline variable/secret NAME.</param>
    private sealed record PipelineScanStep(string Tool, string TrailingArguments, IReadOnlyList<KeyValuePair<string, string>> SecretEnvironment);

    private static List<PipelineScanStep> BuildPipelineScanSteps(IDictionary<string, object?> config)
    {
        var tools = GetToolsDict(config);
        var historyEnabled = GetBool(config, "pipelineHistory");
        var steps = new List<PipelineScanStep>();

        foreach (var tool in PipelineToolOrder)
        {
            if (!GetBool(tools, tool))
            {
                continue;
            }

            var arguments = string.Empty;
            if (tool == "gitleaks" && historyEnabled)
            {
                arguments += " --history";
            }

            var environment = new List<KeyValuePair<string, string>>();
            if (tool == "sonar")
            {
                arguments += " --include-sonar";
                environment.Add(new("SONAR_TOKEN", "SONAR_TOKEN"));

                // Secret pipeline variables never reach a step's environment implicitly; each one the
                // coverage tests need must be mapped here (names validated in ValidateSonarCoverage).
                var sonar = GetDict(config, "sonar");
                if (GetBool(sonar, "coverage") && TryGetDict(sonar, "testEnvironment") is { } testEnvironment)
                {
                    foreach (var (name, variable) in testEnvironment.OrderBy(e => e.Key, StringComparer.Ordinal))
                    {
                        environment.Add(new(name, (string)variable!));
                    }
                }
            }

            steps.Add(new PipelineScanStep(tool, arguments, environment));
        }

        return steps;
    }

    /// <summary>
    /// GitHub Actions counterpart of <see cref="WriteAzurePipelineYaml"/>: same provision step, one
    /// scan step per tool sharing one --run-id, results uploaded as an artifact. Runs on a
    /// self-hosted runner with the CLI on PATH (no shell is pinned: the steps are plain CLI calls,
    /// and a Windows runner without pwsh falls back to the built-in Windows PowerShell).
    /// </summary>
    /// <summary>The lock's <c>toolkitRelease</c> pin; null when the pipelines use the CLI already on PATH.</summary>
    private sealed record ToolkitReleasePin(string Version, string BaseUrl, IReadOnlyDictionary<string, string> Sha256)
    {
        public bool IsPinned => ToolkitReleaseResolver.AssetNames.Keys.All(Sha256.ContainsKey);

        public string AssetUrl(string rid)
            => ToolkitReleaseResolver.DownloadUrl(BaseUrl, Version, ToolkitReleaseResolver.AssetNames[rid]);

        public static ToolkitReleasePin? FromLock(IDictionary<string, object?> lockData)
        {
            if (TryGetDict(lockData, "toolkitRelease") is not { } release)
            {
                return null;
            }

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            if (TryGetDict(release, "sha256") is { } sha256)
            {
                foreach (var (rid, value) in sha256)
                {
                    if (value is string hash && System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-f]{64}$"))
                    {
                        hashes[rid] = hash;
                    }
                }
            }

            return new ToolkitReleasePin(GetString(release, "version")!, GetString(release, "baseUrl")!, hashes);
        }
    }

    /// <summary>
    /// The pipeline step(s) that put the pinned release on PATH, before provision: one bash script
    /// (Linux x64/arm64) and one cmd script (Windows x64), each selected by the agent OS, so neither
    /// needs PowerShell. An unpinned release yields a single step that fails with an explanation.
    /// </summary>
    /// <param name="binDirectory">Download directory, as the CI platform's expression for its temp dir.</param>
    /// <param name="bashPathCommand">bash line that prepends <c>$dir</c> to PATH for later steps.</param>
    /// <param name="cmdPathCommand">cmd line that prepends <c>%dir%</c> to PATH for later steps.</param>
    private static (IReadOnlyList<string> Bash, IReadOnlyList<string> Cmd)? BuildToolkitInstallScripts(
        ToolkitReleasePin release, string binDirectory, string bashPathCommand, string cmdPathCommand)
    {
        if (!release.IsPinned)
        {
            return null;
        }

        var bash = new[]
        {
            "set -euo pipefail",
            "case \"$(uname -m)\" in",
            $"  x86_64) rid=linux-x64; sum={release.Sha256["linux-x64"]} ;;",
            $"  aarch64|arm64) rid=linux-arm64; sum={release.Sha256["linux-arm64"]} ;;",
            "  *) echo \"security-toolkit: unsupported architecture $(uname -m)\" >&2; exit 2 ;;",
            "esac",
            $"dir=\"{binDirectory}/security-toolkit-bin\"",
            "mkdir -p \"$dir\"",
            $"curl -fsSL --retry 3 -o \"$dir/security-toolkit\" \"{release.BaseUrl}/v{release.Version}/security-toolkit-$rid\"",
            "echo \"$sum  $dir/security-toolkit\" | sha256sum -c --quiet -",
            "chmod +x \"$dir/security-toolkit\"",
            bashPathCommand,
        };

        var cmd = new[]
        {
            $"set \"dir={binDirectory}\\security-toolkit-bin\"",
            "if not exist \"%dir%\" mkdir \"%dir%\" || exit /b 2",
            $"curl.exe -fsSL --retry 3 -o \"%dir%\\security-toolkit.exe\" \"{release.AssetUrl("win-x64")}\" || exit /b 2",
            $"certutil -hashfile \"%dir%\\security-toolkit.exe\" SHA256 | findstr /i /x \"{release.Sha256["win-x64"]}\" >nul || (echo security-toolkit: checksum mismatch & exit /b 2)",
            cmdPathCommand,
        };

        return (bash, cmd);
    }

    private static string IndentBlock(IEnumerable<string> lines, int spaces)
        => string.Concat(lines.Select(l => new string(' ', spaces) + l + "\n"));

    private static void WriteGitHubWorkflow(
        BackupService backup, string root, IDictionary<string, object?> config, ToolkitReleasePin? release)
    {
        const string workspace = "${{ github.workspace }}";
        // run_attempt: a re-run keeps run_id, and must not merge into (or collide with) the first attempt.
        const string runId = "${{ github.run_id }}-${{ github.run_attempt }}";
        const string notCancelled = "${{ !cancelled() }}";

        var codeScanning = GetBool(config, "githubCodeScanning");
        var schedule = GetString(config, "githubSchedule") ?? string.Empty;
        var runsOn = string.Join(", ", GetStringList(config, "githubRunsOn").Select(YamlQuote));

        var yaml = "name: Security Toolkit\n" +
                   "on:\n" +
                   "  workflow_dispatch:\n";
        if (schedule.Length > 0)
        {
            yaml += $"  schedule:\n    - cron: {YamlQuote(schedule)}\n";
        }

        yaml += "permissions:\n" +
                "  contents: read\n";
        if (codeScanning)
        {
            yaml += "  security-events: write\n" +
                    "  actions: read\n";
        }

        yaml += "jobs:\n" +
                "  security:\n" +
                $"    runs-on: [{runsOn}]\n" +
                "    steps:\n" +
                $"      - uses: {CheckoutAction}\n" +
                "        with:\n" +
                "          fetch-depth: 0\n" +
                "          persist-credentials: false\n";

        if (release is not null)
        {
            var scripts = BuildToolkitInstallScripts(
                release, "${{ runner.temp }}", "echo \"$dir\" >> \"$GITHUB_PATH\"", ">>\"%GITHUB_PATH%\" echo %dir%");
            if (scripts is { } s)
            {
                yaml += "\n" +
                        $"      - name: Install security-toolkit {release.Version} (Linux)\n" +
                        "        if: runner.os == 'Linux'\n" +
                        "        shell: bash\n" +
                        "        run: |\n" +
                        IndentBlock(s.Bash, 10) +
                        "\n" +
                        $"      - name: Install security-toolkit {release.Version} (Windows)\n" +
                        "        if: runner.os == 'Windows'\n" +
                        "        shell: cmd\n" +
                        "        run: |\n" +
                        IndentBlock(s.Cmd, 10);
            }
            else
            {
                yaml += "\n" +
                        $"      - name: Install security-toolkit {release.Version}\n" +
                        "        run: |\n" +
                        $"          echo security-toolkit v{release.Version} is not pinned in .security/security.lock.json: run a full install first.\n" +
                        "          exit 2\n";
            }
        }

        yaml += "\n" +
                "      - name: Initialize pinned security tools\n" +
                $"        run: security-toolkit provision --project-root \"{workspace}\" --require-pinned\n";

        foreach (var step in BuildPipelineScanSteps(config))
        {
            yaml += "\n" +
                    $"      - name: 'Security scan: {step.Tool}'\n" +
                    $"        if: {notCancelled}\n" +
                    $"        run: security-toolkit scan --project-root \"{workspace}\" --tool {step.Tool} --mode Report --run-id {runId}{step.TrailingArguments}\n";
            if (step.SecretEnvironment.Count > 0)
            {
                yaml += "        env:\n";
                foreach (var (name, secret) in step.SecretEnvironment)
                {
                    yaml += $"          {name}: ${{{{ secrets.{secret} }}}}\n";
                }
            }
        }

        yaml += "\n" +
                "      - name: Upload security results\n" +
                $"        if: {notCancelled}\n" +
                $"        uses: {UploadArtifactAction}\n" +
                "        with:\n" +
                "          name: security-results\n" +
                "          path: .security/results\n";

        if (codeScanning)
        {
            yaml += "\n" +
                    "      - name: Upload SARIF reports to code scanning\n" +
                    $"        if: {notCancelled}\n" +
                    $"        uses: {UploadSarifAction}\n" +
                    "        with:\n" +
                    $"          sarif_file: .security/results/{runId}\n";
        }

        backup.SaveManagedText(Path.Combine(root, ".github", "workflows", "security-toolkit.yml"), yaml, owned: true);
    }

    private static void WriteAzurePipelineYaml(
        BackupService backup, string root, IDictionary<string, object?> config, ToolkitReleasePin? release)
    {
        // A Microsoft-hosted agent is selected by image, a self-hosted (or scale-set) one by pool name.
        var vmImage = GetString(config, "azureVmImage") ?? string.Empty;
        var pool = vmImage.Length > 0
            ? $"pool:\n  vmImage: {YamlQuote(vmImage)}\n"
            : $"pool:\n  name: {YamlQuote(GetString(config, "agentPool") ?? string.Empty)}\n";
        var yaml = "trigger: none\n" +
                   "pr: none\n" +
                   pool +
                   "variables:\n" +
                   "  securityRunId: $(Build.BuildId)\n" +
                   "steps:\n" +
                   "- checkout: self\n" +
                   "  clean: true\n" +
                   "  fetchDepth: 0\n" +
                   "  persistCredentials: false\n";

        if (GetBool(config, "privateNuGet"))
        {
            yaml += "\n- task: NuGetAuthenticate@1\n";
        }

        var privateNpmRc = GetString(config, "privateNpmRc");
        if (!string.IsNullOrEmpty(privateNpmRc))
        {
            yaml += $"\n- task: npmAuthenticate@0\n  inputs:\n    workingFile: {YamlQuote(privateNpmRc)}\n";
        }

        if (release is not null)
        {
            var scripts = BuildToolkitInstallScripts(
                release, "$(Agent.TempDirectory)", "echo \"##vso[task.prependpath]$dir\"", "echo ##vso[task.prependpath]%dir%");
            if (scripts is { } s)
            {
                yaml += "\n" +
                        "- bash: |\n" +
                        IndentBlock(s.Bash, 4) +
                        $"  displayName: Install security-toolkit {release.Version} (Linux)\n" +
                        "  condition: and(succeeded(), eq(variables['Agent.OS'], 'Linux'))\n" +
                        "\n" +
                        "- script: |\n" +
                        IndentBlock(s.Cmd, 4) +
                        $"  displayName: Install security-toolkit {release.Version} (Windows)\n" +
                        "  condition: and(succeeded(), eq(variables['Agent.OS'], 'Windows_NT'))\n";
            }
            else
            {
                yaml += "\n" +
                        "- script: |\n" +
                        $"    echo security-toolkit v{release.Version} is not pinned in .security/security.lock.json: run a full install first.\n" +
                        "    exit 2\n" +
                        $"  displayName: Install security-toolkit {release.Version}\n";
            }
        }

        yaml += "\n" +
                "- script: security-toolkit provision --project-root \"$(Build.SourcesDirectory)\" --require-pinned\n" +
                "  displayName: Initialize pinned security tools\n";

        var tools = GetToolsDict(config);

        foreach (var step in BuildPipelineScanSteps(config))
        {
            // Quoted: an agent work directory containing spaces would otherwise split the path into
            // separate CLI arguments (the git hook quotes "$repo_root" the same way).
            var envBlock = string.Empty;
            if (step.SecretEnvironment.Count > 0)
            {
                envBlock = "  env:\n" + string.Concat(step.SecretEnvironment.Select(e => $"    {e.Key}: $({e.Value})\n"));
            }

            yaml += "\n" +
                    $"- script: security-toolkit scan --project-root \"$(Build.SourcesDirectory)\" --tool {step.Tool} --mode Report --run-id $(securityRunId){step.TrailingArguments}\n" +
                    $"  displayName: 'Security scan: {step.Tool}'\n" +
                    "  condition: always()\n" +
                    envBlock;
        }

        // The Sonar step's coverage reports (.NET vscoveragexml + one lcov per Angular project) are
        // also published to the run's Code Coverage tab; PublishCodeCoverageResults@2 reads both
        // formats natively and merges them into one report.
        if (GetBool(tools, "sonar") && GetBool(GetDict(config, "sonar"), "coverage"))
        {
            yaml += "\n" +
                    "- task: PublishCodeCoverageResults@2\n" +
                    "  displayName: Publish code coverage (Code Coverage tab)\n" +
                    "  condition: always()\n" +
                    "  inputs:\n" +
                    // One pattern per line: the task's findMatch does not expand {a,b} braces.
                    "    summaryFileLocation: |\n" +
                    $"      $(Build.SourcesDirectory)/.security/results/$(securityRunId)/{SonarScanner.DotNetCoverageFileName}\n" +
                    "      $(Build.SourcesDirectory)/.security/results/$(securityRunId)/frontend-coverage-*/lcov.info\n" +
                    "    failIfCoverageEmpty: false\n";
        }

        yaml += "\n" +
                "- task: PublishPipelineArtifact@1\n" +
                "  condition: always()\n" +
                "  inputs:\n" +
                "    targetPath: $(Build.SourcesDirectory)/.security/results\n" +
                "    artifact: security-results\n" +
                "\n" +
                "- task: CopyFiles@2\n" +
                "  displayName: Collect SARIF reports for the Scans tab\n" +
                "  condition: always()\n" +
                "  inputs:\n" +
                "    sourceFolder: $(Build.SourcesDirectory)/.security/results\n" +
                "    contents: '**/*.sarif'\n" +
                "    targetFolder: $(Build.ArtifactStagingDirectory)/CodeAnalysisLogs\n" +
                "    flattenFolders: true\n" +
                "\n" +
                "- task: PublishBuildArtifacts@1\n" +
                "  displayName: Publish SARIF reports (SARIF SAST Scans Tab)\n" +
                "  condition: always()\n" +
                "  inputs:\n" +
                "    PathtoPublish: $(Build.ArtifactStagingDirectory)/CodeAnalysisLogs\n" +
                "    ArtifactName: CodeAnalysisLogs\n";

        backup.SaveManagedText(Path.Combine(root, AzurePipelineFileName), yaml, owned: true);
    }

    private static string YamlQuote(string text)
    {
        if (text.Contains('\r') || text.Contains('\n') || text.Contains('\0'))
        {
            throw new InvalidOperationException("YAML scalar must not contain control characters.");
        }

        return "'" + text.Replace("'", "''") + "'";
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

    private static IDictionary<string, object?> GetToolsDict(IDictionary<string, object?> config) => GetDict(config, "tools");

    private static IDictionary<string, object?>? TryGetDict(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) ? value as IDictionary<string, object?> : null;

    private static IDictionary<string, object?> GetDict(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) && value is IDictionary<string, object?> dict
            ? dict
            : throw new InvalidOperationException($"Missing config section: {key}");

    private static bool GetBool(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) && value is bool b && b;

    private static string? GetString(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) ? value as string : null;

    private static long GetLong(IDictionary<string, object?> source, string key)
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

    private static List<string> GetStringList(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) && value is IEnumerable<object?> list
            ? list.Select(v => v as string ?? string.Empty).Where(s => s.Length > 0).ToList()
            : new List<string>();
}
