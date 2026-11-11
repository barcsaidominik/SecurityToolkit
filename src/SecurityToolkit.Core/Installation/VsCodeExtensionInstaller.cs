namespace SecurityToolkit.Core.Installation;

/// <summary>Outcome of one <see cref="VsCodeExtensionInstaller.InstallAsync"/> call.</summary>
/// <param name="CodeNotFound">True when the "code" command isn't on PATH (handled gracefully, not thrown).</param>
/// <param name="InstalledExtensionIds">Extension ids that were successfully installed, in order.</param>
/// <param name="Message">
/// An informational note: either "no extensions selected" or nothing to report; empty when
/// extensions installed cleanly.
/// </param>
public sealed record VsCodeExtensionOutcome(bool CodeNotFound, IReadOnlyList<string> InstalledExtensionIds, string Message);

/// <summary>
/// Installs the VS Code extensions matching the enabled tools (SonarLint for Sonar, DevSkim's
/// extension for DevSkim).
/// </summary>
public sealed class VsCodeExtensionInstaller
{
    private readonly IProcessRunner _processRunner;
    private readonly Func<string, string?> _findOnPath;

    public VsCodeExtensionInstaller(IProcessRunner processRunner, Func<string, string?>? findOnPath = null)
    {
        _processRunner = processRunner;
        _findOnPath = findOnPath ?? EnvironmentPath.FindOnPath;
    }

    /// <param name="sonarEnabled">The config's <c>tools.sonar</c>.</param>
    /// <param name="devSkimEnabled">The config's <c>tools.devskim</c>.</param>
    /// <exception cref="InvalidOperationException">"code" is on PATH but an extension install fails.</exception>
    public async Task<VsCodeExtensionOutcome> InstallAsync(
        bool sonarEnabled, bool devSkimEnabled, CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        if (sonarEnabled)
        {
            ids.Add("SonarSource.sonarlint-vscode");
        }

        if (devSkimEnabled)
        {
            ids.Add("ms-cst-e.vscode-devskim");
        }

        if (ids.Count == 0)
        {
            return new VsCodeExtensionOutcome(
                CodeNotFound: false,
                InstalledExtensionIds: Array.Empty<string>(),
                "No IDE extension selected for automatic installation. The .NET analyzers work through the project settings.");
        }

        var codePath = _findOnPath("code");
        if (codePath is null)
        {
            // Gracefully report, never throw: "code" simply not being on PATH is an environment
            // fact, not an installer failure.
            return new VsCodeExtensionOutcome(CodeNotFound: true, Array.Empty<string>(), string.Empty);
        }

        foreach (var id in ids)
        {
            var result = await _processRunner.RunAsync(
                codePath, new[] { "--install-extension", id }, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"VS Code extension installation failed: {id}");
            }
        }

        return new VsCodeExtensionOutcome(
            CodeNotFound: false,
            ids,
            "Visual Studio extensions and IDE connected-mode authentication are set up in the IDE's own Extension Manager / connection wizard.");
    }
}
