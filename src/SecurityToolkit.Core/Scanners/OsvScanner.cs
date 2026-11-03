namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// OSV-Scanner dependency vulnerability scan via the pinned Docker image. Supplementary inventory only — never blocking,
/// regardless of mode.
/// </summary>
public sealed class OsvScanner : IScanner
{
    private readonly IProcessRunner _processRunner;
    private readonly IReadOnlyList<string> _dockerMountArguments;

    public OsvScanner(IProcessRunner processRunner, IReadOnlyList<string> dockerMountArguments)
    {
        _processRunner = processRunner;
        _dockerMountArguments = dockerMountArguments;
    }

    public string Name => "osv";

    public async Task<IReadOnlyList<ScanResult>> RunAsync(ScanContext context, CancellationToken cancellationToken = default)
    {
        if (context.Staged || !ScannerSupport.IsToolEnabled(context, "osv"))
        {
            return Array.Empty<ScanResult>();
        }

        try
        {
            var image = ScannerSupport.RequireImage(context.Lock, "osv");

            var arguments = new List<string> { "run" };
            arguments.AddRange(_dockerMountArguments);
            arguments.AddRange(new[]
            {
                image, "scan", "source", "--recursive", ".", "--format", "json",
                "--output-file", $"{context.ContainerOutputDirectory}/osv.json",
            });

            var runResult = await ScannerSupport.RunNativeAsync(
                _processRunner, context, "docker", arguments, "osv.log",
                allowedExitCodes: new[] { 0, 1 }, cancellationToken: cancellationToken).ConfigureAwait(false);

            var reportPath = Path.Combine(context.OutputDirectory, "osv.json");
            var data = ScannerSupport.ReadReport(reportPath);
            if (!data.ContainsKey("results"))
            {
                throw new InvalidOperationException("Invalid OSV JSON schema.");
            }

            var count = 0;
            foreach (var groupObj in ScannerSupport.GetList(data, "results"))
            {
                if (groupObj is not IDictionary<string, object?> group || !group.ContainsKey("packages"))
                {
                    continue;
                }

                foreach (var packageObj in ScannerSupport.GetList(group, "packages"))
                {
                    if (packageObj is IDictionary<string, object?> package && package.ContainsKey("vulnerabilities"))
                    {
                        count += ScannerSupport.GetList(package, "vulnerabilities").Count;
                    }
                }
            }

            if (runResult.ExitCode == 1 && count == 0)
            {
                throw new InvalidOperationException("OSV exit 1 without vulnerabilities: treat as a scanner error.");
            }

            return new[]
            {
                new ScanResult("osv", "repository", ScanStatus.Completed, count, Blocking: false,
                    "Supplementary inventory; exit 1 must include actual vulnerabilities."),
            };
        }
        catch (Exception ex)
        {
            return new[] { new ScanResult("osv", "repository", ScanStatus.Failed, null, Blocking: false, ex.Message) };
        }
    }
}
