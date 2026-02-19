using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Ribbit.Util.Extensions;

public static class TaskEx
{
	public static Task Logging(this Task task, Action<Task, string, string, int> logger, [CallerMemberName] string memberName = null, [CallerFilePath] string filePath = null, [CallerLineNumber] int lineNumber = 0)
	{
		return task.ContinueWith(delegate(Task x)
		{
			logger(x, memberName, filePath, lineNumber);
		});
	}

	public static Task<T> Logging<T>(this Task<T> task, Action<Task<T>, string, string, int> logger, [CallerMemberName] string memberName = null, [CallerFilePath] string filePath = null, [CallerLineNumber] int lineNumber = 0)
	{
		return task.ContinueWith(delegate(Task<T> x)
		{
			logger(x, memberName, filePath, lineNumber);
			return x.Result;
		});
	}

	public static Task<T> Logging<T>(this Task<T> task, Action<Task, string, string, int> logger, [CallerMemberName] string memberName = null, [CallerFilePath] string filePath = null, [CallerLineNumber] int lineNumber = 0)
	{
		return task.ContinueWith(delegate(Task<T> x)
		{
			logger(x, memberName, filePath, lineNumber);
			return x.Result;
		});
	}
}
