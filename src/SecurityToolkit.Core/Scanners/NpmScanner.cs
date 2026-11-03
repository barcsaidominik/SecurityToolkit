namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// npm audit. Requires package-lock.json or
/// npm-shrinkwrap.json (other package managers are never silently converted), resolves a
/// shell-free npm invocation via <see cref="NpmResolver"/>, and treats High/Critical vulnerability
/// counts as blocking in Gate mode.
/// </summary>
public sealed class NpmScanner : IScanner
{
    private readonly IProcessRunner _processRunner;
    private readonly Func<NpmInvocation> _resolveNpm;

    public NpmScanner(IProcessRunner processRunner, Func<NpmInvocation>? resolveNpm = null)
    {
        _processRunner = processRunner;
        _resolveNpm = resolveNpm ?? NpmResolver.Resolve;
    }

    public string Name => "npm";

    public async Task<IReadOnlyList<ScanResult>> RunAsync(ScanContext context, CancellationToken cancellationToken = default)
    {
        if (!ScannerSupport.IsToolEnabled(context, "npm"))
        {
            return Array.Empty<ScanResult>();
        }

        var results = new List<ScanResult>();
        var index = 0;

        foreach (var target in ScannerSupport.GetConfigTargets(context, "frontendTargets"))
        {
            index++;
            try
            {
                var path = ToolkitPaths.GetToolkitPath(context.Root, target);

                if (!File.Exists(Path.Combine(path, "package-lock.json")) &&
                    !File.Exists(Path.Combine(path, "npm-shrinkwrap.json")))
                {
                    throw new InvalidOperationException(
                        "npm audit requires package-lock.json or npm-shrinkwrap.json. Other package managers are not silently converted.");
                }

                var npm = _resolveNpm();
                var arguments = npm.PrefixArguments.Concat(new[] { "audit", "--json" }).ToArray();
                var runResult = await ScannerSupport.RunNativeAsync(
                    _processRunner, context, npm.FileName, arguments, $"npm-{index}.log",
                    allowedExitCodes: new[] { 0, 1 }, workingDirectory: path,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var reportPath = Path.Combine(context.OutputDirectory, $"npm-{index}.json");
                await File.WriteAllTextAsync(reportPath, runResult.StdOut, cancellationToken).ConfigureAwait(false);

                var data = ScannerSupport.ReadReport(reportPath);
                if (data.ContainsKey("error"))
                {
                    throw new InvalidOperationException("npm registry/audit error.");
                }

                var metadata = ScannerSupport.GetDictionary(data, "metadata");
                if (metadata is null || !metadata.ContainsKey("vulnerabilities"))
                {
                    throw new InvalidOperationException("Invalid npm audit schema.");
                }

                var vulnerabilities = ScannerSupport.GetDictionary(metadata, "vulnerabilities")!;
                var total = ScannerSupport.GetInt(vulnerabilities, "total");
                var high = ScannerSupport.GetInt(vulnerabilities, "high");
                var critical = ScannerSupport.GetInt(vulnerabilities, "critical");

                var blocking = context.Mode == ScanMode.Gate && (high + critical) > 0;
                results.Add(new ScanResult("npm", target, ScanStatus.Completed, total, blocking));
            }
            catch (Exception ex)
            {
                results.Add(new ScanResult("npm", target, ScanStatus.Failed, null, Blocking: false, ex.Message));
            }
        }

        return results;
    }
}
