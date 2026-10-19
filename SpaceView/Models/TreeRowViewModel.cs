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

    private readonly FileSystemStore _store;
    private bool _isExpanded;
    private long _size;
    private int _fileCount;
    private int _folderCount;
    private double _percentOfParent;
    private string _name;

    public int NodeId { get; }
    public int ParentNodeId { get; }
    public int Depth { get; }
    public bool IsDirectory { get; }

    public Thickness IndentMargin => new(Depth * 18, 0, 0, 0);
    public Visibility ExpandButtonVisibility => IsDirectory ? Visibility.Visible : Visibility.Hidden;

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
    public string PercentText => $"{_percentOfParent:0.0}%";
    public string FileCountText => IsDirectory ? _fileCount.ToString("N0") : "—";
    public string FolderCountText => IsDirectory ? _folderCount.ToString("N0") : "—";

    public SolidColorBrush PercentBarBrush =>
        _percentOfParent >= 50.0 ? HighUsageBrush :
        _percentOfParent >= 25.0 ? MedHighUsageBrush :
        _percentOfParent >= 10.0 ? MedUsageBrush : LowUsageBrush;

    public TreeRowViewModel(FileSystemStore store, int nodeId, int depth, bool isExpanded = false)
    {
        _store = store;
        NodeId = nodeId;
        Depth = depth;
        _isExpanded = isExpanded;

        NodeRecord rec = _store.GetNodeSnapshot(nodeId);
        ParentNodeId = rec.ParentId;
        IsDirectory = rec.IsDirectory;
        _name = nodeId == 0 ? _store.RootPath : _store.GetName(nodeId);
        _size = rec.Size;
        _fileCount = rec.FileCount;
        _folderCount = rec.FolderCount;
        _percentOfParent = ComputePercent(rec);
    }

    public void RefreshFromStore()
    {
        NodeRecord rec = _store.GetNodeSnapshot(NodeId);
        if (NodeId != 0)
        {
            Name = _store.GetName(NodeId);
        }
        Size = rec.Size;
        FileCount = rec.FileCount;
        FolderCount = rec.FolderCount;
        PercentOfParent = ComputePercent(rec);
    }

    private double ComputePercent(in NodeRecord rec)
    {
        if (NodeId == 0 || rec.ParentId < 0)
            return 100.0;

        NodeRecord parent = _store.GetNodeSnapshot(rec.ParentId);
        if (parent.Size <= 0)
            return 0.0;

        return Math.Clamp((double)rec.Size / parent.Size * 100.0, 0.0, 100.0);
    }

    public string GetFullPath() => _store.GetFullPath(NodeId);

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
