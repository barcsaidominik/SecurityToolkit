using SecurityToolkit.Core.Infra;
using SecurityToolkit.Core.Tests.Scanners;

namespace SecurityToolkit.Core.Tests.Infra;

public class InfraInstallerTests : IDisposable
{
    private const string SonarDigest = "sonarqube@sha256:" + "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
    private const string PostgresDigest = "postgres@sha256:" + "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
    private const string MssqlDigest = "mcr.microsoft.com/mssql/server@sha256:" + "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";

    private readonly string _infra;
    private readonly string _legacy;

    public InfraInstallerTests()
    {
        _infra = Directory.CreateTempSubdirectory("infra-tests-").FullName;
        _legacy = Path.Combine(Directory.CreateTempSubdirectory("infra-legacy-tests-").FullName, "sonar");
    }

    public void Dispose()
    {
        Directory.Delete(_infra, recursive: true);
        Directory.Delete(Path.GetDirectoryName(_legacy)!, recursive: true);
    }

    // The host check reads /proc on Linux; tests pin it so the result never depends on the machine.
    private InfraInstaller Create(FakeProcessRunner runner, long? maxMapCount = null)
        => new(runner, _infra, _legacy, () => maxMapCount);

    [Fact]
    public async Task InstallAsync_SonarWithTooLowMaxMapCount_RefusesBeforeWritingAnything()
    {
        var runner = new FakeProcessRunner();

        var outcome = await Create(runner, maxMapCount: 65530).InstallAsync(new InfraInstallOptions(Sonar: true, Mssql: false));

        Assert.Equal(2, outcome.ExitCode);
        var message = Assert.Single(outcome.Messages);
        Assert.Contains("vm.max_map_count = 65530", message, StringComparison.Ordinal);
        Assert.Contains("sudo sysctl -w vm.max_map_count=262144", message, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
        Assert.False(Directory.Exists(Path.Combine(_infra, "sonar")));
    }

    [Theory]
    [InlineData(262144L)]
    [InlineData(1048576L)]
    public async Task InstallAsync_SonarWithSufficientMaxMapCount_Starts(long maxMapCount)
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue("docker", new ProcessResult(1, "", "no such volume")); // volume inspect
        EnqueuePin(runner, SonarDigest);
        EnqueuePin(runner, PostgresDigest);
        EnqueueRestrict(runner);
        runner.Enqueue("docker", new ProcessResult(0, "", "")); // compose up

        var outcome = await Create(runner, maxMapCount).InstallAsync(new InfraInstallOptions(Sonar: true, Mssql: false));

        Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public async Task InstallAsync_MssqlOnly_IgnoresMaxMapCount()
    {
        var runner = new FakeProcessRunner();
        EnqueuePin(runner, MssqlDigest);
        EnqueueRestrict(runner);
        runner.Enqueue("docker", new ProcessResult(0, "", "")); // compose up

        var outcome = await Create(runner, maxMapCount: 65530).InstallAsync(new InfraInstallOptions(Sonar: false, Mssql: true));

        Assert.Equal(0, outcome.ExitCode);
    }

    private static void EnqueuePin(FakeProcessRunner runner, string digest)
    {
        runner.Enqueue("docker", new ProcessResult(0, "", "")); // pull
        runner.Enqueue("docker", new ProcessResult(0, digest + "\n", "")); // image inspect
    }

    private static void EnqueueRestrict(FakeProcessRunner runner)
        => runner.Enqueue(OperatingSystem.IsWindows() ? "icacls" : "chmod", new ProcessResult(0, "", ""));

    [Fact]
    public async Task InstallAsync_FreshSonar_PinsImagesGeneratesPasswordRestrictsEnvAndStartsStack()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue("docker", new ProcessResult(1, "", "no such volume")); // volume inspect
        EnqueuePin(runner, SonarDigest);
        EnqueuePin(runner, PostgresDigest);
        EnqueueRestrict(runner);
        runner.Enqueue("docker", new ProcessResult(0, "", "")); // compose up

        var outcome = await Create(runner).InstallAsync(new InfraInstallOptions(Sonar: true, Mssql: false));

        Assert.Equal(0, outcome.ExitCode);
        var directory = Path.Combine(_infra, "sonar");
        Assert.Contains("name: security-toolkit-sonar", File.ReadAllText(Path.Combine(directory, "compose.yml")));
        var env = InfraInstaller.ReadEnv(Path.Combine(directory, ".env"));
        Assert.Equal(SonarDigest, env["SONAR_IMAGE"]);
        Assert.Equal(PostgresDigest, env["POSTGRES_IMAGE"]);
        Assert.Equal("9000", env["SONAR_PORT"]);
        Assert.Equal(36, env["SONAR_DB_PASSWORD"].Length);

        Assert.Equal(new[] { "volume", "inspect", "security-toolkit-sonar_postgres_data" }, runner.Calls[0].Arguments);
        Assert.Equal(new[] { "pull", "sonarqube:community" }, runner.Calls[1].Arguments);
        var up = runner.Calls[^1].Arguments;
        Assert.Equal(new[] { "up", "-d", "--wait" }, up[^3..]);
        Assert.Contains(Path.Combine(directory, ".env"), up);
    }

    [Fact]
    public async Task InstallAsync_RerunWithExistingEnv_KeepsPasswordAndPinsAndOnlyStartsStack()
    {
        var directory = Path.Combine(_infra, "sonar");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, ".env"),
            $"SONAR_DB_PASSWORD=keep-me\nSONAR_IMAGE={SonarDigest}\nPOSTGRES_IMAGE={PostgresDigest}\nSONAR_PORT=9100\n");

        var runner = new FakeProcessRunner();
        runner.Enqueue("docker", new ProcessResult(0, "", "")); // compose up only

        var outcome = await Create(runner).InstallAsync(new InfraInstallOptions(Sonar: true, Mssql: false));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Single(runner.Calls);
        var env = InfraInstaller.ReadEnv(Path.Combine(directory, ".env"));
        Assert.Equal("keep-me", env["SONAR_DB_PASSWORD"]);
        Assert.Equal("9100", env["SONAR_PORT"]);
        Assert.Equal(SonarDigest, env["SONAR_IMAGE"]);
    }

    [Fact]
    public async Task InstallAsync_Upgrade_RepinsImagesButKeepsPassword()
    {
        var directory = Path.Combine(_infra, "sonar");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, ".env"),
            $"SONAR_DB_PASSWORD=keep-me\nSONAR_IMAGE=sonarqube@sha256:{new string('0', 64)}\nPOSTGRES_IMAGE={PostgresDigest}\n");

        var runner = new FakeProcessRunner();
        EnqueuePin(runner, SonarDigest);
        EnqueuePin(runner, PostgresDigest);
        runner.Enqueue("docker", new ProcessResult(0, "", ""));

        var outcome = await Create(runner).InstallAsync(new InfraInstallOptions(Sonar: true, Mssql: false, Upgrade: true, SonarPort: 9200));

        Assert.Equal(0, outcome.ExitCode);
        var env = InfraInstaller.ReadEnv(Path.Combine(directory, ".env"));
        Assert.Equal(SonarDigest, env["SONAR_IMAGE"]);
        Assert.Equal("keep-me", env["SONAR_DB_PASSWORD"]);
        Assert.Equal("9200", env["SONAR_PORT"]);
    }

    [Fact]
    public async Task InstallAsync_LegacyProjectInstalledSonarEnv_IsAdoptedWithoutRepulling()
    {
        Directory.CreateDirectory(_legacy);
        File.WriteAllText(
            Path.Combine(_legacy, ".env"),
            $"SONAR_IMAGE={SonarDigest}\nPOSTGRES_IMAGE={PostgresDigest}\nSONAR_DB_PASSWORD=legacy+pw/==\nSONAR_PORT=9000\n");

        var runner = new FakeProcessRunner();
        EnqueueRestrict(runner);
        runner.Enqueue("docker", new ProcessResult(0, "", ""));

        var outcome = await Create(runner).InstallAsync(new InfraInstallOptions(Sonar: true, Mssql: false));

        Assert.Equal(0, outcome.ExitCode);
        var env = InfraInstaller.ReadEnv(Path.Combine(_infra, "sonar", ".env"));
        Assert.Equal("legacy+pw/==", env["SONAR_DB_PASSWORD"]);
        Assert.Equal(SonarDigest, env["SONAR_IMAGE"]);
        Assert.Contains(outcome.Messages, m => m.Contains("were taken over from", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_legacy, ".env"))); // never deleted
    }

    [Fact]
    public async Task InstallAsync_ExistingDataVolumeWithoutAnyEnv_RefusesInsteadOfLockingSonarOut()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue("docker", new ProcessResult(0, "[{}]", "")); // volume inspect: exists

        var outcome = await Create(runner).InstallAsync(new InfraInstallOptions(Sonar: true, Mssql: false));

        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Messages, m => m.Contains("postgres_data", StringComparison.Ordinal));
        Assert.Single(runner.Calls);
        Assert.False(File.Exists(Path.Combine(_infra, "sonar", ".env")));
    }

    [Fact]
    public async Task InstallAsync_FreshMssql_WritesComposeAndLoginScriptAndStarts()
    {
        var runner = new FakeProcessRunner();
        EnqueuePin(runner, MssqlDigest);
        EnqueueRestrict(runner);
        runner.Enqueue("docker", new ProcessResult(0, "", ""));

        var outcome = await Create(runner).InstallAsync(new InfraInstallOptions(Sonar: false, Mssql: true, MssqlPort: 15000));

        Assert.Equal(0, outcome.ExitCode);
        var directory = Path.Combine(_infra, "mssql");
        Assert.Contains("name: security-toolkit-mssql", File.ReadAllText(Path.Combine(directory, "compose.yml")));
        Assert.Contains("GRANT CREATE ANY DATABASE", File.ReadAllText(Path.Combine(directory, "sql", "create-test-login.sql")));
        var env = InfraInstaller.ReadEnv(Path.Combine(directory, ".env"));
        Assert.Equal(MssqlDigest, env["MSSQL_IMAGE"]);
        Assert.Equal("15000", env["MSSQL_PORT"]);
        Assert.EndsWith("a9Z!", env["MSSQL_SA_PASSWORD"]);
    }

    [Fact]
    public async Task InstallAsync_NothingSelected_ReturnsExitCode2()
    {
        var outcome = await Create(new FakeProcessRunner()).InstallAsync(new InfraInstallOptions(Sonar: false, Mssql: false));

        Assert.Equal(2, outcome.ExitCode);
    }

    [Fact]
    public async Task AddTestLoginAsync_PassesCredentialsOnlyViaEnvironmentAndReturnsConnectionString()
    {
        var directory = Path.Combine(_infra, "mssql");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ".env"), "MSSQL_SA_PASSWORD=x\nMSSQL_PORT=14444\n");

        string? passwordSeenByProcess = null;
        var runner = new FakeProcessRunner();
        runner.Enqueue("docker", call =>
        {
            passwordSeenByProcess = Environment.GetEnvironmentVariable("TestPassword");
            Assert.Equal("proj_test", Environment.GetEnvironmentVariable("TestLogin"));
            return new ProcessResult(0, "Test login ready: proj_test", "");
        });

        var connectionString = await Create(runner).AddTestLoginAsync("proj_test");

        Assert.StartsWith("Server=127.0.0.1,14444;User Id=proj_test;Password=", connectionString);
        Assert.Contains($"Password={passwordSeenByProcess};", connectionString);
        Assert.DoesNotContain(runner.Calls[0].Arguments, a => a.Contains(passwordSeenByProcess!, StringComparison.Ordinal));
        Assert.Contains("TestPassword", runner.Calls[0].Arguments);
        Assert.Null(Environment.GetEnvironmentVariable("TestPassword"));
    }

    [Theory]
    [InlineData("1abc")]
    [InlineData("ab")]
    [InlineData("bad-name")]
    [InlineData("x]; DROP LOGIN sa; --")]
    public async Task AddTestLoginAsync_InvalidLoginName_Throws(string login)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(new FakeProcessRunner()).AddTestLoginAsync(login));
    }

    [Fact]
    public async Task AddTestLoginAsync_MssqlNotInstalled_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(new FakeProcessRunner()).AddTestLoginAsync("proj_test"));
        Assert.Contains("infra install --mssql", ex.Message);
    }
}
