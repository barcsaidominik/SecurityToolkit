namespace SecurityToolkit.Core.Tests;

public class EnvironmentPathTests
{
    [Fact]
    public void RefreshFromMachineAndUser_OnWindows_MergesMachineAndUserAheadOfProcessPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var reads = new Dictionary<EnvironmentVariableTarget, string?>
        {
            [EnvironmentVariableTarget.Machine] = "C:\\machine",
            [EnvironmentVariableTarget.User] = "C:\\user",
            [EnvironmentVariableTarget.Process] = "C:\\process",
        };

        string? written = null;

        EnvironmentPath.RefreshFromMachineAndUser(
            (_, target) => reads[target],
            (name, value) =>
            {
                Assert.Equal("PATH", name);
                written = value;
            });

        Assert.Equal("C:\\machine;C:\\user;C:\\process", written);
    }

    [Fact]
    public void RefreshFromMachineAndUser_OnWindows_TreatsMissingScopesAsEmpty()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string? written = null;

        EnvironmentPath.RefreshFromMachineAndUser(
            (_, _) => null,
            (_, value) => written = value);

        Assert.Equal(";;", written);
    }

    [Fact]
    public void RefreshFromMachineAndUser_OnNonWindows_IsNoOp()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var writeCalls = 0;

        EnvironmentPath.RefreshFromMachineAndUser(
            (_, _) => "irrelevant",
            (_, _) => writeCalls++);

        Assert.Equal(0, writeCalls);
    }
}
