using SecurityToolkit.Core.Installation;
using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Cli.Tests;

[Collection("Console")]
public class InteractiveConfigBuilderTests : IDisposable
{
    private readonly string _root;

    public InteractiveConfigBuilderTests()
    {
        _root = Directory.CreateTempSubdirectory("interactive-config-builder-tests-").FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Build_NonInteractivePrompts_ReturnsConfigMatchingDefaults()
    {
        var (scan, config) = SecurityToolkitInstaller.ResolveConfig(_root, configProfile: null, SemgrepInstallScope.Unspecified);
        var prompts = new NonInteractivePrompts();

        var result = InteractiveConfigBuilder.Build(prompts, scan, config);

        var tools = ScannerSupport.GetDictionary(result, "tools")!;
        Assert.False(ScannerSupport.GetBool(tools, "sonar"));
        Assert.False(result.ContainsKey("startSonarServer"));
        Assert.False(ScannerSupport.GetBool(result, "installHook"));
        Assert.Equal("repository", ScannerSupport.GetString(result, "semgrepScope"));
    }

    [Fact]
    public void Build_SonarToolEnabledWithNoDotnetTarget_Throws()
    {
        var (scan, config) = SecurityToolkitInstaller.ResolveConfig(_root, configProfile: null, SemgrepInstallScope.Unspecified);
        var tools = ScannerSupport.GetDictionary(config, "tools")!;
        tools["sonar"] = true;
        var prompts = new NonInteractivePrompts();

        var ex = Assert.Throws<InvalidOperationException>(() => InteractiveConfigBuilder.Build(prompts, scan, config));
        Assert.Contains("Sonar upload requires a .NET target", ex.Message);
    }

    [Fact]
    public void Build_SonarToolEnabledWithDotnetTarget_SetsDotnetTargetFromExisting()
    {
        var (scan, config) = SecurityToolkitInstaller.ResolveConfig(_root, configProfile: null, SemgrepInstallScope.Unspecified);
        var tools = ScannerSupport.GetDictionary(config, "tools")!;
        tools["sonar"] = true;
        config["dotnetTargets"] = new List<object?> { "src/App.sln" };
        var sonar = ScannerSupport.GetDictionary(config, "sonar")!;
        sonar["dotnetTarget"] = "src/App.sln";
        var prompts = new NonInteractivePrompts();

        var result = InteractiveConfigBuilder.Build(prompts, scan, config);

        var resultSonar = ScannerSupport.GetDictionary(result, "sonar")!;
        Assert.Equal("src/App.sln", ScannerSupport.GetString(resultSonar, "dotnetTarget"));
    }

    [Fact]
    public void Build_SonarCoverageAlreadyConfigured_KeepsFilterAndTestEnvironmentByDefault()
    {
        var (scan, config) = SecurityToolkitInstaller.ResolveConfig(_root, configProfile: null, SemgrepInstallScope.Unspecified);
        var tools = ScannerSupport.GetDictionary(config, "tools")!;
        tools["sonar"] = true;
        config["dotnetTargets"] = new List<object?> { "src/App.sln" };
        var sonar = ScannerSupport.GetDictionary(config, "sonar")!;
        sonar["dotnetTarget"] = "src/App.sln";
        sonar["coverage"] = true;
        sonar["testFilter"] = "Category!=Slow";
        sonar["testEnvironment"] = new Dictionary<string, object?> { ["ConnectionStrings__App"] = "AppTestDb" };

        var result = InteractiveConfigBuilder.Build(new NonInteractivePrompts(), scan, config);

        var resultSonar = ScannerSupport.GetDictionary(result, "sonar")!;
        Assert.True(ScannerSupport.GetBool(resultSonar, "coverage"));
        Assert.Equal("Category!=Slow", ScannerSupport.GetString(resultSonar, "testFilter"));
        var environment = ScannerSupport.GetDictionary(resultSonar, "testEnvironment")!;
        Assert.Equal("AppTestDb", Assert.Single(environment).Value);
    }

    [Fact]
    public void Build_FrontendSemgrepScopeWithoutFrontendTargets_Throws()
    {
        var (scan, config) = SecurityToolkitInstaller.ResolveConfig(_root, configProfile: null, SemgrepInstallScope.Frontend);
        var prompts = new NonInteractivePrompts();

        var ex = Assert.Throws<InvalidOperationException>(() => InteractiveConfigBuilder.Build(prompts, scan, config));
        Assert.Contains("Frontend Semgrep scope requires", ex.Message);
    }
}
