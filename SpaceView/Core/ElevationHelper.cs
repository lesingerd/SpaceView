using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;

namespace SpaceView.Core;

public enum RelaunchOutcome
{
    /// <summary>The elevated instance was started; the caller should exit now.</summary>
    Started,
    /// <summary>The user declined the Windows (UAC) prompt; keep running as before.</summary>
    Declined,
    Failed
}

/// <summary>
/// Gets SpaceView elevated. A running process cannot raise its own rights, so this starts a new, elevated instance
/// (ShellExecute verb "runas") and the caller exits. The app manifest stays asInvoker: nothing asks for elevation at
/// launch, only the user's own action does. In-memory scan results do not survive the relaunch; the new instance scans
/// the same drives again (see <see cref="StartupOptions"/>).
/// </summary>
public static class ElevationHelper
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the user declined the UAC prompt
    private const int ErrorAccessDenied = 5;
    private const int EAccessDenied = unchecked((int)0x80070005);

    private static readonly Lazy<bool> ElevatedValue = new(QueryElevated);

    /// <summary>True when this process runs with administrator rights.</summary>
    public static bool IsElevated => ElevatedValue.Value;

    private static bool QueryElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The text for a prompt that offers the relaunch; says what is lost, as the user must know before agreeing.</summary>
    public static string BuildPromptText(string reason, IReadOnlyCollection<string> roots)
    {
        string drives = roots.Count > 0 ? string.Join(", ", roots) : "no drives";
        return $"{reason}\n\n" +
               "SpaceView can restart with administrator rights. The scan results held in memory are discarded: " +
               $"the new window scans {drives} again, one drive at a time, and expanded folders and the selection are not kept. " +
               "Windows will ask you to confirm.\n\nRestart as administrator?";
    }

    /// <summary>The command line for the new instance: the drives in tree order and the pid of the instance that is about to exit.</summary>
    public static string BuildRelaunchArguments(IEnumerable<string> roots, int parentProcessId)
    {
        // Drive roots never contain spaces, so no quoting is needed (and a quoted trailing backslash would escape the quote)
        string drives = string.Join(';', roots.Select(DriveSession.NormalizeRoot));
        return drives.Length > 0
            ? $"--drives {drives} --parent-pid {parentProcessId}"
            : $"--parent-pid {parentProcessId}";
    }

    /// <summary>
    /// Starts an elevated copy of this program that scans <paramref name="roots"/> (in this order). Shows the Windows
    /// consent prompt. Does not exit the current process: the caller does that after <see cref="RelaunchOutcome.Started"/>,
    /// once its tray icon and monitors are released. There is no single-instance mutex in SpaceView; the new instance
    /// waits for the old one to exit (see <see cref="WaitForProcessExitAsync"/>) before it scans.
    /// </summary>
    public static RelaunchOutcome TryRelaunchElevated(IEnumerable<string> roots, out string? error)
    {
        error = null;
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                error = "The path of the program is unknown.";
                return RelaunchOutcome.Failed;
            }

            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = BuildRelaunchArguments(roots, Environment.ProcessId),
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
            };
            using Process? started = Process.Start(startInfo);
            return RelaunchOutcome.Started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return RelaunchOutcome.Declined;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return RelaunchOutcome.Failed;
        }
    }

    /// <summary>True for the errors that an elevated run could avoid.</summary>
    public static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException or SecurityException ||
        (ex is Win32Exception w && w.NativeErrorCode == ErrorAccessDenied) ||
        (ex is IOException io && io.HResult == EAccessDenied);

    /// <summary>Waits (up to <paramref name="timeout"/>) for the instance that started this one to exit; returns at once if it is gone.</summary>
    public static Task WaitForProcessExitAsync(int processId, TimeSpan timeout) =>
        Task.Run(() =>
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                process.WaitForExit(timeout);
            }
            catch
            {
                // Not running (or not accessible): nothing to wait for
            }
        });

    // The shield icon Windows puts on buttons that need elevation
    private const int SiidShield = 77;
    private const uint ShgsiIcon = 0x100;
    private const uint ShgsiSmallIcon = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShStockIconInfo
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetStockIconInfo(int siid, uint flags, ref ShStockIconInfo info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>A small shield icon handle (null if unavailable); the caller copies it and calls <see cref="DestroyIconHandle"/>.</summary>
    public static IntPtr? GetShieldIconHandle()
    {
        try
        {
            var info = new ShStockIconInfo { cbSize = (uint)Marshal.SizeOf<ShStockIconInfo>(), szPath = string.Empty };
            return SHGetStockIconInfo(SiidShield, ShgsiIcon | ShgsiSmallIcon, ref info) == 0 && info.hIcon != IntPtr.Zero
                ? info.hIcon
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void DestroyIconHandle(IntPtr handle) => DestroyIcon(handle);
}
