using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using SpaceView.Core;
using SpaceView.Models;

namespace SpaceView;

// Cut, paste and "Move to..." of files and folders: the cut set and its clipboard format, the shell move and the update
// of the tree afterwards. The rules (what may be moved where, which folders to refresh) are in Core/MoveRules.cs.
public partial class MainWindow
{
    // The cut set: the full paths of the cut items (the truth; they are also on the Windows clipboard as a file drop list
    // with the move effect). The keys are the rows' identity within their stores, resolved from the paths, so the rows
    // show as cut and keep doing so while rows are rebuilt (re-resolved after a graft or a rescan).
    private List<string> _cutPaths = new();
    private readonly HashSet<RowKey> _cutKeys = new();
    private uint _cutClipboardSequence;
    private bool _anyRowMarkedCut;
    private IntPtr _clipboardListenerWindow;

    // =========================================================================
    // Cut state
    // =========================================================================

    private void HookClipboardNotifications()
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowMessageHook);
            if (ShellClipboard.AddListener(source.Handle))
                _clipboardListenerWindow = source.Handle;
        }
    }

    // Sent to every top-level window when a device (a volume, a USB disk) arrives or is removed
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == ShellClipboard.WmClipboardUpdate)
            OnClipboardChanged();
        else if (msg == WmDeviceChange && ((int)wParam == DbtDeviceArrival || (int)wParam == DbtDeviceRemoveComplete))
            OnDeviceChanged();
        return IntPtr.Zero;
    }

    /// <summary>The clipboard content changed: if it is not what the cut put there, the cut is off (a paste would not move what the rows show as cut).</summary>
    private void OnClipboardChanged()
    {
        if (_cutPaths.Count > 0 && ShellClipboard.SequenceNumber != _cutClipboardSequence)
            ClearCut(clearClipboard: false);
    }

    private void ReleaseClipboardNotifications()
    {
        if (_clipboardListenerWindow != IntPtr.Zero)
        {
            ShellClipboard.RemoveListener(_clipboardListenerWindow);
            _clipboardListenerWindow = IntPtr.Zero;
        }
    }

    /// <summary>The cut set is still what the clipboard holds (nothing else was copied or cut since).</summary>
    private bool IsCutTrusted => _cutPaths.Count > 0 && ShellClipboard.SequenceNumber == _cutClipboardSequence;

    private void ClearCut(bool clearClipboard)
    {
        if (clearClipboard && IsCutTrusted)
            ShellClipboard.TryClear();

        _cutPaths = new List<string>();
        _cutKeys.Clear();
        MarkCutRows();
    }

    /// <summary>Looks the cut paths up in the stores again (node ids change when a branch is grafted or a drive is rescanned).</summary>
    private void ResolveCutKeys()
    {
        _cutKeys.Clear();
        foreach (string path in _cutPaths)
        {
            DriveSession? session = FindSessionForPath(path);
            FileSystemStore? store = session?.Store;
            if (session != null && store != null && store.TryFindNodeByPath(path, out int nodeId) && nodeId > 0)
                _cutKeys.Add(new RowKey(session, nodeId));
        }
        MarkCutRows();
    }

    /// <summary>Dims the rows of the cut items and everything shown below a cut folder.</summary>
    private void MarkCutRows()
    {
        if (_cutKeys.Count == 0 && !_anyRowMarkedCut)
            return;

        bool covering = false;
        int coverDepth = 0;
        foreach (TreeRowViewModel row in _visibleRows)
        {
            if (covering && row.Depth > coverDepth)
            {
                row.IsCut = true;
                continue;
            }

            covering = false;
            bool cut = !row.IsMoreRow && _cutKeys.Contains(row.Key);
            row.IsCut = cut;
            if (cut)
            {
                covering = true;
                coverDepth = row.Depth;
            }
        }
        _anyRowMarkedCut = _cutKeys.Count > 0;
    }

    private DriveSession? FindSessionForPath(string path)
    {
        string? root = Path.GetPathRoot(MoveRules.Normalize(path));
        return string.IsNullOrEmpty(root) ? null : _sessions.Find(root);
    }

    // =========================================================================
    // Cut / Paste / Move to
    // =========================================================================

    private void MenuCut_Click(object sender, RoutedEventArgs e) => CutRows(GetCommandRows(sender));

    private void MenuPaste_Click(object sender, RoutedEventArgs e) => _ = PasteIntoAsync(GetContextRow(sender));

    private async void MenuMoveTo_Click(object sender, RoutedEventArgs e)
    {
        List<TreeRowViewModel> rows = GetMovableRows(GetCommandRows(sender));
        if (rows.Count == 0 || _fileOperationInProgress)
            return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = rows.Count == 1 ? $"Move \"{rows[0].Name}\" to..." : $"Move {rows.Count:N0} items to...",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true || string.IsNullOrEmpty(dialog.FolderName))
            return;

        // The dialog is modal: the tree may have changed while it was open, so the paths are read only now
        List<string> paths = GetMovableRows(rows).Select(r => r.GetFullPath()).ToList();
        await MovePathsAsync(paths, dialog.FolderName, fromCut: false);
    }

    /// <summary>Puts the rows on the cut list and on the Windows clipboard (so that Explorer pastes them as a move too).</summary>
    private void CutRows(IReadOnlyCollection<TreeRowViewModel> selection)
    {
        if (_fileOperationInProgress)
            return;

        List<TreeRowViewModel> rows = GetMovableRows(selection);
        if (rows.Count == 0)
        {
            StatusTextBlock.Text = "Nothing to cut: drive rows and drives that are not ready cannot be moved.";
            return;
        }

        List<string> paths = MoveRules.FilterDescendants(rows.Select(r => r.GetFullPath()));
        uint? sequence = ShellClipboard.TrySetFileDrop(paths, ShellClipboard.DropEffectMove);
        if (sequence == null)
        {
            StatusTextBlock.Text = "Could not cut: the clipboard is in use by another program.";
            return;
        }

        // The new content replaced the old; the old cut (if any) is simply overwritten
        _cutPaths = paths;
        _cutClipboardSequence = sequence.Value;
        ResolveCutKeys();
        StatusTextBlock.Text = paths.Count == 1
            ? $"Cut {paths[0]}. Select a folder and paste with Ctrl+V; Esc cancels."
            : $"Cut {paths.Count:N0} items. Select a folder and paste with Ctrl+V; Esc cancels.";
    }

    /// <summary>
    /// What a paste would move: our own cut items while the clipboard still holds them, else a file list that Explorer
    /// (or another program) put there with the move effect. A copy is not accepted. Does not look at the file system.
    /// </summary>
    private bool TryGetPasteSources(out List<string> paths, out bool isOwnCut, out string? reason)
    {
        paths = new List<string>();
        isOwnCut = false;
        reason = null;

        if (_cutPaths.Count > 0)
        {
            if (IsCutTrusted)
            {
                paths = new List<string>(_cutPaths);
                isOwnCut = true;
                return true;
            }

            // Something else was copied since: the cut is not trusted any more
            ClearCut(clearClipboard: false);
        }

        if (!ShellClipboard.TryGetFileDrop(out List<string> dropped, out int effect))
        {
            reason = "Nothing to paste: cut files or folders first.";
            return false;
        }
        if (!ShellClipboard.IsMove(effect))
        {
            reason = "The clipboard holds copied files; SpaceView only moves items that were cut.";
            return false;
        }

        paths = dropped;
        return true;
    }

    /// <summary>Whether a paste into the row is possible now; <paramref name="reason"/> says why not.</summary>
    private bool CanPasteInto(TreeRowViewModel? target, out string? reason)
    {
        reason = null;
        if (target == null || target.IsMoreRow)
        {
            reason = "Select one folder to paste into.";
            return false;
        }
        if (!target.IsDirectory)
        {
            reason = "Items can only be pasted into a folder.";
            return false;
        }
        if (_fileOperationInProgress)
        {
            reason = "Another delete or move is still running.";
            return false;
        }

        DriveSession session = target.Session;
        if (!IsSessionUsable(session, target.Store) || session.IsStale)
        {
            reason = session.IsBusy ? $"{session.RootPath} is being scanned."
                : session.IsUnavailable ? $"{session.RootPath} is not available."
                : session.IsStale ? $"{session.RootPath} is out of date: rescan it first."
                : $"{session.RootPath} is not ready.";
            return false;
        }

        return TryGetPasteSources(out _, out _, out reason);
    }

    private async Task PasteIntoAsync(TreeRowViewModel? target)
    {
        if (!CanPasteInto(target, out string? reason))
        {
            StatusTextBlock.Text = reason ?? "Nothing to paste.";
            return;
        }

        TryGetPasteSources(out List<string> sources, out bool isOwnCut, out _);
        await MovePathsAsync(sources, target!.GetFullPath(), fromCut: isOwnCut, fromForeignClipboard: !isOwnCut);
    }

    // =========================================================================
    // The move
    // =========================================================================

    /// <summary>
    /// Moves the items into the folder: checks the rules, asks once (count, size, source, destination, whether it crosses
    /// drives), moves with the Windows shell (one call on its own thread: native progress, conflict prompts, undo), then
    /// brings the tree in line with what actually moved and selects the items at their new place.
    /// </summary>
    private async Task MovePathsAsync(IReadOnlyList<string> requested, string destination, bool fromCut, bool fromForeignClipboard = false)
    {
        if (_fileOperationInProgress)
            return;

        destination = MoveRules.Normalize(destination);
        if (!Directory.Exists(destination))
        {
            StatusTextBlock.Text = $"The destination folder {destination} does not exist.";
            return;
        }

        // Items that vanished since they were cut cannot be moved
        List<string> existing = requested.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count == 0)
        {
            StatusTextBlock.Text = "The cut items no longer exist.";
            if (fromCut)
                ClearCut(clearClipboard: true);
            return;
        }

        MoveValidation validation = MoveRules.Validate(existing, destination);
        if (!validation.IsValid)
        {
            StatusTextBlock.Text = validation.Error ?? "Nothing to move.";
            return;
        }
        IReadOnlyList<string> sources = validation.Sources;

        DriveSession? destSession = FindSessionForPath(destination);
        if (destSession != null && (!IsSessionUsable(destSession, destSession.Store) || destSession.IsStale))
        {
            StatusTextBlock.Text = $"{destSession.RootPath} is being scanned, not available or out of date; move into it when it is ready.";
            return;
        }

        if (!ConfirmMove(sources, validation.NoOps.Count, destination))
            return;

        _fileOperationInProgress = true;
        try
        {
            StatusTextBlock.Text = sources.Count == 1
                ? $"Moving {sources[0]} to {destination}..."
                : $"Moving {sources.Count:N0} items to {destination}...";

            IntPtr owner = new WindowInteropHelper(this).Handle;
            ShellMoveResult result = await ShellMover.MoveAsync(sources, destination, owner);

            await ApplyMoveResultAsync(result, sources, destination, fromCut, fromForeignClipboard);

            if (result.AccessDenied && !result.AllMoved && !ElevationHelper.IsElevated)
            {
                ElevateButton.Visibility = Visibility.Visible;
                PromptAndRelaunchElevated("Windows denied access while moving the items.");
            }
        }
        catch (Exception ex) when (ElevationHelper.IsAccessDenied(ex) && !ElevationHelper.IsElevated)
        {
            StatusTextBlock.Text = $"Could not move to {destination}: access denied.";
            PromptAndRelaunchElevated($"Could not move the items to \"{destination}\": access was denied.");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Could not move: {ex.Message}";
            System.Windows.MessageBox.Show(this, $"Could not move the items: {ex.Message}", "SpaceView", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _fileOperationInProgress = false;
        }
    }

    /// <summary>The one dialog before a move: count, total size, source, destination, and a warning if it crosses drives.</summary>
    private bool ConfirmMove(IReadOnlyList<string> sources, int leftOut, string destination)
    {
        long totalSize = 0;
        int unknown = 0;
        foreach (string source in sources)
        {
            DriveSession? session = FindSessionForPath(source);
            FileSystemStore? store = session?.Store;
            if (store != null && store.TryFindNodeByPath(source, out int nodeId))
                totalSize += store.GetNodeSnapshot(nodeId).Size;
            else
                unknown++;
        }

        string size = SizeFormatter.FormatBytes(totalSize) + (unknown > 0 ? $" + {unknown:N0} not in the tree" : string.Empty);
        string?[] parents = sources.Select(MoveRules.GetParent).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string from = parents.Length == 1 ? parents[0] ?? string.Empty : $"{parents.Length:N0} different folders";

        string what = sources.Count == 1
            ? $"Move \"{MoveRules.GetName(sources[0])}\" ({size})"
            : $"Move {sources.Count:N0} items ({size})";

        string text = $"{what}?\n\nFrom: {from}\nTo: {destination}";
        if (leftOut > 0)
            text += $"\n\n{leftOut:N0} item(s) already in that folder are left out.";

        string[] crossing = sources
            .Where(s => MoveRules.CrossesDrives(s, destination))
            .Select(s => Path.GetPathRoot(s) ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (crossing.Length > 0)
        {
            text += $"\n\nThis crosses drives ({string.Join(", ", crossing)} to {Path.GetPathRoot(destination)}): " +
                    "the items are copied and then deleted, which can be slow for large items.";
        }

        return System.Windows.MessageBox.Show(
            this, text, "SpaceView", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
    }

    /// <summary>Reports "moved N of M", settles the cut state, brings the tree in line and selects the moved items.</summary>
    private async Task ApplyMoveResultAsync(
        ShellMoveResult result, IReadOnlyList<string> sources, string destination, bool fromCut, bool fromForeignClipboard)
    {
        int moved = result.Moved.Count;
        string summary = moved == sources.Count
            ? (sources.Count == 1 ? $"Moved {sources[0]} to {destination}." : $"Moved {moved:N0} items to {destination}.")
            : result.Aborted && moved == 0
                ? "Move cancelled; nothing was moved."
                : $"Moved {moved:N0} of {sources.Count:N0} items to {destination}" + (result.Aborted ? " (cancelled)." : ".");

        // A folder that is still there may have been moved in part (merged into the destination, or cut short): its parent needs a look too
        List<string> touched = result.Moved.ToList();
        if (!result.AllMoved)
            touched.AddRange(result.NotMoved.Where(Directory.Exists));

        if (touched.Count > 0)
            await RefreshAfterMoveAsync(touched, result.Moved, destination);

        // The cut: gone when everything moved; what is left stays cut (and on the clipboard) when the move was partial or cancelled
        if (fromCut)
        {
            if (result.AllMoved)
            {
                ClearCut(clearClipboard: true);
            }
            else if (moved > 0)
            {
                List<string> remaining = _cutPaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                if (remaining.Count == 0)
                {
                    ClearCut(clearClipboard: true);
                }
                else
                {
                    uint? sequence = ShellClipboard.TrySetFileDrop(remaining, ShellClipboard.DropEffectMove);
                    if (sequence == null)
                    {
                        ClearCut(clearClipboard: false);
                    }
                    else
                    {
                        _cutPaths = remaining;
                        _cutClipboardSequence = sequence.Value;
                        ResolveCutKeys();
                    }
                }
            }
        }
        else if (fromForeignClipboard && result.AllMoved)
        {
            ShellClipboard.TryClear();
        }

        if (moved > 0)
            SelectMovedItems(destination, result.Moved);

        StatusTextBlock.Text = summary;
    }

    // =========================================================================
    // Updating the tree after a move
    // =========================================================================

    /// <summary>
    /// Brings the tree in line: each distinct parent the items left and the destination, in their drives, one after the
    /// other (sources first), skipping folders inside another folder of the plan (see <see cref="MoveRules.PlanRefresh"/>).
    /// A folder is refreshed with the Refresh Branch machinery. A drive root is not: that would rescan the whole drive, so
    /// its top level alone is brought in line (<see cref="ReconcileRootAsync"/>, a store delete/add delta). A drive
    /// that is not in the tree is left out; a drive that is not ready is skipped (its next scan shows the result).
    /// </summary>
    private async Task RefreshAfterMoveAsync(IReadOnlyList<string> touchedSources, IReadOnlyList<string> movedSources, string destination)
    {
        List<RefreshStep> plan = MoveRules.PlanRefresh(touchedSources, destination);
        var movedNames = new HashSet<string>(movedSources.Select(MoveRules.GetName), StringComparer.OrdinalIgnoreCase);

        foreach (RefreshStep step in plan)
        {
            DriveSession? session = FindSessionForPath(step.Path);
            FileSystemStore? store = session?.Store;
            if (session == null || store == null || !IsSessionUsable(session, store))
                continue;

            if (step.IsRoot)
            {
                // Names that were moved into this root must be taken over even if a node of that name exists (an overwrite)
                bool isDestination = MoveRules.SamePath(step.Path, destination);
                await ReconcileRootAsync(session, store, isDestination ? movedNames : new HashSet<string>());
            }
            else if (store.TryFindNodeByPath(step.Path, out int nodeId))
            {
                await RefreshNodeBranchAsync(session, store, nodeId);
            }
        }
    }

    /// <summary>
    /// The top level of a drive root, brought in line without a rescan (see <see cref="RootReconciler"/>): a store delete/add
    /// delta instead of a branch refresh of the root, which would be a scan of the whole drive. The drive's journal updates
    /// are held back meanwhile and replayed afterwards, like in a branch refresh, and released on every path; the store calls
    /// are atomic and idempotent, so a monitor that applied the same move meanwhile cannot make the totals count twice.
    /// </summary>
    private async Task ReconcileRootAsync(DriveSession session, FileSystemStore store, IReadOnlyCollection<string> replaceNames)
    {
        var cts = new CancellationTokenSource();
        _branchCts = cts;
        _branchSession = session;
        IDisposable? journalPause = null;
        string rootPath = session.RootPath;
        StatusTextBlock.Text = $"Updating {rootPath}...";

        try
        {
            NtfsUsnJournalMonitor? monitor = session.Monitor;
            if (monitor != null)
                journalPause = await Task.Run(monitor.PauseApplyingChanges);
            cts.Token.ThrowIfCancellationRequested();

            RootReconciler.Result result = await RootReconciler.ReconcileAsync(store, replaceNames, () => IsLive(session, store), cts.Token);
            if (!result.Listed)
            {
                StatusTextBlock.Text = $"Could not read {rootPath}; its entry in the tree was left as it is.";
                return;
            }

            if (IsLive(session, store))
            {
                ApplyLocalDeletionToView(session, rootPath);
                ResolveCutKeys();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a full scan, which reports its own status
        }
        catch (Exception ex)
        {
            if (IsLive(session, store))
                StatusTextBlock.Text = $"Could not update {rootPath}: {ex.Message}";
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

    // =========================================================================
    // Selection after a move
    // =========================================================================

    /// <summary>
    /// Selects the moved items at their new place: the destination is opened in the tree (its ancestors are expanded if
    /// necessary) and its rows with the moved names are selected. If they cannot be shown (the folder has too many items
    /// to expand here, or the destination is not in a scanned drive), the destination row is selected, or nothing.
    /// </summary>
    private void SelectMovedItems(string destination, IReadOnlyList<string> movedSources)
    {
        DriveSession? session = FindSessionForPath(destination);
        FileSystemStore? store = session?.Store;
        if (session == null || store == null || !IsLive(session, store))
            return;

        TreeRowViewModel? destRow = RevealPath(session, store, destination);
        if (destRow == null)
            return;

        var names = new HashSet<string>(movedSources.Select(MoveRules.GetName), StringComparer.OrdinalIgnoreCase);
        var moved = new List<TreeRowViewModel>();
        int destIndex = _visibleRows.IndexOf(destRow);
        for (int i = destIndex + 1; i < _visibleRows.Count && _visibleRows[i].Depth > destRow.Depth; i++)
        {
            TreeRowViewModel row = _visibleRows[i];
            if (!row.IsMoreRow && row.Depth == destRow.Depth + 1 && names.Contains(row.Name))
                moved.Add(row);
        }

        TreeListBox.SelectedItems.Clear();
        foreach (TreeRowViewModel row in moved.Count > 0 ? moved : new List<TreeRowViewModel> { destRow })
            TreeListBox.SelectedItems.Add(row);

        TreeListBox.ScrollIntoView(moved.Count > 0 ? moved[0] : destRow);
    }

    /// <summary>The row of the folder at <paramref name="path"/>, expanding the rows above it on the way; null if the drive's row is missing.</summary>
    private TreeRowViewModel? RevealPath(DriveSession session, FileSystemStore store, string path)
    {
        if (!_rootRows.TryGetValue(session, out TreeRowViewModel? current))
            return null;

        string rest = MoveRules.Normalize(path).Substring(session.RootPath.TrimEnd('\\').Length).Trim('\\');
        if (rest.Length == 0)
        {
            if (!current.IsExpanded)
                ToggleRowExpansion(current);
            return current;
        }

        foreach (string part in rest.Split('\\'))
        {
            if (current.IsDirectory && !current.IsExpanded)
                ToggleRowExpansion(current);

            int index = _visibleRows.IndexOf(current);
            TreeRowViewModel? next = null;
            for (int i = index + 1; i < _visibleRows.Count && _visibleRows[i].Depth > current.Depth; i++)
            {
                TreeRowViewModel row = _visibleRows[i];
                if (!row.IsMoreRow && row.Depth == current.Depth + 1 && string.Equals(row.Name, part, StringComparison.OrdinalIgnoreCase))
                {
                    next = row;
                    break;
                }
            }

            if (next == null)
                return current; // not shown (behind a "more items" row, or still loading): the nearest folder above

            current = next;
        }

        if (current.IsDirectory && !current.IsExpanded)
            ToggleRowExpansion(current);
        return current;
    }
}
