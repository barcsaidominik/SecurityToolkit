namespace SecurityToolkit.Core.Tests.Scanners;

/// <summary>
/// Deterministic <see cref="IProcessRunner"/> test double: returns a scripted
/// <see cref="ProcessResult"/> per call (matched by file name, in call order for repeats) and
/// records every invocation so tests can assert call history (e.g. that Sonar's "end" step ran).
/// Never starts a real process — no Docker/Semgrep/Gitleaks/dotnet-sonarscanner binary involved.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<(string FileName, Func<RecordedCall, ProcessResult> Respond)> _scripted = new();

    public List<RecordedCall> Calls { get; } = new();

    /// <summary>Queues the next response for a call to <paramref name="fileName"/> (FIFO across all queued responses).</summary>
    public void Enqueue(string fileName, ProcessResult result) => _scripted.Enqueue((fileName, _ => result));

    /// <summary>Queues a response computed from the call itself (e.g. to inspect arguments), or to throw.</summary>
    public void Enqueue(string fileName, Func<RecordedCall, ProcessResult> respond) => _scripted.Enqueue((fileName, respond));

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        string? logPath = null,
        int timeoutSeconds = 1800,
        CancellationToken cancellationToken = default)
    {
        var call = new RecordedCall(fileName, arguments.ToArray(), workingDirectory, logPath);
        Calls.Add(call);

        if (_scripted.Count == 0)
        {
            throw new InvalidOperationException($"Unexpected process call: {fileName} {string.Join(' ', arguments)}");
        }

        var (expectedFileName, respond) = _scripted.Dequeue();
        if (expectedFileName != fileName)
        {
            throw new InvalidOperationException($"Expected next process call to be '{expectedFileName}' but got '{fileName}'.");
        }

        var result = respond(call);

        if (!string.IsNullOrEmpty(logPath))
        {
            File.WriteAllText(logPath, result.StdOut + "\n" + result.StdErr);
        }

        return Task.FromResult(result);
    }

    public sealed record RecordedCall(string FileName, string[] Arguments, string? WorkingDirectory, string? LogPath);
}
