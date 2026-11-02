using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using SpaceView.Core;
using Color = System.Windows.Media.Color;

namespace SpaceView.Models;

public sealed class TreeRowViewModel : INotifyPropertyChanged
{
    private static readonly SolidColorBrush HighUsageBrush = CreateFrozenBrush(244, 63, 94);    // Rose
    private static readonly SolidColorBrush MedHighUsageBrush = CreateFrozenBrush(245, 158, 11); // Amber
    private static readonly SolidColorBrush MedUsageBrush = CreateFrozenBrush(56, 189, 248);     // Sky Blue
    private static readonly SolidColorBrush LowUsageBrush = CreateFrozenBrush(52, 211, 153);     // Emerald

    private readonly FileSystemStore? _store; // null for a drive's root row, which always follows its session's current store
    private bool _isExpanded;
    private long _size;
    private int _fileCount;
    private int _folderCount;
    private double _percentOfParent;
    private string _name;

    private bool _isLoadingChildren;
    private bool _isCut;
    private int _remaining;
    private string _subText = string.Empty;
    private string _marker = string.Empty;
    private string? _toolTipText;

    /// <summary>The drive this row belongs to. A NodeId is only unique within the drive's store, so rows are identified by <see cref="Key"/>.</summary>
    public DriveSession Session { get; }

    /// <summary>True for the row of a drive itself (the root of its store): it shows the drive's details and state.</summary>
    public bool IsRoot { get; }

    public RowKey Key => new(Session, NodeId);

    /// <summary>The node id, or -1 for the synthetic "more items" row.</summary>
    public int NodeId { get; }
    public int ParentNodeId { get; }
    public int Depth { get; }
    public bool IsDirectory { get; }

    /// <summary>
    /// True for the synthetic "… N more items" row that ends a capped list of children: it has no store node
    /// (NodeId -1), is not a directory, and is activated to load the next page of its parent's children.
    /// </summary>
    public bool IsMoreRow { get; }

    /// <summary>
    /// UI-thread flag: children (for an expanded folder) or the next page (for a "more items" row) are being
    /// built in the background; further expand/collapse/load requests for the row are ignored meanwhile.
    /// </summary>
    public bool IsLoadingChildren
    {
        get => _isLoadingChildren;
        set
        {
            _isLoadingChildren = value;
            if (IsMoreRow)
                UpdateMoreRowText();
        }
    }

    /// <summary>Updates the count shown by a "more items" row.</summary>
    public void SetRemaining(int remaining)
    {
        _remaining = remaining;
        UpdateMoreRowText();
    }

    public Thickness IndentMargin => new(Depth * 18, 0, 0, 0);
    public Visibility ExpandButtonVisibility => IsDirectory ? Visibility.Visible : Visibility.Hidden;
    public Visibility DetailVisibility => IsMoreRow ? Visibility.Collapsed : Visibility.Visible;

    public string Name
    {
        get => _name;
        private set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    public long Size
    {
        get => _size;
        private set
        {
            if (_size != value)
            {
                _size = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    public int FileCount
    {
        get => _fileCount;
        private set
        {
            if (_fileCount != value)
            {
                _fileCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FileCountText));
            }
        }
    }

    public int FolderCount
    {
        get => _folderCount;
        private set
        {
            if (_folderCount != value)
            {
                _folderCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FolderCountText));
            }
        }
    }

    public double PercentOfParent
    {
        get => _percentOfParent;
        private set
        {
            if (Math.Abs(_percentOfParent - value) > 0.005)
            {
                _percentOfParent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PercentText));
                OnPropertyChanged(nameof(PercentBarBrush));
            }
        }
    }

    public string SizeText => SizeFormatter.FormatBytes(_size);
    // A drive row has no parent: its bar is the share of the volume in use
    public string PercentText => IsRoot ? $"{_percentOfParent:0.0}% used" : $"{_percentOfParent:0.0}%";

    /// <summary>Drive rows only: format, free space and monitor mode, shown after the name.</summary>
    public string SubText
    {
        get => _subText;
        private set
        {
            if (_subText != value)
            {
                _subText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SubTextVisibility));
            }
        }
    }

    /// <summary>Drive rows only: scanning / queued / stale / not available, empty when all is well.</summary>
    public string Marker
    {
        get => _marker;
        private set
        {
            if (_marker != value)
            {
                _marker = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MarkerVisibility));
            }
        }
    }

    /// <summary>The row's item is cut (waiting to be pasted somewhere): it is shown dimmed. Set by the window, which knows the cut set.</summary>
    public bool IsCut
    {
        get => _isCut;
        set
        {
            if (_isCut != value)
            {
                _isCut = value;
                OnPropertyChanged();
            }
        }
    }

    public string? ToolTipText
    {
        get => _toolTipText;
        private set
        {
            if (_toolTipText != value)
            {
                _toolTipText = value;
                OnPropertyChanged();
            }
        }
    }

    public Visibility SubTextVisibility => _subText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MarkerVisibility => _marker.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string FileCountText => IsDirectory ? _fileCount.ToString("N0") : "—";
    public string FolderCountText => IsDirectory ? _folderCount.ToString("N0") : "—";

    public SolidColorBrush PercentBarBrush =>
        _percentOfParent >= 50.0 ? HighUsageBrush :
        _percentOfParent >= 25.0 ? MedHighUsageBrush :
        _percentOfParent >= 10.0 ? MedUsageBrush : LowUsageBrush;

    /// <summary>
    /// The row of a drive: depth 0, node 0 of the session's store. It exists from the moment the drive is added (before
    /// its first scan has produced a store) and keeps following <see cref="DriveSession.Store"/> across rescans.
    /// </summary>
    public static TreeRowViewModel CreateRoot(DriveSession session, bool isExpanded = false)
    {
        var row = new TreeRowViewModel(session, isExpanded);
        row.RefreshRoot();
        return row;
    }

    private TreeRowViewModel(DriveSession session, bool isExpanded)
    {
        Session = session;
        IsRoot = true;
        NodeId = 0;
        ParentNodeId = -1;
        Depth = 0;
        IsDirectory = true;
        _isExpanded = isExpanded;
        _name = session.Title;
    }

    /// <summary>Builds the row from data already read from the store (no store access, safe off the UI thread).</summary>
    public TreeRowViewModel(DriveSession session, FileSystemStore store, in NodeSnapshot snapshot, int depth, bool isExpanded = false)
    {
        Session = session;
        _store = store;
        NodeId = snapshot.NodeId;
        Depth = depth;
        _isExpanded = isExpanded;

        ParentNodeId = snapshot.ParentId;
        IsDirectory = snapshot.IsDirectory;
        _name = snapshot.Name;
        _size = snapshot.Size;
        _fileCount = snapshot.FileCount;
        _folderCount = snapshot.FolderCount;
        _percentOfParent = ComputePercent(snapshot);
    }

    private TreeRowViewModel(DriveSession session, FileSystemStore store, int parentNodeId, int depth, int remaining, bool isMoreRow)
    {
        Session = session;
        _store = store;
        NodeId = -1;
        ParentNodeId = parentNodeId;
        Depth = depth;
        IsMoreRow = isMoreRow;
        _remaining = remaining;
        _name = string.Empty;
        UpdateMoreRowText();
    }

    /// <summary>
    /// A synthetic row standing in for the children of <paramref name="parentNodeId"/> that are not shown yet. It has no
    /// node of its own (NodeId is -1), so everything that works on a row's node must skip it (see <see cref="IsMoreRow"/>).
    /// </summary>
    public static TreeRowViewModel CreateMoreRow(DriveSession session, FileSystemStore store, int parentNodeId, int depth, int remaining) =>
        new(session, store, parentNodeId, depth, remaining, isMoreRow: true);

    public void RefreshFromStore()
    {
        if (IsMoreRow)
            return;

        if (IsRoot)
        {
            RefreshRoot();
            return;
        }

        NodeSnapshot snapshot = _store!.GetNodeInfo(NodeId);
        Name = snapshot.Name;
        Size = snapshot.Size;
        FileCount = snapshot.FileCount;
        FolderCount = snapshot.FolderCount;
        PercentOfParent = ComputePercent(snapshot);
    }

    /// <summary>Re-reads a drive row from its session: totals from the current store, details and state from the session.</summary>
    public void RefreshRoot()
    {
        FileSystemStore? store = Session.Store;
        if (store != null)
        {
            NodeRecord rec = store.GetNodeSnapshot(0);
            Size = rec.Size;
            FileCount = rec.FileCount;
            FolderCount = rec.FolderCount;
        }
        else
        {
            Size = 0;
            FileCount = 0;
            FolderCount = 0;
        }

        Name = Session.Title;
        SubText = Session.SubText;
        Marker = Session.Marker;
        ToolTipText = Session.ToolTipText;
        PercentOfParent = Session.UsedPercentage;
    }

    private double ComputePercent(in NodeSnapshot snapshot)
    {
        // Rows are relative to their parent, so the children of a drive are relative to that drive's own root
        if (snapshot.ParentId < 0)
            return 100.0;

        if (snapshot.ParentSize <= 0)
            return 0.0;

        return Math.Clamp((double)snapshot.Size / snapshot.ParentSize * 100.0, 0.0, 100.0);
    }

    /// <summary>The path of the row's node (of the folder the hidden items are in, for a "more items" row).</summary>
    public string GetFullPath()
    {
        if (IsRoot)
            return Session.RootPath;

        return _store!.GetFullPath(IsMoreRow ? ParentNodeId : NodeId);
    }

    /// <summary>The store this row was built from (a drive row: its session's current store, if it has one).</summary>
    public FileSystemStore? Store => IsRoot ? Session.Store : _store;

    private void UpdateMoreRowText()
    {
        Name = _isLoadingChildren
            ? "… loading more items…"
            : $"… {_remaining:N0} more items (click to show more)";
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }
}
