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
        ("analyzers", ".NET security analyzers (C# build/IDE szabályok)"),
        ("semgrep", "Semgrep CE (C# és JS/TS statikus kódellenőrzés)"),
        ("gitleaks", "Gitleaks (fájlok, staged változások és history titokkeresés)"),
        ("nuget", "NuGet Audit (ismert .NET csomagsérülékenységek)"),
        ("npm", "npm audit (npm lockfile csomagsérülékenységek)"),
        ("osv", "OSV-Scanner (kiegészítő SCA vélemény)"),
        ("devskim", "DevSkim (kiegészítő security linting)"),
        ("sonar", "SonarScanner for .NET (Sonar szerverhez feltöltés, token később)"),
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
        Console.WriteLine("--- Eszközök ---");
        foreach (var (key, label) in ToolQuestions)
        {
            tools[key] = prompts.ReadYesNo($"Telepítendő: {label}?", ScannerSupport.GetBool(tools, key));
        }

        var frontendTargets = ScannerSupport.GetList(config, "frontendTargets");

        if (ScannerSupport.GetBool(tools, "semgrep"))
        {
            var scope = prompts.ReadValue(
                "Semgrep hatókör: repository=teljes repo; frontend=választott frontendek, C# nélkül (CE modern C# korlát)",
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
        Console.WriteLine("--- Előfeltételek ---");
        if (OperatingSystem.IsWindows())
        {
            config["installPrerequisites"] = prompts.ReadYesNo(
                "Hiányzó rendszerfüggőségek telepítése Windows winget segítségével?", ScannerSupport.GetBool(config, "installPrerequisites"));
        }
        else
        {
            // winget is Windows-only; on Linux the installer only reports what is missing.
            Console.WriteLine("Linuxon a hiányzó rendszerfüggőségeket (git, dotnet, node, docker) a disztribúció csomagkezelőjével kell telepíteni; a telepítő csak jelzi, mi hiányzik.");
            config["installPrerequisites"] = false;
        }

        if (ScannerSupport.GetBool(config, "installPrerequisites"))
        {
            config["sdkChannel"] = prompts.ReadValue(
                ".NET SDK csatorna hiányzó SDK-hoz (8.0/9.0/10.0; global.json kompatibilitás)",
                ScannerSupport.GetString(config, "sdkChannel") ?? "10.0");
            config["nodeWingetId"] = prompts.ReadValue(
                "Node winget package ID (Angular kompatibilitást ellenőrizni kell)",
                ScannerSupport.GetString(config, "nodeWingetId") ?? "OpenJS.NodeJS.LTS");
            config["nodeVersion"] = prompts.ReadValue(
                "Node pontos winget verzió, üres=package aktuális verzió", ScannerSupport.GetString(config, "nodeVersion") ?? string.Empty);
        }

        if (ScannerSupport.GetBool(tools, "semgrep"))
        {
            config["downloadSemgrepRules"] = prompts.ReadYesNo(
                "Gyártói Semgrep registry szabályok letöltése és helyi rögzítése?", ScannerSupport.GetBool(config, "downloadSemgrepRules"));
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
                "Sonar URL (közös szerver; ezen a gépen telepítettnél http://localhost:9000)",
                ScannerSupport.GetString(sonar, "url") ?? "http://localhost:9000");
            var projectKey = prompts.ReadValue("Sonar projektkulcs (más projektekhez külön kulcs)", ScannerSupport.GetString(sonar, "projectKey") ?? string.Empty);

            sonar["url"] = url;
            sonar["projectKey"] = projectKey;

            var dotnetTargets = ScannerSupport.GetList(config, "dotnetTargets").Select(v => v as string ?? string.Empty).Where(v => v.Length > 0).ToList();
            if (dotnetTargets.Count == 0)
            {
                throw new InvalidOperationException("This installer Sonar upload requires a .NET target; standalone frontend Sonar uses a separate CLI setup.");
            }

            var existingSonarTarget = ScannerSupport.GetString(sonar, "dotnetTarget");
            var picked = prompts.SelectTargets(
                "Egyetlen Sonar .NET target (több target esetén első lesz)", dotnetTargets,
                string.IsNullOrEmpty(existingSonarTarget) ? Array.Empty<string>() : new[] { existingSonarTarget });
            if (picked.Count == 0)
            {
                throw new InvalidOperationException("A Sonar .NET target is required.");
            }

            sonar["dotnetTarget"] = picked[0];

            sonar["coverage"] = prompts.ReadYesNo(
                "Teszt-coverage mérése és feltöltése a Sonarba (dotnet test + ng test)?", ScannerSupport.GetBool(sonar, "coverage"));
            if (ScannerSupport.GetBool(sonar, "coverage"))
            {
                sonar["testFilter"] = prompts.ReadValue(
                    "dotnet test --filter kifejezés, üres=minden teszt", ScannerSupport.GetString(sonar, "testFilter") ?? string.Empty);
                sonar["testEnvironment"] = ReadTestEnvironment(prompts, ScannerSupport.GetDictionary(sonar, "testEnvironment"));
            }
        }

        Console.WriteLine();
        Console.WriteLine("--- Git hook / VS Code ---");
        if (ScannerSupport.GetBool(tools, "gitleaks"))
        {
            config["installHook"] = prompts.ReadYesNo(
                "Gyors Gitleaks staged pre-commit hook telepítése?", ScannerSupport.GetBool(config, "installHook"));
        }

        config["installVSCodeExtensions"] = prompts.ReadYesNo(
            "Választott eszközök VS Code extensionjeinek telepítése?", ScannerSupport.GetBool(config, "installVSCodeExtensions"));

        Console.WriteLine();
        Console.WriteLine("--- CI pipeline (Azure DevOps / GitHub Actions) ---");
        config["generatePipeline"] = prompts.ReadYesNo(
            "Külön manuális security pipeline előállítása?", ScannerSupport.GetBool(config, "generatePipeline"));

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

            config["pipelineHistory"] = prompts.ReadYesNo(
                "Gitleaks a teljes elérhető git history-t vizsgálja (-History)? (nem=csak working tree)", ScannerSupport.GetBool(config, "pipelineHistory"));

            if (ScannerSupport.GetDictionary(config, "toolkitRelease") is { } toolkitRelease)
            {
                toolkitRelease["version"] = prompts.ReadValue(
                    "Efemer/hosted agenthez: a pipeline töltse le a security-toolkit ezen release-verzióját (pl. 1.2.0), üres=a gépi PATH-on lévő CLI",
                    ScannerSupport.GetString(toolkitRelease, "version") ?? string.Empty);
            }

            if (providers.Contains("azure"))
            {
                config["azureVmImage"] = prompts.ReadValue(
                    "Microsoft-hosted Azure image (pl. ubuntu-latest), üres=self-hosted pool",
                    ScannerSupport.GetString(config, "azureVmImage") ?? string.Empty);
                if (string.IsNullOrEmpty(ScannerSupport.GetString(config, "azureVmImage")))
                {
                    config["agentPool"] = prompts.ReadValue("Azure self-hosted agent pool neve", ScannerSupport.GetString(config, "agentPool") ?? "SecurityToolkit");
                }
                config["privateNuGet"] = prompts.ReadYesNo("NuGetAuthenticate task kell privát feedhez?", ScannerSupport.GetBool(config, "privateNuGet"));
                config["privateNpmRc"] = prompts.ReadValue(
                    "Privát npm .npmrc relatív útvonala, üres=nem kell task", ScannerSupport.GetString(config, "privateNpmRc") ?? string.Empty);
            }

            if (providers.Contains("github"))
            {
                var labels = prompts.ReadValue(
                    "GitHub runner címkék vesszővel (runs-on)",
                    string.Join(",", ScannerSupport.GetList(config, "githubRunsOn").OfType<string>()));
                config["githubRunsOn"] = labels
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Cast<object?>()
                    .ToList();
                config["githubCodeScanning"] = prompts.ReadYesNo(
                    "SARIF feltöltése a GitHub Code Scanningbe? (privát repóban Advanced Security kell)",
                    ScannerSupport.GetBool(config, "githubCodeScanning"));
                config["githubSchedule"] = prompts.ReadValue(
                    "Ütemezés cron-kifejezéssel (UTC), üres=csak kézi indítás",
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
            Console.WriteLine("Meglévő teszt-környezeti leképezések: " +
                string.Join(", ", existing.Select(e => $"{e.Key}={e.Value}")));
            if (prompts.ReadYesNo("Megtartod ezeket?", true))
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
                "Teszt env-változó a pipeline-ban, ENV_NEV=PipelineValtozoNeve (pl. ConnectionStrings__Default=TestDbConnectionString), üres=kész",
                string.Empty).Trim();
            if (entry.Length == 0)
            {
                return result;
            }

            var separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                Console.WriteLine("Formátum: ENV_NEV=PipelineValtozoNeve");
                continue;
            }

            result[entry[..separator].Trim()] = entry[(separator + 1)..].Trim();
        }
    }
}
