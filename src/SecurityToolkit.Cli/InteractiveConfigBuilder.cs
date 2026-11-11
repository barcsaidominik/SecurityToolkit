using SecurityToolkit.Core.Installation;
using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Cli;

/// <summary>
/// Interactive project-configuration questions (tool selection, Semgrep scope, prerequisite/Node/SDK choices, Sonar
/// URL/key/target, git hook, VS Code extensions, Azure pipeline pool/feed settings). Runs entirely
/// in the CLI layer, exactly like <see cref="ConsolePrompts"/>'s target selection: it resolves
/// answers into a fully-built config dictionary before <see cref="SecurityToolkitInstaller.InstallAsync"/>
/// is ever called, so Core keeps taking fully-resolved input and knows nothing about prompting.
/// </summary>
public static class InteractiveConfigBuilder
{
    private static readonly (string Key, string Label)[] ToolQuestions =
    {
        ("analyzers", ".NET security analyzers (C# build/IDE rules)"),
        ("semgrep", "Semgrep CE (C# and JS/TS static analysis)"),
        ("gitleaks", "Gitleaks (secret scanning of files, staged changes and history)"),
        ("nuget", "NuGet Audit (known .NET package vulnerabilities)"),
        ("npm", "npm audit (npm lockfile package vulnerabilities)"),
        ("osv", "OSV-Scanner (second SCA opinion)"),
        ("devskim", "DevSkim (additional security linting)"),
        ("sonar", "SonarScanner for .NET (upload to a Sonar server, token later)"),
    };

    /// <summary>
    /// Runs the full question sequence against <paramref name="config"/> (already resolved via
    /// <see cref="SecurityToolkitInstaller.ResolveConfig"/>) and returns it, mutated in place with
    /// every answer, ready to pass as <c>configProfile</c> to a non-interactive
    /// <see cref="SecurityToolkitInstaller.InstallAsync"/> call. <paramref name="scan"/> supplies
    /// the discovered .NET/frontend targets already selected into <paramref name="config"/> by the
    /// caller's target-selection step.
    /// </summary>
    public static IDictionary<string, object?> Build(IConsolePrompts prompts, ProjectScanResult scan, IDictionary<string, object?> config)
    {
        var tools = ScannerSupport.GetDictionary(config, "tools")
            ?? throw new InvalidOperationException("config has no tools section.");

        Console.WriteLine();
        Console.WriteLine("--- Tools ---");
        foreach (var (key, label) in ToolQuestions)
        {
            tools[key] = prompts.ReadYesNo($"Enable {label}?", ScannerSupport.GetBool(tools, key));
        }

        var frontendTargets = ScannerSupport.GetList(config, "frontendTargets");

        if (ScannerSupport.GetBool(tools, "semgrep"))
        {
            var scope = prompts.ReadValue(
                "Semgrep scope: repository=whole repo; frontend=selected frontends, without C# (CE limit on modern C#)",
                ScannerSupport.GetString(config, "semgrepScope") ?? "repository");
            if (scope is not ("repository" or "frontend"))
            {
                throw new InvalidOperationException("semgrepScope must be repository or frontend.");
            }

            if (scope == "frontend" && frontendTargets.Count == 0)
            {
                throw new InvalidOperationException("Frontend Semgrep scope requires at least one selected frontend target.");
            }

            config["semgrepScope"] = scope;
        }

        Console.WriteLine();
        Console.WriteLine("--- Prerequisites ---");
        if (OperatingSystem.IsWindows())
        {
            config["installPrerequisites"] = prompts.ReadYesNo(
                "Install missing system dependencies with winget?", ScannerSupport.GetBool(config, "installPrerequisites"));
        }
        else
        {
            // winget is Windows-only; on Linux the installer only reports what is missing.
            Console.WriteLine("On Linux, install missing system dependencies (git, dotnet, node, docker) with the distribution's package manager; the installer only reports what is missing.");
            config["installPrerequisites"] = false;
        }

        if (ScannerSupport.GetBool(config, "installPrerequisites"))
        {
            config["sdkChannel"] = prompts.ReadValue(
                ".NET SDK channel for a missing SDK (8.0/9.0/10.0/11.0; must fit global.json)",
                ScannerSupport.GetString(config, "sdkChannel") ?? "10.0");
            config["nodeWingetId"] = prompts.ReadValue(
                "Node winget package ID (check Angular compatibility)",
                ScannerSupport.GetString(config, "nodeWingetId") ?? "OpenJS.NodeJS.LTS");
            config["nodeVersion"] = prompts.ReadValue(
                "Exact Node winget version, empty=current package version", ScannerSupport.GetString(config, "nodeVersion") ?? string.Empty);
        }

        if (ScannerSupport.GetBool(tools, "semgrep"))
        {
            config["downloadSemgrepRules"] = prompts.ReadYesNo(
                "Download the vendor Semgrep registry rules and pin them locally?", ScannerSupport.GetBool(config, "downloadSemgrepRules"));
        }

        if (ScannerSupport.GetBool(tools, "sonar"))
        {
            var sonar = ScannerSupport.GetDictionary(config, "sonar")
                ?? throw new InvalidOperationException("config has no sonar section.");

            Console.WriteLine();
            Console.WriteLine("--- Sonar ---");
            // The Sonar server itself is shared machine-level infra (install.bat / install.sh /
            // `security-toolkit infra install --sonar`); a project only points at it.
            var url = prompts.ReadValue(
                "Sonar URL (shared server; http://localhost:9000 for one installed on this machine)",
                ScannerSupport.GetString(sonar, "url") ?? "http://localhost:9000");
            var projectKey = prompts.ReadValue("Sonar project key (a separate key per project)", ScannerSupport.GetString(sonar, "projectKey") ?? string.Empty);

            sonar["url"] = url;
            sonar["projectKey"] = projectKey;

            var dotnetTargets = ScannerSupport.GetList(config, "dotnetTargets").Select(v => v as string ?? string.Empty).Where(v => v.Length > 0).ToList();
            if (dotnetTargets.Count == 0)
            {
                throw new InvalidOperationException("This installer Sonar upload requires a .NET target; standalone frontend Sonar uses a separate CLI setup.");
            }

            var existingSonarTarget = ScannerSupport.GetString(sonar, "dotnetTarget");
            var picked = prompts.SelectTargets(
                "Single Sonar .NET target (with several, the first one is used)", dotnetTargets,
                string.IsNullOrEmpty(existingSonarTarget) ? Array.Empty<string>() : new[] { existingSonarTarget });
            if (picked.Count == 0)
            {
                throw new InvalidOperationException("A Sonar .NET target is required.");
            }

            sonar["dotnetTarget"] = picked[0];

            sonar["coverage"] = prompts.ReadYesNo(
                "Measure test coverage and upload it to Sonar (dotnet test + ng test)?", ScannerSupport.GetBool(sonar, "coverage"));
            if (ScannerSupport.GetBool(sonar, "coverage"))
            {
                sonar["testFilter"] = prompts.ReadValue(
                    "dotnet test --filter expression, empty=all tests", ScannerSupport.GetString(sonar, "testFilter") ?? string.Empty);
                sonar["testEnvironment"] = ReadTestEnvironment(prompts, ScannerSupport.GetDictionary(sonar, "testEnvironment"));
            }
        }

        Console.WriteLine();
        Console.WriteLine("--- Git hook / VS Code ---");
        if (ScannerSupport.GetBool(tools, "gitleaks"))
        {
            config["installHook"] = prompts.ReadYesNo(
                "Install the fast Gitleaks staged pre-commit hook?", ScannerSupport.GetBool(config, "installHook"));
        }

        config["installVSCodeExtensions"] = prompts.ReadYesNo(
            "Install the VS Code extensions of the selected tools?", ScannerSupport.GetBool(config, "installVSCodeExtensions"));

        Console.WriteLine();
        Console.WriteLine("--- CI pipeline (Azure DevOps / GitHub Actions) ---");
        config["generatePipeline"] = prompts.ReadYesNo(
            "Generate a separate, manually run security pipeline?", ScannerSupport.GetBool(config, "generatePipeline"));

        if (ScannerSupport.GetBool(config, "generatePipeline"))
        {
            var existingProviders = ScannerSupport.GetList(config, "pipelineProviders").OfType<string>().ToList();
            var providers = new List<object?>();
            if (prompts.ReadYesNo("Azure DevOps pipeline (azure-security-toolkit.yml)?", existingProviders.Contains("azure")))
            {
                providers.Add("azure");
            }

            if (prompts.ReadYesNo("GitHub Actions workflow (.github/workflows/security-toolkit.yml)?", existingProviders.Contains("github")))
            {
                providers.Add("github");
            }

            config["pipelineProviders"] = providers;

            if (ScannerSupport.GetBool(tools, "sonar"))
            {
                // Sonar stays enabled (pinned, runs locally); only the pipelines skip its step.
                var skip = ScannerSupport.GetList(config, "pipelineSkipTools").OfType<string>()
                    .Where(t => t != "sonar").Cast<object?>().ToList();
                if (prompts.ReadYesNo(
                    "Leave Sonar out of the generated pipeline (the Sonar server is reachable from developer machines only)?",
                    ScannerSupport.GetList(config, "pipelineSkipTools").OfType<string>().Contains("sonar")))
                {
                    skip.Add("sonar");
                }

                config["pipelineSkipTools"] = skip;
            }

            config["pipelineHistory"] = prompts.ReadYesNo(
                "Should Gitleaks scan the whole reachable git history (--history)? (no=working tree only)", ScannerSupport.GetBool(config, "pipelineHistory"));
            config["pipelineMode"] = prompts.ReadYesNo(
                "Run the pipeline in Gate mode (a blocking finding fails the step; with .security/baseline.json only new findings do)?",
                ScannerSupport.GetString(config, "pipelineMode") == "Gate")
                ? "Gate"
                : "Report";

            if (ScannerSupport.GetDictionary(config, "toolkitRelease") is { } toolkitRelease)
            {
                toolkitRelease["version"] = prompts.ReadValue(
                    "For ephemeral/hosted agents: security-toolkit release version the pipeline downloads (e.g. 1.2.0), empty=the CLI on the machine PATH",
                    ScannerSupport.GetString(toolkitRelease, "version") ?? string.Empty);
            }

            if (providers.Contains("azure"))
            {
                config["azureVmImage"] = prompts.ReadValue(
                    "Microsoft-hosted Azure image (e.g. ubuntu-latest), empty=self-hosted pool",
                    ScannerSupport.GetString(config, "azureVmImage") ?? string.Empty);
                if (string.IsNullOrEmpty(ScannerSupport.GetString(config, "azureVmImage")))
                {
                    config["agentPool"] = prompts.ReadValue("Azure self-hosted agent pool name", ScannerSupport.GetString(config, "agentPool") ?? "SecurityToolkit");
                }
                config["privateNuGet"] = prompts.ReadYesNo("Add a NuGetAuthenticate task for a private feed?", ScannerSupport.GetBool(config, "privateNuGet"));
                config["privateNpmRc"] = prompts.ReadValue(
                    "Relative path of the private npm .npmrc, empty=no task", ScannerSupport.GetString(config, "privateNpmRc") ?? string.Empty);
            }

            if (providers.Contains("github"))
            {
                var labels = prompts.ReadValue(
                    "GitHub runner labels, comma-separated (runs-on)",
                    string.Join(",", ScannerSupport.GetList(config, "githubRunsOn").OfType<string>()));
                config["githubRunsOn"] = labels
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Cast<object?>()
                    .ToList();
                config["githubCodeScanning"] = prompts.ReadYesNo(
                    "Upload SARIF to GitHub code scanning? (a private repo needs Advanced Security)",
                    ScannerSupport.GetBool(config, "githubCodeScanning"));
                config["githubSchedule"] = prompts.ReadValue(
                    "Schedule as a cron expression (UTC), empty=manual runs only",
                    ScannerSupport.GetString(config, "githubSchedule") ?? string.Empty);
            }
        }

        // Non-selected modules must not accidentally install hooks.
        if (!ScannerSupport.GetBool(tools, "gitleaks"))
        {
            config["installHook"] = false;
        }

        return config;
    }

    /// <summary>
    /// sonar.testEnvironment: env var name → Azure pipeline variable NAME (never a value), e.g. a
    /// test DB connection string kept in a secret pipeline variable. Validated by the installer.
    /// </summary>
    private static Dictionary<string, object?> ReadTestEnvironment(IConsolePrompts prompts, IDictionary<string, object?>? existing)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (existing is { Count: > 0 })
        {
            Console.WriteLine("Existing test environment mappings: " +
                string.Join(", ", existing.Select(e => $"{e.Key}={e.Value}")));
            if (prompts.ReadYesNo("Keep them?", true))
            {
                foreach (var (name, variable) in existing)
                {
                    result[name] = variable;
                }
            }
        }

        while (true)
        {
            var entry = prompts.ReadValue(
                "Test env var in the pipeline, ENV_NAME=PipelineVariableName (e.g. ConnectionStrings__Default=TestDbConnectionString), empty=done",
                string.Empty).Trim();
            if (entry.Length == 0)
            {
                return result;
            }

            var separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                Console.WriteLine("Format: ENV_NAME=PipelineVariableName");
                continue;
            }

            result[entry[..separator].Trim()] = entry[(separator + 1)..].Trim();
        }
    }
}
