namespace SecurityToolkit.Core.Installation;

/// <summary>
/// Installs a pre-commit hook that chains into the security-toolkit CLI's staged secret scan.
/// Preserves and chains any pre-existing hook (never silently discarded), and refuses to touch a
/// repository's hooks directory if it lives outside the project root (external/shared hooks path,
/// e.g. via core.hooksPath pointing elsewhere), the same traversal rule as
/// <see cref="ToolkitPaths.GetToolkitPath"/>.
/// </summary>
public sealed class GitHookInstaller
{
    private const string HookMarker = "# SECURITY-TOOLKIT-HOOK";

    private readonly IProcessRunner _processRunner;
    private readonly Func<string, string?> _findOnPath;

    public GitHookInstaller(IProcessRunner processRunner, Func<string, string?>? findOnPath = null)
    {
        _processRunner = processRunner;
        _findOnPath = findOnPath ?? EnvironmentPath.FindOnPath;
    }

    /// <summary>True when "security-toolkit" resolves on PATH (see <see cref="InstallAsync"/>'s PATH warning).</summary>
    public bool IsExecutableOnPath() => _findOnPath("security-toolkit") is not null;

    /// <param name="root">Project root; the generated hook always passes this back as an absolute <c>--project-root</c>.</param>
    /// <returns>
    /// Null when the hook was installed; otherwise a message for the user (Husky manages the hooks,
    /// nothing was written).
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Not a git repository, the hooks directory is outside <paramref name="root"/>, or a prior
    /// hook backup already exists under the reserved name.
    /// </exception>
    public async Task<string?> InstallAsync(
        string root, BackupService backup, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var pathResult = await _processRunner.RunAsync(
            "git", new[] { "rev-parse", "--git-path", "hooks" }, root, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (pathResult.ExitCode != 0)
        {
            throw new InvalidOperationException("Hook installation requires an existing git repository.");
        }

        var hooksPathRaw = pathResult.StdOut.Trim();
        var hooksDirectory = Path.IsPathRooted(hooksPathRaw) ? hooksPathRaw : Path.Combine(root, hooksPathRaw);
        var hookPath = Path.Combine(hooksDirectory, "pre-commit");

        var relative = Path.GetRelativePath(root, hookPath);
        if (relative == ".." || relative.StartsWith("..", StringComparison.Ordinal) && (relative.Length == 2 || relative[2] is '/' or '\\'))
        {
            throw new InvalidOperationException(
                "External/shared hooks path is not modified. Chain the staged scan into the existing hook manager.");
        }

        // Husky points core.hooksPath into the project (v9: .husky/_, v5–8: .husky). v9 regenerates
        // .husky/_ on every npm install, and .husky/pre-commit is a committed, team-wide script that
        // would break commits for anyone without the CLI, so the hook is left to the team to add.
        var huskyDirectory = FindHuskyDirectory(hooksDirectory);
        if (huskyDirectory is not null)
        {
            var huskyHook = Path.GetRelativePath(root, Path.Combine(huskyDirectory, "pre-commit")).Replace('\\', '/');
            return "Husky manages the git hooks, so the toolkit does not install its own pre-commit hook. " +
                $"Add the secret scan to {huskyHook}:\n  {StagedScanCommand}";
        }

        var existingText = File.Exists(hookPath) ? File.ReadAllText(hookPath) : string.Empty;

        if (existingText.Length > 0 && !existingText.Contains(HookMarker, StringComparison.Ordinal))
        {
            // Preserve and chain the existing hook; never overwrite an occupied backup name.
            var originalPath = Path.Combine(hooksDirectory, "pre-commit.security-toolkit-original");
            if (File.Exists(originalPath))
            {
                throw new InvalidOperationException("Hook backup name already exists; merge hooks manually.");
            }

            backup.SaveManagedText(originalPath, existingText, dryRun: dryRun, lf: true);

            if (!dryRun && !OperatingSystem.IsWindows())
            {
                await _processRunner.RunAsync("chmod", new[] { "+x", originalPath }, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        var absoluteRoot = Path.GetFullPath(root);
        var hookScript = BuildHookScript(absoluteRoot);
        var owned = existingText.Contains(HookMarker, StringComparison.Ordinal);
        backup.SaveManagedText(hookPath, hookScript, owned: owned, dryRun: dryRun, lf: true);

        if (!dryRun && !OperatingSystem.IsWindows())
        {
            var chmodResult = await _processRunner.RunAsync(
                "chmod", new[] { "+x", hookPath }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (chmodResult.ExitCode != 0)
            {
                throw new InvalidOperationException("Cannot mark hook executable.");
            }
        }

        return null;
    }

    /// <summary>The staged secret scan line to add to a Husky pre-commit script.</summary>
    public const string StagedScanCommand = "security-toolkit scan --project-root \"$(git rev-parse --show-toplevel)\" --staged";

    /// <summary>The .husky directory when <paramref name="hooksDirectory"/> is Husky's (".husky/_" or ".husky"), else null.</summary>
    private static string? FindHuskyDirectory(string hooksDirectory)
    {
        var directory = new DirectoryInfo(Path.TrimEndingDirectorySeparator(hooksDirectory));
        if (directory.Name == "_" && directory.Parent?.Name == ".husky")
        {
            return directory.Parent.FullName;
        }

        return directory.Name == ".husky" ? directory.FullName : null;
    }

    /// <summary>
    /// The generated hook shells out to the CLI's "scan --staged", invoking the bare
    /// "security-toolkit" command name resolved from PATH. The CLI is published once to a shared,
    /// machine-wide location (the repo's "install.bat" on Windows, "install.sh" on Linux); every
    /// project's hook then resolves whatever is currently on PATH, so updating the shared install
    /// upgrades every previously-installed hook without touching any project.
    /// <see cref="IsExecutableOnPath"/> gives the installer a way to warn when this resolution
    /// would currently fail. Always written with LF line endings.
    /// </summary>
    /// <param name="installTimeRoot">
    /// Absolute project root at install time, used only as a fallback if "git rev-parse
    /// --show-toplevel" unexpectedly fails at commit time; the hook otherwise recomputes the
    /// repo root dynamically (already absolute), so a clone moved to a different path still works.
    /// </param>
    private static string BuildHookScript(string installTimeRoot)
    {
        var quotedInstallTimeRoot = QuoteForShell(installTimeRoot);

        return "#!/bin/sh\n" +
            $"{HookMarker}\n" +
            "hook_dir=$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd) || exit 2\n" +
            "if [ -f \"$hook_dir/pre-commit.security-toolkit-original\" ]; then\n" +
            "  \"$hook_dir/pre-commit.security-toolkit-original\" \"$@\" || exit $?\n" +
            "fi\n" +
            $"repo_root=$(git rev-parse --show-toplevel) || repo_root={quotedInstallTimeRoot}\n" +
            // .git/hooks is branch-independent: on a branch without the toolkit (older branch,
            // source master) the scan has no config and would block every commit with exit 2.
            "if [ ! -f \"$repo_root/.security/security.config.json\" ]; then\n" +
            "  echo \"security-toolkit: no .security/ on this branch, staged secret scan skipped.\" >&2\n" +
            "  exit 0\n" +
            "fi\n" +
            "exec security-toolkit scan --project-root \"$repo_root\" --staged\n";
    }

    /// <summary>Single-quotes <paramref name="value"/> for POSIX sh, escaping any embedded single quote.</summary>
    private static string QuoteForShell(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
