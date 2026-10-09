using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Reads the persisted playlist output scope for LR2 synchronization.  The
/// database handle and playlist rows stay inside this catalog owner; callers
/// receive only an immutable path snapshot.
/// </summary>
internal sealed class Lr2ManagedPlaylistOutputScopeOwner
{
    private readonly BmsLibraryDbGateway dbGateway;

    internal Lr2ManagedPlaylistOutputScopeOwner(BmsLibraryDbGateway dbGateway)
    {
        this.dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));
    }

    /// <summary>現在の正本と適用済み配置が指す管理領域を短い読取り接続で捕捉します。接続は結果返却前に閉じます。</summary>
    /// <param name="options">受理済み操作の設定入力。</param>
    /// <param name="throwOnFailure">交差保護のために読取り失敗を呼出元へ返す場合はtrue。従来の同期準備では不完全結果を返します。</param>
    /// <returns>管理領域と読取り完了状態。</returns>
    internal Lr2SongDbSyncAppManagedOutputScope Capture(BmsLibraryOptionsSnapshot options, bool throwOnFailure = false)
    {
        if (options == null)
        {
            throw new InvalidOperationException("BMS library options snapshot provider returned null.");
        }

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using LR2SongDBExtended songDb = dbGateway.OpenSongDbReadOnly();
            string playlistTableName = SQLiteTable<LR2SongDBExtended.playlist>.GetTableName();
            long playlistTableExists = songDb.ExecuteScalar<long>(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = ?;",
                playlistTableName);
            if (playlistTableExists == 0)
            {
                return new Lr2SongDbSyncAppManagedOutputScope([], [], [], isComplete: true);
            }

            foreach (BMSTable table in songDb.Table<BMSTable>())
            {
                string outputDirectory = ResolveManagedPlaylistOutputDirectory(table, options);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    directories.Add(outputDirectory);
                }
            }
        }
        catch when (!throwOnFailure)
        {
            return new Lr2SongDbSyncAppManagedOutputScope([], [], [], isComplete: false);
        }

        return new Lr2SongDbSyncAppManagedOutputScope(
            [.. directories],
            [],
            [],
            isComplete: true);
    }

    private static string ResolveManagedPlaylistOutputDirectory(
        BMSTable table,
        BmsLibraryOptionsSnapshot options)
    {
        if (table == null || options == null)
        {
            return null;
        }

        string outputDir;
        try
        {
            outputDir = table.Output_dir;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NullReferenceException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return null;
        }

        try
        {
            return SafeFullPathOrOriginal(BMSPlaylist.GetCustomFolderOutputDirectory(
                table,
                options.LR2CustomFolderOutputBaseDir,
                options.LR2CustomFolderOutputBaseDirRootType,
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories(options.LR2CustomFolderAdditionalOutputBaseDirs)));
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static string SafeFullPathOrOriginal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return path;
        }
    }
}
