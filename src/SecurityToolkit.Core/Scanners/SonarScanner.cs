namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// Sonar server upload (opt-in via --include-sonar). Three-step process: "begin" (registers the analysis with the server), a normal dotnet
/// build (Sonar's own MSBuild integration captures analysis data during this build), then "end"
/// (uploads and, in Gate mode, waits on the quality gate).
///
/// Critical invariant: once "begin" has started successfully, "end" MUST always run — even if the
/// build step throws — otherwise the Sonar scanner leaves a stale, unfinished analysis session
/// (and, in past incidents, a machine-wide MSBuild ImportBefore hook) behind that corrupts
/// unrelated builds. This is enforced with try/finally, not just a catch: the finally block always
/// attempts "end" once "begin" has run, regardless of what happens in between.
///
/// Optional coverage import (sonar.coverage): between build and "end", runs the .NET tests under
/// dotnet-coverage and each Angular frontend target's Karma tests with an lcov reporter, then
/// lets "end" upload both reports. Coverage is best-effort by design: failing tests or a failed
/// collection only add a warning to the result message, never fail the Sonar step.
/// </summary>
public sealed class SonarScanner : IScanner
{
    internal const string DotNetCoverageFileName = "dotnet-coverage.xml";

    /// <summary>How many open vulnerabilities sonar-issues.json keeps for summary.md's ranked issue list.</summary>
    internal const int IssuePageSize = 100;

    private readonly IProcessRunner _processRunner;
    private readonly HttpClient _httpClient;
    private readonly Func<NpmInvocation> _resolveNpm;

    public SonarScanner(IProcessRunner processRunner, HttpClient httpClient, Func<NpmInvocation>? resolveNpm = null)
    {
        _processRunner = processRunner;
        _httpClient = httpClient;
        _resolveNpm = resolveNpm ?? NpmResolver.Resolve;
    }

    public string Name => "sonar";

    public async Task<IReadOnlyList<ScanResult>> RunAsync(ScanContext context, CancellationToken cancellationToken = default)
    {
        if (context.Staged || !ScannerSupport.IsToolEnabled(context, "sonar"))
        {
            return Array.Empty<ScanResult>();
        }

        if (!context.IncludeSonar)
        {
            return new[]
            {
                new ScanResult("sonar", "repository", ScanStatus.NotRun, 0, Blocking: false,
                    "Server upload is opt-in: use -IncludeSonar and a private project token."),
            };
        }

        try
        {
            if (string.IsNullOrEmpty(context.SonarToken))
            {
                throw new InvalidOperationException("Set SONAR_TOKEN. Server token is never written into toolkit.json.");
            }

            var sonarScannerTool = ScannerSupport.GetDictionary(context.Lock, "tools") is { } tools
                ? ScannerSupport.GetDictionary(tools, "sonarScanner")
                : null;
            if (sonarScannerTool is null || !ScannerSupport.GetBool(sonarScannerTool, "ready"))
            {
                throw new InvalidOperationException("SonarScanner is not provisioned.");
            }

            var sonarConfig = ScannerSupport.GetDictionary(context.Config, "sonar")
                ?? throw new InvalidOperationException("Sonar URL, project key and selected .NET target are required.");
            var url = ScannerSupport.GetString(sonarConfig, "url");
            var projectKey = ScannerSupport.GetString(sonarConfig, "projectKey");
            var dotnetTarget = ScannerSupport.GetString(sonarConfig, "dotnetTarget");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(projectKey) || string.IsNullOrEmpty(dotnetTarget))
            {
                throw new InvalidOperationException("Sonar URL, project key and selected .NET target are required.");
            }

            var target = ToolkitPaths.GetToolkitPath(context.Root, dotnetTarget);
            var executableName = "dotnet-sonarscanner" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
            var executable = Path.Combine(context.ToolkitDirectory, "bin", executableName);
            var coverage = ResolveCoveragePlan(context, sonarConfig);

            return await RunBeginBuildEndAsync(context, executable, url, projectKey, target, coverage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new[] { new ScanResult("sonar", "repository", ScanStatus.Failed, null, Blocking: false, ex.Message) };
        }
    }

    /// <summary>
    /// Fetches open VULNERABILITY issue counts by severity from the Sonar server's REST API after
    /// a successful "end", so the scan report shows a real finding count instead of "n/a". Scoped
    /// to VULNERABILITY only (not BUG/CODE_SMELL) to match this toolkit's security focus; Security
    /// Hotspots are a separate Sonar concept (api/hotspots/search) and are not included here.
    /// Best-effort: an API failure here does not turn an otherwise-successful upload into a Failed
    /// result, and is reported via the returned message instead. Up to <see cref="IssuePageSize"/>
    /// issues are kept in sonar-issues.json; summary.md lists the most severe of them. They are
    /// ranked client-side, not with the API's deprecated severity sort.
    /// </summary>
    private async Task<(int? Total, string? Message)> FetchIssueSummaryAsync(
        ScanContext context, string url, string projectKey, CancellationToken cancellationToken)
    {
        try
        {
            var requestUri = new Uri(
                $"{url.TrimEnd('/')}/api/issues/search?componentKeys={Uri.EscapeDataString(projectKey)}&resolved=false&types=VULNERABILITY&facets=severities&ps={IssuePageSize}");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{context.SonarToken}:")));

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"Sonar issue lookup failed: HTTP {(int)response.StatusCode}.");
            }

            var data = ScannerSupport.ParseJsonObject(body);
            var total = ScannerSupport.GetInt(data, "total");

            ToolkitJson.WriteFile(Path.Combine(context.OutputDirectory, "sonar-issues.json"), data);

            return (total, null);
        }
        catch (Exception ex)
        {
            return (null, $"Sonar issue lookup failed: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<ScanResult>> RunBeginBuildEndAsync(
        ScanContext context, string executable, string url, string projectKey, string target, CoveragePlan coverage,
        CancellationToken cancellationToken)
    {
        var beginArguments = new List<string>
        {
            "begin",
            $"/k:{projectKey}",
            $"/d:sonar.host.url={url}",
            $"/d:sonar.token={context.SonarToken}",
            "/d:sonar.scanner.scanAll=true",
            "/d:sonar.exclusions=**/bin/**,**/obj/**,**/node_modules/**,**/dist/**,**/coverage/**,.security/**",
        };
        if (context.Mode == ScanMode.Gate)
        {
            beginArguments.Add("/d:sonar.qualitygate.wait=true");
        }

        // Report paths must be known at "begin"; a report that never materialises only makes
        // Sonar log a warning at "end".
        if (coverage.DotNetCoverageExecutable is not null)
        {
            beginArguments.Add($"/d:sonar.cs.vscoveragexml.reportsPaths={ToSonarPath(coverage.DotNetReportPath)}");
        }

        if (coverage.FrontendTargets.Count > 0)
        {
            beginArguments.Add("/d:sonar.javascript.lcov.reportPaths=" + string.Join(
                ',', coverage.FrontendTargets.Select(t => ToSonarPath(Path.Combine(t.CoverageDirectory, "lcov.info")))));
        }

        await ScannerSupport.RunNativeAsync(
            _processRunner, context, executable, beginArguments, "sonar-begin.log",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var coverageNotes = new List<string>(coverage.Notes);

        // From here on, "begin" has run: "end" must always be attempted, even if the build throws,
        // to avoid leaving a stale/unfinished Sonar analysis session behind (see class remarks).
        try
        {
            await ScannerSupport.RunNativeAsync(
                _processRunner, context, "dotnet",
                new[] { "build", target, "--no-incremental", ScannerSupport.NoNodeReuseSwitch }, "sonar-build.log",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Inside the try: the tests run before "end", and "end" still runs if they throw.
            if (coverage.Enabled)
            {
                await CollectCoverageAsync(context, target, coverage, coverageNotes, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await ScannerSupport.RunNativeAsync(
                    _processRunner, context, executable, new[] { "end", $"/d:sonar.token={context.SonarToken}" }, "sonar-end.log",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // After "end", never between build and "end": the begin/end guarantee stays untouched.
                await ScannerSupport.ShutdownBuildServersAsync(_processRunner, context, cancellationToken).ConfigureAwait(false);
            }
        }

        var (total, message) = await FetchIssueSummaryAsync(context, url, projectKey, cancellationToken).ConfigureAwait(false);

        var messages = new List<string>();
        if (message is not null)
        {
            messages.Add(message);
        }

        if (coverageNotes.Count > 0)
        {
            messages.Add("Coverage: " + string.Join("; ", coverageNotes));
        }

        return new[] { new ScanResult("sonar", projectKey, ScanStatus.Completed, total, Blocking: false, string.Join(" | ", messages)) };
    }

    /// <summary>
    /// One Angular project (of a frontendTargets workspace) whose Karma tests run with the toolkit's
    /// lcov-reporting config. <see cref="UnitTestBuilder"/>: the project uses the newer
    /// <c>@angular/build:unit-test</c> builder (runner: karma), whose flags differ from the karma
    /// builder's (<c>--coverage</c>/<c>--runner-config</c> instead of <c>--code-coverage</c>/<c>--karma-config</c>).
    /// </summary>
    private sealed record FrontendCoverageTarget(
        string Target, string Path, string Project, bool UnitTestBuilder, string KarmaConfigPath, string CoverageDirectory)
    {
        public string Label => $"{Target}:{Project}";
    }

    /// <summary>
    /// What the coverage step will run, resolved before "begin" (whose arguments carry the report
    /// paths). <see cref="Notes"/> collects the parts skipped up front (unprovisioned tool, a
    /// frontend target that isn't an Angular workspace).
    /// </summary>
    private sealed record CoveragePlan(
        bool Enabled,
        string? DotNetCoverageExecutable,
        string DotNetReportPath,
        string? TestFilter,
        IReadOnlyList<FrontendCoverageTarget> FrontendTargets,
        IReadOnlyList<string> Notes);

    private static CoveragePlan ResolveCoveragePlan(ScanContext context, IDictionary<string, object?> sonarConfig)
    {
        var dotNetReportPath = Path.Combine(context.OutputDirectory, DotNetCoverageFileName);
        if (!ScannerSupport.GetBool(sonarConfig, "coverage"))
        {
            return new CoveragePlan(false, null, dotNetReportPath, null, Array.Empty<FrontendCoverageTarget>(), Array.Empty<string>());
        }

        var notes = new List<string>();

        string? dotNetCoverage = null;
        var coverageTool = ScannerSupport.GetDictionary(context.Lock, "tools") is { } tools
            ? ScannerSupport.GetDictionary(tools, "dotnetCoverage")
            : null;
        if (coverageTool is not null && ScannerSupport.GetBool(coverageTool, "ready"))
        {
            dotNetCoverage = Path.Combine(
                context.ToolkitDirectory, "bin", "dotnet-coverage" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        }
        else
        {
            notes.Add(".NET skipped (dotnet-coverage is not provisioned; rerun install)");
        }

        var frontendTargets = new List<FrontendCoverageTarget>();
        var index = 0;
        foreach (var target in ScannerSupport.GetConfigTargets(context, "frontendTargets"))
        {
            var path = ToolkitPaths.GetToolkitPath(context.Root, target);
            var angularJson = Path.Combine(path, "angular.json");
            if (!File.Exists(angularJson))
            {
                notes.Add($"{target} skipped (not an Angular workspace)");
                continue;
            }

            // A workspace can hold several projects with different test builders; a bare `ng test`
            // runs all of them with the same flags, which one builder's schema then rejects.
            IDictionary<string, object?>? projects;
            try
            {
                projects = ScannerSupport.GetDictionary(ScannerSupport.ParseJsonObject(File.ReadAllText(angularJson)), "projects");
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                notes.Add($"{target} skipped (angular.json is not readable: {ex.Message})");
                continue;
            }

            foreach (var (project, value) in projects ?? new Dictionary<string, object?>())
            {
                var test = value is IDictionary<string, object?> projectConfig
                    && ScannerSupport.GetDictionary(projectConfig, "architect") is { } architect
                        ? ScannerSupport.GetDictionary(architect, "test")
                        : null;
                if (test is null)
                {
                    continue;
                }

                var builder = ScannerSupport.GetString(test, "builder") ?? string.Empty;
                var runner = ScannerSupport.GetDictionary(test, "options") is { } options ? ScannerSupport.GetString(options, "runner") : null;
                var unitTest = builder == "@angular/build:unit-test";
                if (!(builder.EndsWith(":karma", StringComparison.Ordinal) || (unitTest && runner == "karma")))
                {
                    notes.Add($"{target}:{project} skipped (unsupported test builder {builder}{(unitTest ? $", runner {runner ?? "vitest"}" : string.Empty)})");
                    continue;
                }

                index++;
                frontendTargets.Add(new FrontendCoverageTarget(
                    target,
                    path,
                    project,
                    unitTest,
                    Path.Combine(context.OutputDirectory, $"karma-coverage-{index}.conf.cjs"),
                    Path.Combine(context.OutputDirectory, $"frontend-coverage-{index}")));
            }
        }

        var testFilter = ScannerSupport.GetString(sonarConfig, "testFilter");
        return new CoveragePlan(
            true, dotNetCoverage, dotNetReportPath, string.IsNullOrWhiteSpace(testFilter) ? null : testFilter,
            frontendTargets, notes);
    }

    /// <summary>
    /// Runs the tests that produce the coverage reports named at "begin". Never throws (except on
    /// cancellation): every failure becomes a note, so the caller always proceeds to "end".
    /// </summary>
    private async Task CollectCoverageAsync(
        ScanContext context, string target, CoveragePlan coverage, List<string> notes, CancellationToken cancellationToken)
    {
        if (coverage.DotNetCoverageExecutable is not null)
        {
            try
            {
                var arguments = new List<string>
                {
                    "collect", "--nologo", "-f", "xml", "-o", coverage.DotNetReportPath,
                    "dotnet", "test", target, "--no-build",
                };
                if (coverage.TestFilter is not null)
                {
                    arguments.AddRange(new[] { "--filter", coverage.TestFilter });
                }

                var result = await _processRunner.RunAsync(
                    coverage.DotNetCoverageExecutable, arguments, context.Root,
                    Path.Combine(context.OutputDirectory, "sonar-test-dotnet.log"),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                notes.Add(DescribeTestRun(".NET", result.ExitCode, File.Exists(coverage.DotNetReportPath), "sonar-test-dotnet.log"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($".NET failed ({ex.Message})");
            }
        }

        var index = 0;
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var frontend in coverage.FrontendTargets)
        {
            index++;
            var logFileName = $"sonar-test-frontend-{index}.log";
            try
            {
                var npm = _resolveNpm();
                if (installed.Add(frontend.Path) && !Directory.Exists(Path.Combine(frontend.Path, "node_modules")))
                {
                    await ScannerSupport.RunNativeAsync(
                        _processRunner, context, npm.FileName,
                        npm.PrefixArguments.Concat(new[] { "ci", "--no-audit", "--no-fund" }).ToArray(),
                        $"sonar-npm-ci-{index}.log", workingDirectory: frontend.Path,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                await File.WriteAllTextAsync(
                    frontend.KarmaConfigPath, KarmaCoverageConfig.Build(frontend.CoverageDirectory), cancellationToken).ConfigureAwait(false);

                var ngArguments = new List<string>
                {
                    "exec", "--no", "--", "ng", "test", frontend.Project, "--watch=false",
                    $"--browsers={KarmaCoverageConfig.LauncherName}",
                };
                ngArguments.AddRange(frontend.UnitTestBuilder
                    ? new[] { "--coverage", $"--runner-config={frontend.KarmaConfigPath}" }
                    : new[] { "--code-coverage", "--progress=false", $"--karma-config={frontend.KarmaConfigPath}" });

                var result = await _processRunner.RunAsync(
                    npm.FileName,
                    npm.PrefixArguments.Concat(ngArguments).ToArray(),
                    frontend.Path,
                    Path.Combine(context.OutputDirectory, logFileName),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var lcovPath = Path.Combine(frontend.CoverageDirectory, "lcov.info");
                await AnchorLcovSourcePathsAsync(lcovPath, frontend.Path, cancellationToken).ConfigureAwait(false);

                notes.Add(DescribeTestRun(frontend.Label, result.ExitCode, File.Exists(lcovPath), logFileName));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"{frontend.Label} failed ({ex.Message})");
            }
        }
    }

    /// <summary>
    /// Karma writes the lcov <c>SF:</c> paths relative to the Angular workspace (<c>src\app\...</c>).
    /// Sonar resolves those by suffix, but the pipeline's Code Coverage tab maps them onto the
    /// repository root and silently drops every file it cannot find there, so only the .NET report
    /// (absolute paths) showed up in its file view. Absolute paths resolve in both.
    /// </summary>
    private static async Task AnchorLcovSourcePathsAsync(string lcovPath, string workspacePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(lcovPath))
        {
            return;
        }

        var lines = await File.ReadAllLinesAsync(lcovPath, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("SF:", StringComparison.Ordinal) && !Path.IsPathRooted(lines[i][3..]))
            {
                lines[i] = "SF:" + Path.GetFullPath(Path.Combine(workspacePath, lines[i][3..]));
            }
        }

        await File.WriteAllLinesAsync(lcovPath, lines, cancellationToken).ConfigureAwait(false);
    }

    private static string DescribeTestRun(string label, int exitCode, bool reportWritten, string logFileName)
        => (exitCode, reportWritten) switch
        {
            (0, true) => $"{label} ok",
            (_, true) => $"{label} report imported, but tests failed (exit {exitCode}; see {logFileName})",
            _ => $"{label} no report (exit {exitCode}; see {logFileName})",
        };

    /// <summary>Sonar analysis properties take forward-slash paths on every OS.</summary>
    private static string ToSonarPath(string path) => path.Replace('\\', '/');
}
