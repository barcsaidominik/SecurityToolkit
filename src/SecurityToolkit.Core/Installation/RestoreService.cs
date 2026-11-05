using System.Security.Cryptography;

namespace SecurityToolkit.Core.Installation;

/// <summary>Outcome of a single restore attempt (whether real or dry-run).</summary>
/// <param name="RestoredPaths">Project-relative paths that were (or, in dry-run, would be) restored, in restore order.</param>
public sealed record RestoreOutcome(IReadOnlyList<string> RestoredPaths);

/// <summary>
/// Reverts one install transaction using its manifest.json. Critical safety property: every entry is
/// preflighted against its recorded post-install hash <em>before any file is touched</em> — if
/// anything has changed since installation (a later manual edit, a newer install, etc.), the
/// whole restore is refused rather than silently discarding that later work.
/// </summary>
public sealed class RestoreService
{
    /// <summary>
    /// Restores every change in <paramref name="manifest"/> under <paramref name="root"/>, in
    /// reverse order (undoing the most recent write first), after verifying every touched file
    /// still has exactly the hash recorded at install time.
    /// </summary>
    /// <param name="root">Project root the manifest's paths are relative to.</param>
    /// <param name="manifest">A parsed transaction manifest (see <see cref="TransactionManifest.Parse"/>).</param>
    /// <param name="dryRun">When true, only reports what would be restored; nothing is written/deleted.</param>
    /// <exception cref="InvalidOperationException">
    /// Any entry's current file is missing/modified since installation, or its backup copy is
    /// missing. No file is modified when this is thrown (full preflight before any mutation).
    /// </exception>
    public RestoreOutcome Restore(string root, TransactionManifest manifest, bool dryRun = false)
    {
        // Preflight every entry before touching anything, to avoid a partial conflict restore.
        foreach (var change in manifest.Changes)
        {
            var path = ResolveProjectPath(root, change.Path);

            if (change.Deleted)
            {
                if (File.Exists(path))
                {
                    throw new InvalidOperationException(
                        $"Recreated since installation: {change.Path}. Restore refuses to overwrite later changes.");
                }

                if (!File.Exists(change.Backup))
                {
                    throw new InvalidOperationException($"Missing backup: {change.Path}");
                }

                continue;
            }

            if (!File.Exists(path) || !string.Equals(ComputeSha256Hex(path), change.NewHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Modified/missing since installation: {change.Path}. Restore refuses to overwrite later changes.");
            }

            if (change.Existed && !File.Exists(change.Backup))
            {
                throw new InvalidOperationException($"Missing backup: {change.Path}");
            }
        }

        var ordered = manifest.Changes.Reverse().ToList();
        var restoredPaths = new List<string>();

        foreach (var change in ordered)
        {
            var path = ResolveProjectPath(root, change.Path);
            restoredPaths.Add(change.Path);

            if (dryRun)
            {
                continue;
            }

            if (change.Deleted)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(change.Backup, path, overwrite: false);
            }
            else if (change.Existed)
            {
                File.Copy(change.Backup, path, overwrite: true);
            }
            else
            {
                File.Delete(path);
            }
        }

        if (!dryRun)
        {
            RefreshInstallStateHashes(root);
        }

        return new RestoreOutcome(restoredPaths);
    }

    /// <summary>
    /// After a real (non-dry-run) restore, recomputes/prunes install-state.json's hash map so a
    /// later install's manual-edit detection reflects the now-restored file content.
    /// </summary>
    private static void RefreshInstallStateHashes(string root)
    {
        var statePath = Path.Combine(root, ".security", "install-state.json");
        if (!File.Exists(statePath))
        {
            return;
        }

        // install-state.json isn't security.lock.json's schema, so parse it directly instead.
        var json = File.ReadAllText(statePath);
        var parsed = SecurityToolkit.Core.Scanners.ScannerSupport.ParseJsonObject(json);

        if (parsed.TryGetValue("hashes", out var hashesValue) && hashesValue is IDictionary<string, object?> hashes)
        {
            var updated = new Dictionary<string, object?>();
            foreach (var (relative, _) in hashes)
            {
                var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                {
                    updated[relative] = ComputeSha256Hex(full);
                }
            }

            parsed["hashes"] = updated;
        }

        ToolkitJson.WriteFile(statePath, parsed);
    }

    private static string ResolveProjectPath(string root, string relative)
        => ToolkitPaths.GetToolkitPath(root, relative, allowMissing: true);

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
