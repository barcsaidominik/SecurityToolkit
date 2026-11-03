using SecurityToolkit.Core.Scanners;
using System.CommandLine;

namespace SecurityToolkit.Cli.Commands;

/// <summary>
/// Builds the "report" command: renders a scan run's status.json as a human-readable, colored
/// console table, so developers can review a run without opening any JSON file directly. Defaults
/// to the most recent run ("results/latest-status.json"); pass <c>--run</c> to inspect an older one.
/// </summary>
public static class ReportCommandFactory
{
    public static Command Create()
    {
        var projectRootOption = new Option<string>("--project-root")
        {
            Description = "Repository root containing .security/.",
            Required = true,
        };
        var runOption = new Option<string?>("--run")
        {
            Description = "A specific run id (the results/<run> directory name). Defaults to the most recent run.",
        };

        var command = new Command("report", "Print the most recent (or a specific) scan run as a human-readable table.")
        {
            projectRootOption, runOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var root = Path.GetFullPath(parseResult.GetValue(projectRootOption)!);
            var resultsDirectory = Path.Combine(root, ".security", "results");
            var run = parseResult.GetValue(runOption);

            string outputDirectory;
            if (run is not null)
            {
                outputDirectory = Path.Combine(resultsDirectory, run);
            }
            else if (Directory.Exists(resultsDirectory))
            {
                outputDirectory = Directory.GetDirectories(resultsDirectory)
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                    .FirstOrDefault() ?? resultsDirectory;
            }
            else
            {
                outputDirectory = resultsDirectory;
            }

            var statusPath = Path.Combine(outputDirectory, "status.json");
            if (!File.Exists(statusPath))
            {
                Console.Error.WriteLine($"No scan report found: {statusPath}. Run 'security-toolkit scan' first.");
                return 2;
            }

            var statusDocument = ScannerSupport.ParseJsonObject(
                await File.ReadAllTextAsync(statusPath, cancellationToken).ConfigureAwait(false));

            ScanReportFormatter.WriteConsoleTable(Console.Out, statusDocument);
            return 0;
        });

        return command;
    }
}
