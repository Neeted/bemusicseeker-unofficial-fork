using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using Ribbit.Util;
using SQLite;

namespace BeMusicSeeker.Models.LR2;

public class SQLiteConnectionEx : SQLiteConnection
{
	public SQLiteConnectionEx(string databasePath, bool storeDateTimeAsTicks = true)
		: base(databasePath, storeDateTimeAsTicks)
	{
	}

	public SQLiteConnectionEx(string databasePath, SQLiteOpenFlags openFlags, bool storeDateTimeAsTicks = true)
		: base(databasePath, openFlags, storeDateTimeAsTicks)
	{
	}

	public static void RetryIfLockedOrBusy(Action onAction, Action<Exception> onError = null, uint? maxRetryCount = 10u)
	{
		if (onAction == null)
		{
			throw new ArgumentNullException("onAction");
		}
		if (onError == null)
		{
			onError = delegate(Exception ex)
			{
				ExceptionDispatchInfo.Capture(ex).Throw();
			};
		}
		Action retryDelay = delegate
		{
			Thread.Sleep(1000);
		};
		Func<Exception, bool> retryCondition = (Exception ex) => ex is SQLiteException ex2 && (ex2.Result == SQLite3.Result.Busy || ex2.Result == SQLite3.Result.Locked);
		if (maxRetryCount.HasValue)
		{
			RetryHelper.RetryIfError(onAction, onError, retryDelay, retryCondition, maxRetryCount.Value);
		}
		else
		{
			RetryHelper.RetryIfError(onAction, onError, retryDelay, retryCondition);
		}
	}

	public new int Delete<T>(object primaryKey)
	{
		int ret = 0;
		RetryIfLockedOrBusy(delegate
		{
			ret = base.Delete<T>(primaryKey);
		}, null, 10u);
		return ret;
	}

	public new int InsertOrReplace(object obj, Type objType)
	{
		int ret = 0;
		RetryIfLockedOrBusy(delegate
		{
			ret = base.InsertOrReplace(obj, objType);
		}, null, 10u);
		return ret;
	}

	public new void Commit()
	{
		RetryIfLockedOrBusy(delegate
		{
			base.Commit();
		}, null, 10u);
	}

	public List<string> TryApplyReadOptimizedPragmas(bool enabled, int cacheSizeKb = 262144, long mmapSizeBytes = 2147483648L)
	{
		List<string> list = new List<string>();
		if (!enabled)
		{
			list.Add("enabled=false");
			return list;
		}
		TryApplyPragma("temp_store=MEMORY", list);
		TryApplyPragma("cache_size=-" + Math.Abs(cacheSizeKb), list);
		TryApplyPragma("mmap_size=" + Math.Max(0, mmapSizeBytes), list);
		return list;
	}

	private void TryApplyPragma(string pragmaBody, List<string> logs)
	{
		if (string.IsNullOrWhiteSpace(pragmaBody))
		{
			return;
		}
		try
		{
			long pragmaResult = ExecuteScalar<long>("PRAGMA " + pragmaBody + ";");
			if (pragmaBody.StartsWith("mmap_size=", StringComparison.OrdinalIgnoreCase))
			{
				logs?.Add(pragmaBody + ":ok(" + pragmaResult + ")");
			}
			else
			{
				logs?.Add(pragmaBody + ":ok");
			}
		}
		catch (Exception ex)
		{
			logs?.Add(pragmaBody + ":ng(" + ex.GetType().Name + ")");
		}
	}
}
