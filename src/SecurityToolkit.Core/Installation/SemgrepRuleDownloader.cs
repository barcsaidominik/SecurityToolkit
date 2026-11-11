using System.Text;
using System.Text.RegularExpressions;

namespace SecurityToolkit.Core.Installation;

/// <summary>
/// Downloads and snapshots Semgrep registry rulesets (e.g. "p/csharp"). Snapshots are
/// downloaded once and never silently re-fetched: a manually-modified snapshot is detected via
/// its previously recorded hash and refused, like an owned file in
/// <see cref="BackupService.SaveManagedText"/>.
/// </summary>
public sealed class SemgrepRuleDownloader
{
    private static readonly Regex ValidRulesetName = new("^p/[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Regex YamlRulesKey = new(@"(?m)^rules\s*:", RegexOptions.Compiled);
    private static readonly Regex HtmlResponse = new("(?i)<(?:html|!doctype\\s+html)\\b", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;

    public SemgrepRuleDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Downloads every ruleset in <paramref name="rulesets"/> (e.g. "p/csharp") into
    /// "&lt;securityDirectory&gt;/rules/&lt;ruleset-with-dashes&gt;.yml", skipping ones that already
    /// exist with an unchanged hash. Returns one informational message per newly-downloaded file.
    /// </summary>
    /// <param name="priorHashes">The previous install-state.json's hash map, used to detect manual edits.</param>
    /// <exception cref="InvalidOperationException">
    /// An invalid ruleset name, a manually-modified existing snapshot, or a registry response that
    /// isn't a recognizable YAML/JSON rules document.
    /// </exception>
    public async Task<IReadOnlyList<string>> DownloadAsync(
        string root,
        string securityDirectory,
        IReadOnlyList<string> rulesets,
        IDictionary<string, string> priorHashes,
        BackupService backup,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();

        foreach (var ruleset in rulesets)
        {
            if (!ValidRulesetName.IsMatch(ruleset))
            {
                throw new InvalidOperationException($"Invalid registry ruleset: {ruleset}");
            }

            var fileName = ruleset.Replace('/', '-') + ".yml";
            var path = Path.Combine(securityDirectory, "rules", fileName);

            if (File.Exists(path))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                // Without a prior install on this checkout (fresh clone) the committed snapshot is the
                // pinned baseline, not a manual edit.
                if (backup.HasPriorInstall
                    && (!priorHashes.TryGetValue(relative, out var expected) || !ToolkitFile.HashMatchesIgnoringLineEndings(path, expected)))
                {
                    throw new InvalidOperationException($"Rule snapshot changed manually: {relative}. Keep local rules in a separate custom file.");
                }

                continue;
            }

            var url = $"https://semgrep.dev/c/{ruleset}";
            string text;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("application/yaml, text/yaml, application/json");
                request.Headers.UserAgent.ParseAdd("SecurityToolkit/1.1");

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                using var response = await _httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                // Read the original bytes instead of relying on the HTTP stack's content-type-driven decoding.
                var bytes = await response.Content.ReadAsByteArrayAsync(linkedCts.Token).ConfigureAwait(false);
                text = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException($"Registry ruleset {ruleset}: {ex.Message}", ex);
            }

            var snapshot = NormalizeRuleSnapshot(text);
            backup.SaveManagedText(path, snapshot, owned: true, dryRun: dryRun);

            if (!dryRun)
            {
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
                messages.Add(
                    $"Semgrep snapshot: {url}; SHA256={hash}. The vendor rule license applies; the snapshot changes only when you deliberately refresh it.");
            }
        }

        return messages;
    }

    /// <summary>
    /// Validates and normalizes a registry response:
    /// strips a UTF-8 BOM, normalizes CRLF to LF, and requires either a non-empty JSON "rules"
    /// array or a YAML document with a top-level "rules:" key (and no HTML error page).
    /// </summary>
    /// <exception cref="InvalidOperationException">The response is neither a valid JSON nor YAML rules document.</exception>
    public static string NormalizeRuleSnapshot(string text)
    {
        var normalized = text.TrimStart('﻿').Replace("\r\n", "\n");

        if (Regex.IsMatch(normalized, @"^\s*\{"))
        {
            IDictionary<string, object?> data;
            try
            {
                data = SecurityToolkit.Core.Scanners.ScannerSupport.ParseJsonObject(normalized);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Registry returned invalid JSON.", ex);
            }

            if (!data.TryGetValue("rules", out var rulesValue) || rulesValue is not IEnumerable<object?> rulesList || !rulesList.Any())
            {
                throw new InvalidOperationException("Registry JSON has no non-empty rules collection.");
            }
        }
        else if (!YamlRulesKey.IsMatch(normalized) || HtmlResponse.IsMatch(normalized))
        {
            throw new InvalidOperationException(
                "Registry response is not a YAML/JSON rules document. Check proxy/login/network; response was not installed.");
        }

        // JSON is valid YAML; Semgrep itself performs authoritative syntax/rule validation at scan time.
        return normalized;
    }
}
