namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// Semgrep scan scope (<c>--semgrep-scope</c>): "repository" scans everything
/// (including C#); "frontend" restricts the scan to configured frontend targets and excludes *.cs.
/// </summary>
public enum SemgrepScope
{
    Repository,
    Frontend,
}
