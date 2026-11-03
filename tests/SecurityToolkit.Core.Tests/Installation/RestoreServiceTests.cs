using SecurityToolkit.Core.Installation;

namespace SecurityToolkit.Core.Tests.Installation;

public class RestoreServiceTests : IDisposable
{
    private readonly string _root;

    public RestoreServiceTests()
    {
        _root = Directory.CreateTempSubdirectory("restore-service-tests-").FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Sha256Hex(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Restore_ExistedFile_RestoresBackupContent()
    {
        var securityDirectory = Path.Combine(_root, ".security");
        Directory.CreateDirectory(securityDirectory);
        var backupFile = Path.Combine(securityDirectory, "backup-toolkit.json");
        File.WriteAllText(backupFile, "old content");

        var currentPath = Path.Combine(_root, "toolkit.json");
        File.WriteAllText(currentPath, "new content");

        var manifest = new TransactionManifest(1, new[]
        {
            new TransactionChange("toolkit.json", Existed: true, backupFile, Sha256Hex(currentPath)),
        });

        var outcome = new RestoreService().Restore(_root, manifest);

        Assert.Equal("old content", File.ReadAllText(currentPath));
        Assert.Equal(new[] { "toolkit.json" }, outcome.RestoredPaths);
    }

    [Fact]
    public void Restore_NewlyCreatedFile_DeletesIt()
    {
        var currentPath = Path.Combine(_root, "azure-security-toolkit.yml");
        File.WriteAllText(currentPath, "generated content");

        var manifest = new TransactionManifest(1, new[]
        {
            new TransactionChange("azure-security-toolkit.yml", Existed: false, Backup: string.Empty, Sha256Hex(currentPath)),
        });

        new RestoreService().Restore(_root, manifest);

        Assert.False(File.Exists(currentPath));
    }

    [Fact]
    public void Restore_FileModifiedSinceInstall_ThrowsAndDoesNotOverwriteLaterWork()
    {
        var securityDirectory = Path.Combine(_root, ".security");
        Directory.CreateDirectory(securityDirectory);
        var backupFile = Path.Combine(securityDirectory, "backup-toolkit.json");
        File.WriteAllText(backupFile, "old content");

        var currentPath = Path.Combine(_root, "toolkit.json");
        File.WriteAllText(currentPath, "content at install time");
        var installTimeHash = Sha256Hex(currentPath);

        // Simulate later manual work after installation, before restore runs.
        File.WriteAllText(currentPath, "manually edited after install");

        var manifest = new TransactionManifest(1, new[]
        {
            new TransactionChange("toolkit.json", Existed: true, backupFile, installTimeHash),
        });

        var ex = Assert.Throws<InvalidOperationException>(() => new RestoreService().Restore(_root, manifest));

        Assert.Contains("toolkit.json", ex.Message);
        // The critical guarantee: the later manual edit must survive untouched.
        Assert.Equal("manually edited after install", File.ReadAllText(currentPath));
    }

    [Fact]
    public void Restore_FileDeletedSinceInstall_Throws()
    {
        var securityDirectory = Path.Combine(_root, ".security");
        Directory.CreateDirectory(securityDirectory);
        var backupFile = Path.Combine(securityDirectory, "backup-toolkit.json");
        File.WriteAllText(backupFile, "old content");

        var manifest = new TransactionManifest(1, new[]
        {
            new TransactionChange("toolkit.json", Existed: true, backupFile, "deadbeef"),
        });

        Assert.Throws<InvalidOperationException>(() => new RestoreService().Restore(_root, manifest));
    }

    [Fact]
    public void Restore_MissingBackupFile_ThrowsWithoutTouchingCurrentFile()
    {
        var currentPath = Path.Combine(_root, "toolkit.json");
        File.WriteAllText(currentPath, "content");

        var manifest = new TransactionManifest(1, new[]
        {
            new TransactionChange("toolkit.json", Existed: true, Backup: Path.Combine(_root, ".security", "missing-backup.json"), Sha256Hex(currentPath)),
        });

        Assert.Throws<InvalidOperationException>(() => new RestoreService().Restore(_root, manifest));
        Assert.Equal("content", File.ReadAllText(currentPath));
    }

    [Fact]
    public void Restore_MultipleEntries_RestoresInReverseOrder()
    {
        var securityDirectory = Path.Combine(_root, ".security");
        Directory.CreateDirectory(securityDirectory);

        var pathA = Path.Combine(_root, "a.txt");
        var pathB = Path.Combine(_root, "b.txt");
        File.WriteAllText(pathA, "a-new");
        File.WriteAllText(pathB, "b-new");

        var backupA = Path.Combine(securityDirectory, "a-backup.txt");
        File.WriteAllText(backupA, "a-old");

        var manifest = new TransactionManifest(1, new[]
        {
            new TransactionChange("a.txt", Existed: true, backupA, Sha256Hex(pathA)),
            new TransactionChange("b.txt", Existed: false, Backup: string.Empty, Sha256Hex(pathB)),
        });

        var outcome = new RestoreService().Restore(_root, manifest);

        // Reverse order: b (created) is undone first, then a (existed) is restored.
        Assert.Equal(new[] { "b.txt", "a.txt" }, outcome.RestoredPaths);
        Assert.Equal("a-old", File.ReadAllText(pathA));
        Assert.False(File.Exists(pathB));
    }

    [Fact]
    public void Restore_DryRun_ReportsWithoutModifyingAnyFile()
    {
        var securityDirectory = Path.Combine(_root, ".security");
        Directory.CreateDirectory(securityDirectory);
        var backupFile = Path.Combine(securityDirectory, "backup-toolkit.json");
        File.WriteAllText(backupFile, "old content");

        var currentPath = Path.Combine(_root, "toolkit.json");
        File.WriteAllText(currentPath, "new content");

        var manifest = new TransactionManifest(1, new[]
        {
            new TransactionChange("toolkit.json", Existed: true, backupFile, Sha256Hex(currentPath)),
        });

        var outcome = new RestoreService().Restore(_root, manifest, dryRun: true);

        Assert.Equal(new[] { "toolkit.json" }, outcome.RestoredPaths);
        Assert.Equal("new content", File.ReadAllText(currentPath));
    }

    [Fact]
    public void Restore_UnsupportedSchemaVersion_ThrowsOnParse()
    {
        var tree = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 2L,
            ["changes"] = new List<object?>(),
        };

        Assert.Throws<InvalidOperationException>(() => TransactionManifest.Parse(tree));
    }
}
