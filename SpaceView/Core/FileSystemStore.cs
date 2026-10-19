using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SpaceView.Core;

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

    public string RootPath { get; }
    public int RootNodeId => 0;

    public int TotalNodeCount => Volatile.Read(ref _nodeCount);
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
            int nodeId = AddNodeInternal(parentId, name, size, fileId, isDirectory);
            if (linkToParent && parentId >= 0)
            {
                ref NodeRecord parent = ref GetNodeRef(parentId);
                ref NodeRecord child = ref GetNodeRef(nodeId);
                child.NextSiblingId = parent.FirstChildId;
                parent.FirstChildId = nodeId;
            }
            if (fileId != 0)
            {
                MapFileIdInternal(fileId, nodeId);
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
                int nodeId = AddNodeInternal(parentId, entry.Name.AsSpan(), entry.Size, entry.FileId, entry.IsDirectory);

                // Re-fetch parent ref in case _chunks grew
                ref NodeRecord child = ref GetNodeRef(nodeId);
                child.NextSiblingId = firstChild;
                firstChild = nodeId;

                if (entry.FileId != 0)
                {
                    MapFileIdInternal(entry.FileId, nodeId);
                }

                if (entry.IsDirectory)
                {
                    outCreatedDirNodeIds[createdDirCount++] = nodeId;
                }
            }

            GetNodeRef(parentId).FirstChildId = firstChild;
        }
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

    private void MapFileIdInternal(ulong fileId, int nodeId)
    {
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
        if (fileId == 0)
        {
            nodeId = -1;
            return false;
        }

        ulong mftIndex = fileId & 0x0000FFFFFFFFFFFFUL;
        if (mftIndex < MaxPagedMftIndex)
        {
            int pageIdx = (int)(mftIndex >> MftPageShift);
            int[]? page = _mftPages[pageIdx];
            if (page != null)
            {
                int val = page[(int)(mftIndex & MftPageMask)];
                if (val > 0)
                {
                    nodeId = val - 1;
                    if (!GetNodeRef(nodeId).IsDeleted)
                        return true;
                }
            }
        }
        else if (_overflowFrnMap.TryGetValue(fileId, out int found) && !GetNodeRef(found).IsDeleted)
        {
            nodeId = found;
            return true;
        }

        nodeId = -1;
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

            // Collect all directories in BFS/DFS order from root, then process in reverse (bottom-up)!
            var dirOrder = new List<int>(Math.Max(1024, _nodeCount / 8));
            var stack = new Stack<int>(256);
            stack.Push(0);

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

    // =========================================================================
    // Real-time NTFS USN Journal Incremental Mutation Operations
    // =========================================================================

    public bool ApplyUsnCreateOrUpdate(
        ulong fileId,
        ulong parentFileId,
        ReadOnlySpan<char> fileName,
        long newFileSize,
        bool isDirectory)
    {
        lock (_syncLock)
        {
            if (TryGetNodeByFileIdUnsafe(fileId, out int existingNodeId))
            {
                ref NodeRecord existing = ref GetNodeRef(existingNodeId);

                // Check if renamed or moved to a different parent
                if (TryGetNodeByFileIdUnsafe(parentFileId, out int newParentId) && newParentId != existing.ParentId)
                {
                    MoveNodeParentUnsafe(existingNodeId, newParentId);
                }

                if (!fileName.IsEmpty && !NameEqualsUnsafe(existingNodeId, fileName))
                {
                    int newHandle = WriteNameUtf8(fileName, out ushort byteLen);
                    ref NodeRecord recAfterWrite = ref GetNodeRef(existingNodeId);
                    recAfterWrite.NameHandle = newHandle;
                    recAfterWrite.NameByteLength = byteLen;
                }

                if (!isDirectory)
                {
                    ref NodeRecord fileRec = ref GetNodeRef(existingNodeId);
                    long sizeDelta = newFileSize - fileRec.Size;
                    if (sizeDelta != 0)
                    {
                        fileRec.Size = newFileSize;
                        PropagateDeltaUnsafe(fileRec.ParentId, sizeDelta, 0, 0);
                    }
                }
                return true;
            }
            else
            {
                // Newly created file or folder
                if (!TryGetNodeByFileIdUnsafe(parentFileId, out int parentNodeId))
                    return false;

                // Check if a child with the same name already exists under parentNodeId
                int existingByName = FindChildByNameUnsafe(parentNodeId, fileName);
                if (existingByName != -1)
                {
                    MapFileIdInternal(fileId, existingByName);
                    ref NodeRecord rec = ref GetNodeRef(existingByName);
                    rec.FileId = fileId;
                    if (!isDirectory)
                    {
                        long delta = newFileSize - rec.Size;
                        rec.Size = newFileSize;
                        if (delta != 0)
                            PropagateDeltaUnsafe(parentNodeId, delta, 0, 0);
                    }
                    return true;
                }

                int nodeId = AddNodeInternal(parentNodeId, fileName, isDirectory ? 0 : newFileSize, fileId, isDirectory);
                ref NodeRecord parent = ref GetNodeRef(parentNodeId);
                ref NodeRecord child = ref GetNodeRef(nodeId);
                child.NextSiblingId = parent.FirstChildId;
                parent.FirstChildId = nodeId;

                if (fileId != 0)
                    MapFileIdInternal(fileId, nodeId);

                PropagateDeltaUnsafe(
                    parentNodeId,
                    isDirectory ? 0 : newFileSize,
                    isDirectory ? 0 : 1,
                    isDirectory ? 1 : 0);

                return true;
            }
        }
    }

    public bool ApplyUsnDelete(ulong fileId, ulong parentFileId, ReadOnlySpan<char> fileName)
    {
        lock (_syncLock)
        {
            int targetNodeId = -1;
            if (!TryGetNodeByFileIdUnsafe(fileId, out targetNodeId))
            {
                if (TryGetNodeByFileIdUnsafe(parentFileId, out int parentId))
                {
                    targetNodeId = FindChildByNameUnsafe(parentId, fileName);
                }
            }

            if (targetNodeId <= 0)
                return false;

            DeleteNodeUnsafe(targetNodeId);
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

        rec.Flags |= NodeRecord.FlagDeleted;
        long removedSize = rec.Size;
        int removedFiles = rec.IsDirectory ? rec.FileCount : 1;
        int removedFolders = rec.IsDirectory ? (1 + rec.FolderCount) : 0;

        PropagateDeltaUnsafe(rec.ParentId, -removedSize, -removedFiles, -removedFolders);
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
