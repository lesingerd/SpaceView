using System.IO;
using SpaceView.Native;

namespace SpaceView.Core;

/// <summary>
/// Brings the top level of a drive's tree in line with the disk without rescanning the drive. A move into or out of a
/// drive root changes only the root's direct children, and a branch refresh of the root would be a scan of everything.
/// Store children that are gone from the disk are deleted; entries that are new (and the names given as <c>replaceNames</c>,
/// which may have overwritten something) are scanned on their own and added with
/// <see cref="FileSystemStore.ReplaceChildWithScanned"/>. Only the new items cost anything.
/// </summary>
public static class RootReconciler
{
    private readonly record struct DiskEntry(string Name, bool IsDirectory, ulong FileId);

    public sealed class Result
    {
        public int Removed { get; init; }
        public int Added { get; init; }
        /// <summary>False if the root could not be listed (the store was left as it was).</summary>
        public bool Listed { get; init; }
    }

    /// <summary>
    /// Must be called from the thread that owns the store's UI state (it only awaits the folder listing and the scans of
    /// new folders). <paramref name="isLive"/> is checked after every await: false stops the work, the store keeps
    /// whatever was applied so far (each store call is complete in itself).
    /// </summary>
    public static async Task<Result> ReconcileAsync(
        FileSystemStore store,
        IReadOnlyCollection<string> replaceNames,
        Func<bool> isLive,
        CancellationToken cancellationToken)
    {
        string rootPath = store.RootPath;
        List<DiskEntry>? disk = await Task.Run(() => ListTopLevel(rootPath), cancellationToken);
        if (disk == null)
            return new Result { Listed = false };
        cancellationToken.ThrowIfCancellationRequested();
        if (!isLive())
            return new Result { Listed = true };

        var inStore = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (int childId in store.GetSortedChildren(store.RootNodeId))
            inStore[store.GetName(childId)] = childId;

        int removed = 0;
        var onDisk = new HashSet<string>(disk.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        foreach ((string name, int childId) in inStore)
        {
            if (!onDisk.Contains(name) && store.DeleteNodeById(childId))
                removed++;
        }

        int added = 0;
        foreach (DiskEntry entry in disk)
        {
            if (inStore.ContainsKey(entry.Name) && !replaceNames.Contains(entry.Name))
                continue;

            string path = Path.Combine(rootPath, entry.Name);
            if (entry.IsDirectory)
            {
                BranchScanResult scan = await VolumeScanner.ScanBranchAsync(path, null, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!isLive())
                    break;

                ulong frn = scan.Store.GetNodeSnapshot(scan.Store.RootNodeId).FileId;
                if (store.ReplaceChildWithScanned(store.RootNodeId, entry.Name, frn != 0 ? frn : entry.FileId, 0, scan.Store) >= 0)
                    added++;
            }
            else if (store.ReplaceChildWithScanned(store.RootNodeId, entry.Name, entry.FileId, ReadAllocationSize(path), null) >= 0)
            {
                added++;
            }
        }

        return new Result { Listed = true, Removed = removed, Added = added };
    }

    /// <summary>The entries directly in a folder as the scanner would list them (links to other folders left out); null if the folder cannot be listed.</summary>
    private static List<DiskEntry>? ListTopLevel(string folder)
    {
        try
        {
            var entries = new List<DiskEntry>();
            foreach (FileSystemInfo info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                bool isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                ulong fileId = 0;
                using (var handle = NtfsNative.OpenForQuery(info.FullName))
                {
                    if (!handle.IsInvalid && NtfsNative.GetFileInformationByHandle(handle, out NtfsNative.BY_HANDLE_FILE_INFORMATION details))
                        fileId = details.FileReferenceNumber;

                    // Symlinks and junctions to folders are not part of the tree (the scanner skips them)
                    if (isDirectory && (info.Attributes & FileAttributes.ReparsePoint) != 0 &&
                        NtfsNative.TryGetReparseTag(handle, out uint tag) && NtfsNative.IsReparseTagNameSurrogate(tag))
                        continue;
                }
                entries.Add(new DiskEntry(info.Name, isDirectory, fileId));
            }
            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long ReadAllocationSize(string path)
    {
        using var handle = NtfsNative.OpenForQuery(path);
        return NtfsNative.TryGetAllocationSize(handle, out long size) ? size : 0;
    }
}
