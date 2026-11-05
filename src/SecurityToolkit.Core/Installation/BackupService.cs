using System.Security.Cryptography;
using System.Text;

namespace SecurityToolkit.Core.Installation;

/// <summary>
/// Tracks every project-file write performed during one install transaction, backing up the
/// prior content first and recording each change for the transaction manifest
/// (".security/backups/&lt;runId&gt;/manifest.json") and ".security/install-state.json".
/// </summary>
/// <remarks>
/// One instance is scoped to a single install run (one <paramref name="runId"/>/backup directory).
/// Not thread-safe; installs run their file writes sequentially.
/// </remarks>
public sealed class BackupService
{
    private readonly string _root;
    private readonly string _securityDirectory;
    private readonly string _runId;
    private readonly bool _checkOnly;
    private readonly List<TransactionChange> _changes = new();
    private readonly List<DriftEntry> _driftEntries = new();
    private readonly HashSet<string> _touchedRelativePaths = new(StringComparer.Ordinal);
    private readonly List<string> _skippedOwnedFiles = new();
    private readonly List<string> _keptRetiredFiles = new();
    private readonly IDictionary<string, string> _priorHashes;
    private readonly bool _hasPriorInstall;

    /// <param name="root">Project root; every managed path must resolve within it (same rule as <see cref="ToolkitPaths.GetToolkitPath"/>).</param>
    /// <param name="securityDirectory">Absolute path to ".security" under <paramref name="root"/>.</param>
    /// <param name="runId">Transaction id, e.g. "yyyyMMdd-HHmmss-fff".</param>
    /// <param name="priorHashes">
    /// The previous install-state.json's "hashes" map (relative path -&gt; SHA-256 hex), used to detect
    /// manual edits to toolkit-owned files before overwriting them. <c>null</c> when this checkout has
    /// no install-state.json at all (a fresh clone or a new machine): there is no record to detect a
    /// manual edit against, so existing owned files are the committed baseline and get overwritten
    /// (the change shows up in git). An empty map means a prior install that recorded no hashes.
    /// </param>
    /// <param name="checkOnly">
    /// When true, every <see cref="SaveManagedText"/> call on this instance reports into
    /// <see cref="DriftEntries"/> instead of writing — see <see cref="InstallOptions.CheckOnly"/>.
    /// </param>
    public BackupService(string root, string securityDirectory, string runId, IDictionary<string, string>? priorHashes = null, bool checkOnly = false)
    {
        _root = root;
        _securityDirectory = securityDirectory;
        _runId = runId;
        _priorHashes = priorHashes ?? new Dictionary<string, string>();
        _hasPriorInstall = priorHashes is not null;
        _checkOnly = checkOnly;
    }

    /// <summary>
    /// False on a checkout without install-state.json (see the constructor's <c>priorHashes</c>):
    /// owned-file manual-edit detection is off, since there is nothing to compare against.
    /// </summary>
    public bool HasPriorInstall => _hasPriorInstall;

    /// <summary>All changes recorded so far in this transaction, in write order.</summary>
    public IReadOnlyList<TransactionChange> Changes => _changes;

    /// <summary>
    /// Every <see cref="SaveManagedText"/> call made with <c>checkOnly: true</c> so far, in call
    /// order. Empty unless the service is being used to answer "would this install change
    /// anything?" rather than to actually perform one.
    /// </summary>
    public IReadOnlyList<DriftEntry> DriftEntries => _driftEntries;

    /// <summary>
    /// Relative paths (forward-slash, project-root-relative) touched so far, for install-state.json's
    /// hash refresh.
    /// </summary>
    public IReadOnlyCollection<string> TouchedRelativePaths => _touchedRelativePaths;

    /// <summary>
    /// Relative paths of owned files this run refused to overwrite because their on-disk content
    /// no longer matches the last recorded hash (a manual edit), in call order. Each entry means
    /// that one file was left untouched — every OTHER file/step in this install run still
    /// proceeded normally; see <see cref="SaveManagedText"/>'s <c>owned</c> parameter.
    /// </summary>
    public IReadOnlyList<string> SkippedOwnedFiles => _skippedOwnedFiles;

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> (UTF-8, no BOM), backing up any
    /// existing content first and recording the change. No-ops (but still marks the path as
    /// touched) when the file already has identical content.
    /// </summary>
    /// <param name="path">Absolute path under <paramref name="root"/>.</param>
    /// <param name="text">New file content.</param>
    /// <param name="owned">
    /// When true, an existing file must match its last recorded hash in <paramref name="priorHashes"/>
    /// (a toolkit-managed file with no untracked manual edits) or the write is skipped. A mismatch
    /// does not abort the run; it's recorded in <see cref="SkippedOwnedFiles"/> instead, so the rest
    /// of the install still proceeds.
    /// </param>
    /// <param name="dryRun">When true, nothing is written/backed up; only reported via the return value.</param>
    /// <param name="lf">Write LF instead of CRLF line endings (shell scripts, see <see cref="ToolkitFile.WriteAllText"/>).</param>
    /// <remarks>
    /// When this instance was constructed with <c>checkOnly: true</c>, this never writes and
    /// instead records one <see cref="DriftEntry"/> describing what an actual install would do —
    /// used to answer "would this change anything?"
    /// without performing or blocking on that change.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><paramref name="path"/> escapes <paramref name="root"/>.</exception>
    public bool SaveManagedText(string path, string text, bool owned = false, bool dryRun = false, bool lf = false)
    {
        text = lf ? ToolkitFile.NormalizeToLf(text) : ToolkitFile.NormalizeLineEndings(text);
        var relative = ToProjectRelative(path);
        var existed = File.Exists(path);
        var oldText = existed ? File.ReadAllText(path) : string.Empty;

        if (existed && string.Equals(oldText, text, StringComparison.Ordinal))
        {
            _touchedRelativePaths.Add(relative);
            if (_checkOnly)
            {
                _driftEntries.Add(new DriftEntry(relative, DriftStatus.Unchanged));
            }

            return false;
        }

        if (owned && existed && _hasPriorInstall)
        {
            if (!_priorHashes.TryGetValue(relative, out var expected) || !ToolkitFile.HashMatchesIgnoringLineEndings(path, expected))
            {
                if (_checkOnly)
                {
                    _driftEntries.Add(new DriftEntry(relative, DriftStatus.ManualChangesDetected));
                    return false;
                }

                // A manual edit to one owned file must not abort the rest of the install (tool
                // provisioning, git hook, rule downloads, ...) — it only means this one file is
                // left as-is; the caller surfaces _skippedOwnedFiles as a pending-action message.
                _skippedOwnedFiles.Add(relative);
                return false;
            }
        }

        if (_checkOnly)
        {
            _driftEntries.Add(new DriftEntry(relative, existed ? DriftStatus.WouldUpdate : DriftStatus.WouldCreate));
            return false;
        }

        if (dryRun)
        {
            return true;
        }

        var backup = string.Empty;
        if (existed)
        {
            backup = Path.Combine(_securityDirectory, "backups", _runId, "files", relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(path, backup, overwrite: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ToolkitFile.WriteAllText(path, text, lf);

        _touchedRelativePaths.Add(relative);
        _changes.Add(new TransactionChange(relative, existed, backup, ComputeSha256Hex(path)));
        return true;
    }

    /// <summary>
    /// Deletes a toolkit-owned file that the current toolkit no longer generates, backing it up
    /// first and recording the deletion in the manifest (restore brings it back). Follows the
    /// owned-file rule of <see cref="SaveManagedText"/>: a file whose content no longer matches
    /// its last recorded hash was edited by hand, so it is kept and listed in
    /// <see cref="KeptRetiredFiles"/>. Without a prior install (fresh clone) the committed file is
    /// the baseline and is deleted (the deletion shows up in git).
    /// </summary>
    /// <returns>True when the file was (or, in check mode, would be) deleted.</returns>
    public bool DeleteRetiredFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var relative = ToProjectRelative(path);
        if (_hasPriorInstall
            && (!_priorHashes.TryGetValue(relative, out var expected) || !ToolkitFile.HashMatchesIgnoringLineEndings(path, expected)))
        {
            if (_checkOnly)
            {
                _driftEntries.Add(new DriftEntry(relative, DriftStatus.ManualChangesDetected));
            }
            else
            {
                _keptRetiredFiles.Add(relative);
            }

            return false;
        }

        if (_checkOnly)
        {
            _driftEntries.Add(new DriftEntry(relative, DriftStatus.WouldDelete));
            return true;
        }

        var backup = Path.Combine(_securityDirectory, "backups", _runId, "files", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(path, backup, overwrite: true);
        File.Delete(path);

        _changes.Add(new TransactionChange(relative, Existed: true, backup, NewHash: string.Empty, Deleted: true));
        return true;
    }

    /// <summary>
    /// Relative paths of retired files that <see cref="DeleteRetiredFile"/> kept because they were
    /// edited by hand, in call order.
    /// </summary>
    public IReadOnlyList<string> KeptRetiredFiles => _keptRetiredFiles;

    /// <summary>
    /// Writes the transaction's manifest.json and updates install-state.json with the final,
    /// post-write hashes of every touched/previously-known file. Call this exactly once, after every <see cref="SaveManagedText"/> call for the run.
    /// </summary>
    /// <param name="dryRun">When true, nothing is written to disk.</param>
    public void Finish(bool dryRun = false)
    {
        if (dryRun)
        {
            return;
        }

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (relative, hash) in _priorHashes)
        {
            var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                hashes[relative] = hash;
            }
        }

        foreach (var relative in _touchedRelativePaths)
        {
            var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                hashes[relative] = ComputeSha256Hex(full);
            }
        }

        foreach (var change in _changes)
        {
            var full = Path.Combine(_root, change.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                hashes[change.Path] = ComputeSha256Hex(full);
            }
        }

        var manifestDirectory = Path.Combine(_securityDirectory, "backups", _runId);
        Directory.CreateDirectory(manifestDirectory);

        var manifest = new TransactionManifest(TransactionManifest.CurrentSchemaVersion, _changes);
        ToolkitJson.WriteFile(Path.Combine(manifestDirectory, "manifest.json"), manifest.ToJsonTree());

        var installState = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1L,
            ["hashes"] = hashes.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value),
            ["lastInstall"] = _runId,
        };
        ToolkitJson.WriteFile(Path.Combine(_securityDirectory, "install-state.json"), installState);
    }

    /// <summary>Path to this transaction's manifest.json (whether or not it has been written yet).</summary>
    public string ManifestPath => Path.Combine(_securityDirectory, "backups", _runId, "manifest.json");

    private string ToProjectRelative(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_root, full).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing write outside project: {path}");
        }

        return relative;
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}

/// <summary>What a real (non-check) <see cref="BackupService.SaveManagedText"/> call would do to one file.</summary>
public enum DriftStatus
{
    /// <summary>The file already has the content this install run would write.</summary>
    Unchanged,

    /// <summary>The file exists with different content and would be overwritten.</summary>
    WouldUpdate,

    /// <summary>The file does not exist yet and would be created.</summary>
    WouldCreate,

    /// <summary>A retired, unmodified toolkit file that would be deleted.</summary>
    WouldDelete,

    /// <summary>
    /// An owned file's on-disk content no longer matches its last recorded hash — a real install
    /// would refuse to overwrite it (see <see cref="BackupService.SaveManagedText"/>'s <c>owned</c>
    /// parameter).
    /// </summary>
    ManualChangesDetected,
}

/// <summary>One file's drift status, from a <see cref="BackupService.SaveManagedText"/> call made with <c>checkOnly: true</c>.</summary>
/// <param name="RelativePath">Project-root-relative, forward-slash path.</param>
/// <param name="Status">What a real install run would do to this file.</param>
public sealed record DriftEntry(string RelativePath, DriftStatus Status);
