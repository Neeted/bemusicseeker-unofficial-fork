using System.Threading;

namespace Ribbit.Threading;

public static class ReaderWriterLockSlimExt
{
	public static ReaderGuard ReaderGuard(this ReaderWriterLockSlim rwlock)
	{
		return new ReaderGuard(rwlock);
	}

	public static WriterGuard WriterGuard(this ReaderWriterLockSlim rwlock)
	{
		return new WriterGuard(rwlock);
	}

	public static UpgradeableGuard UpgradeableGuard(this ReaderWriterLockSlim rwlock)
	{
		return new UpgradeableGuard(rwlock);
	}
}
