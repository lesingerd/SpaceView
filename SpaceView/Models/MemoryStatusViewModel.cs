using System.Runtime.InteropServices;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using SpaceView.Core;
using SpaceView.Native;

namespace SpaceView.Models;

/// <summary>System memory figures (physical memory and commit charge) prepared for the tray popup.</summary>
public sealed class MemoryStatusViewModel
{
    public long TotalBytes { get; }
    public long AvailableBytes { get; }
    public long CacheBytes { get; }
    public long InUseBytes { get; }
    public long FreeBytes { get; }
    public long CommitTotalBytes { get; }
    public long CommitLimitBytes { get; }
    public long CommitPeakBytes { get; }

    /// <summary>Memory in use (total - available) as a percentage of the total, clamped to 0-100.</summary>
    public double InUsePercentage { get; }
    /// <summary>In use plus cached, as a percentage of the total, clamped to 0-100 (the muted underlay of the bar).</summary>
    public double InUseAndCachedPercentage { get; }
    public double AvailablePercentage { get; }
    public double CommitPercentage { get; }
    public double CommitPeakPercentage { get; }

    public string HeaderText { get; }
    public string AvailableSummary { get; }
    public string InUseText { get; }
    public string CachedText { get; }
    public string FreeText { get; }
    public string CommittedText { get; }
    public string CommitLimitText { get; }
    public string PeakText { get; }

    public SolidColorBrush UsedBrush { get; }
    public SolidColorBrush CachedBrush { get; }
    public SolidColorBrush CommitBrush { get; }

    public MemoryStatusViewModel(long total, long available, long cache, long commitTotal, long commitLimit, long commitPeak)
    {
        TotalBytes = Math.Max(0, total);
        AvailableBytes = Math.Clamp(available, 0, TotalBytes);
        InUseBytes = TotalBytes - AvailableBytes;
        CacheBytes = Math.Clamp(cache, 0, AvailableBytes);
        FreeBytes = Math.Max(0, AvailableBytes - CacheBytes);
        CommitTotalBytes = Math.Max(0, commitTotal);
        CommitLimitBytes = Math.Max(0, commitLimit);
        CommitPeakBytes = Math.Max(0, commitPeak);

        InUsePercentage = ToPercentage(InUseBytes, TotalBytes);
        InUseAndCachedPercentage = ToPercentage(InUseBytes + CacheBytes, TotalBytes);
        AvailablePercentage = ToPercentage(AvailableBytes, TotalBytes);
        CommitPercentage = ToPercentage(CommitTotalBytes, CommitLimitBytes);
        CommitPeakPercentage = ToPercentage(CommitPeakBytes, CommitLimitBytes);

        HeaderText = $"Physical memory  •  {SizeFormatter.FormatBytes(TotalBytes)}";
        AvailableSummary = $"{SizeFormatter.FormatBytes(AvailableBytes)} available ({AvailablePercentage:0.0}%)";
        InUseText = $"In use {SizeFormatter.FormatBytes(InUseBytes)}";
        CachedText = $"Cached {SizeFormatter.FormatBytes(CacheBytes)}";
        FreeText = $"Free {SizeFormatter.FormatBytes(FreeBytes)}";
        CommittedText = $"Committed {SizeFormatter.FormatBytes(CommitTotalBytes)}";
        CommitLimitText = $"/ {SizeFormatter.FormatBytes(CommitLimitBytes)}";
        PeakText = $"Peak {SizeFormatter.FormatBytes(CommitPeakBytes)}";

        UsedBrush = Frozen(ThresholdColor(InUsePercentage));
        CommitBrush = Frozen(ThresholdColor(CommitPercentage));
        CachedBrush = Frozen(Color.FromRgb(71, 85, 105)); // #475569
    }

    private static double ToPercentage(long value, long whole) =>
        whole > 0 ? Math.Clamp((double)value / whole * 100.0, 0.0, 100.0) : 0.0;

    private static Color ThresholdColor(double percent) =>
        percent >= 90.0 ? Color.FromRgb(239, 68, 68)
        : percent >= 75.0 ? Color.FromRgb(245, 158, 11)
        : Color.FromRgb(56, 189, 248);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Reads the current figures from the system; null when the call fails.</summary>
    public static MemoryStatusViewModel? TryRead()
    {
        try
        {
            var info = new NtfsNative.PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<NtfsNative.PERFORMANCE_INFORMATION>() };
            if (!NtfsNative.GetPerformanceInfo(out info, info.cb))
                return null;

            ulong page = info.PageSize.ToUInt64();
            if (page == 0)
                return null;

            return new MemoryStatusViewModel(
                ToBytes(info.PhysicalTotal, page),
                ToBytes(info.PhysicalAvailable, page),
                ToBytes(info.SystemCache, page),
                ToBytes(info.CommitTotal, page),
                ToBytes(info.CommitLimit, page),
                ToBytes(info.CommitPeak, page));
        }
        catch
        {
            return null;
        }
    }

    private static long ToBytes(UIntPtr pages, ulong pageSize)
    {
        ulong bytes = pages.ToUInt64() * pageSize;
        return bytes > long.MaxValue ? long.MaxValue : (long)bytes;
    }
}
