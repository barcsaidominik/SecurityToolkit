using SecurityToolkit.Core.Installation;
using SecurityToolkit.Core.Scanners;
using SecurityToolkit.Core.Tests.Scanners;

namespace SecurityToolkit.Core.Tests.Installation;

public class SecurityToolkitInstallerTests : IDisposable
{
    private readonly string _root;

    public SecurityToolkitInstallerTests()
    {
        _root = Directory.CreateTempSubdirectory("installer-tests-").FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static IReadOnlyDictionary<string, string> Templates() => new Dictionary<string, string>
    {
        ["SecurityToolkit.props"] = "<Project></Project>\n",
        ["gitleaks.toml"] = "# gitleaks config\n",
        ["semgrep-starter.yml"] = "rules: []\n",
    };

    [Fact]
    public async Task InstallAsync_NeverWritesLocalLockOrSonarServerPins()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);

        await installer.InstallAsync(options, configProfile: null, toolProvisioner: null);

        var securityDirectory = Path.Combine(_root, ".security");
        var lockJson = await File.ReadAllTextAsync(Path.Combine(securityDirectory, SecurityToolkitInstaller.LockFileName));
        Assert.DoesNotContain("sonarServer", lockJson, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres", lockJson, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(securityDirectory, "security.lock.local.json")));

        var configJson = await File.ReadAllTextAsync(Path.Combine(securityDirectory, SecurityToolkitInstaller.ConfigFileName));
        Assert.DoesNotContain("startSonarServer", configJson, StringComparison.Ordinal);
        Assert.DoesNotContain("sonarServer", configJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAsync_LegacyConfigWithProjectLevelSonarServerKeys_IsAcceptedAndRewrittenWithoutThem()
    {
        var securityDirectory = Path.Combine(_root, ".security");
        Directory.CreateDirectory(securityDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(securityDirectory, SecurityToolkitInstaller.ConfigFileName),
            """
            {
              "schemaVersion": 1,
              "startSonarServer": true,
              "sonar": { "url": "http://sonar.example:9000", "port": 9001 },
              "images": { "sonarServer": "sonarqube:community", "postgres": "postgres:17" }
            }
            """);
        var legacyProfile = new Dictionary<string, object?> { ["startSonarServer"] = true };

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true), legacyProfile, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        var config = ScannerSupport.ParseJsonObject(
            await File.ReadAllTextAsync(Path.Combine(securityDirectory, SecurityToolkitInstaller.ConfigFileName)));
        Assert.False(config.ContainsKey("startSonarServer"));
        var sonar = ScannerSupport.GetDictionary(config, "sonar")!;
        Assert.Equal("http://sonar.example:9000", ScannerSupport.GetString(sonar, "url"));
        Assert.False(sonar.ContainsKey("port"));
        var images = ScannerSupport.GetDictionary(config, "images")!;
        Assert.False(images.ContainsKey("sonarServer"));
        Assert.False(images.ContainsKey("postgres"));
    }

    [Fact]
    public async Task InstallAsync_LegacySnykConfigEntries_AreAcceptedAndDropped()
    {
        var securityDirectory = Path.Combine(_root, ".security");
        Directory.CreateDirectory(securityDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(securityDirectory, SecurityToolkitInstaller.ConfigFileName),
            """
            {
              "schemaVersion": 1,
              "tools": { "gitleaks": true, "snyk": true },
              "versions": { "snyk": "1.1300.0" }
            }
            """);
        var legacyProfile = new Dictionary<string, object?>
        {
            ["tools"] = new Dictionary<string, object?> { ["snyk"] = true },
        };

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true), legacyProfile, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        var configJson = await File.ReadAllTextAsync(Path.Combine(securityDirectory, SecurityToolkitInstaller.ConfigFileName));
        Assert.DoesNotContain("snyk", configJson, StringComparison.Ordinal);
        var lockJson = await File.ReadAllTextAsync(Path.Combine(securityDirectory, SecurityToolkitInstaller.LockFileName));
        Assert.DoesNotContain("snyk", lockJson, StringComparison.Ordinal);
        var yaml = await File.ReadAllTextAsync(Path.Combine(_root, "azure-security-toolkit.yml"));
        Assert.DoesNotContain("snyk", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAsync_DryRun_WritesNothingToDisk()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, DryRun: true);

        var outcome = await installer.InstallAsync(options, configProfile: null, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(_root, ".security")));
    }

    [Fact]
    public async Task InstallAsync_ConfigureOnly_GeneratesFilesWithoutProvisioning()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);

        var outcome = await installer.InstallAsync(options, configProfile: null, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        var securityDirectory = Path.Combine(_root, ".security");
        Assert.True(File.Exists(Path.Combine(securityDirectory, SecurityToolkitInstaller.ConfigFileName)));
        Assert.True(File.Exists(Path.Combine(securityDirectory, SecurityToolkitInstaller.LockFileName)));
        Assert.True(File.Exists(Path.Combine(securityDirectory, "SecurityToolkit.props")));
        Assert.True(File.Exists(Path.Combine(_root, ".gitignore")));
        Assert.Contains("ConfigureOnly", string.Join(' ', outcome.Messages));
    }

    [Fact]
    public async Task InstallAsync_ConfigureOnly_WithGitleaksEnabled_WritesGitleaksTemplate()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);

        await installer.InstallAsync(options, configProfile: null, toolProvisioner: null);

        Assert.True(File.Exists(Path.Combine(_root, ".security", "gitleaks.toml")));
        Assert.True(File.Exists(Path.Combine(_root, ".semgrepignore")));
    }

    [Fact]
    public async Task InstallAsync_CSharpProject_WiresDirectoryBuildPropsAndEditorConfig()
    {
        var projectDirectory = Path.Combine(_root, "src", "App");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);

        await installer.InstallAsync(options, configProfile: null, toolProvisioner: null);

        var propsPath = Path.Combine(_root, "Directory.Build.props");
        Assert.True(File.Exists(propsPath));
        Assert.Contains("SecurityToolkit", File.ReadAllText(propsPath));

        var editorConfigPath = Path.Combine(projectDirectory, ".editorconfig");
        Assert.True(File.Exists(editorConfigPath));
        Assert.Contains("dotnet_diagnostic.CA3001.severity", File.ReadAllText(editorConfigPath));
    }

    [Fact]
    public async Task InstallAsync_LegacyProjectSpecificSonarComposeExists_AddsMigrationMessage()
    {
        var legacySonarDirectory = Path.Combine(_root, ".security", "sonar");
        Directory.CreateDirectory(legacySonarDirectory);
        File.WriteAllText(Path.Combine(legacySonarDirectory, "compose.yml"), "name: security-toolkit-sonar-deadbeefcafe\n");

        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);

        var outcome = await installer.InstallAsync(options, configProfile: null, toolProvisioner: null);

        Assert.Contains(outcome.Messages, m => m.Contains("An old, project-specific Sonar Docker stack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_NoLegacySonarCompose_DoesNotAddMigrationMessage()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);

        var outcome = await installer.InstallAsync(options, configProfile: null, toolProvisioner: null);

        Assert.DoesNotContain(outcome.Messages, m => m.Contains("An old, project-specific Sonar Docker stack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_SonarEnabled_GeneratedPipelineIncludesSonarStep()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);
        var profile = new Dictionary<string, object?>
        {
            ["tools"] = new Dictionary<string, object?> { ["sonar"] = true },
            ["dotnetTargets"] = new List<object?> { "fake.sln" },
            ["sonar"] = new Dictionary<string, object?> { ["dotnetTarget"] = "fake.sln" },
        };

        var outcome = await installer.InstallAsync(options, profile, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        var yaml = File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml"));
        Assert.Contains("--tool sonar", yaml);
        Assert.Contains("--include-sonar", yaml);
        Assert.Contains("SONAR_TOKEN: $(SONAR_TOKEN)", yaml);
    }

    private static Dictionary<string, object?> SonarCoverageProfile(IDictionary<string, object?> sonarExtras) => new()
    {
        ["tools"] = new Dictionary<string, object?> { ["sonar"] = true },
        ["dotnetTargets"] = new List<object?> { "fake.sln" },
        ["sonar"] = new Dictionary<string, object?>(sonarExtras) { ["dotnetTarget"] = "fake.sln" },
    };

    [Fact]
    public async Task InstallAsync_SonarCoverageEnabled_MapsTestEnvironmentToPipelineVariablesAndEnablesDotnetCoverage()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);
        var profile = SonarCoverageProfile(new Dictionary<string, object?>
        {
            ["coverage"] = true,
            ["testEnvironment"] = new Dictionary<string, object?> { ["ConnectionStrings__Default"] = "TestDbConnectionString" },
        });

        var outcome = await installer.InstallAsync(options, profile, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        var yaml = File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml"));
        Assert.Contains("  env:\n    SONAR_TOKEN: $(SONAR_TOKEN)\n    ConnectionStrings__Default: $(TestDbConnectionString)\n", yaml.Replace("\r\n", "\n"));
        Assert.Contains("- task: PublishCodeCoverageResults@2", yaml);
        Assert.Contains(
            "    summaryFileLocation: |\n" +
            "      $(Build.SourcesDirectory)/.security/results/$(securityRunId)/dotnet-coverage.xml\n" +
            "      $(Build.SourcesDirectory)/.security/results/$(securityRunId)/frontend-coverage-*/lcov.info\n",
            yaml.Replace("\r\n", "\n"));

        var lockData = ScannerSupport.ParseJsonObject(
            await File.ReadAllTextAsync(Path.Combine(_root, ".security", SecurityToolkitInstaller.LockFileName)));
        var dotnetCoverage = ScannerSupport.GetDictionary(ScannerSupport.GetDictionary(lockData, "tools")!, "dotnetCoverage")!;
        Assert.True(ScannerSupport.GetBool(dotnetCoverage, "enabled"));
        Assert.Equal("dotnet-coverage", ScannerSupport.GetString(dotnetCoverage, "package"));
    }

    [Fact]
    public async Task InstallAsync_DevSkimIgnores_AreKeptAndValidated()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);
        var valid = new Dictionary<string, object?>
        {
            ["devskim"] = new Dictionary<string, object?>
            {
                ["ignoreGlobs"] = new List<object?> { "**/*.Tests/**" },
                ["ignoreRuleIds"] = new List<object?> { "DS162092" },
            },
        };

        Assert.Equal(0, (await installer.InstallAsync(options, valid, toolProvisioner: null)).ExitCode);
        var config = ScannerSupport.ParseJsonObject(
            await File.ReadAllTextAsync(Path.Combine(_root, ".security", SecurityToolkitInstaller.ConfigFileName)));
        Assert.Equal(new object?[] { "DS162092" }, ScannerSupport.GetList(ScannerSupport.GetDictionary(config, "devskim")!, "ignoreRuleIds"));

        var invalid = new Dictionary<string, object?>
        {
            ["devskim"] = new Dictionary<string, object?> { ["ignoreGlobs"] = new List<object?> { "a/**,b/**" } },
        };
        var outcome = await installer.InstallAsync(options, invalid, toolProvisioner: null);
        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.Contains("devskim.ignoreGlobs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_SonarCoverageDisabled_IgnoresTestEnvironmentAndKeepsDotnetCoverageDisabled()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);
        var profile = SonarCoverageProfile(new Dictionary<string, object?>
        {
            ["testEnvironment"] = new Dictionary<string, object?> { ["ConnectionStrings__Default"] = "TestDbConnectionString" },
        });

        var outcome = await installer.InstallAsync(options, profile, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        var yaml = File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml"));
        Assert.DoesNotContain("ConnectionStrings__Default", yaml);
        Assert.DoesNotContain("PublishCodeCoverageResults", yaml);

        var lockData = ScannerSupport.ParseJsonObject(
            await File.ReadAllTextAsync(Path.Combine(_root, ".security", SecurityToolkitInstaller.LockFileName)));
        var dotnetCoverage = ScannerSupport.GetDictionary(ScannerSupport.GetDictionary(lockData, "tools")!, "dotnetCoverage")!;
        Assert.False(ScannerSupport.GetBool(dotnetCoverage, "enabled"));
    }

    [Fact]
    public async Task InstallAsync_SonarInPipelineSkipTools_StaysPinnedButGetsNoPipelineStepOrSecrets()
    {
        // A Sonar reachable from developer machines only: enabled and pinned (local runs need the
        // scanner), but neither pipeline gets its step, coverage publishing or secrets.
        var profile = SonarCoverageProfile(new Dictionary<string, object?>
        {
            ["coverage"] = true,
            ["testEnvironment"] = new Dictionary<string, object?> { ["ConnectionStrings__Default"] = "TEST_DB_CONNECTION" },
        });
        profile["pipelineProviders"] = new List<object?> { "azure", "github" };
        profile["pipelineSkipTools"] = new List<object?> { "sonar" };

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true), profile, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        foreach (var yaml in new[] { File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml")), ReadGitHubWorkflow() })
        {
            Assert.DoesNotContain("--tool sonar", yaml, StringComparison.Ordinal);
            Assert.DoesNotContain("SONAR_TOKEN", yaml, StringComparison.Ordinal);
            Assert.DoesNotContain("TEST_DB_CONNECTION", yaml, StringComparison.Ordinal);
            Assert.DoesNotContain("PublishCodeCoverageResults", yaml, StringComparison.Ordinal);
            Assert.Contains("--tool gitleaks", yaml, StringComparison.Ordinal);
        }

        var tools = ScannerSupport.GetDictionary(ScannerSupport.ParseJsonObject(
            await File.ReadAllTextAsync(Path.Combine(_root, ".security", SecurityToolkitInstaller.LockFileName))), "tools")!;
        Assert.True(ScannerSupport.GetBool(ScannerSupport.GetDictionary(tools, "sonarScanner")!, "enabled"));
        Assert.True(ScannerSupport.GetBool(ScannerSupport.GetDictionary(tools, "dotnetCoverage")!, "enabled"));

        var github = Assert.Single(outcome.Messages, m => m.StartsWith("GitHub: ", StringComparison.Ordinal));
        Assert.DoesNotContain("SONAR_TOKEN", github, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""["bogus"]""", "pipelineSkipTools entries must be one of")]
    [InlineData("""["sonar", "sonar"]""", "pipelineSkipTools must not contain duplicates")]
    [InlineData("""[1]""", "pipelineSkipTools must be a JSON array of tool names")]
    public async Task InstallAsync_InvalidPipelineSkipTools_ReturnsExitCode2(string json, string expectedMessage)
    {
        var profile = new Dictionary<string, object?> { ["pipelineSkipTools"] = ScannerSupport.ParseJsonObject($$"""{ "v": {{json}} }""")["v"] };

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, DryRun: true), profile, toolProvisioner: null);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Bad-Name", "TestDbConnectionString", "Invalid sonar.testEnvironment variable name")]
    [InlineData("SONAR_TOKEN", "Other", "Invalid sonar.testEnvironment variable name")]
    [InlineData("ConnectionStrings__Default", "$(Injected)", "must name an Azure pipeline variable")]
    [InlineData("ConnectionStrings__Default", "Server=db;Password=x", "must name an Azure pipeline variable")]
    public async Task InstallAsync_InvalidSonarTestEnvironment_ReturnsExitCode2WithoutThrowing(string name, string variable, string expectedMessage)
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, DryRun: true);
        var profile = SonarCoverageProfile(new Dictionary<string, object?>
        {
            ["coverage"] = true,
            ["testEnvironment"] = new Dictionary<string, object?> { [name] = variable },
        });

        var outcome = await installer.InstallAsync(options, profile, toolProvisioner: null);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_SonarTestFilterWithQuote_ReturnsExitCode2WithoutThrowing()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, DryRun: true);
        var profile = SonarCoverageProfile(new Dictionary<string, object?>
        {
            ["coverage"] = true,
            ["testFilter"] = "Category!=\"Db\"",
        });

        var outcome = await installer.InstallAsync(options, profile, toolProvisioner: null);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.Contains("sonar.testFilter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_SonarDisabled_GeneratedPipelineOmitsSonarStep()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);

        await installer.InstallAsync(options, configProfile: null, toolProvisioner: null);

        var yaml = File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml"));
        Assert.DoesNotContain("--tool sonar", yaml);
        Assert.DoesNotContain("--include-sonar", yaml);
        Assert.DoesNotContain("SONAR_TOKEN", yaml);
    }

    [Fact]
    public async Task InstallAsync_DefaultConfig_GeneratedPipelineIncludesHistoryFlag()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);

        await installer.InstallAsync(options, configProfile: null, toolProvisioner: null);

        var yaml = File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml"));
        Assert.Contains("--tool gitleaks --mode Report --run-id $(securityRunId) --history", yaml);
    }

    [Fact]
    public async Task InstallAsync_GeneratedPipeline_QuotesProjectRootInEveryScanStep()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);

        await installer.InstallAsync(options, configProfile: null, toolProvisioner: null);

        var yaml = File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml"));
        var scanSteps = yaml.Split('\n').Where(l => l.Contains("security-toolkit scan")).ToArray();
        Assert.NotEmpty(scanSteps);
        Assert.All(scanSteps, l => Assert.Contains("--project-root \"$(Build.SourcesDirectory)\"", l));
    }

    [Fact]
    public async Task InstallAsync_PipelineHistoryDisabled_GeneratedPipelineOmitsHistoryFlag()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);
        var profile = new Dictionary<string, object?> { ["pipelineHistory"] = false };

        var outcome = await installer.InstallAsync(options, profile, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        var yaml = File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml"));
        Assert.Contains("--tool gitleaks --mode Report --run-id $(securityRunId)\r\n", yaml);
        Assert.DoesNotContain("--history", yaml);
    }

    [Fact]
    public async Task InstallAsync_EachEnabledTool_GetsItsOwnAlwaysRunStep()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);
        var profile = new Dictionary<string, object?>
        {
            ["tools"] = new Dictionary<string, object?>
            {
                ["analyzers"] = true,
                ["nuget"] = true,
                ["npm"] = false,
                ["semgrep"] = true,
                ["gitleaks"] = true,
                ["osv"] = false,
                ["devskim"] = false,
                ["sonar"] = false,
            },
            ["dotnetTargets"] = new List<object?> { "fake.sln" },
        };

        var outcome = await installer.InstallAsync(options, profile, toolProvisioner: null);
        Assert.Equal(0, outcome.ExitCode);

        var yaml = File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml"));
        Assert.Contains("displayName: 'Security scan: analyzers'", yaml);
        Assert.Contains("displayName: 'Security scan: nuget'", yaml);
        Assert.Contains("displayName: 'Security scan: semgrep'", yaml);
        Assert.Contains("displayName: 'Security scan: gitleaks'", yaml);
        Assert.DoesNotContain("displayName: 'Security scan: npm'", yaml);
        Assert.DoesNotContain("displayName: 'Security scan: osv'", yaml);
        Assert.DoesNotContain("displayName: 'Security scan: devskim'", yaml);
        Assert.DoesNotContain("displayName: 'Security scan: sonar'", yaml);

        // Every enabled tool's step is independent (a failure must not block the rest).
        var toolStepCount = System.Text.RegularExpressions.Regex.Matches(yaml, "- script: security-toolkit scan").Count;
        Assert.Equal(4, toolStepCount);
        var alwaysConditionCount = System.Text.RegularExpressions.Regex.Matches(yaml, "condition: always\\(\\)").Count;
        Assert.Equal(7, alwaysConditionCount); // 4 tool steps + PublishPipelineArtifact@1 + CopyFiles@2 + PublishBuildArtifacts@1
    }

    [Fact]
    public async Task InstallAsync_GeneratedPipeline_NoLongerReferencesInvokeSecurityScanScript()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);

        await installer.InstallAsync(options, configProfile: null, toolProvisioner: null);

        var yaml = File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml"));
        Assert.DoesNotContain("Invoke-SecurityScan.ps1", yaml);
        Assert.False(File.Exists(Path.Combine(_root, ".security", "Invoke-SecurityScan.ps1")));
    }

    [Fact]
    public async Task InstallAsync_MissingProjectRoot_Throws()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(Path.Combine(_root, "does-not-exist"), NonInteractive: true, DryRun: true);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => installer.InstallAsync(options, configProfile: null, toolProvisioner: null));
    }

    [Fact]
    public async Task InstallAsync_ConfigProfileWithUnknownKey_Throws()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, DryRun: true);
        var profile = new Dictionary<string, object?> { ["unknownKey"] = true };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => installer.InstallAsync(options, profile, toolProvisioner: null));
    }

    [Fact]
    public async Task InstallAsync_SemgrepScopeFrontendWithoutFrontendTarget_ReturnsExitCode2()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, DryRun: true, SemgrepScope: SemgrepInstallScope.Frontend);

        var outcome = await installer.InstallAsync(options, configProfile: null, toolProvisioner: null);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.Contains("Frontend Semgrep scope requires", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_SonarEnabledWithoutDotnetTarget_ReturnsExitCode2WithoutThrowing()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, DryRun: true);
        var profile = new Dictionary<string, object?>
        {
            ["tools"] = new Dictionary<string, object?> { ["sonar"] = true },
            ["dotnetTargets"] = new List<object?> { "fake.sln" },
        };

        var outcome = await installer.InstallAsync(options, profile, toolProvisioner: null);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.Contains("Sonar .NET target is required", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_RerunAfterManualEdit_SkipsOwnedFileButStillCompletesTheRestOfTheRun()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);

        await installer.InstallAsync(options, configProfile: null, toolProvisioner: null);

        var runnerPath = Path.Combine(_root, ".security", "SecurityToolkit.props");
        File.WriteAllText(runnerPath, "# manually hacked\n");

        var outcome = await installer.InstallAsync(options, configProfile: null, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.Contains("Skipped (manual change detected)", StringComparison.Ordinal) && m.Contains("SecurityToolkit.props", StringComparison.Ordinal));
        Assert.Equal("# manually hacked\n", File.ReadAllText(runnerPath));
        // Every other owned template still gets (re)written normally in the same run.
        Assert.True(File.Exists(Path.Combine(_root, ".security", "gitleaks.toml")));
    }

    [Fact]
    public async Task InstallAsync_CheckOnly_InSyncProject_ReportsAllUnchangedAndWritesNothing()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var installOptions = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);
        await installer.InstallAsync(installOptions, configProfile: null, toolProvisioner: null);

        var stateBefore = File.ReadAllText(Path.Combine(_root, ".security", "install-state.json"));

        var checkOptions = new InstallOptions(_root, NonInteractive: true, CheckOnly: true);
        var outcome = await installer.InstallAsync(checkOptions, configProfile: null, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.ManifestPath);
        Assert.NotNull(outcome.DriftEntries);
        Assert.NotEmpty(outcome.DriftEntries!);
        Assert.All(outcome.DriftEntries!, e => Assert.Equal(DriftStatus.Unchanged, e.Status));
        // A check run must never touch install-state.json.
        Assert.Equal(stateBefore, File.ReadAllText(Path.Combine(_root, ".security", "install-state.json")));
    }

    [Fact]
    public async Task InstallAsync_CheckOnly_TemplateChangedSinceInstall_ReportsWouldUpdate()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var installOptions = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);
        await installer.InstallAsync(installOptions, configProfile: null, toolProvisioner: null);

        var updatedTemplates = new Dictionary<string, string>(Templates()) { ["gitleaks.toml"] = "# gitleaks config v2\n" };
        var updatedInstaller = new SecurityToolkitInstaller(updatedTemplates);

        var checkOptions = new InstallOptions(_root, NonInteractive: true, CheckOnly: true);
        var outcome = await updatedInstaller.InstallAsync(checkOptions, configProfile: null, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains(outcome.DriftEntries!, e => e.RelativePath == ".security/gitleaks.toml" && e.Status == DriftStatus.WouldUpdate);
        Assert.Equal("# gitleaks config\r\n", File.ReadAllText(Path.Combine(_root, ".security", "gitleaks.toml")));
    }

    [Fact]
    public async Task InstallAsync_CheckOnly_ManuallyEditedOwnedFile_ReportsManualChangesWithoutThrowing()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var installOptions = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);
        await installer.InstallAsync(installOptions, configProfile: null, toolProvisioner: null);

        var runnerPath = Path.Combine(_root, ".security", "SecurityToolkit.props");
        File.WriteAllText(runnerPath, "# manually hacked\n");

        var checkOptions = new InstallOptions(_root, NonInteractive: true, CheckOnly: true);
        var outcome = await installer.InstallAsync(checkOptions, configProfile: null, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains(outcome.DriftEntries!, e => e.RelativePath == ".security/SecurityToolkit.props" && e.Status == DriftStatus.ManualChangesDetected);
        Assert.Equal("# manually hacked\n", File.ReadAllText(runnerPath));
    }

    [Fact]
    public async Task InstallAsync_InstallHookEnabled_HookInvokesSharedPathExecutableNotAProjectLocalCopy()
    {
        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("git", new ProcessResult(0, ".git/hooks\n", string.Empty));
        if (!OperatingSystem.IsWindows())
        {
            processRunner.Enqueue("chmod", new ProcessResult(0, string.Empty, string.Empty));
        }

        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);
        var profile = new Dictionary<string, object?> { ["installHook"] = true };
        var dependencies = new InstallerDependencies(
            SemgrepRuleDownloader: null,
            VsCodeExtensionInstaller: null,
            GitHookInstaller: new GitHookInstaller(processRunner));

        await installer.InstallAsync(options, profile, toolProvisioner: null, dependencies, targetSelector: null);

        // No project-local executable copy is made anymore — the hook resolves "security-toolkit"
        // from PATH (the shared, per-machine install), so re-running install.bat / install.sh upgrades every
        // previously-installed hook without touching the target project.
        var securityDirectory = Path.Combine(_root, ".security");
        var localExecutablePath = Path.Combine(
            securityDirectory, "bin", OperatingSystem.IsWindows() ? "security-toolkit.exe" : "security-toolkit");
        Assert.False(File.Exists(localExecutablePath));

        var hookPath = Path.Combine(_root, ".git", "hooks", "pre-commit");
        Assert.True(File.Exists(hookPath));
        var hookContent = File.ReadAllText(hookPath);
        Assert.Contains("exec security-toolkit scan", hookContent, StringComparison.Ordinal);
    }

    /// <summary>Simulates a project installed by an older toolkit that still generated the PS scripts.</summary>
    private async Task InstallWithRetiredScriptsAsync()
    {
        var oldTemplates = new Dictionary<string, string>(Templates());
        var installer = new SecurityToolkitInstaller(oldTemplates);
        await installer.InstallAsync(new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true), configProfile: null, toolProvisioner: null);

        // Write the old scripts and record their hashes the way an earlier install would have.
        var securityDirectory = Path.Combine(_root, ".security");
        var statePath = Path.Combine(securityDirectory, "install-state.json");
        var state = SecurityToolkit.Core.Scanners.ScannerSupport.ParseJsonObject(File.ReadAllText(statePath));
        var hashes = (IDictionary<string, object?>)state["hashes"]!;
        foreach (var name in SecurityToolkitInstaller.RetiredSecurityFiles.Take(3))
        {
            var path = Path.Combine(securityDirectory, name);
            File.WriteAllText(path, $"# {name}\n");
            hashes[$".security/{name}"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        }

        SecurityToolkit.Core.ToolkitJson.WriteFile(statePath, state);
    }

    [Fact]
    public async Task InstallAsync_PipelineProvisionsThroughTheCliWithoutPowerShell()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        await installer.InstallAsync(new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true), configProfile: null, toolProvisioner: null);

        var yaml = File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml"));
        Assert.Contains("- script: security-toolkit provision --project-root \"$(Build.SourcesDirectory)\" --require-pinned", yaml);
        Assert.DoesNotContain("PowerShell@2", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain(".ps1", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAsync_RetiredUnmodifiedScripts_AreDeletedAndRestorable()
    {
        await InstallWithRetiredScriptsAsync();

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true), configProfile: null, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        foreach (var name in SecurityToolkitInstaller.RetiredSecurityFiles)
        {
            Assert.False(File.Exists(Path.Combine(_root, ".security", name)), name);
        }

        Assert.Contains(outcome.Messages, m => m.Contains("Retired toolkit files deleted", StringComparison.Ordinal));
        var state = File.ReadAllText(Path.Combine(_root, ".security", "install-state.json"));
        Assert.DoesNotContain("Toolkit.Common.ps1", state, StringComparison.Ordinal);

        var manifest = TransactionManifest.Parse(
            SecurityToolkit.Core.Scanners.ScannerSupport.ParseJsonObject(File.ReadAllText(outcome.ManifestPath!)));
        new RestoreService().Restore(_root, manifest);

        Assert.Equal("# Toolkit.Common.ps1\n", File.ReadAllText(Path.Combine(_root, ".security", "Toolkit.Common.ps1")));
    }

    [Fact]
    public async Task InstallAsync_RetiredScriptEditedByHand_IsKeptAndReported()
    {
        await InstallWithRetiredScriptsAsync();
        var edited = Path.Combine(_root, ".security", "Restore-SecurityToolkit.ps1");
        File.WriteAllText(edited, "# my own changes\n");

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true), configProfile: null, toolProvisioner: null);

        Assert.Equal("# my own changes\n", File.ReadAllText(edited));
        Assert.False(File.Exists(Path.Combine(_root, ".security", "Toolkit.Common.ps1")));
        Assert.Contains(outcome.Messages, m => m.Contains("changed by hand", StringComparison.Ordinal)
            && m.Contains(".security/Restore-SecurityToolkit.ps1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_CheckOnly_RetiredScript_ReportsWouldDeleteWithoutDeleting()
    {
        await InstallWithRetiredScriptsAsync();

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, CheckOnly: true), configProfile: null, toolProvisioner: null);

        Assert.Contains(outcome.DriftEntries!, e => e.RelativePath == ".security/Toolkit.Common.ps1" && e.Status == DriftStatus.WouldDelete);
        Assert.True(File.Exists(Path.Combine(_root, ".security", "Toolkit.Common.ps1")));
    }

    private static Dictionary<string, object?> GitHubProfile(params (string Key, object? Value)[] extras)
    {
        var profile = new Dictionary<string, object?> { ["pipelineProviders"] = new List<object?> { "github" } };
        foreach (var (key, value) in extras)
        {
            profile[key] = value;
        }

        return profile;
    }

    private string ReadGitHubWorkflow()
        => File.ReadAllText(Path.Combine(_root, ".github", "workflows", "security-toolkit.yml")).Replace("\r\n", "\n");

    [Fact]
    public async Task InstallAsync_DefaultConfig_GeneratesOnlyTheAzurePipeline()
    {
        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true), configProfile: null, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(File.Exists(Path.Combine(_root, "azure-security-toolkit.yml")));
        Assert.False(File.Exists(Path.Combine(_root, ".github", "workflows", "security-toolkit.yml")));
        var config = ScannerSupport.ParseJsonObject(
            await File.ReadAllTextAsync(Path.Combine(_root, ".security", SecurityToolkitInstaller.ConfigFileName)));
        Assert.Equal(new object?[] { "azure" }, ScannerSupport.GetList(config, "pipelineProviders"));
    }

    [Fact]
    public async Task InstallAsync_GitHubProvider_WritesPinnedWorkflowWithOneStepPerToolAndSharedRunId()
    {
        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true), GitHubProfile(), toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        Assert.False(File.Exists(Path.Combine(_root, "azure-security-toolkit.yml")));
        var yaml = ReadGitHubWorkflow();

        Assert.Contains("on:\n  workflow_dispatch:\npermissions:\n  contents: read\njobs:\n", yaml);
        Assert.Contains("    runs-on: ['self-hosted']\n", yaml);
        Assert.Contains(
            "        run: security-toolkit provision --project-root \"${{ github.workspace }}\" --require-pinned\n", yaml);
        Assert.Contains(
            "      - name: 'Security scan: gitleaks'\n" +
            "        if: ${{ !cancelled() }}\n" +
            "        run: security-toolkit scan --project-root \"${{ github.workspace }}\" --tool gitleaks --mode Report " +
            "--run-id ${{ github.run_id }}-${{ github.run_attempt }} --history\n",
            yaml);
        Assert.Contains("name: security-results\n          path: .security/results\n", yaml);

        // Every action is pinned to a full commit SHA, never a movable tag.
        var uses = yaml.Split('\n').Where(l => l.Contains("uses: ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, uses.Count);
        Assert.All(uses, l => Assert.Matches("@[0-9a-f]{40} # v[0-9]", l));

        Assert.DoesNotContain("upload-sarif", yaml);
        Assert.DoesNotContain("security-events", yaml);
        Assert.DoesNotContain("schedule:", yaml);
        Assert.DoesNotContain("SONAR_TOKEN", yaml);
    }

    [Fact]
    public async Task InstallAsync_GitHubWithCodeScanningAndSchedule_AddsSarifUploadPermissionsAndCron()
    {
        var profile = GitHubProfile(
            ("githubCodeScanning", true),
            ("githubSchedule", "0 2 * * 1-5"),
            ("githubRunsOn", new List<object?> { "self-hosted", "security-toolkit" }));

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true), profile, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        var yaml = ReadGitHubWorkflow();
        Assert.Contains("  schedule:\n    - cron: '0 2 * * 1-5'\n", yaml);
        Assert.Contains("  contents: read\n  security-events: write\n  actions: read\n", yaml);
        Assert.Contains("    runs-on: ['self-hosted', 'security-toolkit']\n", yaml);
        Assert.Contains("github/codeql-action/upload-sarif@", yaml);
        Assert.Contains("          sarif_file: .security/results/${{ github.run_id }}-${{ github.run_attempt }}\n", yaml);
    }

    [Fact]
    public async Task InstallAsync_GitHubWithSonarCoverage_MapsSecretsIntoTheSonarStepEnv()
    {
        var profile = SonarCoverageProfile(new Dictionary<string, object?>
        {
            ["coverage"] = true,
            ["testEnvironment"] = new Dictionary<string, object?> { ["ConnectionStrings__Default"] = "TEST_DB_CONNECTION" },
        });
        profile["pipelineProviders"] = new List<object?> { "azure", "github" };

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true), profile, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(File.Exists(Path.Combine(_root, "azure-security-toolkit.yml")));
        var yaml = ReadGitHubWorkflow();
        Assert.Contains(
            "--tool sonar --mode Report --run-id ${{ github.run_id }}-${{ github.run_attempt }} --include-sonar\n" +
            "        env:\n" +
            "          SONAR_TOKEN: ${{ secrets.SONAR_TOKEN }}\n" +
            "          ConnectionStrings__Default: ${{ secrets.TEST_DB_CONNECTION }}\n",
            yaml);

        var github = Assert.Single(outcome.Messages, m => m.StartsWith("GitHub: ", StringComparison.Ordinal));
        Assert.Contains("security-toolkit on PATH", github, StringComparison.Ordinal);
        Assert.Contains("Actions secrets: SONAR_TOKEN, TEST_DB_CONNECTION.", github, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAsync_GitHubHostedWithToolkitRelease_AsksOnlyForWhatTheConfigNeeds()
    {
        // A downloaded CLI on a GitHub-hosted runner, Sonar off: no PATH install and no secrets to add.
        var profile = GitHubProfile(
            ("githubRunsOn", new List<object?> { "ubuntu-latest" }),
            ("toolkitRelease", new Dictionary<string, object?> { ["version"] = "0.1.0" }),
            ("tools", new Dictionary<string, object?> { ["sonar"] = false }));

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true), profile, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        var github = Assert.Single(outcome.Messages, m => m.StartsWith("GitHub: ", StringComparison.Ordinal));
        Assert.Contains("downloads security-toolkit v0.1.0 on the runner (runs-on: ubuntu-latest)", github, StringComparison.Ordinal);
        Assert.Contains("default branch", github, StringComparison.Ordinal);
        Assert.DoesNotContain("on PATH", github, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets", github, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pipelineProviders", "gitlab", "pipelineProviders entries must be one of")]
    [InlineData("githubRunsOn", "self hosted", "githubRunsOn must be")]
    [InlineData("githubSchedule", "0 2 * *", "githubSchedule must be")]
    [InlineData("githubSchedule", "0 2 * * 1\n- run: x", "githubSchedule must be")]
    public async Task InstallAsync_InvalidGitHubPipelineSettings_ReturnExitCode2(string key, string value, string expectedMessage)
    {
        var profile = GitHubProfile();
        profile[key] = key == "githubSchedule" ? value : new List<object?> { value };

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, DryRun: true), profile, toolProvisioner: null);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Test.Db")]
    [InlineData("GITHUB_TOKEN")]
    public async Task InstallAsync_GitHubWithTestEnvironmentNotAValidSecretName_ReturnsExitCode2(string secretName)
    {
        var profile = SonarCoverageProfile(new Dictionary<string, object?>
        {
            ["coverage"] = true,
            ["testEnvironment"] = new Dictionary<string, object?> { ["ConnectionStrings__Default"] = secretName },
        });
        profile["pipelineProviders"] = new List<object?> { "github" };

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, DryRun: true), profile, toolProvisioner: null);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.Contains("must name a GitHub secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_GeneratePipelineWithNoProvider_ReturnsExitCode2()
    {
        var profile = new Dictionary<string, object?> { ["pipelineProviders"] = new List<object?>() };

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, DryRun: true), profile, toolProvisioner: null);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.Contains("at least one provider", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_ProviderDropped_LeavesThePreviousPipelineFileAndSaysSo()
    {
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true);
        Assert.Equal(0, (await installer.InstallAsync(options, configProfile: null, toolProvisioner: null)).ExitCode);

        var outcome = await installer.InstallAsync(options, GitHubProfile(), toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(File.Exists(Path.Combine(_root, "azure-security-toolkit.yml")));
        Assert.Contains(outcome.Messages, m => m.Contains("azure-security-toolkit.yml is no longer updated", StringComparison.Ordinal));
    }

    private static Dictionary<string, object?> ReleaseProfile(string version = "1.2.0", params string[] providers) => new()
    {
        ["pipelineProviders"] = (providers.Length == 0 ? new[] { "azure", "github" } : providers).Cast<object?>().ToList(),
        ["toolkitRelease"] = new Dictionary<string, object?> { ["version"] = version },
    };

    private static InstallerDependencies ReleaseDependencies(FakeHttpMessageHandler handler)
        => new(null, null, null, ToolkitReleaseResolver: new ToolkitReleaseResolver(new HttpClient(handler)));

    [Fact]
    public async Task InstallAsync_ToolkitReleaseUnset_LockAndPipelinesHaveNoDownloadStep()
    {
        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true),
            new Dictionary<string, object?> { ["pipelineProviders"] = new List<object?> { "azure", "github" } },
            toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        var lockJson = await File.ReadAllTextAsync(Path.Combine(_root, ".security", SecurityToolkitInstaller.LockFileName));
        Assert.DoesNotContain("toolkitRelease", lockJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Install security-toolkit", File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml")));
        Assert.DoesNotContain("Install security-toolkit", ReadGitHubWorkflow());
    }

    [Fact]
    public async Task InstallAsync_ToolkitReleaseFullInstall_PinsChecksumsAndWritesVerifiedDownloadSteps()
    {
        var handler = ToolkitReleaseResolverTests.Handler(System.Net.HttpStatusCode.OK, ToolkitReleaseResolverTests.ChecksumFile);

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true), ReleaseProfile(), toolProvisioner: null,
            ReleaseDependencies(handler), targetSelector: null);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(
            new Uri("https://github.com/barcsaidominik/SecurityToolkit/releases/download/v1.2.0/SHA256SUMS"),
            Assert.Single(handler.RequestedUris));

        var lockData = ScannerSupport.ParseJsonObject(
            await File.ReadAllTextAsync(Path.Combine(_root, ".security", SecurityToolkitInstaller.LockFileName)));
        var release = ScannerSupport.GetDictionary(lockData, "toolkitRelease")!;
        Assert.Equal("1.2.0", ScannerSupport.GetString(release, "version"));
        Assert.Equal(ToolkitReleaseResolverTests.WinHash, ScannerSupport.GetString(ScannerSupport.GetDictionary(release, "sha256")!, "win-x64"));

        var azure = File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml")).Replace("\r\n", "\n");
        Assert.Contains("- bash: |\n    set -euo pipefail\n", azure);
        Assert.Contains($"  x86_64) rid=linux-x64; sum={ToolkitReleaseResolverTests.LinuxHash} ;;", azure);
        Assert.Contains($"  aarch64|arm64) rid=linux-arm64; sum={ToolkitReleaseResolverTests.ArmHash} ;;", azure);
        Assert.Contains("    echo \"##vso[task.prependpath]$dir\"\n  displayName: Install security-toolkit 1.2.0 (Linux)\n" +
                        "  condition: and(succeeded(), eq(variables['Agent.OS'], 'Linux'))\n", azure);
        Assert.Contains(
            "curl.exe -fsSL --retry 3 -o \"%dir%\\security-toolkit.exe\" " +
            "\"https://github.com/barcsaidominik/SecurityToolkit/releases/download/v1.2.0/security-toolkit-win-x64.exe\" || exit /b 2",
            azure);
        Assert.Contains($"findstr /i /x \"{ToolkitReleaseResolverTests.WinHash}\"", azure);
        Assert.True(
            azure.IndexOf("Install security-toolkit 1.2.0 (Windows)", StringComparison.Ordinal)
            < azure.IndexOf("Initialize pinned security tools", StringComparison.Ordinal));

        var github = ReadGitHubWorkflow();
        Assert.Contains("        if: runner.os == 'Linux'\n        shell: bash\n        run: |\n          set -euo pipefail\n", github);
        Assert.Contains("          dir=\"${{ runner.temp }}/security-toolkit-bin\"\n", github);
        Assert.Contains("          echo \"$dir\" >> \"$GITHUB_PATH\"\n", github);
        Assert.Contains("        if: runner.os == 'Windows'\n        shell: cmd\n", github);
        Assert.Contains("          >>\"%GITHUB_PATH%\" echo %dir%\n", github);
    }

    [Fact]
    public async Task InstallAsync_ToolkitReleaseAlreadyPinned_KeepsHashesWithoutDownloading()
    {
        var first = ToolkitReleaseResolverTests.Handler(System.Net.HttpStatusCode.OK, ToolkitReleaseResolverTests.ChecksumFile);
        var installer = new SecurityToolkitInstaller(Templates());
        var options = new InstallOptions(_root, NonInteractive: true);
        Assert.Equal(0, (await installer.InstallAsync(options, ReleaseProfile(), null, ReleaseDependencies(first), null)).ExitCode);

        var second = ToolkitReleaseResolverTests.Handler(System.Net.HttpStatusCode.InternalServerError, string.Empty);
        var outcome = await installer.InstallAsync(options, ReleaseProfile(), null, ReleaseDependencies(second), null);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Empty(second.RequestedUris);
        Assert.Contains(ToolkitReleaseResolverTests.LinuxHash, File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml")));
    }

    [Fact]
    public async Task InstallAsync_ToolkitReleaseConfigureOnly_WritesFailingStepAndExplains()
    {
        var handler = ToolkitReleaseResolverTests.Handler(System.Net.HttpStatusCode.OK, ToolkitReleaseResolverTests.ChecksumFile);

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true), ReleaseProfile(), null, ReleaseDependencies(handler), null);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Empty(handler.RequestedUris);
        Assert.Contains(outcome.Messages, m => m.Contains("are not pinned yet", StringComparison.Ordinal));
        var azure = File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml")).Replace("\r\n", "\n");
        Assert.Contains("is not pinned in .security/security.lock.json: run a full install first.\n    exit 2\n", azure);
        Assert.DoesNotContain("curl", azure);
    }

    [Fact]
    public async Task InstallAsync_ToolkitReleaseChecksumsUnavailable_ReturnsExitCode2()
    {
        var handler = ToolkitReleaseResolverTests.Handler(System.Net.HttpStatusCode.NotFound, string.Empty);

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true), ReleaseProfile(), null, ReleaseDependencies(handler), null);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.Contains("404", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("toolkitRelease.version", "v1.2.0", "toolkitRelease.version must be")]
    [InlineData("toolkitRelease.version", "1.2.0\"; rm -rf /", "toolkitRelease.version must be")]
    [InlineData("toolkitRelease.baseUrl", "http://example.test/releases", "toolkitRelease.baseUrl must be")]
    [InlineData("toolkitRelease.baseUrl", "https://example.test/a b", "toolkitRelease.baseUrl must be")]
    [InlineData("azureVmImage", "ubuntu latest", "azureVmImage must be")]
    public async Task InstallAsync_InvalidReleaseOrImageSettings_ReturnExitCode2(string key, string value, string expectedMessage)
    {
        var profile = ReleaseProfile();
        if (key.StartsWith("toolkitRelease.", StringComparison.Ordinal))
        {
            ((Dictionary<string, object?>)profile["toolkitRelease"]!)[key["toolkitRelease.".Length..]] = value;
        }
        else
        {
            profile[key] = value;
        }

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, DryRun: true), profile, toolProvisioner: null);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_AzureVmImage_SelectsAHostedPoolInsteadOfThePoolName()
    {
        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true),
            new Dictionary<string, object?> { ["azureVmImage"] = "ubuntu-latest" },
            toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        var azure = File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml")).Replace("\r\n", "\n");
        Assert.Contains("pool:\n  vmImage: 'ubuntu-latest'\n", azure);
        Assert.DoesNotContain("name: 'SecurityToolkit'", azure);
    }

    [Fact]
    public async Task InstallAsync_PipelineModeGate_WritesGateIntoEveryScanStepOfBothPipelines()
    {
        var profile = new Dictionary<string, object?>
        {
            ["pipelineProviders"] = new List<object?> { "azure", "github" },
            ["pipelineMode"] = "Gate",
        };

        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, ConfigureOnly: true), profile, toolProvisioner: null);

        Assert.Equal(0, outcome.ExitCode);
        var azure = File.ReadAllText(Path.Combine(_root, "azure-security-toolkit.yml"));
        var github = ReadGitHubWorkflow();
        Assert.DoesNotContain("--mode Report", azure);
        Assert.DoesNotContain("--mode Report", github);
        Assert.Contains("--tool gitleaks --mode Gate --run-id $(securityRunId) --history", azure);
        Assert.Contains("--tool gitleaks --mode Gate --run-id ${{ github.run_id }}-${{ github.run_attempt }} --history", github);
    }

    [Fact]
    public async Task InstallAsync_InvalidPipelineMode_ReturnsExitCode2()
    {
        var outcome = await new SecurityToolkitInstaller(Templates()).InstallAsync(
            new InstallOptions(_root, NonInteractive: true, DryRun: true),
            new Dictionary<string, object?> { ["pipelineMode"] = "gate" },
            toolProvisioner: null);

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.Contains("pipelineMode must be Report or Gate", StringComparison.Ordinal));
    }
}
