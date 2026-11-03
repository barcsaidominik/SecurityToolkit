using SecurityToolkit.Core.Installation;
using SecurityToolkit.Core.Tests.Scanners;

namespace SecurityToolkit.Core.Tests.Installation;

public class GitHookInstallerTests : IDisposable
{
    private readonly string _root;
    private readonly string _securityDirectory;

    public GitHookInstallerTests()
    {
        _root = Directory.CreateTempSubdirectory("git-hook-installer-tests-").FullName;
        _securityDirectory = Path.Combine(_root, ".security");
        Directory.CreateDirectory(_securityDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string HooksDirectory => Path.Combine(_root, ".git", "hooks");

    [Fact]
    public async Task InstallAsync_NoExistingHook_WritesNewHookScript()
    {
        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("git", new ProcessResult(0, ".git/hooks\n", string.Empty));
        if (!OperatingSystem.IsWindows())
        {
            processRunner.Enqueue("chmod", new ProcessResult(0, string.Empty, string.Empty));
        }

        var installer = new GitHookInstaller(processRunner);
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        await installer.InstallAsync(_root, backup);

        var hookPath = Path.Combine(HooksDirectory, "pre-commit");
        Assert.True(File.Exists(hookPath));
        var content = File.ReadAllText(hookPath);
        Assert.Contains("SECURITY-TOOLKIT-HOOK", content);
        Assert.Contains("--staged", content);

        // A CRLF shebang line fails on Linux ("/bin/sh^M: bad interpreter").
        Assert.DoesNotContain('\r', File.ReadAllText(hookPath));

        // The hook resolves "security-toolkit" from PATH — the shared, per-machine install
        // (install.bat / install.sh) — rather than a project-local copy of the executable.
        Assert.Contains("exec security-toolkit scan", content);

        // .git/hooks is shared by every branch: a branch without .security/ must not block commits.
        var skipCheck = content.IndexOf("/.security/security.config.json\" ]; then", StringComparison.Ordinal);
        Assert.True(skipCheck > 0 && skipCheck < content.IndexOf("exec security-toolkit scan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_GeneratedHook_PassesAbsoluteProjectRootAsFallback()
    {
        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("git", new ProcessResult(0, ".git/hooks\n", string.Empty));
        if (!OperatingSystem.IsWindows())
        {
            processRunner.Enqueue("chmod", new ProcessResult(0, string.Empty, string.Empty));
        }

        var installer = new GitHookInstaller(processRunner);
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        await installer.InstallAsync(_root, backup);

        var content = File.ReadAllText(Path.Combine(HooksDirectory, "pre-commit"));

        // The hook recomputes repo_root dynamically via git, but falls back to the absolute
        // install-time root (never a relative path) if that unexpectedly fails.
        var expectedRoot = Path.GetFullPath(_root);
        Assert.Contains("|| repo_root=", content);
        Assert.Contains(expectedRoot.Replace("\\", "/"), content.Replace("\\", "/"));
    }

    [Fact]
    public async Task InstallAsync_NotAGitRepository_Throws()
    {
        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("git", new ProcessResult(128, string.Empty, "not a git repository"));

        var installer = new GitHookInstaller(processRunner);
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(_root, backup));
    }

    [Fact]
    public async Task InstallAsync_ExistingUnmanagedHook_IsPreservedAsBackupAndChained()
    {
        Directory.CreateDirectory(HooksDirectory);
        var hookPath = Path.Combine(HooksDirectory, "pre-commit");
        File.WriteAllText(hookPath, "#!/bin/sh\necho 'existing custom hook'\n");

        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("git", new ProcessResult(0, ".git/hooks\n", string.Empty));
        if (!OperatingSystem.IsWindows())
        {
            processRunner.Enqueue("chmod", new ProcessResult(0, string.Empty, string.Empty));
            processRunner.Enqueue("chmod", new ProcessResult(0, string.Empty, string.Empty));
        }

        var installer = new GitHookInstaller(processRunner);
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        await installer.InstallAsync(_root, backup);

        var originalBackupPath = Path.Combine(HooksDirectory, "pre-commit.security-toolkit-original");
        Assert.True(File.Exists(originalBackupPath));
        Assert.Contains("existing custom hook", File.ReadAllText(originalBackupPath));

        var newHookContent = File.ReadAllText(hookPath);
        Assert.Contains("pre-commit.security-toolkit-original", newHookContent);
        Assert.Contains("SECURITY-TOOLKIT-HOOK", newHookContent);
    }

    [Fact]
    public async Task InstallAsync_BackupNameAlreadyOccupied_Throws()
    {
        Directory.CreateDirectory(HooksDirectory);
        File.WriteAllText(Path.Combine(HooksDirectory, "pre-commit"), "#!/bin/sh\necho 'custom'\n");
        File.WriteAllText(Path.Combine(HooksDirectory, "pre-commit.security-toolkit-original"), "#!/bin/sh\necho 'already here'\n");

        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("git", new ProcessResult(0, ".git/hooks\n", string.Empty));

        var installer = new GitHookInstaller(processRunner);
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(_root, backup));
    }

    [Fact]
    public async Task InstallAsync_ExternalHooksPathOutsideRoot_Throws()
    {
        var processRunner = new FakeProcessRunner();
        var outsideHooksPath = Path.Combine(Path.GetTempPath(), "shared-hooks-outside-root");
        processRunner.Enqueue("git", new ProcessResult(0, outsideHooksPath + "\n", string.Empty));

        var installer = new GitHookInstaller(processRunner);
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(_root, backup));
    }

    [Fact]
    public async Task InstallAsync_AlreadyManagedHook_OverwritesCleanlyWithoutBackupOfOriginal()
    {
        Directory.CreateDirectory(HooksDirectory);
        var hookPath = Path.Combine(HooksDirectory, "pre-commit");
        File.WriteAllText(hookPath, "#!/bin/sh\n# SECURITY-TOOLKIT-HOOK\necho 'old version'\n");
        var priorHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(hookPath)));

        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("git", new ProcessResult(0, ".git/hooks\n", string.Empty));
        if (!OperatingSystem.IsWindows())
        {
            processRunner.Enqueue("chmod", new ProcessResult(0, string.Empty, string.Empty));
        }

        var installer = new GitHookInstaller(processRunner);
        var relativeHookPath = Path.GetRelativePath(_root, hookPath).Replace('\\', '/');
        var priorHashes = new Dictionary<string, string> { [relativeHookPath] = priorHash };
        var backup = new BackupService(_root, _securityDirectory, "run-1", priorHashes);

        await installer.InstallAsync(_root, backup);

        Assert.False(File.Exists(Path.Combine(HooksDirectory, "pre-commit.security-toolkit-original")));
        Assert.Contains("SECURITY-TOOLKIT-HOOK", File.ReadAllText(hookPath));
    }

    [Theory]
    [InlineData(".husky/_", ".husky/pre-commit")]               // Husky v9
    [InlineData(".husky", ".husky/pre-commit")]                 // Husky v5–8
    [InlineData("src/Client/.husky/_", "src/Client/.husky/pre-commit")]
    public async Task InstallAsync_HuskyHooksPath_WritesNothingAndReturnsInstructions(string hooksPath, string expectedHuskyHook)
    {
        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("git", new ProcessResult(0, hooksPath + "\n", string.Empty));

        var installer = new GitHookInstaller(processRunner);
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        var message = await installer.InstallAsync(_root, backup);

        Assert.NotNull(message);
        Assert.Contains(expectedHuskyHook, message);
        Assert.Contains(GitHookInstaller.StagedScanCommand, message);
        Assert.False(File.Exists(Path.Combine(_root, hooksPath, "pre-commit")));
        Assert.False(File.Exists(Path.Combine(_root, expectedHuskyHook)));
    }

    [Fact]
    public async Task InstallAsync_DryRun_WritesNothing()
    {
        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("git", new ProcessResult(0, ".git/hooks\n", string.Empty));

        var installer = new GitHookInstaller(processRunner);
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        await installer.InstallAsync(_root, backup, dryRun: true);

        Assert.False(File.Exists(Path.Combine(HooksDirectory, "pre-commit")));
    }

    [Fact]
    public void IsExecutableOnPath_FindOnPathReturnsNull_ReturnsFalse()
    {
        var installer = new GitHookInstaller(new FakeProcessRunner(), findOnPath: _ => null);

        Assert.False(installer.IsExecutableOnPath());
    }

    [Fact]
    public void IsExecutableOnPath_FindOnPathReturnsPath_ReturnsTrue()
    {
        var installer = new GitHookInstaller(new FakeProcessRunner(), findOnPath: _ => "C:/somewhere/security-toolkit.exe");

        Assert.True(installer.IsExecutableOnPath());
    }
}
