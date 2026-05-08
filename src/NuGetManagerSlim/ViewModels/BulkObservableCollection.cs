using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace NuGetManagerSlim.ViewModels
{
    /// <summary>
    /// ObservableCollection that can swap its contents in a single Reset
    /// notification instead of N Add notifications. Used by the package list
    /// to avoid 51 layout passes (1 Clear + 50 Add) when search results land.
    /// </summary>
    internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
    {
        public BulkObservableCollection() { }

        public BulkObservableCollection(IEnumerable<T> items) : base(items) { }

        public void ReplaceAll(IEnumerable<T> items)
        {
            Items.Clear();
            if (items != null)
            {
                foreach (var item in items)
                    Items.Add(item);
            }
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
