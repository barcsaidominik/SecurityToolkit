namespace SecurityToolkit.Core;

/// <summary>
/// Result of running an external process: exit code plus separately captured
/// standard output/error streams.
/// </summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
