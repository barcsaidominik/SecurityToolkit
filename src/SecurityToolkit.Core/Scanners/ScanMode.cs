namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// Scan mode (<c>--mode</c>). Report never blocks on findings
/// (only scanner/technical failures do); Gate applies the absolute blocking thresholds
/// documented in docs/06-local-runs-and-cli.md.
/// </summary>
public enum ScanMode
{
    Report,
    Gate,
}
