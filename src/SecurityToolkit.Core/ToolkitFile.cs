using System.Text;

namespace SecurityToolkit.Core;

/// <summary>
/// Writes text files the way every toolkit-managed/generated file (config, lock, props,
/// pipeline YAML, ignore blocks, gitleaks.toml, ...) is expected to look on disk: UTF-8 without a
/// byte-order mark, CRLF line endings. CRLF specifically because these files live in the target
/// project's working tree and are routinely opened in Visual Studio, which otherwise flags every
/// LF-only file as a line-ending mismatch on every checkout. Not used for scan-result artifacts
/// (JSON reports, logs under ".security/results/") — those are gitignored, external-tool output
/// that nobody edits in an IDE, so their native line endings are left alone.
/// </summary>
public static class ToolkitFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Normalizes <paramref name="text"/> to CRLF line endings (any lone LF or existing CRLF becomes CRLF).</summary>
    public static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>Normalizes <paramref name="text"/> to LF line endings.</summary>
    public static string NormalizeToLf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> as UTF-8 (no BOM), CRLF line endings,
    /// or LF when <paramref name="lf"/> is set: shell scripts (the git hook) must stay LF, a CRLF
    /// shebang line fails on Linux with "bad interpreter".
    /// </summary>
    public static void WriteAllText(string path, string text, bool lf = false)
        => File.WriteAllText(path, lf ? NormalizeToLf(text) : NormalizeLineEndings(text), Utf8NoBom);

    /// <summary>
    /// Whether <paramref name="path"/>'s content hashes to <paramref name="expectedSha256Hex"/>,
    /// ignoring line endings: the raw bytes, or the same text with LF or CRLF line endings. Git
    /// line-ending conversion (core.autocrlf / text=auto) rewrites every toolkit-managed file on
    /// checkout, so a raw-byte comparison against the hash recorded at install time reports a
    /// "manual change" for a file nobody edited.
    /// </summary>
    public static bool HashMatchesIgnoringLineEndings(string path, string expectedSha256Hex)
    {
        var bytes = File.ReadAllBytes(path);
        if (Matches(bytes))
        {
            return true;
        }

        var lf = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        return Matches(Utf8NoBom.GetBytes(lf)) || Matches(Utf8NoBom.GetBytes(lf.Replace("\n", "\r\n", StringComparison.Ordinal)));

        bool Matches(byte[] content) => string.Equals(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)), expectedSha256Hex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Async counterpart of <see cref="WriteAllText"/>.</summary>
    public static Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken = default)
        => File.WriteAllTextAsync(path, NormalizeLineEndings(text), Utf8NoBom, cancellationToken);
}
