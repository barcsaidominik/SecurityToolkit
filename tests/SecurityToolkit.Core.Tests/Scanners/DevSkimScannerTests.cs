using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Core.Tests.Scanners;

public class DevSkimScannerTests
{
    [Fact]
    public async Task RunAsync_NoFindings_ReportsCompletedWithZeroFindings()
    {
        var (context, root) = ScanContextTestFactory.Create(config: DevSkimConfig(), @lock: DevSkimLock());

        try
        {
            var runner = new FakeProcessRunner();
            var executableName = "devskim" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
            var executable = Path.Combine(context.ToolkitDirectory, "bin", executableName);
            runner.Enqueue(executable, call =>
            {
                var sarifPath = Path.Combine(context.OutputDirectory, "devskim.sarif");
                File.WriteAllText(sarifPath, """{ "runs": [ { "results": [] } ] }""");
                return new ProcessResult(0, "", "");
            });

            var scanner = new DevSkimScanner(runner);

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal("devskim", result.Tool);
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Equal(0, result.Findings);
            Assert.False(result.Blocking);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ConfiguredIgnores_AreAppendedToTheCommandLine()
    {
        var config = DevSkimConfig();
        config["devskim"] = new Dictionary<string, object?>
        {
            ["ignoreGlobs"] = new List<object?> { "**/*.Tests/**" },
            ["ignoreRuleIds"] = new List<object?> { "DS162092", "DS176209" },
        };
        var (context, root) = ScanContextTestFactory.Create(config: config, @lock: DevSkimLock());

        try
        {
            var runner = new FakeProcessRunner();
            var executable = Path.Combine(context.ToolkitDirectory, "bin", "devskim" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            runner.Enqueue(executable, call =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "devskim.sarif"), """{ "runs": [ { "results": [] } ] }""");
                return new ProcessResult(0, "", "");
            });

            await new DevSkimScanner(runner).RunAsync(context);

            var arguments = runner.Calls.Single().Arguments.ToList();
            var globs = arguments[arguments.IndexOf("--ignore-globs") + 1];
            Assert.StartsWith("**/.git/**,", globs);
            Assert.EndsWith(",**/*.Tests/**", globs);
            Assert.Contains("**/.sonarqube/**", globs);
            // The generated pipelines (toolkitRelease checksums look like keys to DS173237).
            Assert.Contains("**/azure-security-toolkit.yml", globs);
            Assert.Contains("**/.github/workflows/security-toolkit.yml", globs);
            Assert.Equal("DS162092,DS176209", arguments[arguments.IndexOf("--ignore-rule-ids") + 1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_SarifLinks_UseForwardSlashesAndNoUserInfo()
    {
        var (context, root) = ScanContextTestFactory.Create(config: DevSkimConfig(), @lock: DevSkimLock());

        try
        {
            var runner = new FakeProcessRunner();
            var executable = Path.Combine(context.ToolkitDirectory, "bin", "devskim" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            var sarifPath = Path.Combine(context.OutputDirectory, "devskim.sarif");
            runner.Enqueue(executable, call =>
            {
                File.WriteAllText(sarifPath,
                    """{ "runs": [ { "versionControlProvenance": [ { "repositoryUri": "https://org@dev.azure.com/org/proj/_git/repo" } ], "results": [ { "ruleId": "DS205000", "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "src\\WebApi\\nuget.config" } } } ] } ] } ] }""");
                return new ProcessResult(1, "", "");
            });

            var result = Assert.Single(await new DevSkimScanner(runner).RunAsync(context));

            Assert.Equal(ScanStatus.Completed, result.Status);
            var sarif = File.ReadAllText(sarifPath);
            Assert.Contains("\"uri\": \"src/WebApi/nuget.config\"", sarif);
            Assert.DoesNotContain("\\\\", sarif);
            Assert.Contains("\"repositoryUri\": \"https://dev.azure.com/org/proj/_git/repo\"", sarif);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CountsResultsAcrossRuns()
    {
        var (context, root) = ScanContextTestFactory.Create(config: DevSkimConfig(), @lock: DevSkimLock());

        try
        {
            var runner = new FakeProcessRunner();
            var executableName = "devskim" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
            var executable = Path.Combine(context.ToolkitDirectory, "bin", executableName);
            runner.Enqueue(executable, call =>
            {
                var sarifPath = Path.Combine(context.OutputDirectory, "devskim.sarif");
                File.WriteAllText(sarifPath, """{ "runs": [ { "results": [ {}, {} ] } ] }""");
                return new ProcessResult(1, "", "");
            });

            var scanner = new DevSkimScanner(runner);

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Equal(2, result.Findings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_NotProvisioned_ReportsFailedWithoutRunningExecutable()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: DevSkimConfig(),
            @lock: new Dictionary<string, object?> { ["schemaVersion"] = 1L, ["tools"] = new Dictionary<string, object?>() });

        try
        {
            var runner = new FakeProcessRunner();
            var scanner = new DevSkimScanner(runner);

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Failed, result.Status);
            Assert.Empty(runner.Calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ToolDisabled_ReturnsNoResults()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: new Dictionary<string, object?> { ["tools"] = new Dictionary<string, object?> { ["devskim"] = false } },
            @lock: DevSkimLock());

        try
        {
            var scanner = new DevSkimScanner(new FakeProcessRunner());

            var results = await scanner.RunAsync(context);

            Assert.Empty(results);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, object?> DevSkimConfig() => new()
    {
        ["tools"] = new Dictionary<string, object?> { ["devskim"] = true },
    };

    private static Dictionary<string, object?> DevSkimLock() => new()
    {
        ["schemaVersion"] = 1L,
        ["tools"] = new Dictionary<string, object?>
        {
            ["devskim"] = new Dictionary<string, object?> { ["ready"] = true },
        },
    };
}
