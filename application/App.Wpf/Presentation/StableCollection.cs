using System.Collections.ObjectModel;
namespace DownloadManager.App.Presentation;

/// <summary>Preserves row identities and avoids Reset notifications for periodic snapshots.</summary>
public static class StableCollection
{
    public static void Reconcile<T, TSource, TKey>(ObservableCollection<T> rows, IReadOnlyList<TSource> snapshot,
        Func<T, TKey> rowKey, Func<TSource, TKey> sourceKey, Func<TSource, T> create, Action<T, TSource> update) where TKey : notnull
    {
        var wanted = snapshot.ToDictionary(sourceKey); // Reject ambiguous snapshots before mutations.
        var existing = rows.ToDictionary(rowKey);
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!wanted.ContainsKey(rowKey(rows[i]))) rows.RemoveAt(i);
        for (var i = 0; i < snapshot.Count; i++)
        {
            var source = snapshot[i]; var key = sourceKey(source);
            if (existing.TryGetValue(key, out var row))
            {
                update(row, source);
                if (!EqualityComparer<TKey>.Default.Equals(rowKey(rows[i]), key)) rows.Move(rows.IndexOf(row), i);
            }
            else rows.Insert(i, create(source));
        }
    }
}
