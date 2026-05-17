using System;
using System.Threading;
using Livet;
using Ribbit.Threading;

namespace BeMusicSeeker.Models.Utils;

public class ReaderWriterLockSlimWrapper(LockRecursionPolicy recursionPolicy = LockRecursionPolicy.SupportsRecursion) : NotificationObject
{
    private class ReaderGuard : Ribbit.Threading.ReaderGuard
    {
        private readonly Action onExitReadLock;

        public ReaderGuard(ReaderWriterLockSlim readerWriterLock, Action onEnterReadLock = null, Action onExitReadLock = null)
            : base(readerWriterLock)
        {
            this.onExitReadLock = onExitReadLock;
            onEnterReadLock?.Invoke();
        }

        public override void Dispose()
        {
            base.Dispose();
            onExitReadLock?.Invoke();
        }
    }

    private class WriterGuard : Ribbit.Threading.WriterGuard
    {
        private readonly Action onExitWriteLock;

        public WriterGuard(ReaderWriterLockSlim readerWriterLock, Action onEnterWriteLock = null, Action onExitWriteLock = null)
            : base(readerWriterLock)
        {
            this.onExitWriteLock = onExitWriteLock;
            onEnterWriteLock?.Invoke();
        }

        public override void Dispose()
        {
            base.Dispose();
            onExitWriteLock?.Invoke();
        }
    }

    private class UpgradeableGuard : Ribbit.Threading.UpgradeableGuard
    {
        private class UpgradedGuardExt : UpgradedGuard
        {
            public UpgradedGuardExt(UpgradeableGuard parentGuard, Action onEnterWriteLock = null, Action onExitWriteLock = null)
                : base(parentGuard)
            {
                _writerLock = new WriterGuard(parentGuard._readerWriterLock, onEnterWriteLock, onExitWriteLock);
            }
        }

        private readonly Action onExitUpgradeLock;

        private readonly Action onEnterWriteLock;

        private readonly Action onExitWriteLock;

        public UpgradeableGuard(ReaderWriterLockSlim readerWriterLock, Action onEnterUpgradeLock = null, Action onExitUpgradeLock = null, Action onEnterWriteLock = null, Action onExitWriteLock = null)
            : base(readerWriterLock)
        {
            this.onExitUpgradeLock = onExitUpgradeLock;
            this.onEnterWriteLock = onEnterWriteLock;
            this.onExitWriteLock = onExitWriteLock;
            onEnterUpgradeLock?.Invoke();
        }

        public override void Dispose()
        {
            base.Dispose();
            onExitUpgradeLock?.Invoke();
        }

        public override IDisposable UpgradeToWriterLock()
        {
            _upgradedLock ??= new UpgradedGuardExt(this, onEnterWriteLock, onExitWriteLock);
            return _upgradedLock;
        }
    }

    private readonly ReaderWriterLockSlim readerWriterLockSlim = new(recursionPolicy);

    private readonly object lockThis = new();

    private uint _LockingReadCount;

    private uint _LockingWriteCount;

    private uint _LockingUpgradeCount;

    public int CurrentReadCount => readerWriterLockSlim.CurrentReadCount;

    public bool IsReadLockHeld => readerWriterLockSlim.IsReadLockHeld;

    public bool IsUpgradeableReadLockHeld => readerWriterLockSlim.IsUpgradeableReadLockHeld;

    public bool IsWriteLockHeld => readerWriterLockSlim.IsWriteLockHeld;

    public LockRecursionPolicy RecursionPolicy => readerWriterLockSlim.RecursionPolicy;

    public int RecursiveReadCount => readerWriterLockSlim.RecursiveReadCount;

    public int RecursiveUpgradeCount => readerWriterLockSlim.RecursiveUpgradeCount;

    public int RecursiveWriteCount => readerWriterLockSlim.RecursiveWriteCount;

    public int WaitingReadCount => readerWriterLockSlim.WaitingReadCount;

    public int WaitingUpgradeCount => readerWriterLockSlim.WaitingUpgradeCount;

    public int WaitingWriteCount => readerWriterLockSlim.WaitingWriteCount;

    public uint LockingReadCount
    {
        get
        {
            return _LockingReadCount;
        }
        private set
        {
            if (_LockingReadCount != value)
            {
                _LockingReadCount = value;
                RaisePropertyChanged("LockingReadCount");
            }
        }
    }

    public uint LockingWriteCount
    {
        get
        {
            return _LockingWriteCount;
        }
        private set
        {
            if (_LockingWriteCount != value)
            {
                _LockingWriteCount = value;
                RaisePropertyChanged("LockingWriteCount");
            }
        }
    }

    public uint LockingUpgradeCount
    {
        get
        {
            return _LockingUpgradeCount;
        }
        private set
        {
            if (_LockingUpgradeCount != value)
            {
                _LockingUpgradeCount = value;
                RaisePropertyChanged("LockingUpgradeCount");
            }
        }
    }

    public void Dispose()
    {
        readerWriterLockSlim.Dispose();
    }

    public void EnterReadLock()
    {
        readerWriterLockSlim.EnterReadLock();
    }

    public void EnterUpgradeableReadLock()
    {
        readerWriterLockSlim.EnterUpgradeableReadLock();
    }

    public void EnterWriteLock()
    {
        readerWriterLockSlim.EnterWriteLock();
    }

    public void ExitReadLock()
    {
        readerWriterLockSlim.ExitReadLock();
    }

    public void ExitUpgradeableReadLock()
    {
        readerWriterLockSlim.ExitUpgradeableReadLock();
    }

    public void ExitWriteLock()
    {
        readerWriterLockSlim.ExitWriteLock();
    }

    public bool TryEnterReadLock(int millisecondsTimeout)
    {
        return readerWriterLockSlim.TryEnterReadLock(millisecondsTimeout);
    }

    public bool TryEnterReadLock(TimeSpan timeout)
    {
        return readerWriterLockSlim.TryEnterReadLock(timeout);
    }

    public bool TryEnterUpgradeableReadLock(int millisecondsTimeout)
    {
        return readerWriterLockSlim.TryEnterUpgradeableReadLock(millisecondsTimeout);
    }

    public bool TryEnterUpgradeableReadLock(TimeSpan timeout)
    {
        return readerWriterLockSlim.TryEnterUpgradeableReadLock(timeout);
    }

    public bool TryEnterWriteLock(int millisecondsTimeout)
    {
        return readerWriterLockSlim.TryEnterWriteLock(millisecondsTimeout);
    }

    public bool TryEnterWriteLock(TimeSpan timeout)
    {
        return readerWriterLockSlim.TryEnterWriteLock(timeout);
    }

    public Ribbit.Threading.ReaderGuard GetReaderGuard()
    {
        return new ReaderGuard(readerWriterLockSlim, delegate
        {
            lock (lockThis)
            {
                LockingReadCount++;
            }
        }, delegate
        {
            lock (lockThis)
            {
                LockingReadCount--;
            }
        });
    }

    public Ribbit.Threading.WriterGuard GetWriterGuard()
    {
        return new WriterGuard(readerWriterLockSlim, delegate
        {
            lock (lockThis)
            {
                LockingWriteCount++;
            }
        }, delegate
        {
            lock (lockThis)
            {
                LockingWriteCount--;
            }
        });
    }

    public Ribbit.Threading.UpgradeableGuard GetUpgradeableGuard()
    {
        return new UpgradeableGuard(readerWriterLockSlim, delegate
        {
            lock (lockThis)
            {
                LockingWriteCount++;
            }
        }, delegate
        {
            lock (lockThis)
            {
                LockingWriteCount--;
            }
        }, delegate
        {
            lock (lockThis)
            {
                LockingWriteCount++;
            }
        }, delegate
        {
            lock (lockThis)
            {
                LockingWriteCount--;
            }
        });
    }
}
