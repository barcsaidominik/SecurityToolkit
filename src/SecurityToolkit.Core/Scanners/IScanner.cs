namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// A single security scanner module (analyzers, nuget, npm, semgrep, gitleaks, osv, sonar, ...).
/// Each scanner is responsible for its
/// own try/catch-to-failed-result mapping — a thrown exception must never escape
/// <see cref="RunAsync"/> as an unhandled exception, so one tool's failure never stops the others.
/// </summary>
public interface IScanner
{
    /// <summary>Scanner name as it appears in security.config.json's "tools" map and in ScanResult.Tool.</summary>
    string Name { get; }

    /// <summary>
    /// Runs this scanner against <paramref name="context"/>, returning one result per target
    /// (or a single result for repository-wide scanners). Never throws for expected failure
    /// modes (missing image, invalid report schema, etc.) — those become a Failed ScanResult.
    /// </summary>
    Task<IReadOnlyList<ScanResult>> RunAsync(ScanContext context, CancellationToken cancellationToken = default);
}
