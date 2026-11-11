using SecurityToolkit.Core.Scanners;
using static SecurityToolkit.Core.Installation.ConfigAccess;

namespace SecurityToolkit.Core.Installation;

/// <summary>
/// Generates the CI definitions (<c>pipelineProviders</c>): the Azure DevOps pipeline YAML and
/// the GitHub Actions workflow, both from one shared list of scan steps.
/// </summary>
internal static class PipelineGenerator
{
    /// <summary>
    /// Every scanner the pipeline can generate a step for, in the order
    /// <c>ScanCommandFactory</c>'s scanner list runs them. Each entry's display name is used both
    /// in the generated step's <c>displayName</c> and (lowercased key) as the "tools.&lt;key&gt;"
    /// config lookup and the CLI's <c>--tool</c> value.
    /// </summary>
    private static readonly string[] PipelineToolOrder =
        { "analyzers", "nuget", "npm", "semgrep", "gitleaks", "osv", "devskim", "sonar" };

    internal static readonly string[] SupportedProviders = { "azure", "github" };

    private const string AzurePipelineFileName = "azure-security-toolkit.yml";

    // Actions are pinned to a full commit SHA (a tag can be moved), the same reason the scanner
    // images are digest-pinned; the trailing comment lets Dependabot/humans see the version.
    private const string CheckoutAction = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1";
    private const string UploadArtifactAction = "actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9 # v7.0.2";
    private const string UploadSarifAction = "github/codeql-action/upload-sarif@1fc58d04a4d4374f921bdac573c3811c240f9c26 # v4.39.2";

    internal static void Write(
        BackupService backup, string root, SecurityConfig config,
        IDictionary<string, object?> lockData, List<string> messages)
    {
        var release = ToolkitReleasePin.FromLock(lockData);
        if (release is { IsPinned: false })
        {
            messages.Add(
                $"toolkitRelease: a security-toolkit v{release.Version} checksumjai még nincsenek rögzítve; a generált pipeline " +
                "telepítő lépése ezért hibával áll meg. Futtass egy teljes (--configure-only nélküli) installt.");
        }

        if (config.HasPipelineProvider("azure"))
        {
            WriteAzurePipelineYaml(backup, root, config, release);
        }
        else if (File.Exists(Path.Combine(root, AzurePipelineFileName)))
        {
            messages.Add($"A korábban generált {AzurePipelineFileName} már nem frissül (az azure nincs a pipelineProviders-ben). Ha nincs rá szükség, töröld kézzel.");
        }

        if (config.HasPipelineProvider("github"))
        {
            WriteGitHubWorkflow(backup, root, config, release);

            if (config.PrivateNuGet || config.PrivateNpmRc.Length > 0)
            {
                messages.Add("GitHub: a privateNuGet/privateNpmRc csak az Azure pipeline-ba ír hitelesítő lépést; a GitHub workflow-ban a privát feed elérését a runneren kell megoldani.");
            }
        }
        else if (File.Exists(Path.Combine(root, SecurityToolkitInstaller.GitHubWorkflowRelativePath)))
        {
            messages.Add($"A korábban generált {SecurityToolkitInstaller.GitHubWorkflowRelativePath} már nem frissül (a github nincs a pipelineProviders-ben). Ha nincs rá szükség, töröld kézzel.");
        }
    }

    /// <summary>One scan step per enabled tool, in <see cref="PipelineToolOrder"/>, shared by every provider.</summary>
    /// <param name="TrailingArguments">CLI arguments written after --run-id (keeps the historical Azure order).</param>
    /// <param name="SecretEnvironment">Step env var name → pipeline variable/secret NAME.</param>
    private sealed record PipelineScanStep(string Tool, string Mode, string TrailingArguments, IReadOnlyList<KeyValuePair<string, string>> SecretEnvironment);

    private static List<PipelineScanStep> BuildPipelineScanSteps(SecurityConfig config)
    {
        var steps = new List<PipelineScanStep>();

        foreach (var tool in PipelineToolOrder)
        {
            if (!config.IsToolEnabled(tool))
            {
                continue;
            }

            var arguments = string.Empty;
            if (tool == "gitleaks" && config.PipelineHistory)
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
                if (config.SonarCoverage)
                {
                    environment.AddRange(config.SonarTestEnvironment);
                }
            }

            steps.Add(new PipelineScanStep(tool, config.PipelineMode, arguments, environment));
        }

        return steps;
    }

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

    /// <summary>
    /// GitHub Actions counterpart of <see cref="WriteAzurePipelineYaml"/>: same provision step, one
    /// scan step per tool sharing one --run-id, results uploaded as an artifact. Runs on a
    /// self-hosted runner with the CLI on PATH (no shell is pinned: the steps are plain CLI calls,
    /// and a Windows runner without pwsh falls back to the built-in Windows PowerShell).
    /// </summary>
    private static void WriteGitHubWorkflow(
        BackupService backup, string root, SecurityConfig config, ToolkitReleasePin? release)
    {
        const string workspace = "${{ github.workspace }}";
        // run_attempt: a re-run keeps run_id, and must not merge into (or collide with) the first attempt.
        const string runId = "${{ github.run_id }}-${{ github.run_attempt }}";
        const string notCancelled = "${{ !cancelled() }}";

        var codeScanning = config.GitHubCodeScanning;
        var schedule = config.GitHubSchedule;
        var runsOn = string.Join(", ", config.GitHubRunsOn.Select(YamlQuote));

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
                    $"        run: security-toolkit scan --project-root \"{workspace}\" --tool {step.Tool} --mode {step.Mode} --run-id {runId}{step.TrailingArguments}\n";
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
        BackupService backup, string root, SecurityConfig config, ToolkitReleasePin? release)
    {
        // A Microsoft-hosted agent is selected by image, a self-hosted (or scale-set) one by pool name.
        var vmImage = config.AzureVmImage;
        var pool = vmImage.Length > 0
            ? $"pool:\n  vmImage: {YamlQuote(vmImage)}\n"
            : $"pool:\n  name: {YamlQuote(config.AgentPool)}\n";
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

        if (config.PrivateNuGet)
        {
            yaml += "\n- task: NuGetAuthenticate@1\n";
        }

        var privateNpmRc = config.PrivateNpmRc;
        if (privateNpmRc.Length > 0)
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
                    $"- script: security-toolkit scan --project-root \"$(Build.SourcesDirectory)\" --tool {step.Tool} --mode {step.Mode} --run-id $(securityRunId){step.TrailingArguments}\n" +
                    $"  displayName: 'Security scan: {step.Tool}'\n" +
                    "  condition: always()\n" +
                    envBlock;
        }

        // The Sonar step's coverage reports (.NET vscoveragexml + one lcov per Angular project) are
        // also published to the run's Code Coverage tab; PublishCodeCoverageResults@2 reads both
        // formats natively and merges them into one report.
        if (config.IsToolEnabled("sonar") && config.SonarCoverage)
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
}
