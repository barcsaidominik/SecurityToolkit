using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Core.Tests.Scanners;

public class ScanOrchestratorTests
{
    [Fact]
    public async Task RunAsync_ReportMode_FindingsWithoutFailure_ExitsZero()
    {
        var (context, root) = ScanContextTestFactory.Create(mode: ScanMode.Report);
        try
        {
            var scanner = new StubScanner("nuget", new ScanResult("nuget", "src/app.sln", ScanStatus.Completed, Findings: 5, Blocking: true));
            var orchestrator = new ScanOrchestrator(new FakeProcessRunner(), new IScanner[] { scanner });

            var outcome = await orchestrator.RunAsync(context);

            // Blocking is set on the result (informational), but Report mode never turns it into a gate failure.
            Assert.Equal(0, outcome.ExitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_GateMode_BlockingFinding_ExitsOne()
    {
        var (context, root) = ScanContextTestFactory.Create(mode: ScanMode.Gate);
        try
        {
            var scanner = new StubScanner("gitleaks", new ScanResult("gitleaks", "dir", ScanStatus.Completed, Findings: 1, Blocking: true));
            var orchestrator = new ScanOrchestrator(new FakeProcessRunner(), new IScanner[] { scanner });

            var outcome = await orchestrator.RunAsync(context);

            Assert.Equal(1, outcome.ExitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(ScanMode.Report)]
    [InlineData(ScanMode.Gate)]
    public async Task RunAsync_ScannerFailure_ExitsTwoRegardlessOfMode(ScanMode mode)
    {
        var (context, root) = ScanContextTestFactory.Create(mode: mode);
        try
        {
            var scanner = new StubScanner("semgrep", new ScanResult("semgrep", "repository", ScanStatus.Failed, Findings: null, Blocking: false, "boom"));
            var orchestrator = new ScanOrchestrator(new FakeProcessRunner(), new IScanner[] { scanner });

            var outcome = await orchestrator.RunAsync(context);

            Assert.Equal(2, outcome.ExitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_SharedRunId_EarlierStepFailure_DoesNotFailLaterStepButStaysInStatus()
    {
        var (context, root) = ScanContextTestFactory.Create(mode: ScanMode.Gate);
        try
        {
            // Pipeline step 1 (--tool analyzers) fails and leaves a blocking finding...
            var first = await new ScanOrchestrator(new FakeProcessRunner(), new IScanner[]
            {
                new StubScanner("analyzers", new ScanResult("analyzers", "src/app.sln", ScanStatus.Failed, null, Blocking: true, "Exit 1")),
            }).RunAsync(context);

            // ...step 2 (--tool npm) writes into the same results/<run-id>/status.json.
            var second = await new ScanOrchestrator(new FakeProcessRunner(), new IScanner[]
            {
                new StubScanner("npm", new ScanResult("npm", "src/Client", ScanStatus.Completed, 5, Blocking: false)),
            }).RunAsync(context);

            Assert.Equal(2, first.ExitCode);
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(new[] { "analyzers", "npm" }, second.Results.Select(r => r.Tool).OrderBy(t => t));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WritesHumanReadableSummaryMarkdownAlongsideStatusJson()
    {
        var (context, root) = ScanContextTestFactory.Create(mode: ScanMode.Report);
        try
        {
            var scanner = new StubScanner("gitleaks", new ScanResult("gitleaks", "dir", ScanStatus.Completed, Findings: 3, Blocking: false));
            var orchestrator = new ScanOrchestrator(new FakeProcessRunner(), new IScanner[] { scanner });

            await orchestrator.RunAsync(context);

            var summaryPath = Path.Combine(context.OutputDirectory, "summary.md");
            Assert.True(File.Exists(summaryPath));
            var summary = File.ReadAllText(summaryPath);
            Assert.Contains("gitleaks", summary, StringComparison.Ordinal);
            Assert.Contains("| 3 |", summary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Staged_OnlyGitleaksScannerRuns()
    {
        var (context, root) = ScanContextTestFactory.Create(staged: true);
        try
        {
            var gitleaks = new StubScanner("gitleaks", new ScanResult("gitleaks", "git", ScanStatus.Completed, 0, false));
            var nuget = new StubScanner("nuget", new ScanResult("nuget", "src/app.sln", ScanStatus.Completed, 0, false));
            var orchestrator = new ScanOrchestrator(new FakeProcessRunner(), new IScanner[] { gitleaks, nuget });

            var outcome = await orchestrator.RunAsync(context);

            Assert.False(nuget.WasInvoked);
            Assert.True(gitleaks.WasInvoked);
            Assert.Single(outcome.Results);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_StagedInReportMode_BlockingFinding_ExitsOne()
    {
        // The generated pre-commit hook runs `scan --staged` without --mode (Report), and must still block.
        var (context, root) = ScanContextTestFactory.Create(mode: ScanMode.Report, staged: true);
        try
        {
            var gitleaks = new StubScanner("gitleaks", new ScanResult("gitleaks", "git", ScanStatus.Completed, 1, Blocking: true));
            var orchestrator = new ScanOrchestrator(new FakeProcessRunner(), new IScanner[] { gitleaks });

            var outcome = await orchestrator.RunAsync(context);

            Assert.Equal(1, outcome.ExitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubScanner : IScanner
    {
        private readonly ScanResult _result;

        public StubScanner(string name, ScanResult result)
        {
            Name = name;
            _result = result;
        }

        public string Name { get; }

        public bool WasInvoked { get; private set; }

        public Task<IReadOnlyList<ScanResult>> RunAsync(ScanContext context, CancellationToken cancellationToken = default)
        {
            WasInvoked = true;
            return Task.FromResult<IReadOnlyList<ScanResult>>(new[] { _result });
        }
    }
}
