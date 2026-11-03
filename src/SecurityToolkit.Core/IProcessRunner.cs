namespace SecurityToolkit.Core;

/// <summary>
/// Abstraction over external process execution so callers (and their tests) never
/// depend directly on <see cref="System.Diagnostics.Process"/>.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Starts <paramref name="fileName"/> with <paramref name="arguments"/>, waits for exit
    /// (or <paramref name="timeoutSeconds"/> to elapse), and returns its captured output.
    /// </summary>
    /// <param name="fileName">Executable to start (never passed through a shell).</param>
    /// <param name="arguments">Argument list, passed verbatim — no shell quoting/splitting.</param>
    /// <param name="workingDirectory">Working directory for the process; defaults to the current directory.</param>
    /// <param name="logPath">
    /// Optional path to write combined stdout+stderr to (UTF-8, no BOM). Null/empty skips logging.
    /// </param>
    /// <param name="timeoutSeconds">
    /// Seconds to wait before killing the process tree and throwing. Defaults to 1800 (30 minutes).
    /// </param>
    /// <exception cref="InvalidOperationException">The process could not be started.</exception>
    /// <exception cref="TimeoutException">The process did not exit within <paramref name="timeoutSeconds"/>.</exception>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        string? logPath = null,
        int timeoutSeconds = 1800,
        CancellationToken cancellationToken = default);
}
