namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// Renders a scan run's status.json into human-readable forms (a colored console table and a
/// Markdown summary) so developers can review a run without opening any JSON file directly.
/// Both renderings read the same parsed status document, so they never drift from each other.
/// </summary>
public static class ScanReportFormatter
{
    /// <summary>
    /// Writes ANSI-colored table rows to <paramref name="writer"/>: one row per tool, red when
    /// blocking, yellow when failed/incomplete, green when completed cleanly.
    /// </summary>
    public static void WriteConsoleTable(TextWriter writer, IDictionary<string, object?> statusDocument)
    {
        var mode = ScannerSupport.GetString(statusDocument, "mode") ?? "Report";
        var time = ScannerSupport.GetString(statusDocument, "time") ?? "";
        writer.WriteLine($"Security scan report — mode={mode} time={time}");
        writer.WriteLine();

        var header = $"{"TOOL",-12} {"TARGET",-28} {"STATUS",-10} {"FINDINGS",-9} BLOCKING";
        writer.WriteLine(header);
        writer.WriteLine(new string('-', header.Length));

        foreach (var toolEntry in ScannerSupport.GetList(statusDocument, "tools"))
        {
            if (toolEntry is not IDictionary<string, object?> tool)
            {
                continue;
            }

            var name = ScannerSupport.GetString(tool, "tool") ?? "";
            var target = ScannerSupport.GetString(tool, "target") ?? "";
            var status = ScannerSupport.GetString(tool, "status") ?? "";
            var findings = tool.TryGetValue("findings", out var f) && f is not null ? f.ToString() : "n/a";
            var blocking = tool.TryGetValue("blocking", out var b) && b is bool bb && bb;

            var (colorCode, resetCode) = ConsoleColorFor(status, blocking);
            writer.WriteLine($"{colorCode}{name,-12} {target,-28} {status,-10} {findings,-9} {(blocking ? "YES" : "no")}{resetCode}");

            var message = ScannerSupport.GetString(tool, "message");
            if (!string.IsNullOrEmpty(message))
            {
                writer.WriteLine($"             {message}");
            }
        }
    }

    /// <summary>
    /// Builds the same information as <see cref="WriteConsoleTable"/>, plus (when present in
    /// <paramref name="outputDirectory"/>) the Sonar severity breakdown and Semgrep coverage
    /// notes, as a Markdown document suitable for "results/&lt;run&gt;/summary.md" or attaching to
    /// a PR/pipeline artifact.
    /// </summary>
    public static string BuildMarkdown(IDictionary<string, object?> statusDocument, string outputDirectory)
    {
        var mode = ScannerSupport.GetString(statusDocument, "mode") ?? "Report";
        var time = ScannerSupport.GetString(statusDocument, "time") ?? "";
        var commit = ScannerSupport.GetString(statusDocument, "commit") ?? "";

        var lines = new List<string>
        {
            "# Security scan summary",
            "",
            $"- Mode: `{mode}`",
            $"- Time: `{time}`",
            $"- Commit: `{commit}`",
            "",
            "| Tool | Target | Status | Findings | Blocking | Message |",
            "|---|---|---|---|---|---|",
        };

        foreach (var toolEntry in ScannerSupport.GetList(statusDocument, "tools"))
        {
            if (toolEntry is not IDictionary<string, object?> tool)
            {
                continue;
            }

            var name = ScannerSupport.GetString(tool, "tool") ?? "";
            var target = ScannerSupport.GetString(tool, "target") ?? "";
            var status = ScannerSupport.GetString(tool, "status") ?? "";
            var findings = tool.TryGetValue("findings", out var f) && f is not null ? f.ToString() : "n/a";
            var blocking = tool.TryGetValue("blocking", out var b) && b is bool bb && bb;
            var message = ScannerSupport.GetString(tool, "message") ?? "";

            lines.Add($"| {name} | {target} | {status} | {findings} | {(blocking ? "**yes**" : "no")} | {message} |");
        }

        AppendSonarSeverityBreakdown(lines, outputDirectory);
        AppendSemgrepCoverageNote(lines, outputDirectory);

        return string.Join('\n', lines) + "\n";
    }

    private static void AppendSonarSeverityBreakdown(List<string> lines, string outputDirectory)
    {
        var path = Path.Combine(outputDirectory, "sonar-issues.json");
        if (!File.Exists(path))
        {
            return;
        }

        var data = ScannerSupport.ReadReport(path);
        var severityFacet = ScannerSupport.GetList(data, "facets")
            .OfType<IDictionary<string, object?>>()
            .FirstOrDefault(facet => ScannerSupport.GetString(facet, "property") == "severities");
        if (severityFacet is null)
        {
            return;
        }

        lines.Add("");
        lines.Add("## Sonar vulnerability severities");
        lines.Add("");
        lines.Add("| Severity | Count |");
        lines.Add("|---|---|");
        // The facet comes back by count; the table reads better most-severe first.
        foreach (var entry in ScannerSupport.GetList(severityFacet, "values").OfType<IDictionary<string, object?>>()
            .OrderBy(e => SeverityRank(ScannerSupport.GetString(e, "val"))))
        {
            var severity = ScannerSupport.GetString(entry, "val") ?? "";
            var count = entry.TryGetValue("count", out var c) ? c?.ToString() ?? "0" : "0";
            if (count != "0")
            {
                lines.Add($"| {severity} | {count} |");
            }
        }

        AppendSonarTopIssues(lines, data);
    }

    private const int TopSonarIssueCount = 10;

    private static readonly string[] SonarSeverityOrder = { "BLOCKER", "CRITICAL", "MAJOR", "MINOR", "INFO" };

    /// <summary>
    /// Lists the <see cref="TopSonarIssueCount"/> most severe issues of sonar-issues.json by rule and
    /// file:line, so triage can start from summary.md instead of a manual API query.
    /// </summary>
    private static void AppendSonarTopIssues(List<string> lines, IDictionary<string, object?> data)
    {
        var issues = ScannerSupport.GetList(data, "issues").OfType<IDictionary<string, object?>>()
            .Select((issue, index) => (issue, index))
            .OrderBy(x => SeverityRank(ScannerSupport.GetString(x.issue, "severity")))
            .ThenBy(x => x.index)
            .Select(x => x.issue)
            .Take(TopSonarIssueCount)
            .ToList();
        if (issues.Count == 0)
        {
            return;
        }

        var total = ScannerSupport.GetInt(data, "total");
        lines.Add("");
        lines.Add(total > issues.Count
            ? $"## Top {issues.Count} Sonar vulnerabilities (of {total})"
            : "## Sonar vulnerabilities");
        lines.Add("");
        lines.Add("| Severity | Rule | Location | Message |");
        lines.Add("|---|---|---|---|");
        foreach (var issue in issues)
        {
            // "component" is "<projectKey>:<path>"; the key prefix is noise in a per-project report.
            var component = ScannerSupport.GetString(issue, "component") ?? "";
            var path = component.Contains(':', StringComparison.Ordinal) ? component[(component.IndexOf(':') + 1)..] : component;
            var location = issue.TryGetValue("line", out var line) && line is not null ? $"{path}:{line}" : path;
            lines.Add($"| {ScannerSupport.GetString(issue, "severity")} | {ScannerSupport.GetString(issue, "rule")} | " +
                $"{EscapeCell(location)} | {EscapeCell(ScannerSupport.GetString(issue, "message") ?? "")} |");
        }
    }

    private static int SeverityRank(string? severity)
    {
        var rank = Array.IndexOf(SonarSeverityOrder, severity);
        return rank < 0 ? SonarSeverityOrder.Length : rank;
    }

    private static string EscapeCell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");

    private static void AppendSemgrepCoverageNote(List<string> lines, string outputDirectory)
    {
        var path = Path.Combine(outputDirectory, "semgrep-coverage.json");
        if (!File.Exists(path))
        {
            return;
        }

        var data = ScannerSupport.ReadReport(path);
        var analysisErrors = ScannerSupport.GetInt(data, "analysisErrors");
        var affectedFiles = ScannerSupport.GetList(data, "affectedFiles").Count;

        lines.Add("");
        lines.Add("## Semgrep coverage");
        lines.Add("");
        lines.Add(ScannerSupport.GetBool(data, "partialParsingOnly")
            ? $"Partially parsed: {affectedFiles} file(s) had a construct Semgrep could not parse ({analysisErrors} warning(s)); " +
              "it skipped only that construct and analyzed the rest, so the run counts as complete. Parser warnings do not prove invalid application code."
            : $"Incomplete analysis: {analysisErrors} parser error(s) across {affectedFiles} file(s). " +
              "Findings from successfully-parsed files are preserved above; parser errors do not prove invalid application code.");
    }

    private static (string ColorCode, string ResetCode) ConsoleColorFor(string status, bool blocking)
    {
        const string Reset = "[0m";
        if (blocking)
        {
            return ("[31m", Reset); // red
        }

        return status switch
        {
            "failed" => ("[33m", Reset), // yellow
            "completed" => ("[32m", Reset), // green
            _ => (string.Empty, string.Empty),
        };
    }
}
