using System.Diagnostics;
using System.Text;

namespace SecurityToolkit.Core;

/// <summary>
/// Default <see cref="IProcessRunner"/> implementation backed by <see cref="Process"/>.
/// No shell involved (UseShellExecute=false),
/// stdout/stderr are read independently via async reads to avoid deadlocking on full pipe buffers,
/// and on timeout the whole process tree is killed before throwing.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        string? logPath = null,
        int timeoutSeconds = 1800,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}");
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            KillTree(process);
            throw new TimeoutException($"Timed out: {fileName}");
        }

        var stdOut = await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        var result = new ProcessResult(process.ExitCode, stdOut, stdErr);

        if (!string.IsNullOrEmpty(logPath))
        {
            await File.WriteAllTextAsync(logPath, result.StdOut + "\n" + result.StdErr, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch
        {
            // Best-effort cleanup; the timeout exception is what matters to the caller.
        }
    }
}
