using System.Text.RegularExpressions;

namespace SecurityToolkit.Core.Installation;

/// <summary>
/// Resolves the SHA-256 of every published security-toolkit release binary from the release's
/// <c>SHA256SUMS</c> file (sha256sum format, written by the release workflow). The hashes are
/// pinned in the project's lock and written into the generated pipelines, which verify the
/// downloaded binary against them before putting it on PATH; a release asset swapped after
/// pinning therefore fails the pipeline instead of running.
/// </summary>
public sealed class ToolkitReleaseResolver
{
    /// <summary>Release asset name per supported runtime identifier.</summary>
    public static readonly IReadOnlyDictionary<string, string> AssetNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["win-x64"] = "security-toolkit-win-x64.exe",
        ["linux-x64"] = "security-toolkit-linux-x64",
        ["linux-arm64"] = "security-toolkit-linux-arm64",
    };

    public const string ChecksumFileName = "SHA256SUMS";

    private static readonly Regex ChecksumLine = new(@"^([0-9a-fA-F]{64}) [ *](\S+)$", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;

    public ToolkitReleaseResolver(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>Release download URL of one file: <c>&lt;baseUrl&gt;/v&lt;version&gt;/&lt;file&gt;</c>.</summary>
    public static string DownloadUrl(string baseUrl, string version, string fileName)
        => $"{baseUrl.TrimEnd('/')}/v{version}/{fileName}";

    /// <summary>Returns runtime identifier → lowercase SHA-256 for every asset in <see cref="AssetNames"/>.</summary>
    /// <exception cref="InvalidOperationException">The checksum file is unreachable, malformed, or misses an asset.</exception>
    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        string baseUrl, string version, CancellationToken cancellationToken = default)
    {
        var url = DownloadUrl(baseUrl, version, ChecksumFileName);
        string content;
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"The checksum file of the security-toolkit v{version} release is not available ({(int)response.StatusCode}): {url}");
            }

            content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"The checksum file of the security-toolkit v{version} release could not be downloaded: {url} ({ex.Message})", ex);
        }

        var byFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var match = ChecksumLine.Match(line);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Invalid line in {ChecksumFileName}: {line}");
            }

            byFile[match.Groups[2].Value] = match.Groups[1].Value.ToLowerInvariant();
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rid, asset) in AssetNames)
        {
            if (!byFile.TryGetValue(asset, out var hash))
            {
                throw new InvalidOperationException($"{ChecksumFileName} does not list {asset} (v{version}).");
            }

            result[rid] = hash;
        }

        return result;
    }
}
