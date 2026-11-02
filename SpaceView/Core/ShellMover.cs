using System.IO;
using System.Runtime.InteropServices;

namespace SpaceView.Core;

[Flags]
public enum ShellMoveOptions
{
    /// <summary>The Windows way: progress dialog, conflict prompts ("replace / skip / keep both"), error dialogs, Undo in Explorer.</summary>
    None = 0,
    /// <summary>No dialogs at all (for tests and automation). Together with <see cref="RenameOnCollision"/> a conflict gives the item a new name; without it the shell skips or overwrites as it sees fit.</summary>
    Silent = 1,
    /// <summary>Give an item whose name is taken in the destination a new name ("name (2)") instead of asking.</summary>
    RenameOnCollision = 2
}

public sealed class ShellMoveResult
{
    /// <summary>Sources that are gone from their old place after the operation (checked on the file system, not taken from the shell's return code).</summary>
    public required IReadOnlyList<string> Moved { get; init; }

    /// <summary>Sources that are still where they were.</summary>
    public required IReadOnlyList<string> NotMoved { get; init; }

    /// <summary>The user cancelled in the Windows dialog.</summary>
    public bool Aborted { get; init; }

    /// <summary>The return code of SHFileOperation (0 = success).</summary>
    public int ResultCode { get; init; }

    /// <summary>The shell refused for lack of rights (an elevated SpaceView might get through).</summary>
    public bool AccessDenied { get; init; }

    public bool AllMoved => NotMoved.Count == 0;
}

/// <summary>
/// Moves files and folders with the Windows shell (SHFileOperation, FO_MOVE) in a single call, so the user gets the
/// native progress dialog, conflict prompts and error dialogs, and the move can be undone in Explorer (FOF_ALLOWUNDO).
/// The shell needs a single-threaded apartment, so <see cref="MoveAsync"/> runs the call on a dedicated STA thread.
/// What really moved is read back from the file system afterwards: a cancelled or half-failed operation leaves a mix.
/// </summary>
public static class ShellMover
{
    private const uint FO_MOVE = 0x0001;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_RENAMEONCOLLISION = 0x0008;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    private const int ErrorAccessDenied = 5;
    private const int DeAccessDeniedSource = 0x78;
    private const int MaxShellPath = 259; // SHFileOperation does not understand paths longer than MAX_PATH

    // The native struct is packed to 8 bytes on 64-bit Windows, which is what the default layout gives (the 32-bit
    // layout is packed to 1 and would need its own struct; the application is only used on 64-bit Windows)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>Moves the sources into the destination folder on a dedicated STA thread; the UI thread stays free while the shell works.</summary>
    public static Task<ShellMoveResult> MoveAsync(
        IReadOnlyList<string> sources, string destinationFolder, IntPtr ownerWindow, ShellMoveOptions options = ShellMoveOptions.None)
    {
        var completion = new TaskCompletionSource<ShellMoveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Move(sources, destinationFolder, ownerWindow, options));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "SpaceView shell move"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>The same operation on the calling thread, which must be a single-threaded apartment.</summary>
    public static ShellMoveResult Move(
        IReadOnlyList<string> sources, string destinationFolder, IntPtr ownerWindow, ShellMoveOptions options = ShellMoveOptions.None)
    {
        var existing = sources.Where(s => File.Exists(s) || Directory.Exists(s)).ToList();
        if (existing.Count == 0)
            return Evaluate(existing, aborted: false, resultCode: 0);

        if (existing.Any(s => s.Length > MaxShellPath) || destinationFolder.Length > MaxShellPath)
            throw new PathTooLongException("A path is too long for the Windows shell move (260 characters).");

        ushort flags = FOF_ALLOWUNDO;
        if ((options & ShellMoveOptions.Silent) != 0)
            flags |= FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI;
        if ((options & ShellMoveOptions.RenameOnCollision) != 0)
            flags |= FOF_RENAMEONCOLLISION;

        var op = new SHFILEOPSTRUCT
        {
            hwnd = ownerWindow,
            wFunc = FO_MOVE,
            // Both strings are lists that end with a double NUL; the marshaller adds the last NUL
            pFrom = string.Join('\0', existing) + '\0',
            pTo = destinationFolder + '\0',
            fFlags = flags,
            lpszProgressTitle = null
        };

        int code = SHFileOperationW(ref op);
        return Evaluate(existing, op.fAnyOperationsAborted, code);
    }

    private static ShellMoveResult Evaluate(IReadOnlyList<string> sources, bool aborted, int resultCode)
    {
        // The return code of a half-done operation is not reliable (a cancel is often reported as success): only the
        // file system says what happened
        var moved = new List<string>();
        var notMoved = new List<string>();
        foreach (string source in sources)
        {
            if (File.Exists(source) || Directory.Exists(source))
                notMoved.Add(source);
            else
                moved.Add(source);
        }

        return new ShellMoveResult
        {
            Moved = moved,
            NotMoved = notMoved,
            Aborted = aborted,
            ResultCode = resultCode,
            AccessDenied = resultCode is ErrorAccessDenied or DeAccessDeniedSource
        };
    }
}
