using System.IO;

namespace SpaceView.Core;

public enum DriveSessionState
{
    /// <summary>Waiting for the scan slot: only one drive is scanned at a time.</summary>
    Queued,
    Scanning,
    /// <summary>Scanned (or never scanned and idle after a failure, see <see cref="DriveSession.ScanError"/>).</summary>
    Ready,
    Failed
}

/// <summary>
/// Identifies a tree row. A NodeId is only unique within one store, so two drives can both have a node 5:
/// every comparison between rows must use the session as well as the node id.
/// </summary>
public readonly record struct RowKey(DriveSession Session, int NodeId);

/// <summary>
/// Everything that belongs to one scanned drive in the tree: its store, its live monitor, the scan state and the
/// numbers of the last scan. The window keeps an ordered list of these (see <see cref="DriveSessionList"/>); every
/// tree row resolves the session it belongs to. Touched from the UI thread only, except <see cref="TryBeginUiRefresh"/>.
/// </summary>
public sealed class DriveSession : IDisposable
{
    private CancellationTokenSource? _scanCts;
    private int _uiRefreshPending;
    private UsnMonitorEventArgs? _latestUsnArgs;
    private readonly object _activityLock = new();
    private HashSet<int> _pendingActivity = new();

    private NtfsUsnJournalMonitor? _monitor;
    private EventHandler<UsnMonitorEventArgs>? _onChanges;
    private EventHandler<string>? _onRescanRequired;
    private EventHandler<UsnMonitorStatusEventArgs>? _onStatus;

    /// <summary>The drive root, e.g. <c>E:\</c> (always with a trailing backslash). The key of the session.</summary>
    public string RootPath { get; }

    public DriveInfo? Drive { get; private set; }
    public string VolumeLabel { get; private set; } = "Local Disk";
    public string DriveFormat { get; private set; } = "Unknown";
    public long TotalSize { get; private set; }
    public long FreeSpace { get; private set; }

    /// <summary>Percent of the volume that is in use (from the volume's own numbers, not from the scan).</summary>
    public double UsedPercentage =>
        TotalSize > 0 ? Math.Clamp((double)(TotalSize - FreeSpace) / TotalSize * 100.0, 0.0, 100.0) : 0.0;

    /// <summary>The scanned tree; null until the first scan has finished. A rescan replaces it when it is done.</summary>
    public FileSystemStore? Store { get; private set; }
    public NtfsUsnJournalMonitor? Monitor => _monitor;

    public DriveSessionState State { get; private set; } = DriveSessionState.Queued;
    public string? ScanError { get; private set; }
    public int ScanProgressItems { get; set; }

    /// <summary>The journal wrapped (or the watcher overflowed): the tree may be wrong until the drive is rescanned.</summary>
    public bool IsStale { get; private set; }
    public string? StaleReason { get; private set; }
    /// <summary>The volume is gone from the system; the last scan is still shown, the monitor is stopped.</summary>
    public bool IsUnavailable { get; private set; }
    /// <summary>A monitor fault message, shown until the next scan.</summary>
    public string? MonitorProblem { get; set; }

    // Numbers of the last finished scan
    public bool IsNtfs { get; private set; }
    public int InaccessibleDirectories { get; private set; }
    public TimeSpan ScanDuration { get; private set; }

    public bool IsDisposed { get; private set; }

    public DriveSession(string rootPath)
    {
        RootPath = NormalizeRoot(rootPath);
    }

    /// <summary>Makes sure the root ends with a backslash.</summary>
    public static string NormalizeRoot(string rootPath) =>
        rootPath.EndsWith('\\') ? rootPath : rootPath + "\\";

    public static bool SameRoot(string a, string b) =>
        string.Equals(NormalizeRoot(a), NormalizeRoot(b), StringComparison.OrdinalIgnoreCase);

    public bool IsBusy => State is DriveSessionState.Queued or DriveSessionState.Scanning;

    public bool HasInaccessibleFolders => InaccessibleDirectories > 0;

    /// <summary>The live monitor is the FileSystemWatcher fallback, which sees less than the USN journal.</summary>
    public bool IsLimitedMonitoring => _monitor != null && !_monitor.IsUsnJournalActive;

    public void UpdateVolume(DriveInfo? drive, string label, string format, long totalSize, long freeSpace)
    {
        Drive = drive ?? Drive;
        VolumeLabel = label;
        DriveFormat = format;
        TotalSize = Math.Max(0, totalSize);
        FreeSpace = Math.Clamp(freeSpace, 0, TotalSize);
    }

    // ------------------------------------------------------------------
    // Scanning
    // ------------------------------------------------------------------

    /// <summary>
    /// Starts a scan: cancels the one that is running or waiting and returns the new token source. The previous tree
    /// (if any) and its monitor stay in place until <see cref="ApplyScanResult"/>.
    /// </summary>
    public CancellationTokenSource BeginScan()
    {
        _scanCts?.Cancel(); // a cancelled source is not disposed: a scan may still hold its token
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        State = DriveSessionState.Queued;
        ScanProgressItems = 0;
        ScanError = null;
        return cts;
    }

    public bool IsCurrentScan(CancellationTokenSource cts) => !IsDisposed && ReferenceEquals(_scanCts, cts) && !cts.IsCancellationRequested;

    public void MarkScanning()
    {
        State = DriveSessionState.Scanning;
    }

    /// <summary>Gives up the current scan without a result: the previous tree (if any) stays and the session is idle again.</summary>
    public void AbandonScan()
    {
        _scanCts?.Cancel();
        State = Store != null ? DriveSessionState.Ready : DriveSessionState.Failed;
    }

    public void Fail(string message)
    {
        State = DriveSessionState.Failed;
        ScanError = message;
    }

    /// <summary>Installs a finished scan. The caller must have detached the old monitor and starts the new one.</summary>
    public void ApplyScanResult(ScanResult result)
    {
        Store = result.Store;
        ClearActivity(); // node ids of the previous store mean nothing in this one
        IsNtfs = result.IsNtfs;
        InaccessibleDirectories = result.InaccessibleDirectories;
        ScanDuration = result.Duration;
        IsStale = false;
        StaleReason = null;
        IsUnavailable = false;
        MonitorProblem = null;
        ScanError = null;
        State = DriveSessionState.Ready;
    }

    // ------------------------------------------------------------------
    // Monitor
    // ------------------------------------------------------------------

    /// <summary>Takes ownership of a monitor (not started yet) and the handlers to unhook again in <see cref="DetachMonitor"/>.</summary>
    public void AttachMonitor(
        NtfsUsnJournalMonitor monitor,
        EventHandler<UsnMonitorEventArgs> onChanges,
        EventHandler<string> onRescanRequired,
        EventHandler<UsnMonitorStatusEventArgs> onStatus)
    {
        DetachMonitor();
        _monitor = monitor;
        _onChanges = onChanges;
        _onRescanRequired = onRescanRequired;
        _onStatus = onStatus;
        monitor.ChangesDetected += onChanges;
        monitor.RescanRequired += onRescanRequired;
        monitor.StatusChanged += onStatus;
    }

    public void DetachMonitor()
    {
        NtfsUsnJournalMonitor? monitor = _monitor;
        if (monitor == null)
            return;

        monitor.ChangesDetected -= _onChanges;
        monitor.RescanRequired -= _onRescanRequired;
        monitor.StatusChanged -= _onStatus;
        _monitor = null;
        _onChanges = null;
        _onRescanRequired = null;
        _onStatus = null;
        monitor.Dispose();
    }

    /// <summary>
    /// Called from a monitor thread for every change batch: remembers the newest event and returns true only for the
    /// first one after the last refresh, which is the one that schedules a UI refresh (the others are coalesced).
    /// </summary>
    public bool TryBeginUiRefresh(UsnMonitorEventArgs e)
    {
        Volatile.Write(ref _latestUsnArgs, e);
        return Interlocked.CompareExchange(ref _uiRefreshPending, 1, 0) == 0;
    }

    /// <summary>Clears the pending flag BEFORE the refresh runs, so an event that arrives meanwhile schedules another pass.</summary>
    public UsnMonitorEventArgs? EndUiRefresh()
    {
        Interlocked.Exchange(ref _uiRefreshPending, 0);
        return Volatile.Read(ref _latestUsnArgs);
    }

    /// <summary>
    /// Called from a monitor thread for every change batch, before <see cref="TryBeginUiRefresh"/>: adds the batch's
    /// changed nodes to those waiting for the next UI pass, so none is lost when passes are coalesced. Batches from a
    /// monitor that is no longer this session's are ignored. Bounded by <see cref="FileSystemStore.MaxActivityNodes"/>.
    /// </summary>
    public void AddActivity(NtfsUsnJournalMonitor monitor, IReadOnlyCollection<int> nodes)
    {
        if (nodes.Count == 0)
            return;

        lock (_activityLock)
        {
            if (!ReferenceEquals(Volatile.Read(ref _monitor), monitor))
                return;

            foreach (int node in nodes)
            {
                if (_pendingActivity.Count >= FileSystemStore.MaxActivityNodes)
                    break;
                _pendingActivity.Add(node);
            }
        }
    }

    /// <summary>Hands the nodes collected since the last pass to the UI pass (UI thread) and starts collecting again.</summary>
    public HashSet<int> TakeActivity()
    {
        lock (_activityLock)
        {
            HashSet<int> taken = _pendingActivity;
            if (taken.Count > 0)
                _pendingActivity = new HashSet<int>();
            return taken;
        }
    }

    /// <summary>
    /// Like <see cref="TakeActivity()"/>, but takes at most <paramref name="max"/> nodes; the rest stay for the next pass
    /// (<paramref name="more"/>). <paramref name="overflow"/> is set when the collection hit its cap: nodes were dropped,
    /// so the set does not describe every change and the pass must look at everything. Then all nodes are taken.
    /// </summary>
    public HashSet<int> TakeActivity(int max, out bool overflow, out bool more)
    {
        lock (_activityLock)
        {
            HashSet<int> pending = _pendingActivity;
            overflow = pending.Count >= FileSystemStore.MaxActivityNodes;
            more = false;
            if (pending.Count <= max || overflow)
            {
                if (pending.Count > 0)
                    _pendingActivity = new HashSet<int>();
                return pending;
            }

            var taken = new HashSet<int>();
            foreach (int node in pending)
            {
                if (taken.Count >= max)
                    break;
                taken.Add(node);
            }
            pending.ExceptWith(taken);
            more = pending.Count > 0;
            return taken;
        }
    }

    /// <summary>Drops node ids that are about to be reused for other nodes from the collected activity.</summary>
    public void ForgetActivity(IEnumerable<int> nodes)
    {
        lock (_activityLock)
        {
            foreach (int node in nodes)
                _pendingActivity.Remove(node);
        }
    }

    /// <summary>Set (UI thread) when the next pass cannot rely on the collected changed nodes alone: it then checks every row and folder.</summary>
    public bool NeedsFullSync { get; set; }

    private long _lastPassEndMs;
    private long _lastPassDurationMs;

    /// <summary>Notes how long a live-update pass took and when it ended (UI thread), for <see cref="PassDelayMs"/>.</summary>
    public void RecordPass(long endMs, long durationMs)
    {
        Volatile.Write(ref _lastPassDurationMs, durationMs);
        Volatile.Write(ref _lastPassEndMs, endMs);
    }

    /// <summary>Milliseconds to wait before the next live-update pass may run (any thread).</summary>
    public int PassDelayMs(long nowMs, int minGapMs = PassPacer.MinGapMs) =>
        PassPacer.DelayMs(nowMs, Volatile.Read(ref _lastPassEndMs), Volatile.Read(ref _lastPassDurationMs), minGapMs);

    /// <summary>Forgets the collected nodes (the store was replaced or a branch was grafted: they no longer describe live changes).</summary>
    public void ClearActivity()
    {
        lock (_activityLock)
        {
            _pendingActivity.Clear();
        }
    }

    // ------------------------------------------------------------------
    // Stale / unavailable
    // ------------------------------------------------------------------

    public void MarkStale(string reason)
    {
        IsStale = true;
        StaleReason = reason;
    }

    /// <summary>The volume vanished: the monitor is stopped (it has nothing to watch), the last scan stays.</summary>
    public void MarkUnavailable()
    {
        IsUnavailable = true;
        DetachMonitor();
    }

    /// <summary>The volume is back. Its monitor is gone and changes were missed, so it counts as stale until rescanned.</summary>
    public void MarkAvailableAgain()
    {
        if (!IsUnavailable)
            return;

        IsUnavailable = false;
        MarkStale("the drive was unavailable meanwhile");
    }

    // ------------------------------------------------------------------
    // Display text of the root row
    // ------------------------------------------------------------------

    /// <summary>"E:\  [Label]"</summary>
    public string Title => $"{RootPath.TrimEnd('\\')}\\  [{VolumeLabel}]";

    public string MonitorModeText
    {
        get
        {
            if (IsUnavailable)
                return "monitor stopped";
            if (_monitor == null)
                return Store == null ? "not monitored yet" : "not monitored (non-NTFS)";
            return _monitor.IsUsnJournalActive
                ? "live: USN journal"
                : ElevationHelper.IsElevated ? "limited: file watcher" : "limited: file watcher (not elevated)";
        }
    }

    /// <summary>Format, free space and monitor mode; the used percentage is the root row's bar, size and counts are its columns.</summary>
    public string SubText =>
        $"{DriveFormat}  •  {SizeFormatter.FormatBytes(FreeSpace)} free of {SizeFormatter.FormatBytes(TotalSize)}  •  {MonitorModeText}";

    /// <summary>A short state marker for the root row; empty when there is nothing to flag.</summary>
    public string Marker
    {
        get
        {
            if (IsUnavailable)
                return "⛔ Not available";
            if (State == DriveSessionState.Queued)
                return Store == null ? "⏳ Queued for scan" : "⏳ Queued for rescan";
            if (State == DriveSessionState.Scanning)
                return Store == null
                    ? $"⟳ Scanning… {ScanProgressItems:N0} items"
                    : $"⟳ Rescanning… {ScanProgressItems:N0} items";
            if (State == DriveSessionState.Failed)
                return $"✖ Scan failed: {ScanError}";
            if (IsStale)
                return "⚠ Stale: select to rescan";
            if (!string.IsNullOrEmpty(MonitorProblem))
                return $"⚠ {MonitorProblem}";
            return string.Empty;
        }
    }

    public string ToolTipText =>
        $"{RootPath}  [{VolumeLabel}]  {DriveFormat}\n" +
        $"{UsedPercentage:0.0}% used, {SizeFormatter.FormatBytes(FreeSpace)} free of {SizeFormatter.FormatBytes(TotalSize)}\n" +
        $"Monitor: {MonitorModeText}" +
        (IsStale ? $"\nStale: {StaleReason}" : string.Empty) +
        (!string.IsNullOrEmpty(MonitorProblem) ? $"\n{MonitorProblem}" : string.Empty) +
        (Store != null ? $"\nLast scan: {SizeFormatter.FormatDuration(ScanDuration)}, ~{SizeFormatter.FormatBytes(EstimatedMemoryBytes)} in memory" : string.Empty) +
        (InaccessibleDirectories > 0 ? $"\n{InaccessibleDirectories:N0} inaccessible folder(s) skipped" : string.Empty);

    /// <summary>Approximate memory held by this drive's store (0 before the first scan).</summary>
    public long EstimatedMemoryBytes => Store?.EstimatedMemoryBytes ?? 0;

    // ------------------------------------------------------------------

    /// <summary>Cancels a running scan, stops the monitor and drops the store so its memory can be reclaimed.</summary>
    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;

        _scanCts?.Cancel();
        DetachMonitor();
        Store = null;
    }
}
