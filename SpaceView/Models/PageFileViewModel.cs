using System.Management;
using SpaceView.Core;

namespace SpaceView.Models;

/// <summary>One Win32_PageFileUsage instance (values reported by WMI in MB), prepared for the tray popup.</summary>
public sealed class PageFileViewModel
{
    public string Name { get; }
    public long CurrentUsageMb { get; }
    public long PeakUsageMb { get; }
    public long AllocatedMb { get; }

    /// <summary>Current usage as a percentage of the allocated size (0 when nothing is allocated), clamped to 0-100.</summary>
    public double CurrentPercentage { get; }
    /// <summary>Peak usage as a percentage of the allocated size (0 when nothing is allocated), clamped to 0-100.</summary>
    public double PeakPercentage { get; }

    public string SummaryText { get; }

    public PageFileViewModel(string name, long currentMb, long peakMb, long allocatedMb)
    {
        Name = name;
        CurrentUsageMb = Math.Max(0, currentMb);
        PeakUsageMb = Math.Max(0, peakMb);
        AllocatedMb = Math.Max(0, allocatedMb);

        CurrentPercentage = ToPercentage(CurrentUsageMb, AllocatedMb);
        PeakPercentage = ToPercentage(PeakUsageMb, AllocatedMb);

        SummaryText = $"Current {FormatMb(CurrentUsageMb)}  •  Peak {FormatMb(PeakUsageMb)}  •  Allocated {FormatMb(AllocatedMb)}";
    }

    private static double ToPercentage(long valueMb, long allocatedMb) =>
        allocatedMb > 0 ? Math.Clamp((double)valueMb / allocatedMb * 100.0, 0.0, 100.0) : 0.0;

    private static string FormatMb(long mb) => SizeFormatter.FormatBytes(mb * 1024L * 1024L);

    /// <summary>How often the page files are queried in the background (settable for tests).</summary>
    internal static TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    /// <summary>How long one query may take before it is abandoned (settable for tests).</summary>
    internal static TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);
    /// <summary>The query itself (replaced by tests to fake a hung WMI).</summary>
    internal static Func<List<PageFileViewModel>> QuerySource = ReadPageFiles;

    private static readonly object TimerLock = new();
    private static System.Threading.Timer? _timer;
    private static bool _stopped;
    // The query that outlived its timeout (a hung WMI service) and has not finished: no new query starts until it does
    private static Task<List<PageFileViewModel>>? _inFlight;
    private static bool _hungLogged;
    private static volatile IReadOnlyList<PageFileViewModel> _lastResult = Array.Empty<PageFileViewModel>();

    /// <summary>The result of the most recent query that succeeded (empty until the first one), for the popup, which never waits.</summary>
    public static IReadOnlyList<PageFileViewModel> LastResult => _lastResult;

    /// <summary>
    /// Starts the background refresh: a first query right away, then one every <see cref="RefreshInterval"/>, independent of
    /// the popup. The timer is one-shot and re-armed when a query ends, so at most one query is ever in flight.
    /// </summary>
    public static void StartBackgroundRefresh()
    {
        lock (TimerLock)
        {
            if (_timer != null || _stopped)
                return;
            _timer = new System.Threading.Timer(_ => RunQuery(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stops the background refresh and releases the timer (call at shutdown).</summary>
    public static void StopBackgroundRefresh()
    {
        lock (TimerLock)
        {
            _stopped = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    internal static void RunQuery()
    {
        try
        {
            // The WMI call can hang on a broken WMI service. A query that is still running from an earlier cycle is not
            // piled on (each hung call would hold a thread-pool thread and a WMI connection for good): skip the cycle
            // and look again at the next one. One that finished late is collected first.
            Task<List<PageFileViewModel>>? pending = _inFlight;
            if (pending != null)
            {
                if (!pending.IsCompleted)
                {
                    if (!_hungLogged)
                    {
                        _hungLogged = true;
                        AppLog.Warn("The page file query (WMI) has not answered for a while; further queries are skipped until it does");
                    }
                    return;
                }

                _inFlight = null;
                if (_hungLogged)
                {
                    _hungLogged = false;
                    AppLog.Info("The page file query (WMI) answered again");
                }
                if (pending.IsCompletedSuccessfully)
                    _lastResult = pending.Result;
                else
                    _ = pending.Exception; // observed
            }

            // Wait for the answer only up to the timeout (the query carries its own as well)
            Task<List<PageFileViewModel>> read = Task.Run(QuerySource);
            _inFlight = read;
            if (read.Wait(QueryTimeout))
            {
                _inFlight = null;
                _lastResult = read.Result;
            }
        }
        catch
        {
            // Keep the previous result (a faulted query stays in _inFlight and is observed by the next cycle)
        }
        finally
        {
            lock (TimerLock)
                _timer?.Change(RefreshInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private static List<PageFileViewModel> ReadPageFiles()
    {
        var list = new List<PageFileViewModel>();
        var options = new EnumerationOptions { Timeout = QueryTimeout, ReturnImmediately = true };
        using var searcher = new ManagementObjectSearcher(
            new ManagementScope(@"\\.\root\cimv2"),
            new ObjectQuery("SELECT Name, CurrentUsage, PeakUsage, AllocatedBaseSize FROM Win32_PageFileUsage"),
            options);
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementBaseObject o in results)
        {
            using (o)
            {
                string name = o["Name"] as string ?? "Page file";
                list.Add(new PageFileViewModel(name, ToLong(o["CurrentUsage"]), ToLong(o["PeakUsage"]), ToLong(o["AllocatedBaseSize"])));
            }
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static long ToLong(object? value)
    {
        try { return value == null ? 0 : Convert.ToInt64(value); }
        catch { return 0; }
    }
}
