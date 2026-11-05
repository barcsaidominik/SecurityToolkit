using System.Reflection;

namespace SecurityToolkit.Core.Installation;

/// <summary>
/// Loads the install-time template files (MSBuild props, rule/config starters) embedded in this
/// assembly under "Installation/Templates/", keyed by their original file name (e.g. "gitleaks.toml"). Embedding avoids a companion "templates/" directory on disk,
/// which matters once the CLI is published as a single file.
/// </summary>
public static class InstallerTemplates
{
    private const string ResourcePrefix = "SecurityToolkit.Core.Installation.Templates.";

    /// <summary>Reads every embedded template into memory, keyed by its original file name.</summary>
    public static IReadOnlyDictionary<string, string> LoadAll() => LoadAll(typeof(InstallerTemplates).Assembly);

    /// <summary>Testable overload reading from an arbitrary assembly's embedded resources.</summary>
    public static IReadOnlyDictionary<string, string> LoadAll(Assembly assembly)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = resourceName[ResourcePrefix.Length..];
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Missing embedded resource: {resourceName}");
            using var reader = new StreamReader(stream);
            result[fileName] = reader.ReadToEnd();
        }

        return result;
    }
}
