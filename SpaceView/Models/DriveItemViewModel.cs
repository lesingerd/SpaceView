using System.IO;
using System.Windows.Media;
using SpaceView.Core;
using Color = System.Windows.Media.Color;

namespace SpaceView.Models;

public sealed class DriveItemViewModel
{
    public DriveInfo DriveInfo { get; }
    public string Name { get; }
    public string VolumeLabel { get; }
    public string DriveFormat { get; }
    public long TotalSize { get; }
    public long FreeSpace { get; }
    public long UsedSpace { get; }
    public double UsedPercentage { get; }
    public double FreePercentage { get; }

    public string DisplayTitle { get; }
    public string UsageSummary { get; }
    public string TrayFreeSummary { get; }
    public string UsedVsTotalText { get; }
    public SolidColorBrush BarBrush { get; }

    public DriveItemViewModel(DriveInfo drive)
    {
        DriveInfo = drive;
        Name = drive.Name;

        string label = "Local Disk";
        string format = "Unknown";
        long total = 0;
        long free = 0;

        try
        {
            if (!string.IsNullOrWhiteSpace(drive.VolumeLabel))
                label = drive.VolumeLabel;
            format = drive.DriveFormat;
            total = drive.TotalSize;
            free = drive.AvailableFreeSpace;
        }
        catch
        {
            // Ignore partial drive metadata failures
        }

        VolumeLabel = label;
        DriveFormat = format;
        TotalSize = Math.Max(1, total);
        FreeSpace = Math.Clamp(free, 0, TotalSize);
        UsedSpace = Math.Max(0, TotalSize - FreeSpace);

        UsedPercentage = total > 0 ? Math.Clamp((double)UsedSpace / TotalSize * 100.0, 0.0, 100.0) : 0.0;
        FreePercentage = total > 0 ? Math.Clamp((double)FreeSpace / TotalSize * 100.0, 0.0, 100.0) : 0.0;

        DisplayTitle = $"{Name.TrimEnd('\\')}  [{VolumeLabel}]  •  {DriveFormat}";
        UsageSummary = $"{SizeFormatter.FormatBytes(UsedSpace)} used of {SizeFormatter.FormatBytes(TotalSize)} ({SizeFormatter.FormatBytes(FreeSpace)} free)";
        TrayFreeSummary = $"{SizeFormatter.FormatBytes(FreeSpace)} free ({FreePercentage:0.0}%)";
        UsedVsTotalText = $"{UsedPercentage:0.0}% used  •  {SizeFormatter.FormatBytes(FreeSpace)} / {SizeFormatter.FormatBytes(TotalSize)} free";

        Color color = UsedPercentage >= 90.0
            ? Color.FromRgb(239, 68, 68)   // Coral red when >= 90%
            : UsedPercentage >= 75.0
                ? Color.FromRgb(245, 158, 11) // Amber when >= 75%
                : Color.FromRgb(56, 189, 248); // Sky cyan-blue when healthy

        BarBrush = new SolidColorBrush(color);
        BarBrush.Freeze();
    }

    /// <summary>How long one enumeration waits for the drives to answer before reporting the slow ones as unresponsive.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private static readonly object EnumerationLock = new();
    private static Task<DriveEnumeration>? _enumeration;      // the one enumeration in flight (null or completed: none)
    private static readonly Dictionary<string, Task<DriveItemViewModel?>> Probes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DriveItemViewModel> LastGoodByName = new(StringComparer.OrdinalIgnoreCase);
    private static volatile DriveEnumeration _lastResult = new(new List<DriveItemViewModel>(), new List<string>());

    /// <summary>The result of the most recent enumeration that finished (empty before the first one), for callers that must not wait.</summary>
    public static DriveEnumeration LastResult => _lastResult;

    /// <summary>
    /// Lists the ready drives without ever blocking the caller on a drive that does not answer (a sleeping disk, a dead
    /// network share, an optical drive spinning up). Each drive is probed on its own background task, and only one
    /// enumeration runs at a time: callers that arrive meanwhile share it. A drive that has not answered within the
    /// timeout is reported in <see cref="DriveEnumeration.Unresponsive"/> (with its last known details if there are any, so
    /// it does not drop out of a list); its probe is not repeated until it returns, so a hung drive never piles up tasks.
    /// </summary>
    public static Task<DriveEnumeration> GetReadyDrivesAsync()
    {
        lock (EnumerationLock)
        {
            if (_enumeration == null || _enumeration.IsCompleted)
                _enumeration = Task.Run(EnumerateDrivesAsync);
            return _enumeration;
        }
    }

    private static async Task<DriveEnumeration> EnumerateDrivesAsync()
    {
        var probes = new List<(string Name, Task<DriveItemViewModel?> Probe)>();
        try
        {
            DriveInfo[] drives = DriveInfo.GetDrives();
            lock (EnumerationLock)
            {
                foreach (DriveInfo d in drives)
                {
                    // A probe still running from an earlier enumeration (a hung drive) is waited on again, not restarted
                    if (!Probes.TryGetValue(d.Name, out Task<DriveItemViewModel?>? probe) || probe.IsCompleted)
                    {
                        DriveInfo drive = d;
                        probe = Task.Run(() => ProbeDrive(drive));
                        Probes[d.Name] = probe;
                    }
                    probes.Add((d.Name, probe));
                }
            }
        }
        catch
        {
            // Ignore enumeration errors
        }

        Task all = Task.WhenAll(probes.Select(p => p.Probe));
        await Task.WhenAny(all, Task.Delay(ProbeTimeout)).ConfigureAwait(false);

        var ready = new List<DriveItemViewModel>();
        var unresponsive = new List<string>();
        lock (EnumerationLock)
        {
            foreach ((string name, Task<DriveItemViewModel?> probe) in probes)
            {
                if (probe.IsCompletedSuccessfully)
                {
                    if (probe.Result is { } item)
                    {
                        ready.Add(item);
                        LastGoodByName[name] = item;
                    }
                    else
                    {
                        LastGoodByName.Remove(name); // not ready (no media) or unreadable
                    }
                }
                else if (!probe.IsCompleted)
                {
                    unresponsive.Add(name);
                    if (LastGoodByName.TryGetValue(name, out DriveItemViewModel? lastGood))
                        ready.Add(lastGood);
                }
            }
        }

        var result = new DriveEnumeration(ready, unresponsive);
        _lastResult = result;
        return result;
    }

    private static DriveItemViewModel? ProbeDrive(DriveInfo drive)
    {
        try
        {
            return drive.IsReady ? new DriveItemViewModel(drive) : null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class DriveEnumeration
{
    /// <summary>Drives that are ready, plus the last known details of drives that did not answer in time.</summary>
    public IReadOnlyList<DriveItemViewModel> Drives { get; }
    /// <summary>Names of drives that did not answer in time.</summary>
    public IReadOnlyList<string> Unresponsive { get; }

    public DriveEnumeration(IReadOnlyList<DriveItemViewModel> drives, IReadOnlyList<string> unresponsive)
    {
        Drives = drives;
        Unresponsive = unresponsive;
    }
}
