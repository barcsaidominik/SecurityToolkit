namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// One entry of a scan run's status.json results. A single scanner run can produce more than one of these (e.g. analyzers/nuget/npm loop
/// over multiple configured targets).
/// </summary>
/// <param name="Tool">Scanner name, e.g. "analyzers", "nuget", "npm", "semgrep", "gitleaks", "sonar".</param>
/// <param name="Target">The target this result applies to (a configured path, or a scan mode like "git"/"dir").</param>
/// <param name="Status">Completed, Failed, or NotRun (the Sonar opt-in placeholder).</param>
/// <param name="Findings">Finding count, or null when unknown/not applicable.</param>
/// <param name="Blocking">Whether this result should block Gate mode.</param>
/// <param name="Message">Optional human-readable context.</param>
/// <param name="Fingerprints">
/// One <see cref="FindingBaseline"/> fingerprint per finding that makes this result blocking (the
/// gate-relevant ones only), or null when the scanner does not support a baseline or could not list
/// its findings. Never written to status.json.
/// </param>
public sealed record ScanResult(
    string Tool,
    string Target,
    ScanStatus Status,
    int? Findings,
    bool Blocking,
    string Message = "",
    IReadOnlyList<string>? Fingerprints = null);
