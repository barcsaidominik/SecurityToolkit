namespace SecurityToolkit.Cli.Tests;

/// <summary>
/// Shared xUnit collection for every test class that reassigns Console.Out/Console.Error/
/// Console.In: xUnit parallelizes across test classes by default, and the global Console
/// streams aren't per-test state, so two such classes running concurrently race on them
/// (observed as an intermittent PromptsTests/ExitCodeTests failure). Collection membership
/// forces them onto the same test-collection queue, which xUnit never parallelizes internally.
/// </summary>
[CollectionDefinition("Console")]
public class ConsoleTestCollection
{
}

[Collection("Console")]
public class PromptsTests
{
    private static (T Result, string Output) RunWithConsole<T>(string input, Func<T> action)
    {
        var originalIn = Console.In;
        var originalOut = Console.Out;
        try
        {
            using var reader = new StringReader(input);
            using var writer = new StringWriter();
            Console.SetIn(reader);
            Console.SetOut(writer);

            var result = action();
            return (result, writer.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }

    [Theory]
    [InlineData("y", true)]
    [InlineData("yes", true)]
    [InlineData("NO", false)]
    [InlineData("n", false)]
    [InlineData("no", false)]
    public void ReadYesNo_RecognizesAnswer(string answer, bool expected)
    {
        var prompts = new ConsolePrompts();

        var (result, _) = RunWithConsole(answer + "\n", () => prompts.ReadYesNo("Question", defaultValue: !expected));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ReadYesNo_EmptyAnswer_ReturnsDefault()
    {
        var prompts = new ConsolePrompts();

        var (result, _) = RunWithConsole("\n", () => prompts.ReadYesNo("Question", defaultValue: true));

        Assert.True(result);
    }

    [Fact]
    public void ReadYesNo_InvalidThenValidAnswer_Retries()
    {
        var prompts = new ConsolePrompts();

        var (result, _) = RunWithConsole("bogus\nyes\n", () => prompts.ReadYesNo("Question", defaultValue: false));

        Assert.True(result);
    }

    [Fact]
    public void ReadValue_EmptyAnswer_ReturnsDefault()
    {
        var prompts = new ConsolePrompts();

        var (result, _) = RunWithConsole("\n", () => prompts.ReadValue("Value", "default-value"));

        Assert.Equal("default-value", result);
    }

    [Fact]
    public void ReadValue_NonEmptyAnswer_ReturnsAnswer()
    {
        var prompts = new ConsolePrompts();

        var (result, _) = RunWithConsole("custom-value\n", () => prompts.ReadValue("Value", "default-value"));

        Assert.Equal("custom-value", result);
    }

    [Fact]
    public void SelectTargets_EmptyAnswer_ReturnsAllPaths()
    {
        var prompts = new ConsolePrompts();
        var paths = new[] { "src/A.sln", "src/B.sln" };

        var (result, _) = RunWithConsole("\n", () => prompts.SelectTargets("Targets", paths, Array.Empty<string>()));

        Assert.Equal(paths, result);
    }

    [Fact]
    public void SelectTargets_ZeroAnswer_ReturnsNone()
    {
        var prompts = new ConsolePrompts();
        var paths = new[] { "src/A.sln", "src/B.sln" };

        var (result, _) = RunWithConsole("0\n", () => prompts.SelectTargets("Targets", paths, Array.Empty<string>()));

        Assert.Empty(result);
    }

    [Fact]
    public void SelectTargets_CommaSeparatedIndexes_ReturnsSelectedSubset()
    {
        var prompts = new ConsolePrompts();
        var paths = new[] { "src/A.sln", "src/B.sln", "src/C.sln" };

        var (result, _) = RunWithConsole("1,3\n", () => prompts.SelectTargets("Targets", paths, Array.Empty<string>()));

        Assert.Equal(new[] { "src/A.sln", "src/C.sln" }, result);
    }

    [Fact]
    public void SelectTargets_InvalidThenValidSelection_Retries()
    {
        var prompts = new ConsolePrompts();
        var paths = new[] { "src/A.sln", "src/B.sln" };

        var (result, _) = RunWithConsole("99\n1\n", () => prompts.SelectTargets("Targets", paths, Array.Empty<string>()));

        Assert.Equal(new[] { "src/A.sln" }, result);
    }

    [Fact]
    public void SelectTargets_NoDiscoveredPaths_ReturnsExistingWithoutPrompting()
    {
        var prompts = new ConsolePrompts();
        var existing = new[] { "src/Existing.sln" };

        var (result, output) = RunWithConsole(string.Empty, () => prompts.SelectTargets("Targets", Array.Empty<string>(), existing));

        Assert.Equal(existing, result);
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void ReadInt_EmptyAnswer_ReturnsDefault()
    {
        var prompts = new ConsolePrompts();

        var (result, _) = RunWithConsole("\n", () => prompts.ReadInt("Port", 9000));

        Assert.Equal(9000, result);
    }

    [Fact]
    public void ReadInt_InvalidThenValidAnswer_Retries()
    {
        var prompts = new ConsolePrompts();

        var (result, _) = RunWithConsole("bogus\n9001\n", () => prompts.ReadInt("Port", 9000));

        Assert.Equal(9001, result);
    }

    [Fact]
    public void NonInteractivePrompts_ReadInt_AlwaysReturnsDefault()
    {
        var prompts = new NonInteractivePrompts();

        Assert.Equal(9000, prompts.ReadInt("Port", 9000));
    }

    [Fact]
    public void NonInteractivePrompts_ReadYesNo_AlwaysReturnsDefault()
    {
        var prompts = new NonInteractivePrompts();

        Assert.True(prompts.ReadYesNo("Question", true));
        Assert.False(prompts.ReadYesNo("Question", false));
    }

    [Fact]
    public void NonInteractivePrompts_ReadValue_AlwaysReturnsDefault()
    {
        var prompts = new NonInteractivePrompts();

        Assert.Equal("default", prompts.ReadValue("Value", "default"));
    }

    [Fact]
    public void NonInteractivePrompts_SelectTargets_PrefersExistingSelectionOverDiscovered()
    {
        var prompts = new NonInteractivePrompts();
        var discovered = new[] { "src/A.sln", "src/B.sln" };
        var existing = new[] { "src/Existing.sln" };

        var result = prompts.SelectTargets("Targets", discovered, existing);

        Assert.Equal(existing, result);
    }

    [Fact]
    public void NonInteractivePrompts_SelectTargets_NoExistingSelection_ReturnsAllDiscovered()
    {
        var prompts = new NonInteractivePrompts();
        var discovered = new[] { "src/A.sln", "src/B.sln" };

        var result = prompts.SelectTargets("Targets", discovered, Array.Empty<string>());

        Assert.Equal(discovered, result);
    }
}
