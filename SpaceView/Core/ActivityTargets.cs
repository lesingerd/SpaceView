namespace SpaceView.Core;

/// <summary>
/// Decides which tree rows flash when live monitoring sees changes: for each changed node only the lowest (deepest) row
/// that is currently displayed and contains it - the node's own row, else its nearest displayed ancestor. The ancestors
/// above that row stay quiet. Plain logic with no UI types, so it can be tested on its own.
/// </summary>
public static class ActivityTargets
{
    /// <summary>
    /// Most distinct nodes looked at per pass and kept per drive between passes. A busy volume can raise thousands of
    /// events a second; beyond this the rest of a burst is simply not highlighted.
    /// </summary>
    public const int MaxNodes = FileSystemStore.MaxActivityNodes;

    /// <summary>
    /// Maps each changed node to its target row and returns the distinct rows. <paramref name="displayed"/> holds the
    /// rows currently in the list for <paramref name="session"/> (the "more items" rows are not in it, so an item paged
    /// out of view under an expanded folder highlights that folder's row). The store lock is taken once for the whole
    /// batch; chains are walked through tombstones, so a deleted node resolves to its nearest displayed ancestor.
    /// Nodes beyond <paramref name="maxNodes"/> distinct ones are ignored.
    /// </summary>
    public static List<TRow> Resolve<TRow>(
        FileSystemStore store,
        DriveSession session,
        IReadOnlyCollection<int> nodeIds,
        IReadOnlyDictionary<RowKey, TRow> displayed,
        int maxNodes = MaxNodes)
        where TRow : class =>
        Resolve(store, nodeIds, displayed.Count, (int id, out TRow? row) => displayed.TryGetValue(new RowKey(session, id), out row), maxNodes);

    /// <summary><see cref="Resolve{TRow}(FileSystemStore, DriveSession, IReadOnlyCollection{int}, IReadOnlyDictionary{RowKey, TRow}, int)"/> with the displayed rows of one drive keyed by node id.</summary>
    public static List<TRow> Resolve<TRow>(
        FileSystemStore store,
        IReadOnlyCollection<int> nodeIds,
        IReadOnlyDictionary<int, TRow> displayedById,
        int maxNodes = MaxNodes)
        where TRow : class =>
        Resolve(store, nodeIds, displayedById.Count, (int id, out TRow? row) => displayedById.TryGetValue(id, out row), maxNodes);

    private delegate bool TryGetRow<TRow>(int id, out TRow? row);

    private static List<TRow> Resolve<TRow>(
        FileSystemStore store,
        IReadOnlyCollection<int> nodeIds,
        int displayedCount,
        TryGetRow<TRow> tryGetRow,
        int maxNodes)
        where TRow : class
    {
        var rows = new List<TRow>();
        if (nodeIds.Count == 0 || displayedCount == 0)
            return rows;

        store.ReadParents(parentOf =>
        {
            // node -> its target row (null: none). Shared ancestors are walked only once per batch.
            var resolved = new Dictionary<int, TRow?>();
            var chain = new List<int>();
            var seenRows = new HashSet<TRow>(ReferenceEqualityComparer.Instance);
            int distinct = 0;

            foreach (int start in nodeIds)
            {
                if (resolved.ContainsKey(start))
                    continue;
                if (++distinct > maxNodes)
                    break;

                chain.Clear();
                TRow? target = null;
                int id = start;
                // The depth of a tree is far below this; the bound only guards against a corrupt parent link
                for (int steps = 0; id >= 0 && steps < 4096; steps++)
                {
                    if (resolved.TryGetValue(id, out target))
                        break;

                    chain.Add(id);
                    if (tryGetRow(id, out target))
                        break;

                    id = parentOf(id);
                }

                // Every node on the walked chain below the found row resolves to the same row
                foreach (int visited in chain)
                    resolved[visited] = target;

                if (target != null && seenRows.Add(target))
                    rows.Add(target);
            }
        });

        return rows;
    }
}
