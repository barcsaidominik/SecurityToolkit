using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Core.Tests.Scanners;

public class SemgrepScannerTests
{
    [Fact]
    public async Task RunAsync_CleanRun_ParsesResultsAndErrorsFromJsonReport()
    {
        var (context, root) = ScanContextTestFactory.Create(config: SemgrepConfig(), @lock: SemgrepLock());

        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue("docker", call =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "semgrep.json"), """
                    {
                      "results": [
                        { "check_id": "rule1", "extra": { "severity": "WARNING" } },
                        { "check_id": "rule2", "extra": { "severity": "ERROR" } }
                      ],
                      "errors": []
                    }
                    """);
                return new ProcessResult(0, "", "");
            });

            var scanner = new SemgrepScanner(runner, Array.Empty<string>());

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Equal(2, result.Findings);
            Assert.False(result.Blocking); // Report mode never blocks
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RequestsBothJsonAndSarifOutputInOneInvocation()
    {
        var (context, root) = ScanContextTestFactory.Create(config: SemgrepConfig(), @lock: SemgrepLock());

        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue("docker", call =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "semgrep.json"), """
                    { "results": [], "errors": [] }
                    """);
                Assert.Contains("--json-output", call.Arguments);
                Assert.Contains($"{context.ContainerOutputDirectory}/semgrep.json", call.Arguments);
                Assert.Contains("--sarif-output", call.Arguments);
                Assert.Contains($"{context.ContainerOutputDirectory}/semgrep.sarif", call.Arguments);
                return new ProcessResult(0, "", "");
            });

            var scanner = new SemgrepScanner(runner, Array.Empty<string>());

            await scanner.RunAsync(context);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_GateModeWithErrorSeverityHit_IsBlocking()
    {
        var (context, root) = ScanContextTestFactory.Create(config: SemgrepConfig(), @lock: SemgrepLock(), mode: ScanMode.Gate);

        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue("docker", call =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "semgrep.json"), """
                    {
                      "results": [ { "check_id": "rule2", "extra": { "severity": "ERROR" } } ],
                      "errors": []
                    }
                    """);
                return new ProcessResult(0, "", "");
            });

            var scanner = new SemgrepScanner(runner, Array.Empty<string>());

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.True(result.Blocking);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_FrontendScope_ReportsFrontendTarget()
    {
        var config = SemgrepConfig();
        config["frontendTargets"] = new List<object?> { "src/Client" };
        var (context, root) = ScanContextTestFactory.Create(config: config, @lock: SemgrepLock(), semgrepScope: SemgrepScope.Frontend);

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "Client"));
            var runner = new FakeProcessRunner();
            runner.Enqueue("docker", call =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "semgrep.json"), """
                    {
                      "results": [ { "check_id": "rule1", "extra": { "severity": "WARNING" } } ],
                      "errors": []
                    }
                    """);
                return new ProcessResult(0, "", "");
            });

            var scanner = new SemgrepScanner(runner, Array.Empty<string>());

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal("frontend", result.Target);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_StrictExitThreeWithScanErrors_PreservesFindingsButFailsAndWritesCoverageFile()
    {
        var (context, root) = ScanContextTestFactory.Create(config: SemgrepConfig(), @lock: SemgrepLock());

        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue("docker", call =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "semgrep.json"), """
                    {
                      "results": [ { "check_id": "rule1", "extra": { "severity": "WARNING" } } ],
                      "errors": [ { "path": "src/Broken.cs" } ]
                    }
                    """);
                return new ProcessResult(3, "", "");
            });

            var scanner = new SemgrepScanner(runner, Array.Empty<string>());

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Failed, result.Status);
            Assert.Equal(1, result.Findings); // findings preserved despite incomplete analysis

            var coveragePath = Path.Combine(context.OutputDirectory, "semgrep-coverage.json");
            Assert.True(File.Exists(coveragePath));
            var coverage = File.ReadAllText(coveragePath);
            Assert.Contains("src/Broken.cs", coverage);
            Assert.Contains("\"complete\": false", coverage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, object?> SemgrepConfig() => new()
    {
        ["tools"] = new Dictionary<string, object?> { ["semgrep"] = true },
    };

    private static Dictionary<string, object?> SemgrepLock() => new()
    {
        ["schemaVersion"] = 1L,
        ["tools"] = new Dictionary<string, object?>
        {
            ["semgrep"] = new Dictionary<string, object?>
            {
                ["ready"] = true,
                ["image"] = "semgrep/semgrep@sha256:" + new string('b', 64),
            },
        },
    };
}
