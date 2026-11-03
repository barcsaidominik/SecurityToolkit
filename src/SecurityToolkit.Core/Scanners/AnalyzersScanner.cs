namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// .NET build + Roslyn security analyzer warning inventory. Restores (with the security MSBuild
/// properties enabled) then builds each configured dotnet target, counting distinct security
/// warning lines: CA30xx/CA31xx, EF1002 (raw SQL built from an interpolated/concatenated string)
/// and NU1507 (several package sources without packageSourceMapping: dependency confusion). This
/// is a build warning inventory, not a differential new-finding gate — never blocking.
/// </summary>
public sealed class AnalyzersScanner : IScanner
{
    private static readonly System.Text.RegularExpressions.Regex WarningPattern =
        new(@"(?m)^.*\bwarning (?<code>CA(?:30|31)\d+|EF1002|NU1507)\b.*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // The CA30xx taint rules only treat MVC controller action parameters as untrusted input, so a
    // minimal API (MapGet/Carter) surface stays uncovered; a 0 must not read as "no injection".
    private const string ResultNote =
        "Build warning inventory; not a differential gate. CA30xx taint rules cover MVC controller actions only, not minimal API endpoints.";

    private readonly IProcessRunner _processRunner;

    public AnalyzersScanner(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public string Name => "analyzers";

    public async Task<IReadOnlyList<ScanResult>> RunAsync(ScanContext context, CancellationToken cancellationToken = default)
    {
        if (!ScannerSupport.IsToolEnabled(context, "analyzers"))
        {
            return Array.Empty<ScanResult>();
        }

        var results = new List<ScanResult>();
        var index = 0;

        foreach (var target in ScannerSupport.GetConfigTargets(context, "dotnetTargets"))
        {
            index++;
            var name = $"dotnet-{index}";
            try
            {
                var path = ToolkitPaths.GetToolkitPath(context.Root, target);
                var properties = new[]
                {
                    "-p:SecurityToolkitEnabled=true",
                    "-p:SecurityToolkitAnalyzers=true",
                    "-p:SecurityToolkitAudit=false",
                    ScannerSupport.NoNodeReuseSwitch,
                };

                await ScannerSupport.RunNativeAsync(
                    _processRunner, context, "dotnet",
                    new[] { "restore", path }.Concat(properties).ToArray(),
                    $"{name}-restore.log",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var buildArguments = new[] { "build", path, "--no-restore", "--no-incremental", "-p:TreatWarningsAsErrors=false" }
                    .Concat(properties).ToArray();
                var buildResult = await ScannerSupport.RunNativeAsync(
                    _processRunner, context, "dotnet", buildArguments, $"{name}-build.log",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var combinedOutput = buildResult.StdOut + buildResult.StdErr;
                var warnings = WarningPattern.Matches(combinedOutput)
                    .DistinctBy(m => m.Value)
                    .ToArray();
                var byCode = string.Join(", ", warnings
                    .GroupBy(m => m.Groups["code"].Value, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => $"{g.Key}: {g.Count()}"));

                results.Add(new ScanResult(
                    "analyzers", target, ScanStatus.Completed, warnings.Length, Blocking: false,
                    byCode.Length > 0 ? $"{byCode}. {ResultNote}" : ResultNote));
            }
            catch (Exception ex)
            {
                results.Add(new ScanResult("dotnet", target, ScanStatus.Failed, null, Blocking: false, ex.Message));
            }
        }

        if (index > 0)
        {
            await ScannerSupport.ShutdownBuildServersAsync(_processRunner, context, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }
}
