namespace SecurityToolkit.Core;

/// <summary>
/// Path-safety helpers. Security-critical: behavior must not be weakened here.
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

    private static readonly System.Text.RegularExpressions.Regex RunIdPattern =
        new(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// True when <paramref name="runId"/> is safe as the single results/&lt;run-id&gt; directory name,
    /// on the host and in the scanner containers' mount path: letters, digits, '.', '_' and '-',
    /// starting with a letter or digit (so never "." or ".."), at most 128 characters. Covers the
    /// generated pipelines' ids (Azure build id, GitHub run_id-run_attempt) and the default
    /// timestamp.
    /// </summary>
    public static bool IsValidRunId(string runId) => RunIdPattern.IsMatch(runId);
}
