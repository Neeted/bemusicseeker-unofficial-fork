using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BeMusicSeeker.Tests;

public class LegacyNaturalComparer<T>(bool isWhiteSpacePrior = false) : Comparer<string>, IDisposable
{
    protected Dictionary<string, string[]> table = [];

    protected bool isWhiteSpacePrior = isWhiteSpacePrior;

    public void Dispose()
    {
        table.Clear();
    }

    public override int Compare(string x, string y)
    {
        if (x == y)
        {
            return 0;
        }
        if (isWhiteSpacePrior)
        {
            if (string.IsNullOrWhiteSpace(x))
            {
                return -1;
            }
            if (string.IsNullOrWhiteSpace(y))
            {
                return 1;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(x))
            {
                return 1;
            }
            if (string.IsNullOrWhiteSpace(y))
            {
                return -1;
            }
        }
        if (!table.TryGetValue(x, out string[]? value))
        {
            value = Regex.Split(x, "([+-]?[0-9]+(\\.[0-9]*)?)");
            table.Add(x, value);
        }
        if (!table.TryGetValue(y, out string[]? value2))
        {
            value2 = Regex.Split(y, "([+-]?[0-9]+(\\.[0-9]*)?)");
            table.Add(y, value2);
        }
        for (int i = 0; i < value.Length && i < value2.Length; i++)
        {
            if (value[i] != value2[i])
            {
                return PartCompare(value[i], value2[i]);
            }
        }
        if (value2.Length > value.Length)
        {
            return 1;
        }
        if (value.Length > value2.Length)
        {
            return -1;
        }
        return 0;
    }

    protected static int PartCompare(string left, string right)
    {
        if (!double.TryParse(left, out double result))
        {
            return left.CompareTo(right);
        }
        if (!double.TryParse(right, out double result2))
        {
            return left.CompareTo(right);
        }
        return result.CompareTo(result2);
    }
}
