using System;
using System.IO;

namespace BeMusicSeeker.Models.LR2;

/// <summary>
/// LR2 song DB の application-owned data uninstall を transaction 単位で実行します。
/// </summary>
internal sealed class Lr2ApplicationDataUninstallStore : IApplicationDataUninstallStore
{
    public void Uninstall(string songDbPath)
    {
        if (string.IsNullOrWhiteSpace(songDbPath) || !File.Exists(songDbPath))
        {
            return;
        }

        bool unlockAfterOperation = !LR2SongDBExtended.IsProcessLockEnteredByCurrentThread();
        if (!LR2SongDBExtended.Lock(new TimeSpan(0, 1, 0)))
        {
            throw new TimeoutException(BeMusicSeeker.Properties.Resources.Msg_error_timeout_dblock_uninstall);
        }
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            string savepoint = songDb.SaveTransactionPoint();
            try
            {
                songDb.Uninstall();
                songDb.Commit();
            }
            catch (Exception)
            {
                songDb.RollbackTo(savepoint);
                throw;
            }
        }
        finally
        {
            if (unlockAfterOperation)
            {
                LR2SongDBExtended.Unlock();
            }
        }
    }
}
