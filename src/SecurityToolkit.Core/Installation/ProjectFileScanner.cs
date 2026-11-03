namespace SecurityToolkit.Core.Installation;

/// <summary>One discovered project artifact under the project root.</summary>
/// <param name="FullPath">Absolute path to the file.</param>
/// <param name="RelativePath">Root-relative path, forward slashes.</param>
public sealed record DiscoveredFile(string FullPath, string RelativePath);

/// <summary>Result of scanning a project root for buildable/frontend targets.</summary>
/// <param name="CsprojFiles">Every discovered ".csproj" file.</param>
/// <param name="SolutionRelativePaths">Every discovered ".sln"/".slnx", root-relative.</param>
/// <param name="FrontendRelativeDirectories">Directories containing a "package.json", root-relative.</param>
public sealed record ProjectScanResult(
    IReadOnlyList<DiscoveredFile> CsprojFiles,
    IReadOnlyList<string> SolutionRelativePaths,
    IReadOnlyList<string> FrontendRelativeDirectories);

/// <summary>
/// Discovers .csproj/.sln/.slnx/package.json files under a project root. Skips build output,
/// VCS, and common frontend/tooling cache directories, and never follows reparse points/symlinks.
/// </summary>
public static class ProjectFileScanner
{
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".security", "node_modules", "bin", "obj", "dist", "coverage",
        ".vs", ".idea", "packages", ".angular", ".next", ".nuxt", ".svelte-kit", ".cache",
    };

    public static ProjectScanResult Scan(string root)
    {
        var files = new List<DiscoveredFile>();
        Walk(root, root, files);

        var csprojFiles = files.Where(f => f.RelativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();

        var solutions = files
            .Where(f => f.RelativePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || f.RelativePath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.RelativePath)
            .ToList();

        if (solutions.Count == 0)
        {
            solutions = csprojFiles.Select(f => f.RelativePath).ToList();
        }

        var frontends = files
            .Where(f => string.Equals(Path.GetFileName(f.RelativePath), "package.json", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetDirectoryName(f.RelativePath)?.Replace('\\', '/') ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new ProjectScanResult(csprojFiles, solutions, frontends);
    }

    private static void Walk(string root, string directory, List<DiscoveredFile> results)
    {
        DirectoryInfo info;
        try
        {
            info = new DirectoryInfo(directory);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (entry is DirectoryInfo subDirectory)
            {
                if (!SkipDirectories.Contains(subDirectory.Name))
                {
                    Walk(root, subDirectory.FullName, results);
                }

                continue;
            }

            var name = entry.Name;
            var extension = Path.GetExtension(name);
            if (string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                results.Add(new DiscoveredFile(entry.FullName, relative));
            }
        }
    }
}
