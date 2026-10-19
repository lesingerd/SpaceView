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

    public static List<DriveItemViewModel> GetReadyDrives()
    {
        var list = new List<DriveItemViewModel>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.IsReady)
                {
                    list.Add(new DriveItemViewModel(d));
                }
            }
        }
        catch
        {
            // Ignore enumeration errors
        }
        return list;
    }
}
