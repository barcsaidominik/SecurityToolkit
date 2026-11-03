using SecurityToolkit.Core.Scanners;

namespace SecurityToolkit.Core.Tests.Scanners;

public class ScanReportFormatterTests : IDisposable
{
    private readonly string _outputDirectory = Directory.CreateTempSubdirectory("formatter-tests-").FullName;

    public void Dispose() => Directory.Delete(_outputDirectory, recursive: true);

    [Fact]
    public void BuildMarkdown_SonarIssuesJson_ListsTopIssuesByRuleAndLocation()
    {
        File.WriteAllText(Path.Combine(_outputDirectory, "sonar-issues.json"), """
            {
              "total": 12,
              "issues": [
                { "severity": "MINOR", "rule": "csharpsquid:S3330", "component": "MyApp:src/Cookie.cs", "line": 27, "message": "Set HttpOnly." },
                { "severity": "BLOCKER", "rule": "typescript:S6268", "component": "MyApp:src/Client/safe-html.pipe.ts", "line": 8, "message": "Make sure bypassing | is safe" },
                { "severity": "MAJOR", "rule": "csharpsquid:S2068", "component": "MyApp:src/appsettings.json", "message": "Review this hard-coded password." }
              ],
              "facets": [ { "property": "severities", "values": [ { "val": "MAJOR", "count": 11 }, { "val": "BLOCKER", "count": 1 } ] } ]
            }
            """);

        var markdown = ScanReportFormatter.BuildMarkdown(StatusWithSonar(), _outputDirectory);

        Assert.Contains("## Top 3 Sonar vulnerabilities (of 12)", markdown);
        // Ranked by severity, not by API order.
        Assert.True(markdown.IndexOf("typescript:S6268", StringComparison.Ordinal) < markdown.IndexOf("csharpsquid:S3330", StringComparison.Ordinal));
        Assert.Contains("| BLOCKER | typescript:S6268 | src/Client/safe-html.pipe.ts:8 | Make sure bypassing \\| is safe |", markdown);
        Assert.Contains("| MAJOR | csharpsquid:S2068 | src/appsettings.json | Review this hard-coded password. |", markdown);
        Assert.Contains("| BLOCKER | 1 |\n| MAJOR | 11 |", markdown);
    }

    [Fact]
    public void BuildMarkdown_SonarIssuesJsonWithoutIssues_HasNoIssueSection()
    {
        File.WriteAllText(Path.Combine(_outputDirectory, "sonar-issues.json"), """
            { "total": 0, "issues": [], "facets": [ { "property": "severities", "values": [] } ] }
            """);

        var markdown = ScanReportFormatter.BuildMarkdown(StatusWithSonar(), _outputDirectory);

        Assert.DoesNotContain("Sonar vulnerabilities", markdown);
    }

    private static Dictionary<string, object?> StatusWithSonar() => new()
    {
        ["mode"] = "Report",
        ["tools"] = new List<object?>
        {
            new Dictionary<string, object?> { ["tool"] = "sonar", ["target"] = "MyApp", ["status"] = "completed", ["findings"] = 12L },
        },
    };
}
