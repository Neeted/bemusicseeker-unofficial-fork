using System;
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
		RetryHelper.RetryIfError(onAction, onError, delegate
		{
			Thread.Sleep(1000);
		}, (Exception ex) => ex is SQLiteException ex2 && (ex2.Result == SQLite3.Result.Busy || ex2.Result == SQLite3.Result.Locked));
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
}
