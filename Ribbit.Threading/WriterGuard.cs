using System;
using System.Threading;

namespace Ribbit.Threading;

public class WriterGuard : IDisposable
{
	private ReaderWriterLockSlim _readerWriterLock;

	private bool IsDisposed => _readerWriterLock == null;

	public WriterGuard(ReaderWriterLockSlim readerWriterLock)
	{
		_readerWriterLock = readerWriterLock;
		_readerWriterLock.EnterWriteLock();
	}

	public virtual void Dispose()
	{
		if (IsDisposed)
		{
			throw new ObjectDisposedException(ToString());
		}
		_readerWriterLock.ExitWriteLock();
		_readerWriterLock = null;
	}
}
