using System.Text.RegularExpressions;
using System.Xml;

namespace SecurityToolkit.Core.Installation;

/// <summary>
/// Idempotent "BEGIN/END SECURITY-TOOLKIT" marker-block updates for shared, non-toolkit-owned
/// files (.gitignore, .semgrepignore, git hooks), and the MSBuild props-import injection for
/// Directory.Build.props.
/// </summary>
public static class ManagedBlockWriter
{
    /// <summary>
    /// Returns the full text of <paramref name="path"/>'s current content (or "" if it doesn't
    /// exist) with its "&lt;id&gt;"-tagged block replaced by <paramref name="body"/> (inserted if
    /// absent, appended at end-of-file). Callers persist the result via
    /// <see cref="BackupService.SaveManagedText"/> (never with <c>owned: true</c> — these files are
    /// shared with the rest of the project, not toolkit-owned).
    /// </summary>
    /// <exception cref="InvalidOperationException">The file has a BEGIN marker without a matching END marker.</exception>
    public static string UpdateBlock(string existingText, string body, string id = "SECURITY-TOOLKIT")
    {
        var start = $"# BEGIN {id}";
        var end = $"# END {id}";
        var pattern = "(?ms)^" + Regex.Escape(start) + @"\r?\n.*?^" + Regex.Escape(end) + @"(?:\r?\n)?";
        var block = $"{start}\n{body}\n{end}\n";

        if (existingText.Contains(start, StringComparison.Ordinal) && !Regex.IsMatch(existingText, pattern))
        {
            throw new InvalidOperationException($"Malformed managed block (id={id}).");
        }

        if (Regex.IsMatch(existingText, pattern))
        {
            return Regex.Replace(existingText, pattern, _ => block);
        }

        var separator = existingText.Length > 0 && !existingText.EndsWith('\n') ? "\n" : string.Empty;
        return existingText + separator + block;
    }

    /// <summary>
    /// Adds (or validates the pre-existing) conditional "SecurityToolkit"-labeled MSBuild Import
    /// to an MSBuild project file (typically Directory.Build.props), pointing at
    /// ".security/SecurityToolkit.props" relative to <paramref name="propsPath"/>. Preserves the
    /// rest of the file's content/formatting byte-for-byte by inserting the literal import text
    /// just before the closing "&lt;/Project&gt;" tag, rather than re-serializing the whole document.
    /// </summary>
    /// <param name="propsPath">Absolute path to the target MSBuild project file (created if missing).</param>
    /// <param name="securityDirectory">Absolute path to ".security".</param>
    /// <returns>
    /// The file's new full text to persist via <see cref="BackupService.SaveManagedText"/>, or null
    /// if an equivalent import already exists and nothing needs to change.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The file's root element isn't "Project", or it already has a conflicting "SecurityToolkit"-labeled import.
    /// </exception>
    public static string? AddPropsImport(string propsPath, string securityDirectory)
    {
        var text = File.Exists(propsPath) ? File.ReadAllText(propsPath) : "<Project>\n</Project>\n";

        var xml = new XmlDocument { PreserveWhitespace = true };
        xml.LoadXml(text);

        if (xml.DocumentElement is null || xml.DocumentElement.LocalName != "Project")
        {
            throw new InvalidOperationException($"Not an MSBuild Project: {propsPath}");
        }

        var propsFile = Path.Combine(securityDirectory, "SecurityToolkit.props");
        var relative = Path.GetRelativePath(Path.GetDirectoryName(propsPath)!, propsFile).Replace('\\', '/');
        var expectedProject = $"$(MSBuildThisFileDirectory){relative}";

        var existingImports = xml.DocumentElement.ChildNodes.Cast<XmlNode>()
            .Where(n => n.LocalName == "Import" && (n as XmlElement)?.GetAttribute("Label") == "SecurityToolkit")
            .ToList();

        if (existingImports.Count > 0)
        {
            if (existingImports.Count != 1 || ((XmlElement)existingImports[0]).GetAttribute("Project") != expectedProject)
            {
                throw new InvalidOperationException($"Conflicting SecurityToolkit import: {propsPath}");
            }

            return null;
        }

        var node = xml.CreateElement("Import", xml.DocumentElement.NamespaceURI);
        node.SetAttribute("Label", "SecurityToolkit");
        node.SetAttribute("Project", expectedProject);
        node.SetAttribute(
            "Condition",
            $"'$(SecurityToolkitEnabled)' == 'true' and Exists('{expectedProject}')");

        using var stringWriter = new StringWriter();
        using var xmlWriter = new XmlTextWriter(stringWriter);
        node.WriteTo(xmlWriter);
        var import = stringWriter.ToString();

        var lastIndex = text.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (lastIndex < 0)
        {
            // Self-closing or namespaced Project is serialized only when literal insertion is impossible.
            xml.DocumentElement.AppendChild(node);
            return xml.OuterXml + "\n";
        }

        return text.Insert(lastIndex, $"  {import}\n");
    }
}
