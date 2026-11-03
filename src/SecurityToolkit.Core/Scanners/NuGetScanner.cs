namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// NuGet package vulnerability audit. Restores each configured dotnet target (audit source availability is
/// detected from NU1900/NU1905 in the restore output), then runs
/// "dotnet list package --vulnerable --include-transitive" and counts findings, flagging High/Critical
/// severities as blocking in Gate mode.
/// </summary>
public sealed class NuGetScanner : IScanner
{
    private static readonly System.Text.RegularExpressions.Regex AuditUnavailablePattern =
        new(@"\bNU190[05]\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly IProcessRunner _processRunner;

    public NuGetScanner(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public string Name => "nuget";

    public async Task<IReadOnlyList<ScanResult>> RunAsync(ScanContext context, CancellationToken cancellationToken = default)
    {
        if (!ScannerSupport.IsToolEnabled(context, "nuget"))
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
                    "-p:SecurityToolkitAnalyzers=false",
                    "-p:SecurityToolkitAudit=true",
                    ScannerSupport.NoNodeReuseSwitch,
                };

                var restoreResult = await ScannerSupport.RunNativeAsync(
                    _processRunner, context, "dotnet",
                    new[] { "restore", path }.Concat(properties).ToArray(),
                    $"{name}-restore.log",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var auditUnavailable = AuditUnavailablePattern.IsMatch(restoreResult.StdOut + restoreResult.StdErr);
                if (auditUnavailable)
                {
                    results.Add(new ScanResult(
                        "nuget", target, ScanStatus.Failed, null, Blocking: false,
                        "NuGet vulnerability source unavailable (NU1900/NU1905); see restore log. " +
                        "Package restore succeeded; analyzer build was attempted independently."));
                    continue;
                }

                var listResult = await ScannerSupport.RunNativeAsync(
                    _processRunner, context, "dotnet",
                    new[] { "list", path, "package", "--vulnerable", "--include-transitive", "--format", "json" },
                    $"{name}-nuget.log",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var reportPath = Path.Combine(context.OutputDirectory, $"{name}-nuget.json");
                await File.WriteAllTextAsync(reportPath, listResult.StdOut, cancellationToken).ConfigureAwait(false);

                var data = ScannerSupport.ReadReport(reportPath);
                if (ScannerSupport.GetList(data, "problems").Count > 0)
                {
                    throw new InvalidOperationException("NuGet report contains problems.");
                }
                if (!data.ContainsKey("projects"))
                {
                    throw new InvalidOperationException("Invalid NuGet report schema.");
                }

                var count = 0;
                var high = 0;
                foreach (var projectObj in ScannerSupport.GetList(data, "projects"))
                {
                    if (projectObj is not IDictionary<string, object?> project || !project.ContainsKey("frameworks"))
                    {
                        continue;
                    }

                    foreach (var frameworkObj in ScannerSupport.GetList(project, "frameworks"))
                    {
                        if (frameworkObj is not IDictionary<string, object?> framework)
                        {
                            continue;
                        }

                        foreach (var packageKey in new[] { "topLevelPackages", "transitivePackages" })
                        {
                            if (!framework.ContainsKey(packageKey))
                            {
                                continue;
                            }

                            foreach (var packageObj in ScannerSupport.GetList(framework, packageKey))
                            {
                                if (packageObj is not IDictionary<string, object?> package || !package.ContainsKey("vulnerabilities"))
                                {
                                    continue;
                                }

                                foreach (var vulnObj in ScannerSupport.GetList(package, "vulnerabilities"))
                                {
                                    if (vulnObj is not IDictionary<string, object?> vulnerability)
                                    {
                                        continue;
                                    }

                                    count++;
                                    var severity = ScannerSupport.GetString(vulnerability, "severity");
                                    if (severity is "High" or "Critical")
                                    {
                                        high++;
                                    }
                                }
                            }
                        }
                    }
                }

                var blocking = context.Mode == ScanMode.Gate && high > 0;
                results.Add(new ScanResult("nuget", target, ScanStatus.Completed, count, blocking));
            }
            catch (Exception ex)
            {
                results.Add(new ScanResult("nuget", target, ScanStatus.Failed, null, Blocking: false, ex.Message));
            }
        }

        return results;
    }
}
