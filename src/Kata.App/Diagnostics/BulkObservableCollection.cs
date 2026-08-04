using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Kata.App.Diagnostics;

/// <summary>
/// ObservableCollection that supports a single-notification bulk replace. The
/// default Clear + N Add pattern fires 1 + N CollectionChanged events which,
/// through WPF binding, forces the item host (Nodify NodifyEditor, ListBox, …)
/// to react to each — quadratic-ish cost on hundreds of items and a big source
/// of UI hitches. ReplaceAll swaps the whole content and raises a single Reset.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection() { }
    public BulkObservableCollection(IEnumerable<T> items) : base(items) { }

    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Clears then re-adds items in chunks, yielding the UI thread between chunks.
    /// Firing a single Reset is fast for the collection itself but the item host
    /// (Nodify) still has to create N visuals synchronously, which stalls the mouse.
    /// Chunked adds let WPF interleave input events between batches.
    /// </summary>
    public async Task ReplaceAllChunkedAsync(IReadOnlyList<T> items, int chunkSize = 40)
    {
        CheckReentrancy();
        Items.Clear();
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

        for (int i = 0; i < items.Count; i += chunkSize)
        {
            int end = System.Math.Min(i + chunkSize, items.Count);
            for (int j = i; j < end; j++)
            {
                var item = items[j];
                Items.Add(item);
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, j));
            }
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));

            // Give the UI thread a chance to pump input / render before the next batch.
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
    }
}
