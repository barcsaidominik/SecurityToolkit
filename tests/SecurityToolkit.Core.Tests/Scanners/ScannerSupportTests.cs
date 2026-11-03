using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Core.Tests.Scanners;

public class ScannerSupportTests
{
    [Fact]
    public void MergeLocalOverride_NoLocalFile_LeavesConfigUnchanged()
    {
        var root = Directory.CreateTempSubdirectory("scanner-support-tests-").FullName;
        try
        {
            var config = new Dictionary<string, object?> { ["sonar"] = new Dictionary<string, object?> { ["url"] = "https://sonar.example.com" } };

            ScannerSupport.MergeLocalOverride(config, root);

            var sonar = Assert.IsType<Dictionary<string, object?>>(config["sonar"]);
            Assert.Equal("https://sonar.example.com", sonar["url"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MergeLocalOverride_OverridesNestedFieldWithoutDroppingSiblings()
    {
        var root = Directory.CreateTempSubdirectory("scanner-support-tests-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "security.config.local.json"), """
                {
                  "sonar": { "url": "http://localhost:9000", "port": 9000 },
                  "startSonarServer": true
                }
                """);

            var config = new Dictionary<string, object?>
            {
                ["sonar"] = new Dictionary<string, object?>
                {
                    ["url"] = "https://sonar.example.com",
                    ["projectKey"] = "team-shared-key",
                },
                ["startSonarServer"] = false,
                ["tools"] = new Dictionary<string, object?> { ["sonar"] = true },
            };

            ScannerSupport.MergeLocalOverride(config, root);

            var sonar = Assert.IsType<Dictionary<string, object?>>(config["sonar"]);
            Assert.Equal("http://localhost:9000", sonar["url"]);
            Assert.Equal(9000L, sonar["port"]);
            Assert.Equal("team-shared-key", sonar["projectKey"]); // untouched sibling survives the merge
            Assert.Equal(true, config["startSonarServer"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MergeLocalOverride_AllowsKeyNotPresentInBaseConfig()
    {
        var root = Directory.CreateTempSubdirectory("scanner-support-tests-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "security.config.local.json"), """{ "debugOnly": true }""");
            var config = new Dictionary<string, object?>();

            ScannerSupport.MergeLocalOverride(config, root);

            Assert.Equal(true, config["debugOnly"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
