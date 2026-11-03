using SecurityToolkit.Core.Installation;
using SecurityToolkit.Core.Tests.Scanners;

namespace SecurityToolkit.Core.Tests.Installation;

public class VsCodeExtensionInstallerTests
{
    [Fact]
    public async Task InstallAsync_NoToolsEnabled_ReturnsInformationalMessageWithoutInvokingCode()
    {
        var processRunner = new FakeProcessRunner();
        var installer = new VsCodeExtensionInstaller(processRunner, findOnPath: _ => "/usr/bin/code");

        var outcome = await installer.InstallAsync(sonarEnabled: false, devSkimEnabled: false);

        Assert.False(outcome.CodeNotFound);
        Assert.Empty(outcome.InstalledExtensionIds);
        Assert.NotEmpty(outcome.Message);
        Assert.Empty(processRunner.Calls);
    }

    [Fact]
    public async Task InstallAsync_CodeNotOnPath_ReportsGracefullyWithoutThrowing()
    {
        var processRunner = new FakeProcessRunner();
        var installer = new VsCodeExtensionInstaller(processRunner, findOnPath: _ => null);

        var outcome = await installer.InstallAsync(sonarEnabled: true, devSkimEnabled: false);

        Assert.True(outcome.CodeNotFound);
        Assert.Empty(outcome.InstalledExtensionIds);
    }

    [Fact]
    public async Task InstallAsync_SonarAndDevSkimEnabled_InstallsBothExtensions()
    {
        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("/usr/bin/code", new ProcessResult(0, string.Empty, string.Empty));
        processRunner.Enqueue("/usr/bin/code", new ProcessResult(0, string.Empty, string.Empty));

        var installer = new VsCodeExtensionInstaller(processRunner, findOnPath: _ => "/usr/bin/code");

        var outcome = await installer.InstallAsync(sonarEnabled: true, devSkimEnabled: true);

        Assert.Equal(new[] { "SonarSource.sonarlint-vscode", "ms-cst-e.vscode-devskim" }, outcome.InstalledExtensionIds);
        Assert.Equal(2, processRunner.Calls.Count);
    }

    [Fact]
    public async Task InstallAsync_ExtensionInstallFails_Throws()
    {
        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("/usr/bin/code", new ProcessResult(1, string.Empty, "boom"));

        var installer = new VsCodeExtensionInstaller(processRunner, findOnPath: _ => "/usr/bin/code");

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(sonarEnabled: true, devSkimEnabled: false));
    }
}
