namespace SpaceView.Core;

public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024.0 && unitIndex < Units.Length - 1)
        {
            value /= 1024.0;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes:N0} B"
            : $"{value:0.00} {Units[unitIndex]}";
    }

    /// <summary>"4.52s" below a minute, then "24m 49s" or "1h 02m 05s".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 60)
            return $"{duration.TotalSeconds:0.00}s";

        int total = (int)Math.Round(duration.TotalSeconds);
        int hours = total / 3600;
        int minutes = total % 3600 / 60;
        int seconds = total % 60;
        return hours > 0 ? $"{hours}h {minutes:00}m {seconds:00}s" : $"{minutes}m {seconds:00}s";
    }
}
