namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// The "status" values written to status.json: "completed", "failed", or "not-run" (the Sonar
/// opt-in placeholder result when --include-sonar wasn't passed).
/// </summary>
public enum ScanStatus
{
    Completed,
    Failed,
    NotRun,
}
