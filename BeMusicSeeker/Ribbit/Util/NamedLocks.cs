using System.Collections.Generic;

namespace Ribbit.Util;

public class NamedLocks<T>
{
    private readonly Dictionary<T, object> locks = [];

    private readonly object lockObject = new();

    public object GetLockObject(T name)
    {
        object obj;
        lock (lockObject)
        {
            if (!locks.ContainsKey(name))
            {
                obj = (locks[name] = new object());
            }
            else
            {
                obj = locks[name];
            }
        }
        return obj;
    }
}
