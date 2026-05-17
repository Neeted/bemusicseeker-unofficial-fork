using System.Collections.Generic;

namespace Ribbit.Util.Extensions;

public static class DictionaryExt
{
    public static TValue TryGetValue<TKey, TValue>(this Dictionary<TKey, TValue> tbl, TKey key)
    {
        if (!tbl.TryGetValue(key, out var value))
        {
            return default(TValue);
        }
        return value;
    }

    public static TValue TryGetValue<TKey, TValue>(this Dictionary<TKey, TValue> tbl, TKey key, TValue defValue)
    {
        if (!tbl.TryGetValue(key, out var value))
        {
            return defValue;
        }
        return value;
    }
}
