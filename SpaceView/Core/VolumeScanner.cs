using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceView.Native;

namespace SpaceView.Core;

public sealed class ScanProgressReport
{
    public int ScannedNodes { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public string CurrentStatus { get; init; } = string.Empty;
}

public sealed class ScanResult
{
    public required FileSystemStore Store { get; init; }
    public required TimeSpan Duration { get; init; }
    public bool IsNtfs { get; init; }
    public bool UsnJournalReady { get; init; }
    public ulong UsnJournalId { get; init; }
    public long StartUsn { get; init; }
}

public static class VolumeScanner
{
    private const uint BufferSize = 64 * 1024; // 64 KB buffer per worker thread

    static VolumeScanner()
    {
        NtfsNative.EnableBackupPrivilege();
    }

    public static async Task<ScanResult> ScanDriveAsync(
        DriveInfo drive,
        IProgress<ScanProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        string rootPath = drive.RootDirectory.FullName;
        bool isNtfs = false;
        try
        {
            isNtfs = string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Ignore if format cannot be queried
        }

        return await Task.Run(() => ExecuteScan(rootPath, isNtfs, progress, cancellationToken), cancellationToken);
    }

    private static ScanResult ExecuteScan(
        string rootPath,
        bool isNtfs,
        IProgress<ScanProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        ulong usnJournalId = 0;
        long startUsn = 0;
        bool usnReady = false;

        if (isNtfs)
        {
            usnReady = TryQueryOrCreateUsnJournal(rootPath, out usnJournalId, out startUsn);
        }

        ulong rootFileId = GetDirectoryFileId(rootPath);
        var store = new FileSystemStore(rootPath, rootFileId);

        var workQueue = new ConcurrentQueue<(int NodeId, string Path)>();
        int pendingDirectories = 1;
        workQueue.Enqueue((store.RootNodeId, store.RootPath));

        int workerCount = Math.Clamp(Environment.ProcessorCount, 4, 16);
        var workers = new Task[workerCount];

        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progressTask = Task.Run(async () =>
        {
            while (!progressCts.Token.IsCancellationRequested && Volatile.Read(ref pendingDirectories) > 0)
            {
                try
                {
                    await Task.Delay(120, progressCts.Token);
                    int count = store.TotalNodeCount;
                    progress?.Report(new ScanProgressReport
                    {
                        ScannedNodes = count,
                        ElapsedMilliseconds = sw.ElapsedMilliseconds,
                        CurrentStatus = $"Scanning {rootPath} ({count:N0} items found)..."
                    });
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, CancellationToken.None);

        for (int w = 0; w < workerCount; w++)
        {
            workers[w] = Task.Run(() => RunWorker(store, workQueue, ref pendingDirectories, cancellationToken), CancellationToken.None);
        }

        Task.WaitAll(workers);
        progressCts.Cancel();
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new ScanProgressReport
        {
            ScannedNodes = store.TotalNodeCount,
            ElapsedMilliseconds = sw.ElapsedMilliseconds,
            CurrentStatus = "Calculating folder consumption totals..."
        });

        store.ComputeRecursiveTotals();
        sw.Stop();

        return new ScanResult
        {
            Store = store,
            Duration = sw.Elapsed,
            IsNtfs = isNtfs,
            UsnJournalReady = usnReady,
            UsnJournalId = usnJournalId,
            StartUsn = startUsn
        };
    }

    private static unsafe void RunWorker(
        FileSystemStore store,
        ConcurrentQueue<(int NodeId, string Path)> workQueue,
        ref int pendingDirectories,
        CancellationToken cancellationToken)
    {
        byte* buffer = (byte*)NativeMemory.AlignedAlloc(BufferSize, 8);
        var batchEntries = new List<PendingChildEntry>(256);
        var dirNames = new List<string>(64);

        try
        {
            var spin = new SpinWait();
            while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref pendingDirectories) > 0)
            {
                if (workQueue.TryDequeue(out var currentDir))
                {
                    spin.Reset();
                    try
                    {
                        ScanSingleDirectory(
                            store,
                            currentDir.NodeId,
                            currentDir.Path,
                            buffer,
                            batchEntries,
                            dirNames,
                            workQueue,
                            ref pendingDirectories,
                            cancellationToken);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref pendingDirectories);
                    }
                }
                else
                {
                    spin.SpinOnce();
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    private static unsafe void ScanSingleDirectory(
        FileSystemStore store,
        int dirNodeId,
        string dirPath,
        byte* buffer,
        List<PendingChildEntry> batchEntries,
        List<string> dirNames,
        ConcurrentQueue<(int NodeId, string Path)> workQueue,
        ref int pendingDirectories,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return;

        // Use extended-length path prefix \\?\ to handle paths > 260 characters seamlessly
        string nativePath = dirPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? dirPath
            : @"\\?\" + dirPath;

        using SafeFileHandle hDir = NtfsNative.CreateFileW(
            nativePath,
            NtfsNative.FILE_LIST_DIRECTORY | NtfsNative.FILE_READ_ATTRIBUTES,
            NtfsNative.FILE_SHARE_ALL,
            IntPtr.Zero,
            NtfsNative.OPEN_EXISTING,
            NtfsNative.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (hDir.IsInvalid)
            return;

        batchEntries.Clear();
        dirNames.Clear();

        int infoClass = NtfsNative.FileIdBothDirectoryRestartInfo;

        while (!ct.IsCancellationRequested)
        {
            bool ok = NtfsNative.GetFileInformationByHandleEx(hDir, infoClass, buffer, BufferSize);
            if (!ok)
                break;

            infoClass = NtfsNative.FileIdBothDirectoryInfo;

            byte* ptr = buffer;
            while (true)
            {
                NtfsNative.FILE_ID_BOTH_DIR_INFO* info = (NtfsNative.FILE_ID_BOTH_DIR_INFO*)ptr;
                int charLen = (int)(info->FileNameLength / sizeof(char));

                if (charLen > 0)
                {
                    ReadOnlySpan<char> nameSpan = new ReadOnlySpan<char>(info->FileName, charLen);

                    // Skip "." and ".." entries
                    bool isDot = (charLen == 1 && nameSpan[0] == '.') ||
                                 (charLen == 2 && nameSpan[0] == '.' && nameSpan[1] == '.');

                    if (!isDot)
                    {
                        uint attrs = info->FileAttributes;
                        bool isDir = (attrs & NtfsNative.FILE_ATTRIBUTE_DIRECTORY) != 0;
                        bool isReparse = (attrs & NtfsNative.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

                        // Skip directory junctions / symlinks to avoid cycles and double-counting
                        if (!(isDir && isReparse))
                        {
                            string name = new string(nameSpan);
                            long size = isDir ? 0 : Math.Max(0, info->EndOfFile);
                            ulong fileId = (ulong)info->FileId;

                            batchEntries.Add(new PendingChildEntry(name, size, fileId, isDir));
                            if (isDir)
                            {
                                dirNames.Add(name);
                            }
                        }
                    }
                }

                if (info->NextEntryOffset == 0)
                    break;

                ptr += info->NextEntryOffset;
            }
        }

        if (batchEntries.Count == 0)
            return;

        Span<PendingChildEntry> entriesSpan = CollectionsMarshal.AsSpan(batchEntries);
        int[] createdDirNodeIds = dirNames.Count > 0 ? new int[dirNames.Count] : Array.Empty<int>();

        store.AddDirectoryBatch(dirNodeId, entriesSpan, createdDirNodeIds, out int createdDirCount);

        if (createdDirCount > 0)
        {
            // Increment pendingDirectories BEFORE enqueueing so workers never premature-exit
            Interlocked.Add(ref pendingDirectories, createdDirCount);

            string baseDir = dirPath.EndsWith('\\') ? dirPath : dirPath + "\\";
            for (int i = 0; i < createdDirCount; i++)
            {
                string subPath = baseDir + dirNames[i];
                workQueue.Enqueue((createdDirNodeIds[i], subPath));
            }
        }
    }

    private static ulong GetDirectoryFileId(string path)
    {
        using SafeFileHandle hDir = NtfsNative.CreateFileW(
            path,
            NtfsNative.FILE_READ_ATTRIBUTES,
            NtfsNative.FILE_SHARE_ALL,
            IntPtr.Zero,
            NtfsNative.OPEN_EXISTING,
            NtfsNative.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (!hDir.IsInvalid && NtfsNative.GetFileInformationByHandle(hDir, out var info))
        {
            return info.FileReferenceNumber;
        }
        return 0;
    }

    public static unsafe bool TryQueryOrCreateUsnJournal(string rootPath, out ulong journalId, out long nextUsn)
    {
        journalId = 0;
        nextUsn = 0;

        string volumeDevicePath = @"\\.\" + rootPath.TrimEnd('\\');
        using SafeFileHandle hVolume = NtfsNative.CreateFileW(
            volumeDevicePath,
            NtfsNative.GENERIC_READ,
            NtfsNative.FILE_SHARE_ALL,
            IntPtr.Zero,
            NtfsNative.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (hVolume.IsInvalid)
            return false;

        NtfsNative.USN_JOURNAL_DATA_V0 journalData = default;
        if (NtfsNative.DeviceIoControl(
            hVolume,
            NtfsNative.FSCTL_QUERY_USN_JOURNAL,
            null,
            0,
            &journalData,
            (uint)sizeof(NtfsNative.USN_JOURNAL_DATA_V0),
            out _,
            IntPtr.Zero))
        {
            journalId = journalData.UsnJournalID;
            nextUsn = journalData.NextUsn;
            return true;
        }

        int err = Marshal.GetLastWin32Error();
        if (err == NtfsNative.ERROR_JOURNAL_NOT_ACTIVE)
        {
            var createData = new NtfsNative.CREATE_USN_JOURNAL_DATA
            {
                MaximumSize = 32 * 1024 * 1024,
                AllocationDelta = 8 * 1024 * 1024
            };
            if (NtfsNative.DeviceIoControl(
                hVolume,
                NtfsNative.FSCTL_CREATE_USN_JOURNAL,
                &createData,
                (uint)sizeof(NtfsNative.CREATE_USN_JOURNAL_DATA),
                null,
                0,
                out _,
                IntPtr.Zero))
            {
                if (NtfsNative.DeviceIoControl(
                    hVolume,
                    NtfsNative.FSCTL_QUERY_USN_JOURNAL,
                    null,
                    0,
                    &journalData,
                    (uint)sizeof(NtfsNative.USN_JOURNAL_DATA_V0),
                    out _,
                    IntPtr.Zero))
                {
                    journalId = journalData.UsnJournalID;
                    nextUsn = journalData.NextUsn;
                    return true;
                }
            }
        }

        return false;
    }
}
