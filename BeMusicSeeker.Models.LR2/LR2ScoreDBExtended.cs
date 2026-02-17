using System;
using System.Threading;

namespace BeMusicSeeker.Models.LR2;

public sealed class LR2ScoreDBExtended : LR2ScoreDB
{
	private bool doNotUnlock;

	private static object lockObject = new object();

	public static bool Lock(TimeSpan timespan)
	{
		if (Monitor.IsEntered(lockObject))
		{
			return true;
		}
		bool lockTaken = false;
		Monitor.TryEnter(lockObject, timespan, ref lockTaken);
		return lockTaken;
	}

	public static void Unlock()
	{
		if (Monitor.IsEntered(lockObject))
		{
			Monitor.Exit(lockObject);
		}
	}

	public LR2ScoreDBExtended(string dbPath)
		: base(dbPath)
	{
		if (Monitor.IsEntered(lockObject))
		{
			doNotUnlock = true;
		}
		else
		{
			Monitor.Enter(lockObject);
		}
		base.BusyTimeout = new TimeSpan(0, 0, 60);
	}

	protected override void Dispose(bool disposing)
	{
		if (Monitor.IsEntered(lockObject) && !doNotUnlock)
		{
			Monitor.Exit(lockObject);
		}
		base.Dispose(disposing);
	}
}
