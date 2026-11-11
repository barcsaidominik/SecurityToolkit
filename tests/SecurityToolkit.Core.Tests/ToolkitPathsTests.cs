namespace SecurityToolkit.Core.Tests;

public class ToolkitPathsTests : IDisposable
{
    private readonly string _root;

    public ToolkitPathsTests()
    {
        _root = Directory.CreateTempSubdirectory("toolkit-paths-tests-").FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void GetToolkitPath_RejectsAbsolutePath()
    {
        var absolute = OperatingSystem.IsWindows() ? "C:\\Windows\\win.ini" : "/etc/passwd";

        var ex = Assert.Throws<ArgumentException>(() => ToolkitPaths.GetToolkitPath(_root, absolute));

        Assert.Contains(absolute, ex.Message);
    }

    [Fact]
    public void GetToolkitPath_RejectsParentTraversalEscape()
    {
        Assert.Throws<InvalidOperationException>(
            () => ToolkitPaths.GetToolkitPath(_root, "../secrets.txt", allowMissing: true));
    }

    [Fact]
    public void GetToolkitPath_RejectsNestedTraversalThatEscapesRoot()
    {
        Assert.Throws<InvalidOperationException>(
            () => ToolkitPaths.GetToolkitPath(_root, Path.Combine("sub", "..", "..", "secrets.txt"), allowMissing: true));
    }

    [Fact]
    public void GetToolkitPath_AllowsLegitimateRelativePathWithinRoot()
    {
        var filePath = Path.Combine(_root, "config.json");
        File.WriteAllText(filePath, "{}");

        var resolved = ToolkitPaths.GetToolkitPath(_root, "config.json");

        Assert.Equal(Path.GetFullPath(filePath), resolved);
    }

    [Fact]
    public void GetToolkitPath_AllowsTraversalThatStaysWithinRoot()
    {
        var subDir = Directory.CreateDirectory(Path.Combine(_root, "sub"));
        var filePath = Path.Combine(_root, "config.json");
        File.WriteAllText(filePath, "{}");

        var resolved = ToolkitPaths.GetToolkitPath(_root, Path.Combine("sub", "..", "config.json"));

        Assert.Equal(Path.GetFullPath(filePath), resolved);
    }

    [Fact]
    public void GetToolkitPath_MissingPathWithoutAllowMissing_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => ToolkitPaths.GetToolkitPath(_root, "does-not-exist.json"));
    }

    [Fact]
    public void GetToolkitPath_MissingPathWithAllowMissing_ReturnsResolvedPath()
    {
        var expected = Path.GetFullPath(Path.Combine(_root, "does-not-exist.json"));

        var resolved = ToolkitPaths.GetToolkitPath(_root, "does-not-exist.json", allowMissing: true);

        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("20261111-191754-123")] // the CLI's default timestamp
    [InlineData("4711")] // Azure $(Build.BuildId)
    [InlineData("1234567890-2")] // GitHub <run_id>-<run_attempt>
    [InlineData("e2e_run.1")]
    public void IsValidRunId_PipelineAndDefaultIds_AreAccepted(string runId)
    {
        Assert.True(ToolkitPaths.IsValidRunId(runId));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("a/b")]
    [InlineData("/abs")]
    [InlineData("C:\\abs")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("trailing\n")]
    public void IsValidRunId_TraversalSeparatorsAndShellCharacters_AreRejected(string runId)
    {
        Assert.False(ToolkitPaths.IsValidRunId(runId));
    }

    [Fact]
    public void IsValidRunId_LongerThan128Characters_IsRejected()
    {
        Assert.True(ToolkitPaths.IsValidRunId(new string('a', 128)));
        Assert.False(ToolkitPaths.IsValidRunId(new string('a', 129)));
    }
}
