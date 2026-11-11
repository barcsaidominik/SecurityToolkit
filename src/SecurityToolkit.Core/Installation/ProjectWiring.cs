using static SecurityToolkit.Core.Installation.ConfigAccess;

namespace SecurityToolkit.Core.Installation;

/// <summary>
/// The toolkit's managed blocks in the target project's own files: .gitignore, .semgrepignore,
/// Directory.Build.props imports and .editorconfig analyzer severities.
/// </summary>
internal static class ProjectWiring
{
    private static readonly string[] AnalyzerDiagnostics =
    {
        "CA3001", "CA3002", "CA3003", "CA3006", "CA3007", "CA3009", "CA3075",
    };

    internal static void WriteIgnoreBlocks(BackupService backup, string root, IDictionary<string, object?> config)
    {
        var gitignorePath = Path.Combine(root, ".gitignore");
        var gitignoreBody = string.Join('\n', new[]
        {
            ".security/bin/",
            ".security/npm-tools/",
            ".security/results/",
            ".security/backups/",
            ".security/install-state.json",
            ".security/pending-actions.md",
            $".security/{SecurityToolkitInstaller.LocalConfigFileName}",
            // Leftover of older installs (machine-local Sonar pins); kept ignored so a stale copy is
            // never committed by accident.
            ".security/security.lock.local.json",
            ".security/vendor/",
            ".sonarqube/",
        });
        var existingGitignore = File.Exists(gitignorePath) ? File.ReadAllText(gitignorePath) : string.Empty;
        backup.SaveManagedText(gitignorePath, ManagedBlockWriter.UpdateBlock(existingGitignore, gitignoreBody));

        if (GetBool(GetToolsDict(config), "semgrep"))
        {
            var semgrepIgnorePath = Path.Combine(root, ".semgrepignore");
            var semgrepIgnoreBody = string.Join('\n', new[]
            {
                "**/bin/**", "**/obj/**", "**/node_modules/**", "**/dist/**", "**/coverage/**", ".security/**",
            });
            var existingSemgrepIgnore = File.Exists(semgrepIgnorePath) ? File.ReadAllText(semgrepIgnorePath) : string.Empty;
            backup.SaveManagedText(semgrepIgnorePath, ManagedBlockWriter.UpdateBlock(existingSemgrepIgnore, semgrepIgnoreBody));
        }
    }

    internal static void WireDotNetProjects(BackupService backup, string root, ProjectScanResult scan, IDictionary<string, object?> config)
    {
        var tools = GetToolsDict(config);
        if (!GetBool(tools, "analyzers") && !GetBool(tools, "nuget"))
        {
            return;
        }

        var securityDirectory = Path.Combine(root, ".security");
        var propsPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var editorConfigPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in scan.CsprojFiles)
        {
            var directory = Path.GetDirectoryName(project.FullPath)!;
            string? nearest = null;
            var cursor = directory;
            while (true)
            {
                var candidate = Path.Combine(cursor, "Directory.Build.props");
                if (File.Exists(candidate))
                {
                    nearest = candidate;
                    break;
                }

                if (string.Equals(cursor, root, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                cursor = Path.GetDirectoryName(cursor) ?? root;
            }

            nearest ??= Path.Combine(root, "Directory.Build.props");
            propsPaths.Add(nearest);

            if (GetBool(tools, "analyzers"))
            {
                editorConfigPaths.Add(Path.Combine(directory, ".editorconfig"));
            }
        }

        foreach (var propsPath in propsPaths)
        {
            var updated = ManagedBlockWriter.AddPropsImport(propsPath, securityDirectory);
            if (updated is not null)
            {
                backup.SaveManagedText(propsPath, updated);
            }
        }

        var body = "[*.cs]\n" + string.Join('\n', AnalyzerDiagnostics.Select(code => $"dotnet_diagnostic.{code}.severity = warning"));
        foreach (var editorConfigPath in editorConfigPaths)
        {
            var existing = File.Exists(editorConfigPath) ? File.ReadAllText(editorConfigPath) : string.Empty;
            backup.SaveManagedText(editorConfigPath, ManagedBlockWriter.UpdateBlock(existing, body));
        }
    }
}
