using static SecurityToolkit.Core.Installation.ConfigAccess;

namespace SecurityToolkit.Core.Installation;

/// <summary>
/// Validates a resolved security.config.json tree. Every failure is an
/// <see cref="InvalidOperationException"/>: the installer turns it into an exit-2 outcome.
/// </summary>
internal static class SecurityConfigValidator
{
    internal static void Validate(IDictionary<string, object?> config, ProjectScanResult scan)
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
        if (sdkChannel is not ("8.0" or "9.0" or "10.0" or "11.0"))
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

        if (GetString(config, "pipelineMode") is not ("Report" or "Gate"))
        {
            throw new InvalidOperationException("pipelineMode must be Report or Gate.");
        }

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
        if (providers.Any(p => !PipelineGenerator.SupportedProviders.Contains(p, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                $"pipelineProviders entries must be one of: {string.Join(", ", PipelineGenerator.SupportedProviders)}.");
        }

        if (providers.Distinct(StringComparer.Ordinal).Count() != providers.Count)
        {
            throw new InvalidOperationException("pipelineProviders must not contain duplicates.");
        }

        if (!config.TryGetValue("pipelineSkipTools", out var rawSkip)
            || rawSkip is not IEnumerable<object?> skipValues
            || skipValues.Any(s => s is not string))
        {
            throw new InvalidOperationException("pipelineSkipTools must be a JSON array of tool names.");
        }

        var skipTools = GetStringList(config, "pipelineSkipTools");
        if (skipTools.Any(t => !PipelineGenerator.PipelineToolOrder.Contains(t, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                $"pipelineSkipTools entries must be one of: {string.Join(", ", PipelineGenerator.PipelineToolOrder)}.");
        }

        if (skipTools.Distinct(StringComparer.Ordinal).Count() != skipTools.Count)
        {
            throw new InvalidOperationException("pipelineSkipTools must not contain duplicates.");
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
}
