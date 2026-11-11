namespace SecurityToolkit.Cli.Tests;

[Collection("Console")]
public class ExitCodeTests : IDisposable
{
    private readonly string _root;

    public ExitCodeTests()
    {
        _root = Directory.CreateTempSubdirectory("cli-exit-code-tests-").FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Provision_WithoutInstalledToolkit_ExitsTwo()
    {
        var parseResult = CliApp.BuildRootCommand().Parse(new[] { "provision", "--project-root", _root, "--require-pinned" });

        Assert.Equal(2, await parseResult.InvokeAsync());
    }

    [Theory]
    [InlineData("--staged")]
    [InlineData("--secrets-only")]
    public async Task Scan_UpdateBaselineWithStaged_ExitsTwo(string flag)
    {
        var parseResult = CliApp.BuildRootCommand().Parse(new[] { "scan", "--project-root", _root, "--update-baseline", flag });

        Assert.Equal(2, await parseResult.InvokeAsync());
    }

    [Fact]
    public async Task Scan_UpdateBaselineInGateMode_ExitsTwo()
    {
        var parseResult = CliApp.BuildRootCommand().Parse(new[] { "scan", "--project-root", _root, "--update-baseline", "--mode", "Gate" });

        Assert.Equal(2, await parseResult.InvokeAsync());
    }

    [Theory]
    [InlineData("../escaped")]
    [InlineData("..")]
    [InlineData("a/b")]
    public async Task Scan_RunIdOutsideResults_ExitsTwoBeforeWritingAnything(string runId)
    {
        var project = Path.Combine(_root, "project");
        var securityDirectory = Path.Combine(project, ".security");
        Directory.CreateDirectory(securityDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(securityDirectory, SecurityToolkit.Core.Installation.SecurityToolkitInstaller.ConfigFileName),
            """{ "tools": {} }""");

        var parseResult = CliApp.BuildRootCommand().Parse(new[] { "scan", "--project-root", project, "--run-id", runId });

        Assert.Equal(2, await parseResult.InvokeAsync());
        Assert.False(Directory.Exists(Path.Combine(securityDirectory, "escaped")));
        Assert.False(Directory.Exists(Path.Combine(securityDirectory, "results")));
    }

    [Fact]
    public async Task Report_RunOutsideResults_ExitsTwo()
    {
        // A status.json one level above results/ must not be reachable through --run.
        var securityDirectory = Path.Combine(_root, ".security");
        Directory.CreateDirectory(Path.Combine(securityDirectory, "results"));
        await File.WriteAllTextAsync(Path.Combine(securityDirectory, "status.json"), """{ "schemaVersion": 1, "tools": [] }""");

        var parseResult = CliApp.BuildRootCommand().Parse(new[] { "report", "--project-root", _root, "--run", ".." });

        Assert.Equal(2, await parseResult.InvokeAsync());
    }

    [Fact]
    public async Task Install_DryRun_ExitsZero()
    {
        var root = CliApp.BuildRootCommand();
        var parseResult = root.Parse(new[]
        {
            "install", "--project-root", _root, "--dry-run", "--non-interactive",
        });

        var exitCode = await parseResult.InvokeAsync();

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("--check")]
    [InlineData("--dry-run")]
    public async Task Install_CheckOrDryRun_WithoutNonInteractive_NeverReadsStdin(string mode)
    {
        // A discovered solution makes the target selector a real prompt candidate.
        await File.WriteAllTextAsync(Path.Combine(_root, "Sample.slnx"), "<Solution />");
        var configure = CliApp.BuildRootCommand().Parse(new[]
        {
            "install", "--project-root", _root, "--configure-only", "--non-interactive",
        });
        Assert.Equal(0, await configure.InvokeAsync());

        var stdin = new CountingEmptyReader();
        var originalIn = Console.In;
        Console.SetIn(stdin);
        try
        {
            var parseResult = CliApp.BuildRootCommand().Parse(new[] { "install", "--project-root", _root, mode });
            Assert.NotEqual(2, await parseResult.InvokeAsync());
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        Assert.Equal(0, stdin.Reads);
    }

    private sealed class CountingEmptyReader : TextReader
    {
        public int Reads { get; private set; }

        public override int Read()
        {
            Reads++;
            return -1;
        }

        public override string? ReadLine()
        {
            Reads++;
            return null;
        }
    }

    [Fact]
    public async Task Install_LocalOverride_NeverWritesIntoCommittedConfig()
    {
        var securityDirectory = Path.Combine(_root, ".security");
        Directory.CreateDirectory(securityDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(securityDirectory, SecurityToolkit.Core.Installation.SecurityToolkitInstaller.LocalConfigFileName),
            """{ "sonar": { "url": "http://127.0.0.1:9000" } }""");

        var root = CliApp.BuildRootCommand();
        var install = root.Parse(new[]
        {
            "install", "--project-root", _root, "--configure-only", "--non-interactive",
        });
        Assert.Equal(0, await install.InvokeAsync());

        // The local override must never be written into the committed config, which every CI
        // agent reads too. This shipped inverted once (local override merged into the written
        // config) and had to be reverted.
        var configJson = await File.ReadAllTextAsync(
            Path.Combine(securityDirectory, SecurityToolkit.Core.Installation.SecurityToolkitInstaller.ConfigFileName));
        Assert.DoesNotContain("127.0.0.1", configJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_MissingProjectRoot_ExitsNonZero()
    {
        var root = CliApp.BuildRootCommand();
        var parseResult = root.Parse(new[]
        {
            "install", "--project-root", Path.Combine(_root, "does-not-exist"), "--dry-run", "--non-interactive",
        });

        var exitCode = await parseResult.InvokeAsync();

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task Restore_ManifestReflectsUnmodifiedFiles_ExitsZero()
    {
        var backupDirectory = Path.Combine(_root, ".security", "backups", "run-1");
        Directory.CreateDirectory(backupDirectory);
        var backupFile = Path.Combine(backupDirectory, "toolkit.json.bak");
        File.WriteAllText(backupFile, "old content");

        var currentPath = Path.Combine(_root, "toolkit.json");
        File.WriteAllText(currentPath, "new content");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(currentPath)));

        var manifestPath = Path.Combine(backupDirectory, "manifest.json");
        File.WriteAllText(manifestPath, $$"""
        {
          "schemaVersion": 1,
          "changes": [
            { "path": "toolkit.json", "existed": true, "backup": "{{backupFile.Replace("\\", "\\\\")}}", "newHash": "{{hash}}" }
          ]
        }
        """);

        var root = CliApp.BuildRootCommand();
        var parseResult = root.Parse(new[]
        {
            "restore", "--project-root", _root, "--manifest", manifestPath,
        });

        var exitCode = await parseResult.InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal("old content", File.ReadAllText(currentPath));
    }

    [Fact]
    public async Task Restore_ConflictingChecksum_ExitsNonZeroAndDoesNotOverwrite()
    {
        var backupDirectory = Path.Combine(_root, ".security", "backups", "run-1");
        Directory.CreateDirectory(backupDirectory);
        var backupFile = Path.Combine(backupDirectory, "toolkit.json.bak");
        File.WriteAllText(backupFile, "old content");

        var currentPath = Path.Combine(_root, "toolkit.json");
        File.WriteAllText(currentPath, "manually edited after install");

        var manifestPath = Path.Combine(backupDirectory, "manifest.json");
        File.WriteAllText(manifestPath, $$"""
        {
          "schemaVersion": 1,
          "changes": [
            { "path": "toolkit.json", "existed": true, "backup": "{{backupFile.Replace("\\", "\\\\")}}", "newHash": "deadbeef" }
          ]
        }
        """);

        var root = CliApp.BuildRootCommand();
        var parseResult = root.Parse(new[]
        {
            "restore", "--project-root", _root, "--manifest", manifestPath,
        });

        var exitCode = await parseResult.InvokeAsync();

        Assert.NotEqual(0, exitCode);
        Assert.Equal("manually edited after install", File.ReadAllText(currentPath));
    }

    [Fact]
    public async Task Entry_ScanWithoutSecurityConfig_ExitsTwoWithOneLineError()
    {
        // Exit 1 would read as "blocking findings" in a pipeline; a missing config is a technical error.
        var error = new StringWriter();

        var exitCode = await CliApp.InvokeAsync(new[] { "scan", "--project-root", _root }, error);

        Assert.Equal(2, exitCode);
        var message = error.ToString();
        Assert.StartsWith("Error: ", message, StringComparison.Ordinal);
        Assert.Contains("security.config.json", message, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", message, StringComparison.Ordinal);
        Assert.Single(message.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Entry_RestoreWithMissingManifest_ExitsTwo()
    {
        var error = new StringWriter();

        var exitCode = await CliApp.InvokeAsync(
            new[] { "restore", "--project-root", _root, "--manifest", Path.Combine(_root, "missing.json") }, error);

        Assert.Equal(2, exitCode);
        Assert.Contains("missing.json", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entry_PassesThroughCommandAndParseErrorExitCodes()
    {
        var error = new StringWriter();

        Assert.Equal(0, await CliApp.InvokeAsync(new[] { "install", "--project-root", _root, "--dry-run", "--non-interactive" }, error));
        Assert.Equal(2, await CliApp.InvokeAsync(new[] { "report", "--project-root", _root }, error));
        Assert.Equal(1, await CliApp.InvokeAsync(new[] { "scan" }, error)); // parse error: missing --project-root
        Assert.DoesNotContain("Error: ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_NoScanRunYet_ExitsTwo()
    {
        var root = CliApp.BuildRootCommand();
        var parseResult = root.Parse(new[] { "report", "--project-root", _root });

        var exitCode = await parseResult.InvokeAsync();

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Report_LatestRun_PrintsToolRowAndExitsZero()
    {
        var runId = "20260101-000000-000";
        var runDirectory = Path.Combine(_root, ".security", "results", runId);
        Directory.CreateDirectory(runDirectory);
        File.WriteAllText(Path.Combine(runDirectory, "status.json"), """
            {
              "schemaVersion": 1,
              "mode": "Report",
              "time": "2026-01-01T00:00:00Z",
              "tools": [
                { "tool": "gitleaks", "target": "dir", "status": "completed", "findings": 3, "blocking": false, "message": "" }
              ]
            }
            """);

        var root = CliApp.BuildRootCommand();
        var parseResult = root.Parse(new[] { "report", "--project-root", _root });

        var stdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(stdout);
        int exitCode;
        try
        {
            exitCode = await parseResult.InvokeAsync();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(0, exitCode);
        Assert.Contains("gitleaks", stdout.ToString(), StringComparison.Ordinal);
    }
}
