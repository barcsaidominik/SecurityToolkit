using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecurityToolkit.Core;

/// <summary>
/// Resolves the latest stable version of a NuGet package via the flat container index.
/// </summary>
public sealed class NuGetVersionResolver
{
    private readonly HttpClient _httpClient;

    public NuGetVersionResolver(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Returns the latest stable (no "-" prerelease marker) version of <paramref name="package"/>,
    /// per the flat container index's ascending version order (its last stable entry is latest).
    /// </summary>
    /// <exception cref="InvalidOperationException">No stable version is published for the package.</exception>
    public async Task<string> GetLatestStableVersionAsync(string package, CancellationToken cancellationToken = default)
    {
        var id = package.ToLowerInvariant();
        var uri = new Uri($"https://api.nuget.org/v3-flatcontainer/{id}/index.json");

        using var stream = await _httpClient.GetStreamAsync(uri, cancellationToken).ConfigureAwait(false);
        var index = await JsonSerializer.DeserializeAsync(stream, ToolkitJsonContext.Default.NuGetFlatContainerIndex, cancellationToken)
            .ConfigureAwait(false);

        var stable = index?.Versions?.Where(v => !v.Contains('-', StringComparison.Ordinal)).ToArray()
            ?? Array.Empty<string>();

        if (stable.Length == 0)
        {
            throw new InvalidOperationException($"No stable version: {package}");
        }

        return stable[^1];
    }
}

internal sealed class NuGetFlatContainerIndex
{
    [JsonPropertyName("versions")]
    public List<string>? Versions { get; set; }
}

[JsonSerializable(typeof(NuGetFlatContainerIndex))]
internal partial class ToolkitJsonContext : JsonSerializerContext
{
}
