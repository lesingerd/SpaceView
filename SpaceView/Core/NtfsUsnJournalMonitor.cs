using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceView.Native;

namespace SpaceView.Core;

public sealed class UsnMonitorEventArgs : EventArgs
{
    public int BatchEventsProcessed { get; init; }
    public long TotalEventsProcessed { get; init; }
    public string LastChangedItem { get; init; } = string.Empty;
    public bool IsUsnJournalMode { get; init; }

    /// <summary>
    /// Store nodes this batch created, changed, moved (the node and its old parent), deleted or unlinked. A deleted
    /// node keeps its parent link, so its chain can still be walked. Capped at <see cref="FileSystemStore.MaxActivityNodes"/>.
    /// </summary>
    public IReadOnlyCollection<int> ChangedNodes { get; init; } = Array.Empty<int>();
}

public enum UsnMonitorStatusKind
{
    /// <summary>The monitor could not use the USN journal and switched to the FileSystemWatcher fallback.</summary>
    FellBackToWatcher,
    /// <summary>Something went wrong; live data may be incomplete until a rescan.</summary>
    Fault
}

public sealed class UsnMonitorStatusEventArgs : EventArgs
{
    public UsnMonitorStatusKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Real-time NTFS USN Change Journal monitor that reads FSCTL_READ_USN_JOURNAL records
/// and updates the FileSystemStore in place with O(depth) delta propagation.
/// Includes automatic fallback to FileSystemWatcher for non-NTFS volumes or non-admin runs.
/// </summary>
public sealed class NtfsUsnJournalMonitor : IDisposable
{
    private const int MaxFallbackDirectoryEntries = 2000;

    private readonly FileSystemStore _store;
    private ulong _journalId;
    private long _nextUsn;
    private volatile bool _usnActive;
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;
    private FileSystemWatcher? _fallbackWatcher;
    private long _totalEventsProcessed;
    private int _rescanSignalled;
    private int _nameStorageSignalled;
    private string? _lastFaultMessage;
    private bool _faultReportedThisPoll;
    private bool _disposed;
    private UsnJournalBacklog? _backlog;

    // Held while a journal batch is read and applied, so a pause request waits for an in-flight batch
    private readonly object _applyGate = new();
    private int _pauseCount;

    public bool IsUsnJournalActive => _usnActive;
    public long TotalEventsProcessed => Interlocked.Read(ref _totalEventsProcessed);

    public event EventHandler<UsnMonitorEventArgs>? ChangesDetected;

    /// <summary>Raised (on a background thread) when the monitor lost track of changes and the tree must be rebuilt.</summary>
    public event EventHandler<string>? RescanRequired;

    /// <summary>Raised (on a background thread) for faults and for a switch to the fallback watcher.</summary>
    public event EventHandler<UsnMonitorStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Raised (on a background thread) once the changes the journal drain collected during the scan have been applied;
    /// the argument is the number applied.
    /// </summary>
    public event EventHandler<int>? BacklogApplied;

    public NtfsUsnJournalMonitor(
        FileSystemStore store,
        bool usnReady,
        ulong journalId,
        long startUsn)
    {
        _store = store;
        _journalId = journalId;
        _nextUsn = startUsn;
        _usnActive = usnReady && journalId != 0;
    }

    /// <summary>
    /// Hands over what the journal drain collected while the tree was being scanned (call before <see cref="Start"/>).
    /// The monitor applies those changes first, through the normal batch path, then continues from the drain's position.
    /// If the drain lost changes, the monitor still continues from there but signals <see cref="RescanRequired"/> at once.
    /// </summary>
    internal void SetBacklog(UsnJournalBacklog backlog)
    {
        _backlog = backlog;
        if (backlog.JournalId != 0)
            _journalId = backlog.JournalId;
        _nextUsn = backlog.NextUsn;
    }

    public void Start()
    {
        if (_usnActive)
        {
            _monitorTask = Task.Run(() => RunUsnJournalLoopAsync(_cts.Token));
        }
        else
        {
            StartFallbackWatcher();
        }
    }

    /// <summary>
    /// Stops applying USN journal records until the returned scope is disposed (dispose is idempotent and safe
    /// after the monitor itself was disposed). Returns once any batch already being applied has finished, so
    /// everything applied before the pause is visible to a scan started afterwards. While paused nothing is
    /// read and <c>_nextUsn</c> does not move: the records of the pause stay in the journal and are applied,
    /// in order, after the resume. That replay is what keeps changes made during a branch scan from being lost
    /// when the scanned subtree replaces the old one. It is idempotent because updates are keyed by FRN, sizes
    /// are read fresh (absolute), a create/update for a node that already matches changes nothing, and a
    /// delete only removes a node whose FRN (or name, for an unmapped FRN) still matches.
    /// If the journal wraps meanwhile, the first read after the resume fails and RescanRequired is raised as usual.
    /// In FileSystemWatcher mode nothing is paused (the watcher applies events immediately), so changes made
    /// during the scan can be lost until the next rescan.
    /// </summary>
    public IDisposable PauseApplyingChanges()
    {
        lock (_applyGate)
        {
            _pauseCount++;
        }
        return new PauseScope(this);
    }

    private void ResumeApplying()
    {
        lock (_applyGate)
        {
            if (_pauseCount > 0)
                _pauseCount--;
        }
    }

    private sealed class PauseScope : IDisposable
    {
        private NtfsUsnJournalMonitor? _owner;

        public PauseScope(NtfsUsnJournalMonitor owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ResumeApplying();
    }

    private async Task RunUsnJournalLoopAsync(CancellationToken ct)
    {
        try
        {
            string volumeDevicePath = @"\\.\" + _store.RootPath.TrimEnd('\\');
            using SafeFileHandle hVolume = NtfsNative.CreateFileW(
                volumeDevicePath,
                NtfsNative.GENERIC_READ,
                NtfsNative.FILE_SHARE_ALL,
                IntPtr.Zero,
                NtfsNative.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            int openError = hVolume.IsInvalid ? Marshal.GetLastWin32Error() : 0;

            // What the drain collected during the scan goes in first; without a usable volume handle the nodes are
            // still found by path
            UsnJournalBacklog? backlog = Interlocked.Exchange(ref _backlog, null);
            if (backlog != null)
                ApplyBacklog(hVolume, backlog, ct);

            if (hVolume.IsInvalid)
            {
                SwitchToFallback($"Could not open the volume for USN journal access (Win32 error {openError})");
                return;
            }

            IntPtr bufferPtr = UsnJournalReader.AllocateAlignedBuffer();
            try
            {
                var batch = new UsnBatch();

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(400, ct).ConfigureAwait(false);

                    // One bad batch must not silently kill the monitor: report it and keep polling
                    try
                    {
                        lock (_applyGate)
                        {
                            if (_pauseCount > 0)
                                continue;

                            _faultReportedThisPoll = false;
                            PollAndApplyUsnBatch(hVolume, bufferPtr, batch, ct);
                            if (!_faultReportedThisPoll)
                                _lastFaultMessage = null;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        ReportFault($"USN monitor error: {ex.Message}");
                    }
                }
            }
            finally
            {
                UsnJournalReader.FreeAlignedBuffer(bufferPtr);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            ReportFault($"USN monitor stopped: {ex.Message}");
        }
    }

    private void PollAndApplyUsnBatch(
        SafeFileHandle hVolume,
        IntPtr bufferPtr,
        UsnBatch batch,
        CancellationToken ct)
    {
        batch.Clear();

        while (!ct.IsCancellationRequested)
        {
            UsnChunkResult chunk = UsnJournalReader.ReadChunk(hVolume, bufferPtr, _journalId, ref _nextUsn, batch, out int readError);
            if (chunk == UsnChunkResult.Failed)
            {
                HandleJournalReadFailure(hVolume, readError);
                break;
            }

            // A successful read means the journal position is valid again
            Volatile.Write(ref _rescanSignalled, 0);

            if (chunk == UsnChunkResult.End)
                break;

            // Cap batch size so UI updates stay responsive during heavy disk writes
            if (batch.Items.Count >= 2000)
                break;
        }

        if (batch.Items.Count == 0)
            return;

        ApplyAndReport(hVolume, batch, ct);
    }

    /// <summary>Applies a batch to the store and tells listeners what changed.</summary>
    private int ApplyAndReport(SafeFileHandle hVolume, UsnBatch batch, CancellationToken ct)
    {
        int appliedCount = ApplyBatch(hVolume, batch, ct);
        CheckNameStorage();
        int[] changedNodes = _store.TakeActivity();

        if (appliedCount > 0)
        {
            long total = Interlocked.Add(ref _totalEventsProcessed, appliedCount);
            ChangesDetected?.Invoke(this, new UsnMonitorEventArgs
            {
                BatchEventsProcessed = appliedCount,
                TotalEventsProcessed = total,
                LastChangedItem = batch.LastItemName,
                IsUsnJournalMode = true,
                ChangedNodes = changedNodes
            });
        }
        return appliedCount;
    }

    /// <summary>
    /// Applies the changes the journal drain collected during the scan, as one batch (so the parent-before-child retry
    /// sees all of them), and then reports a lost-changes failure of the drain, if any. Replaying a record the scan
    /// already reflects changes nothing: see <see cref="UsnJournalDrainer"/>. Returns the number of changes applied.
    /// </summary>
    internal int ApplyBacklog(SafeFileHandle hVolume, UsnJournalBacklog backlog, CancellationToken ct)
    {
        int applied = 0;
        lock (_applyGate)
        {
            if (backlog.Batch.Items.Count > 0)
                applied = ApplyAndReport(hVolume, backlog.Batch, ct);
        }
        backlog.Batch.Clear(); // the records are applied; free the memory

        AppLog.Info($"{_store.RootPath}: journal backlog applied ({applied:N0} changes)" +
                    (backlog.FailureReason != null ? $"; the drain had lost changes: {backlog.FailureReason}" : string.Empty));
        BacklogApplied?.Invoke(this, applied);
        if (backlog.FailureReason != null)
            RaiseRescanRequired(backlog.FailureReason);
        return applied;
    }

    /// <summary>
    /// Applies a batch in USN order. Items whose parent is not known yet (for example a child whose
    /// parent folder is created later in the same batch) are retried until no further progress is made.
    /// </summary>
    private int ApplyBatch(SafeFileHandle hVolume, UsnBatch batch, CancellationToken ct)
    {
        int applied = 0;
        var retry = new List<ulong>();

        foreach (ulong frn in batch.Order)
        {
            if (ct.IsCancellationRequested)
                return applied;

            switch (ApplyBatchItem(hVolume, frn, batch.Items[frn], treatUnmappedParentAsGone: false))
            {
                case UsnApplyResult.Applied:
                    applied++;
                    break;
                case UsnApplyResult.ParentUnmapped:
                    retry.Add(frn);
                    break;
            }
        }

        while (retry.Count > 0 && !ct.IsCancellationRequested)
        {
            var stillUnmapped = new List<ulong>();
            foreach (ulong frn in retry)
            {
                switch (ApplyBatchItem(hVolume, frn, batch.Items[frn], treatUnmappedParentAsGone: false))
                {
                    case UsnApplyResult.Applied:
                        applied++;
                        break;
                    case UsnApplyResult.ParentUnmapped:
                        stillUnmapped.Add(frn);
                        break;
                }
            }

            bool progressed = stillUnmapped.Count < retry.Count;
            retry = stillUnmapped;
            if (!progressed)
                break;
        }

        // The parent never became known: tracked items were moved out of the tree, others are outside it
        foreach (ulong frn in retry)
        {
            if (ct.IsCancellationRequested)
                break;

            if (ApplyBatchItem(hVolume, frn, batch.Items[frn], treatUnmappedParentAsGone: true) == UsnApplyResult.Applied)
                applied++;
        }

        return applied;
    }

    private UsnApplyResult ApplyBatchItem(
        SafeFileHandle hVolume,
        ulong fileId,
        UsnBatchItem item,
        bool treatUnmappedParentAsGone)
    {
        try
        {
            bool isDir = (item.Attrs & NtfsNative.FILE_ATTRIBUTE_DIRECTORY) != 0;
            bool isReparse = (item.Attrs & NtfsNative.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

            // Delete wins over every other reason combined for this FRN. A skipped junction/symlink was never
            // added, so deleting it is a no-op.
            if ((item.CombinedReason & NtfsNative.USN_REASON_FILE_DELETE) != 0)
            {
                return _store.ApplyUsnDelete(fileId, item.ParentFrn, item.Name.AsSpan())
                    ? UsnApplyResult.Applied
                    : UsnApplyResult.Ignored;
            }

            // A name of a hard-linked file was added or removed. The record names the link it concerns; if that name
            // is gone from disk it is dropped from the tree (the file itself lives on), otherwise it is applied
            // below like any update. Several link changes of one FRN in a poll collapse into the last record.
            bool linkChanged = !isDir && (item.CombinedReason & NtfsNative.USN_REASON_HARD_LINK_CHANGE) != 0;
            if (linkChanged && !LinkNameExistsOnDisk(fileId, item))
            {
                return _store.ApplyUsnLinkRemoved(fileId, item.ParentFrn, item.Name.AsSpan())
                    ? UsnApplyResult.Applied
                    : UsnApplyResult.Ignored;
            }

            // Query by FRN first, then by path. Opened without following reparse points, so nothing is recalled.
            using SafeFileHandle hFile = OpenNodeForQuery(hVolume, fileId, item);

            // The scan skips directory symlinks/junctions only; other reparse directories (cloud placeholders) are tracked
            if (isDir && isReparse &&
                (!NtfsNative.TryGetReparseTag(hFile, out uint reparseTag) || NtfsNative.IsReparseTagNameSurrogate(reparseTag)))
            {
                return UsnApplyResult.Ignored;
            }

            long fileSize = 0;
            bool sizeKnown = true;
            if (!isDir)
                sizeKnown = NtfsNative.TryGetAllocationSize(hFile, out fileSize);

            // Only a rename record moves a tracked node; a record under another parent for a known FRN is
            // otherwise one more name of a hard-linked file
            bool isRename = (item.CombinedReason & NtfsNative.USN_REASON_RENAME_NEW_NAME) != 0;
            return _store.ApplyUsnCreateOrUpdate(
                fileId, item.ParentFrn, item.Name.AsSpan(), fileSize, isDir, sizeKnown, treatUnmappedParentAsGone, isRename);
        }
        catch (Exception ex)
        {
            ReportFault($"Failed to apply change to '{item.Name}': {ex.Message}. Live totals may be out of date until the next rescan.");
            return UsnApplyResult.Ignored;
        }
    }

    /// <summary>
    /// Whether the file name in a hard-link-change record still exists with this FRN. True when it cannot be
    /// checked (parent outside the tracked tree), so nothing is dropped on a guess.
    /// </summary>
    private bool LinkNameExistsOnDisk(ulong fileId, UsnBatchItem item)
    {
        if (!_store.TryGetNodeByFileId(item.ParentFrn, out int parentNodeId))
            return true;

        try
        {
            using SafeFileHandle h = NtfsNative.OpenForQuery(Path.Combine(_store.GetFullPath(parentNodeId), item.Name));
            if (h.IsInvalid)
                return false;
            return !NtfsNative.GetFileInformationByHandle(h, out var info) || info.FileReferenceNumber == fileId;
        }
        catch
        {
            return true;
        }
    }

    private void HandleJournalReadFailure(SafeFileHandle hVolume, int readError)
    {
        if (UsnJournalReader.IsPositionLost(hVolume, readError, ref _journalId, ref _nextUsn, out string lostMessage))
        {
            RaiseRescanRequired(lostMessage);
            return;
        }

        ReportFault($"Reading the USN journal failed (Win32 error {readError}); will keep retrying");
    }

    /// <summary>The store's name storage is nearly full (see <see cref="FileSystemStore.NameStorageNearlyFull"/>): ask for a rescan, once.</summary>
    internal void CheckNameStorage()
    {
        if (!_store.NameStorageNearlyFull || Interlocked.Exchange(ref _nameStorageSignalled, 1) == 1)
            return;

        const string reason = "name storage is nearly full; rescan to compact";
        AppLog.Warn($"{_store.RootPath}: {reason} ({_store.NameSlabCount:N0} slabs, {_store.LiveNodeCount:N0} live nodes)");
        RescanRequired?.Invoke(this, reason);
    }

    private void RaiseRescanRequired(string reason)
    {
        // Once per episode: stays latched until a journal read succeeds again
        if (Interlocked.Exchange(ref _rescanSignalled, 1) == 1)
            return;

        AppLog.Warn($"{_store.RootPath}: rescan required: {reason}");
        RescanRequired?.Invoke(this, reason);
    }

    private void ReportFault(string message)
    {
        _faultReportedThisPoll = true;
        if (string.Equals(_lastFaultMessage, message, StringComparison.Ordinal))
            return;

        _lastFaultMessage = message;
        AppLog.Warn($"{_store.RootPath}: monitor fault: {message}");
        StatusChanged?.Invoke(this, new UsnMonitorStatusEventArgs
        {
            Kind = UsnMonitorStatusKind.Fault,
            Message = message
        });
    }

    private void SwitchToFallback(string reason)
    {
        _usnActive = false;
        AppLog.Warn($"{_store.RootPath}: fell back to the file system watcher: {reason}");
        StartFallbackWatcher();
        StatusChanged?.Invoke(this, new UsnMonitorStatusEventArgs
        {
            Kind = UsnMonitorStatusKind.FellBackToWatcher,
            Message = reason
        });
    }

    /// <summary>Opens the node by file ID, or by path through its known parent; the handle is invalid if both fail.</summary>
    private SafeFileHandle OpenNodeForQuery(SafeFileHandle hVolume, ulong fileId, UsnBatchItem item)
    {
        SafeFileHandle hFile = OpenFileByIdForQuery(hVolume, fileId);
        if (!hFile.IsInvalid)
            return hFile;

        hFile.Dispose();
        if (_store.TryGetNodeByFileId(item.ParentFrn, out int parentNodeId))
        {
            try
            {
                return NtfsNative.OpenForQuery(Path.Combine(_store.GetFullPath(parentNodeId), item.Name));
            }
            catch
            {
                // Ignore transient file access errors
            }
        }
        return new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
    }

    private static unsafe SafeFileHandle OpenFileByIdForQuery(SafeFileHandle hVolume, ulong fileId)
    {
        var desc = new NtfsNative.FILE_ID_DESCRIPTOR
        {
            dwSize = (uint)sizeof(NtfsNative.FILE_ID_DESCRIPTOR),
            Type = 0, // FileIdType
            FileId = fileId
        };

        // FILE_FLAG_OPEN_REPARSE_POINT: query the node itself, never follow a link and never recall a cloud file
        return NtfsNative.OpenFileById(
            hVolume,
            ref desc,
            NtfsNative.FILE_READ_ATTRIBUTES,
            NtfsNative.FILE_SHARE_ALL,
            IntPtr.Zero,
            NtfsNative.FILE_FLAG_BACKUP_SEMANTICS | NtfsNative.FILE_FLAG_OPEN_REPARSE_POINT);
    }

    private void StartFallbackWatcher()
    {
        try
        {
            _fallbackWatcher = new FileSystemWatcher(_store.RootPath)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
            };

            _fallbackWatcher.Created += (_, e) => ApplyFallbackChange(e.FullPath, isDelete: false);
            _fallbackWatcher.Changed += (_, e) => ApplyFallbackChange(e.FullPath, isDelete: false);
            _fallbackWatcher.Deleted += (_, e) => ApplyFallbackChange(e.FullPath, isDelete: true);
            _fallbackWatcher.Renamed += (_, e) =>
            {
                // A rename keeps the FRN, so updating the new path moves/renames the existing node (and
                // its subtree). Deleting the old path first would tombstone that whole subtree.
                if (GetFileIdForPath(e.FullPath) == 0)
                    ApplyFallbackChange(e.OldFullPath, isDelete: true);
                ApplyFallbackChange(e.FullPath, isDelete: false, isRename: true);
            };
            _fallbackWatcher.Error += (_, e) =>
            {
                Exception ex = e.GetException();
                if (ex is InternalBufferOverflowException)
                    RaiseRescanRequired("The file system watcher buffer overflowed and changes were lost");
                else
                    ReportFault($"File system watcher error: {ex.Message}");
            };
            _fallbackWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            ReportFault($"Live monitoring is unavailable for this volume: {ex.Message}");
        }
    }

    private void ApplyFallbackChange(string fullPath, bool isDelete, bool isRename = false)
    {
        try
        {
            int applied = HandleFallbackEvent(fullPath, isDelete, expandNewDirectory: true, isRename);
            CheckNameStorage();
            int[] changedNodes = _store.TakeActivity();
            if (applied > 0)
            {
                long total = Interlocked.Add(ref _totalEventsProcessed, applied);
                ChangesDetected?.Invoke(this, new UsnMonitorEventArgs
                {
                    BatchEventsProcessed = applied,
                    TotalEventsProcessed = total,
                    LastChangedItem = Path.GetFileName(fullPath),
                    IsUsnJournalMode = false,
                    ChangedNodes = changedNodes
                });
            }
        }
        catch
        {
            // Ignore transient errors
        }
    }

    /// <summary>Applies one watcher event to the store and returns the number of store changes made.</summary>
    private int HandleFallbackEvent(string fullPath, bool isDelete, bool expandNewDirectory, bool isRename = false)
    {
        string? dirPath = Path.GetDirectoryName(fullPath);
        string name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(dirPath) || string.IsNullOrEmpty(name))
            return 0;

        ulong parentId = GetFileIdForPath(dirPath);
        if (parentId == 0)
            return 0;

        if (isDelete)
        {
            return _store.ApplyUsnDelete(0, parentId, name.AsSpan()) ? 1 : 0;
        }

        // One handle gives identity, kind, reparse tag and size on disk; it does not follow links or recall cloud files
        using SafeFileHandle h = NtfsNative.OpenForQuery(fullPath);
        if (h.IsInvalid || !NtfsNative.GetFileInformationByHandle(h, out var info))
            return 0; // already gone (or unreadable); a Deleted event will follow

        ulong fileId = info.FileReferenceNumber;
        if (fileId == 0)
            return 0;

        bool isDir = (info.FileAttributes & NtfsNative.FILE_ATTRIBUTE_DIRECTORY) != 0;
        bool isReparse = (info.FileAttributes & NtfsNative.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

        // Same rule as the scan: directory symlinks/junctions are not tracked
        if (isDir && isReparse &&
            (!NtfsNative.TryGetReparseTag(h, out uint reparseTag) || NtfsNative.IsReparseTagNameSurrogate(reparseTag)))
        {
            return 0;
        }

        long size = 0;
        bool sizeKnown = true;
        if (!isDir)
            sizeKnown = NtfsNative.TryGetAllocationSize(h, out size);

        bool wasKnown = _store.TryGetNodeByFileId(fileId, out _);
        if (_store.ApplyUsnCreateOrUpdate(fileId, parentId, name.AsSpan(), size, isDir, sizeKnown, isRename: isRename) != UsnApplyResult.Applied)
            return 0;

        int applied = 1;
        if (isDir && expandNewDirectory && !wasKnown)
        {
            // A junction is created as a plain directory first and turned into a link a moment later, so the
            // check above can have seen the empty directory: take it out again rather than walk through the link.
            if (IsNameSurrogateDirectory(fullPath))
            {
                _store.ApplyUsnDelete(fileId, parentId, name.AsSpan());
                return 0;
            }
            applied += AddNewDirectoryContents(fullPath);
        }
        return applied;
    }

    /// <summary>
    /// A directory created or moved into the tree raises a single watcher event for the directory itself.
    /// Cheap, bounded best effort: add what is inside it now (directory symlinks/junctions are skipped, as in the scan).
    /// </summary>
    private int AddNewDirectoryContents(string dirPath)
    {
        int applied = 0;
        int visited = 0;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0
        };

        // Walk by hand so a directory symlink/junction is never entered; HandleFallbackEvent applies the same
        // tag check and returns 0 for them.
        var pending = new Stack<string>();
        pending.Push(dirPath);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            try
            {
                var entries = new FileSystemEnumerable<(string Path, bool IsDirectory)>(
                    current,
                    (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory),
                    options);

                foreach ((string path, bool isDirectory) in entries)
                {
                    if (++visited > MaxFallbackDirectoryEntries)
                    {
                        ReportFault($"'{Path.GetFileName(dirPath)}' contains more than {MaxFallbackDirectoryEntries:N0} items; the rest are not tracked until the next rescan");
                        return applied;
                    }

                    applied += HandleFallbackEvent(path, isDelete: false, expandNewDirectory: false);
                    if (isDirectory && !IsNameSurrogateDirectory(path))
                        pending.Push(path);
                }
            }
            catch
            {
                // Ignore transient errors (directory may already be gone)
            }
        }
        return applied;
    }

    /// <summary>True for a directory symlink or junction (also when it cannot be queried, to stay on the safe side).</summary>
    private static bool IsNameSurrogateDirectory(string path)
    {
        using SafeFileHandle h = NtfsNative.OpenForQuery(path);
        if (h.IsInvalid || !NtfsNative.GetFileInformationByHandle(h, out var info))
            return true;

        return (info.FileAttributes & NtfsNative.FILE_ATTRIBUTE_REPARSE_POINT) != 0 &&
               (!NtfsNative.TryGetReparseTag(h, out uint tag) || NtfsNative.IsReparseTagNameSurrogate(tag));
    }

    private static ulong GetFileIdForPath(string path)
    {
        using SafeFileHandle h = NtfsNative.OpenForQuery(path);

        if (!h.IsInvalid && NtfsNative.GetFileInformationByHandle(h, out var info))
        {
            return info.FileReferenceNumber;
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _fallbackWatcher?.Dispose();
        _cts.Dispose();
    }
}
