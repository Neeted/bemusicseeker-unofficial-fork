using System;
using System.IO;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.LR2;

/// <summary>
/// LR2 song DB の application-owned data uninstall を transaction 単位で実行します。
/// </summary>
internal sealed class Lr2ApplicationDataUninstallStore : IApplicationDataUninstallStore
{
    private readonly ApplicationDatabaseScope databaseScope;

    /// <summary>アンインストールと接続に同じ書込み排他を適用するスコープを受け取ります。</summary>
    internal Lr2ApplicationDataUninstallStore(ApplicationDatabaseScope databaseScope)
    {
        this.databaseScope = databaseScope ?? throw new ArgumentNullException(nameof(databaseScope));
    }

    public void Uninstall(string songDbPath)
    {
        if (string.IsNullOrWhiteSpace(songDbPath) || !File.Exists(songDbPath))
        {
            return;
        }

        using IDisposable lease = databaseScope.TryEnterSong(new TimeSpan(0, 1, 0));
        if (lease == null)
        {
            throw new TimeoutException(BeMusicSeeker.Properties.Resources.Msg_error_timeout_dblock_uninstall);
        }

        // 外側の待機と接続内の再入は、それぞれの所有権をusingで戻します。
        using LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath);
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            songDb.Uninstall();
            songDb.Commit();
        }
        catch (Exception)
        {
            try
            {
                songDb.RollbackTo(savepoint);
            }
            catch (Exception rollbackException)
            {
                try
                {
                    NLogWrapper.FileLogger?.Warn(rollbackException, "application_data_uninstall_rollback_failed");
                }
                catch
                {
                    // 任意診断の失敗でアンインストールの元原因を置き換えません。
                }
            }
            throw;
        }
    }
}
