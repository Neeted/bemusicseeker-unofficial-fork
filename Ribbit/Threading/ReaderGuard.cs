using System;
using System.Threading;

namespace Ribbit.Threading;

public class ReaderGuard : IDisposable
{
    private readonly ReaderWriterLockSlim _readerWriterLock;

    public ReaderGuard(ReaderWriterLockSlim readerWriterLock)
    {
        _readerWriterLock = readerWriterLock;
        _readerWriterLock.EnterReadLock();
    }

    public virtual void Dispose()
    {
        _readerWriterLock.ExitReadLock();
    }
}
