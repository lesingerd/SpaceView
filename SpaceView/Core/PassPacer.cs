namespace SpaceView.Core;

/// <summary>
/// Spaces the live-update passes of a drive so the UI thread always gets idle time: after a pass that took d the next one
/// starts no sooner than <see cref="GapFactor"/> times d later (at least <see cref="MinGapMs"/>, at most <see cref="MaxGapMs"/>).
/// </summary>
public static class PassPacer
{
    public const int GapFactor = 3;
    public const int MinGapMs = 20;
    public const int MaxGapMs = 3000;

    /// <summary>The gap that must follow a pass of the given duration.</summary>
    public static long GapMs(long lastDurationMs) => Math.Clamp(lastDurationMs * GapFactor, MinGapMs, MaxGapMs);

    /// <summary>How long to wait from <paramref name="nowMs"/> before the next pass (0: run now).</summary>
    public static int DelayMs(long nowMs, long lastEndMs, long lastDurationMs)
    {
        if (lastEndMs <= 0)
            return 0;
        long wait = lastEndMs + GapMs(lastDurationMs) - nowMs;
        return wait <= 0 ? 0 : (int)Math.Min(wait, MaxGapMs);
    }
}
