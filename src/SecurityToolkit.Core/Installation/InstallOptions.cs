namespace SecurityToolkit.Core.Installation;

/// <summary>
/// Semgrep scan scope selected at install time (<c>--semgrep-scope</c>: "repository" or "frontend").
/// </summary>
public enum SemgrepInstallScope
{
    /// <summary>Not explicitly selected; the installer keeps the existing/default value ("repository").</summary>
    Unspecified,
    Repository,
    Frontend,
}

/// <summary>
/// Options of one install run (the CLI's <c>install</c> switches).
/// </summary>
/// <param name="ProjectRoot">The target project/repository root directory. Required.</param>
/// <param name="ConfigFile">
/// Optional path to a JSON profile merged over the defaults/existing security.config.json
/// (unknown keys throw).
/// </param>
/// <param name="NonInteractive">
/// Skips every interactive prompt; unanswered choices fall back to their current/default value.
/// Selection prompts (targets) default to "all" rather than to the (usually empty) previous
/// selection.
/// </param>
/// <param name="ConfigureOnly">
/// Generates/updates project files (security.config.json, props, ignore blocks, pipeline YAML)
/// but skips prerequisite installation, tool provisioning, rule downloads, and VS Code extensions.
/// </param>
/// <param name="DryRun">Prints the resolved configuration and writes nothing to disk.</param>
/// <param name="RunAfterInstall">Runs a full Report-mode scan immediately after a successful install.</param>
/// <param name="SemgrepScope">
/// Overrides the persisted Semgrep scope for this run. <see cref="SemgrepInstallScope.Unspecified"/>
/// leaves the existing/default value untouched.
/// </param>
/// <param name="CheckOnly">
/// Reports, for every toolkit-managed project file, whether the current on-disk content matches
/// what this install run would generate — without writing anything, without provisioning tools,
/// and without ever throwing on an owned file's manual-edit hash mismatch (see
/// <see cref="InstallOutcome.DriftEntries"/>). Answers "is this project's .security/ in sync with
/// the toolkit source it was generated from?" so drift can be caught deliberately instead of
/// discovered by a real install refusing to overwrite an unexpectedly-changed file.
/// </param>
public sealed record InstallOptions(
    string ProjectRoot,
    string? ConfigFile = null,
    bool NonInteractive = false,
    bool ConfigureOnly = false,
    bool DryRun = false,
    bool RunAfterInstall = false,
    SemgrepInstallScope SemgrepScope = SemgrepInstallScope.Unspecified,
    bool CheckOnly = false);
