namespace SecurityToolkit.Core;

/// <summary>
/// Locates a shell-free way to invoke npm.
/// </summary>
public static class NpmResolver
{
    /// <summary>
    /// Resolves how to invoke npm on the current machine.
    /// </summary>
    /// <exception cref="FileNotFoundException">npm (or, on Windows, node) is not on PATH, or npm-cli.js could not be located.</exception>
    public static NpmInvocation Resolve() => Resolve(EnvironmentPath.FindOnPath);

    /// <summary>
    /// Testable overload accepting a PATH-lookup function in place of the real environment PATH,
    /// so behavior can be verified without depending on the actual machine's npm/node installation.
    /// </summary>
    /// <param name="findOnPath">
    /// Given a command name ("npm" or "node"), returns its resolved executable path, or null if not found.
    /// </param>
    public static NpmInvocation Resolve(Func<string, string?> findOnPath)
    {
        var npmPath = findOnPath("npm")
            ?? throw new FileNotFoundException("Cannot locate npm on PATH.");

        if (!OperatingSystem.IsWindows())
        {
            return new NpmInvocation(npmPath, Array.Empty<string>());
        }

        var nodePath = findOnPath("node")
            ?? throw new FileNotFoundException("Cannot locate node on PATH.");

        var npmDir = Path.GetDirectoryName(npmPath)
            ?? throw new FileNotFoundException("Cannot determine npm's containing directory.");
        var nodeDir = Path.GetDirectoryName(nodePath)
            ?? throw new FileNotFoundException("Cannot determine node's containing directory.");

        var candidates = new[]
        {
            Path.Combine(npmDir, "node_modules", "npm", "bin", "npm-cli.js"),
            Path.Combine(nodeDir, "node_modules", "npm", "bin", "npm-cli.js"),
        };

        var cli = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "Cannot locate npm-cli.js next to node/npm. Use a standard Node install or configure its PATH.");

        return new NpmInvocation(nodePath, new[] { cli });
    }
}

/// <summary>
/// Thin wrapper around PATH lookup, kept separate so <see cref="NpmResolver"/> can be tested
/// with a fake resolver instead of the real machine PATH.
/// </summary>
public static class EnvironmentPath
{
    /// <summary>
    /// Finds the first executable named <paramref name="command"/> on the PATH, honoring
    /// Windows' PATHEXT. Returns null if not found.
    /// </summary>
    public static string? FindOnPath(string command)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { string.Empty };

        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Merges the Machine and User PATH environment variables into the current process' PATH.
    /// This picks up a tool that a
    /// winget install just added to the Machine/User PATH registry values but that this
    /// process' own PATH snapshot (taken at process start) doesn't yet reflect. Windows-only
    /// (Machine/User PATH scopes don't exist elsewhere); a no-op on other platforms.
    /// </summary>
    public static void RefreshFromMachineAndUser() => RefreshFromMachineAndUser(
        static (name, target) => Environment.GetEnvironmentVariable(name, target),
        static (name, value) => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process));

    /// <summary>
    /// Testable overload accepting the environment variable read/write functions in place of the
    /// real <see cref="Environment"/> API, so this can be verified without mutating the actual
    /// process-wide PATH as a side effect of running a test.
    /// </summary>
    public static void RefreshFromMachineAndUser(
        Func<string, EnvironmentVariableTarget, string?> getEnvironmentVariable,
        Action<string, string?> setProcessEnvironmentVariable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var machine = getEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? string.Empty;
        var user = getEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        var process = getEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? string.Empty;

        setProcessEnvironmentVariable("PATH", $"{machine};{user};{process}");
    }
}
