using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using SpaceView.Core;
using SpaceView.Models;
using SpaceView.Native;
using Color = System.Windows.Media.Color;

namespace SpaceView;

public sealed class BulkObservableCollection<T> : ObservableCollection<T>, IRowList<T>
{
    // Every bulk change is announced as a Reset, which clears the selection of the list showing the collection. These
    // two events bracket it so the window can note the selection before and put it back afterwards.
    public event Action? ResetStarting;
    public event Action? ResetCompleted;

    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        ResetStarting?.Invoke();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        ResetCompleted?.Invoke();
    }

    public void InsertRange(int index, IReadOnlyList<T> newItems)
    {
        if (newItems.Count == 0)
            return;

        CheckReentrancy();
        ResetStarting?.Invoke();
        if (Items is List<T> list)
        {
            list.InsertRange(index, newItems);
        }
        else
        {
            for (int i = 0; i < newItems.Count; i++)
            {
                Items.Insert(index + i, newItems[i]);
            }
        }
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        ResetCompleted?.Invoke();
    }

    /// <summary>Replaces <paramref name="removeCount"/> items at <paramref name="index"/> with <paramref name="newItems"/> using a single change notification.</summary>
    public void ReplaceRange(int index, int removeCount, IReadOnlyList<T> newItems)
    {
        if (removeCount <= 0 && newItems.Count == 0)
            return;

        CheckReentrancy();
        ResetStarting?.Invoke();
        if (Items is List<T> list)
        {
            list.RemoveRange(index, removeCount);
            list.InsertRange(index, newItems);
        }
        else
        {
            for (int i = 0; i < removeCount; i++)
            {
                Items.RemoveAt(index);
            }
            for (int i = 0; i < newItems.Count; i++)
            {
                Items.Insert(index + i, newItems[i]);
            }
        }
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        ResetCompleted?.Invoke();
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0)
            return;

        CheckReentrancy();
        ResetStarting?.Invoke();
        if (Items is List<T> list)
        {
            list.RemoveRange(index, count);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                Items.RemoveAt(index);
            }
        }
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        ResetCompleted?.Invoke();
    }

    /// <summary>Moves <paramref name="count"/> items starting at <paramref name="index"/> so that the first one ends up at <paramref name="newIndex"/> (counted after the block was taken out), with a single change notification.</summary>
    public void MoveBlock(int index, int count, int newIndex)
    {
        if (count <= 0 || index == newIndex)
            return;

        CheckReentrancy();
        ResetStarting?.Invoke();
        var block = new List<T>(count);
        for (int i = 0; i < count; i++)
        {
            block.Add(Items[index + i]);
        }

        if (Items is List<T> list)
        {
            list.RemoveRange(index, count);
            list.InsertRange(newIndex, block);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                Items.RemoveAt(index);
            }
            for (int i = 0; i < count; i++)
            {
                Items.Insert(newIndex + i, block[i]);
            }
        }
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        ResetCompleted?.Invoke();
    }
}

public partial class MainWindow : Window
{
    private readonly BulkObservableCollection<TreeRowViewModel> _visibleRows = new();
    private readonly TrayIconManager _trayManager;
    private readonly DispatcherTimer _memoryTimer;

    // One tree, one root row per scanned drive; the first session is the most recently chosen drive. Every row resolves
    // its own session, and NodeIds are only unique per store, so rows are compared together with their session (RowKey).
    private readonly DriveSessionList _sessions = new();
    private readonly Dictionary<DriveSession, TreeRowViewModel> _rootRows = new();
    // Only one drive is scanned at a time (waiting scans show "queued"): several scans at once would thrash the disk
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private CancellationTokenSource? _branchCts; // non-null while a branch refresh is running
    private DriveSession? _branchSession;        // the session that branch refresh belongs to
    private bool _suppressDriveSelectionChanged; // set while the drive list is refreshed in place
    private bool _fileOperationInProgress;      // a delete or move (including the view update after it) is running: no second one starts
    private List<RowKey>? _selectionBeforeReset; // the selection noted when a Reset-style change starts, put back when it is done
    private WindowState _lastNonMinimizedState = WindowState.Normal;
    private bool _isExiting;
    private readonly string _initialOverlayTitle;
    private readonly string _initialOverlaySubtitle;

    // A folder shows at most this many children at a time; the rest is behind a "more items" row
    private const int ChildPageSize = 5000;
    // Folders with more items than this (all levels) are expanded on a background thread
    private const int BackgroundExpandThreshold = 20_000;

    public MainWindow()
    {
        InitializeComponent();

        TreeListBox.ItemsSource = _visibleRows;
        _visibleRows.ResetStarting += CaptureSelectionBeforeReset;
        _visibleRows.ResetCompleted += RestoreSelectionAfterReset;
        SourceInitialized += (_, _) => HookClipboardNotifications();
        _initialOverlayTitle = OverlayTitleText.Text;
        _initialOverlaySubtitle = OverlaySubtitleText.Text;

        _trayManager = new TrayIconManager(
            toggleMainWindowAction: ToggleWindowVisibility,
            rescanAction: () => Dispatcher.Invoke(RescanCurrentDrive),
            exitApplicationAction: () => Dispatcher.Invoke(ExitApplication));

        // Convert System.Drawing.Icon to WPF ImageSource for Window Icon
        Icon = Imaging.CreateBitmapSourceFromHIcon(
            _trayManager.AppIcon.Handle,
            Int32Rect.Empty,
             System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        LoadShieldIcon();

        _ = StartupAsync();

        _memoryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _memoryTimer.Tick += (_, _) => UpdateMemoryUsageDisplay();
        _memoryTimer.Start();
        UpdateMemoryUsageDisplay();

        Loaded += (_, _) =>
        {
            // Open the drive dropdown on initial launch so the user immediately sees drives to select
            // (not when this is a relaunch that restores a list of drives)
            if (App.StartupOptions.Drives.Count == 0)
            {
                DriveComboBox.Focus();
                DriveComboBox.IsDropDownOpen = true;
            }
        };
    }

    /// <summary>
    /// Lists the drives and, when this instance was started with <c>--drives</c> (an elevated relaunch), adds those drives
    /// in the given order; they are scanned one after the other. If a previous instance is named, waits for it to exit first.
    /// </summary>
    private async Task StartupAsync()
    {
        StartupOptions options = App.StartupOptions;
        if (options.ParentProcessId is int parentPid)
        {
            StatusTextBlock.Text = "Waiting for the previous SpaceView window to close...";
            await ElevationHelper.WaitForProcessExitAsync(parentPid, TimeSpan.FromSeconds(10));
        }

        await RefreshDriveListAsync(preserveSelection: false);

        if (options.Drives.Count > 0)
            RestoreDrives(options.Drives);
    }

    private void RestoreDrives(IReadOnlyList<string> roots)
    {
        var missing = new List<string>();
        foreach (string root in roots)
        {
            DriveItemViewModel? drive = DriveItemViewModel.LastResult.Drives
                .FirstOrDefault(d => DriveSession.SameRoot(d.Name, root));
            if (drive == null)
            {
                missing.Add(root);
                continue;
            }

            DriveSession session = _sessions.AddLast(drive.Name, r => new DriveSession(r), out bool added);
            if (!added)
                continue;

            ApplyVolumeInfo(session, drive);
            AddRootRow(session, _visibleRows.Count);
            _ = StartSessionScanAsync(session);
        }

        if (_sessions.Count > 0)
        {
            RescanButton.IsEnabled = true;
            EmptyStateOverlay.Visibility = Visibility.Collapsed;
            DrivePlaceholderText.Visibility = Visibility.Collapsed;

            // Show the first drive as the dropdown's choice without starting anything
            DriveItemViewModel? first = DriveItemViewModel.LastResult.Drives
                .FirstOrDefault(d => DriveSession.SameRoot(d.Name, _sessions[0].RootPath));
            _suppressDriveSelectionChanged = true;
            try
            {
                DriveComboBox.SelectedItem = first;
            }
            finally
            {
                _suppressDriveSelectionChanged = false;
            }
        }

        if (missing.Count > 0)
            StatusTextBlock.Text = $"Not available, skipped: {string.Join(", ", missing)}";
    }

    private async Task RefreshDriveListAsync(bool preserveSelection)
    {
        DriveEnumeration result;
        try
        {
            result = await DriveItemViewModel.GetReadyDrivesAsync();
        }
        catch
        {
            return;
        }

        // Read the selection only now: the user may have picked another drive while the drives were being queried
        string? previousDriveName = preserveSelection ? (DriveComboBox.SelectedItem as DriveItemViewModel)?.Name : null;
        DriveItemViewModel? match = previousDriveName == null
            ? null
            : result.Drives.FirstOrDefault(d => string.Equals(d.Name, previousDriveName, StringComparison.OrdinalIgnoreCase));

        // Replacing the items clears the selection; put it back without telling the SelectionChanged handler, which
        // would otherwise rescan the drive (and discard a scan that just finished).
        _suppressDriveSelectionChanged = true;
        try
        {
            DriveComboBox.ItemsSource = result.Drives;
            if (match != null)
                DriveComboBox.SelectedItem = match;
        }
        finally
        {
            _suppressDriveSelectionChanged = false;
        }

        bool hasSelection = DriveComboBox.SelectedItem != null;
        DrivePlaceholderText.Visibility = hasSelection || _sessions.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        RescanButton.IsEnabled = _sessions.Count > 0;

        // Drives in the tree that are no longer in the list: keep showing them, but say so and stop watching a volume
        // that is gone. Drives that merely answered too slowly stay in the list. A drive that is being scanned is left alone.
        var gone = new List<string>();
        foreach (DriveSession session in _sessions)
        {
            DriveItemViewModel? drive = result.Drives.FirstOrDefault(d => DriveSession.SameRoot(d.Name, session.RootPath));
            if (drive != null)
            {
                ApplyVolumeInfo(session, drive);
                session.MarkAvailableAgain();
            }
            else if (!session.IsBusy && !session.IsUnavailable)
            {
                session.MarkUnavailable();
                gone.Add(session.RootPath);
            }
            RefreshRootRow(session);
        }

        if (gone.Count > 0)
        {
            StatusTextBlock.Text = $"Drive {string.Join(", ", gone)} is no longer available. Showing the results of the last scan; live monitoring stopped.";
        }
        else if (result.Unresponsive.Count > 0)
        {
            StatusTextBlock.Text = $"Not responding, showing last known details: {string.Join(", ", result.Unresponsive)}";
        }

        UpdateMonitorBadge();
        UpdateElevationUi();
    }

    private static void ApplyVolumeInfo(DriveSession session, DriveItemViewModel drive) =>
        session.UpdateVolume(drive.DriveInfo, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.FreeSpace);

    /// <summary>
    /// A drive was chosen in the dropdown: it becomes the first root. A new drive is added and scanned; one that is
    /// already in the tree is moved to the top as it is (no rescan, expanded folders stay) unless it is stale or was
    /// unavailable, which is when the user's selection rescans it.
    /// </summary>
    private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDriveSelectionChanged)
            return;

        if (DriveComboBox.SelectedItem is not DriveItemViewModel selectedDrive)
        {
            DrivePlaceholderText.Visibility = _sessions.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        DrivePlaceholderText.Visibility = Visibility.Collapsed;
        ActivateDrive(selectedDrive);
    }

    private void ActivateDrive(DriveItemViewModel drive)
    {
        SessionSelectOutcome outcome = _sessions.SelectToTop(drive.Name, root => new DriveSession(root), out DriveSession session);
        ApplyVolumeInfo(session, drive);

        switch (outcome)
        {
            case SessionSelectOutcome.Added:
                AddRootRow(session, 0);
                break;
            case SessionSelectOutcome.MovedToTop:
                MoveSessionRowsToTop(session);
                RefreshRootRow(session);
                break;
        }

        RescanButton.IsEnabled = true;
        EmptyStateOverlay.Visibility = Visibility.Collapsed;

        if (outcome == SessionSelectOutcome.Added)
        {
            _ = StartSessionScanAsync(session);
        }
        else if (!session.IsBusy && (session.IsStale || session.IsUnavailable || session.Store == null))
        {
            // Stale (journal wrapped), back from unavailable, or never finished: choosing the drive rescans it
            _ = StartSessionScanAsync(session, session.StaleReason ?? (session.IsUnavailable ? "The drive was unavailable" : null));
        }

        TreeRowViewModel root = _rootRows[session];
        TreeListBox.SelectedItem = root;
        TreeListBox.ScrollIntoView(root);
        UpdateMonitorBadge();
        UpdateStatsText();
    }

    private void RescanButton_Click(object sender, RoutedEventArgs e)
    {
        DriveSession? session = GetActiveSession();
        if (session != null)
            RescanSession(session);
    }

    private async void RefreshDrivesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDriveListAsync(preserveSelection: true);
        if (DriveComboBox.SelectedItem == null && _sessions.Count == 0)
        {
            DriveComboBox.IsDropDownOpen = true;
        }
    }

    private void RescanCurrentDrive()
    {
        if (!IsVisible)
        {
            ShowAndActivateWindow();
        }
        // "Current" is the most recently chosen drive: the first root
        if (_sessions.Count > 0)
        {
            RescanSession(_sessions[0]);
        }
    }

    /// <summary>The drive the toolbar acts on: that of the selected row, else the dropdown's choice, else the first root.</summary>
    private DriveSession? GetActiveSession()
    {
        if (TreeListBox.SelectedItem is TreeRowViewModel row)
            return row.Session;

        if (DriveComboBox.SelectedItem is DriveItemViewModel drive)
        {
            DriveSession? chosen = _sessions.Find(drive.Name);
            if (chosen != null)
                return chosen;
        }
        return _sessions.Count > 0 ? _sessions[0] : null;
    }

    private void RescanSession(DriveSession session)
    {
        if (session.IsBusy)
        {
            StatusTextBlock.Text = $"{session.RootPath} is already being scanned.";
            return;
        }
        _ = StartSessionScanAsync(session, session.StaleReason);
    }

    // =========================================================================
    // Scanning (one drive at a time)
    // =========================================================================

    private void AddRootRow(DriveSession session, int index)
    {
        TreeRowViewModel root = TreeRowViewModel.CreateRoot(session);
        _rootRows[session] = root;
        _visibleRows.InsertRange(index, new[] { root });
    }

    private void RefreshRootRow(DriveSession session)
    {
        if (_rootRows.TryGetValue(session, out TreeRowViewModel? root))
            root.RefreshRoot();
    }

    /// <summary>Moves a drive's root row and everything below it to the top, as it is: no rescan, expansion state untouched.</summary>
    private void MoveSessionRowsToTop(DriveSession session)
    {
        if (!TryGetSessionBlock(session, out int start, out int end) || start == 0)
            return;

        // The reset notification clears the list's selection: it is put back by key (see RestoreSelectionAfterReset)
        _visibleRows.MoveBlock(start, end - start, 0);
    }

    /// <summary>Start index of the session's root row and the index after its last visible descendant.</summary>
    private bool TryGetSessionBlock(DriveSession session, out int start, out int end)
    {
        start = end = -1;
        if (!_rootRows.TryGetValue(session, out TreeRowViewModel? root))
            return false;

        start = _visibleRows.IndexOf(root);
        if (start < 0)
            return false;

        end = start + 1 + CountDescendantRows(start);
        return true;
    }

    private bool IsLive(DriveSession session, FileSystemStore store) =>
        !session.IsDisposed && ReferenceEquals(session.Store, store);

    /// <summary>
    /// Scans (or rescans) one drive. The scan waits for its turn if another drive is being scanned (the row shows
    /// "queued"). On a rescan the old tree and its monitor stay until the new scan is done and replace them.
    /// </summary>
    private async Task StartSessionScanAsync(DriveSession session, string? rescanReason = null)
    {
        if (session.IsDisposed)
            return;

        // A branch refresh of this drive is superseded by the scan (the branch refresh releases its own resources)
        if (ReferenceEquals(_branchSession, session))
            _branchCts?.Cancel();

        CancellationTokenSource cts = session.BeginScan();
        CancellationToken ct = cts.Token;
        RefreshRootRow(session);
        StatusTextBlock.Text = $"{session.RootPath} queued for scanning...";

        bool gateHeld = false;
        try
        {
            await _scanGate.WaitAsync(ct);
            gateHeld = true;
            if (!session.IsCurrentScan(cts))
                return;

            session.MarkScanning();
            RefreshRootRow(session);
            StatusTextBlock.Text = rescanReason != null
                ? $"{rescanReason}. Rescanning {session.RootPath}..."
                : $"Scanning {session.RootPath}...";

            DriveInfo drive = session.Drive ?? new DriveInfo(session.RootPath);
            bool ready = await Task.Run(() =>
            {
                try { return drive.IsReady; }
                catch { return false; }
            }, CancellationToken.None);
            if (!session.IsCurrentScan(cts))
                return;

            if (!ready)
            {
                // Scanning a volume that is gone would replace a good tree with an empty one
                if (session.Store != null)
                {
                    session.MarkUnavailable();
                    session.AbandonScan();
                    StatusTextBlock.Text = $"Drive {session.RootPath} is not available; keeping the last scan.";
                }
                else
                {
                    session.Fail("the drive is not ready");
                    StatusTextBlock.Text = $"Error scanning {session.RootPath}: the drive is not ready.";
                }
                RefreshRootRow(session);
                UpdateMonitorBadge();
                UpdateElevationUi();
                return;
            }

            var progress = new Progress<ScanProgressReport>(report =>
            {
                // A report can arrive after the scan has finished or been replaced
                if (session.State != DriveSessionState.Scanning || !session.IsCurrentScan(cts))
                    return;

                session.ScanProgressItems = report.ScannedNodes;
                RefreshRootRow(session);
                StatusTextBlock.Text = report.CurrentStatus;
            });

            ScanResult result = await VolumeScanner.ScanDriveAsync(drive, progress, ct);
            if (!session.IsCurrentScan(cts))
                return;

            CompleteScan(session, result, rescanReason);
        }
        catch (OperationCanceledException)
        {
            // Scan superseded by another one, or its drive was removed from the view
        }
        catch (Exception ex)
        {
            if (session.IsCurrentScan(cts))
            {
                session.Fail(ex.Message);
                RefreshRootRow(session);
                StatusTextBlock.Text = $"Error scanning {session.RootPath}: {ex.Message}";
                UpdateMonitorBadge();
            }
        }
        finally
        {
            if (gateHeld)
                _scanGate.Release();
        }
    }

    /// <summary>Installs a finished scan: swaps the store, rebuilds the drive's rows (keeping what was expanded) and starts its monitor.</summary>
    private void CompleteScan(DriveSession session, ScanResult result, string? rescanReason)
    {
        bool firstLoad = session.Store == null;
        bool expandRoot = firstLoad
            ? _sessions.Count > 0 && ReferenceEquals(_sessions[0], session)
            : _rootRows.TryGetValue(session, out TreeRowViewModel? existing) && existing.IsExpanded;

        session.DetachMonitor();
        session.ApplyScanResult(result);
        RebuildSessionRows(session, expandRoot);
        ResolveCutKeys();
        StartMonitor(session, result);
        RefreshRootRow(session);

        string inaccessible = result.InaccessibleDirectories > 0
            ? $"  •  {result.InaccessibleDirectories:N0} inaccessible folder(s) skipped"
            : string.Empty;
        // Not elevated: say how to get at them (a button, not a dialog)
        string hint = result.InaccessibleDirectories > 0 && !ElevationHelper.IsElevated
            ? "  •  use \"Run as administrator\" to include them"
            : string.Empty;
        StatusTextBlock.Text = rescanReason != null
            ? $"Rescanned {session.RootPath} in {result.Duration.TotalSeconds:0.00}s ({rescanReason}){inaccessible}{hint}"
            : $"Scanned {session.RootPath} in {result.Duration.TotalSeconds:0.00}s{inaccessible}{hint}";

        UpdateStatsText();
        UpdateMonitorBadge();
        UpdateElevationUi();
        UpdateMemoryUsageDisplay();
    }

    private void StartMonitor(DriveSession session, ScanResult result)
    {
        // Real-time monitoring for NTFS (USN journal, or the file watcher fallback); other file systems are not monitored
        if (!result.IsNtfs || session.Store == null)
            return;

        var monitor = new NtfsUsnJournalMonitor(
            session.Store,
            result.UsnJournalReady,
            result.UsnJournalId,
            result.StartUsn);

        session.AttachMonitor(
            monitor,
            (_, e) => OnUsnChangesDetected(session, monitor, e),
            (_, reason) => OnMonitorRescanRequired(session, monitor, reason),
            (_, e) => OnMonitorStatusChanged(session, monitor, e));
        monitor.Start();
    }

    /// <summary>
    /// Replaces the rows below a drive's root after its store was swapped: the root stays expanded or collapsed as it
    /// was, expanded sub-folders (matched by name path) are expanded again, and selection and scroll position are kept.
    /// </summary>
    private void RebuildSessionRows(DriveSession session, bool expandRoot)
    {
        FileSystemStore? store = session.Store;
        if (store == null || !_rootRows.TryGetValue(session, out TreeRowViewModel? root))
            return;

        int rootIndex = _visibleRows.IndexOf(root);
        if (rootIndex < 0)
            return;

        HashSet<string>? expandedSubfolders = root.IsExpanded ? CaptureExpandedSubfolders(rootIndex) : null;
        // The rows of the new tree have new node ids, so the selected rows of this drive are found again by name path
        List<string> selectedPaths = CaptureSelectedPaths(rootIndex, rootIndex + 1 + CountDescendantRows(rootIndex));
        ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(TreeListBox);
        double scrollOffset = scrollViewer?.VerticalOffset ?? 0;

        var newRows = new List<TreeRowViewModel>();
        root.IsExpanded = expandRoot;
        if (expandRoot)
        {
            AppendChildRows(newRows, session, store, store.RootNodeId, 1, string.Empty, expandedSubfolders);
        }
        _visibleRows.ReplaceRange(rootIndex + 1, CountDescendantRows(rootIndex), newRows);
        root.RefreshRoot();

        ReselectPaths(rootIndex, selectedPaths, fallback: root);

        if (scrollViewer != null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => scrollViewer.ScrollToVerticalOffset(scrollOffset));
        }
    }

    /// <summary>
    /// Name paths (see <see cref="RelativePathOfRow"/>) of the selected rows in the block [<paramref name="blockStart"/>,
    /// <paramref name="blockEnd"/>) whose first row is the block's root, found in one pass over the block. The selected
    /// rows below the root are taken out of the selection here, so that nothing selects them
    /// again by key: a key is only valid within one store, and a node of a new store could carry an old row's id.
    /// </summary>
    private List<string> CaptureSelectedPaths(int blockStart, int blockEnd)
    {
        var paths = new List<string>();
        var selected = new HashSet<TreeRowViewModel>(TreeListBox.SelectedItems.OfType<TreeRowViewModel>());
        if (selected.Count == 0)
            return paths;

        int baseDepth = _visibleRows[blockStart].Depth;
        var names = new List<string>();
        for (int i = blockStart; i < blockEnd; i++)
        {
            TreeRowViewModel r = _visibleRows[i];
            if (r.IsMoreRow)
                continue;
            if (i == blockStart)
            {
                if (selected.Contains(r))
                    paths.Add(string.Empty);
                continue;
            }

            int level = Math.Min(r.Depth - baseDepth - 1, names.Count);
            names.RemoveRange(level, names.Count - level);
            names.Add(r.Name);
            if (selected.Contains(r))
            {
                paths.Add(string.Join('\\', names));
                TreeListBox.SelectedItems.Remove(r);
            }
        }
        return paths;
    }

    /// <summary>
    /// Selects the rows at the given name paths below the block root at <paramref name="blockStart"/>, in addition to what is
    /// selected. A path that no longer exists is replaced by <paramref name="fallback"/> (the block root), so the selection
    /// is never lost.
    /// </summary>
    private void ReselectPaths(int blockStart, IReadOnlyList<string> paths, TreeRowViewModel fallback)
    {
        if (paths.Count == 0)
            return;

        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toSelect = new List<TreeRowViewModel>();

        int baseDepth = _visibleRows[blockStart].Depth;
        var names = new List<string>();
        if (wanted.Contains(string.Empty))
        {
            toSelect.Add(_visibleRows[blockStart]);
            found.Add(string.Empty);
        }
        for (int i = blockStart + 1; i < _visibleRows.Count && _visibleRows[i].Depth > baseDepth; i++)
        {
            TreeRowViewModel r = _visibleRows[i];
            if (r.IsMoreRow)
                continue;

            int level = Math.Min(r.Depth - baseDepth - 1, names.Count);
            names.RemoveRange(level, names.Count - level);
            names.Add(r.Name);
            string path = string.Join('\\', names);
            if (wanted.Contains(path))
            {
                toSelect.Add(r);
                found.Add(path);
            }
        }

        if (found.Count < wanted.Count)
            toSelect.Add(fallback);

        foreach (TreeRowViewModel row in toSelect)
        {
            if (!TreeListBox.SelectedItems.Contains(row))
                TreeListBox.SelectedItems.Add(row);
        }
    }

    private void CaptureSelectionBeforeReset()
    {
        // Resets can nest (a handler that changes the rows again): the outermost one noted the selection
        _selectionBeforeReset ??= TreeListBox.SelectedItems
            .OfType<TreeRowViewModel>()
            .Where(r => !r.IsMoreRow)
            .Select(r => r.Key)
            .ToList();
    }

    /// <summary>Puts the selection back after a Reset-style change: the rows that are still there (matched by session and node id) are selected again.</summary>
    private void RestoreSelectionAfterReset()
    {
        List<RowKey>? keys = _selectionBeforeReset;
        _selectionBeforeReset = null;
        MarkCutRows();
        if (keys == null || keys.Count == 0)
            return;

        var wanted = new HashSet<RowKey>(keys);
        var alreadySelected = new HashSet<object>(TreeListBox.SelectedItems.Cast<object>());
        foreach (TreeRowViewModel row in _visibleRows)
        {
            // A "more items" row has no node of its own: every one of them has the same key
            if (!row.IsMoreRow && wanted.Contains(row.Key) && !alreadySelected.Contains(row))
                TreeListBox.SelectedItems.Add(row);
        }
    }

    /// <summary>Name path of the row at <paramref name="rowIndex"/> relative to the root at <paramref name="rootIndex"/> ('\' separated; empty for the root); null for a "more items" row.</summary>
    private string? RelativePathOfRow(int rootIndex, int rowIndex)
    {
        if (rowIndex < rootIndex || rowIndex >= _visibleRows.Count || _visibleRows[rowIndex].IsMoreRow)
            return null;
        if (rowIndex == rootIndex)
            return string.Empty;

        var names = new List<string> { _visibleRows[rowIndex].Name };
        int depth = _visibleRows[rowIndex].Depth;
        for (int i = rowIndex - 1; i > rootIndex; i--)
        {
            TreeRowViewModel r = _visibleRows[i];
            if (!r.IsMoreRow && r.Depth < depth)
            {
                names.Add(r.Name);
                depth = r.Depth;
            }
        }
        names.Reverse();
        return string.Join('\\', names);
    }

    private TreeRowViewModel? FindRowByRelativePath(int rootIndex, string relativePath)
    {
        if (relativePath.Length == 0)
            return _visibleRows[rootIndex];

        int baseDepth = _visibleRows[rootIndex].Depth;
        var names = new List<string>();
        for (int i = rootIndex + 1; i < _visibleRows.Count && _visibleRows[i].Depth > baseDepth; i++)
        {
            TreeRowViewModel r = _visibleRows[i];
            if (r.IsMoreRow)
                continue;

            int level = Math.Min(r.Depth - baseDepth - 1, names.Count);
            names.RemoveRange(level, names.Count - level);
            names.Add(r.Name);
            if (string.Equals(string.Join('\\', names), relativePath, StringComparison.OrdinalIgnoreCase))
                return r;
        }
        return null;
    }

    private void ExpandToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton btn && btn.Tag is TreeRowViewModel row)
        {
            ToggleRowExpansion(row);
            // The chevron flips itself on click; if the toggle was refused or is still loading, show the real state
            BindingOperations.GetBindingExpression(btn, ToggleButton.IsCheckedProperty)?.UpdateTarget();
            e.Handled = true;
        }
    }

    private void TreeListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ctrl/Shift + click are selection gestures, and a double-click on the chevron is two clicks on its button
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 ||
            FindAncestor<ToggleButton>(e.OriginalSource as DependencyObject) != null)
            return;

        // The row under the pointer, not the selection: with several rows selected the selection says nothing about the click
        if (ItemsControl.ContainerFromElement(TreeListBox, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: TreeRowViewModel row })
        {
            if (row.IsMoreRow)
                _ = LoadMoreRowsAsync(row);
            else if (row.IsDirectory)
                ToggleRowExpansion(row);
        }
    }

    private void TreeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatsText();
    }

    private void TreeListBoxItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: TreeRowViewModel { IsMoreRow: true } moreRow })
        {
            e.Handled = true;
            _ = LoadMoreRowsAsync(moreRow);
        }
    }

    private void ToggleRowExpansion(TreeRowViewModel row)
    {
        DriveSession session = row.Session;
        FileSystemStore? store = row.Store;
        // A drive that has not finished its first scan has no tree to expand
        if (store == null || !IsLive(session, store) || !row.IsDirectory || row.IsLoadingChildren)
            return;

        int rowIndex = _visibleRows.IndexOf(row);
        if (rowIndex < 0)
            return;

        if (row.IsExpanded)
        {
            // Collapse: remove all contiguous descendant rows that have Depth > row.Depth
            int removeCount = CountDescendantRows(rowIndex);

            row.IsExpanded = false;
            if (removeCount > 0)
            {
                _visibleRows.RemoveRange(rowIndex + 1, removeCount);
            }
        }
        else
        {
            NodeRecord rec = store.GetNodeSnapshot(row.NodeId);
            if (rec.FileCount + rec.FolderCount > BackgroundExpandThreshold)
            {
                _ = ExpandRowInBackgroundAsync(session, store, row);
                return;
            }

            // Expand: query sorted children from compact store and insert them directly below rowIndex
            List<TreeRowViewModel> childRows = CreateChildRows(session, store, row.NodeId, row.Depth + 1);
            row.IsExpanded = true;
            _visibleRows.InsertRange(rowIndex + 1, childRows);
        }
    }

    /// <summary>
    /// Expands a folder with a very large number of items: the first page of children is read and turned into rows on a
    /// background thread. While that runs the row counts as expanded but is marked loading, so a second toggle, a live
    /// update or a branch refresh cannot touch it; the rows are inserted only if the row is still where it was.
    /// </summary>
    private async Task ExpandRowInBackgroundAsync(DriveSession session, FileSystemStore store, TreeRowViewModel row)
    {
        row.IsLoadingChildren = true;
        row.IsExpanded = true;
        StatusTextBlock.Text = $"Loading {row.Name}...";

        try
        {
            int childDepth = row.Depth + 1;
            (List<TreeRowViewModel> childRows, int total) = await Task.Run(() => LoadChildRows(session, store, row.NodeId, childDepth, skip: 0, alreadyShown: null));

            int rowIndex = _visibleRows.IndexOf(row);
            if (rowIndex < 0 || !IsLive(session, store))
                return; // the row was removed (deleted, collapsed away by a refresh) or the drive's tree replaced meanwhile

            AddMoreRowIfNeeded(childRows, session, store, row.NodeId, childDepth, total, shownCount: childRows.Count);
            _visibleRows.InsertRange(rowIndex + 1, childRows);
            StatusTextBlock.Text = $"Loaded {row.Name}: {total:N0} items";
            session.NeedsFullSync = true; // changes during the load are not in the rows
        }
        catch (Exception ex)
        {
            row.IsExpanded = false;
            StatusTextBlock.Text = $"Could not load {row.Name}: {ex.Message}";
        }
        finally
        {
            row.IsLoadingChildren = false;
        }
    }

    /// <summary>Replaces a "more items" row with the next page of children of its folder (read off the UI thread).</summary>
    private async Task LoadMoreRowsAsync(TreeRowViewModel moreRow)
    {
        DriveSession session = moreRow.Session;
        FileSystemStore? store = moreRow.Store;
        if (store == null || !IsLive(session, store) || !moreRow.IsMoreRow || moreRow.IsLoadingChildren)
            return;

        int rowIndex = _visibleRows.IndexOf(moreRow);
        if (rowIndex < 0)
            return;

        // The children of this folder that are on screen (the rows above this one, at its depth)
        var shown = new HashSet<int>();
        for (int i = rowIndex - 1; i >= 0 && _visibleRows[i].Depth >= moreRow.Depth; i--)
        {
            if (_visibleRows[i].Depth == moreRow.Depth && !_visibleRows[i].IsMoreRow)
                shown.Add(_visibleRows[i].NodeId);
        }

        moreRow.IsLoadingChildren = true;
        try
        {
            int parentNodeId = moreRow.ParentNodeId;
            int depth = moreRow.Depth;
            (List<TreeRowViewModel> pageRows, int total) = await Task.Run(() => LoadChildRows(session, store, parentNodeId, depth, skip: shown.Count, alreadyShown: shown));

            rowIndex = _visibleRows.IndexOf(moreRow);
            if (rowIndex < 0 || !IsLive(session, store))
                return;

            // Rows a live update added while the page was being read (rows of other drives have other keys)
            var present = new HashSet<RowKey>(_visibleRows.Where(r => !r.IsMoreRow).Select(r => r.Key));
            pageRows.RemoveAll(r => present.Contains(r.Key));

            AddMoreRowIfNeeded(pageRows, session, store, parentNodeId, depth, total, shownCount: shown.Count + pageRows.Count);
            _visibleRows.ReplaceRange(rowIndex, 1, pageRows);
            session.NeedsFullSync = true; // changes during the load are not in the rows
            // The first new row becomes the selection, unless the user has several rows selected: that is kept
            if (pageRows.Count > 0 && TreeListBox.SelectedItems.Count <= 1)
                TreeListBox.SelectedItem = pageRows[0];
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Could not load more items: {ex.Message}";
        }
        finally
        {
            moreRow.IsLoadingChildren = false;
        }
    }

    /// <summary>Number of rows directly below <paramref name="rowIndex"/> that are its descendants (Depth greater than its own).</summary>
    private int CountDescendantRows(int rowIndex)
    {
        int depth = _visibleRows[rowIndex].Depth;
        int count = 0;
        while (rowIndex + 1 + count < _visibleRows.Count &&
               _visibleRows[rowIndex + 1 + count].Depth > depth)
        {
            count++;
        }
        return count;
    }

    /// <summary>The rows for the first page of a folder's children, followed by a "more items" row if there are more.</summary>
    private static List<TreeRowViewModel> CreateChildRows(DriveSession session, FileSystemStore store, int parentNodeId, int childDepth)
    {
        (List<TreeRowViewModel> childRows, int total) = LoadChildRows(session, store, parentNodeId, childDepth, skip: 0, alreadyShown: null);
        AddMoreRowIfNeeded(childRows, session, store, parentNodeId, childDepth, total, shownCount: childRows.Count);
        return childRows;
    }

    /// <summary>
    /// Reads one page of children (largest first, starting at rank <paramref name="skip"/>) in a single store call and
    /// builds their rows, leaving out those in <paramref name="alreadyShown"/>. Touches no UI state, so it can run on a
    /// background thread. Also returns the total number of children the folder has.
    /// </summary>
    private static (List<TreeRowViewModel> Rows, int Total) LoadChildRows(
        DriveSession session, FileSystemStore store, int parentNodeId, int childDepth, int skip, HashSet<int>? alreadyShown)
    {
        var page = new List<NodeSnapshot>(Math.Min(ChildPageSize, 1024));
        int total = store.GetChildPage(parentNodeId, skip, ChildPageSize, page);

        var rows = new List<TreeRowViewModel>(page.Count + 1);
        foreach (NodeSnapshot child in page)
        {
            if (alreadyShown == null || !alreadyShown.Contains(child.NodeId))
                rows.Add(new TreeRowViewModel(session, store, child, childDepth));
        }
        return (rows, total);
    }

    /// <summary>Ends a block of child rows with a "more items" row when <paramref name="shownCount"/> of the <paramref name="total"/> children are on screen.</summary>
    private static void AddMoreRowIfNeeded(
        List<TreeRowViewModel> rows, DriveSession session, FileSystemStore store, int parentNodeId, int childDepth, int total, int shownCount)
    {
        if (total > shownCount)
            rows.Add(TreeRowViewModel.CreateMoreRow(session, store, parentNodeId, childDepth, total - shownCount));
    }

    // =========================================================================
    // Live NTFS USN Change Journal UI Synchronization (per drive)
    // =========================================================================

    private void OnUsnChangesDetected(DriveSession session, NtfsUsnJournalMonitor monitor, UsnMonitorEventArgs e)
    {
        // Changed nodes are collected for every batch (the coalesced passes below only see the newest args)
        session.AddActivity(monitor, e.ChangedNodes);

        // Always publish the newest args; only the first event after a refresh schedules a new one
        if (!session.TryBeginUiRefresh(e))
            return;

        ScheduleLivePass(session, monitor);
    }

    /// <summary>
    /// Queues a live-update pass at Background priority, no sooner than the pacing gap after the previous pass (a multiple
    /// of how long that took), so a busy volume cannot keep the UI thread occupied: events arriving meanwhile coalesce.
    /// </summary>
    private void ScheduleLivePass(DriveSession session, NtfsUsnJournalMonitor monitor)
    {
        void Run()
        {
            // Clear the flag BEFORE refreshing: an event raised while the refresh runs then schedules
            // another pass instead of being swallowed.
            UsnMonitorEventArgs? args = session.EndUiRefresh();
            if (args != null && ReferenceEquals(session.Monitor, monitor))
                ApplyLiveStoreUpdatesToVisibleRows(session, args);
        }

        int delay = session.PassDelayMs(Environment.TickCount64);
        if (delay <= 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, Run);
            return;
        }

        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, Run);
            }
            catch (InvalidOperationException)
            {
                // The application is shutting down
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// The journal wrapped (or the watcher overflowed): the drive's tree may be wrong. Nothing is rescanned in the
    /// background; the drive is marked stale and rescanned when the user selects it or uses Refresh.
    /// </summary>
    private void OnMonitorRescanRequired(DriveSession session, NtfsUsnJournalMonitor monitor, string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // Ignore signals from a monitor that was already replaced by a newer scan
            if (!ReferenceEquals(session.Monitor, monitor))
                return;

            session.MarkStale(reason);
            RefreshRootRow(session);
            StatusTextBlock.Text = $"{session.RootPath}: {reason}. Live data may be incomplete; select the drive or use Refresh (F5) to rescan.";
        });
    }

    private void OnMonitorStatusChanged(DriveSession session, NtfsUsnJournalMonitor monitor, UsnMonitorStatusEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(session.Monitor, monitor))
                return;

            if (e.Kind == UsnMonitorStatusKind.FellBackToWatcher)
            {
                StatusTextBlock.Text = $"{session.RootPath}: {e.Message}; using limited FileSystemWatcher monitoring instead.";
            }
            else
            {
                session.MonitorProblem = "Live monitoring problem (see status bar)";
                StatusTextBlock.Text = $"{session.RootPath}: {e.Message}";
            }
            RefreshRootRow(session);
            UpdateMonitorBadge();
            UpdateElevationUi();
        });
    }

    /// <summary>The toolbar badge describes the first (most recently chosen) drive; the others show their mode on their own rows.</summary>
    private void UpdateMonitorBadge()
    {
        DriveSession? first = _sessions.Count > 0 ? _sessions[0] : null;
        if (first == null || (first.Store == null && !first.IsUnavailable))
        {
            UsnStatusBadge.Visibility = Visibility.Collapsed;
            return;
        }

        string drive = first.RootPath.TrimEnd('\\');
        UsnStatusBadge.Visibility = Visibility.Visible;
        if (first.IsUnavailable)
        {
            UsnStatusDot.Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            SetMonitorBadgeText($"{drive} not available", $"{drive} not available (showing last scan)");
        }
        else if (first.Monitor == null)
        {
            UsnStatusDot.Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            SetMonitorBadgeText($"{drive} {first.DriveFormat} (no USN)", $"{drive} {first.DriveFormat} Volume (Non-NTFS)");
        }
        else if (!string.IsNullOrEmpty(first.MonitorProblem))
        {
            UsnStatusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
            SetMonitorBadgeText($"{drive} Monitoring problem", $"{drive} Live monitoring problem (see status bar)");
        }
        else if (first.Monitor.IsUsnJournalActive)
        {
            UsnStatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Emerald green
            SetMonitorBadgeText($"{drive} USN live monitoring", $"{drive} NTFS USN Journal Active (Live Monitoring)");
        }
        else
        {
            UsnStatusDot.Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // Cyan
            SetMonitorBadgeText($"{drive} Limited monitoring",
                ElevationHelper.IsElevated
                    ? $"{drive} Limited Monitoring: FileSystemWatcher (USN journal unavailable)"
                    : $"{drive} Limited Monitoring: FileSystemWatcher (not elevated)");
        }
    }

    /// <summary>Short text on the toolbar badge (it must fit beside the buttons); the full wording is its tooltip.</summary>
    private void SetMonitorBadgeText(string shortText, string fullText)
    {
        UsnStatusText.Text = shortText;
        UsnStatusBadge.ToolTip = fullText;
    }

    // At most this many changed nodes are handled per pass; the rest wait for the next one
    private const int MaxChangedPerPass = 1024;
    // Deleted node ids are only looked at for reuse once this many are waiting
    private const int ReclaimBatchMin = 64;

    private DriveSession? _syncSession;
    private FileSystemStore? _syncStore;
    private LiveRowSync<TreeRowViewModel>? _rowSyncInstance;
    private readonly List<int> _reclaimIds = new();
    private readonly HashSet<int> _reclaimSet = new();
    private readonly HashSet<int> _reclaimReferenced = new();

    /// <summary>The row sync; it reads the session and store of the pass that is running (one pass at a time, UI thread).</summary>
    private LiveRowSync<TreeRowViewModel> RowSync => _rowSyncInstance ??= new LiveRowSync<TreeRowViewModel>(
        (snapshot, depth) => new TreeRowViewModel(_syncSession!, _syncStore!, snapshot, depth),
        (parentNodeId, depth, remaining) => TreeRowViewModel.CreateMoreRow(_syncSession!, _syncStore!, parentNodeId, depth, remaining),
        ChildPageSize);

    /// <summary>
    /// Brings this drive's rows in line with the store after live changes. Unless <paramref name="full"/> (the store was
    /// changed from the UI, so the collected changed nodes do not describe it) or the changed-node collection overflowed,
    /// only the rows the changed nodes can affect are looked at (see <see cref="LiveRowSync{TRow}"/>).
    /// </summary>
    private void ApplyLiveStoreUpdatesToVisibleRows(DriveSession session, UsnMonitorEventArgs e, bool full = false)
    {
        long startMs = Environment.TickCount64;
        HashSet<int> activity = session.TakeActivity(MaxChangedPerPass, out bool overflow, out bool more);
        FileSystemStore? store = session.Store;
        if (store == null || !TryGetSessionBlock(session, out int start, out int end))
            return;

        bool fullPass = full || overflow || session.NeedsFullSync;
        session.NeedsFullSync = false;
        store.AdvanceReclaimEpoch();

        // 1. Remove rows of deleted or moved nodes, 2. add rows for new children of expanded folders (kept in step with
        //    the "more items" rows), 3. refresh the sizes, counts and % of parent of the rows that can have changed
        _syncSession = session;
        _syncStore = store;
        end = RowSync.Apply(_visibleRows, start, end, store, fullPass ? null : activity, out bool needsFullNext);
        if (needsFullNext)
            session.NeedsFullSync = true;
        RefreshRootRow(session);

        MarkCutRows();

        // 4. Update bottom status bar totals
        UpdateStatsText();

        // The rows now match the store, so the changed nodes can be placed on them
        PulseActivityRows(store, activity);

        ReclaimNodeIds(session, store, start, end);

        // 5. Update top USN badge (it describes the first drive)
        if (_sessions.Count > 0 && ReferenceEquals(_sessions[0], session))
        {
            string drive = session.RootPath.TrimEnd('\\');
            string modeLabel = e.IsUsnJournalMode ? "NTFS USN Journal" : "Limited Monitor (FileSystemWatcher)";
            SetMonitorBadgeText($"{drive} {(e.IsUsnJournalMode ? "USN live" : "Limited")}: {e.TotalEventsProcessed:N0} updates",
                $"{drive} {modeLabel}: {e.TotalEventsProcessed:N0} updates ({e.LastChangedItem})");
        }

        long endMs = Environment.TickCount64;
        session.RecordPass(endMs, endMs - startMs);

        // Changed nodes that did not fit in this pass wait for the next one
        if (more && session.Monitor is NtfsUsnJournalMonitor monitor && session.TryBeginUiRefresh(e))
            ScheduleLivePass(session, monitor);
    }

    /// <summary>
    /// Flashes, for each changed node, the lowest row that is displayed and contains it (see <see cref="ActivityTargets"/>).
    /// A change under a "more items" cut-off lights the folder's row, not the "more items" row. Skipped when Windows
    /// animations are off. Uses the rows the sync just brought up to date.
    /// </summary>
    private void PulseActivityRows(FileSystemStore store, HashSet<int> activity)
    {
        if (activity.Count == 0 || !SystemParameters.ClientAreaAnimation)
            return;

        long now = Environment.TickCount64;
        foreach (TreeRowViewModel row in ActivityTargets.Resolve(store, activity, RowSync.Displayed))
        {
            row.Pulse(now);
        }
    }

    /// <summary>
    /// Makes ids of deleted nodes reusable once nothing refers to them: the store releases them two passes after the
    /// delete, and only if no row of this drive has them as its node or parent, no cut key holds them, and no operation
    /// that carries node ids across an await or a dialog is running (delete, move, branch refresh, a folder or page
    /// loading in the background, a modal dialog over the window).
    /// </summary>
    private void ReclaimNodeIds(DriveSession session, FileSystemStore store, int start, int end)
    {
        if (store.PendingFreeCount < ReclaimBatchMin ||
            _fileOperationInProgress || _branchCts != null || session.State != DriveSessionState.Ready || !IsMainWindowEnabled())
        {
            return;
        }

        _reclaimIds.Clear();
        if (store.TakeReclaimable(_reclaimIds) == 0)
            return;

        _reclaimSet.Clear();
        _reclaimSet.UnionWith(_reclaimIds);
        _reclaimReferenced.Clear();
        if (!RowSync.FindReferencedIds(_visibleRows, start, end, _reclaimSet, _reclaimReferenced))
        {
            store.ReturnReclaimable(_reclaimIds);
            return;
        }

        // A cut item whose node is gone is no longer cut; its key must not match the node that gets the id next
        _cutKeys.RemoveWhere(k => ReferenceEquals(k.Session, session) && _reclaimSet.Contains(k.NodeId));

        store.ReturnReclaimable(_reclaimIds.Where(_reclaimReferenced.Contains).ToList());
        List<int> safe = _reclaimIds.Where(id => !_reclaimReferenced.Contains(id)).ToList();
        store.CommitReclaim(safe);
        session.ForgetActivity(safe);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    /// <summary>False while a modal dialog owned by the window is open (the dialog disables its owner).</summary>
    private bool IsMainWindowEnabled()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        return handle == IntPtr.Zero || IsWindowEnabled(handle);
    }

    /// <summary>Refreshes every row of the drive (and brings its rows in line with the store): used after the store was changed outside the live updates.</summary>
    private void RefreshSessionRows(DriveSession session)
    {
        FileSystemStore? store = session.Store;
        if (store == null || !TryGetSessionBlock(session, out int start, out int end))
            return;

        _syncSession = session;
        _syncStore = store;
        RowSync.Apply(_visibleRows, start, end, store, null, out _);
        RefreshRootRow(session);
    }

    /// <summary>Totals of the drive of the selected row (else the first drive).</summary>
    private void UpdateStatsText()
    {
        DriveSession? session = (TreeListBox.SelectedItem as TreeRowViewModel)?.Session
            ?? (_sessions.Count > 0 ? _sessions[0] : null);
        FileSystemStore? store = session?.Store;
        if (session == null || store == null)
        {
            StatsTextBlock.Text = string.Empty;
            return;
        }

        NodeRecord rootRec = store.GetNodeSnapshot(0);
        string drive = $"{session.RootPath.TrimEnd('\\')}  Size on disk: {SizeFormatter.FormatBytes(rootRec.Size)}  •  {rootRec.FileCount:N0} files  •  {rootRec.FolderCount:N0} folders";

        // With several rows selected the selection's own total comes first (a folder and its selected descendant count once)
        if (TreeListBox.SelectedItems.Count > 1)
        {
            List<TreeRowViewModel> top = GetSelectedTopLevelRows();
            long selectedSize = top.Sum(r => r.Size);
            StatsTextBlock.Text = $"{TreeListBox.SelectedItems.Count:N0} selected: {SizeFormatter.FormatBytes(selectedSize)}  |  {drive}";
        }
        else
        {
            StatsTextBlock.Text = drive;
        }
    }

    // =========================================================================
    // Elevation
    // =========================================================================

    private void LoadShieldIcon()
    {
        if (ElevationHelper.IsElevated)
            return;

        IntPtr? handle = ElevationHelper.GetShieldIconHandle();
        if (handle == null)
            return;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(handle.Value, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            ShieldImage.Source = source;
            ShieldImage.Visibility = Visibility.Visible;
        }
        catch
        {
            // Text only
        }
        finally
        {
            ElevationHelper.DestroyIconHandle(handle.Value);
        }
    }

    /// <summary>Shows "Run as administrator" while some drive is monitored in limited mode or had unreadable folders, and the app is not elevated.</summary>
    private void UpdateElevationUi()
    {
        bool offer = !ElevationHelper.IsElevated &&
                     _sessions.Any(s => s.IsLimitedMonitoring || (s.Store != null && s.HasInaccessibleFolders));
        ElevateButton.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ElevateButton_Click(object sender, RoutedEventArgs e)
    {
        var reasons = new List<string>();
        string[] limited = _sessions.Where(s => s.IsLimitedMonitoring).Select(s => s.RootPath).ToArray();
        string[] skipped = _sessions.Where(s => s.Store != null && s.HasInaccessibleFolders).Select(s => s.RootPath).ToArray();
        if (limited.Length > 0)
            reasons.Add($"{string.Join(", ", limited)} is monitored in limited mode (not elevated).");
        if (skipped.Length > 0)
            reasons.Add($"Some folders on {string.Join(", ", skipped)} could not be read.");

        PromptAndRelaunchElevated(string.Join(" ", reasons));
    }

    /// <summary>
    /// Asks whether to restart SpaceView with administrator rights (the prompt says that the scan results in memory
    /// are lost and the drives are scanned again) and, on yes, starts the elevated copy and exits this one. Returns true
    /// if the elevated copy was started (the application is then shutting down). Does nothing if already elevated. Any
    /// operation that fails with access denied can call this.
    /// </summary>
    public bool PromptAndRelaunchElevated(string reason)
    {
        if (ElevationHelper.IsElevated)
            return false;

        IReadOnlyList<string> roots = _sessions.RootPaths;
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            this,
            ElevationHelper.BuildPromptText(reason, roots),
            "SpaceView",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return false;

        switch (ElevationHelper.TryRelaunchElevated(roots, out string? error))
        {
            case RelaunchOutcome.Started:
                // Only now release the tray icon, monitors and scans: if the prompt had been declined everything keeps running
                ExitApplication();
                return true;
            case RelaunchOutcome.Declined:
                StatusTextBlock.Text = "Administrator restart was cancelled in the Windows prompt; SpaceView keeps running without elevation.";
                return false;
            default:
                StatusTextBlock.Text = $"Could not restart as administrator: {error}";
                return false;
        }
    }

    // =========================================================================
    // Context Menu Handlers
    // =========================================================================

    /// <summary>
    /// A right-click selects its row (see TreeListBoxItem_PreviewMouseRightButtonDown), but the row is still resolved
    /// from the menu's own placement target so a handler can never act on a different row than the one clicked.
    /// </summary>
    private TreeRowViewModel? GetContextRow(object sender)
    {
        TreeRowViewModel? row;
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement target } } &&
            target.DataContext is TreeRowViewModel clicked)
        {
            row = clicked;
        }
        else
        {
            row = TreeListBox.SelectedItem as TreeRowViewModel;
        }

        // The "more items" row is not a file or folder: no menu action applies to it
        return row is { IsMoreRow: false } ? row : null;
    }

    /// <summary>
    /// The rows a menu command acts on: the whole selection if the clicked row is part of it, else just the clicked row.
    /// Never contains a "more items" row. In the order they are shown.
    /// </summary>
    private List<TreeRowViewModel> GetCommandRows(object sender)
    {
        TreeRowViewModel? clicked = GetContextRow(sender);
        if (clicked == null)
            return new List<TreeRowViewModel>();

        return TreeListBox.SelectedItems.Contains(clicked) ? GetSelectedRowsInOrder() : new List<TreeRowViewModel> { clicked };
    }

    /// <summary>The selected rows (without "more items" rows) in the order they are shown, not in the order they were clicked.</summary>
    private List<TreeRowViewModel> GetSelectedRowsInOrder()
    {
        var selected = new HashSet<object>(TreeListBox.SelectedItems.Cast<object>());
        var rows = new List<TreeRowViewModel>(selected.Count);
        if (selected.Count == 0)
            return rows;

        foreach (TreeRowViewModel row in _visibleRows)
        {
            if (!row.IsMoreRow && selected.Contains(row))
                rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// The selected rows without those that lie inside another selected row (a folder carries its selected descendants
    /// along). Rows below a folder follow it with a greater depth, so one pass in display order finds them.
    /// </summary>
    private List<TreeRowViewModel> GetSelectedTopLevelRows() => RemoveNestedRows(GetSelectedRowsInOrder());

    private List<TreeRowViewModel> RemoveNestedRows(IReadOnlyCollection<TreeRowViewModel> rows)
    {
        var wanted = new HashSet<TreeRowViewModel>(rows);
        var top = new List<TreeRowViewModel>(wanted.Count);
        if (wanted.Count == 0)
            return top;

        bool covering = false;
        int coverDepth = 0;
        foreach (TreeRowViewModel row in _visibleRows)
        {
            // The rows below a folder are the ones that follow it with a greater depth
            if (covering && row.Depth > coverDepth)
                continue;

            covering = false;
            if (wanted.Contains(row))
            {
                top.Add(row);
                covering = true;
                coverDepth = row.Depth;
            }
        }
        return top;
    }

    private void TreeListBoxItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Not handled: the context menu must still open
        if (sender is ListBoxItem item)
        {
            // Inside the selection the selection stays whole (the menu acts on all of it); outside it the clicked row alone is selected
            if (!item.IsSelected)
            {
                TreeListBox.SelectedItems.Clear();
                item.IsSelected = true;
            }
            item.Focus();
        }
    }

    private void RowContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;

        var row = (menu.PlacementTarget as FrameworkElement)?.DataContext as TreeRowViewModel;
        bool applies = row is { IsMoreRow: false };
        List<TreeRowViewModel> rows = applies && TreeListBox.SelectedItems.Contains(row)
            ? GetSelectedRowsInOrder()
            : new List<TreeRowViewModel>();
        bool multi = rows.Count > 1;
        bool canOperate = applies && !_fileOperationInProgress && GetMovableRows(rows).Count > 0;

        foreach (FrameworkElement item in menu.Items.OfType<FrameworkElement>())
        {
            // The menu is shared by all rows, so every item is set each time it opens
            string? tag = item.Tag as string;
            if (tag == "RemoveDrive")
            {
                item.Visibility = row is { IsRoot: true } && !multi ? Visibility.Visible : Visibility.Collapsed;
                continue;
            }

            if (item is not MenuItem menuItem)
                continue;

            switch (tag)
            {
                case "RefreshBranch":
                    // On a drive row this is a rescan of that drive
                    menuItem.Header = row is { IsRoot: true } && !multi ? "Rescan Drive" : "Refresh Branch";
                    menuItem.IsEnabled = applies && (multi ? GetRefreshableFolders(rows).Count > 0 : CanRefreshBranch(row));
                    break;
                case "Delete":
                case "Cut":
                case "MoveTo":
                    menuItem.IsEnabled = canOperate;
                    break;
                case "Paste":
                    menuItem.IsEnabled = applies && !multi && CanPasteInto(row, out _);
                    break;
                case "SingleOnly":
                    // Open in Explorer and Properties show one item
                    menuItem.IsEnabled = applies && !multi;
                    break;
                default:
                    menuItem.IsEnabled = applies;
                    break;
            }
        }
    }

    private void TreeListBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (e.Key == Key.F5 && modifiers == ModifierKeys.None)
        {
            List<TreeRowViewModel> selected = GetSelectedRowsInOrder();
            if (selected.Count == 1 && CanRefreshBranch(selected[0]))
            {
                e.Handled = true;
                _ = RefreshBranchAsync(selected[0]);
            }
            else if (selected.Count > 1 && GetRefreshableFolders(selected).Count > 0)
            {
                e.Handled = true;
                _ = RefreshBranchesAsync(selected);
            }
        }
        else if (e.Key == Key.Delete && modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = DeleteRowsAsync(GetSelectedRowsInOrder());
        }
        else if (e.Key == Key.X && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            CutRows(GetSelectedRowsInOrder());
        }
        else if (e.Key == Key.V && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            List<TreeRowViewModel> selected = GetSelectedRowsInOrder();
            _ = PasteIntoAsync(selected.Count == 1 ? selected[0] : null);
        }
        else if (e.Key == Key.Escape && modifiers == ModifierKeys.None && _cutPaths.Count > 0)
        {
            e.Handled = true;
            ClearCut(clearClipboard: true);
            StatusTextBlock.Text = "Cut cancelled.";
        }
    }

    private void MenuOpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var row = GetContextRow(sender);
        if (row == null)
            return;

        string fullPath = row.GetFullPath();
        try
        {
            if (row.IsDirectory)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{fullPath}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Could not open Explorer: {ex.Message}", "SpaceView", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        List<TreeRowViewModel> rows = GetCommandRows(sender);
        if (rows.Count == 0)
            return;

        try
        {
            // Several rows: one path per line
            string path = string.Join(Environment.NewLine, rows.Select(r => r.GetFullPath()));
            System.Windows.Clipboard.SetText(path);
            StatusTextBlock.Text = rows.Count == 1 ? $"Copied path: {path}" : $"Copied {rows.Count:N0} paths.";
        }
        catch
        {
            // Ignore clipboard contention
        }
    }

    private void MenuProperties_Click(object sender, RoutedEventArgs e)
    {
        var row = GetContextRow(sender);
        if (row == null)
            return;

        string path = row.GetFullPath();
        NtfsNative.ShowFileProperties(path);
    }

    private void MenuRemoveDrive_Click(object sender, RoutedEventArgs e)
    {
        var row = GetContextRow(sender);
        if (row is { IsRoot: true })
            RemoveSession(row.Session);
    }

    /// <summary>
    /// Takes a drive out of the view: cancels its scan, stops its monitor, drops its store (so the memory can be
    /// reclaimed) and removes its rows. Nothing else is touched.
    /// </summary>
    private void RemoveSession(DriveSession session)
    {
        if (ReferenceEquals(_branchSession, session))
            _branchCts?.Cancel();

        if (_rootRows.Remove(session, out TreeRowViewModel? root))
        {
            int rootIndex = _visibleRows.IndexOf(root);
            if (rootIndex >= 0)
                _visibleRows.RemoveRange(rootIndex, 1 + CountDescendantRows(rootIndex));
        }

        _sessions.Remove(session);
        string rootPath = session.RootPath;
        session.Dispose();

        // The dropdown may still show this drive as chosen; clear that so choosing it again adds it back
        if (DriveComboBox.SelectedItem is DriveItemViewModel chosen && DriveSession.SameRoot(chosen.Name, rootPath))
        {
            _suppressDriveSelectionChanged = true;
            try
            {
                DriveComboBox.SelectedItem = null;
            }
            finally
            {
                _suppressDriveSelectionChanged = false;
            }
        }

        RescanButton.IsEnabled = _sessions.Count > 0;
        if (_sessions.Count == 0)
        {
            OverlayTitleText.Text = _initialOverlayTitle;
            OverlaySubtitleText.Text = _initialOverlaySubtitle;
            EmptyStateOverlay.Visibility = Visibility.Visible;
            DrivePlaceholderText.Visibility = Visibility.Visible;
        }

        ResolveCutKeys();
        StatusTextBlock.Text = $"Removed {rootPath} from the view.";
        UpdateStatsText();
        UpdateMonitorBadge();
        UpdateElevationUi();

        // The store's big arrays are only freed by a full collection; ask for one without blocking the window
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
        UpdateMemoryUsageDisplay();
    }

    private void MenuDeleteToRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        _ = DeleteRowsAsync(GetCommandRows(sender));
    }

    /// <summary>
    /// The rows a delete, cut or move can act on: the selection without drive rows and "more items" rows, without rows
    /// inside another selected row, and without rows of a drive that is being scanned, is not available or has no tree.
    /// </summary>
    private List<TreeRowViewModel> GetMovableRows(IReadOnlyCollection<TreeRowViewModel> selection)
    {
        var candidates = selection.Where(r => !r.IsRoot && !r.IsMoreRow && IsSessionUsable(r.Session, r.Store)).ToList();
        return RemoveNestedRows(candidates);
    }

    /// <summary>A drive whose tree can be trusted to show the files: scanned, not being scanned again and still there.</summary>
    private bool IsSessionUsable(DriveSession session, FileSystemStore? store) =>
        store != null && IsLive(session, store) && session.State == DriveSessionState.Ready && !session.IsUnavailable;

    /// <summary>Moves the rows to the Recycle Bin, one after the other, after a single confirmation that states the count and total size.</summary>
    private async Task DeleteRowsAsync(IReadOnlyCollection<TreeRowViewModel> selection)
    {
        if (selection.Count == 0 || _fileOperationInProgress)
            return;

        List<TreeRowViewModel> rows = GetMovableRows(selection);
        if (rows.Count == 0)
        {
            if (selection.Any(r => r.IsRoot))
                System.Windows.MessageBox.Show(this, "Cannot delete the root volume.", "SpaceView", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string prompt;
        if (rows.Count == 1)
        {
            TreeRowViewModel only = rows[0];
            string kind = only.IsDirectory ? "folder" : "file";
            prompt = $"Move the {kind} \"{only.Name}\" ({only.SizeText}) to the Recycle Bin?\n\n{only.GetFullPath()}";
        }
        else
        {
            const int listed = 8;
            string names = string.Join("\n", rows.Take(listed).Select(r => r.GetFullPath()));
            string more = rows.Count > listed ? $"\n... and {rows.Count - listed:N0} more" : string.Empty;
            string skipped = selection.Count(r => !r.IsMoreRow) > rows.Count
                ? "\n\nDrive rows and items inside another selected folder are not listed separately."
                : string.Empty;
            prompt = $"Move {rows.Count:N0} items ({SizeFormatter.FormatBytes(rows.Sum(r => r.Size))}) to the Recycle Bin?\n\n{names}{more}{skipped}";
        }

        MessageBoxResult answer = System.Windows.MessageBox.Show(
            this, prompt, "SpaceView", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes || _fileOperationInProgress)
            return;

        _fileOperationInProgress = true;
        int deleted = 0;
        string currentPath = string.Empty;
        string currentName = string.Empty;
        bool stopped = false;
        try
        {
            // One shell operation at a time; the tree follows after every item, so a stop part-way leaves it correct
            for (int i = 0; i < rows.Count; i++)
            {
                TreeRowViewModel row = rows[i];
                DriveSession session = row.Session;
                FileSystemStore? store = row.Store;
                if (store == null || !IsLive(session, store))
                    break; // the drive's tree was replaced meanwhile (rescan, removed drive)

                currentPath = row.GetFullPath();
                currentName = row.Name;
                StatusTextBlock.Text = rows.Count == 1
                    ? $"Moving {currentPath} to the Recycle Bin..."
                    : $"Moving {currentPath} to the Recycle Bin ({i + 1:N0} of {rows.Count:N0})...";
                bool gone = await DeleteToRecycleBinAsync(currentPath, row.IsDirectory);

                // Back on the UI thread. The tree may have been replaced by a rescan while the shell was working.
                if (gone && IsLive(session, store))
                {
                    store.DeleteNodeById(row.NodeId);
                    ApplyLocalDeletionToView(session, row.Name);
                    deleted++;
                }
                else if (!gone)
                {
                    // The user cancelled in the Windows dialog (or the shell stopped part-way): nothing is removed from the tree
                    stopped = true;
                    break;
                }
            }

            if (stopped)
            {
                string what = rows.Count == 1 ? currentPath : $"Deleted {deleted:N0} of {rows.Count:N0} items; {currentPath}";
                StatusTextBlock.Text = ElevationHelper.IsElevated
                    ? $"{what} was not deleted (cancelled or incomplete)."
                    : $"{what} was not deleted (cancelled or incomplete). If access was denied, \"Run as administrator\" may help.";
                if (!ElevationHelper.IsElevated)
                    ElevateButton.Visibility = Visibility.Visible;
            }
            else if (rows.Count == 1)
            {
                if (deleted == 1)
                    StatusTextBlock.Text = $"Moved {currentPath} to the Recycle Bin.";
            }
            else
            {
                StatusTextBlock.Text = $"Moved {deleted:N0} of {rows.Count:N0} items to the Recycle Bin.";
            }
        }
        catch (Exception ex) when (ElevationHelper.IsAccessDenied(ex) && !ElevationHelper.IsElevated)
        {
            StatusTextBlock.Text = $"Could not delete {currentPath}: access denied.";
            PromptAndRelaunchElevated($"Could not delete \"{currentName}\": access was denied.");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Could not delete item: {ex.Message}", "SpaceView", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _fileOperationInProgress = false;
        }
    }

    /// <summary>
    /// Sends a file or folder to the Recycle Bin on a dedicated STA thread, so a large delete does not freeze the UI
    /// while the Windows progress/confirmation dialogs (UIOption.AllDialogs) still work: the shell file operation
    /// needs a single-threaded apartment, which thread-pool threads are not. Completes with whether the item is gone;
    /// a cancel in the dialog is not an error (UICancelOption.DoNothing) and simply leaves the item in place.
    /// </summary>
    private static Task<bool> DeleteToRecycleBinAsync(string path, bool isDirectory)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (isDirectory)
                {
                    if (Directory.Exists(path))
                        FileSystem.DeleteDirectory(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
                    completion.SetResult(!Directory.Exists(path));
                }
                else
                {
                    if (File.Exists(path))
                        FileSystem.DeleteFile(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
                    completion.SetResult(!File.Exists(path));
                }
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "SpaceView recycle bin delete"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>Updates rows and stats after the store itself was changed from the UI (no journal event involved).</summary>
    private void ApplyLocalDeletionToView(DriveSession session, string itemName)
    {
        ApplyLiveStoreUpdatesToVisibleRows(session, new UsnMonitorEventArgs
        {
            BatchEventsProcessed = 1,
            TotalEventsProcessed = (session.Monitor?.TotalEventsProcessed ?? 0) + 1,
            LastChangedItem = itemName,
            IsUsnJournalMode = session.Monitor?.IsUsnJournalActive ?? false
        }, full: true);
    }

    // =========================================================================
    // Refresh Branch
    // =========================================================================

    private bool CanRefreshBranch(TreeRowViewModel? row)
    {
        // Not while a delete or move is working on the files (it refreshes the branches itself when it is done)
        if (row == null || !row.IsDirectory || row.IsMoreRow || row.IsLoadingChildren || _fileOperationInProgress)
            return false;

        DriveSession session = row.Session;
        // A drive row is a rescan of the drive (not while it is already being scanned); a folder needs a finished tree
        // of a drive that is there (a missing volume would make every folder look vanished)
        return row.IsRoot
            ? !session.IsBusy
            : session.Store != null && session.State == DriveSessionState.Ready && !session.IsUnavailable && _branchCts == null;
    }

    /// <summary>The folders among the rows that can be refreshed one after the other: no drive rows (a rescan is a different thing) and none inside another selected folder.</summary>
    private List<TreeRowViewModel> GetRefreshableFolders(IReadOnlyCollection<TreeRowViewModel> rows)
    {
        var folders = rows.Where(r => !r.IsRoot && r.IsDirectory && CanRefreshBranch(r)).ToList();
        return RemoveNestedRows(folders);
    }

    private void MenuRefreshBranch_Click(object sender, RoutedEventArgs e)
    {
        List<TreeRowViewModel> rows = GetCommandRows(sender);
        if (rows.Count > 1)
        {
            _ = RefreshBranchesAsync(rows);
        }
        else if (rows.Count == 1 && CanRefreshBranch(rows[0]))
        {
            _ = RefreshBranchAsync(rows[0]);
        }
    }

    /// <summary>Refreshes several folders, one after the other (a branch refresh uses the one branch slot), skipping those inside another.</summary>
    private async Task RefreshBranchesAsync(IReadOnlyCollection<TreeRowViewModel> rows)
    {
        List<TreeRowViewModel> folders = GetRefreshableFolders(rows);
        int refreshed = 0;
        foreach (TreeRowViewModel folder in folders)
        {
            // The slot may have been taken meanwhile (a rescan, a delete): then the rest is not refreshed
            if (!CanRefreshBranch(folder) || folder.Store is not { } store || !IsLive(folder.Session, store))
                break;

            if (await RefreshNodeBranchAsync(folder.Session, store, folder.NodeId))
                refreshed++;
        }

        if (folders.Count > 1)
            StatusTextBlock.Text = $"Refreshed {refreshed:N0} of {folders.Count:N0} folders.";
    }

    private async Task RefreshBranchAsync(TreeRowViewModel row)
    {
        DriveSession session = row.Session;
        FileSystemStore? store = row.Store;
        if (!CanRefreshBranch(row))
            return;

        // The volume root is just a full rescan of that drive
        if (row.IsRoot)
        {
            await StartSessionScanAsync(session, session.StaleReason);
            return;
        }

        if (store == null || !IsLive(session, store))
            return;

        await RefreshNodeBranchAsync(session, store, row.NodeId);
    }

    /// <summary>
    /// Rescans one folder of a drive's tree (not the drive root) and grafts the result in; the folder's row, if it is
    /// visible, is rebuilt. The drive's journal updates are held back from before the scan until the graft is done and then
    /// replayed, and that hold is released on every path. Returns true if the folder was refreshed (or found gone and
    /// removed), false if nothing could be done.
    /// </summary>
    private async Task<bool> RefreshNodeBranchAsync(DriveSession session, FileSystemStore store, int nodeId)
    {
        string path = store.GetFullPath(nodeId);
        if (!Directory.Exists(path))
        {
            RemoveVanishedBranch(session, store, nodeId, path);
            return true;
        }

        var cts = new CancellationTokenSource();
        _branchCts = cts;
        _branchSession = session;
        IDisposable? journalPause = null;
        StatusTextBlock.Text = $"Refreshing {path}...";

        try
        {
            // Hold back journal updates from before the scan starts until the graft is done, so changes made
            // meanwhile are applied (replayed) to the new nodes rather than lost with the old ones.
            NtfsUsnJournalMonitor? monitor = session.Monitor;
            if (monitor != null)
            {
                journalPause = await Task.Run(monitor.PauseApplyingChanges);
            }
            cts.Token.ThrowIfCancellationRequested();

            var progress = new Progress<ScanProgressReport>(report =>
            {
                if (!cts.IsCancellationRequested)
                    StatusTextBlock.Text = $"Refreshing {path}: {report.ScannedNodes:N0} items found...";
            });

            BranchScanResult scan = await VolumeScanner.ScanBranchAsync(path, progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            if (!IsLive(session, store))
                return false;

            NodeRecord rec = store.GetNodeSnapshot(nodeId);
            if (rec.IsDeleted || !rec.IsDirectory)
            {
                StatusTextBlock.Text = $"{path} is no longer in the tree; refresh skipped.";
                return false;
            }

            if (!scan.RootListed)
            {
                if (!Directory.Exists(path))
                {
                    RemoveVanishedBranch(session, store, nodeId, path);
                    return true;
                }

                StatusTextBlock.Text = $"Could not read {path} (access denied?); keeping the previous data.";
                return false;
            }

            GraftAndRebuildRows(session, store, nodeId, scan);

            string inaccessible = scan.InaccessibleDirectories > 0
                ? $"  •  {scan.InaccessibleDirectories:N0} inaccessible folder(s) skipped"
                : string.Empty;
            NodeRecord after = store.GetNodeSnapshot(nodeId);
            StatusTextBlock.Text =
                $"Refreshed {path} in {scan.Duration.TotalSeconds:0.00}s: {SizeFormatter.FormatBytes(after.Size)}, {after.FileCount:N0} files, {after.FolderCount:N0} folders{inaccessible}";
            return true;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a full scan, which reports its own status
            return false;
        }
        catch (Exception ex)
        {
            if (IsLive(session, store))
                StatusTextBlock.Text = $"Could not refresh {path}: {ex.Message}";
            return false;
        }
        finally
        {
            journalPause?.Dispose();
            if (ReferenceEquals(_branchCts, cts))
            {
                _branchCts = null;
                _branchSession = null;
            }
            cts.Dispose();
        }
    }

    /// <summary>The folder is gone from disk: drop its node (and rows) the same way a delete would.</summary>
    private void RemoveVanishedBranch(DriveSession session, FileSystemStore store, int nodeId, string path)
    {
        if (!IsLive(session, store))
            return;

        string name = store.GetName(nodeId);
        store.DeleteNodeById(nodeId);
        ApplyLocalDeletionToView(session, name);
        StatusTextBlock.Text = $"{path} no longer exists; removed from the tree.";
    }

    /// <summary>The visible row of a node, or null (the node has no row on screen, or is a "more items" row).</summary>
    private TreeRowViewModel? FindRow(DriveSession session, int nodeId)
    {
        if (nodeId < 0)
            return null;

        var key = new RowKey(session, nodeId);
        foreach (TreeRowViewModel row in _visibleRows)
        {
            if (!row.IsMoreRow && row.Key == key)
                return row;
        }
        return null;
    }

    /// <summary>
    /// Swaps the scanned branch into the store and brings the rows in line: stale descendant rows are replaced,
    /// sub-folders that were expanded (matched by name path) are re-expanded, every visible row of the drive is refreshed
    /// (ancestor sizes and sibling percentages changed), and selection and scroll position are kept. The folder may
    /// have no row on screen (then only the store and the numbers change).
    /// </summary>
    private void GraftAndRebuildRows(DriveSession session, FileSystemStore store, int nodeId, BranchScanResult scan)
    {
        TreeRowViewModel? row = FindRow(session, nodeId);
        int rowIndex = row != null ? _visibleRows.IndexOf(row) : -1;
        HashSet<string>? expandedSubfolders = rowIndex >= 0 && row!.IsExpanded
            ? CaptureExpandedSubfolders(rowIndex)
            : null;
        // Selected rows below the folder get new node ids: they are found again by name path
        List<string> selectedPaths = rowIndex >= 0
            ? CaptureSelectedPaths(rowIndex, rowIndex + 1 + CountDescendantRows(rowIndex))
            : new List<string>();
        ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(TreeListBox);
        double scrollOffset = scrollViewer?.VerticalOffset ?? 0;

        if (!store.GraftBranch(nodeId, scan.Store))
            throw new InvalidOperationException("The folder is no longer a live directory in the tree.");

        // The rebuilt branch is not activity; node ids collected before the graft no longer describe it
        session.ClearActivity();

        if (rowIndex >= 0)
        {
            // The graft replaced every descendant node: drop all old descendant rows and rebuild the expanded ones
            var newRows = new List<TreeRowViewModel>();
            if (row!.IsExpanded)
            {
                AppendChildRows(newRows, session, store, nodeId, row.Depth + 1, string.Empty, expandedSubfolders);
            }
            _visibleRows.ReplaceRange(rowIndex + 1, CountDescendantRows(rowIndex), newRows);
            ReselectPaths(rowIndex, selectedPaths, fallback: row);
        }

        ResolveCutKeys();
        RefreshSessionRows(session);
        UpdateStatsText();

        if (scrollViewer != null)
        {
            // Rows above the branch are unchanged, so the old offset is still right once layout has caught up
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => scrollViewer.ScrollToVerticalOffset(scrollOffset));
        }
    }

    /// <summary>Name paths (relative to the row, '\' separated) of the expanded folders below <paramref name="rowIndex"/>.</summary>
    private HashSet<string> CaptureExpandedSubfolders(int rowIndex)
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int baseDepth = _visibleRows[rowIndex].Depth;
        var names = new List<string>();

        for (int i = rowIndex + 1; i < _visibleRows.Count && _visibleRows[i].Depth > baseDepth; i++)
        {
            TreeRowViewModel r = _visibleRows[i];
            if (r.IsMoreRow)
                continue; // not a folder: its text must not become part of a name path

            int level = Math.Min(r.Depth - baseDepth - 1, names.Count);
            names.RemoveRange(level, names.Count - level);
            names.Add(r.Name);

            if (r.IsDirectory && r.IsExpanded)
                expanded.Add(string.Join('\\', names));
        }
        return expanded;
    }

    private static void AppendChildRows(
        List<TreeRowViewModel> target,
        DriveSession session,
        FileSystemStore store,
        int parentNodeId,
        int childDepth,
        string parentPath,
        HashSet<string>? expandedSubfolders)
    {
        foreach (TreeRowViewModel child in CreateChildRows(session, store, parentNodeId, childDepth))
        {
            target.Add(child);
            if (expandedSubfolders == null || !child.IsDirectory)
                continue;

            string childPath = parentPath.Length == 0 ? child.Name : parentPath + "\\" + child.Name;
            if (expandedSubfolders.Contains(childPath))
            {
                child.IsExpanded = true;
                AppendChildRows(target, session, store, child.NodeId, childDepth + 1, childPath, expandedSubfolders);
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        DependencyObject? current = start;
        while (current != null && current is not T)
        {
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return current as T;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;

            T? nested = FindDescendant<T>(child);
            if (nested != null)
                return nested;
        }
        return null;
    }

    // =========================================================================
    // System Tray & Window Lifecycle
    // =========================================================================

    private void MinimizeToTrayButton_Click(object sender, RoutedEventArgs e)
    {
        MinimizeToTray();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            MinimizeToTray();
        }
        else
        {
            _lastNonMinimizedState = WindowState;
        }
    }

    private void MinimizeToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void ShowAndActivateWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = _lastNonMinimizedState == WindowState.Minimized ? WindowState.Normal : _lastNonMinimizedState;
        Activate();
    }

    private void ToggleWindowVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            MinimizeToTray();
        }
        else
        {
            ShowAndActivateWindow();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting)
        {
            _isExiting = true;
            CleanupResources();
            System.Windows.Application.Current.Shutdown();
        }
    }

    private void ExitApplication()
    {
        _isExiting = true;
        CleanupResources();
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Stops every scan and monitor of every drive and releases the tray icon.</summary>
    private void CleanupResources()
    {
        _memoryTimer.Stop();
        ReleaseClipboardNotifications();
        _branchCts?.Cancel();
        foreach (DriveSession session in _sessions)
        {
            session.Dispose();
        }
        _trayManager.Dispose();
    }

    private void UpdateMemoryUsageDisplay()
    {
        long managedBytes = GC.GetTotalMemory(forceFullCollection: false);
        long processBytes = 0;
        try
        {
            using Process process = Process.GetCurrentProcess();
            processBytes = process.WorkingSet64;
        }
        catch
        {
            // Show the managed figure only
        }

        int nodeCount = 0;
        long storeBytes = 0;
        var perDrive = new List<string>();
        foreach (DriveSession session in _sessions)
        {
            FileSystemStore? store = session.Store;
            if (store == null)
                continue;

            nodeCount += store.TotalNodeCount;
            long bytes = store.EstimatedMemoryBytes;
            storeBytes += bytes;
            perDrive.Add($"{session.RootPath.TrimEnd('\\')} {SizeFormatter.FormatBytes(bytes)}");
        }

        string processText = processBytes > 0 ? $"  |  Process: {SizeFormatter.FormatBytes(processBytes)}" : string.Empty;
        MemoryTextBlock.Text = nodeCount > 0
            ? $"Nodes: {nodeCount:N0}  |  Stores ~{SizeFormatter.FormatBytes(storeBytes)}" +
              (perDrive.Count > 1 ? $" ({string.Join(", ", perDrive)})" : string.Empty) +
              $"  |  Managed RAM: {SizeFormatter.FormatBytes(managedBytes)}{processText}"
            : $"Managed RAM: {SizeFormatter.FormatBytes(managedBytes)}{processText}";
    }
}
