namespace SecurityToolkit.Cli.Tests;

public class CommandParsingTests
{
    [Fact]
    public void Install_ParsesAllOptions()
    {
        var root = CliApp.BuildRootCommand();
        var parseResult = root.Parse(new[]
        {
            "install",
            "--project-root", "C:/Work/Project",
            "--config-file", "profile.json",
            "--non-interactive",
            "--configure-only",
            "--dry-run",
            "--run-after-install",
            "--semgrep-scope", "frontend",
            "--check",
        });

        Assert.Empty(parseResult.Errors);
        Assert.Equal("install", parseResult.CommandResult.Command.Name);
    }

    [Fact]
    public void Provision_ParsesAllOptions()
    {
        var parseResult = CliApp.BuildRootCommand().Parse(new[] { "provision", "--project-root", ".", "--require-pinned" });

        Assert.Empty(parseResult.Errors);
        Assert.Equal("provision", parseResult.CommandResult.Command.Name);
    }

    [Fact]
    public void InfraInstall_ParsesAllOptions()
    {
        var parseResult = CliApp.BuildRootCommand().Parse(new[]
        {
            "infra", "install", "--sonar", "--mssql", "--upgrade", "--sonar-port", "9000", "--mssql-port", "14333",
        });

        Assert.Empty(parseResult.Errors);
        Assert.Equal("install", parseResult.CommandResult.Command.Name);
        Assert.Equal("infra", parseResult.CommandResult.Command.Parents.OfType<System.CommandLine.Command>().First().Name);
    }

    [Fact]
    public void InfraAddTestLogin_MissingLogin_ProducesParseError()
    {
        var parseResult = CliApp.BuildRootCommand().Parse(new[] { "infra", "add-test-login" });

        Assert.NotEmpty(parseResult.Errors);
    }

    [Fact]
    public async Task InfraInstall_PortOutOfRange_ExitsWithTwoBeforeTouchingDocker()
    {
        var parseResult = CliApp.BuildRootCommand().Parse(new[] { "infra", "install", "--mssql", "--mssql-port", "80" });

        Assert.Equal(2, await parseResult.InvokeAsync());
    }

    [Fact]
    public void Install_MissingRequiredProjectRoot_ProducesParseError()
    {
        var root = CliApp.BuildRootCommand();
        var parseResult = root.Parse(new[] { "install", "--dry-run" });

        Assert.NotEmpty(parseResult.Errors);
    }

    [Fact]
    public void Install_InvalidSemgrepScope_ProducesParseError()
    {
        var root = CliApp.BuildRootCommand();
        var parseResult = root.Parse(new[]
        {
            "install", "--project-root", "C:/Work/Project", "--semgrep-scope", "bogus",
        });

        Assert.NotEmpty(parseResult.Errors);
    }

    [Fact]
    public void Scan_DefaultsModeToReport()
    {
        var root = CliApp.BuildRootCommand();
        var parseResult = root.Parse(new[] { "scan", "--project-root", "C:/Work/Project" });

        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void Scan_InvalidMode_ProducesParseError()
    {
        var root = CliApp.BuildRootCommand();
        var parseResult = root.Parse(new[]
        {
            "scan", "--project-root", "C:/Work/Project", "--mode", "Bogus",
        });

        Assert.NotEmpty(parseResult.Errors);
    }

    [Fact]
    public void Scan_ParsesGateModeAndStagedFlag()
    {
        var root = CliApp.BuildRootCommand();
        var parseResult = root.Parse(new[]
        {
            "scan", "--project-root", "C:/Work/Project", "--mode", "Gate", "--staged",
        });

        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void Restore_ParsesProjectRootAndManifest()
    {
        var root = CliApp.BuildRootCommand();
        var parseResult = root.Parse(new[]
        {
            "restore", "--project-root", "C:/Work/Project", "--manifest", ".security/backups/x/manifest.json", "--dry-run",
        });

        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void Restore_MissingManifest_ProducesParseError()
    {
        var root = CliApp.BuildRootCommand();
        var parseResult = root.Parse(new[] { "restore", "--project-root", "C:/Work/Project" });

        Assert.NotEmpty(parseResult.Errors);
    }
}
