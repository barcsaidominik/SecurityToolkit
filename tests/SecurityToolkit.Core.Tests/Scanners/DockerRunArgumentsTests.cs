using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Core.Tests.Scanners;

public class DockerRunArgumentsTests
{
    [Fact]
    public void Build_MountsRootAtSrcWithSafeDirectory()
    {
        var arguments = DockerRunArguments.Build("/repo", hostUser: null);

        Assert.Contains("type=bind,source=/repo,target=/src", arguments);
        Assert.Contains("GIT_CONFIG_VALUE_0=/src", arguments);
    }

    [Fact]
    public void Build_WithHostUser_RunsAsThatUserWithWritableHome()
    {
        var arguments = DockerRunArguments.Build("/repo", hostUser: "1000:1000").ToList();

        var userIndex = arguments.IndexOf("--user");
        Assert.True(userIndex >= 0);
        Assert.Equal("1000:1000", arguments[userIndex + 1]);
        Assert.Contains("HOME=/tmp", arguments);
    }

    [Fact]
    public void CurrentUnixUser_IsNullOutsideLinux_ElseUidGid()
    {
        var user = DockerRunArguments.CurrentUnixUser();

        if (!OperatingSystem.IsLinux())
        {
            Assert.Null(user);
        }
        else if (user is not null)
        {
            Assert.Matches("^[0-9]+:[0-9]+$", user);
        }
    }
}
