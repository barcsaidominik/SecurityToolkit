namespace SecurityToolkit.Core.Installation;

/// <summary>Outcome of one <see cref="PrerequisiteProvisioner.EnsureAsync"/> run.</summary>
/// <param name="Completed">True when every required prerequisite ended up available.</param>
/// <param name="InstalledCommands">Commands winget was used to install, in order.</param>
/// <param name="Messages">
/// Human-readable notes for missing/uninstallable prerequisites, collected instead of thrown so
/// the caller can decide how to surface them.
/// </param>
public sealed record PrerequisiteOutcome(bool Completed, IReadOnlyList<string> InstalledCommands, IReadOnlyList<string> Messages);

/// <summary>
/// Ensures git/dotnet/node/docker are available before <see cref="ToolProvisioner"/> runs,
/// installing missing ones via winget on Windows when <c>installPrerequisites</c> is enabled.
/// </summary>
public sealed class PrerequisiteProvisioner
{
    private readonly IProcessRunner _processRunner;
    private readonly Func<string, string?> _findOnPath;
    private readonly Action _refreshPath;

    public PrerequisiteProvisioner(
        IProcessRunner processRunner,
        Func<string, string?>? findOnPath = null,
        Action? refreshPath = null)
    {
        _processRunner = processRunner;
        _findOnPath = findOnPath ?? EnvironmentPath.FindOnPath;
        _refreshPath = refreshPath ?? EnvironmentPath.RefreshFromMachineAndUser;
    }

    /// <param name="tools">The resolved <c>security.config.json</c> "tools" section.</param>
    /// <param name="installPrerequisites">The config's <c>installPrerequisites</c>.</param>
    /// <param name="sdkChannel">The config's <c>sdkChannel</c> (e.g. "10.0").</param>
    /// <param name="nodeWingetId">The config's <c>nodeWingetId</c>.</param>
    /// <param name="nodeVersion">The config's <c>nodeVersion</c>; empty installs the package's current version.</param>
    public async Task<PrerequisiteOutcome> EnsureAsync(
        IDictionary<string, object?> tools,
        bool installPrerequisites,
        string sdkChannel,
        string nodeWingetId,
        string nodeVersion,
        CancellationToken cancellationToken = default)
    {
        var needed = new List<(string Command, string WingetId)> { ("git", "Git.Git") };

        if (GetBool(tools, "analyzers") || GetBool(tools, "nuget") || GetBool(tools, "devskim") || GetBool(tools, "sonar"))
        {
            needed.Add(("dotnet", $"Microsoft.DotNet.SDK.{sdkChannel.Split('.')[0]}"));
        }

        if (GetBool(tools, "npm"))
        {
            needed.Add(("node", nodeWingetId));
        }

        if (GetBool(tools, "semgrep") || GetBool(tools, "gitleaks") || GetBool(tools, "osv"))
        {
            needed.Add(("docker", "Docker.DockerDesktop"));
        }

        var installed = new List<string>();
        var messages = new List<string>();

        foreach (var (command, wingetId) in needed)
        {
            if (_findOnPath(command) is not null)
            {
                continue;
            }

            if (!installPrerequisites)
            {
                messages.Add($"Missing prerequisite: {command}. Enable installPrerequisites or install it with the system package manager.");
                continue;
            }

            if (!OperatingSystem.IsWindows())
            {
                messages.Add($"Missing prerequisite: {command}. Automatic installation is Windows-only (winget); on Linux install it with the distribution's package manager and rerun.");
                continue;
            }

            var wingetPath = _findOnPath("winget");
            if (wingetPath is null)
            {
                messages.Add($"Missing prerequisite: {command}. winget is not on PATH; install it (or {command}) manually and rerun.");
                continue;
            }

            var args = new List<string>
            {
                "install", "--id", wingetId, "--exact", "--source", "winget",
                "--accept-source-agreements", "--accept-package-agreements",
            };

            if (command == "node" && !string.IsNullOrEmpty(nodeVersion))
            {
                args.Add("--version");
                args.Add(nodeVersion);
            }

            var result = await _processRunner.RunAsync(wingetPath, args, timeoutSeconds: 1800, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                messages.Add($"winget failed for {command}: {result.StdErr} {result.StdOut}".Trim());
                continue;
            }

            installed.Add(command);

            _refreshPath();

            if (_findOnPath(command) is null)
            {
                messages.Add($"Installed {command} but it is not available in this session. Restart your terminal/Windows if required, then rerun.");
            }
        }

        return new PrerequisiteOutcome(messages.Count == 0, installed, messages);
    }

    private static bool GetBool(IDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out var value) && value is bool b && b;
}
