using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace BeMusicSeeker.Models.Utils;

internal static class ObservableCollectionExt
{
    internal static void AddRange<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        if (collection == null)
        {
            throw new ArgumentNullException(nameof(collection));
        }
        if (items == null)
        {
            throw new ArgumentNullException(nameof(items));
        }
        foreach (T item in items)
        {
            collection.Add(item);
        }
    }

    internal static bool RemoveExt<T>(this ObservableCollection<T> collection, T item)
        where T : class
    {
        if (collection == null)
        {
            throw new ArgumentNullException(nameof(collection));
        }
        int index = collection.IndexOf(item);
        if (index < 0)
        {
            return false;
        }
        collection.RemoveAt(index);
        return true;
    }

    internal static List<T> Remove<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
        where T : class
    {
        if (collection == null)
        {
            throw new ArgumentNullException(nameof(collection));
        }
        if (items == null)
        {
            throw new ArgumentNullException(nameof(items));
        }
        List<T> removed = [];
        foreach (T item in items)
        {
            if (collection.RemoveExt(item))
            {
                removed.Add(item);
            }
        }
        return removed;
    }

    internal static void Replace<T>(this ObservableCollection<T> collection, T source, T destination)
    {
        if (collection == null)
        {
            throw new ArgumentNullException(nameof(collection));
        }
        collection[collection.IndexOf(source)] = destination;
    }
}
