using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Core.Tests.Scanners;

public class OsvScannerTests
{
    [Fact]
    public async Task RunAsync_NoVulnerabilities_ReportsCompletedWithZeroFindings()
    {
        var (context, root) = ScanContextTestFactory.Create(config: OsvConfig(), @lock: OsvLock());

        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue("docker", call =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "osv.json"), """{ "results": [] }""");
                return new ProcessResult(0, "", "");
            });

            var scanner = new OsvScanner(runner, Array.Empty<string>());

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal("osv", result.Tool);
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
    public async Task RunAsync_CountsVulnerabilitiesAcrossPackages()
    {
        var (context, root) = ScanContextTestFactory.Create(config: OsvConfig(), @lock: OsvLock());

        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue("docker", call =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "osv.json"), """
                    {
                      "results": [
                        {
                          "packages": [
                            { "vulnerabilities": [ {}, {} ] },
                            { "vulnerabilities": [ {} ] }
                          ]
                        }
                      ]
                    }
                    """);
                return new ProcessResult(1, "", "");
            });

            var scanner = new OsvScanner(runner, Array.Empty<string>());

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Equal(3, result.Findings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ExitCodeOneWithoutVulnerabilities_IsTreatedAsFailure()
    {
        var (context, root) = ScanContextTestFactory.Create(config: OsvConfig(), @lock: OsvLock());

        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue("docker", call =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "osv.json"), """{ "results": [] }""");
                return new ProcessResult(1, "", "");
            });

            var scanner = new OsvScanner(runner, Array.Empty<string>());

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Failed, result.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ImageNotPinned_ReportsFailedWithoutRunningDocker()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: OsvConfig(),
            @lock: new Dictionary<string, object?> { ["schemaVersion"] = 1L, ["tools"] = new Dictionary<string, object?>() });

        try
        {
            var runner = new FakeProcessRunner();
            var scanner = new OsvScanner(runner, Array.Empty<string>());

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
            config: new Dictionary<string, object?> { ["tools"] = new Dictionary<string, object?> { ["osv"] = false } },
            @lock: OsvLock());

        try
        {
            var scanner = new OsvScanner(new FakeProcessRunner(), Array.Empty<string>());

            var results = await scanner.RunAsync(context);

            Assert.Empty(results);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, object?> OsvConfig() => new()
    {
        ["tools"] = new Dictionary<string, object?> { ["osv"] = true },
    };

    private static Dictionary<string, object?> OsvLock() => new()
    {
        ["schemaVersion"] = 1L,
        ["tools"] = new Dictionary<string, object?>
        {
            ["osv"] = new Dictionary<string, object?>
            {
                ["ready"] = true,
                ["image"] = "ghcr.io/google/osv-scanner@sha256:" + new string('a', 64),
            },
        },
    };
}
