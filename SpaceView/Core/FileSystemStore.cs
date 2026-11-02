using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SpaceView.Core;

public enum UsnApplyResult
{
    /// <summary>Nothing to change (item outside the tracked tree, or already up to date).</summary>
    Ignored,
    /// <summary>The store was modified.</summary>
    Applied,
    /// <summary>The parent is not (yet) known; the caller may retry once other records have been applied.</summary>
    ParentUnmapped
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct NodeRecord
{
    public long Size;
    public ulong FileId;
    public int ParentId;
    public int FirstChildId;
    public int NextSiblingId;
    public int FileCount;
    public int FolderCount;
    public int NameHandle;
    public ushort NameByteLength;
    public byte Flags;
    public byte Reserved;

    public const byte FlagDirectory = 1;
    public const byte FlagDeleted = 2;

    public readonly bool IsDirectory
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Flags & FlagDirectory) != 0;
    }

    public readonly bool IsDeleted
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Flags & FlagDeleted) != 0;
    }
}

/// <summary>A consistent copy of one node's display data, read under a single store lock.</summary>
public readonly struct NodeSnapshot
{
    public readonly int NodeId;
    public readonly int ParentId;
    public readonly bool IsDirectory;
    public readonly string Name;
    public readonly long Size;
    public readonly int FileCount;
    public readonly int FolderCount;
    /// <summary>Size of the parent directory (0 for the root); the denominator for the node's percentage.</summary>
    public readonly long ParentSize;

    public NodeSnapshot(int nodeId, in NodeRecord rec, string name, long parentSize)
    {
        NodeId = nodeId;
        ParentId = rec.ParentId;
        IsDirectory = rec.IsDirectory;
        Name = name;
        Size = rec.Size;
        FileCount = rec.FileCount;
        FolderCount = rec.FolderCount;
        ParentSize = parentSize;
    }
}

/// <summary>
/// High-density, arena-backed file system tree store capable of holding millions of nodes
/// in a fraction of the memory of object-based trees, while supporting real-time O(depth) updates.
/// </summary>
public sealed class FileSystemStore
{
    private const int ChunkShift = 16;
    private const int ChunkSize = 1 << ChunkShift; // 65,536 nodes per chunk (~2.8 MB per chunk)
    private const int ChunkMask = ChunkSize - 1;

    private const int NameSlabShift = 20;
    private const int NameSlabSize = 1 << NameSlabShift; // 1 MB UTF-8 slabs
    private const int NameSlabMask = NameSlabSize - 1;

    private const int MaxPagedMftIndex = 16 * 1024 * 1024; // 16M MFT records via paged lookup
    private const int MftPageShift = 16;
    private const int MftPageSize = 1 << MftPageShift;
    private const int MftPageMask = MftPageSize - 1;

    private readonly object _syncLock = new();
    private NodeRecord[][] _chunks = new NodeRecord[64][];
    private int _nodeCount;

    private readonly List<byte[]> _nameSlabs = new(32);
    private int _currentSlabIndex = -1;
    private int _currentSlabOffset = NameSlabSize;

    // Sparse paged table mapping 48-bit MFT record number -> nodeId (1-based stored value so 0 means unmapped)
    private readonly int[]?[] _mftPages = new int[MaxPagedMftIndex >> MftPageShift][];
    private readonly Dictionary<ulong, int> _overflowFrnMap = new();

    // Hard links: a file with several names has one node per name, all with the same FRN. Exactly one of them (the
    // "carrier", the node the FRN map points at) holds the size; the other links have size 0 so the file is counted once.
    // Only files with two or more nodes have an entry (FRN -> every node of the file, carrier included, possibly
    // tombstoned ones that are skipped on use).
    private readonly Dictionary<ulong, List<int>> _linkGroups = new();

    // Nodes touched by live (journal / watcher) updates since the last TakeActivity, for the activity highlight.
    // Guarded by _syncLock like every other mutation; capped so a busy volume cannot grow it without bound.
    private readonly HashSet<int> _activity = new();

    /// <summary>Most distinct nodes remembered between two <see cref="TakeActivity"/> calls (also kept per drive between UI passes, and resolved per pass).</summary>
    public const int MaxActivityNodes = 4096;

    public string RootPath { get; }
    public int RootNodeId => 0;

    public int TotalNodeCount => Volatile.Read(ref _nodeCount);

    /// <summary>
    /// A cheap estimate of the memory held by the store: the allocated node chunks, name slabs and FRN pages, plus a rough
    /// per-entry cost for the two dictionaries. It counts allocated arrays, not live nodes.
    /// </summary>
    public long EstimatedMemoryBytes
    {
        get
        {
            lock (_syncLock)
            {
                long chunks = 0;
                foreach (NodeRecord[] chunk in _chunks)
                {
                    if (chunk != null)
                        chunks++;
                }

                long pages = 0;
                foreach (int[]? page in _mftPages)
                {
                    if (page != null)
                        pages++;
                }

                return chunks * ChunkSize * Unsafe.SizeOf<NodeRecord>() +
                       _nameSlabs.Count * (long)NameSlabSize +
                       pages * MftPageSize * sizeof(int) +
                       _overflowFrnMap.Count * 40L +
                       _linkGroups.Count * 96L;
            }
        }
    }
    public object SyncRoot => _syncLock;

    public FileSystemStore(string rootPath, ulong rootFileId = 0)
    {
        RootPath = rootPath.EndsWith('\\') ? rootPath : rootPath + "\\";
        AllocateNewSlab();

        // Node 0 is always the volume root
        int rootId = AddNodeInternal(-1, RootPath.TrimEnd('\\'), 0, rootFileId, isDirectory: true);
        if (rootFileId != 0)
        {
            MapFileIdInternal(rootFileId, rootId);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref NodeRecord GetNodeRef(int nodeId)
    {
        return ref _chunks[nodeId >> ChunkShift][nodeId & ChunkMask];
    }

    public NodeRecord GetNodeSnapshot(int nodeId)
    {
        lock (_syncLock)
        {
            return _chunks[nodeId >> ChunkShift][nodeId & ChunkMask];
        }
    }

    public int AddNode(int parentId, ReadOnlySpan<char> name, long size, ulong fileId, bool isDirectory, bool linkToParent = true)
    {
        lock (_syncLock)
        {
            int nodeId = AddAndMapNodeUnsafe(parentId, name, size, fileId, isDirectory);
            if (linkToParent && parentId >= 0)
            {
                ref NodeRecord parent = ref GetNodeRef(parentId);
                ref NodeRecord child = ref GetNodeRef(nodeId);
                child.NextSiblingId = parent.FirstChildId;
                parent.FirstChildId = nodeId;
            }
            return nodeId;
        }
    }

    /// <summary>
    /// Batch inserts all entries of a single directory under a single lock acquisition for high multi-threaded throughput.
    /// </summary>
    public void AddDirectoryBatch(
        int parentId,
        ReadOnlySpan<PendingChildEntry> entries,
        Span<int> outCreatedDirNodeIds,
        out int createdDirCount)
    {
        createdDirCount = 0;
        if (entries.IsEmpty)
            return;

        lock (_syncLock)
        {
            ref NodeRecord parent = ref GetNodeRef(parentId);
            int firstChild = parent.FirstChildId;

            for (int i = 0; i < entries.Length; i++)
            {
                ref readonly PendingChildEntry entry = ref entries[i];
                int nodeId = AddAndMapNodeUnsafe(parentId, entry.Name.AsSpan(), entry.Size, entry.FileId, entry.IsDirectory);

                // Re-fetch parent ref in case _chunks grew
                ref NodeRecord child = ref GetNodeRef(nodeId);
                child.NextSiblingId = firstChild;
                firstChild = nodeId;

                if (entry.IsDirectory)
                {
                    outCreatedDirNodeIds[createdDirCount++] = nodeId;
                }
            }

            GetNodeRef(parentId).FirstChildId = firstChild;
        }
    }

    /// <summary>
    /// Adds a node and maps its FRN. A file whose FRN already belongs to a live, different file node is another hard
    /// link to that file: it is added with size 0 (the carrier already counts the size) and left out of the FRN map,
    /// which keeps pointing at the carrier. The caller links the node into its parent.
    /// </summary>
    private int AddAndMapNodeUnsafe(int parentId, ReadOnlySpan<char> name, long size, ulong fileId, bool isDirectory)
    {
        bool isExtraLink = TryGetLinkCarrierUnsafe(fileId, isDirectory, out int carrierId);
        int nodeId = AddNodeInternal(parentId, name, isExtraLink ? 0 : size, fileId, isDirectory);
        if (isExtraLink)
            RegisterLinkUnsafe(fileId, carrierId, nodeId);
        else if (fileId != 0)
            MapFileIdInternal(fileId, nodeId);
        return nodeId;
    }

    /// <summary>True if the FRN is already mapped to a live file node with the very same FRN (not just the same MFT slot).</summary>
    private bool TryGetLinkCarrierUnsafe(ulong fileId, bool isDirectory, out int carrierId)
    {
        carrierId = -1;
        if (fileId == 0 || isDirectory || !TryGetNodeByFileIdUnsafe(fileId, out int mapped))
            return false;

        ref NodeRecord m = ref GetNodeRef(mapped);
        if (m.IsDirectory || m.FileId != fileId)
            return false;

        carrierId = mapped;
        return true;
    }

    private void RegisterLinkUnsafe(ulong fileId, int carrierId, int linkId)
    {
        if (!_linkGroups.TryGetValue(fileId, out List<int>? group))
        {
            group = new List<int>(2) { carrierId };
            _linkGroups[fileId] = group;
        }
        else if (!group.Contains(carrierId))
        {
            group.Add(carrierId);
        }
        group.Add(linkId);
    }

    /// <summary>
    /// Called before a file node is tombstoned. Takes it out of its link group and, if it was the carrier and the file
    /// has other live names, hands the size (and the FRN map slot) to one of them so the file stays counted once.
    /// The caller removes the node's own size from its ancestors.
    /// </summary>
    private void ReleaseLinkUnsafe(int nodeId)
    {
        ref NodeRecord rec = ref GetNodeRef(nodeId);
        ulong fileId = rec.FileId;
        if (rec.IsDirectory || fileId == 0 || !_linkGroups.TryGetValue(fileId, out List<int>? group))
            return;

        bool wasCarrier = TryGetNodeByFileIdUnsafe(fileId, out int carrierId) && carrierId == nodeId;
        group.RemoveAll(id => id == nodeId || GetNodeRef(id).IsDeleted || GetNodeRef(id).FileId != fileId);

        if (wasCarrier && group.Count > 0)
        {
            int heirId = group[0];
            long size = rec.Size;
            GetNodeRef(heirId).Size = size;
            MapFileIdInternal(fileId, heirId);
            if (size != 0)
                PropagateDeltaUnsafe(GetNodeRef(heirId).ParentId, size, 0, 0);
        }

        if (group.Count < 2)
            _linkGroups.Remove(fileId);
    }

    /// <summary>Finds the live child of <paramref name="parentId"/> that is the name <paramref name="name"/> of file <paramref name="fileId"/>, or -1.</summary>
    private int FindLinkByNameUnsafe(int parentId, ReadOnlySpan<char> name, ulong fileId)
    {
        int childId = FindChildByNameUnsafe(parentId, name);
        return childId > 0 && GetNodeRef(childId).FileId == fileId ? childId : -1;
    }

    private int AddNodeInternal(int parentId, ReadOnlySpan<char> name, long size, ulong fileId, bool isDirectory)
    {
        int nodeId = _nodeCount++;
        int chunkIdx = nodeId >> ChunkShift;
        int offsetIdx = nodeId & ChunkMask;

        if (chunkIdx >= _chunks.Length)
        {
            Array.Resize(ref _chunks, _chunks.Length * 2);
        }
        if (_chunks[chunkIdx] == null)
        {
            _chunks[chunkIdx] = new NodeRecord[ChunkSize];
        }

        int nameHandle = WriteNameUtf8(name, out ushort byteLen);

        ref NodeRecord rec = ref _chunks[chunkIdx][offsetIdx];
        rec.Size = size;
        rec.FileId = fileId;
        rec.ParentId = parentId;
        rec.FirstChildId = -1;
        rec.NextSiblingId = -1;
        rec.FileCount = 0;
        rec.FolderCount = 0;
        rec.NameHandle = nameHandle;
        rec.NameByteLength = byteLen;
        rec.Flags = isDirectory ? NodeRecord.FlagDirectory : (byte)0;
        rec.Reserved = 0;

        return nodeId;
    }

    private int WriteNameUtf8(ReadOnlySpan<char> name, out ushort byteLen)
    {
        if (name.IsEmpty)
        {
            byteLen = 0;
            return 0;
        }

        int maxBytes = Encoding.UTF8.GetMaxByteCount(name.Length);
        if (maxBytes > ushort.MaxValue)
        {
            name = name[..(ushort.MaxValue / 4)];
            maxBytes = Encoding.UTF8.GetMaxByteCount(name.Length);
        }

        if (_currentSlabOffset + maxBytes > NameSlabSize)
        {
            AllocateNewSlab();
        }

        byte[] slab = _nameSlabs[_currentSlabIndex];
        int written = Encoding.UTF8.GetBytes(name, slab.AsSpan(_currentSlabOffset));
        int handle = (_currentSlabIndex << NameSlabShift) | _currentSlabOffset;
        _currentSlabOffset += written;
        byteLen = (ushort)written;
        return handle;
    }

    private void AllocateNewSlab()
    {
        _nameSlabs.Add(new byte[NameSlabSize]);
        _currentSlabIndex = _nameSlabs.Count - 1;
        _currentSlabOffset = 0;
    }

    public string GetName(int nodeId)
    {
        lock (_syncLock)
        {
            return GetNameUnsafe(nodeId);
        }
    }

    private string GetNameUnsafe(int nodeId)
    {
        ref NodeRecord rec = ref GetNodeRef(nodeId);
        if (rec.NameByteLength == 0)
            return string.Empty;

        int slabIdx = rec.NameHandle >> NameSlabShift;
        int offset = rec.NameHandle & NameSlabMask;
        return Encoding.UTF8.GetString(_nameSlabs[slabIdx], offset, rec.NameByteLength);
    }

    private bool NameEqualsUnsafe(int nodeId, ReadOnlySpan<char> targetName)
    {
        ref NodeRecord rec = ref GetNodeRef(nodeId);
        int slabIdx = rec.NameHandle >> NameSlabShift;
        int offset = rec.NameHandle & NameSlabMask;
        ReadOnlySpan<byte> storedUtf8 = _nameSlabs[slabIdx].AsSpan(offset, rec.NameByteLength);

        Span<byte> stackBuf = stackalloc byte[512];
        int maxBytes = Encoding.UTF8.GetMaxByteCount(targetName.Length);
        if (maxBytes <= stackBuf.Length)
        {
            int len = Encoding.UTF8.GetBytes(targetName, stackBuf);
            if (storedUtf8.SequenceEqual(stackBuf[..len]))
                return true;
        }
        return string.Equals(GetNameUnsafe(nodeId), targetName.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public string GetFullPath(int nodeId)
    {
        lock (_syncLock)
        {
            return GetFullPathUnsafe(nodeId);
        }
    }

    private string GetFullPathUnsafe(int nodeId)
    {
        if (nodeId <= 0)
            return RootPath;

        var parts = new List<string>(12);
        int curr = nodeId;
        while (curr > 0)
        {
            parts.Add(GetNameUnsafe(curr));
            curr = GetNodeRef(curr).ParentId;
        }
        parts.Reverse();
        return Path.Combine(RootPath, string.Join(Path.DirectorySeparatorChar, parts));
    }

    private void UnmapFileIdInternal(ulong fileId, int nodeId)
    {
        if (fileId == 0)
            return;

        ulong mftIndex = fileId & 0x0000FFFFFFFFFFFFUL;
        if (mftIndex < MaxPagedMftIndex)
        {
            int[]? page = _mftPages[(int)(mftIndex >> MftPageShift)];
            int offset = (int)(mftIndex & MftPageMask);
            // Only clear the slot if it still points at this node (it may already belong to a newer file)
            if (page != null && page[offset] == nodeId + 1)
                page[offset] = 0;
        }
        else if (_overflowFrnMap.TryGetValue(fileId, out int found) && found == nodeId)
        {
            _overflowFrnMap.Remove(fileId);
        }
    }

    private void MapFileIdInternal(ulong fileId, int nodeId)
    {
        if (fileId == 0)
            return;

        ulong mftIndex = fileId & 0x0000FFFFFFFFFFFFUL;
        if (mftIndex < MaxPagedMftIndex)
        {
            int pageIdx = (int)(mftIndex >> MftPageShift);
            int offset = (int)(mftIndex & MftPageMask);
            int[]? page = _mftPages[pageIdx];
            if (page == null)
            {
                page = new int[MftPageSize];
                _mftPages[pageIdx] = page;
            }
            page[offset] = nodeId + 1; // 1-based so 0 is empty
        }
        else
        {
            _overflowFrnMap[fileId] = nodeId;
        }
    }

    public bool TryGetNodeByFileId(ulong fileId, out int nodeId)
    {
        lock (_syncLock)
        {
            return TryGetNodeByFileIdUnsafe(fileId, out nodeId);
        }
    }

    private bool TryGetNodeByFileIdUnsafe(ulong fileId, out int nodeId)
    {
        if (TryGetMappedNodeUnsafe(fileId, out nodeId) && !GetNodeRef(nodeId).IsDeleted)
            return true;

        nodeId = -1;
        return false;
    }

    /// <summary>The node the FRN map points at, live or tombstoned.</summary>
    private bool TryGetMappedNodeUnsafe(ulong fileId, out int nodeId)
    {
        nodeId = -1;
        if (fileId == 0)
            return false;

        ulong mftIndex = fileId & 0x0000FFFFFFFFFFFFUL;
        if (mftIndex < MaxPagedMftIndex)
        {
            int[]? page = _mftPages[(int)(mftIndex >> MftPageShift)];
            int val = page != null ? page[(int)(mftIndex & MftPageMask)] : 0;
            if (val > 0)
            {
                nodeId = val - 1;
                return true;
            }
            return false;
        }

        if (_overflowFrnMap.TryGetValue(fileId, out int found))
        {
            nodeId = found;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Computes recursive directory sizes, file counts, and folder counts in an iterative post-order pass.
    /// Avoids stack overflow even on deeply nested directory trees.
    /// </summary>
    public void ComputeRecursiveTotals()
    {
        lock (_syncLock)
        {
            if (_nodeCount == 0)
                return;

            ComputeTotalsUnsafe(0);
        }
    }

    /// <summary>Recomputes the totals of directory <paramref name="rootDirId"/> and every live directory below it. The ancestors are not touched.</summary>
    private void ComputeTotalsUnsafe(int rootDirId)
    {
        // Collect all directories in BFS/DFS order from the start directory, then process in reverse (bottom-up)!
        var dirOrder = new List<int>(Math.Max(1024, _nodeCount / 8));
        var stack = new Stack<int>(256);
        stack.Push(rootDirId);

        while (stack.Count > 0)
        {
            int dirId = stack.Pop();
            dirOrder.Add(dirId);

            ref NodeRecord dirRec = ref GetNodeRef(dirId);
            dirRec.Size = 0;
            dirRec.FileCount = 0;
            dirRec.FolderCount = 0;

            int childId = dirRec.FirstChildId;
            while (childId != -1)
            {
                ref NodeRecord child = ref GetNodeRef(childId);
                if (!child.IsDeleted && child.IsDirectory)
                {
                    stack.Push(childId);
                }
                childId = child.NextSiblingId;
            }
        }

        // Reverse order guarantees every child directory is totaled before its parent directory!
        for (int i = dirOrder.Count - 1; i >= 0; i--)
        {
            int dirId = dirOrder[i];
            long totalSize = 0;
            int totalFiles = 0;
            int totalFolders = 0;

            int childId = GetNodeRef(dirId).FirstChildId;
            while (childId != -1)
            {
                ref NodeRecord child = ref GetNodeRef(childId);
                if (!child.IsDeleted)
                {
                    totalSize += child.Size;
                    if (child.IsDirectory)
                    {
                        totalFolders += 1 + child.FolderCount;
                        totalFiles += child.FileCount;
                    }
                    else
                    {
                        totalFiles += 1;
                    }
                }
                childId = child.NextSiblingId;
            }

            ref NodeRecord dir = ref GetNodeRef(dirId);
            dir.Size = totalSize;
            dir.FileCount = totalFiles;
            dir.FolderCount = totalFolders;
        }
    }

    /// <summary>
    /// Returns the active children of a directory sorted descending by size.
    /// </summary>
    public int[] GetSortedChildren(int parentId)
    {
        lock (_syncLock)
        {
            ref NodeRecord parent = ref GetNodeRef(parentId);
            int childId = parent.FirstChildId;
            if (childId == -1)
                return Array.Empty<int>();

            int count = 0;
            int probe = childId;
            while (probe != -1)
            {
                if (!GetNodeRef(probe).IsDeleted)
                    count++;
                probe = GetNodeRef(probe).NextSiblingId;
            }

            if (count == 0)
                return Array.Empty<int>();

            int[] result = new int[count];
            long[] sizes = ArrayPool<long>.Shared.Rent(count);
            try
            {
                int idx = 0;
                probe = childId;
                while (probe != -1)
                {
                    ref NodeRecord c = ref GetNodeRef(probe);
                    if (!c.IsDeleted)
                    {
                        result[idx] = probe;
                        sizes[idx] = c.Size;
                        idx++;
                    }
                    probe = c.NextSiblingId;
                }

                // Sort ascending by size, then reverse for descending order
                Array.Sort(sizes, result, 0, count);
                Array.Reverse(result, 0, count);
                return result;
            }
            finally
            {
                ArrayPool<long>.Shared.Return(sizes);
            }
        }
    }

    /// <summary>Name, totals and parent size of one node under a single lock (the root's name is its path).</summary>
    public NodeSnapshot GetNodeInfo(int nodeId)
    {
        lock (_syncLock)
        {
            return GetNodeInfoUnsafe(nodeId);
        }
    }

    private NodeSnapshot GetNodeInfoUnsafe(int nodeId)
    {
        ref NodeRecord rec = ref GetNodeRef(nodeId);
        string name = nodeId == 0 ? RootPath : GetNameUnsafe(nodeId);
        long parentSize = rec.ParentId >= 0 ? GetNodeRef(rec.ParentId).Size : 0;
        return new NodeSnapshot(nodeId, rec, name, parentSize);
    }

    private sealed class ChildOrder : IComparer<(long Size, int Id)>
    {
        public static readonly ChildOrder Instance = new();

        // Largest first; equal sizes by node id so that consecutive pages never overlap or leave gaps
        public int Compare((long Size, int Id) x, (long Size, int Id) y)
        {
            int bySize = y.Size.CompareTo(x.Size);
            return bySize != 0 ? bySize : x.Id.CompareTo(y.Id);
        }
    }

    /// <summary>
    /// Appends up to <paramref name="take"/> live children of a directory to <paramref name="results"/>, largest first,
    /// starting at rank <paramref name="skip"/>, with everything the UI needs for a row (name, totals, parent size),
    /// all read under one lock. Returns the total number of live children, so the caller knows how many are left.
    /// The sort covers all children and happens under the lock; for millions of entries call it off the UI thread.
    /// </summary>
    public int GetChildPage(int parentId, int skip, int take, List<NodeSnapshot> results)
    {
        lock (_syncLock)
        {
            ref NodeRecord parent = ref GetNodeRef(parentId);
            long parentSize = parent.Size;

            int count = 0;
            for (int c = parent.FirstChildId; c != -1; c = GetNodeRef(c).NextSiblingId)
            {
                if (!GetNodeRef(c).IsDeleted)
                    count++;
            }

            if (count == 0 || take <= 0 || skip >= count)
                return count;

            var entries = ArrayPool<(long Size, int Id)>.Shared.Rent(count);
            try
            {
                int idx = 0;
                for (int c = parent.FirstChildId; c != -1; c = GetNodeRef(c).NextSiblingId)
                {
                    ref NodeRecord rec = ref GetNodeRef(c);
                    if (!rec.IsDeleted)
                        entries[idx++] = (rec.Size, c);
                }

                Array.Sort(entries, 0, count, ChildOrder.Instance);

                int end = (int)Math.Min((long)count, (long)skip + take);
                for (int i = Math.Max(0, skip); i < end; i++)
                {
                    int id = entries[i].Id;
                    results.Add(new NodeSnapshot(id, GetNodeRef(id), GetNameUnsafe(id), parentSize));
                }
                return count;
            }
            finally
            {
                ArrayPool<(long Size, int Id)>.Shared.Return(entries);
            }
        }
    }

    // =========================================================================
    // Real-time NTFS USN Journal Incremental Mutation Operations
    // =========================================================================

    /// <summary>Remembers a node touched by a live update (call under the lock). Beyond the cap further nodes are dropped.</summary>
    private void NoteActivityUnsafe(int nodeId)
    {
        if (_activity.Count < MaxActivityNodes)
            _activity.Add(nodeId);
    }

    /// <summary>
    /// Returns the nodes that live updates (created, changed, renamed/moved - the old parent too -, deleted or unlinked)
    /// touched since the last call, and forgets them. A deleted node stays in the list and keeps its parent link.
    /// </summary>
    public int[] TakeActivity()
    {
        lock (_syncLock)
        {
            if (_activity.Count == 0)
                return Array.Empty<int>();

            int[] nodes = new int[_activity.Count];
            _activity.CopyTo(nodes);
            _activity.Clear();
            return nodes;
        }
    }

    /// <summary>
    /// Runs <paramref name="read"/> with the store lock held, once, handing it a parent lookup (-1 for the root or an
    /// unknown id). Works for deleted nodes too, whose parent link is kept, so a batch can walk many ancestor chains
    /// under a single lock acquisition. The callback must be quick and must not call back into the store.
    /// </summary>
    public void ReadParents(Action<Func<int, int>> read)
    {
        lock (_syncLock)
        {
            read(id => id >= 0 && id < _nodeCount ? GetNodeRef(id).ParentId : -1);
        }
    }

    public UsnApplyResult ApplyUsnCreateOrUpdate(
        ulong fileId,
        ulong parentFileId,
        ReadOnlySpan<char> fileName,
        long newFileSize,
        bool isDirectory,
        bool sizeKnown = true,
        bool treatUnmappedParentAsGone = true,
        bool isRename = true)
    {
        lock (_syncLock)
        {
            if (TryGetNodeByFileIdUnsafe(fileId, out int existingNodeId))
            {
                ref NodeRecord existing = ref GetNodeRef(existingNodeId);
                bool parentKnown = TryGetNodeByFileIdUnsafe(parentFileId, out int newParentId);

                // The FRN is shared by every name of a hard-linked file, so a record for a name other than the
                // carrier's is not a move unless it says so (isRename). The size lives on the carrier only.
                // A rename of one name of a file that has several is indistinguishable from a new link here: the
                // new name is added and the old one stays until the next rescan.
                bool linkRecord = !isDirectory && (!isRename || _linkGroups.ContainsKey(fileId));
                if (linkRecord)
                {
                    bool isCarrierName = parentKnown && newParentId == existing.ParentId &&
                                         (fileName.IsEmpty || NameEqualsUnsafe(existingNodeId, fileName));
                    if (parentKnown && !isCarrierName && !fileName.IsEmpty &&
                        FindLinkByNameUnsafe(newParentId, fileName, fileId) < 0)
                    {
                        int linkId = AddNodeInternal(newParentId, fileName, 0, fileId, isDirectory: false);
                        ref NodeRecord parentRec = ref GetNodeRef(newParentId);
                        GetNodeRef(linkId).NextSiblingId = parentRec.FirstChildId;
                        parentRec.FirstChildId = linkId;
                        RegisterLinkUnsafe(fileId, existingNodeId, linkId);
                        PropagateDeltaUnsafe(newParentId, 0, 1, 0);
                        NoteActivityUnsafe(linkId);
                    }
                    // else: the name is already tracked, or it lives outside the tracked tree
                }
                // Check if renamed or moved to a different parent
                else if (!parentKnown)
                {
                    // An unmapped parent is only a problem if it is not the parent the node already has
                    if (existing.ParentId >= 0 && GetNodeRef(existing.ParentId).FileId != parentFileId)
                    {
                        if (!treatUnmappedParentAsGone)
                            return UsnApplyResult.ParentUnmapped;

                        // Moved out of the tracked tree: it no longer counts here
                        NoteActivityUnsafe(existingNodeId);
                        DeleteNodeUnsafe(existingNodeId);
                        return UsnApplyResult.Applied;
                    }
                }
                else if (newParentId != existing.ParentId)
                {
                    NoteActivityUnsafe(existing.ParentId); // the old location
                    MoveNodeParentUnsafe(existingNodeId, newParentId);
                }

                if (!linkRecord && !fileName.IsEmpty && !NameEqualsUnsafe(existingNodeId, fileName))
                {
                    int newHandle = WriteNameUtf8(fileName, out ushort byteLen);
                    ref NodeRecord recAfterWrite = ref GetNodeRef(existingNodeId);
                    recAfterWrite.NameHandle = newHandle;
                    recAfterWrite.NameByteLength = byteLen;
                }

                // A failed size read must not zero the node: keep the old size
                if (!isDirectory && sizeKnown)
                {
                    ref NodeRecord fileRec = ref GetNodeRef(existingNodeId);
                    long sizeDelta = newFileSize - fileRec.Size;
                    if (sizeDelta != 0)
                    {
                        fileRec.Size = newFileSize;
                        PropagateDeltaUnsafe(fileRec.ParentId, sizeDelta, 0, 0);
                    }
                }
                NoteActivityUnsafe(existingNodeId);
                return UsnApplyResult.Applied;
            }
            else
            {
                // Newly created file or folder
                if (!TryGetNodeByFileIdUnsafe(parentFileId, out int parentNodeId))
                    return UsnApplyResult.ParentUnmapped;

                // Check if a child with the same name already exists under parentNodeId
                // (atomic-save pattern: a temp file with a new FRN replaces the old file name)
                int existingByName = FindChildByNameUnsafe(parentNodeId, fileName);
                if (existingByName != -1)
                {
                    ref NodeRecord rec = ref GetNodeRef(existingByName);

                    // Retire the old FRN before mapping the new one so the old file delete
                    // record can no longer resolve to this live node.
                    UnmapFileIdInternal(rec.FileId, existingByName);
                    rec.FileId = fileId;
                    MapFileIdInternal(fileId, existingByName);

                    if (!isDirectory && sizeKnown)
                    {
                        long delta = newFileSize - rec.Size;
                        rec.Size = newFileSize;
                        if (delta != 0)
                            PropagateDeltaUnsafe(parentNodeId, delta, 0, 0);
                    }
                    NoteActivityUnsafe(existingByName);
                    return UsnApplyResult.Applied;
                }

                long initialSize = isDirectory || !sizeKnown ? 0 : newFileSize;
                int nodeId = AddNodeInternal(parentNodeId, fileName, initialSize, fileId, isDirectory);
                ref NodeRecord parent = ref GetNodeRef(parentNodeId);
                ref NodeRecord child = ref GetNodeRef(nodeId);
                child.NextSiblingId = parent.FirstChildId;
                parent.FirstChildId = nodeId;

                if (fileId != 0)
                    MapFileIdInternal(fileId, nodeId);

                PropagateDeltaUnsafe(
                    parentNodeId,
                    initialSize,
                    isDirectory ? 0 : 1,
                    isDirectory ? 1 : 0);

                NoteActivityUnsafe(nodeId);
                return UsnApplyResult.Applied;
            }
        }
    }

    public bool ApplyUsnDelete(ulong fileId, ulong parentFileId, ReadOnlySpan<char> fileName)
    {
        lock (_syncLock)
        {
            int targetNodeId = -1;
            if (TryGetNodeByFileIdUnsafe(fileId, out int mappedNodeId))
            {
                // The map only keys on the MFT index: make sure this really is the file being deleted
                ulong storedId = GetNodeRef(mappedNodeId).FileId;
                if (storedId != 0 && storedId != fileId)
                    return false;
                targetNodeId = mappedNodeId;
            }
            else if (TryGetNodeByFileIdUnsafe(parentFileId, out int parentId))
            {
                int byName = FindChildByNameUnsafe(parentId, fileName);
                if (byName > 0)
                {
                    // Never let a stale delete (old FRN) remove a node that has since been
                    // re-bound to a different FRN, e.g. by an atomic save.
                    ulong storedId = GetNodeRef(byName).FileId;
                    if (fileId == 0 || storedId == 0 || storedId == fileId)
                        targetNodeId = byName;
                }
            }

            if (targetNodeId <= 0)
                return false;

            NoteActivityUnsafe(targetNodeId);

            // A delete record by FRN means the file itself is gone, so every name of a hard-linked file goes
            // (the extra links first, so the carrier has no heir to hand its size to)
            ulong targetFileId = GetNodeRef(targetNodeId).FileId;
            if (fileId != 0 && targetFileId != 0 && _linkGroups.TryGetValue(targetFileId, out List<int>? group))
            {
                foreach (int linkId in group.ToArray())
                {
                    if (linkId != targetNodeId && !GetNodeRef(linkId).IsDeleted && GetNodeRef(linkId).FileId == targetFileId)
                        DeleteNodeUnsafe(linkId);
                }
            }

            DeleteNodeUnsafe(targetNodeId);
            return true;
        }
    }

    /// <summary>
    /// One name of a hard-linked file was removed while the file lives on. Drops that name's node (the size stays
    /// counted once: a carrier hands it to one of the remaining names). Returns false if the name is not tracked.
    /// </summary>
    public bool ApplyUsnLinkRemoved(ulong fileId, ulong parentFileId, ReadOnlySpan<char> fileName)
    {
        lock (_syncLock)
        {
            if (!TryGetNodeByFileIdUnsafe(parentFileId, out int parentId))
                return false;

            int linkId = FindLinkByNameUnsafe(parentId, fileName, fileId);
            if (linkId <= 0)
                return false;

            NoteActivityUnsafe(linkId);
            DeleteNodeUnsafe(linkId);
            return true;
        }
    }

    public bool DeleteNodeById(int nodeId)
    {
        lock (_syncLock)
        {
            if (nodeId <= 0 || nodeId >= _nodeCount)
                return false;
            DeleteNodeUnsafe(nodeId);
            return true;
        }
    }

    private void DeleteNodeUnsafe(int nodeId)
    {
        ref NodeRecord rec = ref GetNodeRef(nodeId);
        if (rec.IsDeleted)
            return;

        // The node totals already exclude anything deleted earlier, so propagate exactly once
        long removedSize = rec.Size;
        int removedFiles = rec.IsDirectory ? rec.FileCount : 1;
        int removedFolders = rec.IsDirectory ? (1 + rec.FolderCount) : 0;
        int parentId = rec.ParentId;

        // If this name carries the size of a hard-linked file that has other names, one of them takes it over.
        // (A carrier lost inside a deleted subtree is not handed over here: the file stays under-counted until a
        // branch refresh or rescan.)
        if (!rec.IsDirectory)
            ReleaseLinkUnsafe(nodeId);

        TombstoneSubtreeUnsafe(nodeId);

        PropagateDeltaUnsafe(parentId, -removedSize, -removedFiles, -removedFolders);
    }

    /// <summary>
    /// Flags a node and every live descendant as deleted (iteratively, no recursion). Deleted nodes are
    /// unresolvable by FRN, so later events for any of them cannot propagate deltas a second time.
    /// Does not touch ancestor totals; callers propagate the delta themselves.
    /// Already-deleted descendants (and their subtrees) are skipped. With includeRoot false only the
    /// descendants are tombstoned.
    /// </summary>
    private void TombstoneSubtreeUnsafe(int nodeId, bool includeRoot = true)
    {
        var stack = new Stack<int>(64);
        stack.Push(nodeId);

        while (stack.Count > 0)
        {
            int id = stack.Pop();
            ref NodeRecord rec = ref GetNodeRef(id);
            if (rec.IsDeleted)
                continue;

            if (id != nodeId || includeRoot)
                rec.Flags |= NodeRecord.FlagDeleted;

            int childId = rec.FirstChildId;
            while (childId != -1)
            {
                ref NodeRecord child = ref GetNodeRef(childId);
                if (!child.IsDeleted)
                    stack.Push(childId);
                childId = child.NextSiblingId;
            }
        }
    }

    /// <summary>
    /// Replaces everything below directory <paramref name="dirNodeId"/> with the contents of a freshly scanned
    /// <paramref name="scratch"/> store (whose root node is that directory) and applies the change in totals to
    /// the ancestors exactly once. The directory keeps its node id so UI rows that reference it stay valid.
    /// Returns false (store untouched) if the node is not a live directory.
    /// Old descendants are tombstoned, not reclaimed; their FRN map slots may keep pointing at them, which is
    /// harmless: lookups skip deleted nodes and a later map write for the same MFT index simply overwrites the slot.
    /// The scratch store must no longer be in use by anything else.
    /// </summary>
    public bool GraftBranch(int dirNodeId, FileSystemStore scratch)
    {
        if (ReferenceEquals(scratch, this))
            throw new ArgumentException("The scratch store must be a different store.", nameof(scratch));

        lock (_syncLock)
        {
            if (dirNodeId < 0 || dirNodeId >= _nodeCount)
                return false;

            NodeRecord dir = GetNodeRef(dirNodeId);
            if (dir.IsDeleted || !dir.IsDirectory)
                return false;

            NodeRecord scratchRoot = scratch.GetNodeRef(scratch.RootNodeId);
            if (!scratchRoot.IsDirectory)
                return false;

            long oldSize = dir.Size;
            int oldFiles = dir.FileCount;
            int oldFolders = dir.FolderCount;

            // Detach the old subtree. Its nodes stay allocated but are unreachable and unresolvable.
            TombstoneSubtreeUnsafe(dirNodeId, includeRoot: false);
            GetNodeRef(dirNodeId).FirstChildId = -1;

            // Re-key the directory itself if the folder was replaced (same path, new FRN)
            if (scratchRoot.FileId != 0 && scratchRoot.FileId != dir.FileId)
            {
                UnmapFileIdInternal(dir.FileId, dirNodeId);
                GetNodeRef(dirNodeId).FileId = scratchRoot.FileId;
                MapFileIdInternal(scratchRoot.FileId, dirNodeId);
            }

            CopyScratchSubtreeUnsafe(dirNodeId, scratch);

            // Totals are recomputed over the copy (not taken from the scratch store, whose sizes assume it counted
            // every link); the ancestors get the difference once
            ComputeTotalsUnsafe(dirNodeId);
            ref NodeRecord dirRec = ref GetNodeRef(dirNodeId);

            if (dirRec.ParentId >= 0)
            {
                PropagateDeltaUnsafe(
                    dirRec.ParentId,
                    dirRec.Size - oldSize,
                    dirRec.FileCount - oldFiles,
                    dirRec.FolderCount - oldFolders);
            }

            if (_linkGroups.Count > 0)
                HandOverOrphanedLinkSizesUnsafe();

            return true;
        }
    }

    /// <summary>
    /// Copies everything below the scratch store's root into the (childless) directory <paramref name="dirNodeId"/>,
    /// breadth first. Totals are not computed here.
    /// Hard links: whatever was tombstoned before the call is unresolvable, so a file whose FRN still resolves here
    /// belongs to a node outside the copied branch and is already counted; AddAndMapNodeUnsafe then gives the copied
    /// name size 0. The scratch store has its own carrier per file, so every copy of a linked file is given the file's
    /// size and the first one copied becomes the carrier here, whatever the copy order.
    /// </summary>
    private void CopyScratchSubtreeUnsafe(int dirNodeId, FileSystemStore scratch)
    {
        // (scratch directory id, id of its counterpart in this store)
        var pending = new Queue<(int ScratchDirId, int NewDirId)>(256);
        pending.Enqueue((scratch.RootNodeId, dirNodeId));

        while (pending.Count > 0)
        {
            (int scratchDirId, int newDirId) = pending.Dequeue();

            int firstChild = -1;
            int scratchChildId = scratch.GetNodeRef(scratchDirId).FirstChildId;
            while (scratchChildId != -1)
            {
                int currentScratchId = scratchChildId;
                NodeRecord sc = scratch.GetNodeRef(currentScratchId);
                scratchChildId = sc.NextSiblingId;
                if (sc.IsDeleted)
                    continue;

                long size = sc.Size;
                if (!sc.IsDirectory && sc.FileId != 0 &&
                    scratch.TryGetNodeByFileIdUnsafe(sc.FileId, out int scratchCarrierId) &&
                    scratch.GetNodeRef(scratchCarrierId).FileId == sc.FileId)
                {
                    size = scratch.GetNodeRef(scratchCarrierId).Size;
                }

                int newId = AddAndMapNodeUnsafe(newDirId, scratch.GetNameUnsafe(currentScratchId), size, sc.FileId, sc.IsDirectory);
                GetNodeRef(newId).NextSiblingId = firstChild;
                firstChild = newId;

                if (sc.IsDirectory)
                    pending.Enqueue((currentScratchId, newId));
            }

            GetNodeRef(newDirId).FirstChildId = firstChild;
        }
    }

    /// <summary>
    /// Makes <paramref name="name"/> a child of directory <paramref name="parentId"/> that was scanned elsewhere: the
    /// root of <paramref name="scratch"/> is the directory (copied with everything below it), or, with a null scratch
    /// store, a file of <paramref name="fileSize"/> bytes. A live child of the same name is replaced. Everything happens
    /// under one lock and the ancestors get the net difference exactly once, so it is safe next to a monitor that
    /// applies the same change: whichever comes second finds the node in place and changes nothing more.
    /// This is the delta for a move into a drive root, where a branch refresh would be a rescan of the whole drive.
    /// Returns the new node id, or -1 if the parent is not a live directory.
    /// </summary>
    public int ReplaceChildWithScanned(int parentId, string name, ulong fileId, long fileSize, FileSystemStore? scratch)
    {
        lock (_syncLock)
        {
            if (parentId < 0 || parentId >= _nodeCount)
                return -1;

            NodeRecord parent = GetNodeRef(parentId);
            if (parent.IsDeleted || !parent.IsDirectory)
                return -1;

            int existingId = FindChildByNameUnsafe(parentId, name);
            if (existingId > 0)
                DeleteNodeUnsafe(existingId);

            bool isDirectory = scratch != null;
            int newId = AddAndMapNodeUnsafe(parentId, name, isDirectory ? 0 : fileSize, fileId, isDirectory);
            GetNodeRef(newId).NextSiblingId = GetNodeRef(parentId).FirstChildId;
            GetNodeRef(parentId).FirstChildId = newId;

            if (scratch != null)
            {
                CopyScratchSubtreeUnsafe(newId, scratch);
                ComputeTotalsUnsafe(newId);
            }

            NodeRecord added = GetNodeRef(newId);
            PropagateDeltaUnsafe(
                parentId,
                added.Size,
                isDirectory ? added.FileCount : 1,
                isDirectory ? 1 + added.FolderCount : 0);

            if (_linkGroups.Count > 0)
                HandOverOrphanedLinkSizesUnsafe();

            return newId;
        }
    }

    /// <summary>Finds the live node at <paramref name="fullPath"/> by walking down from the root by name (case-insensitive). The root path itself gives node 0.</summary>
    public bool TryFindNodeByPath(string fullPath, out int nodeId)
    {
        nodeId = -1;
        string rootNoSlash = RootPath.TrimEnd('\\');
        if (!fullPath.StartsWith(rootNoSlash, StringComparison.OrdinalIgnoreCase) ||
            (fullPath.Length > rootNoSlash.Length && fullPath[rootNoSlash.Length] != '\\'))
            return false;

        string rest = fullPath.Substring(rootNoSlash.Length).Trim('\\');
        lock (_syncLock)
        {
            int current = 0;
            if (rest.Length > 0)
            {
                foreach (string part in rest.Split('\\'))
                {
                    current = FindChildByNameUnsafe(current, part);
                    if (current < 0)
                        return false;
                }
            }

            if (GetNodeRef(current).IsDeleted)
                return false;

            nodeId = current;
            return true;
        }
    }

    /// <summary>
    /// A hard-linked file whose carrier was tombstoned (in a replaced or deleted subtree) and which still has a live
    /// name elsewhere is no longer counted by anyone: the first live name takes over the size and the FRN map slot.
    /// Walks only the linked files, so it is cheap enough to run after a graft.
    /// </summary>
    private void HandOverOrphanedLinkSizesUnsafe()
    {
        foreach ((ulong fileId, List<int> group) in _linkGroups.ToArray())
        {
            if (!TryGetMappedNodeUnsafe(fileId, out int mapped) || !GetNodeRef(mapped).IsDeleted)
                continue;

            int heirId = -1;
            foreach (int id in group)
            {
                ref NodeRecord candidate = ref GetNodeRef(id);
                if (!candidate.IsDeleted && candidate.FileId == fileId)
                {
                    heirId = id;
                    break;
                }
            }
            if (heirId < 0)
                continue;

            long size = GetNodeRef(mapped).Size;
            GetNodeRef(heirId).Size = size;
            MapFileIdInternal(fileId, heirId);
            if (size != 0)
                PropagateDeltaUnsafe(GetNodeRef(heirId).ParentId, size, 0, 0);
        }
    }

    private void MoveNodeParentUnsafe(int nodeId, int newParentId)
    {
        ref NodeRecord node = ref GetNodeRef(nodeId);
        int oldParentId = node.ParentId;
        if (oldParentId == newParentId || oldParentId < 0)
            return;

        // Unlink from oldParentId's FirstChildId linked list
        ref NodeRecord oldParent = ref GetNodeRef(oldParentId);
        int prev = -1;
        int curr = oldParent.FirstChildId;
        while (curr != -1)
        {
            if (curr == nodeId)
            {
                if (prev == -1)
                    oldParent.FirstChildId = node.NextSiblingId;
                else
                    GetNodeRef(prev).NextSiblingId = node.NextSiblingId;
                break;
            }
            prev = curr;
            curr = GetNodeRef(curr).NextSiblingId;
        }

        long movedSize = node.Size;
        int movedFiles = node.IsDirectory ? node.FileCount : 1;
        int movedFolders = node.IsDirectory ? (1 + node.FolderCount) : 0;

        PropagateDeltaUnsafe(oldParentId, -movedSize, -movedFiles, -movedFolders);

        // Link to newParentId
        ref NodeRecord newParent = ref GetNodeRef(newParentId);
        node.ParentId = newParentId;
        node.NextSiblingId = newParent.FirstChildId;
        newParent.FirstChildId = nodeId;

        PropagateDeltaUnsafe(newParentId, movedSize, movedFiles, movedFolders);
    }

    private int FindChildByNameUnsafe(int parentId, ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || parentId < 0)
            return -1;

        int childId = GetNodeRef(parentId).FirstChildId;
        while (childId != -1)
        {
            ref NodeRecord child = ref GetNodeRef(childId);
            if (!child.IsDeleted && NameEqualsUnsafe(childId, name))
                return childId;
            childId = child.NextSiblingId;
        }
        return -1;
    }

    private void PropagateDeltaUnsafe(int startParentId, long sizeDelta, int fileDelta, int folderDelta)
    {
        int curr = startParentId;
        int guard = 0;
        while (curr >= 0 && guard++ < 1024)
        {
            ref NodeRecord p = ref GetNodeRef(curr);
            p.Size = Math.Max(0, p.Size + sizeDelta);
            p.FileCount = Math.Max(0, p.FileCount + fileDelta);
            p.FolderCount = Math.Max(0, p.FolderCount + folderDelta);
            curr = p.ParentId;
        }
    }
}

public readonly struct PendingChildEntry
{
    public readonly string Name;
    public readonly long Size;
    public readonly ulong FileId;
    public readonly bool IsDirectory;

    public PendingChildEntry(string name, long size, ulong fileId, bool isDirectory)
    {
        Name = name;
        Size = size;
        FileId = fileId;
        IsDirectory = isDirectory;
    }
}
