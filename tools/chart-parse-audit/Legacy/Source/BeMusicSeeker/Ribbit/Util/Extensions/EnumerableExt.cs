using System;
using System.Collections.Generic;
using System.Linq;

namespace Ribbit.Util.Extensions;

public static class EnumerableExt
{
    private class CompareSelector<T, TKey>(Func<T, TKey> selector) : IEqualityComparer<T>
    {
        private readonly Func<T, TKey> selector = selector;

        public bool Equals(T x, T y)
        {
            return selector(x).Equals(selector(y));
        }

        public int GetHashCode(T obj)
        {
            return selector(obj).GetHashCode();
        }
    }

    public static T ElementAtOrDefault<T>(this IEnumerable<T> list, int index, T @default)
    {
        if (index < 0 || index >= list.Count())
        {
            return @default;
        }
        return list.ElementAt(index);
    }

    public static T ElementAtOrDefault<T>(this IEnumerable<T> list, int index, Func<T> @default)
    {
        if (index < 0 || index >= list.Count())
        {
            return @default();
        }
        return list.ElementAt(index);
    }

    public static IEnumerable<T> Distinct<T, TKey>(this IEnumerable<T> source, Func<T, TKey> selector)
    {
        return source.Distinct(new CompareSelector<T, TKey>(selector));
    }

    public static IEnumerable<IEnumerable<T>> Split<T>(this IEnumerable<T> list, int parts)
    {
        int i = 0;
        return from item in list
               group item by i++ % parts into part
               select part.AsEnumerable();
    }

    public static IEnumerable<IEnumerable<T>> Section<T>(this IEnumerable<T> source, int length)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException("length");
        }
        var list = new List<T>(length);
        foreach (T item in source)
        {
            list.Add(item);
            if (list.Count == length)
            {
                yield return list.AsReadOnly();
                list = new List<T>(length);
            }
        }
        if (list.Count > 0)
        {
            yield return list.AsReadOnly();
        }
    }

    public static double StdDev(this IEnumerable<double> values)
    {
        double result = 0.0;
        int num = values.Count();
        if (num > 1)
        {
            double avg = values.Average();
            result = System.Math.Sqrt(values.Sum(d => (d - avg) * (d - avg)) / (double)(num - 1));
        }
        return result;
    }

    public static IEnumerable<T> SequentialDistinct<T>(this IEnumerable<T> source) where T : IComparable
    {
        if (source == null)
        {
            throw new ArgumentNullException("source");
        }
        T prev;
        try
        {
            prev = source.First();
        }
        catch
        {
            yield break;
        }
        yield return prev;
        foreach (T item in source.Skip(1))
        {
            if (item.CompareTo(prev) != 0)
            {
                prev = item;
                yield return item;
            }
        }
    }

    public static IEnumerable<T> SequentialDistinct<T, TKey>(this IEnumerable<T> source, Func<T, TKey> selector) where TKey : IComparable
    {
        if (source == null)
        {
            throw new ArgumentNullException("source");
        }
        if (selector == null)
        {
            throw new ArgumentNullException("selector");
        }
        T val;
        TKey compVal;
        try
        {
            val = source.First();
            compVal = selector(val);
        }
        catch
        {
            yield break;
        }
        yield return val;
        foreach (T item in source.Skip(1))
        {
            TKey val2 = selector(item);
            if (compVal.CompareTo(val2) != 0)
            {
                compVal = val2;
                yield return item;
            }
        }
    }

    public static IEnumerable<T> SequentialDistinct<T>(this IEnumerable<T> source, Func<T, T, bool> skipWhile)
    {
        if (source == null)
        {
            throw new ArgumentNullException("source");
        }
        if (skipWhile == null)
        {
            throw new ArgumentNullException("skipWhile");
        }
        T prev;
        try
        {
            prev = source.First();
        }
        catch
        {
            yield break;
        }
        yield return prev;
        foreach (T item in source.Skip(1))
        {
            if (!skipWhile(prev, item))
            {
                prev = item;
                yield return item;
            }
        }
    }

    public static IEnumerable<T> Materialize<T>(this IEnumerable<T> source, bool nullToEmpty = true)
    {
        if (nullToEmpty && source == null)
        {
            return [];
        }
        if (source == null)
        {
            throw new ArgumentNullException("source");
        }
        if (source is ICollection<T>)
        {
            return source;
        }
        if (source is IReadOnlyCollection<T>)
        {
            return source;
        }
        return [.. source];
    }

    public static IEnumerable<T> DequeWhile<T>(this Queue<T> src, Func<T, bool> condition)
    {
        if (src == null)
        {
            throw new ArgumentNullException("src");
        }
        while (src.Count > 0 && condition(src.Peek()))
        {
            yield return src.Dequeue();
        }
    }
}
