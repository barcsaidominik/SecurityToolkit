using System.Runtime.InteropServices;

namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// The shared <c>docker run</c> arguments of the containerized scanners (Semgrep, Gitleaks, OSV):
/// the repository bind-mounted at <c>/src</c>, with git's safe.directory set for it.
/// </summary>
public static class DockerRunArguments
{
    /// <param name="root">Absolute repository root on the host.</param>
    /// <param name="hostUser">
    /// "uid:gid" to run the container as, or null to keep the image's own user. Defaults to
    /// <see cref="CurrentUnixUser"/>: on Linux a container's root user would leave root-owned
    /// report files in the working tree (the agent's next checkout with <c>clean: true</c> then
    /// fails), and a non-root image user may not be able to write the results directory at all.
    /// Docker Desktop (Windows/macOS) maps file ownership itself.
    /// </param>
    public static IReadOnlyList<string> Build(string root, string? hostUser = null)
    {
        var arguments = new List<string>
        {
            "--rm", "--mount", $"type=bind,source={root},target=/src", "-w", "/src",
            "-e", "GIT_CONFIG_COUNT=1", "-e", "GIT_CONFIG_KEY_0=safe.directory", "-e", "GIT_CONFIG_VALUE_0=/src",
        };

        hostUser ??= CurrentUnixUser();
        if (hostUser is not null)
        {
            // An arbitrary uid has no home directory in the image; Semgrep and git both write there.
            arguments.AddRange(new[] { "--user", hostUser, "-e", "HOME=/tmp" });
        }

        return arguments;
    }

    /// <summary>"uid:gid" of this process on Linux, or null on other platforms and for root.</summary>
    public static string? CurrentUnixUser()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var uid = GetEffectiveUserId();
        return uid == 0 ? null : $"{uid}:{GetEffectiveGroupId()}";
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "getegid")]
    private static extern uint GetEffectiveGroupId();
}
