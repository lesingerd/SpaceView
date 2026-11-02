using System.Collections;

namespace SpaceView.Core;

public enum SessionSelectOutcome
{
    /// <summary>The drive was not in the list; a new session was created at the top.</summary>
    Added,
    /// <summary>The drive was already in the list and was moved to the top (nothing is rescanned or rebuilt).</summary>
    MovedToTop,
    AlreadyFirst
}

/// <summary>
/// The ordered sessions of the tree: the most recently chosen drive is first. Pure bookkeeping (no scanning, no UI),
/// so the ordering rules can be tested on their own.
/// </summary>
public sealed class DriveSessionList : IReadOnlyList<DriveSession>
{
    private readonly List<DriveSession> _items = new();

    public int Count => _items.Count;
    public DriveSession this[int index] => _items[index];
    public IEnumerator<DriveSession> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public DriveSession? Find(string rootPath) =>
        _items.Find(s => DriveSession.SameRoot(s.RootPath, rootPath));

    public int IndexOf(DriveSession session) => _items.IndexOf(session);

    public IReadOnlyList<string> RootPaths => _items.Select(s => s.RootPath).ToList();

    /// <summary>
    /// A drive was chosen: adds it as the first session if it is not there, or moves its existing session to the top.
    /// <paramref name="create"/> is only called for a new drive.
    /// </summary>
    public SessionSelectOutcome SelectToTop(string rootPath, Func<string, DriveSession> create, out DriveSession session)
    {
        DriveSession? existing = Find(rootPath);
        if (existing == null)
        {
            session = create(DriveSession.NormalizeRoot(rootPath));
            _items.Insert(0, session);
            return SessionSelectOutcome.Added;
        }

        session = existing;
        int index = _items.IndexOf(existing);
        if (index == 0)
            return SessionSelectOutcome.AlreadyFirst;

        _items.RemoveAt(index);
        _items.Insert(0, existing);
        return SessionSelectOutcome.MovedToTop;
    }

    /// <summary>Adds a drive at the end (restoring a saved order); returns the existing session if the drive is already there.</summary>
    public DriveSession AddLast(string rootPath, Func<string, DriveSession> create, out bool added)
    {
        DriveSession? existing = Find(rootPath);
        added = existing == null;
        if (existing != null)
            return existing;

        DriveSession session = create(DriveSession.NormalizeRoot(rootPath));
        _items.Add(session);
        return session;
    }

    public bool Remove(DriveSession session) => _items.Remove(session);
}
