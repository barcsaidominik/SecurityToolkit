using SecurityToolkit.Core.Installation;
using SecurityToolkit.Core.Scanners;
using System.CommandLine;

namespace SecurityToolkit.Cli.Commands;

/// <summary>
/// Builds the "restore" command: reverts one install transaction from its manifest.json.
/// </summary>
public static class RestoreCommandFactory
{
    public static Command Create()
    {
        var projectRootOption = new Option<string>("--project-root")
        {
            Description = "Project root the manifest's paths are relative to.",
            Required = true,
        };
        var manifestOption = new Option<string>("--manifest")
        {
            Description = "Path to the transaction's manifest.json (e.g. .security/backups/<timestamp>/manifest.json).",
            Required = true,
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Report what would be restored; write/delete nothing.",
        };

        var command = new Command("restore", "Revert one install transaction using its manifest.json.")
        {
            projectRootOption, manifestOption, dryRunOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var root = Path.GetFullPath(parseResult.GetValue(projectRootOption)!);
            var manifestPath = parseResult.GetValue(manifestOption)!;
            var dryRun = parseResult.GetValue(dryRunOption);

            var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var manifestTree = ScannerSupport.ParseJsonObject(manifestJson);
            var manifest = TransactionManifest.Parse(manifestTree);

            try
            {
                var outcome = new RestoreService().Restore(root, manifest, dryRun);
                foreach (var path in outcome.RestoredPaths)
                {
                    Console.WriteLine(dryRun ? $"Would restore: {path}" : $"Restored: {path}");
                }

                Console.WriteLine("Project files restored. Machine packages, Docker volumes and external service changes are not uninstalled.");
                return 0;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
        });

        return command;
    }
}
