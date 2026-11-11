using static SecurityToolkit.Core.Installation.ConfigAccess;

namespace SecurityToolkit.Core.Installation;

/// <summary>Builds security.lock.json from the resolved config and the previous lock.</summary>
internal static class SecurityLockBuilder
{
    /// <summary>
    /// Builds the committed lock's tool entries — CI-relevant tools only (semgrep, gitleaks, osv,
    /// devskim, sonarScanner, dotnetCoverage). The shared Sonar/PostgreSQL server images are
    /// machine-level infra, pinned in that stack's own .env (<c>security-toolkit infra install</c>),
    /// never here. Reads its own prior state directly (not passed in) so callers don't have to
    /// remember to read/thread it.
    /// </summary>
    internal static IDictionary<string, object?> Build(SecurityConfig config, string securityDirectory)
    {
        var oldLock = ReadExistingLock(securityDirectory);
        var oldTools = oldLock is not null ? GetDict(oldLock, "tools") : new Dictionary<string, object?>();

        var lockTools = new Dictionary<string, object?>();

        foreach (var name in new[] { "semgrep", "gitleaks", "osv" })
        {
            var enabled = config.IsToolEnabled(name);
            var image = config.Images.GetValueOrDefault(name, string.Empty);
            var wasReady = false;

            if (TryGetDict(oldTools, name) is { } oldTool
                && string.Equals(GetString(oldTool, "sourceImage"), image, StringComparison.Ordinal)
                && GetBool(oldTool, "ready"))
            {
                image = GetString(oldTool, "image") ?? image;
                wasReady = true;
            }

            lockTools[name] = new Dictionary<string, object?>
            {
                ["enabled"] = enabled,
                ["image"] = image,
                ["sourceImage"] = config.Images.GetValueOrDefault(name, string.Empty),
                ["ready"] = enabled && wasReady,
            };
        }

        foreach (var name in new[] { "devskim", "sonarScanner", "dotnetCoverage" })
        {
            var enabled = name switch
            {
                "sonarScanner" => config.IsToolEnabled("sonar"),
                "dotnetCoverage" => config.IsToolEnabled("sonar") && config.SonarCoverage,
                _ => config.IsToolEnabled(name),
            };
            var package = name switch
            {
                "devskim" => "Microsoft.CST.DevSkim.CLI",
                "sonarScanner" => "dotnet-sonarscanner",
                _ => "dotnet-coverage",
            };

            var version = config.Versions.GetValueOrDefault(name, string.Empty);
            if (string.IsNullOrEmpty(version) && TryGetDict(oldTools, name) is { } oldTool2)
            {
                version = GetString(oldTool2, "version") ?? string.Empty;
            }

            var wasReady = TryGetDict(oldTools, name) is { } oldTool3
                && GetBool(oldTool3, "ready")
                && string.Equals(GetString(oldTool3, "version"), version, StringComparison.Ordinal);

            lockTools[name] = new Dictionary<string, object?>
            {
                ["enabled"] = enabled,
                ["package"] = package,
                ["version"] = version,
                ["ready"] = enabled && wasReady,
            };
        }

        var lockData = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1L,
            ["tools"] = lockTools,
        };

        // Only present when enabled, so a project that never uses it keeps a byte-identical lock.
        var releaseVersion = config.ToolkitReleaseVersion;
        if (releaseVersion.Length > 0)
        {
            var baseUrl = config.ToolkitReleaseBaseUrl.TrimEnd('/');
            var hashes = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (oldLock is not null
                && TryGetDict(oldLock, "toolkitRelease") is { } oldRelease
                && string.Equals(GetString(oldRelease, "version"), releaseVersion, StringComparison.Ordinal)
                && string.Equals(GetString(oldRelease, "baseUrl"), baseUrl, StringComparison.Ordinal)
                && TryGetDict(oldRelease, "sha256") is { } oldHashes
                && ToolkitReleaseResolver.AssetNames.Keys.All(rid => GetString(oldHashes, rid) is { Length: 64 }))
            {
                foreach (var rid in ToolkitReleaseResolver.AssetNames.Keys)
                {
                    hashes[rid] = GetString(oldHashes, rid);
                }
            }

            lockData["toolkitRelease"] = new Dictionary<string, object?>
            {
                ["version"] = releaseVersion,
                ["baseUrl"] = baseUrl,
                ["sha256"] = hashes,
            };
        }

        return lockData;
    }

    /// <summary>Reads the committed lock, if it exists yet.</summary>
    private static IDictionary<string, object?>? ReadExistingLock(string securityDirectory)
    {
        try
        {
            return ToolkitJson.ReadLock(securityDirectory);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
