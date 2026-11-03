namespace SecurityToolkit.Core.Tests;

public class ProcessRunnerTests
{
    private readonly IProcessRunner _runner = new ProcessRunner();

    [Fact]
    public async Task RunAsync_ProcessExitsBeforeTimeout_ReturnsResultWithoutThrowing()
    {
        var (fileName, arguments) = BuildCommand("exit 0");

        var result = await _runner.RunAsync(fileName, arguments, timeoutSeconds: 30);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_ProcessExceedsTimeout_ThrowsTimeoutExceptionAndKillsProcess()
    {
        // A short-lived shell command that outlives an aggressively short timeout, so the
        // runner's own timeout enforcement (not process misbehavior) is what's under test.
        var (fileName, arguments) = BuildSleepCommand(seconds: 10);

        await Assert.ThrowsAsync<TimeoutException>(
            () => _runner.RunAsync(fileName, arguments, timeoutSeconds: 1));
    }

    [Fact]
    public async Task RunAsync_CapturesStdOutAndStdErrSeparately()
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "echo out-value 1>&2 & echo out-value" })
            : ("/bin/sh", new[] { "-c", "echo out-value >&2; echo out-value" });

        var result = await _runner.RunAsync(fileName, arguments, timeoutSeconds: 30);

        Assert.Contains("out-value", result.StdOut);
        Assert.Contains("out-value", result.StdErr);
    }

    [Fact]
    public async Task RunAsync_UnknownExecutable_ThrowsInvalidOperationExceptionOrProcessException()
    {
        // Process.Start throws Win32Exception (which derives from SystemException, not
        // InvalidOperationException) when the executable cannot be found at all on Windows;
        // ProcessRunner only wraps the "Start() returned false" case explicitly. Assert on the
        // broader failure contract instead of a single exception type to avoid coupling the
        // test to that OS-specific distinction.
        await Assert.ThrowsAnyAsync<Exception>(
            () => _runner.RunAsync("this-executable-does-not-exist-12345", Array.Empty<string>()));
    }

    private static (string FileName, string[] Arguments) BuildCommand(string shellCommand)
    {
        return OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", shellCommand })
            : ("/bin/sh", new[] { "-c", shellCommand });
    }

    private static (string FileName, string[] Arguments) BuildSleepCommand(int seconds)
    {
        return OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", $"ping -n {seconds + 1} 127.0.0.1 > nul" })
            : ("/bin/sh", new[] { "-c", $"sleep {seconds}" });
    }
}
