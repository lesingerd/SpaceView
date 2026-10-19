using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceView.Native;

namespace SpaceView.Core;

public sealed class UsnMonitorEventArgs : EventArgs
{
    public int BatchEventsProcessed { get; init; }
    public long TotalEventsProcessed { get; init; }
    public string LastChangedItem { get; init; } = string.Empty;
    public bool IsUsnJournalMode { get; init; }
}

/// <summary>
/// Real-time NTFS USN Change Journal monitor that reads FSCTL_READ_USN_JOURNAL records
/// and updates the FileSystemStore in place with O(depth) delta propagation.
/// Includes automatic fallback to FileSystemWatcher for non-NTFS volumes or non-admin runs.
/// </summary>
public sealed class NtfsUsnJournalMonitor : IDisposable
{
    private const uint ReadBufferSize = 64 * 1024;

    private const uint MonitoredReasons =
        NtfsNative.USN_REASON_FILE_CREATE |
        NtfsNative.USN_REASON_FILE_DELETE |
        NtfsNative.USN_REASON_DATA_EXTEND |
        NtfsNative.USN_REASON_DATA_TRUNCATION |
        NtfsNative.USN_REASON_DATA_OVERWRITE |
        NtfsNative.USN_REASON_RENAME_OLD_NAME |
        NtfsNative.USN_REASON_RENAME_NEW_NAME |
        NtfsNative.USN_REASON_CLOSE;

    private readonly FileSystemStore _store;
    private readonly ulong _journalId;
    private long _nextUsn;
    private readonly bool _useUsnJournal;
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;
    private FileSystemWatcher? _fallbackWatcher;
    private long _totalEventsProcessed;
    private bool _disposed;

    public bool IsUsnJournalActive => _useUsnJournal;
    public long TotalEventsProcessed => Interlocked.Read(ref _totalEventsProcessed);

    public event EventHandler<UsnMonitorEventArgs>? ChangesDetected;

    public NtfsUsnJournalMonitor(
        FileSystemStore store,
        bool usnReady,
        ulong journalId,
        long startUsn)
    {
        _store = store;
        _journalId = journalId;
        _nextUsn = startUsn;
        _useUsnJournal = usnReady && journalId != 0;
    }

    public void Start()
    {
        if (_useUsnJournal)
        {
            _monitorTask = Task.Run(() => RunUsnJournalLoopAsync(_cts.Token));
        }
        else
        {
            StartFallbackWatcher();
        }
    }

    private async Task RunUsnJournalLoopAsync(CancellationToken ct)
    {
        string volumeDevicePath = @"\\.\" + _store.RootPath.TrimEnd('\\');
        using SafeFileHandle hVolume = NtfsNative.CreateFileW(
            volumeDevicePath,
            NtfsNative.GENERIC_READ,
            NtfsNative.FILE_SHARE_ALL,
            IntPtr.Zero,
            NtfsNative.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (hVolume.IsInvalid)
        {
            StartFallbackWatcher();
            return;
        }

        IntPtr bufferPtr = AllocateAlignedBuffer();
        try
        {
            var seenInBatch = new Dictionary<ulong, (ulong ParentFrn, string Name, uint CombinedReason, uint Attrs)>(256);

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
                PollAndApplyUsnBatch(hVolume, bufferPtr, seenInBatch, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            FreeAlignedBuffer(bufferPtr);
        }
    }

    private static unsafe IntPtr AllocateAlignedBuffer()
    {
        return (IntPtr)NativeMemory.AlignedAlloc(ReadBufferSize, 8);
    }

    private static unsafe void FreeAlignedBuffer(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            NativeMemory.AlignedFree((void*)ptr);
        }
    }

    private unsafe void PollAndApplyUsnBatch(
        SafeFileHandle hVolume,
        IntPtr bufferPtr,
        Dictionary<ulong, (ulong ParentFrn, string Name, uint CombinedReason, uint Attrs)> seenInBatch,
        CancellationToken ct)
    {
        byte* buffer = (byte*)bufferPtr;
        seenInBatch.Clear();
        string lastItemName = string.Empty;

        while (!ct.IsCancellationRequested)
        {
            var readData = new NtfsNative.READ_USN_JOURNAL_DATA_V0
            {
                StartUsn = _nextUsn,
                ReasonMask = MonitoredReasons,
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalID = _journalId
            };

            if (!NtfsNative.DeviceIoControl(
                hVolume,
                NtfsNative.FSCTL_READ_USN_JOURNAL,
                &readData,
                (uint)sizeof(NtfsNative.READ_USN_JOURNAL_DATA_V0),
                buffer,
                ReadBufferSize,
                out uint bytesReturned,
                IntPtr.Zero))
            {
                break;
            }

            if (bytesReturned <= sizeof(long))
                break;

            long nextUsn = *(long*)buffer;
            if (nextUsn <= _nextUsn)
                break;

            _nextUsn = nextUsn;

            uint offset = sizeof(long);
            while (offset + sizeof(NtfsNative.USN_RECORD_V2) <= bytesReturned)
            {
                NtfsNative.USN_RECORD_V2* rec = (NtfsNative.USN_RECORD_V2*)(buffer + offset);
                if (rec->RecordLength == 0)
                    break;

                if (rec->MajorVersion == 2 &&
                    rec->FileNameOffset + rec->FileNameLength <= rec->RecordLength)
                {
                    char* namePtr = (char*)((byte*)rec + rec->FileNameOffset);
                    int charLen = rec->FileNameLength / sizeof(char);
                    if (charLen > 0)
                    {
                        string name = new string(namePtr, 0, charLen);
                        // Ignore internal NTFS metadata streams ($MFT, $UsnJrnl, etc.)
                        if (name[0] != '$')
                        {
                            ulong frn = rec->FileReferenceNumber;
                            uint combinedReason = rec->Reason;
                            if (seenInBatch.TryGetValue(frn, out var prev))
                            {
                                combinedReason |= prev.CombinedReason;
                            }
                            seenInBatch[frn] = (rec->ParentFileReferenceNumber, name, combinedReason, rec->FileAttributes);
                            lastItemName = name;
                        }
                    }
                }

                offset += rec->RecordLength;
            }

            // Cap batch size so UI updates stay responsive during heavy disk writes
            if (seenInBatch.Count >= 2000)
                break;
        }

        if (seenInBatch.Count == 0)
            return;

        int appliedCount = 0;
        foreach (var kvp in seenInBatch)
        {
            if (ct.IsCancellationRequested)
                break;

            ulong fileId = kvp.Key;
            var (parentFrn, name, reason, attrs) = kvp.Value;

            bool isDir = (attrs & NtfsNative.FILE_ATTRIBUTE_DIRECTORY) != 0;
            bool isReparse = (attrs & NtfsNative.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
            if (isDir && isReparse)
                continue;

            if ((reason & NtfsNative.USN_REASON_FILE_DELETE) != 0)
            {
                if (_store.ApplyUsnDelete(fileId, parentFrn, name.AsSpan()))
                {
                    appliedCount++;
                }
            }
            else
            {
                long fileSize = 0;
                if (!isDir)
                {
                    if (!TryGetFileSizeById(hVolume, fileId, out fileSize))
                    {
                        if (_store.TryGetNodeByFileId(parentFrn, out int parentNodeId))
                        {
                            try
                            {
                                string fullPath = Path.Combine(_store.GetFullPath(parentNodeId), name);
                                var fi = new FileInfo(fullPath);
                                if (fi.Exists)
                                    fileSize = fi.Length;
                            }
                            catch
                            {
                                // Ignore transient file access errors
                            }
                        }
                    }
                }

                if (_store.ApplyUsnCreateOrUpdate(fileId, parentFrn, name.AsSpan(), fileSize, isDir))
                {
                    appliedCount++;
                }
            }
        }

        if (appliedCount > 0)
        {
            long total = Interlocked.Add(ref _totalEventsProcessed, appliedCount);
            ChangesDetected?.Invoke(this, new UsnMonitorEventArgs
            {
                BatchEventsProcessed = appliedCount,
                TotalEventsProcessed = total,
                LastChangedItem = lastItemName,
                IsUsnJournalMode = true
            });
        }
    }

    private static unsafe bool TryGetFileSizeById(SafeFileHandle hVolume, ulong fileId, out long fileSize)
    {
        fileSize = 0;
        var desc = new NtfsNative.FILE_ID_DESCRIPTOR
        {
            dwSize = (uint)sizeof(NtfsNative.FILE_ID_DESCRIPTOR),
            Type = 0, // FileIdType
            FileId = fileId
        };

        using SafeFileHandle hFile = NtfsNative.OpenFileById(
            hVolume,
            ref desc,
            NtfsNative.FILE_READ_ATTRIBUTES,
            NtfsNative.FILE_SHARE_ALL,
            IntPtr.Zero,
            NtfsNative.FILE_FLAG_BACKUP_SEMANTICS);

        if (!hFile.IsInvalid && NtfsNative.GetFileSizeEx(hFile, out fileSize))
        {
            return true;
        }
        return false;
    }

    private void StartFallbackWatcher()
    {
        try
        {
            _fallbackWatcher = new FileSystemWatcher(_store.RootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
            };

            _fallbackWatcher.Created += (_, e) => HandleFallbackEvent(e.FullPath, isDelete: false);
            _fallbackWatcher.Changed += (_, e) => HandleFallbackEvent(e.FullPath, isDelete: false);
            _fallbackWatcher.Deleted += (_, e) => HandleFallbackEvent(e.FullPath, isDelete: true);
            _fallbackWatcher.Renamed += (_, e) =>
            {
                HandleFallbackEvent(e.OldFullPath, isDelete: true);
                HandleFallbackEvent(e.FullPath, isDelete: false);
            };
            _fallbackWatcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Ignore if volume doesn't support FileSystemWatcher
        }
    }

    private void HandleFallbackEvent(string fullPath, bool isDelete)
    {
        try
        {
            string? dirPath = Path.GetDirectoryName(fullPath);
            string name = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(dirPath) || string.IsNullOrEmpty(name))
                return;

            ulong parentId = GetFileIdForPath(dirPath);
            if (parentId == 0)
                return;

            if (isDelete)
            {
                if (_store.ApplyUsnDelete(0, parentId, name.AsSpan()))
                {
                    long total = Interlocked.Increment(ref _totalEventsProcessed);
                    ChangesDetected?.Invoke(this, new UsnMonitorEventArgs
                    {
                        BatchEventsProcessed = 1,
                        TotalEventsProcessed = total,
                        LastChangedItem = name,
                        IsUsnJournalMode = false
                    });
                }
            }
            else
            {
                ulong fileId = GetFileIdForPath(fullPath);
                bool isDir = Directory.Exists(fullPath);
                long size = 0;
                if (!isDir && File.Exists(fullPath))
                {
                    size = new FileInfo(fullPath).Length;
                }
                if (_store.ApplyUsnCreateOrUpdate(fileId, parentId, name.AsSpan(), size, isDir))
                {
                    long total = Interlocked.Increment(ref _totalEventsProcessed);
                    ChangesDetected?.Invoke(this, new UsnMonitorEventArgs
                    {
                        BatchEventsProcessed = 1,
                        TotalEventsProcessed = total,
                        LastChangedItem = name,
                        IsUsnJournalMode = false
                    });
                }
            }
        }
        catch
        {
            // Ignore transient errors
        }
    }

    private static ulong GetFileIdForPath(string path)
    {
        using SafeFileHandle h = NtfsNative.CreateFileW(
            path,
            NtfsNative.FILE_READ_ATTRIBUTES,
            NtfsNative.FILE_SHARE_ALL,
            IntPtr.Zero,
            NtfsNative.OPEN_EXISTING,
            NtfsNative.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (!h.IsInvalid && NtfsNative.GetFileInformationByHandle(h, out var info))
        {
            return info.FileReferenceNumber;
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _fallbackWatcher?.Dispose();
        _cts.Dispose();
    }
}
