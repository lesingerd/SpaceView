using System.Runtime.InteropServices;

namespace SpaceView.Core;

/// <summary>What <see cref="LiveRowSync{TRow}"/> needs from a tree row. Plain members only, so the sync has no UI types.</summary>
public interface ILiveRow
{
    /// <summary>The node id, or -1 for a "more items" row.</summary>
    int NodeId { get; }
    int ParentNodeId { get; }
    int Depth { get; }
    bool IsMoreRow { get; }
    bool IsDirectory { get; }
    bool IsExpanded { get; }
    bool IsLoadingChildren { get; }
    string Name { get; }
    /// <summary>The size the row shows now (it may be behind the store).</summary>
    long Size { get; }

    /// <summary>Updates the count shown by a "more items" row.</summary>
    void SetRemaining(int remaining);
    /// <summary>Takes size, counts and the percentage of the parent from the store's numbers.</summary>
    void ApplyValues(in NodeRowValues values);
    /// <summary>Takes everything, the name included.</summary>
    void ApplySnapshot(in NodeSnapshot snapshot);
}

/// <summary>The list of rows being synced: the few list operations the sync uses, so a test can use a plain list.</summary>
public interface IRowList<TRow>
{
    int Count { get; }
    TRow this[int index] { get; }
    void Insert(int index, TRow row);
    void RemoveAt(int index);
    void RemoveRange(int index, int count);
    /// <summary>Replaces <paramref name="removeCount"/> rows at <paramref name="index"/> with <paramref name="newItems"/> in one step (one change notification).</summary>
    void ReplaceRange(int index, int removeCount, IReadOnlyList<TRow> newItems);
}

/// <summary>
/// Brings the visible rows of one drive in line with the store after live changes, in time linear in the rows (not
/// quadratic): rows of deleted or moved nodes are removed, new children of expanded folders are inserted at their sorted
/// place among the visible siblings, each folder's "more items" row keeps its count, and the displayed numbers are
/// refreshed. With a set of changed nodes only the rows that can have changed are looked at (the changed nodes, their
/// ancestors, and the children of those, whose percentages depend on them) and a folder's children are never listed or
/// sorted; without one ("full") every row and every expanded folder is checked.
/// <para>
/// After the numbers are refreshed the visible children of each such folder are checked against the order (largest first,
/// equal sizes by node id) in one linear scan, since a size that changed live can put a row out of place. A folder is only
/// re-ordered when some adjacent pair is out of order by more than <c>min(1% of the larger size, 1 MB)</c> (so rows of
/// near-equal size do not swap back and forth on every pass); it is then sorted exactly. Each child moves together with
/// its block of visible descendant rows (the existing row objects are moved, none is recreated), the folder's "more items"
/// row stays last, and all re-ordered folders of the pass are applied with one <see cref="IRowList{TRow}.ReplaceRange"/>
/// over the span that changed. A folder with hidden children (a "more items" row) is re-sorted among its visible children
/// only: a visible child that now ranks below a hidden one stays in the page, in its sorted place among the visible
/// siblings, until the folder is expanded again. The drive's root row is not a sibling of anything here, so the order of
/// the drives is never touched. The window can pass allowReorder false (a menu or dialog is open); the need is then
/// reported through needsFullNext.
/// </para>
/// Holds its scratch collections between calls, so a pass allocates little. Single threaded (UI thread).
/// </summary>
public sealed class LiveRowSync<TRow> where TRow : class, ILiveRow
{
    private struct Cand
    {
        public int Parent;
        public int Id;
        public long Size;
    }

    private struct Edit
    {
        public int Index;
        public int Kind; // 0 remove range, 1 remove one (a "more items" row), 2 insert
        public int Seq;
        // Inserts at the same index end up in ascending Order: the end of a deeper folder's block comes before the end of
        // an enclosing folder's block, and both before an insert in front of the row that is at that index
        public int Order;
        public int Count;
        public TRow? Row;
    }

    private readonly Func<NodeSnapshot, int, TRow> _createRow;
    private readonly Func<int, int, int, TRow> _createMoreRow; // parent node id, depth, remaining

    private readonly List<int> _changedIds = new();
    private readonly List<int> _changedPos = new();
    private NodeRowValues[] _changedVals = Array.Empty<NodeRowValues>();
    private readonly HashSet<int> _changedSet = new();
    private readonly HashSet<int> _wanted = new();
    private readonly Dictionary<int, int> _rowPos = new();
    private readonly HashSet<int> _removedIds = new();
    private readonly List<int> _removeStarts = new();
    private readonly List<(int Index, int Count)> _removals = new();
    private readonly HashSet<int> _touched = new();
    private readonly List<int> _touchedList = new();
    private readonly List<Cand> _cands = new();
    private readonly List<Cand> _ins = new();
    private readonly List<Edit> _edits = new();
    private readonly List<int> _childPos = new();
    private readonly HashSet<int> _shownIds = new();
    private readonly List<(int Id, long Size)> _children = new();
    private readonly HashSet<int> _affected = new();
    private readonly Dictionary<int, TRow> _displayed = new();
    private int[] _refreshIds = Array.Empty<int>();
    private TRow[] _refreshRows = Array.Empty<TRow>();
    private NodeRowValues[] _refreshVals = Array.Empty<NodeRowValues>();
    private int _seq;

    private struct Block
    {
        public int Start;
        public int End;
        public long Size;
        public int Id;
    }

    private readonly HashSet<int> _flagged = new();
    private readonly HashSet<int> _path = new();
    private readonly List<Block> _blocks = new();
    private readonly List<TRow> _output = new();
    private int[] _parentPos = new int[16];
    private int[] _prevId = new int[16];
    private long[] _prevSize = new long[16];
    private bool[] _prevSet = new bool[16];

    private static readonly IComparer<Block> BlockOrder = Comparer<Block>.Create((a, b) => CompareRank(a.Size, a.Id, b.Size, b.Id));

    /// <summary>An adjacent pair counts as out of order only when the lower one is larger by more than 1% of its size or 1 MB, whichever is less.</summary>
    private const long ResortMinBytes = 1024 * 1024;

    /// <summary>Called just before rows are re-ordered (the window notes the scroll anchor and focus).</summary>
    public Action? BeforeReorder { get; set; }
    /// <summary>Called right after rows were re-ordered (the window puts the scroll anchor and focus back).</summary>
    public Action? AfterReorder { get; set; }

    /// <summary>The number of folders whose children were re-ordered by the last <see cref="Apply"/>.</summary>
    public int ReorderedFolders { get; private set; }

    private static readonly Comparison<Cand> ByParentThenRank = (a, b) =>
    {
        int c = a.Parent.CompareTo(b.Parent);
        return c != 0 ? c : CompareRank(a.Size, a.Id, b.Size, b.Id);
    };

    private static readonly Comparison<Cand> ByRank = (a, b) => CompareRank(a.Size, a.Id, b.Size, b.Id);

    private static readonly Comparison<Edit> ApplyOrder = (a, b) =>
    {
        int c = b.Index.CompareTo(a.Index);
        if (c != 0)
            return c;
        c = a.Kind.CompareTo(b.Kind);
        if (c != 0)
            return c;
        c = b.Order.CompareTo(a.Order);
        return c != 0 ? c : b.Seq.CompareTo(a.Seq);
    };

    private const int MaxSingleRemovals = 64;

    private readonly int _pageSize;

    /// <param name="pageSize">The number of children a folder shows by itself: once a folder shows this many, a new child
    /// that sorts after the last one is counted by the "more items" row instead of being added.</param>
    public LiveRowSync(Func<NodeSnapshot, int, TRow> createRow, Func<int, int, int, TRow> createMoreRow, int pageSize)
    {
        _createRow = createRow;
        _createMoreRow = createMoreRow;
        _pageSize = pageSize;
    }

    /// <summary>
    /// The rows of the last <see cref="Apply"/>, by node id: every row after a full pass, else the changed nodes' rows and
    /// their ancestors' (which is all <see cref="ActivityTargets"/> can walk to).
    /// </summary>
    public IReadOnlyDictionary<int, TRow> Displayed => _displayed;

    /// <summary>Lets go of the rows kept from the last <see cref="Apply"/> (so they and their store can be collected).</summary>
    public void ReleaseRows()
    {
        _displayed.Clear();
        Array.Clear(_refreshRows);
        ShrinkScratch();
    }

    // One pass over a huge change (a drive-wide cleanup) must not leave its high-water collections behind for good
    private const int RetainLimit = 64 * 1024;

    /// <summary>Gives back scratch collections that grew past <see cref="RetainLimit"/> entries (they only hold per-pass data and are cleared at the start of the next pass).</summary>
    private void ShrinkScratch()
    {
        Shrink(_changedIds);
        Shrink(_changedPos);
        Shrink(_changedSet);
        Shrink(_wanted);
        Shrink(_rowPos);
        Shrink(_removedIds);
        Shrink(_removeStarts);
        Shrink(_removals);
        Shrink(_touched);
        Shrink(_touchedList);
        Shrink(_cands);
        Shrink(_ins);
        Shrink(_edits);
        Shrink(_childPos);
        Shrink(_shownIds);
        Shrink(_children);
        Shrink(_affected);
        Shrink(_flagged);
        Shrink(_path);
        Shrink(_blocks);
        _output.Clear();
        Shrink(_output);
        if (_changedVals.Length > RetainLimit) _changedVals = Array.Empty<NodeRowValues>();
        if (_refreshIds.Length > RetainLimit) _refreshIds = Array.Empty<int>();
        if (_refreshRows.Length > RetainLimit) _refreshRows = Array.Empty<TRow>();
        if (_refreshVals.Length > RetainLimit) _refreshVals = Array.Empty<NodeRowValues>();
    }

    private static void Shrink<T>(List<T> list)
    {
        if (list.Capacity > RetainLimit)
        {
            list.Clear();
            list.TrimExcess();
        }
    }

    private static void Shrink<T>(HashSet<T> set)
    {
        if (set.EnsureCapacity(0) > RetainLimit)
        {
            set.Clear();
            set.TrimExcess();
        }
    }

    private static void Shrink<TKey, TValue>(Dictionary<TKey, TValue> map) where TKey : notnull
    {
        if (map.EnsureCapacity(0) > RetainLimit)
        {
            map.Clear();
            map.TrimExcess();
        }
    }

    /// <summary>Largest first; equal sizes by node id (the order of <see cref="FileSystemStore.GetChildPage"/>).</summary>
    private static int CompareRank(long sizeA, int idA, long sizeB, int idB)
    {
        int bySize = sizeB.CompareTo(sizeA);
        return bySize != 0 ? bySize : idA.CompareTo(idB);
    }

    private static bool Before(long sizeA, int idA, long sizeB, int idB) => CompareRank(sizeA, idA, sizeB, idB) < 0;

    /// <summary>
    /// Syncs rows <paramref name="start"/> (the drive's root row) up to <paramref name="end"/> and returns the new end.
    /// <paramref name="changed"/> null means a full pass. <paramref name="needsFullNext"/> is set when a change under a
    /// folder that was still loading had to be skipped, so the next pass should be a full one.
    /// </summary>
    public int Apply(
        IRowList<TRow> rows, int start, int end, FileSystemStore store, IReadOnlyCollection<int>? changed, out bool needsFullNext,
        bool allowReorder = true)
    {
        needsFullNext = false;
        ReorderedFolders = 0;
        _removals.Clear();
        _edits.Clear();
        _seq = 0;

        if (changed == null)
            SyncFull(rows, start, end, store);
        else
            SyncChanged(rows, start, end, store, changed, ref needsFullNext);

        end += ApplyEdits(rows);
        Refresh(rows, start, end, store, changed);
        Reorder(rows, start, end, changed, allowReorder, ref needsFullNext);
        return end;
    }

    // ---------------------------------------------------------------------
    // Structure
    // ---------------------------------------------------------------------

    private void SyncChanged(
        IRowList<TRow> rows, int start, int end, FileSystemStore store, IReadOnlyCollection<int> changed, ref bool needsFullNext)
    {
        _changedIds.Clear();
        _changedIds.AddRange(changed);
        int n = _changedIds.Count;
        Ensure(ref _changedVals, n);
        store.ReadRowValues(CollectionsMarshal.AsSpan(_changedIds), _changedVals);

        _wanted.Clear();
        for (int k = 0; k < n; k++)
        {
            _wanted.Add(_changedIds[k]);
            if (_changedVals[k].ParentId >= 0)
                _wanted.Add(_changedVals[k].ParentId);
        }
        _rowPos.Clear();
        LocateRows(rows, start, end, _wanted);

        // Rows of deleted nodes and of nodes that now have another parent go (with what is shown below them)
        _touched.Clear();
        _removeStarts.Clear();
        _removedIds.Clear();
        for (int k = 0; k < n; k++)
        {
            int id = _changedIds[k];
            if (!_rowPos.TryGetValue(id, out int pos))
                continue;
            TRow row = rows[pos];
            if (_changedVals[k].Deleted || _changedVals[k].ParentId != row.ParentNodeId)
            {
                _removeStarts.Add(pos);
                _removedIds.Add(id);
                _touched.Add(row.ParentNodeId);
            }
        }
        BuildRemovals(rows, end);

        // Live changed nodes without a row are the candidates for new rows under their (expanded) parent
        _cands.Clear();
        for (int k = 0; k < n; k++)
        {
            NodeRowValues v = _changedVals[k];
            if (v.Deleted || v.ParentId < 0)
                continue;
            _touched.Add(v.ParentId);
            int id = _changedIds[k];
            if (_rowPos.ContainsKey(id) && !_removedIds.Contains(id))
                continue;
            _cands.Add(new Cand { Parent = v.ParentId, Id = id, Size = v.Size });
        }
        _cands.Sort(ByParentThenRank);

        // The parents of removed rows may not be among the located rows (their row is found in one more scan)
        _wanted.Clear();
        foreach (int p in _touched)
        {
            if (!_rowPos.ContainsKey(p))
                _wanted.Add(p);
        }
        if (_wanted.Count > 0)
            LocateRows(rows, start, end, _wanted);

        _touchedList.Clear();
        _touchedList.AddRange(_touched);
        _touchedList.Sort();

        int ci = 0;
        foreach (int parentId in _touchedList)
        {
            while (ci < _cands.Count && _cands[ci].Parent < parentId)
                ci++;
            int cj = ci;
            while (cj < _cands.Count && _cands[cj].Parent == parentId)
                cj++;

            if (!_rowPos.TryGetValue(parentId, out int pPos))
                continue;
            TRow pRow = rows[pPos];
            if (!pRow.IsDirectory || !pRow.IsExpanded || InRemoved(pPos))
                continue;
            if (pRow.IsLoadingChildren)
            {
                needsFullNext = true; // its children come from that load, which may predate this change
                continue;
            }

            SyncFolder(rows, end, store, pPos, pRow, ci, cj, allChildren: false);
            ci = cj;
        }
    }

    private void SyncFull(IRowList<TRow> rows, int start, int end, FileSystemStore store)
    {
        _changedIds.Clear();
        _changedPos.Clear();
        for (int i = start + 1; i < end; i++)
        {
            TRow row = rows[i];
            if (row.IsMoreRow)
                continue;
            _changedIds.Add(row.NodeId);
            _changedPos.Add(i);
        }
        int n = _changedIds.Count;
        Ensure(ref _changedVals, n);
        store.ReadRowValues(CollectionsMarshal.AsSpan(_changedIds), _changedVals);

        _removeStarts.Clear();
        for (int k = 0; k < n; k++)
        {
            if (_changedVals[k].Deleted || _changedVals[k].ParentId != rows[_changedPos[k]].ParentNodeId)
                _removeStarts.Add(_changedPos[k]);
        }
        BuildRemovals(rows, end);

        for (int i = start; i < end; i++)
        {
            TRow row = rows[i];
            if (row.IsMoreRow || !row.IsDirectory || !row.IsExpanded || row.IsLoadingChildren || InRemoved(i))
                continue;
            SyncFolder(rows, end, store, i, row, 0, 0, allChildren: true);
        }
    }

    private void LocateRows(IRowList<TRow> rows, int start, int end, HashSet<int> ids)
    {
        for (int i = start; i < end; i++)
        {
            TRow row = rows[i];
            if (!row.IsMoreRow && ids.Contains(row.NodeId))
                _rowPos[row.NodeId] = i;
        }
    }

    /// <summary>Turns the start rows of removals into non-overlapping ranges (a row inside an earlier range goes with it) and queues them.</summary>
    private void BuildRemovals(IRowList<TRow> rows, int end)
    {
        _removeStarts.Sort();
        int lastEnd = -1;
        foreach (int pos in _removeStarts)
        {
            if (pos < lastEnd)
                continue;
            int depth = rows[pos].Depth;
            int count = 1;
            while (pos + count < end && rows[pos + count].Depth > depth)
                count++;
            _removals.Add((pos, count));
            _edits.Add(new Edit { Index = pos, Kind = 0, Count = count });
            lastEnd = pos + count;
        }
    }

    private bool InRemoved(int pos)
    {
        int lo = 0, hi = _removals.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            (int index, int count) = _removals[mid];
            if (pos < index)
                hi = mid - 1;
            else if (pos >= index + count)
                lo = mid + 1;
            else
                return true;
        }
        return false;
    }

    /// <summary>
    /// One expanded folder: one pass over its block finds the visible children and the "more items" row, new children are
    /// merged in at their sorted place among them, and the "more items" count is made right from the folder's live count.
    /// A new child that sorts after the last visible child while children are hidden is not shown but counted by the
    /// "more items" row, so the page does not grow on its own. With <paramref name="allChildren"/> the new children are
    /// found by listing the folder's children instead of coming from the candidate slice.
    /// </summary>
    private void SyncFolder(IRowList<TRow> rows, int end, FileSystemStore store, int pPos, TRow pRow, int candFrom, int candTo, bool allChildren)
    {
        int pDepth = pRow.Depth;
        int childDepth = pDepth + 1;

        _childPos.Clear();
        _shownIds.Clear();
        int morePos = -1;
        int rr = 0;
        int j = pPos + 1;
        for (; j < end && rows[j].Depth > pDepth; j++)
        {
            while (rr < _removals.Count && _removals[rr].Index + _removals[rr].Count <= j)
                rr++;
            if (rr < _removals.Count && _removals[rr].Index <= j)
            {
                j = _removals[rr].Index + _removals[rr].Count - 1; // rows that are going anyway
                continue;
            }

            TRow r = rows[j];
            if (r.Depth != childDepth)
                continue;
            if (r.IsMoreRow)
            {
                morePos = j;
            }
            else
            {
                _childPos.Add(j);
                if (allChildren)
                    _shownIds.Add(r.NodeId);
            }
        }
        int blockEnd = j;

        _ins.Clear();
        int liveCount;
        if (allChildren)
        {
            _children.Clear();
            liveCount = store.CopyLiveChildren(pRow.NodeId, _children);
            foreach ((int id, long size) in _children)
            {
                if (!_shownIds.Contains(id))
                    _ins.Add(new Cand { Parent = pRow.NodeId, Id = id, Size = size });
            }
        }
        else
        {
            liveCount = store.CountLiveChildren(pRow.NodeId);
            for (int c = candFrom; c < candTo; c++)
                _ins.Add(_cands[c]);
        }

        // With a full page shown, a new child that sorts after the last visible child stays hidden (counted by the "more items" row)
        if (_childPos.Count >= _pageSize && _childPos.Count > 0 && _ins.Count > 0)
        {
            TRow last = rows[_childPos[^1]];
            long lastSize = last.Size;
            int lastId = last.NodeId;
            _ins.RemoveAll(c => !Before(c.Size, c.Id, lastSize, lastId));
        }
        _ins.Sort(ByRank);

        // Merge: each new child goes before the first visible child it outranks, the rest at the end of the children
        int ni = 0;
        for (int k = 0; k < _childPos.Count && ni < _ins.Count; k++)
        {
            TRow r = rows[_childPos[k]];
            while (ni < _ins.Count && Before(_ins[ni].Size, _ins[ni].Id, r.Size, r.NodeId))
                QueueInsert(store, _childPos[k], _ins[ni++].Id, childDepth, int.MaxValue);
        }
        int tail = morePos >= 0 ? morePos : blockEnd;
        while (ni < _ins.Count)
            QueueInsert(store, tail, _ins[ni++].Id, childDepth, -childDepth);

        int remaining = liveCount - (_childPos.Count + _ins.Count);
        if (morePos >= 0)
        {
            if (remaining > 0)
                rows[morePos].SetRemaining(remaining);
            else
                _edits.Add(new Edit { Index = morePos, Kind = 1, Count = 1 });
        }
        else if (remaining > 0 && (allChildren || _childPos.Count >= _pageSize))
        {
            // A folder shown in full has no hidden children: what the live count has beyond the listed rows are children not in
            // the changed set of this pass (created since, or deferred), which the next pass lists
            _edits.Add(new Edit { Index = blockEnd, Kind = 2, Order = -childDepth, Seq = _seq++, Row = _createMoreRow(pRow.NodeId, childDepth, remaining) });
        }
    }

    private void QueueInsert(FileSystemStore store, int index, int nodeId, int depth, int order)
    {
        NodeSnapshot snapshot = store.GetNodeInfo(nodeId);
        _edits.Add(new Edit { Index = index, Kind = 2, Order = order, Seq = _seq++, Row = _createRow(snapshot, depth) });
    }

    /// <summary>Applies the queued edits from the end of the list backwards, so earlier indices stay valid. Returns the change in row count.</summary>
    private int ApplyEdits(IRowList<TRow> rows)
    {
        if (_edits.Count == 0)
            return 0;

        _edits.Sort(ApplyOrder);
        int delta = 0;
        foreach (Edit e in _edits)
        {
            switch (e.Kind)
            {
                case 0:
                    // One change notification per row keeps the list's selection and scroll position (a range
                    // removal is announced as a reset); a big block is worth the reset
                    if (e.Count <= MaxSingleRemovals)
                    {
                        for (int i = e.Index + e.Count - 1; i >= e.Index; i--)
                            rows.RemoveAt(i);
                    }
                    else
                    {
                        rows.RemoveRange(e.Index, e.Count);
                    }
                    delta -= e.Count;
                    break;
                case 1:
                    rows.RemoveAt(e.Index);
                    delta--;
                    break;
                default:
                    rows.Insert(e.Index, e.Row!);
                    delta++;
                    break;
            }
        }
        _edits.Clear();
        return delta;
    }

    // ---------------------------------------------------------------------
    // Numbers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Refreshes the displayed numbers of rows start+1 .. end (the drive row refreshes itself). Without a changed set
    /// every row; with one, the changed nodes' rows and their ancestors', and the children of those (their percentages).
    /// A folder row that is not an ancestor of a change has nothing below it to refresh, so its block is skipped.
    /// Renamed nodes are in the changed set, so their names are re-read; a full pass compares every name.
    /// </summary>
    private void Refresh(IRowList<TRow> rows, int start, int end, FileSystemStore store, IReadOnlyCollection<int>? changed)
    {
        bool all = changed == null;
        _displayed.Clear();
        if (end > start)
            _displayed[rows[start].NodeId] = rows[start];

        if (!all)
        {
            _affected.Clear();
            store.AddWithAncestors(changed!, _affected);
            _changedSet.Clear();
            foreach (int id in changed!)
                _changedSet.Add(id);
        }

        int count = 0;
        int skipBelow = -1;
        for (int i = start + 1; i < end; i++)
        {
            TRow row = rows[i];
            if (skipBelow >= 0)
            {
                if (row.Depth > skipBelow)
                    continue;
                skipBelow = -1;
            }
            if (row.IsMoreRow)
                continue;

            bool self = true;
            bool refresh = true;
            if (!all)
            {
                self = _affected.Contains(row.NodeId);
                refresh = self || _affected.Contains(row.ParentNodeId);
                if (row.IsDirectory && !self)
                    skipBelow = row.Depth;
            }
            if (self)
                _displayed[row.NodeId] = row;
            if (!refresh)
                continue;

            if (count == _refreshIds.Length)
            {
                int cap = Math.Max(1024, count * 2);
                Array.Resize(ref _refreshIds, cap);
                Array.Resize(ref _refreshRows, cap);
            }
            _refreshIds[count] = row.NodeId;
            _refreshRows[count] = row;
            count++;
        }
        if (count == 0)
            return;

        Ensure(ref _refreshVals, count);
        store.ReadRowValues(_refreshIds.AsSpan(0, count), _refreshVals);
        for (int k = 0; k < count; k++)
        {
            TRow row = _refreshRows[k];
            if (_refreshVals[k].Deleted)
                continue; // its row goes in the next pass
            row.ApplyValues(_refreshVals[k]);

            if (all ? !store.NameIs(row.NodeId, row.Name) : _changedSet.Contains(row.NodeId))
                row.ApplySnapshot(store.GetNodeInfo(row.NodeId));
        }
        Array.Clear(_refreshRows, 0, count); // do not keep rows alive
    }

    // ---------------------------------------------------------------------
    // Order
    // ---------------------------------------------------------------------

    /// <summary>
    /// Finds the folders whose visible children are out of order (one linear pass; with a changed set only inside the folders
    /// that are ancestors of a change, which Refresh just collected) and re-orders them with one ReplaceRange.
    /// </summary>
    private void Reorder(IRowList<TRow> rows, int start, int end, IReadOnlyCollection<int>? changed, bool allowReorder, ref bool needsFullNext)
    {
        bool all = changed == null;
        if (end - start < 3 || (!all && changed!.Count == 0))
            return;

        _flagged.Clear();
        _path.Clear();
        int top = 0;
        int skipBelow = -1;
        _parentPos[0] = start;
        _prevSet[0] = false;
        for (int i = start + 1; i < end; i++)
        {
            TRow row = rows[i];
            int d = row.Depth;
            if (skipBelow >= 0)
            {
                if (d > skipBelow)
                    continue;
                skipBelow = -1;
            }
            if (row.IsMoreRow || d < 1)
                continue;

            if (d >= _parentPos.Length - 1)
            {
                int cap = d * 2 + 2;
                Array.Resize(ref _parentPos, cap);
                Array.Resize(ref _prevId, cap);
                Array.Resize(ref _prevSize, cap);
                Array.Resize(ref _prevSet, cap);
            }
            // A row at depth d starts a new run of siblings below it; the rows of a deeper run are over
            for (int k = d + 1; k <= top; k++)
                _prevSet[k] = false;
            if (d > top)
                _prevSet[d] = false;
            top = d;

            long size = row.Size;
            if (_prevSet[d] && Before(size, row.NodeId, _prevSize[d], _prevId[d]))
            {
                long diff = size - _prevSize[d];
                int pPos = _parentPos[d - 1];
                if (diff > Math.Min(size / 100, ResortMinBytes) && !rows[pPos].IsLoadingChildren)
                {
                    _flagged.Add(pPos);
                    for (int k = 0; k < d; k++)
                        _path.Add(_parentPos[k]);
                }
            }
            _prevSet[d] = true;
            _prevSize[d] = size;
            _prevId[d] = row.NodeId;
            _parentPos[d] = i;

            if (!all && row.IsDirectory && !_affected.Contains(row.NodeId))
                skipBelow = d;
        }
        if (_flagged.Count == 0)
            return;
        if (!allowReorder)
        {
            needsFullNext = true; // a menu or dialog is up: look again on the next pass
            _flagged.Clear();
            _path.Clear();
            return;
        }

        _output.Clear();
        _blocks.Clear();
        _output.Add(rows[start]);
        EmitChildren(rows, start, end);

        // Only the span between the first and the last row that moved is replaced
        int first = 0;
        int count = end - start;
        while (first < count && ReferenceEquals(_output[first], rows[start + first]))
            first++;
        int last = count - 1;
        while (last > first && ReferenceEquals(_output[last], rows[start + last]))
            last--;
        if (first < count)
        {
            BeforeReorder?.Invoke();
            rows.ReplaceRange(start + first, last - first + 1, _output.GetRange(first, last - first + 1));
            AfterReorder?.Invoke();
            ReorderedFolders = _flagged.Count;
        }
        _output.Clear();
        _blocks.Clear();
        _flagged.Clear();
        _path.Clear();
    }

    /// <summary>
    /// Appends to _output the rows below the folder row at <paramref name="pPos"/> up to <paramref name="pEnd"/>: a folder
    /// that is on the path to a flagged one is taken apart into its child blocks (sorted if it is flagged itself, with the
    /// "more items" row last), any other block is copied as it is.
    /// </summary>
    private void EmitChildren(IRowList<TRow> rows, int pPos, int pEnd)
    {
        if (!_path.Contains(pPos))
        {
            for (int j = pPos + 1; j < pEnd; j++)
                _output.Add(rows[j]);
            return;
        }

        int childDepth = rows[pPos].Depth + 1;
        int baseIdx = _blocks.Count;
        int morePos = -1;
        for (int j = pPos + 1; j < pEnd;)
        {
            TRow r = rows[j];
            int e = j + 1;
            while (e < pEnd && rows[e].Depth > childDepth)
                e++;
            if (r.IsMoreRow)
                morePos = j;
            else
                _blocks.Add(new Block { Start = j, End = e, Size = r.Size, Id = r.NodeId });
            j = e;
        }
        int n = _blocks.Count;
        if (_flagged.Contains(pPos))
            _blocks.Sort(baseIdx, n - baseIdx, BlockOrder);

        for (int b = baseIdx; b < n; b++)
        {
            Block blk = _blocks[b];
            _output.Add(rows[blk.Start]);
            EmitChildren(rows, blk.Start, blk.End);
        }
        if (morePos >= 0)
            _output.Add(rows[morePos]);
        _blocks.RemoveRange(baseIdx, n - baseIdx);
    }

    private static void Ensure<T>(ref T[] array, int size)
    {
        if (array.Length < size)
            array = new T[Math.Max(size, array.Length * 2)];
    }

    // ---------------------------------------------------------------------
    // Reusing node ids
    // ---------------------------------------------------------------------

    /// <summary>
    /// Finds which of <paramref name="candidates"/> (node ids that were deleted) a row of the block still refers to, as its
    /// own node or its parent, and adds them to <paramref name="referenced"/>. Returns false if some folder is still
    /// loading its children (rows are about to be built from node ids read earlier): then no id may be released.
    /// </summary>
    public bool FindReferencedIds(IRowList<TRow> rows, int start, int end, HashSet<int> candidates, HashSet<int> referenced)
    {
        for (int i = start; i < end; i++)
        {
            TRow row = rows[i];
            if (row.IsLoadingChildren)
                return false;
            if (row.NodeId >= 0 && candidates.Contains(row.NodeId))
                referenced.Add(row.NodeId);
            if (row.ParentNodeId >= 0 && candidates.Contains(row.ParentNodeId))
                referenced.Add(row.ParentNodeId);
        }
        return true;
    }
}
