using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Core.Tests.Scanners;

public class NuGetScannerTests
{
    [Fact]
    public async Task RunAsync_ListPackageFails_ReportsNuGetToolNameNotDotnet()
    {
        var (context, root) = ScanContextTestFactory.Create(config: NuGetConfig(), @lock: NuGetLock());

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "app.sln"), "");

            var runner = new FakeProcessRunner();
            runner.Enqueue("dotnet", new ProcessResult(0, "", "")); // restore succeeds
            runner.Enqueue("dotnet", new ProcessResult(1, "", "error: Response status code does not indicate success: 401 (Unauthorized).")); // list fails

            var scanner = new NuGetScanner(runner);

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal("nuget", result.Tool); // must not regress to the generic process name "dotnet"
            Assert.Equal(ScanStatus.Failed, result.Status);
            Assert.Contains("dotnet-1-nuget.log", result.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_AuditSourceUnavailable_ReportsFailedWithoutRunningListPackage()
    {
        var (context, root) = ScanContextTestFactory.Create(config: NuGetConfig(), @lock: NuGetLock());

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "app.sln"), "");

            var runner = new FakeProcessRunner();
            runner.Enqueue("dotnet", new ProcessResult(0, "", "warning NU1900: Error occurred while getting package vulnerability data."));

            var scanner = new NuGetScanner(runner);

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal("nuget", result.Tool);
            Assert.Equal(ScanStatus.Failed, result.Status);
            Assert.Single(runner.Calls); // "list package" is never invoked
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_NoVulnerabilities_ReportsCompletedWithZeroFindings()
    {
        var (context, root) = ScanContextTestFactory.Create(config: NuGetConfig(), @lock: NuGetLock());

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "app.sln"), "");

            var runner = new FakeProcessRunner();
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, """{ "projects": [] }""", ""));

            var scanner = new NuGetScanner(runner);

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal("nuget", result.Tool);
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Equal(0, result.Findings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, object?> NuGetConfig() => new()
    {
        ["tools"] = new Dictionary<string, object?> { ["nuget"] = true },
        ["dotnetTargets"] = new List<object?> { "src/app.sln" },
    };

    private static Dictionary<string, object?> NuGetLock() => new()
    {
        ["schemaVersion"] = 1L,
        ["tools"] = new Dictionary<string, object?>(),
    };
}
