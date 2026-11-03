using SecurityToolkit.Core.Installation;

namespace SecurityToolkit.Core.Tests.Installation;

public class InstallerTemplatesTests
{
    [Fact]
    public void LoadAll_ReturnsEveryExpectedTemplateFile()
    {
        var templates = InstallerTemplates.LoadAll();

        Assert.Equal(
            new[]
            {
                "Toolkit.Common.ps1", "Initialize-SecurityTools.ps1",
                "Restore-SecurityToolkit.ps1", "SecurityToolkit.props", "gitleaks.toml", "semgrep-starter.yml",
                "security.config.local.json.example",
            }.OrderBy(x => x, StringComparer.Ordinal),
            templates.Keys.OrderBy(x => x, StringComparer.Ordinal));

        Assert.Contains("Get-ToolkitPath", templates["Toolkit.Common.ps1"]);
    }

    [Theory]
    [InlineData(".sonarqube/out/ScannerEngineInput.json", true)]
    [InlineData("src/WebApi/.sonarqube/conf/SonarQubeAnalysisConfig.xml", true)]
    [InlineData("src/WebApi/WebApi.Host/bin/Debug/appsettings.json", true)]
    [InlineData("src/WebApi/WebApi.Host/obj/project.assets.json", true)]
    [InlineData("src/WebApi/WebApi.Host/appsettings.json", false)]
    [InlineData("src/sonarqube.json", false)]
    public void GitleaksBuildOutputAllowlist_CoversGeneratedDirectoriesOnly(string path, bool allowlisted)
    {
        var toml = InstallerTemplates.LoadAll()["gitleaks.toml"];
        var match = System.Text.RegularExpressions.Regex.Match(
            toml, @"description = ""Build output directories[^\n]*\npaths = \['''(?<pattern>[^']+)'''\]");
        Assert.True(match.Success, "Build output allowlist not found in gitleaks.toml.");

        Assert.Equal(allowlisted, System.Text.RegularExpressions.Regex.IsMatch(path, match.Groups["pattern"].Value));
    }
}
