using System.Security.Cryptography;

namespace SecurityToolkit.Core.Infra;

/// <summary>What one <see cref="InfraInstaller.InstallAsync"/> run sets up.</summary>
/// <param name="Sonar">The shared SonarQube + PostgreSQL stack.</param>
/// <param name="Mssql">The shared, test-only SQL Server.</param>
/// <param name="Upgrade">Re-pulls the source image tags and re-pins their digests; otherwise existing pins are kept.</param>
/// <param name="SonarPort">Host port for Sonar; null keeps the existing (or adopted legacy, or default 9000) value.</param>
/// <param name="MssqlPort">Loopback host port for SQL Server; null keeps the existing (or default 14333) value.</param>
public sealed record InfraInstallOptions(
    bool Sonar,
    bool Mssql,
    bool Upgrade = false,
    int? SonarPort = null,
    int? MssqlPort = null);

/// <summary>Outcome of an infra command: 0 success, 2 technical/configuration failure (the CLI's exit code contract).</summary>
public sealed record InfraOutcome(int ExitCode, IReadOnlyList<string> Messages);

/// <summary>
/// Installs the machine-wide, project-independent infrastructure every target project connects to:
/// the shared SonarQube + PostgreSQL stack and the shared test SQL Server. Run once per machine
/// (by install.bat / install.sh via <c>security-toolkit infra install</c>), never by a project install. Each
/// component lives in its own folder under <see cref="ResolveInfraDirectory"/> as a toolkit-owned
/// compose.yml (rewritten on every run) plus a .env that is created once and then kept: it holds
/// the generated passwords and the digest-pinned images, so a rerun never rotates a database
/// password or silently upgrades an image (<see cref="InfraInstallOptions.Upgrade"/> does that).
/// </summary>
public sealed class InfraInstaller
{
    public const string SonarComposeProject = "security-toolkit-sonar";
    public const string MssqlComposeProject = "security-toolkit-mssql";

    internal const string SonarSourceImage = "sonarqube:community";
    internal const string PostgresSourceImage = "postgres:17";
    internal const string MssqlSourceImage = "mcr.microsoft.com/mssql/server:2022-latest";

    private const string TemplatePrefix = "SecurityToolkit.Core.Infra.Templates.";
    private static readonly char[] PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789".ToCharArray();

    private readonly IProcessRunner _processRunner;
    private readonly string _infraDirectory;
    private readonly string _legacySonarDirectory;
    private readonly Func<long?> _readMaxMapCount;

    /// <summary>Elasticsearch (inside SonarQube) refuses to start below this vm.max_map_count.</summary>
    public const long RequiredMaxMapCount = 262144;

    /// <param name="infraDirectory">Override for tests; defaults to <see cref="ResolveInfraDirectory"/>.</param>
    /// <param name="legacySonarDirectory">Override for tests; defaults to <see cref="ResolveLegacySonarDirectory"/>.</param>
    /// <param name="readMaxMapCount">Override for tests; defaults to <see cref="ReadHostMaxMapCount"/>.</param>
    public InfraInstaller(
        IProcessRunner processRunner, string? infraDirectory = null, string? legacySonarDirectory = null, Func<long?>? readMaxMapCount = null)
    {
        _processRunner = processRunner;
        _infraDirectory = infraDirectory ?? ResolveInfraDirectory();
        _legacySonarDirectory = legacySonarDirectory ?? ResolveLegacySonarDirectory();
        _readMaxMapCount = readMaxMapCount ?? ReadHostMaxMapCount;
    }

    /// <summary>
    /// The Linux host's vm.max_map_count, which Docker containers share. Null where it cannot be
    /// checked from here: on Windows/macOS Docker runs in its own VM (Docker Desktop configures it).
    /// </summary>
    public static long? ReadHostMaxMapCount()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return long.TryParse(File.ReadAllText("/proc/sys/vm/max_map_count").Trim(), out var value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// "%ProgramData%\SecurityToolkit\infra" on Windows, "/var/lib/security-toolkit/infra" elsewhere
    /// (.NET maps CommonApplicationData to /usr/share on Linux, which is for read-only data).
    /// </summary>
    public static string ResolveInfraDirectory()
        => OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SecurityToolkit", "infra")
            : "/var/lib/security-toolkit/infra";

    /// <summary>
    /// Where older versions generated the shared Sonar stack, per project install
    /// ("%LOCALAPPDATA%\SecurityToolkit\sonar"). Its .env is adopted, never deleted, by this class.
    /// </summary>
    public static string ResolveLegacySonarDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecurityToolkit", "sonar");

    public string SonarDirectory => Path.Combine(_infraDirectory, "sonar");

    public string MssqlDirectory => Path.Combine(_infraDirectory, "mssql");

    public async Task<InfraOutcome> InstallAsync(InfraInstallOptions options, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var exitCode = 0;

        if (!options.Sonar && !options.Mssql)
        {
            return new InfraOutcome(2, new[] { "Nothing selected: pass --sonar and/or --mssql." });
        }

        foreach (var (selected, name, install) in new (bool, string, Func<Task>)[]
        {
            (options.Sonar, "Sonar", () => InstallSonarAsync(options, messages, cancellationToken)),
            (options.Mssql, "MSSQL", () => InstallMssqlAsync(options, messages, cancellationToken)),
        })
        {
            if (!selected)
            {
                continue;
            }

            try
            {
                await install().ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                messages.Add($"{name}: {ex.Message}");
                exitCode = 2;
            }
            catch (UnauthorizedAccessException ex)
            {
                messages.Add($"{name}: {ex.Message} The machine-level infra needs administrator rights (Windows: an elevated terminal, Linux: sudo).");
                exitCode = 2;
            }
        }

        return new InfraOutcome(exitCode, messages);
    }

    /// <summary>
    /// Creates <paramref name="login"/> on the shared test SQL Server (or rotates its password) with
    /// CREATE ANY DATABASE — enough to create its own databases and, as their owner, drop them,
    /// but no access to another project's databases (dbcreator could drop any of them). Returns
    /// the connection string to store in that project's secret pipeline variable.
    /// </summary>
    public async Task<string> AddTestLoginAsync(string login, CancellationToken cancellationToken = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(login, "^[A-Za-z][A-Za-z0-9_]{2,63}$"))
        {
            throw new InvalidOperationException("Login name: a letter, then 2-63 letters/digits/underscores.");
        }

        var envPath = Path.Combine(MssqlDirectory, ".env");
        if (!File.Exists(envPath))
        {
            throw new InvalidOperationException("The test SQL Server is not installed; run `security-toolkit infra install --mssql` first.");
        }

        var env = ReadEnv(envPath);
        var password = GeneratePassword();

        // `exec -e NAME` without a value copies NAME from this process's environment, so the
        // password never appears on any command line.
        Environment.SetEnvironmentVariable("TestLogin", login);
        Environment.SetEnvironmentVariable("TestPassword", password);
        try
        {
            var arguments = ComposeArguments(MssqlDirectory).Concat(new[]
            {
                "exec", "-T", "-e", "TestLogin", "-e", "TestPassword", "mssql",
                "/opt/mssql-tools18/bin/sqlcmd", "-C", "-b", "-S", "localhost", "-U", "sa", "-i", "/scripts/create-test-login.sql",
            }).ToArray();
            var result = await _processRunner.RunAsync("docker", arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Creating the test login failed (exit {result.ExitCode}). Is the stack up? {result.StdErr.Trim()}");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TestLogin", null);
            Environment.SetEnvironmentVariable("TestPassword", null);
        }

        var port = env.TryGetValue("MSSQL_PORT", out var p) ? p : "14333";
        // 127.0.0.1, not localhost: the port is published on IPv4 loopback only, and SqlClient
        // resolving localhost to ::1 first gets "actively refused".
        return $"Server=127.0.0.1,{port};User Id={login};Password={password};Encrypt=True;TrustServerCertificate=True";
    }

    private async Task InstallSonarAsync(InfraInstallOptions options, List<string> messages, CancellationToken cancellationToken)
    {
        // Checked before anything is written: below the limit the container starts, then
        // Elasticsearch's bootstrap check stops Sonar, and it restarts in a loop (restart: unless-stopped).
        // A kernel setting of the whole machine, so the toolkit reports it and never changes it.
        if (_readMaxMapCount() is { } maxMapCount && maxMapCount < RequiredMaxMapCount)
        {
            throw new InvalidOperationException(
                $"vm.max_map_count = {maxMapCount}, but SonarQube (Elasticsearch) needs at least {RequiredMaxMapCount}, otherwise it does not start. " +
                $"Set it with: sudo sysctl -w vm.max_map_count={RequiredMaxMapCount}, permanently: " +
                $"echo 'vm.max_map_count={RequiredMaxMapCount}' | sudo tee /etc/sysctl.d/99-security-toolkit-sonar.conf. " +
                "Then run the infra installation again.");
        }

        Directory.CreateDirectory(SonarDirectory);
        WriteTemplate("sonar.compose.yml", Path.Combine(SonarDirectory, "compose.yml"));

        var envPath = Path.Combine(SonarDirectory, ".env");
        var isNew = !File.Exists(envPath);
        Dictionary<string, string> env;

        if (!isNew)
        {
            env = ReadEnv(envPath);
        }
        else if (File.Exists(Path.Combine(_legacySonarDirectory, ".env")))
        {
            // The existing PostgreSQL volume was initialised with the legacy password; a fresh one
            // would lock Sonar out of its own data. Adopt everything, including the image pins, so
            // the move never doubles as an unplanned Sonar version upgrade.
            env = ReadEnv(Path.Combine(_legacySonarDirectory, ".env"));
            messages.Add(
                $"Sonar: the earlier settings (DB password, port, images) were taken over from {_legacySonarDirectory}. " +
                "Once Sonar runs fine from the new location, that folder can be deleted.");
        }
        else if (await VolumeExistsAsync($"{SonarComposeProject}_postgres_data", cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The {SonarComposeProject}_postgres_data volume already exists, but its .env was not found " +
                $"(neither at {envPath} nor at the old location {_legacySonarDirectory}). With a new password Sonar could not reach " +
                "its existing database: copy the old .env here (SONAR_DB_PASSWORD), or if the data can be discarded, " +
                $"delete the volume (docker volume rm {SonarComposeProject}_postgres_data).");
        }
        else
        {
            env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SONAR_DB_PASSWORD"] = GeneratePassword(),
            };
        }

        env.TryAdd("SONAR_PORT", "9000");
        env.TryAdd("SONAR_MEM_LIMIT", "4g");
        if (options.SonarPort is { } sonarPort)
        {
            env["SONAR_PORT"] = sonarPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await PinAsync(env, "SONAR_IMAGE", SonarSourceImage, options.Upgrade, cancellationToken).ConfigureAwait(false);
        await PinAsync(env, "POSTGRES_IMAGE", PostgresSourceImage, options.Upgrade, cancellationToken).ConfigureAwait(false);

        await WriteEnvAsync(envPath, env, restrict: isNew, cancellationToken).ConfigureAwait(false);
        await UpAsync(SonarDirectory, cancellationToken).ConfigureAwait(false);
        messages.Add($"Sonar: running (http://localhost:{env["SONAR_PORT"]}), files: {SonarDirectory}");
    }

    private async Task InstallMssqlAsync(InfraInstallOptions options, List<string> messages, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(MssqlDirectory, "sql"));
        WriteTemplate("mssql.compose.yml", Path.Combine(MssqlDirectory, "compose.yml"));
        WriteTemplate("create-test-login.sql", Path.Combine(MssqlDirectory, "sql", "create-test-login.sql"));

        var envPath = Path.Combine(MssqlDirectory, ".env");
        var isNew = !File.Exists(envPath);
        var env = isNew
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["MSSQL_SA_PASSWORD"] = GeneratePassword() }
            : ReadEnv(envPath);

        env.TryAdd("MSSQL_PORT", "14333");
        env.TryAdd("MSSQL_MEMORY_LIMIT_MB", "2048");
        if (options.MssqlPort is { } mssqlPort)
        {
            env["MSSQL_PORT"] = mssqlPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await PinAsync(env, "MSSQL_IMAGE", MssqlSourceImage, options.Upgrade, cancellationToken).ConfigureAwait(false);

        await WriteEnvAsync(envPath, env, restrict: isNew, cancellationToken).ConfigureAwait(false);
        await UpAsync(MssqlDirectory, cancellationToken).ConfigureAwait(false);
        messages.Add(
            $"MSSQL: running (127.0.0.1,{env["MSSQL_PORT"]}), files: {MssqlDirectory}. " +
            "Per-project login: security-toolkit infra add-test-login <name>");
    }

    /// <summary>Keeps an existing digest pin unless upgrading; otherwise pulls the source tag and pins its digest.</summary>
    private async Task PinAsync(
        Dictionary<string, string> env, string key, string sourceImage, bool upgrade, CancellationToken cancellationToken)
    {
        if (!upgrade && env.TryGetValue(key, out var existing) && IsDigestPinned(existing))
        {
            return;
        }

        var pull = await _processRunner.RunAsync("docker", new[] { "pull", sourceImage }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (pull.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker pull {sourceImage} failed: {pull.StdErr.Trim()}");
        }

        var inspect = await _processRunner.RunAsync(
            "docker", new[] { "image", "inspect", sourceImage, "--format", "{{index .RepoDigests 0}}" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var digest = inspect.StdOut.Trim();
        if (inspect.ExitCode != 0 || !IsDigestPinned(digest))
        {
            throw new InvalidOperationException($"Cannot resolve the image digest of {sourceImage}.");
        }

        env[key] = digest;
    }

    private static bool IsDigestPinned(string image)
        => System.Text.RegularExpressions.Regex.IsMatch(image, "@sha256:[a-f0-9]{64}$");

    private async Task<bool> VolumeExistsAsync(string volume, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync("docker", new[] { "volume", "inspect", volume }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private async Task UpAsync(string directory, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            "docker", ComposeArguments(directory).Concat(new[] { "up", "-d", "--wait" }).ToArray(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker compose up failed (exit {result.ExitCode}); see docker compose logs in {directory}. {result.StdErr.Trim()}");
        }
    }

    private static string[] ComposeArguments(string directory)
        => new[] { "compose", "-f", Path.Combine(directory, "compose.yml"), "--env-file", Path.Combine(directory, ".env") };

    private async Task WriteEnvAsync(string path, Dictionary<string, string> env, bool restrict, CancellationToken cancellationToken)
    {
        var content = string.Concat(env.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={e.Value}\n"));
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        if (restrict)
        {
            await RestrictPermissionsAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The .env holds database passwords: readable by its creator, SYSTEM and Administrators only
    /// (Windows), or by its owner only (chmod 600; root, as the infra install runs under sudo).
    /// </summary>
    private async Task RestrictPermissionsAsync(string path, CancellationToken cancellationToken)
    {
        ProcessResult result;
        if (OperatingSystem.IsWindows())
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            result = await _processRunner.RunAsync(
                "icacls",
                new[] { path, "/inheritance:r", "/grant:r", $"{identity}:(F)", "*S-1-5-18:(F)", "*S-1-5-32-544:(F)" },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await _processRunner.RunAsync("chmod", new[] { "600", path }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not restrict .env permissions: {path}");
        }
    }

    /// <summary>Parses a compose .env file (KEY=VALUE lines; blanks and # comments skipped).</summary>
    public static Dictionary<string, string> ReadEnv(string path)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf('=');
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || separator <= 0)
            {
                continue;
            }

            env[trimmed[..separator]] = trimmed[(separator + 1)..];
        }

        return env;
    }

    /// <summary>36 chars, no quotes/semicolons (connection-string and T-SQL safe), every SQL Server complexity class.</summary>
    internal static string GeneratePassword()
        => new string(Enumerable.Range(0, 32).Select(_ => PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)]).ToArray())
           + "a9Z!";

    private static void WriteTemplate(string name, string destination)
    {
        var assembly = typeof(InfraInstaller).Assembly;
        using var stream = assembly.GetManifestResourceStream(TemplatePrefix + name)
            ?? throw new InvalidOperationException($"Missing embedded infra template: {name}");
        using var reader = new StreamReader(stream);
        File.WriteAllText(destination, reader.ReadToEnd().Replace("\r\n", "\n"));
    }
}
