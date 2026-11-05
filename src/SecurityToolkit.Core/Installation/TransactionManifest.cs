namespace SecurityToolkit.Core.Installation;

/// <summary>
/// One entry of an install transaction manifest, recorded by
/// <see cref="BackupService.SaveManagedText"/> on every write.
/// </summary>
/// <param name="Path">Project-relative path (forward slashes), the manifest key used for restore.</param>
/// <param name="Existed">Whether the file already existed before this transaction touched it.</param>
/// <param name="Backup">
/// Absolute path to the pre-change backup copy, or "" when <paramref name="Existed"/> is false
/// (nothing to restore back to — restore just deletes the file).
/// </param>
/// <param name="NewHash">SHA-256 hex digest of the file's content after the write ("" for a deletion).</param>
/// <param name="Deleted">
/// The transaction deleted the file (a retired toolkit file); restore copies <paramref name="Backup"/> back.
/// </param>
public sealed record TransactionChange(string Path, bool Existed, string Backup, string NewHash, bool Deleted = false);

/// <summary>
/// The backup manifest.json schema: <c>{ schemaVersion: 1, changes: [...] }</c>.
/// </summary>
public sealed record TransactionManifest(int SchemaVersion, IReadOnlyList<TransactionChange> Changes)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Converts to the plain dictionary tree shape <see cref="ToolkitJson"/> serializes.</summary>
    public IDictionary<string, object?> ToJsonTree() => new Dictionary<string, object?>
    {
        ["schemaVersion"] = (long)SchemaVersion,
        ["changes"] = Changes.Select(c =>
        {
            var entry = new Dictionary<string, object?>
            {
                ["path"] = c.Path,
                ["existed"] = c.Existed,
                ["backup"] = c.Backup,
                ["newHash"] = c.NewHash,
            };

            // Written only when set, so manifests without deletions keep their earlier shape.
            if (c.Deleted)
            {
                entry["deleted"] = true;
            }

            return (object?)entry;
        }).ToList(),
    };

    /// <summary>
    /// Parses a manifest previously written via <see cref="ToJsonTree"/>/<see cref="ToolkitJson.WriteFile"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Unsupported schema version, or malformed content.</exception>
    public static TransactionManifest Parse(IDictionary<string, object?> tree)
    {
        var schemaVersion = tree.TryGetValue("schemaVersion", out var sv) ? sv switch
        {
            long l => (int)l,
            int i => i,
            double d => (int)d,
            _ => (int?)null,
        } : null;

        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidOperationException("Unsupported backup manifest.");
        }

        if (!tree.TryGetValue("changes", out var changesValue) || changesValue is not IEnumerable<object?> changesList)
        {
            throw new InvalidOperationException("Manifest has no changes list.");
        }

        var changes = new List<TransactionChange>();
        foreach (var item in changesList)
        {
            if (item is not IDictionary<string, object?> changeDict)
            {
                throw new InvalidOperationException("Malformed manifest change entry.");
            }

            var path = changeDict.TryGetValue("path", out var p) ? p as string : null;
            var existed = changeDict.TryGetValue("existed", out var e) && e is bool eb && eb;
            var backup = changeDict.TryGetValue("backup", out var b) ? b as string ?? string.Empty : string.Empty;
            var newHash = changeDict.TryGetValue("newHash", out var h) ? h as string ?? string.Empty : string.Empty;
            var deleted = changeDict.TryGetValue("deleted", out var d) && d is bool db && db;

            if (path is null)
            {
                throw new InvalidOperationException("Malformed manifest change entry: missing path.");
            }

            changes.Add(new TransactionChange(path, existed, backup, newHash, deleted));
        }

        return new TransactionManifest(schemaVersion.Value, changes);
    }
}
