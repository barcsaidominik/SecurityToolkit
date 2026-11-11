using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Core.Tests.Scanners;

public class GitleaksScannerTests
{
    [Fact]
    public async Task RunAsync_FindingsContainSecretMatchLine_AreRedactedInFinalReport()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: GitleaksConfig(),
            @lock: GitleaksLock());

        try
        {
            var mountArguments = new[] { "--rm" };
            var runner = new FakeProcessRunner();
            runner.Enqueue("docker", call =>
            {
                var reportPath = Path.Combine(context.OutputDirectory, "gitleaks.json");
                File.WriteAllText(reportPath, """
                    [
                      {
                        "Description": "AWS Access Key",
                        "Secret": "AKIAABCDEFGHIJKLMNOP",
                        "Match": "aws_key=AKIAABCDEFGHIJKLMNOP",
                        "Line": "aws_key=AKIAABCDEFGHIJKLMNOP // leaked",
                        "File": "config.txt"
                      }
                    ]
                    """);
                return new ProcessResult(10, "", "");
            });

            var scanner = new GitleaksScanner(runner, mountArguments);

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Equal(1, result.Findings);

            var finalReport = File.ReadAllText(Path.Combine(context.OutputDirectory, "gitleaks.json"));
            Assert.DoesNotContain("AKIAABCDEFGHIJKLMNOP", finalReport);
            Assert.Contains("[REDACTED]", finalReport);
            Assert.Contains("config.txt", finalReport); // non-sensitive fields survive
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_NumericFields_RemainNumbersAfterRedaction()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: GitleaksConfig(),
            @lock: GitleaksLock());

        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue("docker", call =>
            {
                var reportPath = Path.Combine(context.OutputDirectory, "gitleaks.json");
                File.WriteAllText(reportPath, """
                    [
                      {
                        "Description": "AWS Access Key",
                        "Secret": "AKIAABCDEFGHIJKLMNOP",
                        "Match": "aws_key=AKIAABCDEFGHIJKLMNOP",
                        "Line": "aws_key=AKIAABCDEFGHIJKLMNOP // leaked",
                        "File": "config.txt",
                        "StartLine": 42,
                        "EndLine": 42,
                        "Verified": false
                      }
                    ]
                    """);
                return new ProcessResult(10, "", "");
            });

            var scanner = new GitleaksScanner(runner, Array.Empty<string>());

            await scanner.RunAsync(context);

            var finalReport = File.ReadAllText(Path.Combine(context.OutputDirectory, "gitleaks.json"));
            var node = System.Text.Json.Nodes.JsonNode.Parse(finalReport);
            var hit = Assert.IsType<System.Text.Json.Nodes.JsonObject>(Assert.Single(Assert.IsType<System.Text.Json.Nodes.JsonArray>(node)));

            // Secret/Match/Line become redacted strings...
            Assert.Equal("[REDACTED]", hit["Secret"]!.GetValue<string>());
            Assert.Equal("[REDACTED]", hit["Match"]!.GetValue<string>());
            Assert.Equal("[REDACTED]", hit["Line"]!.GetValue<string>());

            // ...but every other field keeps its native JSON type: numbers stay numbers, not strings.
            Assert.Equal(System.Text.Json.JsonValueKind.Number, hit["StartLine"]!.GetValue<System.Text.Json.JsonElement>().ValueKind);
            Assert.Equal(42, hit["StartLine"]!.GetValue<int>());
            Assert.Equal(System.Text.Json.JsonValueKind.Number, hit["EndLine"]!.GetValue<System.Text.Json.JsonElement>().ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.False, hit["Verified"]!.GetValue<System.Text.Json.JsonElement>().ValueKind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WritesSarifReport_WithoutLeakingRawSecretValue()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: GitleaksConfig(),
            @lock: GitleaksLock());

        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue("docker", call =>
            {
                var reportPath = Path.Combine(context.OutputDirectory, "gitleaks.json");
                File.WriteAllText(reportPath, """
                    [
                      {
                        "Description": "AWS Access Key",
                        "RuleID": "aws-access-key",
                        "Secret": "AKIAABCDEFGHIJKLMNOP",
                        "Match": "aws_key=AKIAABCDEFGHIJKLMNOP",
                        "Line": "aws_key=AKIAABCDEFGHIJKLMNOP // leaked",
                        "File": "config.txt",
                        "StartLine": 42
                      }
                    ]
                    """);
                return new ProcessResult(10, "", "");
            });

            var scanner = new GitleaksScanner(runner, Array.Empty<string>());

            await scanner.RunAsync(context);

            var sarifPath = Path.Combine(context.OutputDirectory, "gitleaks.sarif");
            Assert.True(File.Exists(sarifPath));

            var sarif = File.ReadAllText(sarifPath);
            Assert.DoesNotContain("AKIAABCDEFGHIJKLMNOP", sarif);
            Assert.Contains("\"version\": \"2.1.0\"", sarif);
            Assert.Contains("aws-access-key", sarif);
            Assert.Contains("config.txt", sarif);
            Assert.Contains("42", sarif);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Staged_UsesGitScanModeWithPreCommitFlags()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: GitleaksConfig(),
            @lock: GitleaksLock(),
            staged: true);

        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue("git", new ProcessResult(0, "true\n", ""));
            runner.Enqueue("docker", call =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "gitleaks.json"), "[]");
                Assert.Contains("--staged", call.Arguments);
                Assert.Contains("--pre-commit", call.Arguments);
                return new ProcessResult(0, "", "");
            });

            var scanner = new GitleaksScanner(runner, Array.Empty<string>());

            var results = await scanner.RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal("git", result.Target);
            Assert.Equal(0, result.Findings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_StagedInReportMode_FindingIsBlocking()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: GitleaksConfig(),
            @lock: GitleaksLock(),
            mode: ScanMode.Report,
            staged: true);

        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue("git", new ProcessResult(0, "true\n", ""));
            runner.Enqueue("docker", _ =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "gitleaks.json"),
                    """[{"RuleID":"generic-api-key","File":"a.txt","StartLine":1,"Secret":"x","Match":"x","Line":"x"}]""");
                return new ProcessResult(10, "", ""); // --exit-code 10: leaks found
            });

            var results = await new GitleaksScanner(runner, Array.Empty<string>()).RunAsync(context);

            var result = Assert.Single(results);
            Assert.Equal(1, result.Findings);
            Assert.True(result.Blocking);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{}", 0)]
    [InlineData("""{"findings":[{"RuleID":"x"}]}""", 10)]
    [InlineData("null", 0)]
    [InlineData("[]", 10)]
    public async Task RunAsync_MalformedOrInconsistentReport_FailsInsteadOfReportingClean(string report, int exitCode)
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: GitleaksConfig(),
            @lock: GitleaksLock(),
            mode: ScanMode.Gate);

        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue("docker", _ =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "gitleaks.json"), report);
                return new ProcessResult(exitCode, "", "");
            });

            var result = Assert.Single(await new GitleaksScanner(runner, Array.Empty<string>()).RunAsync(context));

            Assert.Equal(ScanStatus.Failed, result.Status);
            Assert.Null(result.Findings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CleanReport_Completes()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: GitleaksConfig(),
            @lock: GitleaksLock(),
            mode: ScanMode.Gate);

        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue("docker", _ =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "gitleaks.json"), "[]");
                return new ProcessResult(0, "", "");
            });

            var result = Assert.Single(await new GitleaksScanner(runner, Array.Empty<string>()).RunAsync(context));

            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Equal(0, result.Findings);
            Assert.False(result.Blocking);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, object?> GitleaksConfig() => new()
    {
        ["tools"] = new Dictionary<string, object?> { ["gitleaks"] = true },
    };

    private static Dictionary<string, object?> GitleaksLock() => new()
    {
        ["schemaVersion"] = 1L,
        ["tools"] = new Dictionary<string, object?>
        {
            ["gitleaks"] = new Dictionary<string, object?>
            {
                ["ready"] = true,
                ["image"] = "zricethezav/gitleaks@sha256:" + new string('a', 64),
            },
        },
    };
}
