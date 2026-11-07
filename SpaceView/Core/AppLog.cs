using System.IO;

namespace SpaceView.Core;

/// <summary>
/// A tiny thread-safe rolling log: <c>%LocalAppData%\SpaceView\spaceview.log</c>, capped at <see cref="MaxBytes"/> with one
/// <c>.1</c> backup. Meant for a few lines per event of interest, never per file event. Writing never throws.
/// </summary>
public static class AppLog
{
    public const long MaxBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _path;
    private static long _length = -1; // -1: not measured yet
    private static readonly int ProcessId = Environment.ProcessId;

    /// <summary>The log file; tests may point it elsewhere before the first write.</summary>
    public static string Path
    {
        get
        {
            lock (Gate)
                return _path ??= DefaultPath();
        }
        set
        {
            lock (Gate)
            {
                _path = value;
                _length = -1;
            }
        }
    }

    private static string DefaultPath()
    {
        // Lets test runs keep their lines out of the real log
        string? overridePath = Environment.GetEnvironmentVariable("SPACEVIEW_LOG");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;

        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceView");
        return System.IO.Path.Combine(dir, "spaceview.log");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{ProcessId}] {level} {message}{Environment.NewLine}";
            lock (Gate)
            {
                string path = _path ??= DefaultPath();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

                if (_length < 0)
                    _length = File.Exists(path) ? new FileInfo(path).Length : 0;

                if (_length >= MaxBytes)
                {
                    string backup = path + ".1";
                    if (File.Exists(backup))
                        File.Delete(backup);
                    File.Move(path, backup);
                    _length = 0;
                }

                File.AppendAllText(path, line);
                _length += line.Length; // close enough: the cap is approximate
            }
        }
        catch
        {
            // Logging must never take the app down
        }
    }
}
