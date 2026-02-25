using System.Collections.Generic;
using System.Linq;
using Livet;

namespace BeMusicSeeker.Models.Utils;

internal static class DispatcherCollectionExt
{
    public static void AddRange<T>(this DispatcherCollection<T> collection, IEnumerable<T> items)
    {
        foreach (T item in items)
        {
            collection.Add(item);
        }
    }

    public static bool RemoveExt<T>(this DispatcherCollection<T> collection, T item) where T : class
    {
        int num = collection.IndexOf(item);
        if (num != -1)
        {
            collection.RemoveAt(num);
            return true;
        }
        return false;
    }

    public static List<T> Remove<T>(this DispatcherCollection<T> collection, IEnumerable<T> items) where T : class
    {
        List<T> list = new List<T>();
        foreach (int item in Enumerable.Range(0, items.Count()))
        {
            if (collection.RemoveExt(items.ElementAt(item)))
            {
                list.Add(items.ElementAt(item));
            }
        }
        return list;
    }

    public static void Replace<T>(this DispatcherCollection<T> collection, T src, T dst)
    {
        collection[collection.IndexOf(src)] = dst;
    }
}
