using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Core.Tests.Scanners;

public class FindingBaselineTests
{
    private sealed class FixedScanner : IScanner
    {
        private readonly ScanResult[] _results;

        public FixedScanner(string name, params ScanResult[] results)
        {
            Name = name;
            _results = results;
        }

        public string Name { get; }

        public Task<IReadOnlyList<ScanResult>> RunAsync(ScanContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ScanResult>>(_results);
    }

    private static ScanResult Gitleaks(params string[] fingerprints)
        => new("gitleaks", "dir", ScanStatus.Completed, fingerprints.Length, Blocking: fingerprints.Length > 0, Fingerprints: fingerprints);

    private static void WriteBaseline(ScanContext context, string json)
    {
        Directory.CreateDirectory(context.ToolkitDirectory);
        File.WriteAllText(Path.Combine(context.ToolkitDirectory, FindingBaseline.FileName), json);
    }

    private static Task<ScanOutcome> Run(ScanContext context, params IScanner[] scanners)
        => new ScanOrchestrator(new FakeProcessRunner(), scanners).RunAsync(context);

    [Fact]
    public void CountNew_TreatsTheBaselineAsAMultiset()
    {
        var known = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };

        Assert.Equal(0, FindingBaseline.CountNew(new[] { "a", "b", "b" }, known));
        Assert.Equal(2, FindingBaseline.CountNew(new[] { "a", "a", "c" }, known));
        Assert.Equal(3, FindingBaseline.CountNew(new[] { "a", "b", "c" }, known: null));
    }

    [Fact]
    public async Task Gate_FindingsAllInBaseline_DoNotBlock()
    {
        var (context, root) = ScanContextTestFactory.Create(mode: ScanMode.Gate);
        try
        {
            WriteBaseline(context, """{ "schemaVersion": 1, "tools": { "gitleaks": { "fp1": 1, "fp2": 1 } } }""");

            var outcome = await Run(context, new FixedScanner("gitleaks", Gitleaks("fp1", "fp2")));

            Assert.Equal(0, outcome.ExitCode);
            var result = Assert.Single(outcome.Results);
            Assert.False(result.Blocking);
            Assert.Equal(2, result.Findings); // the report still lists every finding
            Assert.Contains("2 ismert, 0 új", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Gate_FindingBeyondBaseline_Blocks()
    {
        var (context, root) = ScanContextTestFactory.Create(mode: ScanMode.Gate);
        try
        {
            WriteBaseline(context, """{ "schemaVersion": 1, "tools": { "gitleaks": { "fp1": 1 } } }""");

            var outcome = await Run(context, new FixedScanner("gitleaks", Gitleaks("fp1", "fp1")));

            Assert.Equal(1, outcome.ExitCode);
            Assert.Contains("1 ismert, 1 új", Assert.Single(outcome.Results).Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Gate_NoBaselineFile_StaysAbsolute()
    {
        var (context, root) = ScanContextTestFactory.Create(mode: ScanMode.Gate);
        try
        {
            var outcome = await Run(context, new FixedScanner("gitleaks", Gitleaks("fp1")));

            Assert.Equal(1, outcome.ExitCode);
            Assert.Equal(string.Empty, Assert.Single(outcome.Results).Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Gate_ToolWithoutBaselineEntryOrFingerprints_StaysAbsolute()
    {
        var (context, root) = ScanContextTestFactory.Create(mode: ScanMode.Gate);
        try
        {
            WriteBaseline(context, """{ "schemaVersion": 1, "tools": { "semgrep": { "fp1": 1 } } }""");

            var noEntry = await Run(context, new FixedScanner("gitleaks", Gitleaks("fp1")));
            var noFingerprints = await Run(context, new FixedScanner(
                "npm", new ScanResult("npm", "web", ScanStatus.Completed, 3, Blocking: true)));

            Assert.Equal(1, noEntry.ExitCode);
            Assert.Equal(1, noFingerprints.ExitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Staged_SecretInBaseline_DoesNotBlockTheCommit()
    {
        var (context, root) = ScanContextTestFactory.Create(mode: ScanMode.Report, staged: true);
        try
        {
            WriteBaseline(context, """{ "schemaVersion": 1, "tools": { "gitleaks": { "fp1": 1 } } }""");

            var outcome = await Run(context, new FixedScanner("gitleaks", Gitleaks("fp1")));

            Assert.Equal(0, outcome.ExitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "schemaVersion": 2, "tools": {} }""")]
    [InlineData("""{ "schemaVersion": 1, "tools": { "gitleaks": { "fp1": 0 } } }""")]
    public async Task InvalidBaseline_IsATechnicalFailureNotAnException(string json)
    {
        var (context, root) = ScanContextTestFactory.Create(mode: ScanMode.Gate);
        try
        {
            WriteBaseline(context, json);

            var outcome = await Run(context, new FixedScanner("gitleaks", Gitleaks("fp1")));

            Assert.Equal(2, outcome.ExitCode);
            Assert.Contains(outcome.Results, r => r.Tool == "baseline" && r.Status == ScanStatus.Failed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateBaseline_RecordsPerToolAndKeepsToolsThatDidNotRunOrFailed()
    {
        var (context, root) = ScanContextTestFactory.Create(mode: ScanMode.Report);
        try
        {
            WriteBaseline(context, """
                { "schemaVersion": 1, "tools": { "semgrep": { "old": 1 }, "npm": { "keep": 2 }, "gitleaks": { "stale": 1 } } }
                """);
            var update = context with { UpdateBaseline = true };

            var outcome = await Run(
                update,
                new FixedScanner("gitleaks", Gitleaks("fp1", "fp1", "fp2")),
                new FixedScanner("semgrep", new ScanResult("semgrep", "repository", ScanStatus.Failed, null, Blocking: false, "boom")));

            Assert.Equal(2, outcome.ExitCode); // the semgrep failure still counts
            Assert.All(outcome.Results, r => Assert.False(r.Blocking));
            Assert.Contains(outcome.Notes!, n => n.Contains("gitleaks → 3", StringComparison.Ordinal));
            Assert.Contains(outcome.Notes!, n => n.Contains("semgrep nem frissült", StringComparison.Ordinal));

            var baseline = FindingBaseline.Load(context.ToolkitDirectory)!;
            Assert.Equal(new Dictionary<string, int> { ["fp1"] = 2, ["fp2"] = 1 }, baseline["gitleaks"]);
            Assert.Equal(new Dictionary<string, int> { ["old"] = 1 }, baseline["semgrep"]);
            Assert.Equal(new Dictionary<string, int> { ["keep"] = 2 }, baseline["npm"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Semgrep_FingerprintFollowsTheCodeNotTheLineNumber()
    {
        static async Task<IReadOnlyList<string>> FingerprintsFor(string source, int line, string severity = "ERROR")
        {
            var (context, root) = ScanContextTestFactory.Create(
                config: new Dictionary<string, object?> { ["tools"] = new Dictionary<string, object?> { ["semgrep"] = true } },
                @lock: new Dictionary<string, object?>
                {
                    ["schemaVersion"] = 1L,
                    ["tools"] = new Dictionary<string, object?>
                    {
                        ["semgrep"] = new Dictionary<string, object?> { ["ready"] = true, ["image"] = "semgrep/semgrep@sha256:" + new string('b', 64) },
                    },
                });
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "src"));
                File.WriteAllText(Path.Combine(root, "src", "Repo.cs"), source);
                var runner = new FakeProcessRunner();
                runner.Enqueue("docker", _ =>
                {
                    File.WriteAllText(Path.Combine(context.OutputDirectory, "semgrep.json"), $$"""
                        { "results": [ { "check_id": "sql-injection", "path": "src/Repo.cs",
                          "start": { "line": {{line}} }, "end": { "line": {{line}} }, "extra": { "severity": "{{severity}}" } } ],
                          "errors": [] }
                        """);
                    return new ProcessResult(0, "", "");
                });

                return Assert.Single(await new SemgrepScanner(runner, Array.Empty<string>()).RunAsync(context)).Fingerprints!;
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        var original = await FingerprintsFor("class A {\n  var q = Raw(input);\n}\n", 2);
        var moved = await FingerprintsFor("// header\n\nclass A {\n    var q = Raw(input);\n}\n", 4);
        var changed = await FingerprintsFor("class A {\n  var q = Raw(other);\n}\n", 2);
        var warning = await FingerprintsFor("class A {\n  var q = Raw(input);\n}\n", 2, "WARNING");

        Assert.Equal(original, moved);
        Assert.NotEqual(original, changed);
        Assert.Empty(warning); // only gate-relevant (ERROR) hits are fingerprinted
    }

    [Fact]
    public async Task Gitleaks_FingerprintIgnoresLineNumbersAndNeverContainsTheSecret()
    {
        var (context, root) = ScanContextTestFactory.Create(
            config: new Dictionary<string, object?> { ["tools"] = new Dictionary<string, object?> { ["gitleaks"] = true } },
            @lock: new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1L,
                ["tools"] = new Dictionary<string, object?>
                {
                    ["gitleaks"] = new Dictionary<string, object?> { ["ready"] = true, ["image"] = "zricethezav/gitleaks@sha256:" + new string('a', 64) },
                },
            });
        try
        {
            var runner = new FakeProcessRunner();
            runner.Enqueue("docker", _ =>
            {
                File.WriteAllText(Path.Combine(context.OutputDirectory, "gitleaks.json"), """
                    [
                      { "RuleID": "api-key", "File": "appsettings.json", "StartLine": 3, "Commit": "", "Match": "\"ApiKey\": \"REDACTED\"", "Secret": "REDACTED" },
                      { "RuleID": "api-key", "File": "./appsettings.json", "StartLine": 40, "Commit": "", "Match": "\"ApiKey\": \"REDACTED\"", "Secret": "REDACTED" }
                    ]
                    """);
                return new ProcessResult(10, "", "");
            });

            var result = Assert.Single(await new GitleaksScanner(runner, Array.Empty<string>()).RunAsync(context));

            Assert.Equal(2, result.Fingerprints!.Count);
            Assert.Equal(result.Fingerprints[0], result.Fingerprints[1]);
            Assert.Matches("^[0-9a-f]{64}$", result.Fingerprints[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NuGet_FingerprintsOnlyHighAndCriticalByPackageAndAdvisory()
    {
        var (context, root) = ScanContextTestFactory.Create(config: new Dictionary<string, object?>
        {
            ["tools"] = new Dictionary<string, object?> { ["nuget"] = true },
            ["dotnetTargets"] = new List<object?> { "src/app.sln" },
        });
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "app.sln"), "");
            var runner = new FakeProcessRunner();
            runner.Enqueue("dotnet", new ProcessResult(0, "", ""));
            runner.Enqueue("dotnet", new ProcessResult(0, """
                { "version": 1, "projects": [ { "path": "a.csproj", "frameworks": [ { "framework": "net10.0",
                  "topLevelPackages": [ { "id": "Newtonsoft.Json", "resolvedVersion": "12.0.3", "vulnerabilities": [
                    { "severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-5crp-9r3c-p9vr" },
                    { "severity": "Moderate", "advisoryurl": "https://github.com/advisories/GHSA-other" } ] } ] } ] } ] }
                """, ""));

            var result = Assert.Single(await new NuGetScanner(runner).RunAsync(context));

            Assert.Equal(2, result.Findings);
            Assert.Equal(
                FindingBaseline.Fingerprint("nuget", "src/app.sln", "Newtonsoft.Json", "https://github.com/advisories/GHSA-5crp-9r3c-p9vr"),
                Assert.Single(result.Fingerprints!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Npm_FingerprintsHighAndCriticalAdvisoriesFromViaObjects()
    {
        var (context, root) = ScanContextTestFactory.Create(config: new Dictionary<string, object?>
        {
            ["tools"] = new Dictionary<string, object?> { ["npm"] = true },
            ["frontendTargets"] = new List<object?> { "web" },
        });
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "web"));
            File.WriteAllText(Path.Combine(root, "web", "package-lock.json"), "{}");
            var runner = new FakeProcessRunner();
            runner.Enqueue("npm", new ProcessResult(1, """
                { "auditReportVersion": 2, "vulnerabilities": {
                    "lodash": { "severity": "critical", "via": [ { "name": "lodash", "url": "https://github.com/advisories/GHSA-a", "severity": "critical" } ] },
                    "app-lib": { "severity": "critical", "via": [ "lodash" ] },
                    "minimist": { "severity": "moderate", "via": [ { "name": "minimist", "url": "https://github.com/advisories/GHSA-b", "severity": "moderate" } ] } },
                  "metadata": { "vulnerabilities": { "total": 3, "high": 0, "critical": 2 } } }
                """, ""));

            var result = Assert.Single(await new NpmScanner(runner, () => new NpmInvocation("npm", Array.Empty<string>())).RunAsync(context));

            Assert.Equal(
                FindingBaseline.Fingerprint("npm", "web", "lodash", "https://github.com/advisories/GHSA-a"),
                Assert.Single(result.Fingerprints!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
