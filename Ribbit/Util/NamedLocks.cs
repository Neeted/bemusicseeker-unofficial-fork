using System.Collections.Generic;

namespace Ribbit.Util;

public class NamedLocks<T>
{
	private Dictionary<T, object> locks = new Dictionary<T, object>();

	private object lockObject = new object();

	public object GetLockObject(T name)
	{
		object obj;
		lock (lockObject)
		{
			if (!locks.ContainsKey(name))
			{
				obj = (locks[name] = new object());
				obj = obj;
			}
			else
			{
				obj = locks[name];
			}
		}
		return obj;
	}
}
