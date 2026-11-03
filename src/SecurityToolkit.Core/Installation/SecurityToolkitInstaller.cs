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
    PrerequisiteProvisioner? PrerequisiteProvisioner = null);

/// <summary>
/// Project-independent local security toolkit installer. Generates/updates the target project's
/// ".security" directory (runners, common helper, props, rules, JSON config/lock, restore
/// script), wires the project's Directory.Build.props/.editorconfig/.gitignore/.semgrepignore,
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
            backup.SaveManagedText(Path.Combine(securityDirectory, ConfigFileName), ToolkitJson.Serialize(config));

            WriteIgnoreBlocks(backup, root, config);
            WireDotNetProjects(backup, root, scan, config);

            var lockData = BuildLock(config, securityDirectory);
            backup.SaveManagedText(Path.Combine(securityDirectory, LockFileName), ToolkitJson.Serialize(lockData), owned: true);

            if (GetBool(config, "generatePipeline"))
            {
                WritePipelineYaml(backup, root, config);
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
                messages.Add("Sonar: admin belépés/jelszócsere, private projekt és project analysis token létrehozása szükséges. Token csak SONAR_TOKEN environment változóból. Feltöltés: -IncludeSonar. A közös Sonar-szervert gépenként egyszer az install.bat / install.sh (security-toolkit infra install --sonar) telepíti, a projekt-install nem indítja.");
            }

            if (GetBool(config, "generatePipeline"))
            {
                messages.Add("Azure: pool/repo/feed jogosultságok és a generált YAML-hoz pipeline létrehozása szükséges; a telepítő nem csatlakozik Azure-hoz.");
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
            ["privateNuGet"] = false,
            ["privateNpmRc"] = string.Empty,
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

        if (!GetBool(tools, "gitleaks"))
        {
            config["installHook"] = false;
        }

        foreach (var key in new[] { "installPrerequisites", "downloadSemgrepRules", "installHook", "installVSCodeExtensions", "generatePipeline", "pipelineHistory", "privateNuGet" })
        {
            if (config[key] is not bool)
            {
                throw new InvalidOperationException($"{key} must be a JSON boolean.");
            }
        }

        _ = scan;
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
        foreach (var name in new[]
        {
            "Toolkit.Common.ps1", "Initialize-SecurityTools.ps1",
            "Restore-SecurityToolkit.ps1", "SecurityToolkit.props",
        })
        {
            if (_templateFiles.TryGetValue(name, out var content))
            {
                backup.SaveManagedText(Path.Combine(securityDirectory, name), content, owned: true);
            }
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

        return new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1L,
            ["tools"] = lockTools,
        };
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

    private static void WritePipelineYaml(BackupService backup, string root, IDictionary<string, object?> config)
    {
        var agentPool = YamlQuote(GetString(config, "agentPool") ?? string.Empty);
        var yaml = "trigger: none\n" +
                   "pr: none\n" +
                   $"pool:\n  name: {agentPool}\n" +
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

        yaml += "\n" +
                "- task: PowerShell@2\n" +
                "  displayName: Initialize pinned security tools\n" +
                "  inputs:\n" +
                "    pwsh: true\n" +
                "    targetType: filePath\n" +
                "    filePath: .security/Initialize-SecurityTools.ps1\n" +
                "    arguments: -RequirePinned\n";

        var tools = GetToolsDict(config);
        var historyEnabled = GetBool(config, "pipelineHistory");

        foreach (var tool in PipelineToolOrder)
        {
            if (!GetBool(tools, tool))
            {
                continue;
            }

            // Quoted: an agent work directory containing spaces would otherwise split the path into
            // separate CLI arguments (the git hook quotes "$repo_root" the same way).
            var arguments = $"--project-root \"$(Build.SourcesDirectory)\" --tool {tool} --mode Report --run-id $(securityRunId)";
            if (tool == "gitleaks" && historyEnabled)
            {
                arguments += " --history";
            }

            var envBlock = string.Empty;
            if (tool == "sonar")
            {
                arguments += " --include-sonar";
                envBlock = "  env:\n    SONAR_TOKEN: $(SONAR_TOKEN)\n";

                // Secret pipeline variables never reach a step's environment implicitly; each one the
                // coverage tests need must be mapped here (names validated in ValidateSonarCoverage).
                var sonar = GetDict(config, "sonar");
                if (GetBool(sonar, "coverage") && TryGetDict(sonar, "testEnvironment") is { } testEnvironment)
                {
                    foreach (var (name, variable) in testEnvironment.OrderBy(e => e.Key, StringComparer.Ordinal))
                    {
                        envBlock += $"    {name}: $({variable})\n";
                    }
                }
            }

            yaml += "\n" +
                    $"- script: security-toolkit scan {arguments}\n" +
                    $"  displayName: 'Security scan: {tool}'\n" +
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

        backup.SaveManagedText(Path.Combine(root, "azure-security-toolkit.yml"), yaml, owned: true);
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
