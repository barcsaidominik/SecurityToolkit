using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Core.Tests.Scanners;

public class AnalyzersScannerTests
{
    [Fact]
    public async Task RunAsync_BuildsWithoutNodeReuse_ThenShutsDownBuildServers()
    {
        var (context, root) = ScanContextTestFactory.Create(config: AnalyzersConfig(), @lock: EmptyLock());

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "app.sln"), "");

            var runner = new FakeProcessRunner();
            runner.Enqueue("dotnet", new ProcessResult(0, "", "")); // restore
            runner.Enqueue("dotnet", new ProcessResult(0, "src/a.cs(1,1): warning CA3075: Insecure DTD processing", "")); // build
            runner.Enqueue("dotnet", new ProcessResult(0, "", "")); // build-server shutdown

            var scanner = new AnalyzersScanner(runner);

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Equal(1, result.Findings);

            Assert.Equal(3, runner.Calls.Count);
            Assert.Contains("-nodeReuse:false", runner.Calls[0].Arguments);
            Assert.Contains("-nodeReuse:false", runner.Calls[1].Arguments);
            Assert.Equal(new[] { "build-server", "shutdown" }, runner.Calls[2].Arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CountsEf1002AndNu1507NextToCaRules_IgnoringOtherWarnings()
    {
        var (context, root) = ScanContextTestFactory.Create(config: AnalyzersConfig(), @lock: EmptyLock());

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "app.sln"), "");

            var build = string.Join("\n",
                "src/a.cs(1,1): warning CA3001: Review code for SQL injection vulnerabilities",
                "src/b.cs(5,9): warning EF1002: Method 'SqlQueryRaw' inserts interpolated strings",
                "src/A.csproj : warning NU1507: There are 2 package sources defined in your configuration",
                "src/B.csproj : warning NU1507: There are 2 package sources defined in your configuration",
                "src/B.csproj : warning NU1507: There are 2 package sources defined in your configuration",
                "src/c.cs(2,2): warning CS8618: Non-nullable property must contain a non-null value");
            var runner = new FakeProcessRunner();
            runner.Enqueue("dotnet", new ProcessResult(0, "", "")); // restore
            runner.Enqueue("dotnet", new ProcessResult(0, build, "")); // build
            runner.Enqueue("dotnet", new ProcessResult(0, "", "")); // build-server shutdown

            var result = Assert.Single(await new AnalyzersScanner(runner).RunAsync(context));

            Assert.Equal(4, result.Findings);
            Assert.StartsWith("CA3001: 1, EF1002: 1, NU1507: 2.", result.Message);
            Assert.Contains("minimal API", result.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_BuildServerShutdownFails_ScanResultStaysCompleted()
    {
        var (context, root) = ScanContextTestFactory.Create(config: AnalyzersConfig(), @lock: EmptyLock());

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "app.sln"), "");

            var runner = new FakeProcessRunner();
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(1, "", "shutdown failed"));

            var scanner = new AnalyzersScanner(runner);

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Equal(0, result.Findings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_BuildFails_FailedResultKeepsTheToolName()
    {
        var (context, root) = ScanContextTestFactory.Create(config: AnalyzersConfig(), @lock: EmptyLock());

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "app.sln"), "");

            var runner = new FakeProcessRunner();
            runner.Enqueue("dotnet", new ProcessResult(0, "", "")); // restore
            runner.Enqueue("dotnet", new ProcessResult(1, "", "error CS1002: ; expected")); // build fails
            runner.Enqueue("dotnet", new ProcessResult(0, "", "")); // build-server shutdown

            var result = Assert.Single(await new AnalyzersScanner(runner).RunAsync(context));

            Assert.Equal(ScanStatus.Failed, result.Status);
            // The scanner name, not the process name: status.json merges per tool name (a shared
            // --run-id), so a failed run must replace the earlier "analyzers" entry.
            Assert.Equal("analyzers", result.Tool);
            Assert.Equal("src/app.sln", result.Target);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Disabled_RunsNoProcessAtAll()
    {
        var config = AnalyzersConfig();
        config["tools"] = new Dictionary<string, object?> { ["analyzers"] = false };
        var (context, root) = ScanContextTestFactory.Create(config: config, @lock: EmptyLock());

        try
        {
            var runner = new FakeProcessRunner();
            var scanner = new AnalyzersScanner(runner);

            var results = await scanner.RunAsync(context);

            Assert.Empty(results);
            Assert.Empty(runner.Calls); // no stray build-server shutdown when nothing was built
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, object?> AnalyzersConfig() => new()
    {
        ["tools"] = new Dictionary<string, object?> { ["analyzers"] = true },
        ["dotnetTargets"] = new List<object?> { "src/app.sln" },
    };

    private static Dictionary<string, object?> EmptyLock() => new()
    {
        ["schemaVersion"] = 1L,
        ["tools"] = new Dictionary<string, object?>(),
    };
}
