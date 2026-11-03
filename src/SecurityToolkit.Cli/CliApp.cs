using SecurityToolkit.Cli.Commands;
using System.CommandLine;

namespace SecurityToolkit.Cli;

/// <summary>
/// Builds the CLI's command tree. Kept separate from Program.cs (whose top-level statements
/// aren't otherwise referenceable) so tests can parse/invoke commands without spawning the
/// published executable.
/// </summary>
public static class CliApp
{
    public static RootCommand BuildRootCommand() => new("SecurityToolkit CLI - install, scan, and restore the local security toolkit.")
    {
        InstallCommandFactory.Create(),
        ScanCommandFactory.Create(),
        RestoreCommandFactory.Create(),
        ReportCommandFactory.Create(),
        InfraCommandFactory.Create(),
    };
}
