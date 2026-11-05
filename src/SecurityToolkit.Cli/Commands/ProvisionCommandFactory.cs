using SecurityToolkit.Core;
using SecurityToolkit.Core.Installation;
using System.CommandLine;

namespace SecurityToolkit.Cli.Commands;

/// <summary>
/// Builds the "provision" command: pulls and digest-pins the Docker images and installs the
/// pinned .NET tools recorded in .security/security.lock.json (<see cref="ToolProvisioner"/>).
/// The generated pipeline runs it with <c>--require-pinned</c> before the scan steps.
/// </summary>
public static class ProvisionCommandFactory
{
    public static Command Create()
    {
        var projectRootOption = new Option<string>("--project-root")
        {
            Description = "Repository root containing .security/.",
            Required = true,
        };
        var requirePinnedOption = new Option<bool>("--require-pinned")
        {
            Description = "Fail instead of resolving a floating image tag or an unpinned tool version (CI).",
        };

        var command = new Command("provision", "Provision the scanner tools pinned in .security/security.lock.json.")
        {
            projectRootOption, requirePinnedOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var securityDirectory = Path.Combine(Path.GetFullPath(parseResult.GetValue(projectRootOption)!), ".security");
            using var httpClient = new HttpClient();
            var provisioner = new ToolProvisioner(new ProcessRunner(), new NuGetVersionResolver(httpClient));

            ProvisionOutcome outcome;
            try
            {
                outcome = await provisioner.ProvisionAsync(
                    securityDirectory, parseResult.GetValue(requirePinnedOption), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
            {
                Console.Error.WriteLine($"{ex.Message} Run `security-toolkit install` in this repository first.");
                return 2;
            }

            foreach (var error in outcome.Errors)
            {
                Console.Error.WriteLine(error);
            }

            if (!outcome.Completed)
            {
                return 2;
            }

            Console.WriteLine("Provisioned: every enabled tool is ready.");
            return 0;
        });

        return command;
    }
}
