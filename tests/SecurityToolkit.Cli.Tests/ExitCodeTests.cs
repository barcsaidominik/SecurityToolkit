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
