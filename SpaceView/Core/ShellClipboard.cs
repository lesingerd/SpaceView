using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;

namespace SpaceView.Core;

/// <summary>
/// The Windows clipboard conventions for cut and paste of files: a file drop list plus the "Preferred DropEffect"
/// format, whose value says what a paste should do (2 = move, as Explorer writes for Cut; 5 = copy). Every call must be
/// made on a single-threaded apartment thread.
/// </summary>
public static class ShellClipboard
{
    public const int DropEffectNone = 0;
    public const int DropEffectCopy = 1;
    public const int DropEffectMove = 2;
    public const int DropEffectLink = 4;

    public const string PreferredDropEffectFormat = "Preferred DropEffect";
    public const int WmClipboardUpdate = 0x031D;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    /// <summary>Changes with every change of the clipboard's content: a stored value that no longer matches means somebody else replaced it.</summary>
    public static uint SequenceNumber => GetClipboardSequenceNumber();

    /// <summary>Asks Windows to post WM_CLIPBOARDUPDATE to the window whenever the clipboard changes.</summary>
    public static bool AddListener(IntPtr hwnd) => AddClipboardFormatListener(hwnd);

    public static void RemoveListener(IntPtr hwnd) => RemoveClipboardFormatListener(hwnd);

    /// <summary>
    /// Puts the paths on the clipboard as a file drop list with the given preferred drop effect, so that Explorer
    /// pastes them accordingly. Returns the clipboard's sequence number right after the change, or null if the
    /// clipboard could not be written (another program holds it).
    /// </summary>
    public static uint? TrySetFileDrop(IEnumerable<string> paths, int dropEffect)
    {
        try
        {
            var list = new StringCollection();
            foreach (string path in paths)
                list.Add(path);

            var data = new System.Windows.DataObject();
            data.SetFileDropList(list);
            data.SetData(PreferredDropEffectFormat, new MemoryStream(BitConverter.GetBytes(dropEffect)));
            System.Windows.Clipboard.SetDataObject(data, copy: true);
            return SequenceNumber;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a file drop list and its preferred drop effect (-1 if the content has none). Returns false if the
    /// clipboard holds no files or cannot be read.
    /// </summary>
    public static bool TryGetFileDrop(out List<string> paths, out int dropEffect)
    {
        paths = new List<string>();
        dropEffect = -1;

        // Another program (a clipboard manager, Explorer) may hold the clipboard for a moment: try a few times
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                paths.Clear();
                dropEffect = -1;
                if (!System.Windows.Clipboard.ContainsFileDropList())
                    return false;

                foreach (string? path in System.Windows.Clipboard.GetFileDropList())
                {
                    if (!string.IsNullOrEmpty(path))
                        paths.Add(path);
                }

                if (System.Windows.Clipboard.GetData(PreferredDropEffectFormat) is MemoryStream stream && stream.Length >= 4)
                {
                    var buffer = new byte[4];
                    stream.Position = 0;
                    if (stream.Read(buffer, 0, 4) == 4)
                        dropEffect = BitConverter.ToInt32(buffer, 0);
                }
                return paths.Count > 0;
            }
            catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
            {
                Thread.Sleep(40);
            }
        }

        paths.Clear();
        dropEffect = -1;
        return false;
    }

    /// <summary>True if the effect asks for a move and not also for a copy.</summary>
    public static bool IsMove(int dropEffect) => dropEffect >= 0 && (dropEffect & DropEffectMove) != 0 && (dropEffect & DropEffectCopy) == 0;

    public static void TryClear()
    {
        try
        {
            System.Windows.Clipboard.Clear();
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
        {
            // Held by another program; the content stays
        }
    }
}
