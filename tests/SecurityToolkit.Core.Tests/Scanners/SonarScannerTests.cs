using SecurityToolkit.Core.Scanners;
using SecurityToolkit.Core.Tests.Installation;

namespace SecurityToolkit.Core.Tests.Scanners;

public class SonarScannerTests
{
    private static HttpClient StubIssueSearchClient(string responseBody = """{"total":0,"issues":[]}""")
        => new(new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody),
        }));

    [Fact]
    public async Task RunAsync_BuildStepThrows_EndIsStillInvoked()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: SonarConfig(),
            @lock: SonarLock(),
            includeSonar: true,
            sonarToken: "s3cr3t-token");

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "app.sln"), "");

            var executableName = "dotnet-sonarscanner" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
            var executablePath = Path.Combine(context.ToolkitDirectory, "bin", executableName);

            var runner = new FakeProcessRunner();
            runner.Enqueue(executablePath, new ProcessResult(0, "begin ok", ""));
            // The build step fails (e.g. compile error) — "end" must still run afterward.
            runner.Enqueue("dotnet", _ => throw new InvalidOperationException("build failed"));
            runner.Enqueue(executablePath, new ProcessResult(0, "end ok", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", "")); // build-server shutdown

            var scanner = new SonarScanner(runner, StubIssueSearchClient());

            var results = await scanner.RunAsync(context);

            Assert.Equal(4, runner.Calls.Count);
            Assert.Equal(executablePath, runner.Calls[0].FileName);
            Assert.Contains("begin", runner.Calls[0].Arguments);
            Assert.Equal("dotnet", runner.Calls[1].FileName);
            Assert.Equal(executablePath, runner.Calls[2].FileName);
            Assert.Contains("end", runner.Calls[2].Arguments);
            // Build servers are shut down only AFTER "end", even when the build failed.
            Assert.Equal(new[] { "build-server", "shutdown" }, runner.Calls[3].Arguments);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Failed, result.Status);
            Assert.Contains("build failed", result.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_BeginAndBuildSucceed_EndRunsAndResultIsCompleted()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: SonarConfig(),
            @lock: SonarLock(),
            includeSonar: true,
            sonarToken: "s3cr3t-token");

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "app.sln"), "");

            var executableName = "dotnet-sonarscanner" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
            var executablePath = Path.Combine(context.ToolkitDirectory, "bin", executableName);

            var runner = new FakeProcessRunner();
            runner.Enqueue(executablePath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));
            runner.Enqueue(executablePath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", "")); // build-server shutdown

            var scanner = new SonarScanner(runner, StubIssueSearchClient("""{"total":9,"issues":[]}"""));

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Equal(9, result.Findings);
            Assert.Equal(4, runner.Calls.Count);
            Assert.Contains("-nodeReuse:false", runner.Calls[1].Arguments);
            Assert.Equal(new[] { "build-server", "shutdown" }, runner.Calls[3].Arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_IssueLookupFails_StillReturnsCompletedWithNullFindings()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: SonarConfig(),
            @lock: SonarLock(),
            includeSonar: true,
            sonarToken: "s3cr3t-token");

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "app.sln"), "");

            var executableName = "dotnet-sonarscanner" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
            var executablePath = Path.Combine(context.ToolkitDirectory, "bin", executableName);

            var runner = new FakeProcessRunner();
            runner.Enqueue(executablePath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));
            runner.Enqueue(executablePath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", "")); // build-server shutdown

            using var httpClient = new HttpClient(new FakeHttpMessageHandler(
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));
            var scanner = new SonarScanner(runner, httpClient);

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Null(result.Findings);
            Assert.Contains("Sonar issue lookup failed", result.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_NotIncludeSonar_ReturnsNotRunWithoutInvokingAnyProcess()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: SonarConfig(),
            @lock: SonarLock(),
            includeSonar: false);

        try
        {
            var runner = new FakeProcessRunner();
            var scanner = new SonarScanner(runner, StubIssueSearchClient());

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.NotRun, result.Status);
            Assert.Empty(runner.Calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CoverageEnabled_RunsDotnetTestsUnderDotnetCoverageBetweenBuildAndEnd()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: SonarConfig(coverage: true, testFilter: "Category!=Db"),
            @lock: SonarLock(dotnetCoverageReady: true),
            includeSonar: true,
            sonarToken: "s3cr3t-token");

        try
        {
            CreateSolution(root);
            var (scannerPath, coveragePath) = ToolPaths(context);
            var reportPath = Path.Combine(context.OutputDirectory, "dotnet-coverage.xml");

            var runner = new FakeProcessRunner();
            runner.Enqueue(scannerPath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));
            runner.Enqueue(coveragePath, _ =>
            {
                File.WriteAllText(reportPath, "<results/>");
                return new ProcessResult(0, "", "");
            });
            runner.Enqueue(scannerPath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", "")); // build-server shutdown

            var results = await new SonarScanner(runner, StubIssueSearchClient()).RunAsync(context);

            Assert.Contains($"/d:sonar.cs.vscoveragexml.reportsPaths={reportPath.Replace('\\', '/')}", runner.Calls[0].Arguments);
            Assert.DoesNotContain(runner.Calls[0].Arguments, a => a.StartsWith("/d:sonar.javascript.lcov", StringComparison.Ordinal));
            Assert.Equal(
                new[]
                {
                    "collect", "--nologo", "-f", "xml", "-o", reportPath,
                    "dotnet", "test", Path.Combine(root, "src", "app.sln"), "--no-build", "--filter", "Category!=Db",
                },
                runner.Calls[2].Arguments);
            Assert.Contains("end", runner.Calls[3].Arguments);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Contains("Coverage: .NET ok", result.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CoverageTestsFail_StaysCompletedWithWarning()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: SonarConfig(coverage: true),
            @lock: SonarLock(dotnetCoverageReady: true),
            includeSonar: true,
            sonarToken: "s3cr3t-token");

        try
        {
            CreateSolution(root);
            var (scannerPath, coveragePath) = ToolPaths(context);

            var runner = new FakeProcessRunner();
            runner.Enqueue(scannerPath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));
            runner.Enqueue(coveragePath, _ =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "dotnet-coverage.xml"), "<results/>");
                return new ProcessResult(1, "", "2 tests failed");
            });
            runner.Enqueue(scannerPath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));

            var results = await new SonarScanner(runner, StubIssueSearchClient()).RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.False(result.Blocking);
            Assert.Contains(".NET report imported, but tests failed (exit 1", result.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CoverageCollectionThrows_EndIsStillInvokedAndResultIsCompleted()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: SonarConfig(coverage: true),
            @lock: SonarLock(dotnetCoverageReady: true),
            includeSonar: true,
            sonarToken: "s3cr3t-token");

        try
        {
            CreateSolution(root);
            var (scannerPath, coveragePath) = ToolPaths(context);

            var runner = new FakeProcessRunner();
            runner.Enqueue(scannerPath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));
            runner.Enqueue(coveragePath, _ => throw new TimeoutException("tests hung"));
            runner.Enqueue(scannerPath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));

            var results = await new SonarScanner(runner, StubIssueSearchClient()).RunAsync(context);

            Assert.Contains("end", runner.Calls[3].Arguments);
            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Contains(".NET failed (tests hung)", result.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CoverageEnabledButDotnetCoverageNotProvisioned_SkipsDotnetTestsWithNote()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: SonarConfig(coverage: true),
            @lock: SonarLock(dotnetCoverageReady: false),
            includeSonar: true,
            sonarToken: "s3cr3t-token");

        try
        {
            CreateSolution(root);
            var (scannerPath, _) = ToolPaths(context);

            var runner = new FakeProcessRunner();
            runner.Enqueue(scannerPath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));
            runner.Enqueue(scannerPath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));

            var results = await new SonarScanner(runner, StubIssueSearchClient()).RunAsync(context);

            Assert.Equal(4, runner.Calls.Count);
            Assert.DoesNotContain(runner.Calls[0].Arguments, a => a.Contains("vscoveragexml", StringComparison.Ordinal));
            Assert.Contains("dotnet-coverage is not provisioned", Assert.Single(results).Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CoverageEnabledWithAngularWorkspace_RunsEachProjectWithItsBuildersFlags()
    {
        var config = SonarConfig(coverage: true);
        config["frontendTargets"] = new List<object?> { "src/Client", "src/Static" };

        var (context, root) = ScanContextTestFactory.Create(
            config: config,
            @lock: SonarLock(dotnetCoverageReady: false),
            includeSonar: true,
            sonarToken: "s3cr3t-token");

        try
        {
            CreateSolution(root);
            var client = Path.Combine(root, "src", "Client");
            Directory.CreateDirectory(client);
            File.WriteAllText(Path.Combine(client, "angular.json"), """
                {
                  "projects": {
                    "web": { "architect": { "test": { "builder": "@angular/build:karma", "options": {} } } },
                    "pwa": { "architect": { "test": { "builder": "@angular/build:unit-test", "options": { "runner": "karma" } } } },
                    "vit": { "architect": { "test": { "builder": "@angular/build:unit-test" } } },
                    "lib": { "architect": { "build": { "builder": "@angular/build:ng-packagr" } } }
                  }
                }
                """);
            Directory.CreateDirectory(Path.Combine(root, "src", "Static")); // not an Angular workspace

            var (scannerPath, _) = ToolPaths(context);
            var npm = new NpmInvocation("node", new[] { "npm-cli.js" });
            string Coverage(int i) => Path.Combine(context.OutputDirectory, $"frontend-coverage-{i}");
            string Karma(int i) => Path.Combine(context.OutputDirectory, $"karma-coverage-{i}.conf.cjs");
            var alreadyAbsolute = Path.Combine(root, "elsewhere", "b.ts");
            ProcessResult WriteLcov(int i)
            {
                Directory.CreateDirectory(Coverage(i));
                File.WriteAllText(Path.Combine(Coverage(i), "lcov.info"), i == 1
                    ? $"TN:\nSF:src{Path.DirectorySeparatorChar}app{Path.DirectorySeparatorChar}a.ts\nDA:1,1\nend_of_record\nSF:{alreadyAbsolute}\nend_of_record\n"
                    : "TN:\n");
                return new ProcessResult(0, "", "");
            }

            var runner = new FakeProcessRunner();
            runner.Enqueue(scannerPath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));
            runner.Enqueue("node", new ProcessResult(0, "", "")); // npm ci, once per workspace
            runner.Enqueue("node", _ => WriteLcov(1)); // ng test web
            runner.Enqueue("node", _ => WriteLcov(2)); // ng test pwa
            runner.Enqueue(scannerPath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));

            var results = await new SonarScanner(runner, StubIssueSearchClient(), () => npm).RunAsync(context);

            Assert.Contains(
                "/d:sonar.javascript.lcov.reportPaths=" +
                $"{Path.Combine(Coverage(1), "lcov.info").Replace('\\', '/')},{Path.Combine(Coverage(2), "lcov.info").Replace('\\', '/')}",
                runner.Calls[0].Arguments);
            Assert.Equal(new[] { "npm-cli.js", "ci", "--no-audit", "--no-fund" }, runner.Calls[2].Arguments);
            Assert.Equal(client, runner.Calls[2].WorkingDirectory);

            var web = runner.Calls[3];
            Assert.Equal(client, web.WorkingDirectory);
            Assert.Equal(new[] { "npm-cli.js", "exec", "--no", "--", "ng", "test", "web" }, web.Arguments.Take(7));
            Assert.Contains("--code-coverage", web.Arguments);
            Assert.Contains($"--karma-config={Karma(1)}", web.Arguments);
            Assert.DoesNotContain("--coverage", web.Arguments);

            var pwa = runner.Calls[4];
            Assert.Equal("pwa", pwa.Arguments[6]);
            Assert.Contains("--coverage", pwa.Arguments);
            Assert.Contains($"--runner-config={Karma(2)}", pwa.Arguments);
            Assert.DoesNotContain("--code-coverage", pwa.Arguments);
            Assert.DoesNotContain(pwa.Arguments, a => a.StartsWith("--karma-config", StringComparison.Ordinal));

            var karmaConfig = File.ReadAllText(Karma(1));
            Assert.Contains("lcovonly", karmaConfig);
            Assert.Contains(System.Text.Json.JsonSerializer.Serialize(Coverage(1)), karmaConfig);
            Assert.DoesNotContain("type: 'html'", karmaConfig);

            // Workspace-relative SF: paths are anchored to the workspace (the Code Coverage tab drops
            // paths it can't resolve from the repo root); absolute ones and other records stay as they were.
            var lcov = File.ReadAllLines(Path.Combine(Coverage(1), "lcov.info"));
            Assert.Equal(
                new[] { "TN:", "SF:" + Path.Combine(client, "src", "app", "a.ts"), "DA:1,1", "end_of_record", "SF:" + alreadyAbsolute, "end_of_record" },
                lcov);

            var message = Assert.Single(results).Message;
            Assert.Contains("src/Static skipped (not an Angular workspace)", message);
            Assert.Contains("src/Client:vit skipped (unsupported test builder @angular/build:unit-test, runner vitest)", message);
            Assert.Contains("src/Client:web ok", message);
            Assert.Contains("src/Client:pwa ok", message);
            Assert.DoesNotContain("lib", message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CoverageDisabled_AddsNoCoverageArgumentsOrSteps()
    {
        var config = SonarConfig();
        config["frontendTargets"] = new List<object?> { "src/Client" };

        var (context, root) = ScanContextTestFactory.Create(
            config: config,
            @lock: SonarLock(dotnetCoverageReady: true),
            includeSonar: true,
            sonarToken: "s3cr3t-token");

        try
        {
            CreateSolution(root);
            var (scannerPath, _) = ToolPaths(context);

            var runner = new FakeProcessRunner();
            runner.Enqueue(scannerPath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));
            runner.Enqueue(scannerPath, new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));

            var results = await new SonarScanner(runner, StubIssueSearchClient()).RunAsync(context);

            Assert.Equal(4, runner.Calls.Count);
            Assert.DoesNotContain(runner.Calls[0].Arguments, a => a.Contains("coverage", StringComparison.OrdinalIgnoreCase) && a.Contains("reportPaths", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("Coverage", Assert.Single(results).Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateSolution(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "app.sln"), "");
    }

    private static (string Scanner, string Coverage) ToolPaths(ScanContext context)
    {
        var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return (
            Path.Combine(context.ToolkitDirectory, "bin", "dotnet-sonarscanner" + suffix),
            Path.Combine(context.ToolkitDirectory, "bin", "dotnet-coverage" + suffix));
    }

    private static Dictionary<string, object?> SonarConfig(bool coverage = false, string testFilter = "") => new()
    {
        ["tools"] = new Dictionary<string, object?> { ["sonar"] = true },
        ["sonar"] = new Dictionary<string, object?>
        {
            ["url"] = "http://localhost:9000",
            ["projectKey"] = "test-project",
            ["dotnetTarget"] = "src/app.sln",
            ["coverage"] = coverage,
            ["testFilter"] = testFilter,
        },
    };

    private static Dictionary<string, object?> SonarLock(bool dotnetCoverageReady = false) => new()
    {
        ["schemaVersion"] = 1L,
        ["tools"] = new Dictionary<string, object?>
        {
            ["sonarScanner"] = new Dictionary<string, object?> { ["ready"] = true },
            ["dotnetCoverage"] = new Dictionary<string, object?> { ["ready"] = dotnetCoverageReady },
        },
    };
}
