using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Core.Installation;

/// <summary>Outcome of one <see cref="ToolProvisioner.ProvisionAsync"/> run.</summary>
/// <param name="Completed">True when every enabled tool provisioned cleanly (no errors).</param>
/// <param name="Errors">Per-tool failure messages, "&lt;name&gt;: &lt;message&gt;" (also written to setup-status.json's "errors").</param>
public sealed record ProvisionOutcome(bool Completed, IReadOnlyList<string> Errors);

/// <summary>
/// Provisions the tools recorded in security.lock.json: pulls + digest-pins the Docker images,
/// and installs/validates the pinned .NET tool versions. Writes back the updated lock
/// (now-ready/pinned entries) plus ".security/results/setup-status.json". Runs at the end of a
/// full install and, with <c>requirePinned</c>, as the pipeline's <c>security-toolkit provision</c> step.
/// </summary>
public sealed class ToolProvisioner
{
    private static readonly string[] DockerImageTools = { "semgrep", "gitleaks", "osv" };
    private static readonly string[] DotNetTools = { "devskim", "sonarScanner", "dotnetCoverage" };

    private readonly IProcessRunner _processRunner;
    private readonly NuGetVersionResolver _nuGetVersionResolver;

    public ToolProvisioner(IProcessRunner processRunner, NuGetVersionResolver nuGetVersionResolver)
    {
        _processRunner = processRunner;
        _nuGetVersionResolver = nuGetVersionResolver;
    }

    /// <param name="securityDirectory">Absolute path to ".security".</param>
    /// <param name="requirePinned">
    /// Refuses to (re-)resolve a floating image tag/unpinned tool version;
    /// every enabled tool must already be pinned by a prior install run.
    /// </param>
    public async Task<ProvisionOutcome> ProvisionAsync(
        string securityDirectory,
        bool requirePinned = false,
        CancellationToken cancellationToken = default)
    {
        var lockData = ToolkitJson.ReadLock(securityDirectory);
        var tools = ScannerSupport.GetDictionary(lockData, "tools")
            ?? throw new InvalidOperationException("security.lock.json has no tools section.");

        var errors = new List<string>();
        var bin = Path.Combine(securityDirectory, "bin");
        Directory.CreateDirectory(bin);

        foreach (var name in DockerImageTools)
        {
            await ProvisionDockerImageAsync(name, tools, requirePinned, errors, cancellationToken).ConfigureAwait(false);
        }

        foreach (var name in DotNetTools)
        {
            if (ScannerSupport.GetDictionary(tools, name) is not { } tool || !ScannerSupport.GetBool(tool, "enabled"))
            {
                continue;
            }

            try
            {
                var package = ScannerSupport.GetString(tool, "package") ?? throw new InvalidOperationException("Missing package.");
                var version = ScannerSupport.GetString(tool, "version");

                if (requirePinned && string.IsNullOrEmpty(version))
                {
                    throw new InvalidOperationException("Pin the .NET tool version first.");
                }

                if (string.IsNullOrEmpty(version))
                {
                    version = await _nuGetVersionResolver.GetLatestStableVersionAsync(package, cancellationToken).ConfigureAwait(false);
                }

                var list = await _processRunner.RunAsync(
                    "dotnet", new[] { "tool", "list", "--tool-path", bin }, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (list.ExitCode != 0)
                {
                    throw new InvalidOperationException(list.StdErr);
                }

                var row = System.Text.RegularExpressions.Regex.Match(
                    list.StdOut, $@"(?im)^{System.Text.RegularExpressions.Regex.Escape(package)}\s+(\S+)\s+");

                if (row.Success && !string.Equals(row.Groups[1].Value, version, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Existing tool version differs. Remove/update {package} explicitly before reprovisioning.");
                }

                if (!row.Success)
                {
                    var install = await _processRunner.RunAsync(
                        "dotnet", new[] { "tool", "install", package, "--version", version, "--tool-path", bin },
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (install.ExitCode != 0)
                    {
                        throw new InvalidOperationException(install.StdErr);
                    }
                }

                tool["version"] = version;
                tool["ready"] = true;
            }
            catch (Exception ex)
            {
                tool["ready"] = false;
                errors.Add($"{name}: {ex.Message}");
            }
        }

        ToolkitJson.WriteFile(Path.Combine(securityDirectory, "security.lock.json"), lockData);

        var resultsDirectory = Path.Combine(securityDirectory, "results");
        Directory.CreateDirectory(resultsDirectory);

        var completed = errors.Count == 0;
        ToolkitJson.WriteFile(Path.Combine(resultsDirectory, "setup-status.json"), new Dictionary<string, object?>
        {
            ["errors"] = errors.Cast<object?>().ToList(),
            ["completed"] = completed,
        });

        return new ProvisionOutcome(completed, errors);
    }

    /// <summary>Pulls and digest-pins one enabled Docker-image tool entry in place within <paramref name="tools"/>; records a per-tool error on failure instead of throwing.</summary>
    private async Task ProvisionDockerImageAsync(
        string name, IDictionary<string, object?> tools, bool requirePinned, List<string> errors, CancellationToken cancellationToken)
    {
        if (ScannerSupport.GetDictionary(tools, name) is not { } tool || !ScannerSupport.GetBool(tool, "enabled"))
        {
            return;
        }

        try
        {
            var image = ScannerSupport.GetString(tool, "image") ?? throw new InvalidOperationException("Missing image.");

            if (requirePinned && !System.Text.RegularExpressions.Regex.IsMatch(image, "@sha256:[a-f0-9]{64}$"))
            {
                throw new InvalidOperationException("Image must be pinned first by the installer.");
            }

            var pull = await _processRunner.RunAsync("docker", new[] { "pull", image }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (pull.ExitCode != 0)
            {
                throw new InvalidOperationException(pull.StdErr);
            }

            var inspect = await _processRunner.RunAsync(
                "docker", new[] { "image", "inspect", image, "--format", "{{index .RepoDigests 0}}" },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var digest = inspect.StdOut.Trim();
            if (inspect.ExitCode != 0 || !System.Text.RegularExpressions.Regex.IsMatch(digest, "@sha256:[a-f0-9]{64}$"))
            {
                throw new InvalidOperationException("Cannot resolve image digest.");
            }

            tool["image"] = digest;
            tool["ready"] = true;
        }
        catch (Exception ex)
        {
            tool["ready"] = false;
            errors.Add($"{name}: {ex.Message}");
        }
    }
}
