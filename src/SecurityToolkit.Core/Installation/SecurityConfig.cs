using static SecurityToolkit.Core.Installation.ConfigAccess;

namespace SecurityToolkit.Core.Installation;

/// <summary>
/// Read-only, typed view of a validated security.config.json tree, for the code that generates
/// files from it (lock, pipelines, project wiring). The dictionary stays the source of truth for
/// merging and serialization, so the written config keeps its exact shape; this view is built
/// once, after <see cref="SecurityConfigValidator.Validate"/>, and never written back.
/// </summary>
internal sealed record SecurityConfig
{
    public required IReadOnlySet<string> EnabledTools { get; init; }

    public required bool InstallHook { get; init; }

    public required bool InstallPrerequisites { get; init; }

    public required string SdkChannel { get; init; }

    public required string NodeWingetId { get; init; }

    public required string NodeVersion { get; init; }

    public required bool DownloadSemgrepRules { get; init; }

    public required IReadOnlyList<string> SemgrepRulesets { get; init; }

    public required bool InstallVSCodeExtensions { get; init; }

    public required bool GeneratePipeline { get; init; }

    public required IReadOnlyList<string> PipelineProviders { get; init; }

    public required bool PipelineHistory { get; init; }

    public required string PipelineMode { get; init; }

    /// <summary>
    /// pipelineSkipTools: enabled tools (still pinned and run locally) that the generated pipelines
    /// get no step for, e.g. a Sonar reachable from developer machines only.
    /// </summary>
    public required IReadOnlySet<string> PipelineSkipTools { get; init; }

    public required string AgentPool { get; init; }

    public required string AzureVmImage { get; init; }

    public required bool PrivateNuGet { get; init; }

    public required string PrivateNpmRc { get; init; }

    public required IReadOnlyList<string> GitHubRunsOn { get; init; }

    public required bool GitHubCodeScanning { get; init; }

    public required string GitHubSchedule { get; init; }

    /// <summary>sonar.coverage: the Sonar step also runs the tests with coverage.</summary>
    public required bool SonarCoverage { get; init; }

    /// <summary>sonar.testEnvironment, ordinal by env var name: env var → pipeline variable/secret NAME.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> SonarTestEnvironment { get; init; }

    /// <summary>images.&lt;tool&gt; for the Docker-based scanners (semgrep, gitleaks, osv).</summary>
    public required IReadOnlyDictionary<string, string> Images { get; init; }

    /// <summary>versions.&lt;tool&gt; for the .NET tools (devskim, sonarScanner, dotnetCoverage); empty = latest.</summary>
    public required IReadOnlyDictionary<string, string> Versions { get; init; }

    /// <summary>toolkitRelease.version; empty = the pipelines use the CLI already on the agent's PATH.</summary>
    public required string ToolkitReleaseVersion { get; init; }

    public required string ToolkitReleaseBaseUrl { get; init; }

    public bool IsToolEnabled(string name) => EnabledTools.Contains(name);

    /// <summary>The tool is enabled and the generated pipelines run it (not in pipelineSkipTools).</summary>
    public bool IsToolInPipeline(string name) => IsToolEnabled(name) && !PipelineSkipTools.Contains(name);

    public bool HasPipelineProvider(string name) => PipelineProviders.Contains(name, StringComparer.Ordinal);

    /// <summary>Builds the view; <paramref name="config"/> must already have passed validation.</summary>
    public static SecurityConfig From(IDictionary<string, object?> config)
    {
        var sonar = GetDict(config, "sonar");
        var testEnvironment = sonar.TryGetValue("testEnvironment", out var rawEnvironment)
            && rawEnvironment is IDictionary<string, object?> environment
                ? environment
                    .OrderBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => new KeyValuePair<string, string>(e.Key, (string)e.Value!))
                    .ToList()
                : new List<KeyValuePair<string, string>>();
        var toolkitRelease = GetDict(config, "toolkitRelease");

        return new SecurityConfig
        {
            EnabledTools = GetToolsDict(config)
                .Where(t => t.Value is true)
                .Select(t => t.Key)
                .ToHashSet(StringComparer.Ordinal),
            InstallHook = GetBool(config, "installHook"),
            InstallPrerequisites = GetBool(config, "installPrerequisites"),
            SdkChannel = GetString(config, "sdkChannel") ?? string.Empty,
            NodeWingetId = GetString(config, "nodeWingetId") ?? string.Empty,
            NodeVersion = GetString(config, "nodeVersion") ?? string.Empty,
            DownloadSemgrepRules = GetBool(config, "downloadSemgrepRules"),
            SemgrepRulesets = GetStringList(config, "semgrepRulesets"),
            InstallVSCodeExtensions = GetBool(config, "installVSCodeExtensions"),
            GeneratePipeline = GetBool(config, "generatePipeline"),
            PipelineProviders = GetStringList(config, "pipelineProviders"),
            PipelineHistory = GetBool(config, "pipelineHistory"),
            PipelineMode = GetString(config, "pipelineMode") ?? "Report",
            PipelineSkipTools = GetStringList(config, "pipelineSkipTools").ToHashSet(StringComparer.Ordinal),
            AgentPool = GetString(config, "agentPool") ?? string.Empty,
            AzureVmImage = GetString(config, "azureVmImage") ?? string.Empty,
            PrivateNuGet = GetBool(config, "privateNuGet"),
            PrivateNpmRc = GetString(config, "privateNpmRc") ?? string.Empty,
            GitHubRunsOn = GetStringList(config, "githubRunsOn"),
            GitHubCodeScanning = GetBool(config, "githubCodeScanning"),
            GitHubSchedule = GetString(config, "githubSchedule") ?? string.Empty,
            SonarCoverage = GetBool(sonar, "coverage"),
            SonarTestEnvironment = testEnvironment,
            Images = ReadStringMap(GetDict(config, "images")),
            Versions = ReadStringMap(GetDict(config, "versions")),
            ToolkitReleaseVersion = GetString(toolkitRelease, "version") ?? string.Empty,
            ToolkitReleaseBaseUrl = GetString(toolkitRelease, "baseUrl") ?? SecurityToolkitInstaller.DefaultToolkitReleaseBaseUrl,
        };
    }

    private static Dictionary<string, string> ReadStringMap(IDictionary<string, object?> source)
        => source.ToDictionary(e => e.Key, e => e.Value as string ?? string.Empty, StringComparer.Ordinal);
}
