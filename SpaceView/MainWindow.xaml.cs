using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void InsertRange(int index, IReadOnlyList<T> newItems)
    {
        if (newItems.Count == 0)
            return;

        CheckReentrancy();
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
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0)
            return;

        CheckReentrancy();
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
    }
}

public partial class MainWindow : Window
{
    private readonly BulkObservableCollection<TreeRowViewModel> _visibleRows = new();
    private readonly TrayIconManager _trayManager;
    private readonly DispatcherTimer _memoryTimer;

    private FileSystemStore? _currentStore;
    private NtfsUsnJournalMonitor? _usnMonitor;
    private CancellationTokenSource? _scanCts;
    private WindowState _lastNonMinimizedState = WindowState.Normal;
    private bool _isExiting;
    private int _uiRefreshPending;

    public MainWindow()
    {
        InitializeComponent();

        TreeListBox.ItemsSource = _visibleRows;

        _trayManager = new TrayIconManager(
            toggleMainWindowAction: ToggleWindowVisibility,
            rescanAction: () => Dispatcher.Invoke(RescanCurrentDrive),
            exitApplicationAction: () => Dispatcher.Invoke(ExitApplication));

        // Convert System.Drawing.Icon to WPF ImageSource for Window Icon
        Icon = Imaging.CreateBitmapSourceFromHIcon(
            _trayManager.AppIcon.Handle,
            Int32Rect.Empty,
             System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        LoadDrivesIntoComboBox(preserveSelection: false);

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
            DriveComboBox.Focus();
            DriveComboBox.IsDropDownOpen = true;
        };
    }

    private void LoadDrivesIntoComboBox(bool preserveSelection)
    {
        string? previousDriveName = (DriveComboBox.SelectedItem as DriveItemViewModel)?.Name;
        var drives = DriveItemViewModel.GetReadyDrives();
        DriveComboBox.ItemsSource = drives;

        if (preserveSelection && previousDriveName != null)
        {
            var match = drives.FirstOrDefault(d => string.Equals(d.Name, previousDriveName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                DriveComboBox.SelectedItem = match;
            }
        }
    }

    private async void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveComboBox.SelectedItem is not DriveItemViewModel selectedDrive)
        {
            DrivePlaceholderText.Visibility = Visibility.Visible;
            RescanButton.IsEnabled = false;
            return;
        }

        DrivePlaceholderText.Visibility = Visibility.Collapsed;
        RescanButton.IsEnabled = true;

        await StartDriveScanAsync(selectedDrive);
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e)
    {
        if (DriveComboBox.SelectedItem is DriveItemViewModel selectedDrive)
        {
            await StartDriveScanAsync(selectedDrive);
        }
    }

    private void RefreshDrivesButton_Click(object sender, RoutedEventArgs e)
    {
        LoadDrivesIntoComboBox(preserveSelection: true);
        if (DriveComboBox.SelectedItem == null)
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
        if (DriveComboBox.SelectedItem is DriveItemViewModel selectedDrive)
        {
            _ = StartDriveScanAsync(selectedDrive);
        }
    }

    private async Task StartDriveScanAsync(DriveItemViewModel driveItem)
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        CancellationToken ct = _scanCts.Token;

        StopUsnMonitor();

        // Show scanning state
        EmptyStateOverlay.Visibility = Visibility.Visible;
        ScanProgressBar.Visibility = Visibility.Visible;
        OverlayTitleText.Text = $"Scanning {driveItem.DisplayTitle}...";
        OverlaySubtitleText.Text = "Enumerating file system records and calculating directory consumption...";
        UsnStatusBadge.Visibility = Visibility.Collapsed;
        StatusTextBlock.Text = $"Scanning {driveItem.Name}...";
        StatsTextBlock.Text = string.Empty;

        var progress = new Progress<ScanProgressReport>(report =>
        {
            if (!ct.IsCancellationRequested)
            {
                StatusTextBlock.Text = report.CurrentStatus;
                OverlaySubtitleText.Text = $"{report.ScannedNodes:N0} items scanned ({report.ElapsedMilliseconds / 1000.0:0.0}s elapsed)...";
            }
        });

        try
        {
            ScanResult result = await VolumeScanner.ScanDriveAsync(driveItem.DriveInfo, progress, ct);
            if (ct.IsCancellationRequested)
                return;

            _currentStore = result.Store;
            PopulateInitialTree(_currentStore);

            EmptyStateOverlay.Visibility = Visibility.Collapsed;
            ScanProgressBar.Visibility = Visibility.Collapsed;

            NodeRecord rootRec = _currentStore.GetNodeSnapshot(0);
            StatusTextBlock.Text = $"Scanned {_currentStore.RootPath} in {result.Duration.TotalSeconds:0.00}s";
            StatsTextBlock.Text = $"Total: {SizeFormatter.FormatBytes(rootRec.Size)}  •  {rootRec.FileCount:N0} files  •  {rootRec.FolderCount:N0} folders";
            UpdateMemoryUsageDisplay();

            // Start real-time monitoring if NTFS (or fallback watcher)
            if (result.IsNtfs)
            {
                _usnMonitor = new NtfsUsnJournalMonitor(
                    _currentStore,
                    result.UsnJournalReady,
                    result.UsnJournalId,
                    result.StartUsn);

                _usnMonitor.ChangesDetected += OnUsnChangesDetected;
                _usnMonitor.Start();

                UsnStatusBadge.Visibility = Visibility.Visible;
                if (result.UsnJournalReady)
                {
                    UsnStatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Emerald green
                    UsnStatusText.Text = "NTFS USN Journal Active (Live Monitoring)";
                }
                else
                {
                    UsnStatusDot.Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // Cyan
                    UsnStatusText.Text = "Live FileSystem Monitoring Active";
                }
            }
            else
            {
                UsnStatusBadge.Visibility = Visibility.Visible;
                UsnStatusDot.Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                UsnStatusText.Text = $"{driveItem.DriveFormat} Volume (Non-NTFS)";
            }
        }
        catch (OperationCanceledException)
        {
            // Scan superseded by another selection
        }
        catch (Exception ex)
        {
            ScanProgressBar.Visibility = Visibility.Collapsed;
            OverlayTitleText.Text = "Scan Failed";
            OverlaySubtitleText.Text = ex.Message;
            StatusTextBlock.Text = $"Error scanning {driveItem.Name}: {ex.Message}";
        }
    }

    private void PopulateInitialTree(FileSystemStore store)
    {
        var initialList = new List<TreeRowViewModel>(256);

        // Add Root node expanded
        var rootRow = new TreeRowViewModel(store, store.RootNodeId, depth: 0, isExpanded: true);
        initialList.Add(rootRow);

        // Add top-level children sorted descending by size
        int[] rootChildren = store.GetSortedChildren(store.RootNodeId);
        foreach (int childId in rootChildren)
        {
            initialList.Add(new TreeRowViewModel(store, childId, depth: 1, isExpanded: false));
        }

        _visibleRows.ReplaceAll(initialList);
    }

    private void ExpandToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton btn && btn.Tag is TreeRowViewModel row)
        {
            ToggleRowExpansion(row);
            e.Handled = true;
        }
    }

    private void TreeListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TreeListBox.SelectedItem is TreeRowViewModel row && row.IsDirectory)
        {
            ToggleRowExpansion(row);
        }
    }

    private void ToggleRowExpansion(TreeRowViewModel row)
    {
        if (_currentStore == null || !row.IsDirectory)
            return;

        int rowIndex = _visibleRows.IndexOf(row);
        if (rowIndex < 0)
            return;

        if (row.IsExpanded)
        {
            // Collapse: remove all contiguous descendant rows that have Depth > row.Depth
            int removeStart = rowIndex + 1;
            int removeCount = 0;
            while (removeStart + removeCount < _visibleRows.Count &&
                   _visibleRows[removeStart + removeCount].Depth > row.Depth)
            {
                removeCount++;
            }

            row.IsExpanded = false;
            if (removeCount > 0)
            {
                _visibleRows.RemoveRange(removeStart, removeCount);
            }
        }
        else
        {
            // Expand: query sorted children from compact store and insert them directly below rowIndex
            int[] childIds = _currentStore.GetSortedChildren(row.NodeId);
            row.IsExpanded = true;

            if (childIds.Length > 0)
            {
                int childDepth = row.Depth + 1;
                var childRows = new List<TreeRowViewModel>(childIds.Length);
                foreach (int childId in childIds)
                {
                    childRows.Add(new TreeRowViewModel(_currentStore, childId, childDepth, isExpanded: false));
                }
                _visibleRows.InsertRange(rowIndex + 1, childRows);
            }
        }
    }

    // =========================================================================
    // Live NTFS USN Change Journal UI Synchronization
    // =========================================================================

    private void OnUsnChangesDetected(object? sender, UsnMonitorEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _uiRefreshPending, 1, 0) != 0)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try
            {
                ApplyLiveStoreUpdatesToVisibleRows(e);
            }
            finally
            {
                Interlocked.Exchange(ref _uiRefreshPending, 0);
            }
        });
    }

    private void ApplyLiveStoreUpdatesToVisibleRows(UsnMonitorEventArgs e)
    {
        if (_currentStore == null || _visibleRows.Count == 0)
            return;

        // 1. Remove any visible rows that were deleted (and their descendants if a folder was deleted)
        for (int i = _visibleRows.Count - 1; i >= 0; i--)
        {
            var row = _visibleRows[i];
            NodeRecord rec = _currentStore.GetNodeSnapshot(row.NodeId);
            if (rec.IsDeleted)
            {
                int removeCount = 1;
                while (i + removeCount < _visibleRows.Count &&
                       _visibleRows[i + removeCount].Depth > row.Depth)
                {
                    removeCount++;
                }
                _visibleRows.RemoveRange(i, removeCount);
            }
        }

        // 2. Check if any expanded folders have newly created children not yet in _visibleRows
        var existingNodeIds = new HashSet<int>(_visibleRows.Select(r => r.NodeId));
        for (int i = 0; i < _visibleRows.Count; i++)
        {
            var row = _visibleRows[i];
            if (row.IsDirectory && row.IsExpanded)
            {
                int[] currentChildren = _currentStore.GetSortedChildren(row.NodeId);
                foreach (int childId in currentChildren)
                {
                    if (!existingNodeIds.Contains(childId))
                    {
                        var newRow = new TreeRowViewModel(_currentStore, childId, row.Depth + 1, isExpanded: false);
                        _visibleRows.Insert(i + 1, newRow);
                        existingNodeIds.Add(childId);
                    }
                }
            }
        }

        // 3. Refresh all visible rows' sizes, file/folder counts, and % of parent
        foreach (var row in _visibleRows)
        {
            row.RefreshFromStore();
        }

        // 4. Update top USN badge & bottom status bar
        NodeRecord rootRec = _currentStore.GetNodeSnapshot(0);
        StatsTextBlock.Text = $"Total: {SizeFormatter.FormatBytes(rootRec.Size)}  •  {rootRec.FileCount:N0} files  •  {rootRec.FolderCount:N0} folders";

        string modeLabel = e.IsUsnJournalMode ? "NTFS USN Journal" : "Live Monitor";
        UsnStatusText.Text = $"{modeLabel}: {e.TotalEventsProcessed:N0} updates ({e.LastChangedItem})";
    }

    // =========================================================================
    // Context Menu Handlers
    // =========================================================================

    private TreeRowViewModel? GetSelectedRow() => TreeListBox.SelectedItem as TreeRowViewModel;

    private void MenuOpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var row = GetSelectedRow();
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
        var row = GetSelectedRow();
        if (row == null)
            return;

        try
        {
            string path = row.GetFullPath();
            System.Windows.Clipboard.SetText(path);
            StatusTextBlock.Text = $"Copied path: {path}";
        }
        catch
        {
            // Ignore clipboard contention
        }
    }

    private void MenuProperties_Click(object sender, RoutedEventArgs e)
    {
        var row = GetSelectedRow();
        if (row == null)
            return;

        string path = row.GetFullPath();
        NtfsNative.ShowFileProperties(path);
    }

    private void MenuDeleteToRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        var row = GetSelectedRow();
        if (row == null || _currentStore == null)
            return;

        if (row.NodeId == 0)
        {
            System.Windows.MessageBox.Show(this, "Cannot delete the root volume.", "SpaceView", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string path = row.GetFullPath();
        try
        {
            if (row.IsDirectory)
            {
                if (Directory.Exists(path))
                {
                    FileSystem.DeleteDirectory(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
                }
                if (!Directory.Exists(path))
                {
                    _currentStore.DeleteNodeById(row.NodeId);
                    ApplyLiveStoreUpdatesToVisibleRows(new UsnMonitorEventArgs
                    {
                        BatchEventsProcessed = 1,
                        TotalEventsProcessed = (_usnMonitor?.TotalEventsProcessed ?? 0) + 1,
                        LastChangedItem = row.Name,
                        IsUsnJournalMode = _usnMonitor?.IsUsnJournalActive ?? false
                    });
                }
            }
            else
            {
                if (File.Exists(path))
                {
                    FileSystem.DeleteFile(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
                }
                if (!File.Exists(path))
                {
                    _currentStore.DeleteNodeById(row.NodeId);
                    ApplyLiveStoreUpdatesToVisibleRows(new UsnMonitorEventArgs
                    {
                        BatchEventsProcessed = 1,
                        TotalEventsProcessed = (_usnMonitor?.TotalEventsProcessed ?? 0) + 1,
                        LastChangedItem = row.Name,
                        IsUsnJournalMode = _usnMonitor?.IsUsnJournalActive ?? false
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Could not delete item: {ex.Message}", "SpaceView", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    private void StopUsnMonitor()
    {
        if (_usnMonitor != null)
        {
            _usnMonitor.ChangesDetected -= OnUsnChangesDetected;
            _usnMonitor.Dispose();
            _usnMonitor = null;
        }
    }

    private void CleanupResources()
    {
        _memoryTimer.Stop();
        _scanCts?.Cancel();
        StopUsnMonitor();
        _trayManager.Dispose();
    }

    private void UpdateMemoryUsageDisplay()
    {
        long managedBytes = GC.GetTotalMemory(forceFullCollection: false);
        int nodeCount = _currentStore?.TotalNodeCount ?? 0;
        MemoryTextBlock.Text = nodeCount > 0
            ? $"Nodes: {nodeCount:N0}  |  Managed RAM: {SizeFormatter.FormatBytes(managedBytes)}"
            : $"Managed RAM: {SizeFormatter.FormatBytes(managedBytes)}";
    }
}
