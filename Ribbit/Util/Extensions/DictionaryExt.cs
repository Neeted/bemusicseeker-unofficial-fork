using System.Collections.Generic;

namespace Ribbit.Util.Extensions;

public static class DictionaryExt
{
    public static TValue TryGetValue<TKey, TValue>(this Dictionary<TKey, TValue> tbl, TKey key)
    {
        if (!tbl.TryGetValue(key, out TValue value))
        {
            return default;
        }
        return value;
    }

    public static TValue TryGetValue<TKey, TValue>(this Dictionary<TKey, TValue> tbl, TKey key, TValue defValue)
    {
        if (!tbl.TryGetValue(key, out TValue value))
        {
            return defValue;
        }
        return value;
    }
}
