namespace SecurityToolkit.Core.Tests;

public class ToolkitJsonTests
{
    [Fact]
    public void Serialize_SortsObjectKeysCaseSensitiveOrdinal()
    {
        var value = new Dictionary<string, object?>
        {
            ["b"] = 1,
            ["A"] = 2,
            ["a"] = 3,
        };

        var json = ToolkitJson.Serialize(value);

        // Ordinal sort: uppercase letters sort before lowercase ("A" < "a" < "b").
        var indexA = json.IndexOf("\"A\"", StringComparison.Ordinal);
        var indexLowerA = json.IndexOf("\"a\"", StringComparison.Ordinal);
        var indexB = json.IndexOf("\"b\"", StringComparison.Ordinal);

        Assert.True(indexA < indexLowerA);
        Assert.True(indexLowerA < indexB);
    }

    [Fact]
    public void Serialize_IsDeterministic_RepeatedCallsProduceIdenticalOutput()
    {
        var value = new Dictionary<string, object?>
        {
            ["zeta"] = new List<object?> { 1, 2, new Dictionary<string, object?> { ["y"] = 1, ["x"] = 2 } },
            ["alpha"] = "value",
        };

        var first = ToolkitJson.Serialize(value);
        var second = ToolkitJson.Serialize(value);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Serialize_CanonicalizesNestedDictionariesAndLists()
    {
        var value = new Dictionary<string, object?>
        {
            ["tools"] = new Dictionary<string, object?>
            {
                ["semgrep"] = new Dictionary<string, object?> { ["version"] = "1.2.3", ["digest"] = "abc" },
            },
            ["items"] = new List<object?>
            {
                new Dictionary<string, object?> { ["b"] = 1, ["a"] = 2 },
            },
        };

        var json = ToolkitJson.Serialize(value);

        var expectedOrder = new[] { "\"digest\"", "\"version\"" };
        var digestIndex = json.IndexOf(expectedOrder[0], StringComparison.Ordinal);
        var versionIndex = json.IndexOf(expectedOrder[1], StringComparison.Ordinal);
        Assert.True(digestIndex >= 0 && versionIndex >= 0 && digestIndex < versionIndex);

        var itemAIndex = json.IndexOf("\"a\": 2", StringComparison.Ordinal);
        var itemBIndex = json.IndexOf("\"b\": 1", StringComparison.Ordinal);
        Assert.True(itemAIndex >= 0 && itemBIndex >= 0 && itemAIndex < itemBIndex);
    }

    [Fact]
    public void Serialize_EndsWithTrailingNewline()
    {
        var json = ToolkitJson.Serialize(new Dictionary<string, object?> { ["a"] = 1 });

        Assert.EndsWith("\n", json);
    }

    [Fact]
    public void ReadLock_ParsesValidSchemaVersion1_ReturnsNestedDictionary()
    {
        var directory = Directory.CreateTempSubdirectory("toolkit-lock-tests-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "security.lock.json"),
                """
                {
                  "schemaVersion": 1,
                  "tools": {
                    "semgrep": { "version": "1.2.3" }
                  }
                }
                """);

            var lockData = ToolkitJson.ReadLock(directory);

            Assert.Equal(1L, lockData["schemaVersion"]);
            var tools = Assert.IsAssignableFrom<IDictionary<string, object?>>(lockData["tools"]);
            var semgrep = Assert.IsAssignableFrom<IDictionary<string, object?>>(tools["semgrep"]);
            Assert.Equal("1.2.3", semgrep["version"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadLock_UnsupportedSchemaVersion_Throws()
    {
        var directory = Directory.CreateTempSubdirectory("toolkit-lock-tests-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "security.lock.json"),
                """{ "schemaVersion": 2, "tools": {} }""");

            Assert.Throws<InvalidOperationException>(() => ToolkitJson.ReadLock(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadLock_MissingFile_Throws()
    {
        var directory = Directory.CreateTempSubdirectory("toolkit-lock-tests-").FullName;
        try
        {
            Assert.Throws<FileNotFoundException>(() => ToolkitJson.ReadLock(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
