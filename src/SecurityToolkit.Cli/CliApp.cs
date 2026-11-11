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
    /// <summary>Exit code of a technical/configuration error in the 0/1/2 contract (1 = blocking findings).</summary>
    public const int TechnicalErrorExitCode = 2;

    public static RootCommand BuildRootCommand() => new("SecurityToolkit CLI - install, scan, and restore the local security toolkit.")
    {
        InstallCommandFactory.Create(),
        ProvisionCommandFactory.Create(),
        ScanCommandFactory.Create(),
        RestoreCommandFactory.Create(),
        ReportCommandFactory.Create(),
        InfraCommandFactory.Create(),
    };

    /// <summary>
    /// The process entry point: parses and runs <paramref name="args"/>, returning the command's
    /// own exit code (0/1/2). An exception that escapes a command becomes exit 2 with a one-line
    /// message on <paramref name="error"/>: System.CommandLine's default handler would print a
    /// stack trace and exit 1, which pipelines read as "blocking findings" (e.g. a scan of a
    /// repository without .security/security.config.json). Parse errors keep System.CommandLine's
    /// own exit code and help output.
    /// </summary>
    public static async Task<int> InvokeAsync(string[] args, TextWriter? error = null, CancellationToken cancellationToken = default)
    {
        error ??= Console.Error;
        try
        {
            return await BuildRootCommand()
                .Parse(args)
                .InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"Error: {ex.Message} ({ex.GetType().Name})").ConfigureAwait(false);
            return TechnicalErrorExitCode;
        }
    }
}
