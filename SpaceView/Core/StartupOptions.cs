using System.Text.RegularExpressions;

namespace SpaceView.Core;

/// <summary>
/// Command-line arguments of SpaceView: <c>--drives C:\;E:\</c> (the drives to scan, first = most recently chosen) and
/// <c>--parent-pid 1234</c> (the instance that started this one and is about to exit). Written by
/// <see cref="ElevationHelper"/> when it relaunches the app elevated.
/// </summary>
public sealed class StartupOptions
{
    private static readonly Regex DriveRootPattern = new(@"^[A-Za-z]:\\?$", RegexOptions.Compiled);

    public static StartupOptions Empty { get; } = new(Array.Empty<string>(), null);

    /// <summary>Drive roots such as <c>E:\</c>, in the order given, without duplicates. Anything that is not a drive root is dropped.</summary>
    public IReadOnlyList<string> Drives { get; }
    public int? ParentProcessId { get; }

    public StartupOptions(IReadOnlyList<string> drives, int? parentProcessId)
    {
        Drives = drives;
        ParentProcessId = parentProcessId;
    }

    public static StartupOptions Parse(string[] args)
    {
        var drives = new List<string>();
        int? parentPid = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? value = null;

            if (TrySplit(arg, "--drives", out value) || TrySplit(arg, "--parent-pid", out value))
            {
                // "--name value" form: the value is the next argument
                if (value == null && i + 1 < args.Length)
                    value = args[++i];
            }
            else
            {
                continue;
            }

            if (arg.StartsWith("--drives", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string part in (value ?? string.Empty).Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string cleaned = part.Trim('"');
                    if (!DriveRootPattern.IsMatch(cleaned))
                        continue; // only whole drives are ever scanned from the command line

                    string root = char.ToUpperInvariant(cleaned[0]) + ":\\";
                    if (!drives.Contains(root, StringComparer.OrdinalIgnoreCase))
                        drives.Add(root);
                }
            }
            else if (int.TryParse(value, out int pid) && pid > 0)
            {
                parentPid = pid;
            }
        }

        return new StartupOptions(drives, parentPid);
    }

    /// <summary>Matches "--name" (value null: it is in the next argument) and "--name=value".</summary>
    private static bool TrySplit(string arg, string name, out string? value)
    {
        value = null;
        if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            return true;

        if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = arg.Substring(name.Length + 1);
            return true;
        }
        return false;
    }
}
