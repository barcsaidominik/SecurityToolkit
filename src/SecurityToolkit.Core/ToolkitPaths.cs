namespace SecurityToolkit.Core;

/// <summary>
/// Path-safety helpers. Security-critical: behavior must not be weakened here. The generated
/// ".security/Toolkit.Common.ps1" has the same check (Get-ToolkitPath); keep the two in sync.
/// </summary>
public static class ToolkitPaths
{
    /// <summary>
    /// Resolves <paramref name="relative"/> against <paramref name="root"/>, rejecting any path that
    /// is absolute or that escapes <paramref name="root"/> via traversal (e.g. "../secrets").
    /// </summary>
    /// <param name="root">The project/toolkit root directory that <paramref name="relative"/> must stay within.</param>
    /// <param name="relative">A path expected to be relative to <paramref name="root"/>.</param>
    /// <param name="allowMissing">
    /// When false (default), throws if the resolved path does not exist on disk.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="relative"/> is rooted/absolute.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="relative"/> escapes <paramref name="root"/>.</exception>
    /// <exception cref="FileNotFoundException">The resolved path does not exist and <paramref name="allowMissing"/> is false.</exception>
    public static string GetToolkitPath(string root, string relative, bool allowMissing = false)
    {
        if (Path.IsPathRooted(relative))
        {
            throw new ArgumentException($"Project paths must be relative: {relative}", nameof(relative));
        }

        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rel = Path.GetRelativePath(root, full);

        var escapesRoot = rel == ".." ||
            (rel.Length > 2 && rel[0] == '.' && rel[1] == '.' && (rel[2] == '/' || rel[2] == '\\'));

        if (escapesRoot)
        {
            throw new InvalidOperationException($"Path leaves project: {relative}");
        }

        if (!allowMissing && !Path.Exists(full))
        {
            throw new FileNotFoundException($"Missing path: {relative}", full);
        }

        return full;
    }
}
