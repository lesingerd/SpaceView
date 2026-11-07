using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceView.Native;

public static unsafe class NtfsNative
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_LIST_DIRECTORY = 0x00000001;
    public const uint FILE_READ_ATTRIBUTES = 0x00000080;

    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint FILE_SHARE_DELETE = 0x00000004;
    public const uint FILE_SHARE_ALL = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    /// <summary>Open the reparse point itself instead of following it (never recalls cloud content, never crosses a link).</summary>
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

    // Bit 29 of a reparse tag marks name-surrogate reparse points (symlinks 0xA000000C, mount points/junctions
    // 0xA0000003): they redirect to another part of the namespace. Cloud placeholders (OneDrive 0x9000xxxx),
    // dedup, WSL and similar tags are not name surrogates and are ordinary files/folders.
    private const uint ReparseTagNameSurrogate = 0x20000000;

    /// <summary>Equivalent of the SDK's IsReparseTagNameSurrogate macro.</summary>
    public static bool IsReparseTagNameSurrogate(uint reparseTag) => (reparseTag & ReparseTagNameSurrogate) != 0;

    public const int FileStandardInfo = 1;
    public const int FileAttributeTagInfo = 9;
    public const int FileIdBothDirectoryInfo = 10;
    public const int FileIdBothDirectoryRestartInfo = 11;

    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_JOURNAL_ENTRY_DELETED = 1181;
    public const int ERROR_JOURNAL_NOT_ACTIVE = 1179;
    public const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;

    // DeviceIoControl codes for USN Journal and MFT
    public const uint FSCTL_ENUM_USN_DATA = 0x000900b3;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900bb;
    public const uint FSCTL_CREATE_USN_JOURNAL = 0x000900e7;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;

    // USN Reason Flags
    public const uint USN_REASON_DATA_OVERWRITE = 0x00000001;
    public const uint USN_REASON_DATA_EXTEND = 0x00000002;
    public const uint USN_REASON_DATA_TRUNCATION = 0x00000004;
    public const uint USN_REASON_FILE_CREATE = 0x00000100;
    public const uint USN_REASON_FILE_DELETE = 0x00000200;
    public const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
    public const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
    public const uint USN_REASON_HARD_LINK_CHANGE = 0x00010000;
    public const uint USN_REASON_CLOSE = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ID_BOTH_DIR_INFO
    {
        public uint NextEntryOffset;
        public uint FileIndex;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public long EndOfFile;
        public long AllocationSize;
        public uint FileAttributes;
        public uint FileNameLength;
        public uint EaSize;
        public byte ShortNameLength;
        public fixed char ShortName[12];
        public long FileId;
        public fixed char FileName[1];
    }

    /// <summary>FILE_STANDARD_INFO (GetFileInformationByHandleEx, FileStandardInfo).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_STANDARD_INFO
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    /// <summary>FILE_ATTRIBUTE_TAG_INFO (GetFileInformationByHandleEx, FileAttributeTagInfo).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct USN_JOURNAL_DATA_V0
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CREATE_USN_JOURNAL_DATA
    {
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_ENUM_DATA_V0
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct READ_USN_JOURNAL_DATA_V0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct USN_RECORD_V2
    {
        public uint RecordLength;
        public ushort MajorVersion;
        public ushort MinorVersion;
        public ulong FileReferenceNumber;
        public ulong ParentFileReferenceNumber;
        public long Usn;
        public long TimeStamp;
        public uint Reason;
        public uint SourceInfo;
        public uint SecurityId;
        public uint FileAttributes;
        public ushort FileNameLength;
        public ushort FileNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ID_DESCRIPTOR
    {
        public uint dwSize;
        public int Type; // 0 = FileIdType
        public ulong FileId;
        // The native descriptor ends in a 16-byte union (FILE_ID_128), so the struct is 24 bytes.
        public ulong UnionPadding;
    }

    // Win32 FILETIME is two 32-bit halves (4-byte aligned). Declaring it as a long would shift
    // every following field of BY_HANDLE_FILE_INFORMATION by 4 bytes.
    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;

        public ulong FileReferenceNumber => ((ulong)FileIndexHigh << 32) | FileIndexLow;
        public long FileSize => ((long)FileSizeHigh << 32) | FileSizeLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int FileInformationClass,
        byte* lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileSizeEx(
        SafeFileHandle hFile,
        out long lpFileSize);

    /// <summary>
    /// Opens a file or directory for attribute queries only, without following a reparse point and without
    /// requesting data access, so a cloud placeholder is neither recalled nor hydrated.
    /// </summary>
    public static SafeFileHandle OpenForQuery(string path)
    {
        // The \\?\ prefix turns off path normalisation and the MAX_PATH limit; UNC and already-prefixed paths are left alone
        string native = path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + path;
        return CreateFileW(
            native,
            FILE_READ_ATTRIBUTES,
            FILE_SHARE_ALL,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);
    }

    /// <summary>Size on disk (allocated bytes) of the opened file; the same figure a directory listing reports as AllocationSize.</summary>
    public static bool TryGetAllocationSize(SafeFileHandle hFile, out long allocationSize)
    {
        FILE_STANDARD_INFO info;
        if (!hFile.IsInvalid && GetFileInformationByHandleEx(hFile, FileStandardInfo, (byte*)&info, (uint)sizeof(FILE_STANDARD_INFO)))
        {
            allocationSize = Math.Max(0, info.AllocationSize);
            return true;
        }
        allocationSize = 0;
        return false;
    }

    public static bool TryGetReparseTag(SafeFileHandle hFile, out uint reparseTag)
    {
        FILE_ATTRIBUTE_TAG_INFO info;
        if (!hFile.IsInvalid && GetFileInformationByHandleEx(hFile, FileAttributeTagInfo, (byte*)&info, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
        {
            reparseTag = info.ReparseTag;
            return true;
        }
        reparseTag = 0;
        return false;
    }

    /// <summary>Enter background processing mode (Very Low I/O priority, low memory priority) for the calling thread.</summary>
    public const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    public const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    /// <summary>PERFORMANCE_INFORMATION (psapi.h); counts are in pages of PageSize bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeFileHandle OpenFileById(
        SafeFileHandle hVolumeHint,
        ref FILE_ID_DESCRIPTOR lpFileId,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwFlagsAndAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        void* lpInBuffer,
        uint nInBufferSize,
        void* lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    // Token privilege elevation for SeBackupPrivilege
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static void EnableBackupPrivilege()
    {
        try
        {
            if (OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
            {
                try
                {
                    if (LookupPrivilegeValueW(null, "SeBackupPrivilege", out LUID luid))
                    {
                        var tp = new TOKEN_PRIVILEGES
                        {
                            PrivilegeCount = 1,
                            Luid = luid,
                            Attributes = SE_PRIVILEGE_ENABLED
                        };
                        AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                finally
                {
                    CloseHandle(token);
                }
            }
        }
        catch
        {
            // Ignore if not running elevated
        }
    }

    // Shell properties dialog helper
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const int SW_SHOW = 5;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO lpExecInfo);

    public static void ShowFileProperties(string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            lpVerb = "properties",
            lpFile = path,
            nShow = SW_SHOW,
            fMask = SEE_MASK_INVOKEIDLIST
        };
        ShellExecuteExW(ref info);
    }

    // Window positioning & cursor helpers for Tray popup
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);
}
