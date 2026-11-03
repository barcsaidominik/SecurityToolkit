using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Core.Tests.Scanners;

/// <summary>Builds minimal, disposable <see cref="ScanContext"/> instances for scanner tests.</summary>
internal static class ScanContextTestFactory
{
    public static (ScanContext Context, string Root) Create(
        IDictionary<string, object?>? config = null,
        IDictionary<string, object?>? @lock = null,
        ScanMode mode = ScanMode.Report,
        SemgrepScope semgrepScope = SemgrepScope.Repository,
        bool history = false,
        bool staged = false,
        bool includeSonar = false,
        string? sonarToken = null)
    {
        var root = Directory.CreateTempSubdirectory("scanner-tests-").FullName;
        var toolkitDirectory = Path.Combine(root, ".security");
        var outputDirectory = Path.Combine(toolkitDirectory, "results", "run");
        Directory.CreateDirectory(outputDirectory);

        var context = new ScanContext(
            Root: root,
            ToolkitDirectory: toolkitDirectory,
            OutputDirectory: outputDirectory,
            ContainerOutputDirectory: ".security/results/run",
            Config: config ?? new Dictionary<string, object?> { ["tools"] = new Dictionary<string, object?>() },
            Lock: @lock ?? new Dictionary<string, object?> { ["schemaVersion"] = 1L, ["tools"] = new Dictionary<string, object?>() },
            Mode: mode,
            SemgrepScope: semgrepScope,
            History: history,
            Staged: staged,
            IncludeSonar: includeSonar,
            SonarToken: sonarToken);

        return (context, root);
    }

    public static Dictionary<string, object?> ToolsConfig(params (string Name, bool Enabled)[] tools)
    {
        var toolsDictionary = new Dictionary<string, object?>();
        foreach (var (name, enabled) in tools)
        {
            toolsDictionary[name] = enabled;
        }

        return new Dictionary<string, object?> { ["tools"] = toolsDictionary };
    }
}
