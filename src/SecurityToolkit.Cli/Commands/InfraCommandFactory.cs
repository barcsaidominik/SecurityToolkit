using SecurityToolkit.Core;
using SecurityToolkit.Core.Infra;
using System.CommandLine;

namespace SecurityToolkit.Cli.Commands;

/// <summary>
/// Builds the "infra" command group: the machine-wide, project-independent infrastructure
/// (shared Sonar + PostgreSQL, shared test SQL Server) that install.bat / install.sh sets up once per machine
/// and every project install then only connects to.
/// </summary>
public static class InfraCommandFactory
{
    public static Command Create()
    {
        var command = new Command("infra", "Machine-wide shared infrastructure (Sonar + PostgreSQL, test SQL Server).")
        {
            CreateInstall(),
            CreateAddTestLogin(),
        };

        return command;
    }

    private static Command CreateInstall()
    {
        var sonarOption = new Option<bool>("--sonar") { Description = "Install/start the shared SonarQube + PostgreSQL stack." };
        var mssqlOption = new Option<bool>("--mssql") { Description = "Install/start the shared, test-only SQL Server." };
        var upgradeOption = new Option<bool>("--upgrade")
        {
            Description = "Re-pull the source image tags and re-pin their digests (default: keep the existing pins).",
        };
        var sonarPortOption = new Option<int?>("--sonar-port") { Description = "Sonar host port (default: keep existing, else 9000)." };
        var mssqlPortOption = new Option<int?>("--mssql-port")
        {
            Description = "SQL Server loopback port (default: keep existing, else 14333).",
        };

        var command = new Command(
            "install",
            $"Install or update the selected components under {InfraInstaller.ResolveInfraDirectory()}. " +
            "Idempotent: existing passwords and image pins are kept.")
        {
            sonarOption, mssqlOption, upgradeOption, sonarPortOption, mssqlPortOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            foreach (var port in new[] { parseResult.GetValue(sonarPortOption), parseResult.GetValue(mssqlPortOption) })
            {
                if (port is < 1024 or > 65535)
                {
                    Console.Error.WriteLine("Ports must be 1024..65535.");
                    return 2;
                }
            }

            var outcome = await new InfraInstaller(new ProcessRunner()).InstallAsync(
                new InfraInstallOptions(
                    Sonar: parseResult.GetValue(sonarOption),
                    Mssql: parseResult.GetValue(mssqlOption),
                    Upgrade: parseResult.GetValue(upgradeOption),
                    SonarPort: parseResult.GetValue(sonarPortOption),
                    MssqlPort: parseResult.GetValue(mssqlPortOption)),
                cancellationToken).ConfigureAwait(false);

            foreach (var message in outcome.Messages)
            {
                (outcome.ExitCode == 0 ? Console.Out : Console.Error).WriteLine(message);
            }

            return outcome.ExitCode;
        });

        return command;
    }

    private static Command CreateAddTestLogin()
    {
        var loginArgument = new Argument<string>("login")
        {
            Description = "Login name for one project (e.g. my_project_test); rerunning rotates its password.",
        };

        var command = new Command(
            "add-test-login",
            "Create (or rotate the password of) one project's login on the shared test SQL Server and print its connection string.")
        {
            loginArgument,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var connectionString = await new InfraInstaller(new ProcessRunner())
                    .AddTestLoginAsync(parseResult.GetValue(loginArgument)!, cancellationToken).ConfigureAwait(false);

                Console.WriteLine("Store this as a SECRET pipeline variable of the project (e.g. TestDbConnectionString):");
                Console.WriteLine(connectionString);
                Console.WriteLine("Rerunning this command for the same login rotates the password; update the variable then.");
                return 0;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"{ex.Message} The infra .env is readable by administrators only (Linux: run with sudo).");
                return 2;
            }
        });

        return command;
    }
}
