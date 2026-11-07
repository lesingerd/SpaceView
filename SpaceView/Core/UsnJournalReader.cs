using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceView.Native;

namespace SpaceView.Core;

/// <summary>One file's journal records, combined: the newest parent/name/attributes and every reason seen.</summary>
internal struct UsnBatchItem
{
    public ulong ParentFrn;
    public string Name;
    public uint CombinedReason;
    public uint Attrs;
}

/// <summary>Journal records, keyed by FRN but remembered in USN (first-seen) order.</summary>
internal sealed class UsnBatch
{
    // Rough cost of one entry besides its name: dictionary slot, order list slot, string header
    private const int EntryOverheadBytes = 120;

    public readonly Dictionary<ulong, UsnBatchItem> Items = new(256);
    public readonly List<ulong> Order = new(256);
    public string LastItemName = string.Empty;

    /// <summary>Approximate memory held by the entries; lets the background drain enforce a cap.</summary>
    public long EstimatedBytes { get; private set; }

    /// <summary>Adds a record, combining it with an earlier one of the same FRN (reasons are OR-ed, the newest name wins).</summary>
    public void Add(ulong frn, ulong parentFrn, string name, uint reason, uint attrs)
    {
        if (Items.TryGetValue(frn, out var prev))
        {
            reason |= prev.CombinedReason;
            EstimatedBytes += 2L * (name.Length - prev.Name.Length);
        }
        else
        {
            Order.Add(frn);
            EstimatedBytes += EntryOverheadBytes + 2L * name.Length;
        }
        Items[frn] = new UsnBatchItem
        {
            ParentFrn = parentFrn,
            Name = name,
            CombinedReason = reason,
            Attrs = attrs
        };
        LastItemName = name;
    }

    public void Clear()
    {
        Items.Clear();
        Order.Clear();
        LastItemName = string.Empty;
        EstimatedBytes = 0;
    }
}

internal enum UsnChunkResult
{
    /// <summary>The read failed; see the Win32 error.</summary>
    Failed,
    /// <summary>Nothing (more) to read right now.</summary>
    End,
    /// <summary>Records were read and the position moved on; read again.</summary>
    More
}

/// <summary>
/// Reads FSCTL_READ_USN_JOURNAL on a volume handle. Shared by the live monitor and by the drain that runs during a scan,
/// so both parse and combine records the same way.
/// </summary>
internal static class UsnJournalReader
{
    public const uint ReadBufferSize = 64 * 1024;

    // MFT records 0-23 are reserved for NTFS metadata files ($MFT, $LogFile, $Bitmap, ...)
    private const ulong MftIndexMask = 0x0000FFFFFFFFFFFFUL;
    private const ulong FirstUserMftIndex = 24;

    public const uint MonitoredReasons =
        NtfsNative.USN_REASON_FILE_CREATE |
        NtfsNative.USN_REASON_FILE_DELETE |
        NtfsNative.USN_REASON_DATA_EXTEND |
        NtfsNative.USN_REASON_DATA_TRUNCATION |
        NtfsNative.USN_REASON_DATA_OVERWRITE |
        NtfsNative.USN_REASON_RENAME_OLD_NAME |
        NtfsNative.USN_REASON_RENAME_NEW_NAME |
        NtfsNative.USN_REASON_HARD_LINK_CHANGE |
        NtfsNative.USN_REASON_CLOSE;

    public static unsafe IntPtr AllocateAlignedBuffer()
    {
        return (IntPtr)NativeMemory.AlignedAlloc(ReadBufferSize, 8);
    }

    public static unsafe void FreeAlignedBuffer(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            NativeMemory.AlignedFree((void*)ptr);
        }
    }

    /// <summary>
    /// Reads one buffer of records starting at <paramref name="nextUsn"/> and adds them to <paramref name="batch"/>.
    /// The position advances even when every new record was filtered out, so it never lags behind the journal tail
    /// and falls off the end of the (circular) journal.
    /// </summary>
    public static unsafe UsnChunkResult ReadChunk(
        SafeFileHandle hVolume,
        IntPtr bufferPtr,
        ulong journalId,
        ref long nextUsn,
        UsnBatch batch,
        out int win32Error)
    {
        win32Error = 0;
        byte* buffer = (byte*)bufferPtr;

        var readData = new NtfsNative.READ_USN_JOURNAL_DATA_V0
        {
            StartUsn = nextUsn,
            ReasonMask = MonitoredReasons,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalID = journalId
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
            win32Error = Marshal.GetLastWin32Error();
            return UsnChunkResult.Failed;
        }

        if (bytesReturned < sizeof(long))
            return UsnChunkResult.End;

        long newUsn = *(long*)buffer;
        if (newUsn <= nextUsn)
            return UsnChunkResult.End;

        nextUsn = newUsn;

        if (bytesReturned == sizeof(long))
            return UsnChunkResult.End;

        uint offset = sizeof(long);
        while (offset + sizeof(NtfsNative.USN_RECORD_V2) <= bytesReturned)
        {
            NtfsNative.USN_RECORD_V2* rec = (NtfsNative.USN_RECORD_V2*)(buffer + offset);
            if (rec->RecordLength == 0)
                break;

            // Skip NTFS metadata files ($MFT, $LogFile, ...) by MFT record number, not by name:
            // legitimate "$" names (e.g. Recycle Bin $R... renames) must still be tracked.
            ulong frn = rec->FileReferenceNumber;
            if (rec->MajorVersion == 2 &&
                (frn & MftIndexMask) >= FirstUserMftIndex &&
                rec->FileNameOffset + rec->FileNameLength <= rec->RecordLength)
            {
                char* namePtr = (char*)((byte*)rec + rec->FileNameOffset);
                int charLen = rec->FileNameLength / sizeof(char);
                if (charLen > 0)
                {
                    batch.Add(frn, rec->ParentFileReferenceNumber, new string(namePtr, 0, charLen), rec->Reason, rec->FileAttributes);
                }
            }

            offset += rec->RecordLength;
        }
        return UsnChunkResult.More;
    }

    /// <summary>
    /// After a failed read: decides whether the position in the journal is lost (wrapped, reset, deleted) or the failure
    /// may pass. When it is lost, <paramref name="journalId"/> and <paramref name="nextUsn"/> are moved to the current
    /// journal tail (if it can be queried) and <paramref name="lostMessage"/> says what happened.
    /// </summary>
    public static unsafe bool IsPositionLost(
        SafeFileHandle hVolume,
        int readError,
        ref ulong journalId,
        ref long nextUsn,
        out string lostMessage)
    {
        lostMessage = string.Empty;

        NtfsNative.USN_JOURNAL_DATA_V0 journal = default;
        bool queried = NtfsNative.DeviceIoControl(
            hVolume,
            NtfsNative.FSCTL_QUERY_USN_JOURNAL,
            null,
            0,
            &journal,
            (uint)sizeof(NtfsNative.USN_JOURNAL_DATA_V0),
            out _,
            IntPtr.Zero);
        int queryError = queried ? 0 : Marshal.GetLastWin32Error();

        if (queried)
        {
            bool lostPosition =
                readError == NtfsNative.ERROR_JOURNAL_ENTRY_DELETED ||
                readError == NtfsNative.ERROR_JOURNAL_NOT_ACTIVE ||
                journal.UsnJournalID != journalId ||
                nextUsn < journal.LowestValidUsn;

            if (lostPosition)
            {
                // Re-sync to the current journal tail; the tree itself must be rebuilt by a rescan
                journalId = journal.UsnJournalID;
                nextUsn = journal.NextUsn;
                lostMessage = $"The NTFS change journal was reset or wrapped (Win32 error {readError})";
                return true;
            }
        }
        else if (queryError == NtfsNative.ERROR_JOURNAL_NOT_ACTIVE ||
                 readError == NtfsNative.ERROR_JOURNAL_NOT_ACTIVE ||
                 readError == NtfsNative.ERROR_JOURNAL_ENTRY_DELETED)
        {
            lostMessage = $"The NTFS change journal is no longer active (Win32 error {readError})";
            return true;
        }

        return false;
    }
}
