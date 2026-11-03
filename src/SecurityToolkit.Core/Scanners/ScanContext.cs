namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// Everything a single scanner needs to run, computed once per scan (root, config, lock,
/// output directory, etc.).
/// </summary>
/// <param name="Root">Absolute repository root (the parent of the ".security" folder).</param>
/// <param name="ToolkitDirectory">The ".security" directory containing security.config.json/security.lock.json.</param>
/// <param name="OutputDirectory">Per-run results directory, e.g. ".security/results/&lt;runId&gt;".</param>
/// <param name="ContainerOutputDirectory">
/// The same output directory, but as seen from inside a Docker container bind-mounted at /src
/// (relative to it, e.g. ".security/results/&lt;runId&gt;").
/// </param>
/// <param name="Config">Parsed security.config.json (nested dictionary tree), local override merged in.</param>
/// <param name="Lock">Parsed security.lock.json via <see cref="ToolkitJson.ReadLock"/>.</param>
/// <param name="Mode">Report or Gate.</param>
/// <param name="SemgrepScope">Repository or Frontend.</param>
/// <param name="History">--history: Gitleaks scans reachable git history instead of the working tree.</param>
/// <param name="Staged">--staged: Gitleaks scans the git index (pre-commit) instead of the working tree.</param>
/// <param name="IncludeSonar">--include-sonar: opts in to the Sonar server upload step.</param>
/// <param name="SonarToken">SONAR_TOKEN env value, or null/empty if unset. Never logged/persisted verbatim.</param>
public sealed record ScanContext(
    string Root,
    string ToolkitDirectory,
    string OutputDirectory,
    string ContainerOutputDirectory,
    IDictionary<string, object?> Config,
    IDictionary<string, object?> Lock,
    ScanMode Mode,
    SemgrepScope SemgrepScope,
    bool History,
    bool Staged,
    bool IncludeSonar,
    string? SonarToken);
