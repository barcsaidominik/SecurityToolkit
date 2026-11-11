using SecurityToolkit.Core.Scanners;
using static SecurityToolkit.Core.Installation.ConfigAccess;

namespace SecurityToolkit.Core.Installation;

/// <summary>
/// Builds the default security.config.json tree for a scanned project and merges an existing
/// config / profile over it, rejecting unknown keys and dropping retired ones.
/// </summary>
internal static class SecurityConfigMerger
{
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

    internal static IDictionary<string, object?> BuildDefaultConfig(ProjectScanResult scan)
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
            // Gate: a step turns red (exit 1) on a blocking finding beyond .security/baseline.json; the
            // other steps still run (condition: always()/!cancelled()).
            ["pipelineMode"] = "Report",
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
                ["baseUrl"] = SecurityToolkitInstaller.DefaultToolkitReleaseBaseUrl,
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
    internal static void MergeExistingConfig(IDictionary<string, object?> config, string securityDirectory)
    {
        var existingPath = Path.Combine(securityDirectory, SecurityToolkitInstaller.ConfigFileName);
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
    /// validated in <see cref="SecurityConfigValidator.Validate"/> instead.
    /// </summary>
    private static readonly HashSet<string> FreeFormMapKeys = new(StringComparer.Ordinal) { "testEnvironment" };

    /// <summary>Deep merge: every key in <paramref name="extra"/> must already exist in <paramref name="baseConfig"/>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="extra"/> has an unknown key.</exception>
    internal static void MergeConfig(IDictionary<string, object?> baseConfig, IDictionary<string, object?> extra)
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
}
