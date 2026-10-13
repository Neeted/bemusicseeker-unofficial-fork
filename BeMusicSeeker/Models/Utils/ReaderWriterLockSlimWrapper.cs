using System;
using System.Threading;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// 実ロックの取得・解放と観測用の回数を管理します。表示への変更通知の失敗では、取得済みロックや解放の結果を変更しません。
/// </summary>
public class ReaderWriterLockSlimWrapper(LockRecursionPolicy recursionPolicy = LockRecursionPolicy.SupportsRecursion) : ObservableObject
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

    // 高頻度の表示通知故障をwrapperごとに一回だけ診断し、例外履歴は保持しません。
    private int lockCountNotificationFailureReported;

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

    /// <summary>実ロックの読取り回数です。値の確定は必須で、表示通知は任意です。</summary>
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
                NotifyLockCountBestEffort(nameof(LockingReadCount));
            }
        }
    }

    /// <summary>実ロックの書込み相当（昇格可能読取りを含む）回数です。値の確定は必須で、表示通知は任意です。</summary>
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
                NotifyLockCountBestEffort(nameof(LockingWriteCount));
            }
        }
    }

    /// <summary>昇格回数の観測値です。既存APIの計上区分を保ち、値の確定と任意の表示通知を分離します。</summary>
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
                NotifyLockCountBestEffort(nameof(LockingUpgradeCount));
            }
        }
    }

    private void NotifyLockCountBestEffort(string propertyName)
    {
        try
        {
            RaisePropertyChanged(propertyName);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref lockCountNotificationFailureReported, 1) == 0)
            {
                try
                {
                    NLogWrapper.FileLogger?.Warn(exception, $"Lock count display notification failed: property={propertyName}");
                }
                catch
                {
                    // 通知もその診断も、実Enter/Exitと確定済みcountを失敗へ変えません。
                }
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
        lock (lockThis)
        {
            LockingReadCount++;
        }
    }

    public void EnterUpgradeableReadLock()
    {
        readerWriterLockSlim.EnterUpgradeableReadLock();
        lock (lockThis)
        {
            LockingWriteCount++;
        }
    }

    public void EnterWriteLock()
    {
        readerWriterLockSlim.EnterWriteLock();
        lock (lockThis)
        {
            LockingWriteCount++;
        }
    }

    public void ExitReadLock()
    {
        readerWriterLockSlim.ExitReadLock();
        lock (lockThis)
        {
            LockingReadCount--;
        }
    }

    public void ExitUpgradeableReadLock()
    {
        readerWriterLockSlim.ExitUpgradeableReadLock();
        lock (lockThis)
        {
            LockingWriteCount--;
        }
    }

    public void ExitWriteLock()
    {
        readerWriterLockSlim.ExitWriteLock();
        lock (lockThis)
        {
            LockingWriteCount--;
        }
    }

    public bool TryEnterReadLock(int millisecondsTimeout)
    {
        bool entered = readerWriterLockSlim.TryEnterReadLock(millisecondsTimeout);
        if (entered)
        {
            lock (lockThis)
            {
                LockingReadCount++;
            }
        }
        return entered;
    }

    public bool TryEnterReadLock(TimeSpan timeout)
    {
        bool entered = readerWriterLockSlim.TryEnterReadLock(timeout);
        if (entered)
        {
            lock (lockThis)
            {
                LockingReadCount++;
            }
        }
        return entered;
    }

    public bool TryEnterUpgradeableReadLock(int millisecondsTimeout)
    {
        bool entered = readerWriterLockSlim.TryEnterUpgradeableReadLock(millisecondsTimeout);
        if (entered)
        {
            lock (lockThis)
            {
                LockingWriteCount++;
            }
        }
        return entered;
    }

    public bool TryEnterUpgradeableReadLock(TimeSpan timeout)
    {
        bool entered = readerWriterLockSlim.TryEnterUpgradeableReadLock(timeout);
        if (entered)
        {
            lock (lockThis)
            {
                LockingWriteCount++;
            }
        }
        return entered;
    }

    public bool TryEnterWriteLock(int millisecondsTimeout)
    {
        bool entered = readerWriterLockSlim.TryEnterWriteLock(millisecondsTimeout);
        if (entered)
        {
            lock (lockThis)
            {
                LockingWriteCount++;
            }
        }
        return entered;
    }

    public bool TryEnterWriteLock(TimeSpan timeout)
    {
        bool entered = readerWriterLockSlim.TryEnterWriteLock(timeout);
        if (entered)
        {
            lock (lockThis)
            {
                LockingWriteCount++;
            }
        }
        return entered;
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
