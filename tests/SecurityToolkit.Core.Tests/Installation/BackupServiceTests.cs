using SecurityToolkit.Core.Installation;

namespace SecurityToolkit.Core.Tests.Installation;

public class BackupServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _securityDirectory;

    public BackupServiceTests()
    {
        _root = Directory.CreateTempSubdirectory("backup-service-tests-").FullName;
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

    [Fact]
    public void SaveManagedText_NewFile_RecordsChangeWithNoBackup()
    {
        var backup = new BackupService(_root, _securityDirectory, "run-1");
        var path = Path.Combine(_root, "toolkit.json");

        var changed = backup.SaveManagedText(path, "{}\n");

        Assert.True(changed);
        Assert.Equal("{}\r\n", File.ReadAllText(path));
        Assert.Single(backup.Changes);
        Assert.False(backup.Changes[0].Existed);
        Assert.Equal(string.Empty, backup.Changes[0].Backup);
    }

    [Fact]
    public void SaveManagedText_ExistingFile_BacksUpPriorContent()
    {
        var path = Path.Combine(_root, "toolkit.json");
        File.WriteAllText(path, "old content");

        var backup = new BackupService(_root, _securityDirectory, "run-1");
        backup.SaveManagedText(path, "new content");

        Assert.Single(backup.Changes);
        var change = backup.Changes[0];
        Assert.True(change.Existed);
        Assert.True(File.Exists(change.Backup));
        Assert.Equal("old content", File.ReadAllText(change.Backup));
        Assert.Equal("new content", File.ReadAllText(path));
    }

    [Fact]
    public void SaveManagedText_IdenticalContent_NoOpsAndRecordsNoChange()
    {
        var path = Path.Combine(_root, "toolkit.json");
        File.WriteAllText(path, "same");

        var backup = new BackupService(_root, _securityDirectory, "run-1");
        var changed = backup.SaveManagedText(path, "same");

        Assert.False(changed);
        Assert.Empty(backup.Changes);
        Assert.Contains("toolkit.json", backup.TouchedRelativePaths);
    }

    [Fact]
    public void SaveManagedText_OwnedFileWhoseLineEndingsGitConverted_IsUpdatedNotTreatedAsManualEdit()
    {
        var path = Path.Combine(_root, "runner.ps1");
        // Installed as CRLF (hash recorded), then checked out as LF by a core.autocrlf=false clone.
        var crlfHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("old line 1\r\nold line 2\r\n")));
        File.WriteAllText(path, "old line 1\nold line 2\n");
        var backup = new BackupService(
            _root, _securityDirectory, "run-1", priorHashes: new Dictionary<string, string> { ["runner.ps1"] = crlfHash });

        var changed = backup.SaveManagedText(path, "new template content\n", owned: true);

        Assert.True(changed);
        Assert.Empty(backup.SkippedOwnedFiles);
        Assert.Equal("new template content\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void SaveManagedText_OwnedFileWithManualEdit_SkipsWithoutThrowingAndLeavesContentUntouched()
    {
        var path = Path.Combine(_root, "runner.ps1");
        File.WriteAllText(path, "manually edited content");

        // No prior hash recorded for this file -> looks unmanaged/manually edited.
        var backup = new BackupService(_root, _securityDirectory, "run-1", priorHashes: new Dictionary<string, string>());

        var changed = backup.SaveManagedText(path, "new template content", owned: true);

        Assert.False(changed);
        Assert.Equal("manually edited content", File.ReadAllText(path));
        Assert.Contains("runner.ps1", backup.SkippedOwnedFiles);
        Assert.Empty(backup.Changes);
    }

    [Fact]
    public void SaveManagedText_OwnedFileOnFreshClone_IsTreatedAsBaselineAndOverwritten()
    {
        var path = Path.Combine(_root, "runner.ps1");
        File.WriteAllText(path, "committed content");

        // No install-state.json on this checkout (fresh clone / new machine): priorHashes is null.
        var backup = new BackupService(_root, _securityDirectory, "run-1", priorHashes: null);

        var changed = backup.SaveManagedText(path, "new template content", owned: true);

        Assert.True(changed);
        Assert.False(backup.HasPriorInstall);
        Assert.Empty(backup.SkippedOwnedFiles);
        Assert.Equal("new template content", File.ReadAllText(path));
    }

    [Fact]
    public void SaveManagedText_OwnedFileMatchingPriorHash_OverwritesCleanly()
    {
        var path = Path.Combine(_root, "runner.ps1");
        File.WriteAllText(path, "template v1");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

        var priorHashes = new Dictionary<string, string> { ["runner.ps1"] = hash };
        var backup = new BackupService(_root, _securityDirectory, "run-1", priorHashes);

        backup.SaveManagedText(path, "template v2", owned: true);

        Assert.Equal("template v2", File.ReadAllText(path));
    }

    [Fact]
    public void SaveManagedText_PathEscapingRoot_Throws()
    {
        var backup = new BackupService(_root, _securityDirectory, "run-1");
        var outsidePath = Path.Combine(Path.GetTempPath(), "outside-security-toolkit-test.txt");

        Assert.Throws<InvalidOperationException>(() => backup.SaveManagedText(outsidePath, "content"));
    }

    [Fact]
    public void Finish_WritesManifestAndInstallState()
    {
        var backup = new BackupService(_root, _securityDirectory, "run-1");
        backup.SaveManagedText(Path.Combine(_root, "toolkit.json"), "{}\n");

        backup.Finish();

        Assert.True(File.Exists(backup.ManifestPath));
        var installStatePath = Path.Combine(_securityDirectory, "install-state.json");
        Assert.True(File.Exists(installStatePath));
        Assert.Contains("toolkit.json", File.ReadAllText(installStatePath));
    }

    [Fact]
    public void Finish_DryRun_WritesNothing()
    {
        var backup = new BackupService(_root, _securityDirectory, "run-1");
        backup.SaveManagedText(Path.Combine(_root, "toolkit.json"), "{}\n", dryRun: true);

        backup.Finish(dryRun: true);

        Assert.False(File.Exists(backup.ManifestPath));
        Assert.False(File.Exists(Path.Combine(_securityDirectory, "install-state.json")));
    }
}
