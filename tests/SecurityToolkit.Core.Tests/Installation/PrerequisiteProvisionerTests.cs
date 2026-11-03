using SecurityToolkit.Core.Installation;
using SecurityToolkit.Core.Tests.Scanners;

namespace SecurityToolkit.Core.Tests.Installation;

public class PrerequisiteProvisionerTests
{
    private static Dictionary<string, object?> Tools(bool npm = false, bool analyzers = false, bool semgrep = false)
        => new()
        {
            ["analyzers"] = analyzers,
            ["semgrep"] = semgrep,
            ["gitleaks"] = false,
            ["nuget"] = false,
            ["npm"] = npm,
            ["osv"] = false,
            ["devskim"] = false,
            ["sonar"] = false,
        };

    [Fact]
    public async Task EnsureAsync_AllToolsAvailable_DoesNotInvokeWinget()
    {
        var processRunner = new FakeProcessRunner();
        var provisioner = new PrerequisiteProvisioner(processRunner, findOnPath: _ => "/usr/bin/tool");

        var outcome = await provisioner.EnsureAsync(
            Tools(npm: true), installPrerequisites: true, sdkChannel: "10.0",
            nodeWingetId: "OpenJS.NodeJS.LTS", nodeVersion: string.Empty);

        Assert.True(outcome.Completed);
        Assert.Empty(outcome.InstalledCommands);
        Assert.Empty(outcome.Messages);
        Assert.Empty(processRunner.Calls);
    }

    [Fact]
    public async Task EnsureAsync_MissingToolAndInstallEnabledOnWindows_InvokesWingetWithCorrectId()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("C:\\winget.exe", call =>
        {
            Assert.Equal(
                new[] { "install", "--id", "Git.Git", "--exact", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements" },
                call.Arguments);
            return new ProcessResult(0, string.Empty, string.Empty);
        });

        var found = new Dictionary<string, string?> { ["winget"] = "C:\\winget.exe", ["git"] = null };
        var callCount = 0;
        var provisioner = new PrerequisiteProvisioner(processRunner, findOnPath: command =>
        {
            if (command == "git")
            {
                callCount++;
                // Not found before install, found afterwards (second lookup, post-winget).
                return callCount > 1 ? "C:\\Program Files\\Git\\bin\\git.exe" : null;
            }

            return found.TryGetValue(command, out var value) ? value : null;
        });

        var outcome = await provisioner.EnsureAsync(
            Tools(), installPrerequisites: true, sdkChannel: "10.0",
            nodeWingetId: "OpenJS.NodeJS.LTS", nodeVersion: string.Empty);

        Assert.True(outcome.Completed);
        Assert.Equal(new[] { "git" }, outcome.InstalledCommands);
        Assert.Empty(outcome.Messages);
        Assert.Single(processRunner.Calls);
    }

    [Fact]
    public async Task EnsureAsync_MissingToolAndInstallDisabled_DoesNotInvokeWingetAndReportsMessage()
    {
        var processRunner = new FakeProcessRunner();
        var provisioner = new PrerequisiteProvisioner(processRunner, findOnPath: _ => null);

        var outcome = await provisioner.EnsureAsync(
            Tools(), installPrerequisites: false, sdkChannel: "10.0",
            nodeWingetId: "OpenJS.NodeJS.LTS", nodeVersion: string.Empty);

        Assert.False(outcome.Completed);
        Assert.Empty(outcome.InstalledCommands);
        Assert.Contains(outcome.Messages, m => m.Contains("git", StringComparison.Ordinal));
        Assert.Empty(processRunner.Calls);
    }

    [Fact]
    public async Task EnsureAsync_MissingToolOnNonWindows_NeverInvokesWingetRegardlessOfInstallFlag()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var processRunner = new FakeProcessRunner();
        var provisioner = new PrerequisiteProvisioner(processRunner, findOnPath: _ => null);

        var outcome = await provisioner.EnsureAsync(
            Tools(), installPrerequisites: true, sdkChannel: "10.0",
            nodeWingetId: "OpenJS.NodeJS.LTS", nodeVersion: string.Empty);

        Assert.False(outcome.Completed);
        Assert.Empty(outcome.InstalledCommands);
        Assert.Contains(outcome.Messages, m => m.Contains("Windows-only", StringComparison.Ordinal));
        Assert.Empty(processRunner.Calls);
    }

    [Fact]
    public async Task EnsureAsync_NpmToolEnabled_UsesConfiguredNodeWingetIdAndVersion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("C:\\winget.exe", call =>
        {
            Assert.Equal(
                new[] { "install", "--id", "OpenJS.NodeJS.LTS", "--exact", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements", "--version", "20.0.0" },
                call.Arguments);
            return new ProcessResult(0, string.Empty, string.Empty);
        });

        var nodeLookups = 0;
        var provisioner = new PrerequisiteProvisioner(processRunner, findOnPath: command =>
        {
            if (command == "winget")
            {
                return "C:\\winget.exe";
            }

            if (command == "git")
            {
                return "C:\\git.exe";
            }

            if (command == "node")
            {
                nodeLookups++;
                // Not found before install, found afterwards (second lookup, post-winget).
                return nodeLookups > 1 ? "C:\\node.exe" : null;
            }

            return null;
        });

        var outcome = await provisioner.EnsureAsync(
            Tools(npm: true), installPrerequisites: true, sdkChannel: "10.0",
            nodeWingetId: "OpenJS.NodeJS.LTS", nodeVersion: "20.0.0");

        Assert.Single(processRunner.Calls);
        Assert.Equal(new[] { "node" }, outcome.InstalledCommands);
        Assert.True(outcome.Completed);
    }

    [Fact]
    public async Task EnsureAsync_SuccessfulWingetInstall_RefreshesPathBeforeRecheckingAvailability()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("C:\\winget.exe", _ => new ProcessResult(0, string.Empty, string.Empty));

        var refreshed = false;
        var gitLookups = 0;
        var provisioner = new PrerequisiteProvisioner(
            processRunner,
            findOnPath: command =>
            {
                if (command == "winget")
                {
                    return "C:\\winget.exe";
                }

                if (command != "git")
                {
                    return null;
                }

                gitLookups++;
                if (gitLookups == 1)
                {
                    // Not found pre-install.
                    return null;
                }

                // Second lookup (post-winget, post-refresh): only "available" once the
                // refresh has actually happened first — this is what proves ordering.
                return refreshed ? "C:\\Program Files\\Git\\bin\\git.exe" : null;
            },
            refreshPath: () => refreshed = true);

        var outcome = await provisioner.EnsureAsync(
            Tools(), installPrerequisites: true, sdkChannel: "10.0",
            nodeWingetId: "OpenJS.NodeJS.LTS", nodeVersion: string.Empty);

        Assert.True(refreshed);
        Assert.Equal(2, gitLookups);
        Assert.Equal(new[] { "git" }, outcome.InstalledCommands);
        Assert.True(outcome.Completed);
        Assert.Empty(outcome.Messages);
    }

    [Fact]
    public async Task EnsureAsync_FailedWingetInstall_DoesNotRefreshPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var processRunner = new FakeProcessRunner();
        processRunner.Enqueue("C:\\winget.exe", _ => new ProcessResult(1, string.Empty, "boom"));

        var refreshCalls = 0;
        var provisioner = new PrerequisiteProvisioner(
            processRunner,
            findOnPath: command => command == "winget" ? "C:\\winget.exe" : null,
            refreshPath: () => refreshCalls++);

        var outcome = await provisioner.EnsureAsync(
            Tools(), installPrerequisites: true, sdkChannel: "10.0",
            nodeWingetId: "OpenJS.NodeJS.LTS", nodeVersion: string.Empty);

        Assert.Equal(0, refreshCalls);
        Assert.False(outcome.Completed);
        Assert.Empty(outcome.InstalledCommands);
    }
}
