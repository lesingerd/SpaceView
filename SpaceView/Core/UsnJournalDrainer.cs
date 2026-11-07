using System.IO;
using Microsoft.Win32.SafeHandles;
using SpaceView.Native;

namespace SpaceView.Core;

/// <summary>Where the drain gets journal records from: a volume's change journal, or a synthetic source in tests.</summary>
internal interface IUsnRecordSource : IDisposable
{
    /// <summary>The journal the source reads (changes when the position was lost and the source re-synced to the tail).</summary>
    ulong JournalId { get; }

    /// <summary>
    /// Reads the records that follow <paramref name="nextUsn"/> into <paramref name="into"/> and advances the position.
    /// Returns false, with a message, when the position can no longer be read (the journal wrapped or was reset);
    /// <paramref name="nextUsn"/> and <see cref="JournalId"/> then point at the current tail. A failure that may pass
    /// returns true without moving the position.
    /// </summary>
    bool TryRead(ref long nextUsn, UsnBatch into, out string? lostMessage);
}

/// <summary>Reads the change journal of a volume through a handle of its own.</summary>
internal sealed class VolumeUsnRecordSource : IUsnRecordSource
{
    private readonly SafeFileHandle _volume;
    private IntPtr _buffer;
    private ulong _journalId;

    private VolumeUsnRecordSource(SafeFileHandle volume, ulong journalId)
    {
        _volume = volume;
        _journalId = journalId;
        _buffer = UsnJournalReader.AllocateAlignedBuffer();
    }

    public ulong JournalId => _journalId;

    /// <summary>Null when the volume cannot be opened (for example without administrator rights).</summary>
    public static VolumeUsnRecordSource? TryOpen(string rootPath, ulong journalId)
    {
        SafeFileHandle volume = NtfsNative.CreateFileW(
            @"\\.\" + rootPath.TrimEnd('\\'),
            NtfsNative.GENERIC_READ,
            NtfsNative.FILE_SHARE_ALL,
            IntPtr.Zero,
            NtfsNative.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (volume.IsInvalid)
        {
            volume.Dispose();
            return null;
        }
        return new VolumeUsnRecordSource(volume, journalId);
    }

    public bool TryRead(ref long nextUsn, UsnBatch into, out string? lostMessage)
    {
        lostMessage = null;
        if (UsnJournalReader.ReadChunk(_volume, _buffer, _journalId, ref nextUsn, into, out int error) != UsnChunkResult.Failed)
            return true;

        if (UsnJournalReader.IsPositionLost(_volume, error, ref _journalId, ref nextUsn, out string message))
        {
            lostMessage = message;
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        _volume.Dispose();
        UsnJournalReader.FreeAlignedBuffer(_buffer);
        _buffer = IntPtr.Zero;
    }
}

/// <summary>
/// What the drain collected while a scan ran, handed to the monitor of the new tree: the changes to apply first
/// and the journal position to continue from. <see cref="FailureReason"/> is set when changes were lost (the buffer
/// overflowed, or the journal wrapped); the tree is then stale until rescanned.
/// </summary>
internal sealed class UsnJournalBacklog
{
    public required UsnBatch Batch { get; init; }
    public required ulong JournalId { get; init; }
    public required long NextUsn { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>
/// Reads the change journal in the background while a volume is scanned. The scan of a big, busy volume can outlast
/// a small journal; reading it as it grows, and keeping the records in memory (coalesced per FRN, in USN order, as
/// the monitor's batches are), means nothing is lost when the journal wraps meanwhile. When the scan is done,
/// <see cref="Complete"/> hands the records and the position to the new monitor. Records the scan already reflects
/// are harmless to apply: updates are keyed by FRN, sizes are read fresh, and a delete only removes a node whose
/// FRN (or name) still matches.
/// </summary>
internal sealed class UsnJournalDrainer : IDisposable
{
    /// <summary>Default cap on the buffered records (estimated memory); beyond it the drain gives up.</summary>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    private readonly IUsnRecordSource _source;
    private readonly long _maxBytes;
    private readonly int _pollMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateLock = new();
    private UsnBatch _batch = new();
    private Thread? _thread;
    private ulong _journalId;
    private long _nextUsn;
    private string? _failure;
    private bool _completed;
    private bool _disposed;
    private bool _cleanedUp;

    public UsnJournalDrainer(IUsnRecordSource source, ulong journalId, long startUsn, long maxBytes = DefaultMaxBytes, int pollMs = 500)
    {
        _source = source;
        _journalId = journalId;
        _nextUsn = startUsn;
        _maxBytes = maxBytes;
        _pollMs = pollMs;
    }

    /// <summary>Opens the volume's journal and starts draining; null when the volume cannot be read (the monitor then works as before).</summary>
    public static UsnJournalDrainer? StartForVolume(string rootPath, ulong journalId, long startUsn)
    {
        VolumeUsnRecordSource? source = VolumeUsnRecordSource.TryOpen(rootPath, journalId);
        if (source == null)
            return null;

        var drainer = new UsnJournalDrainer(source, journalId, startUsn);
        drainer.Start();
        return drainer;
    }

    public bool IsRunning => _thread?.IsAlive == true;

    /// <summary>Why changes were lost, or null while the drain is intact.</summary>
    public string? FailureReason
    {
        get { lock (_stateLock) return _failure; }
    }

    /// <summary>Distinct files with buffered changes.</summary>
    public int BufferedCount
    {
        get { lock (_stateLock) return _batch.Items.Count; }
    }

    public void Start()
    {
        // A normal-priority thread of its own: reading the journal is cheap, and it must not wait behind the
        // background-mode scan workers
        _thread = new Thread(Run) { IsBackground = true, Name = "SpaceView journal drain" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            while (!_cts.IsCancellationRequested && FailureReason == null)
            {
                DrainAvailable(honourCancel: true);
                _cts.Token.WaitHandle.WaitOne(_pollMs);
            }
        }
        catch (Exception ex)
        {
            Fail($"The change journal could not be read during the scan ({ex.Message})");
        }
    }

    /// <summary>Reads until the journal has nothing new. A failure ends the drain: the buffer is dropped and the volume handle released.</summary>
    private void DrainAvailable(bool honourCancel)
    {
        while (!(honourCancel && _cts.IsCancellationRequested))
        {
            lock (_stateLock)
            {
                if (_failure != null || _disposed)
                    return;
            }

            long before = _nextUsn;
            bool ok = _source.TryRead(ref _nextUsn, _batch, out string? lost);
            lock (_stateLock)
            {
                _journalId = _source.JournalId;
            }

            if (!ok)
            {
                Fail($"{lost} while the scan was running, so changes made meanwhile were lost");
                return;
            }
            if (_batch.EstimatedBytes > _maxBytes)
            {
                Fail($"More than {_maxBytes / (1024 * 1024):N0} MB of changes were made during the scan, too many to keep");
                return;
            }
            if (_nextUsn <= before)
                return;
        }
    }

    private void Fail(string reason)
    {
        lock (_stateLock)
        {
            if (_failure != null)
                return;
            _failure = reason;
            _batch = new UsnBatch(); // release the memory
            DisposeSource(); // and the volume handle
        }
    }

    /// <summary>
    /// Stops the drain after one last read and returns what it collected. Call once, when the scan has finished.
    /// The drain is finished with afterwards; its volume handle is released.
    /// </summary>
    public UsnJournalBacklog Complete()
    {
        if (_cleanedUp)
            throw new ObjectDisposedException(nameof(UsnJournalDrainer));

        StopThread();
        try
        {
            if (FailureReason == null)
                DrainAvailable(honourCancel: false);
        }
        catch (Exception ex)
        {
            Fail($"The change journal could not be read during the scan ({ex.Message})");
        }

        lock (_stateLock)
        {
            _completed = true;
            var backlog = new UsnJournalBacklog
            {
                Batch = _batch,
                JournalId = _journalId,
                NextUsn = _nextUsn,
                FailureReason = _failure
            };
            _batch = new UsnBatch();
            DisposeSource();
            return backlog;
        }
    }

    private void StopThread()
    {
        _cts.Cancel();
        Thread? thread = _thread;
        if (thread != null && thread != Thread.CurrentThread)
            thread.Join();
    }

    private void DisposeSource()
    {
        if (_disposed)
            return;
        _disposed = true;
        _source.Dispose();
    }

    /// <summary>Stops the drain (the scan was cancelled or failed) and releases the volume handle and the buffer.</summary>
    public void Dispose()
    {
        if (_cleanedUp)
            return;
        _cleanedUp = true;

        StopThread();
        lock (_stateLock)
        {
            if (!_completed)
                _batch = new UsnBatch();
            DisposeSource();
        }
        _cts.Dispose();
    }
}
