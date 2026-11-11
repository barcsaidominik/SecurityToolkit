namespace SecurityToolkit.Core.Scanners;

/// <summary>
/// DevSkim static analysis via the locally provisioned CLI binary. Supplementary lint inventory only — never
/// blocking, regardless of mode.
/// </summary>
public sealed class DevSkimScanner : IScanner
{
    private readonly IProcessRunner _processRunner;

    public DevSkimScanner(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public string Name => "devskim";

    public async Task<IReadOnlyList<ScanResult>> RunAsync(ScanContext context, CancellationToken cancellationToken = default)
    {
        if (context.Staged || !ScannerSupport.IsToolEnabled(context, "devskim"))
        {
            return Array.Empty<ScanResult>();
        }

        try
        {
            var devskimTool = ScannerSupport.GetDictionary(context.Lock, "tools") is { } tools
                ? ScannerSupport.GetDictionary(tools, "devskim")
                : null;
            if (devskimTool is null || !ScannerSupport.GetBool(devskimTool, "ready"))
            {
                throw new InvalidOperationException("DevSkim is not provisioned.");
            }

            var executableName = "devskim" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
            var executable = Path.Combine(context.ToolkitDirectory, "bin", executableName);
            var sarifPath = Path.Combine(context.OutputDirectory, "devskim.sarif");

            // .sonarqube/ is SonarScanner's work dir (left behind by a local Sonar run, like for Gitleaks).
            // The generated pipelines are the toolkit's own owned files: with toolkitRelease they carry
            // the pinned SHA-256 checksums, which DS173237 reports as keys in every target project.
            // devskim.ignoreGlobs / ignoreRuleIds (validated at install) narrow the run on top of the
            // fixed exclusions; a config from before these keys existed simply has none.
            var settings = ScannerSupport.GetDictionary(context.Config, "devskim");
            var extraGlobs = settings is null ? new List<string>() : ScannerSupport.GetList(settings, "ignoreGlobs").OfType<string>().ToList();
            var ignoredRules = settings is null ? new List<string>() : ScannerSupport.GetList(settings, "ignoreRuleIds").OfType<string>().ToList();

            var arguments = new List<string>
            {
                "analyze", "--source-code", context.Root, "--output-file", sarifPath,
                "--file-format", "sarif", "--skip-excerpts", "--ignore-globs",
                string.Join(',', new[]
                {
                    "**/.git/**", "**/.security/**", "**/bin/**", "**/obj/**", "**/node_modules/**", "**/dist/**", "**/.sonarqube/**",
                    "**/azure-security-toolkit.yml", "**/.github/workflows/security-toolkit.yml",
                }.Concat(extraGlobs)),
            };
            if (ignoredRules.Count > 0)
            {
                arguments.Add("--ignore-rule-ids");
                arguments.Add(string.Join(',', ignoredRules));
            }

            var runResult = await ScannerSupport.RunNativeAsync(
                _processRunner, context, executable,
                arguments,
                "devskim.log",
                allowedExitCodes: new[] { 0, 1 }, cancellationToken: cancellationToken).ConfigureAwait(false);

            var data = ScannerSupport.ReadReport(sarifPath);
            if (!data.ContainsKey("runs"))
            {
                throw new InvalidOperationException("Invalid DevSkim SARIF.");
            }

            var count = 0;
            foreach (var runObj in ScannerSupport.GetList(data, "runs"))
            {
                if (runObj is IDictionary<string, object?> run && run.ContainsKey("results"))
                {
                    count += ScannerSupport.GetList(run, "results").Count;
                }
            }

            if (runResult.ExitCode == 1 && count == 0)
            {
                throw new InvalidOperationException("DevSkim failed without findings.");
            }

            // DevSkim on Windows writes "src\dir\file" URIs; the Scans tab turns them into Azure Repos
            // links, which only resolve with forward slashes (otherwise a blank file view).
            if (NormalizeUris(data))
            {
                ToolkitJson.WriteFile(sarifPath, data);
            }

            return new[]
            {
                new ScanResult("devskim", "repository", ScanStatus.Completed, count, Blocking: false,
                    "Supplementary lint inventory; no blocking threshold."),
            };
        }
        catch (Exception ex)
        {
            return new[] { new ScanResult("devskim", "repository", ScanStatus.Failed, null, Blocking: false, ex.Message) };
        }
    }

    /// <summary>
    /// Rewrites every SARIF "uri" value to forward slashes and strips user info from
    /// "repositoryUri", in place; true if anything changed.
    /// </summary>
    private static bool NormalizeUris(object? node)
    {
        var changed = false;
        switch (node)
        {
            case IDictionary<string, object?> dict:
                foreach (var key in dict.Keys.ToList())
                {
                    if (key == "uri" && dict[key] is string uri && uri.Contains('\\', StringComparison.Ordinal))
                    {
                        dict[key] = uri.Replace('\\', '/');
                        changed = true;
                    }
                    else if (key == "repositoryUri" && dict[key] is string repositoryUri
                        && Uri.TryCreate(repositoryUri, UriKind.Absolute, out var parsed) && parsed.UserInfo.Length > 0)
                    {
                        // DevSkim copies the git remote verbatim ("https://org@dev.azure.com/..."); a link
                        // with embedded user info opens a blank page in the browser until reloaded.
                        dict[key] = new UriBuilder(parsed) { UserName = string.Empty, Password = string.Empty }.Uri.AbsoluteUri;
                        changed = true;
                    }
                    else
                    {
                        changed |= NormalizeUris(dict[key]);
                    }
                }

                break;
            case IList<object?> list:
                foreach (var item in list)
                {
                    changed |= NormalizeUris(item);
                }

                break;
        }

        return changed;
    }
}
