using System;
using System.Threading;

namespace Ribbit.Threading;

public class UpgradeableGuard : IDisposable
{
    protected class UpgradedGuard : IDisposable
    {
        private readonly UpgradeableGuard _parentGuard;

        protected WriterGuard _writerLock;

        public UpgradedGuard(UpgradeableGuard parentGuard)
        {
            _parentGuard = parentGuard;
            _writerLock = new WriterGuard(_parentGuard._readerWriterLock);
        }

        public void Dispose()
        {
            _writerLock.Dispose();
            _parentGuard._upgradedLock = null;
        }
    }

    protected readonly ReaderWriterLockSlim _readerWriterLock;

    protected UpgradedGuard _upgradedLock;

    public UpgradeableGuard(ReaderWriterLockSlim readerWriterLock)
    {
        _readerWriterLock = readerWriterLock;
        _readerWriterLock.EnterUpgradeableReadLock();
    }

    public virtual IDisposable UpgradeToWriterLock()
    {
        _upgradedLock ??= new UpgradedGuard(this);
        return _upgradedLock;
    }

    public virtual void Dispose()
    {
        _upgradedLock?.Dispose();
        _readerWriterLock.ExitUpgradeableReadLock();
    }
}
