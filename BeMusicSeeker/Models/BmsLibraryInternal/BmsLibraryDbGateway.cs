using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// chart_info backfill 中に補完した MD5/SHA-256 対応です。
/// BMSFile へ SHA-256 を反映する前に DB へ保存できるよう、所有オブジェクトとは分離しています。
/// </summary>
internal sealed class ChartDigestBackfillEntry
{
    /// <summary>
    /// 保存する digest 対応を作成します。
    /// </summary>
    /// <param name="md5">LR2 song.hash と対応する MD5。</param>
    /// <param name="sha256">譜面ファイル全体の SHA-256。</param>
    public ChartDigestBackfillEntry(string md5, string sha256)
    {
        Md5 = md5 ?? string.Empty;
        Sha256 = sha256 ?? string.Empty;
    }

    /// <summary>
    /// LR2 song.hash と対応する MD5 です。
    /// </summary>
    public string Md5 { get; }

    /// <summary>
    /// 譜面ファイル全体の SHA-256 です。
    /// </summary>
    public string Sha256 { get; }
}

internal sealed class BmsLibraryDbGateway
{
    private const int ChartInfoLookupChunkSize = 500;

    private const string ChartDigestMapUpsertSql =
        "INSERT OR REPLACE INTO chart_digest_map (md5, sha256) VALUES (?, ?);";

    private const string ChartInfoUpsertSql =
        "INSERT OR REPLACE INTO chart_info ("
        + "sha256, md5, charthash, level, difficulty, difficulty_defined, mainbpm, maxbpm, minbpm, length, mode, judge, feature, notes, n, ln, s, ls, total, total_defined, density, peakdensity, enddensity, distribution, speedchange, speedchange_count, lanenotes, parser_version, updated_at"
        + ") VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);";

    private const string ChartInfoParseFailureUpsertSql =
        "INSERT OR REPLACE INTO chart_info_parse_failure ("
        + "md5, sha256, path, parser_version, failure_kind, exception_type, message, parse_timeout_ms, updated_at"
        + ") VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);";

    private const string ChartInfoParseFailureDeleteSql =
        "DELETE FROM chart_info_parse_failure WHERE md5 = ?;";

    private const string ChartInfoColumnList =
        "sha256, md5, charthash, level, difficulty, difficulty_defined, mainbpm, maxbpm, minbpm, length, mode, judge, feature, notes, n, ln, s, ls, total, total_defined, density, peakdensity, enddensity, distribution, speedchange, speedchange_count, lanenotes, parser_version, updated_at";

    internal const string BmsonAppSchemaVersionName = "bmson_app_schema";

    internal const int CurrentBmsonAppSchemaVersion = 1;

    internal const string ChartInfoSchemaVersionName = "chart_info_schema";

    internal const int CurrentChartInfoSchemaVersion = 4;

    internal const int CurrentChartInfoParserVersion = 20;

    internal const int ChartInfoMetadataBundleFormatVersion = 1;

    public string SongDbPath { get; }

    public string ScoreDbPath { get; }

    public BmsLibraryDbGateway(string songDbPath, string scoreDbPath = null)
    {
        SongDbPath = songDbPath ?? throw new ArgumentNullException(nameof(songDbPath));
        ScoreDbPath = scoreDbPath;
    }

    public LR2SongDBExtended OpenSongDb()
    {
        return new LR2SongDBExtended(SongDbPath);
    }

    public LR2SongDBExtended OpenSongDbReadOnly()
    {
        return new LR2SongDBExtended(SongDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, acquireProcessLock: false);
    }

    public LR2ScoreDBExtended OpenScoreDb()
    {
        if (string.IsNullOrWhiteSpace(ScoreDbPath))
        {
            throw new InvalidOperationException("Score DB path is not configured.");
        }
        return new LR2ScoreDBExtended(ScoreDbPath);
    }

    public LR2ScoreDBExtended OpenScoreDbReadOnly()
    {
        if (string.IsNullOrWhiteSpace(ScoreDbPath))
        {
            throw new InvalidOperationException("Score DB path is not configured.");
        }
        return new LR2ScoreDBExtended(ScoreDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, acquireProcessLock: false);
    }

    public void ExecuteSongDbTransaction(Action<LR2SongDBExtended> action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            action(songDb);
            songDb.Commit();
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    public void DeleteInstallRows(IEnumerable<string> installPaths)
    {
        List<string> paths = (installPaths ?? Enumerable.Empty<string>()).Where((string path) => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            foreach (string path in paths)
            {
                songDb.Delete<LR2SongDBExtended.install>(path);
            }
        });
    }

    public void UpsertInstallRows(IEnumerable<BMSPackage> packages)
    {
        List<BMSPackage> items = (packages ?? Enumerable.Empty<BMSPackage>()).Where((BMSPackage package) => package != null && !string.IsNullOrWhiteSpace(package.path)).ToList();
        if (items.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            songDb.InsertAll(items, typeof(LR2SongDBExtended.install));
        });
    }

    public void UpsertSongs(IEnumerable<BMSFile> bmsFiles)
    {
        List<BMSFile> files = (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).ToList();
        if (files.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureBmsonSchema(songDb);
            foreach (BMSFile file in files)
            {
                Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(file);
                string previousHash = GetSongHashByPath(songDb, file.path);
                songDb.InsertOrReplace(file, typeof(LR2SongDB.song));
                UpsertChartDigest(songDb, file);
                DeleteChartDigestIfOrphaned(songDb, previousHash, file.hash);
            }
        });
    }

    internal static void CommitFileScanDiffChunk(LR2SongDBExtended songDb, FileScanDiffCommitChunk chunk)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        if (chunk == null || !chunk.HasItems)
        {
            return;
        }

        EnsureBmsonSchema(songDb);
        EnsureChartInfoSchema(songDb);
        songDb.CreateTable<LR2SongDBExtended.maintenance>();
        foreach (string deletedPath in chunk.DeletedBmsPaths)
        {
            if (string.IsNullOrWhiteSpace(deletedPath))
            {
                continue;
            }
            string deletedHash = GetSongHashByPath(songDb, deletedPath);
            songDb.Delete<LR2SongDB.song>(deletedPath);
            songDb.Delete<LR2SongDBExtended.maintenance>(deletedPath);
            DeleteChartDigestIfOrphaned(songDb, deletedHash);
        }
        foreach (BMSFile addedFile in chunk.AddedBmsFiles)
        {
            if (addedFile == null)
            {
                continue;
            }
            Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(addedFile);
            string previousHash = GetSongHashByPath(songDb, addedFile.path);
            songDb.InsertOrReplace(addedFile, typeof(LR2SongDB.song));
            UpsertChartDigest(songDb, addedFile);
            DeleteChartDigestIfOrphaned(songDb, previousHash, addedFile.hash);
        }
        foreach (string deletedBmsonPath in chunk.DeletedBmsonPaths)
        {
            if (!string.IsNullOrWhiteSpace(deletedBmsonPath))
            {
                songDb.Delete<LR2SongDBExtended.bmson_song>(deletedBmsonPath);
                songDb.Delete<LR2SongDBExtended.maintenance>(deletedBmsonPath);
            }
        }
        foreach (LR2SongDBExtended.bmson_song addedBmsonSong in chunk.UpsertBmsonSongs)
        {
            if (addedBmsonSong != null)
            {
                songDb.InsertOrReplace(addedBmsonSong, typeof(LR2SongDBExtended.bmson_song));
            }
        }
        foreach (BMSFileMaintenanceInfo maintenanceInfo in chunk.MaintenanceInfoRows)
        {
            if (maintenanceInfo != null && !string.IsNullOrWhiteSpace(maintenanceInfo.path))
            {
                songDb.InsertOrReplace(maintenanceInfo, typeof(LR2SongDBExtended.maintenance));
            }
        }
        UpsertChartInfoBackfillChunk(
            songDb,
            Enumerable.Empty<ChartDigestBackfillEntry>(),
            chunk.ChartInfoRows,
            chunk.ParseFailureRows,
            chunk.ParseFailureDeleteMd5s);
    }

    public void DeleteSongsAndMaintenance(IEnumerable<BMSFile> bmsFiles)
    {
        List<BMSFile> files = (bmsFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null && !string.IsNullOrWhiteSpace(file.path)).ToList();
        if (files.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureBmsonSchema(songDb);
            foreach (BMSFile file in files)
            {
                songDb.Delete<LR2SongDB.song>(file.path);
                songDb.Delete<LR2SongDBExtended.maintenance>(file.path);
            }
            DeleteChartDigestsIfOrphaned(songDb, files.Select((BMSFile file) => file.hash));
        });
    }

    public void DeleteBmsonSongs(IEnumerable<LR2SongDBExtended.bmson_song> songs)
    {
        List<LR2SongDBExtended.bmson_song> entries = (songs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
            .Where((LR2SongDBExtended.bmson_song song) => song != null && !string.IsNullOrWhiteSpace(song.path))
            .ToList();
        if (entries.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate(LR2SongDBExtended songDb)
        {
            bool hasMaintenanceTable = TableExists(songDb, SQLiteTable<LR2SongDBExtended.maintenance>.GetTableName());
            foreach (LR2SongDBExtended.bmson_song entry in entries)
            {
                songDb.Delete<LR2SongDBExtended.bmson_song>(entry.path);
                if (hasMaintenanceTable)
                {
                    songDb.Delete<LR2SongDBExtended.maintenance>(entry.path);
                }
            }
        });
    }

    public void UpsertMaintenanceInfos(IEnumerable<BMSFileMaintenanceInfo> maintenanceInfos)
    {
        List<BMSFileMaintenanceInfo> entries = (maintenanceInfos ?? Enumerable.Empty<BMSFileMaintenanceInfo>())
            .Where((BMSFileMaintenanceInfo info) => info != null && !string.IsNullOrWhiteSpace(info.path))
            .ToList();
        if (entries.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            foreach (BMSFileMaintenanceInfo entry in entries)
            {
                songDb.InsertOrReplace(entry, typeof(LR2SongDBExtended.maintenance));
            }
        });
    }

    public int DeleteMaintenanceRows(IEnumerable<string> paths)
    {
        List<string> entries = (paths ?? Enumerable.Empty<string>())
            .Where((string path) => !string.IsNullOrWhiteSpace(path))
            .Select((string path) => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entries.Count == 0)
        {
            return 0;
        }
        int deleted = 0;
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            foreach (string path in entries)
            {
                deleted += songDb.Delete<LR2SongDBExtended.maintenance>(path);
            }
        });
        return deleted;
    }

    public ScoreTableLoadResult LoadScoresAndPlayerId()
    {
        ScoreTableLoadResult result = new ScoreTableLoadResult();
        if (string.IsNullOrWhiteSpace(ScoreDbPath))
        {
            return result;
        }
        using LR2ScoreDBExtended scoreDb = OpenScoreDbReadOnly();
        result.ReadOnly = scoreDb.IsReadOnlyConnection;
        result.DbLockWaitMs = scoreDb.ProcessLockWaitMs;
        result.Scores.AddRange(scoreDb.Table<BMSScore>().ToList());
        result.LR2Id = scoreDb.Table<LR2ScoreDB.player>().ToList().FirstOrDefault()?.irid ?? 0;
        return result;
    }

    public List<BMSPackage> LoadInstallPackages()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        return songDb.Table<BMSPackage>().ToList();
    }

    public PlaylistEntriesHydrationLoadResult LoadStartupPlaylistEntries()
    {
        PlaylistEntriesHydrationLoadResult result = new PlaylistEntriesHydrationLoadResult();
        using LR2SongDBExtended songDb = OpenSongDbReadOnly();
        result.ReadOnly = songDb.IsReadOnlyConnection;
        result.DbLockWaitMs = songDb.ProcessLockWaitMs;
        string tableName = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string playlistIdColumn = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.playlist_id);
        string sql =
            "SELECT "
            + string.Join(", ", new[]
            {
                playlistIdColumn,
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.md5),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.sha256),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.level),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.title),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.artist),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.folder),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.lr2_bmsid),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.url),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.url_diff),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.name_diff),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.org_md5),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.adddate),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.comment),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.memo),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.is_removed)
            })
            + " FROM " + tableName
            + " WHERE " + playlistIdColumn + " IS NOT NULL;";
        Stopwatch stopwatch = Stopwatch.StartNew();
        LR2SongDBExtended.SQLiteCommandExtended command = (LR2SongDBExtended.SQLiteCommandExtended)songDb.CreateCommand(sql);
        command.ForEachRawValueAsString(delegate (string[] values)
        {
            if (values == null || values.Length < 16 || !TryParseNullableInt(values[0], out int playlistId))
            {
                return;
            }
            result.Entries.Add(BMSTableEntry.CreateHydratedPlaylistEntry(
                playlistId,
                values[1],
                values[2],
                ParseNullableDouble(values[3]),
                values[4],
                values[5],
                values[6],
                values[7],
                values[8],
                values[9],
                values[10],
                values[11],
                ParseNullableDateTime(values[12]),
                values[13],
                values[14],
                ParseBoolean(values[15])));
        });
        stopwatch.Stop();
        result.DbReadMs = stopwatch.ElapsedMilliseconds;
        result.MaterializeMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    private static bool TryParseNullableInt(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static double? ParseNullableDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double invariantValue))
        {
            return invariantValue;
        }
        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out double currentValue))
        {
            return currentValue;
        }
        return null;
    }

    private static DateTime? ParseNullableDateTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
        {
            try
            {
                return new DateTime(ticks);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime invariantValue))
        {
            return invariantValue;
        }
        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out DateTime currentValue))
        {
            return currentValue;
        }
        return null;
    }

    private static bool ParseBoolean(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        if (bool.TryParse(value, out bool boolValue))
        {
            return boolValue;
        }
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integerValue))
        {
            return integerValue != 0;
        }
        return false;
    }

    public void ReplaceSongPathWithMaintenance(BMSFile bmsFile, string oldPath)
    {
        if (bmsFile == null)
        {
            throw new ArgumentNullException(nameof(bmsFile));
        }
        if (string.IsNullOrWhiteSpace(oldPath))
        {
            throw new ArgumentNullException(nameof(oldPath));
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureBmsonSchema(songDb);
            songDb.Delete<LR2SongDB.song>(oldPath);
            songDb.Delete<LR2SongDBExtended.maintenance>(oldPath);
            BMSFileMaintenanceInfo maintenanceInfo = bmsFile.HasValidMaintenanceInfoSnapshot
                ? bmsFile.TryGetMaintenanceInfoWithoutCreating()
                : null;
            if (maintenanceInfo != null)
            {
                songDb.InsertOrReplace(maintenanceInfo, typeof(LR2SongDBExtended.maintenance));
            }
            Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(bmsFile);
            songDb.InsertOrReplace(bmsFile, typeof(LR2SongDB.song));
            UpsertChartDigest(songDb, bmsFile);
        });
    }

    public void EnsureBmsonSchema()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            EnsureBmsonSchema(songDb);
            songDb.Commit();
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    /// <summary>
    /// chart_info テーブルと関連 index を作成または修復します。
    /// </summary>
    public void EnsureChartInfoSchema()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            EnsureChartInfoSchema(songDb);
            songDb.Commit();
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    public void CompleteBmsonStartupMigration()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            CompleteBmsonStartupMigration(songDb);
            songDb.Commit();
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    /// <summary>
    /// chart_info schema version が現行かどうかを返します。
    /// </summary>
    /// <returns>現行 schema であれば true。</returns>
    public bool IsChartInfoSchemaCurrent()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        return IsChartInfoSchemaCurrent(songDb);
    }

    /// <summary>
    /// chart_info schema version を現行として記録します。
    /// </summary>
    public void MarkChartInfoSchemaCurrent()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            SetChartInfoSchemaVersion(songDb, CurrentChartInfoSchemaVersion);
            songDb.Commit();
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    public Dictionary<string, string> LoadChartDigestMap()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        return LoadChartDigestMap(songDb);
    }

    internal Dictionary<string, string> LoadChartDigestMap(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        string tableName = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        if (!TableExists(songDb, tableName))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartDigestQueryRow item in songDb.Query<ChartDigestQueryRow>("SELECT md5, sha256 FROM " + tableName + " WHERE md5 IS NOT NULL AND TRIM(md5) <> '' AND sha256 IS NOT NULL AND TRIM(sha256) <> '';"))
        {
            if (item != null && !string.IsNullOrWhiteSpace(item.md5) && !string.IsNullOrWhiteSpace(item.sha256))
            {
                dictionary[item.md5] = item.sha256;
            }
        }
        return dictionary;
    }

    /// <summary>
    /// chart_info を sha256 keyed dictionary として読み込みます。
    /// </summary>
    /// <returns>sha256 をキーにした譜面解析メタデータ。</returns>
    public Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfoMap()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        EnsureChartInfoSchema(songDb);
        Dictionary<string, LR2SongDBExtended.chart_info> dictionary = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.chart_info item in songDb.Table<LR2SongDBExtended.chart_info>())
        {
            if (item != null && !string.IsNullOrWhiteSpace(item.sha256))
            {
                dictionary[item.sha256] = item;
            }
        }
        return dictionary;
    }

    public ChartInfoHydrationLoadResult LoadChartInfoHydrationData(TimeSpan parseTimeout)
    {
        ChartInfoHydrationLoadResult result = new ChartInfoHydrationLoadResult();
        using LR2SongDBExtended songDb = OpenSongDbReadOnly();
        result.ReadOnly = songDb.IsReadOnlyConnection;
        result.DbLockWaitMs = songDb.ProcessLockWaitMs;
        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (LR2SongDBExtended.chart_info item in songDb.Table<LR2SongDBExtended.chart_info>())
        {
            result.ChartInfoRows++;
            if (item != null && !string.IsNullOrWhiteSpace(item.sha256))
            {
                result.ChartInfoBySha256[item.sha256] = item;
            }
        }
        foreach (LR2SongDBExtended.chart_info_parse_failure row in songDb.Table<LR2SongDBExtended.chart_info_parse_failure>())
        {
            result.ParseFailureRows++;
            if (IsCurrentChartInfoParseFailure(row, parseTimeout))
            {
                result.CurrentParseFailuresByMd5[row.md5] = row;
            }
        }
        stopwatch.Stop();
        result.DbReadMs = stopwatch.ElapsedMilliseconds;
        result.MaterializeMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// 指定された SHA-256 だけに対応する chart_info を読み込みます。
    /// playlist の未所持行解決や inline chart_info で全件読み込みを避けるために使用します。
    /// </summary>
    /// <param name="sha256s">検索対象 SHA-256。</param>
    /// <returns>SHA-256 をキーにした chart_info。</returns>
    public Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfosBySha256(IEnumerable<string> sha256s)
    {
        List<string> keys = NormalizeChartInfoLookupKeys(sha256s);
        Dictionary<string, LR2SongDBExtended.chart_info> result = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0)
        {
            return result;
        }
        using LR2SongDBExtended songDb = OpenSongDb();
        EnsureChartInfoSchema(songDb);
        foreach (LR2SongDBExtended.chart_info row in QueryChartInfosByColumn(songDb, "sha256", keys, orderBySha256: false))
        {
            if (row != null && !string.IsNullOrWhiteSpace(row.sha256))
            {
                result[row.sha256] = row;
            }
        }
        return result;
    }

    public bool IsChartInfoMetadataBundleImportRecorded(string bundleSha256)
    {
        if (string.IsNullOrWhiteSpace(bundleSha256))
        {
            return false;
        }

        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            EnsureChartInfoSchema(songDb);
            bool imported = IsChartInfoMetadataBundleImported(songDb, BuildChartInfoMetadataBundleImportKey(bundleSha256));
            songDb.Commit();
            return imported;
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    /// <summary>
    /// 指定された MD5 だけに対応する chart_info を読み込みます。
    /// 同一 MD5 に複数 SHA-256 が存在する場合は SHA-256 昇順で最初の行を採用します。
    /// </summary>
    /// <param name="md5s">検索対象 MD5。</param>
    /// <returns>MD5 をキーにした chart_info。</returns>
    public Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfosByMd5(IEnumerable<string> md5s)
    {
        List<string> keys = NormalizeChartInfoLookupKeys(md5s);
        Dictionary<string, LR2SongDBExtended.chart_info> result = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0)
        {
            return result;
        }
        using LR2SongDBExtended songDb = OpenSongDb();
        EnsureChartInfoSchema(songDb);
        foreach (LR2SongDBExtended.chart_info row in QueryChartInfosByColumn(songDb, "md5", keys, orderBySha256: true))
        {
            if (row != null && !string.IsNullOrWhiteSpace(row.md5) && !result.ContainsKey(row.md5))
            {
                result[row.md5] = row;
            }
        }
        return result;
    }

    /// <summary>
    /// 現行 parser / timeout 条件で有効な chart_info 解析失敗記録を MD5 keyed dictionary として読み込みます。
    /// </summary>
    /// <param name="parseTimeout">今回の解析 timeout。</param>
    /// <returns>MD5 をキーにした解析失敗記録。</returns>
    public Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> LoadCurrentChartInfoParseFailureMap(TimeSpan parseTimeout)
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        EnsureChartInfoSchema(songDb);
        Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> result = new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.chart_info_parse_failure row in songDb.Table<LR2SongDBExtended.chart_info_parse_failure>())
        {
            if (IsCurrentChartInfoParseFailure(row, parseTimeout))
            {
                result[row.md5] = row;
            }
        }
        return result;
    }

    public ChartInfoBackfillCandidateSummary GetChartInfoBackfillCandidateSummary(TimeSpan parseTimeout)
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        EnsureBmsonSchema(songDb);
        EnsureChartInfoSchema(songDb);

        ChartInfoBackfillCandidateSummary summary = new ChartInfoBackfillCandidateSummary();
        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        string bmsonTable = SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName();
        Dictionary<string, string> digestByMd5 = LoadChartDigestMapForCandidateSummary(songDb);
        HashSet<string> anyChartInfoSha256 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> currentChartInfoSha256 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartInfoSummaryRow row in songDb.Query<ChartInfoSummaryRow>("SELECT sha256, parser_version FROM chart_info WHERE sha256 IS NOT NULL AND TRIM(sha256) <> '';"))
        {
            string sha256 = NormalizeLookupKey(row.sha256);
            if (string.IsNullOrWhiteSpace(sha256))
            {
                continue;
            }
            anyChartInfoSha256.Add(sha256);
            if (row.parser_version >= CurrentChartInfoParserVersion)
            {
                currentChartInfoSha256.Add(sha256);
            }
        }
        HashSet<string> currentParseFailureMd5 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartInfoParseFailureSummaryRow row in songDb.Query<ChartInfoParseFailureSummaryRow>("SELECT md5, parser_version, failure_kind, parse_timeout_ms FROM chart_info_parse_failure WHERE md5 IS NOT NULL AND TRIM(md5) <> '';"))
        {
            string md5 = NormalizeLookupKey(row.md5);
            if (string.IsNullOrWhiteSpace(md5))
            {
                continue;
            }
            if (IsCurrentChartInfoParseFailure(row.parser_version, row.failure_kind, row.parse_timeout_ms, parseTimeout))
            {
                currentParseFailureMd5.Add(md5);
            }
        }

        foreach (ChartInfoOwnerMd5Row row in songDb.Query<ChartInfoOwnerMd5Row>("SELECT hash AS md5 FROM " + songTable + " WHERE hash IS NOT NULL AND TRIM(hash) <> '';"))
        {
            string md5 = NormalizeLookupKey(row.md5);
            if (string.IsNullOrWhiteSpace(md5))
            {
                continue;
            }
            summary.BmsOwnerCount++;
            digestByMd5.TryGetValue(md5, out string sha256);
            ClassifyChartInfoBackfillCandidate(summary, md5, sha256, currentChartInfoSha256, anyChartInfoSha256, currentParseFailureMd5, missingDigestWhenShaMissing: true);
        }

        foreach (BmsonChartInfoOwnerRow row in songDb.Query<BmsonChartInfoOwnerRow>("SELECT md5, sha256 FROM " + bmsonTable + " WHERE path IS NOT NULL AND TRIM(path) <> '';"))
        {
            string md5 = NormalizeLookupKey(row.md5);
            string sha256 = NormalizeLookupKey(row.sha256);
            summary.BmsonOwnerCount++;
            ClassifyChartInfoBackfillCandidate(summary, md5, sha256, currentChartInfoSha256, anyChartInfoSha256, currentParseFailureMd5, missingDigestWhenShaMissing: true);
        }
        return summary;
    }

    public void UpsertChartInfoParseFailures(IEnumerable<LR2SongDBExtended.chart_info_parse_failure> rows)
    {
        List<LR2SongDBExtended.chart_info_parse_failure> sourceRows = NormalizeChartInfoParseFailureRows(rows);
        if (sourceRows.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureChartInfoSchema(songDb);
            foreach (LR2SongDBExtended.chart_info_parse_failure row in sourceRows)
            {
                ExecuteChartInfoParseFailureUpsert(songDb, row);
            }
        });
    }

    public void DeleteChartInfoParseFailuresByMd5(IEnumerable<string> md5s)
    {
        List<string> sourceMd5s = NormalizeChartInfoLookupKeys(md5s);
        if (sourceMd5s.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureChartInfoSchema(songDb);
            foreach (string md5 in sourceMd5s)
            {
                songDb.Execute(ChartInfoParseFailureDeleteSql, md5);
            }
        });
    }

    public List<LR2SongDBExtended.bmson_song> LoadBmsonSongs()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName()))
        {
            return new List<LR2SongDBExtended.bmson_song>();
        }
        return songDb.Table<LR2SongDBExtended.bmson_song>().ToList();
    }

    /// <summary>
    /// 解析済み chart_info 行を保存します。
    /// </summary>
    /// <param name="rows">保存する譜面解析メタデータ。</param>
    public void UpsertChartInfos(IEnumerable<LR2SongDBExtended.chart_info> rows)
    {
        List<LR2SongDBExtended.chart_info> sourceRows = (rows ?? Enumerable.Empty<LR2SongDBExtended.chart_info>())
            .Where((LR2SongDBExtended.chart_info row) => row != null && !string.IsNullOrWhiteSpace(row.sha256))
            .ToList();
        if (sourceRows.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureChartInfoSchema(songDb);
            foreach (LR2SongDBExtended.chart_info row in sourceRows)
            {
                ExecuteChartInfoUpsert(songDb, row);
            }
        });
    }

    /// <summary>
    /// chart_info backfill で使う app 独自 table を一度に準備します。
    /// chunk commit ごとに schema repair を繰り返さないため、backfill 開始時に呼びます。
    /// </summary>
    public void EnsureChartInfoBackfillSchema()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            EnsureBmsonSchema(songDb);
            EnsureChartInfoSchema(songDb);
            songDb.Commit();
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    public ChartInfoMetadataBundleImportResult ImportChartInfoMetadataBundle(string bundleDbPath, string bundleSha256)
    {
        if (string.IsNullOrWhiteSpace(bundleDbPath))
        {
            throw new ArgumentNullException(nameof(bundleDbPath));
        }
        if (!File.Exists(bundleDbPath))
        {
            throw new FileNotFoundException("chart_info metadata bundle was not found.", bundleDbPath);
        }
        if (string.IsNullOrWhiteSpace(bundleSha256))
        {
            throw new ArgumentNullException(nameof(bundleSha256));
        }
        bundleSha256 = bundleSha256.Trim().ToLowerInvariant();
        Stopwatch stopwatch = Stopwatch.StartNew();
        ChartInfoMetadataBundleImportResult result = new ChartInfoMetadataBundleImportResult
        {
            BundleSha256 = bundleSha256
        };
        using LR2SongDBExtended songDb = OpenSongDb();
        bool attached = false;
        string savepoint = null;
        Exception importException = null;
        try
        {
            songDb.Execute("ATTACH DATABASE ? AS bundle;", bundleDbPath);
            attached = true;
            savepoint = songDb.SaveTransactionPoint();
            EnsureChartInfoSchema(songDb);
            EnsureBmsonSchema(songDb);
            ChartInfoMetadataBundleManifest manifest = LoadChartInfoMetadataBundleManifest(songDb);
            result.BundleId = manifest.bundle_id;
            result.SourceChartInfoCount = Math.Max(0, manifest.chart_info_count);
            result.SourceDigestCount = Math.Max(0, manifest.chart_digest_count);

            string importKey = BuildChartInfoMetadataBundleImportKey(bundleSha256);
            if (IsChartInfoMetadataBundleImported(songDb, importKey))
            {
                result.Skipped = true;
                result.SkipReason = "already_imported";
                songDb.Commit();
                stopwatch.Stop();
                result.ElapsedMs = stopwatch.ElapsedMilliseconds;
                return result;
            }

            PrepareChartInfoMetadataImportTempTables(songDb);
            result.ImportedChartInfoCount = songDb.ExecuteScalar<int>("SELECT COUNT(1) FROM temp.chart_info_metadata_import_info_to_import;");
            result.ImportedDigestCount = songDb.ExecuteScalar<int>("SELECT COUNT(1) FROM temp.chart_info_metadata_import_digest_to_import;");
            result.FailureClearedCount = songDb.ExecuteScalar<int>("SELECT COUNT(1) FROM temp.chart_info_metadata_import_failure_to_clear;");

            songDb.Execute(
                "INSERT OR REPLACE INTO main.chart_info ("
                + ChartInfoColumnList
                + ") SELECT "
                + ChartInfoColumnList
                + " FROM temp.chart_info_metadata_import_info_to_import;");
            songDb.Execute(
                "INSERT OR IGNORE INTO main.chart_digest_map (md5, sha256) "
                + "SELECT md5, sha256 FROM temp.chart_info_metadata_import_digest_to_import;");
            songDb.Execute(
                "DELETE FROM main.chart_info_parse_failure WHERE md5 IN (SELECT md5 FROM temp.chart_info_metadata_import_failure_to_clear);");
            songDb.InsertOrReplace(new LR2SongDBExtended.chart_info_import_history
            {
                import_key = importKey,
                bundle_id = result.BundleId,
                bundle_sha256 = bundleSha256,
                parser_version = CurrentChartInfoParserVersion,
                chart_info_imported_count = result.ImportedChartInfoCount,
                chart_digest_imported_count = result.ImportedDigestCount,
                failure_cleared_count = result.FailureClearedCount,
                imported_at = DateTime.UtcNow
            }, typeof(LR2SongDBExtended.chart_info_import_history));
            songDb.Commit();
        }
        catch (Exception ex)
        {
            importException = ex;
            if (savepoint != null)
            {
                try
                {
                    songDb.RollbackTo(savepoint);
                }
                catch
                {
                }
            }
            throw;
        }
        finally
        {
            if (attached)
            {
                try
                {
                    songDb.Execute("DETACH DATABASE bundle;");
                }
                catch
                {
                    if (importException == null)
                    {
                        throw;
                    }
                }
            }
            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        }
        return result;
    }

    /// <summary>
    /// chart_info backfill の1 chunk 分を短い transaction で保存します。
    /// digest と chart_info をまとめて保存し、途中終了時は次回 backfill が未保存分だけを再開します。
    /// </summary>
    /// <param name="digestEntries">補完した MD5/SHA-256 対応。</param>
    /// <param name="rows">保存する譜面解析メタデータ。</param>
    /// <param name="parseFailureRows">保存する解析失敗記録。</param>
    /// <param name="parseFailureDeleteMd5s">削除する解析失敗記録の MD5。</param>
    public void UpsertChartInfoBackfillChunk(
        IEnumerable<ChartDigestBackfillEntry> digestEntries,
        IEnumerable<LR2SongDBExtended.chart_info> rows,
        IEnumerable<LR2SongDBExtended.chart_info_parse_failure> parseFailureRows = null,
        IEnumerable<string> parseFailureDeleteMd5s = null)
    {
        List<ChartDigestBackfillEntry> sourceDigestEntries = (digestEntries ?? Enumerable.Empty<ChartDigestBackfillEntry>())
            .Where((ChartDigestBackfillEntry entry) => entry != null && !string.IsNullOrWhiteSpace(entry.Md5) && !string.IsNullOrWhiteSpace(entry.Sha256))
            .ToList();
        List<LR2SongDBExtended.chart_info> sourceRows = (rows ?? Enumerable.Empty<LR2SongDBExtended.chart_info>())
            .Where((LR2SongDBExtended.chart_info row) => row != null && !string.IsNullOrWhiteSpace(row.sha256))
            .ToList();
        List<LR2SongDBExtended.chart_info_parse_failure> sourceParseFailureRows = NormalizeChartInfoParseFailureRows(parseFailureRows);
        List<string> sourceParseFailureDeleteMd5s = NormalizeChartInfoLookupKeys(parseFailureDeleteMd5s);
        if (sourceDigestEntries.Count == 0 && sourceRows.Count == 0 && sourceParseFailureRows.Count == 0 && sourceParseFailureDeleteMd5s.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            UpsertChartInfoBackfillChunk(songDb, sourceDigestEntries, sourceRows, sourceParseFailureRows, sourceParseFailureDeleteMd5s);
        });
    }

    internal static void UpsertChartInfoBackfillChunk(
        LR2SongDBExtended songDb,
        IEnumerable<ChartDigestBackfillEntry> digestEntries,
        IEnumerable<LR2SongDBExtended.chart_info> rows,
        IEnumerable<LR2SongDBExtended.chart_info_parse_failure> parseFailureRows = null,
        IEnumerable<string> parseFailureDeleteMd5s = null)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        List<ChartDigestBackfillEntry> sourceDigestEntries = (digestEntries ?? Enumerable.Empty<ChartDigestBackfillEntry>())
            .Where((ChartDigestBackfillEntry entry) => entry != null && !string.IsNullOrWhiteSpace(entry.Md5) && !string.IsNullOrWhiteSpace(entry.Sha256))
            .ToList();
        List<LR2SongDBExtended.chart_info> sourceRows = (rows ?? Enumerable.Empty<LR2SongDBExtended.chart_info>())
            .Where((LR2SongDBExtended.chart_info row) => row != null && !string.IsNullOrWhiteSpace(row.sha256))
            .ToList();
        List<LR2SongDBExtended.chart_info_parse_failure> sourceParseFailureRows = NormalizeChartInfoParseFailureRows(parseFailureRows);
        List<string> sourceParseFailureDeleteMd5s = NormalizeChartInfoLookupKeys(parseFailureDeleteMd5s);
        if (sourceDigestEntries.Count == 0 && sourceRows.Count == 0 && sourceParseFailureRows.Count == 0 && sourceParseFailureDeleteMd5s.Count == 0)
        {
            return;
        }
        EnsureChartInfoSchema(songDb);
        foreach (ChartDigestBackfillEntry entry in sourceDigestEntries)
        {
            songDb.Execute(ChartDigestMapUpsertSql, entry.Md5, entry.Sha256);
        }
        foreach (LR2SongDBExtended.chart_info row in sourceRows)
        {
            ExecuteChartInfoUpsert(songDb, row);
        }
        foreach (string md5 in sourceParseFailureDeleteMd5s)
        {
            songDb.Execute(ChartInfoParseFailureDeleteSql, md5);
        }
        foreach (LR2SongDBExtended.chart_info_parse_failure row in sourceParseFailureRows)
        {
            ExecuteChartInfoParseFailureUpsert(songDb, row);
        }
    }

    private static void ExecuteChartInfoUpsert(LR2SongDBExtended songDb, LR2SongDBExtended.chart_info row)
    {
        songDb.Execute(
            ChartInfoUpsertSql,
            row.sha256,
            row.md5,
            row.charthash,
            row.level,
            row.difficulty,
            row.difficulty_defined ? 1 : 0,
            row.mainbpm,
            row.maxbpm,
            row.minbpm,
            row.length,
            row.mode,
            row.judge,
            row.feature,
            row.notes,
            row.n,
            row.ln,
            row.s,
            row.ls,
            row.total,
            row.total_defined ? 1 : 0,
            row.density,
            row.peakdensity,
            row.enddensity,
            row.distribution,
            row.speedchange,
            row.speedchange_count,
            row.lanenotes,
            row.parser_version,
            row.updated_at);
    }

    private static void ExecuteChartInfoParseFailureUpsert(LR2SongDBExtended songDb, LR2SongDBExtended.chart_info_parse_failure row)
    {
        songDb.Execute(
            ChartInfoParseFailureUpsertSql,
            row.md5,
            row.sha256,
            row.path,
            row.parser_version,
            row.failure_kind,
            row.exception_type,
            row.message,
            row.parse_timeout_ms,
            row.updated_at);
    }

    public void UpsertChartDigests(IEnumerable<BMSFile> files)
    {
        List<BMSFile> sourceFiles = (files ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => file != null && !string.IsNullOrWhiteSpace(file.hash) && !string.IsNullOrWhiteSpace(file.sha256))
            .ToList();
        if (sourceFiles.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureBmsonSchema(songDb);
            foreach (BMSFile file in sourceFiles)
            {
                UpsertChartDigest(songDb, file);
            }
        });
    }

    public void UpsertBmsonSongs(IEnumerable<LR2SongDBExtended.bmson_song> songs)
    {
        List<LR2SongDBExtended.bmson_song> sourceSongs = (songs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
            .Where((LR2SongDBExtended.bmson_song song) => song != null && !string.IsNullOrWhiteSpace(song.path))
            .ToList();
        if (sourceSongs.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureBmsonSchema(songDb);
            foreach (LR2SongDBExtended.bmson_song song in sourceSongs)
            {
                songDb.InsertOrReplace(song, typeof(LR2SongDBExtended.bmson_song));
            }
        });
    }

    public void ReplaceBmsonSongPath(LR2SongDBExtended.bmson_song song, string oldPath)
    {
        if (song == null)
        {
            throw new ArgumentNullException(nameof(song));
        }
        if (string.IsNullOrWhiteSpace(song.path))
        {
            throw new ArgumentNullException(nameof(song.path));
        }
        if (string.IsNullOrWhiteSpace(oldPath))
        {
            throw new ArgumentNullException(nameof(oldPath));
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureBmsonSchema(songDb);
            bool hasMaintenanceTable = TableExists(songDb, SQLiteTable<LR2SongDBExtended.maintenance>.GetTableName());
            songDb.Delete<LR2SongDBExtended.bmson_song>(oldPath);
            if (hasMaintenanceTable)
            {
                songDb.Delete<LR2SongDBExtended.maintenance>(oldPath);
            }
            songDb.InsertOrReplace(song, typeof(LR2SongDBExtended.bmson_song));
            if (hasMaintenanceTable && song.MaintenanceInfo != null)
            {
                song.MaintenanceInfo.NormalizeForBmson(song.path, song.md5);
                songDb.InsertOrReplace(song.MaintenanceInfo, typeof(LR2SongDBExtended.maintenance));
            }
        });
    }

    public bool ReplaceFolderRecord(string oldFolderPath, string newFolderPath)
    {
        if (string.IsNullOrWhiteSpace(oldFolderPath) || string.IsNullOrWhiteSpace(newFolderPath))
        {
            throw new ArgumentNullException(string.IsNullOrWhiteSpace(oldFolderPath) ? nameof(oldFolderPath) : nameof(newFolderPath));
        }
        if (!Directory.Exists(newFolderPath))
        {
            throw new DirectoryNotFoundException(string.Format(Resources.Error_RenameDestDirNotFound, newFolderPath));
        }
        try
        {
            bool replaced = false;
            ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
            {
                LR2SongDB.folder folder = songDb.Table<LR2SongDB.folder>()
                    .ToList()
                    .FirstOrDefault((LR2SongDB.folder item) => item.path.Equals(oldFolderPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                if (folder == null)
                {
                    return;
                }
                replaced = true;
                songDb.Delete<LR2SongDB.folder>(folder.path);
                folder.title = Path.GetFileName(newFolderPath);
                folder.path = newFolderPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (folder.parent != "e2977170")
                {
                    string directoryName = Path.GetDirectoryName(newFolderPath.TrimEnd(Path.DirectorySeparatorChar));
                    Encoding encoding = Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                    folder.parent = LR2CRC32.Compute(encoding.GetBytes(directoryName + "\\\0")).ToString("x");
                }
                songDb.InsertOrReplace(folder, typeof(LR2SongDB.folder));
            });
            return replaced;
        }
        catch (DirectoryNotFoundException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (IOException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public List<LR2IRData> LoadIrData(int lr2Id)
    {
        return LoadIrDataWithMetrics(lr2Id).Rows;
    }

    public List<LR2IRScore> LoadIrScoreRows()
    {
        return LoadIrScoreRowsWithMetrics().Rows;
    }

    public IrScoreRowsLoadResult LoadIrScoreRowsWithMetrics()
    {
        IrScoreRowsLoadResult result = new IrScoreRowsLoadResult();
        using LR2SongDBExtended songDb = OpenSongDbReadOnly();
        result.ReadOnly = songDb.IsReadOnlyConnection;
        result.DbLockWaitMs = songDb.ProcessLockWaitMs;
        result.Rows.AddRange(songDb.Query<LR2IRScore>("SELECT * FROM " + SQLiteTable<LR2SongDBExtended.ir_score>.GetTableName() + ";"));
        return result;
    }

    public LR2SongDBExtended.ir_score_refresh_metadata LoadIrScoreRefreshMetadata(int lr2Id)
    {
        return LoadIrScoreRefreshMetadata(lr2Id, out _);
    }

    public LR2SongDBExtended.ir_score_refresh_metadata LoadIrScoreRefreshMetadata(int lr2Id, out long dbLockWaitMs)
    {
        using LR2SongDBExtended songDb = OpenSongDbReadOnly();
        dbLockWaitMs = songDb.ProcessLockWaitMs;
        return songDb.Table<LR2SongDBExtended.ir_score_refresh_metadata>().FirstOrDefault((LR2SongDBExtended.ir_score_refresh_metadata row) => row.lr2id == lr2Id);
    }

    public void UpsertIrScoreRefreshMetadata(int lr2Id, string scoreDigestSha256)
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        songDb.CreateTable<LR2SongDBExtended.ir_score_refresh_metadata>();
        songDb.InsertOrReplace(new LR2SongDBExtended.ir_score_refresh_metadata
        {
            lr2id = lr2Id,
            score_digest_sha256 = scoreDigestSha256 ?? string.Empty,
            updated_at = DateTime.UtcNow
        }, typeof(LR2SongDBExtended.ir_score_refresh_metadata));
    }

    public IrDataLoadResult LoadIrDataWithMetrics(int lr2Id)
    {
        IrDataLoadResult result = new IrDataLoadResult();
        using LR2SongDBExtended songDb = OpenSongDbReadOnly();
        result.ReadOnly = songDb.IsReadOnlyConnection;
        result.DbLockWaitMs = songDb.ProcessLockWaitMs;
        string tableName = SQLiteTable<LR2SongDBExtended.ir_data>.GetTableName();
        string lr2IdColumn = SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName((LR2SongDBExtended.ir_data row) => row.lr2id);
        Stopwatch stopwatch = Stopwatch.StartNew();
        result.Rows.AddRange(songDb.Query<LR2IRData>("SELECT * FROM " + tableName + " WHERE " + lr2IdColumn + " = ?;", lr2Id));
        stopwatch.Stop();
        result.DbReadMs = stopwatch.ElapsedMilliseconds;
        result.MaterializeMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    public void UpsertIrData(IEnumerable<LR2IRData> irData)
    {
        List<LR2IRData> entries = DeduplicateIrData(irData);
        if (entries.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            foreach (LR2IRData entry in entries)
            {
                songDb.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.ir_data>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName((LR2SongDBExtended.ir_data e) => e.hash) + " = '" + entry.hash + "' AND " + SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName((LR2SongDBExtended.ir_data e) => e.lr2id) + " = " + entry.lr2id + ";");
                songDb.InsertOrReplace(entry, typeof(LR2SongDBExtended.ir_data));
            }
        });
    }

    public bool TryBulkInsertIrDataForEmptyLr2Id(int lr2Id, IEnumerable<LR2IRData> irData)
    {
        List<LR2IRData> entries = DeduplicateIrData(irData)
            .Where((LR2IRData entry) => entry.lr2id == lr2Id)
            .ToList();
        if (entries.Count == 0)
        {
            return true;
        }
        bool inserted = false;
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            string tableName = SQLiteTable<LR2SongDBExtended.ir_data>.GetTableName();
            string lr2IdColumn = SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName((LR2SongDBExtended.ir_data row) => row.lr2id);
            long existingRows = songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM " + tableName + " WHERE " + lr2IdColumn + " = " + lr2Id + ";");
            if (existingRows != 0)
            {
                return;
            }
            songDb.InsertAll(entries, typeof(LR2SongDBExtended.ir_data));
            inserted = true;
        });
        return inserted;
    }

    private static List<LR2IRData> DeduplicateIrData(IEnumerable<LR2IRData> irData)
    {
        Dictionary<string, LR2IRData> deduplicated = new Dictionary<string, LR2IRData>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2IRData entry in irData ?? Enumerable.Empty<LR2IRData>())
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.hash))
            {
                continue;
            }
            deduplicated[entry.hash + "\u001f" + entry.lr2id] = entry;
        }
        return deduplicated.Values.ToList();
    }

    public void ReplaceIrScoreTable(IEnumerable<LR2IRScore> scoreTable)
    {
        List<LR2IRScore> entries = (scoreTable ?? Enumerable.Empty<LR2IRScore>()).Where((LR2IRScore score) => score != null).ToList();
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            songDb.DropTable<LR2SongDBExtended.ir_score>();
            songDb.CreateTable<LR2SongDBExtended.ir_score>();
            if (entries.Count > 0)
            {
                songDb.InsertAll(entries, typeof(LR2SongDBExtended.ir_score));
            }
            songDb.CreateIndex("ir_score_idx_unsent", SQLiteTable<LR2SongDBExtended.ir_score>.GetTableName(), new string[9]
            {
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.hash),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.clear),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.combo),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.pg),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.gr),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.gd),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.bd),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.pr),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.minbp)
            });
        });
    }

    internal static void EnsureBmsonSchema(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        songDb.CreateTable<LR2SongDBExtended.app_schema_version>();
        RepairableBmsonSchemaIssues issues = BmsonMigrationPreflightService.AnalyzeRepairableBmsonSchemaIssues(songDb);
        bool rebuildChartDigestMapTable = issues.HasFlag(RepairableBmsonSchemaIssues.ChartDigestMapTableMissing)
            || issues.HasFlag(RepairableBmsonSchemaIssues.ChartDigestMapTableInvalid);
        bool rebuildBmsonSongTable = issues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongTableMissing)
            || issues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongTableInvalid);
        if (rebuildChartDigestMapTable && TableExists(songDb, SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName()))
        {
            songDb.DropTable<LR2SongDBExtended.chart_digest_map>();
        }
        if (rebuildBmsonSongTable && TableExists(songDb, SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName()))
        {
            songDb.DropTable<LR2SongDBExtended.bmson_song>();
        }

        string chartDigestMapTableName = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();

        string bmsonSongTableName = SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName();
        songDb.CreateTable<LR2SongDBExtended.bmson_song>();
        EnsureIndex(songDb, "bmson_song_idx_md5", bmsonSongTableName, SQLiteTable<LR2SongDBExtended.bmson_song>.GetColumnName((LR2SongDBExtended.bmson_song row) => row.md5));
        EnsureIndex(songDb, "bmson_song_idx_sha256", bmsonSongTableName, SQLiteTable<LR2SongDBExtended.bmson_song>.GetColumnName((LR2SongDBExtended.bmson_song row) => row.sha256));
        EnsureIndex(songDb, "bmson_song_idx_folder", bmsonSongTableName, SQLiteTable<LR2SongDBExtended.bmson_song>.GetColumnName((LR2SongDBExtended.bmson_song row) => row.folder));
    }

    internal static void EnsureIrDataSchema(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        string tableName = SQLiteTable<LR2SongDBExtended.ir_data>.GetTableName();
        songDb.CreateTable<LR2SongDBExtended.ir_data>();
        songDb.CreateIndex("ir_data_idx", tableName, new string[1] { SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName((LR2SongDBExtended.ir_data e) => e.lr2id) });
        songDb.CreateIndex(
            "ir_data_idx_lr2id_hash",
            tableName,
            new[]
            {
                SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName((LR2SongDBExtended.ir_data e) => e.lr2id),
                SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName((LR2SongDBExtended.ir_data e) => e.hash)
            });
    }

    /// <summary>
    /// chart_info テーブルと関連 index を作成または修復します。
    /// </summary>
    /// <param name="songDb">対象 song.db 接続。</param>
    internal static void EnsureChartInfoSchema(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        songDb.CreateTable<LR2SongDBExtended.app_schema_version>();
        string tableName = SQLiteTable<LR2SongDBExtended.chart_info>.GetTableName();
        if (TableExists(songDb, tableName) && !IsChartInfoTableCompatible(songDb))
        {
            songDb.DropTable<LR2SongDBExtended.chart_info>();
        }
        songDb.CreateTable<LR2SongDBExtended.chart_info>();
        EnsureIndex(songDb, "chart_info_idx_md5", tableName, SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName((LR2SongDBExtended.chart_info row) => row.md5));
        EnsureIndex(songDb, "chart_info_idx_charthash", tableName, SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName((LR2SongDBExtended.chart_info row) => row.charthash));
        EnsureIndex(songDb, "chart_info_idx_parser_version", tableName, SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName((LR2SongDBExtended.chart_info row) => row.parser_version));
        string failureTableName = SQLiteTable<LR2SongDBExtended.chart_info_parse_failure>.GetTableName();
        if (TableExists(songDb, failureTableName) && !IsChartInfoParseFailureTableCompatible(songDb))
        {
            songDb.DropTable<LR2SongDBExtended.chart_info_parse_failure>();
        }
        songDb.CreateTable<LR2SongDBExtended.chart_info_parse_failure>();
        EnsureIndex(songDb, "chart_info_parse_failure_idx_sha256", failureTableName, SQLiteTable<LR2SongDBExtended.chart_info_parse_failure>.GetColumnName((LR2SongDBExtended.chart_info_parse_failure row) => row.sha256));
        EnsureIndex(songDb, "chart_info_parse_failure_idx_parser_version", failureTableName, SQLiteTable<LR2SongDBExtended.chart_info_parse_failure>.GetColumnName((LR2SongDBExtended.chart_info_parse_failure row) => row.parser_version));
        string importHistoryTableName = SQLiteTable<LR2SongDBExtended.chart_info_import_history>.GetTableName();
        if (TableExists(songDb, importHistoryTableName) && !IsChartInfoImportHistoryTableCompatible(songDb))
        {
            songDb.DropTable<LR2SongDBExtended.chart_info_import_history>();
        }
        songDb.CreateTable<LR2SongDBExtended.chart_info_import_history>();
        EnsureIndex(songDb, "chart_info_import_history_idx_bundle_sha256", importHistoryTableName, SQLiteTable<LR2SongDBExtended.chart_info_import_history>.GetColumnName((LR2SongDBExtended.chart_info_import_history row) => row.bundle_sha256));
        SetChartInfoSchemaVersion(songDb, CurrentChartInfoSchemaVersion);
    }

    internal static void UpsertChartDigest(LR2SongDBExtended songDb, BMSFile file)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        if (file == null || string.IsNullOrWhiteSpace(file.hash) || string.IsNullOrWhiteSpace(file.sha256))
        {
            return;
        }
        LR2SongDBExtended.chart_digest_map row = new LR2SongDBExtended.chart_digest_map
        {
            md5 = file.hash,
            sha256 = file.sha256
        };
        songDb.InsertOrReplace(row, typeof(LR2SongDBExtended.chart_digest_map));
    }

    internal static void CompleteBmsonStartupMigration(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        Dictionary<string, string> reusableDigests = LoadReusableChartDigestMap(songDb);
        EnsureBmsonSchema(songDb);
        RepairChartDigestMapConsistency(songDb, reusableDigests);
        SetBmsonAppSchemaVersion(songDb, CurrentBmsonAppSchemaVersion);
    }

    internal static void RepairChartDigestMapConsistency(LR2SongDBExtended songDb)
    {
        RepairChartDigestMapConsistency(songDb, null);
    }

    internal static void DeleteChartDigestIfOrphaned(LR2SongDBExtended songDb, string md5, string preservedMd5 = null)
    {
        if (songDb == null || string.IsNullOrWhiteSpace(md5))
        {
            return;
        }
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName()))
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(preservedMd5) && string.Equals(md5, preservedMd5, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        long count = songDb.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName((LR2SongDB.song row) => row.hash)
            + " = " + BMSPlaylist.SqlQuoteForTest(md5) + ";");
        if (count <= 0)
        {
            songDb.Delete<LR2SongDBExtended.chart_digest_map>(md5);
        }
    }

    internal static void DeleteChartDigestsIfOrphaned(LR2SongDBExtended songDb, IEnumerable<string> md5s, string preservedMd5 = null)
    {
        foreach (string md5 in (md5s ?? Enumerable.Empty<string>()).Where((string item) => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DeleteChartDigestIfOrphaned(songDb, md5, preservedMd5);
        }
    }

    private static bool TableExists(LR2SongDBExtended songDb, string tableName)
    {
        return TableExists(songDb, null, tableName);
    }

    private static bool TableExists(LR2SongDBExtended songDb, string schemaName, string tableName)
    {
        string masterTableName = string.IsNullOrWhiteSpace(schemaName) ? "sqlite_master" : schemaName + ".sqlite_master";
        return songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM " + masterTableName + " WHERE type = 'table' AND name = " + BMSPlaylist.SqlQuoteForTest(tableName) + ";") > 0;
    }

    private static bool IndexExists(LR2SongDBExtended songDb, string indexName)
    {
        return songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = " + BMSPlaylist.SqlQuoteForTest(indexName) + ";") > 0;
    }

    private static void EnsureIndex(LR2SongDBExtended songDb, string indexName, string tableName, string columnName)
    {
        if (!IndexExists(songDb, indexName))
        {
            songDb.CreateIndex(indexName, tableName, columnName);
        }
    }

    private static string GetSongHashByPath(LR2SongDBExtended songDb, string path)
    {
        if (songDb == null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        return songDb.ExecuteScalar<string>(
            "SELECT " + SQLiteTable<LR2SongDB.song>.GetColumnName((LR2SongDB.song row) => row.hash)
            + " FROM " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName((LR2SongDB.song row) => row.path)
            + " = " + BMSPlaylist.SqlQuoteForTest(path)
            + " LIMIT 1;");
    }

    private static bool IsChartInfoSchemaCurrent(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            return false;
        }
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.app_schema_version>.GetTableName()))
        {
            return false;
        }
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.chart_info>.GetTableName()) || !IsChartInfoTableCompatible(songDb))
        {
            return false;
        }
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.chart_info_parse_failure>.GetTableName()) || !IsChartInfoParseFailureTableCompatible(songDb))
        {
            return false;
        }
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.chart_info_import_history>.GetTableName()) || !IsChartInfoImportHistoryTableCompatible(songDb))
        {
            return false;
        }
        long count = songDb.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM " + SQLiteTable<LR2SongDBExtended.app_schema_version>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDBExtended.app_schema_version>.GetColumnName((LR2SongDBExtended.app_schema_version row) => row.name)
            + " = " + BMSPlaylist.SqlQuoteForTest(ChartInfoSchemaVersionName)
            + " AND " + SQLiteTable<LR2SongDBExtended.app_schema_version>.GetColumnName((LR2SongDBExtended.app_schema_version row) => row.version)
            + " >= " + CurrentChartInfoSchemaVersion + ";");
        return count > 0;
    }

    private static void SetChartInfoSchemaVersion(LR2SongDBExtended songDb, int version)
    {
        songDb.InsertOrReplace(new LR2SongDBExtended.app_schema_version
        {
            name = ChartInfoSchemaVersionName,
            version = version
        }, typeof(LR2SongDBExtended.app_schema_version));
    }

    private static void SetBmsonAppSchemaVersion(LR2SongDBExtended songDb, int version)
    {
        songDb.InsertOrReplace(new LR2SongDBExtended.app_schema_version
        {
            name = BmsonAppSchemaVersionName,
            version = version
        }, typeof(LR2SongDBExtended.app_schema_version));
    }

    private static string BuildChartInfoMetadataBundleImportKey(string bundleSha256)
    {
        return (bundleSha256 ?? string.Empty).Trim().ToLowerInvariant() + ":" + CurrentChartInfoParserVersion;
    }

    private static bool IsChartInfoMetadataBundleImported(LR2SongDBExtended songDb, string importKey)
    {
        return songDb.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM " + SQLiteTable<LR2SongDBExtended.chart_info_import_history>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDBExtended.chart_info_import_history>.GetColumnName((LR2SongDBExtended.chart_info_import_history row) => row.import_key)
            + " = ?;",
            importKey) > 0L;
    }

    private static ChartInfoMetadataBundleManifest LoadChartInfoMetadataBundleManifest(LR2SongDBExtended songDb)
    {
        RequireAttachedTableColumns(
            songDb,
            "bundle",
            "chart_info_metadata_bundle",
            new[]
            {
                "bundle_id",
                "format_version",
                "generated_at",
                "chart_info_schema_version",
                "chart_info_parser_version",
                "chart_info_count",
                "chart_digest_count"
            });
        RequireAttachedTableColumns(songDb, "bundle", "chart_info", ChartInfoColumnList.Split(new[] { ", " }, StringSplitOptions.None));
        RequireAttachedTableColumns(songDb, "bundle", "chart_digest_map", new[] { "md5", "sha256" });
        ChartInfoMetadataBundleManifest manifest = songDb.Query<ChartInfoMetadataBundleManifest>(
            "SELECT bundle_id, format_version, generated_at, chart_info_schema_version, chart_info_parser_version, chart_info_count, chart_digest_count "
            + "FROM bundle.chart_info_metadata_bundle ORDER BY bundle_id COLLATE NOCASE ASC LIMIT 1;").FirstOrDefault();
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.bundle_id))
        {
            throw new InvalidDataException("chart_info metadata bundle manifest is missing.");
        }
        if (manifest.format_version != ChartInfoMetadataBundleFormatVersion)
        {
            throw new InvalidDataException("Unsupported chart_info metadata bundle format version: " + manifest.format_version);
        }
        if (manifest.chart_info_schema_version < CurrentChartInfoSchemaVersion)
        {
            throw new InvalidDataException("Stale chart_info metadata bundle schema version: " + manifest.chart_info_schema_version);
        }
        if (manifest.chart_info_parser_version < CurrentChartInfoParserVersion)
        {
            throw new InvalidDataException("Stale chart_info metadata bundle parser version: " + manifest.chart_info_parser_version);
        }
        return manifest;
    }

    private static void PrepareChartInfoMetadataImportTempTables(LR2SongDBExtended songDb)
    {
        DropChartInfoMetadataImportTempTables(songDb);
        songDb.Execute(
            "CREATE TEMP TABLE chart_info_metadata_import_info AS SELECT "
            + "lower(trim(b.sha256)) AS sha256, "
            + "lower(trim(b.md5)) AS md5, "
            + NormalizedOptionalSha256Expression("b.charthash") + " AS charthash, "
            + "b.level AS level, "
            + "b.difficulty AS difficulty, "
            + "b.difficulty_defined AS difficulty_defined, "
            + "b.mainbpm AS mainbpm, "
            + "b.maxbpm AS maxbpm, "
            + "b.minbpm AS minbpm, "
            + "b.length AS length, "
            + "b.mode AS mode, "
            + "b.judge AS judge, "
            + "b.feature AS feature, "
            + "b.notes AS notes, "
            + "b.n AS n, "
            + "b.ln AS ln, "
            + "b.s AS s, "
            + "b.ls AS ls, "
            + "b.total AS total, "
            + "b.total_defined AS total_defined, "
            + "b.density AS density, "
            + "b.peakdensity AS peakdensity, "
            + "b.enddensity AS enddensity, "
            + "b.distribution AS distribution, "
            + "b.speedchange AS speedchange, "
            + "b.speedchange_count AS speedchange_count, "
            + "b.lanenotes AS lanenotes, "
            + "b.parser_version AS parser_version, "
            + "b.updated_at AS updated_at "
            + "FROM bundle.chart_info b WHERE "
            + ValidSha256Condition("b.sha256")
            + " AND " + ValidMd5Condition("b.md5")
            + " AND b.parser_version >= " + CurrentChartInfoParserVersion + ";");
        songDb.Execute(
            "CREATE TEMP TABLE chart_info_metadata_import_info_to_import AS "
            + "SELECT s.* FROM temp.chart_info_metadata_import_info s "
            + "LEFT JOIN main.chart_info d ON d.sha256 = s.sha256 "
            + "WHERE d.sha256 IS NULL OR IFNULL(d.parser_version, 0) < " + CurrentChartInfoParserVersion + ";");
        songDb.Execute("CREATE TEMP TABLE chart_info_metadata_import_digest_source (md5 TEXT PRIMARY KEY, sha256 TEXT);");
        songDb.Execute(
            "INSERT OR IGNORE INTO temp.chart_info_metadata_import_digest_source (md5, sha256) "
            + "SELECT lower(trim(d.md5)), lower(trim(d.sha256)) FROM bundle.chart_digest_map d WHERE "
            + ValidMd5Condition("d.md5")
            + " AND " + ValidSha256Condition("d.sha256")
            + " ORDER BY lower(trim(d.md5)) COLLATE NOCASE ASC, lower(trim(d.sha256)) COLLATE NOCASE ASC;");
        songDb.Execute(
            "INSERT OR IGNORE INTO temp.chart_info_metadata_import_digest_source (md5, sha256) "
            + "SELECT md5, sha256 FROM temp.chart_info_metadata_import_info WHERE "
            + ValidMd5Condition("md5")
            + " AND " + ValidSha256Condition("sha256")
            + " ORDER BY md5 COLLATE NOCASE ASC, sha256 COLLATE NOCASE ASC;");
        songDb.Execute(
            "CREATE TEMP TABLE chart_info_metadata_import_digest_to_import AS "
            + "SELECT s.* FROM temp.chart_info_metadata_import_digest_source s "
            + "LEFT JOIN main.chart_digest_map d ON d.md5 = s.md5 "
            + "WHERE d.md5 IS NULL;");
        songDb.Execute(
            "CREATE TEMP TABLE chart_info_metadata_import_failure_to_clear AS "
            + "SELECT DISTINCT f.md5 FROM main.chart_info_parse_failure f "
            + "JOIN temp.chart_info_metadata_import_info s ON s.md5 = f.md5;");
    }

    private static void DropChartInfoMetadataImportTempTables(LR2SongDBExtended songDb)
    {
        songDb.Execute("DROP TABLE IF EXISTS temp.chart_info_metadata_import_failure_to_clear;");
        songDb.Execute("DROP TABLE IF EXISTS temp.chart_info_metadata_import_digest_to_import;");
        songDb.Execute("DROP TABLE IF EXISTS temp.chart_info_metadata_import_digest_source;");
        songDb.Execute("DROP TABLE IF EXISTS temp.chart_info_metadata_import_info_to_import;");
        songDb.Execute("DROP TABLE IF EXISTS temp.chart_info_metadata_import_info;");
    }

    private static string NormalizedOptionalSha256Expression(string column)
    {
        return "CASE WHEN " + ValidSha256Condition(column) + " THEN lower(trim(" + column + ")) ELSE NULL END";
    }

    private static string ValidSha256Condition(string column)
    {
        return column + " IS NOT NULL AND length(trim(" + column + ")) = 64 AND lower(trim(" + column + ")) NOT GLOB '*[^0-9a-f]*'";
    }

    private static string ValidMd5Condition(string column)
    {
        return column + " IS NOT NULL AND length(trim(" + column + ")) = 32 AND lower(trim(" + column + ")) NOT GLOB '*[^0-9a-f]*'";
    }

    private static void RequireAttachedTableColumns(LR2SongDBExtended songDb, string schemaName, string tableName, IEnumerable<string> requiredColumns)
    {
        if (!TableExists(songDb, schemaName, tableName))
        {
            throw new InvalidDataException("Attached chart_info metadata bundle does not contain required table: " + tableName);
        }
        HashSet<string> columns = GetTableColumns(songDb, schemaName, tableName);
        foreach (string column in requiredColumns ?? Enumerable.Empty<string>())
        {
            if (!columns.Contains(column))
            {
                throw new InvalidDataException("Attached chart_info metadata bundle table " + tableName + " does not contain required column: " + column);
            }
        }
    }

    private static HashSet<string> GetTableColumns(LR2SongDBExtended songDb, string schemaName, string tableName)
    {
        string pragmaPrefix = string.IsNullOrWhiteSpace(schemaName) ? string.Empty : schemaName + ".";
        return new HashSet<string>(
            songDb.Query<TableInfoRow>("PRAGMA " + pragmaPrefix + "table_info('" + tableName.Replace("'", "''") + "');")
                .Select((TableInfoRow row) => row.name),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> LoadReusableChartDigestMap(LR2SongDBExtended songDb)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string tableName = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        if (!TableExists(songDb, tableName))
        {
            return result;
        }
        string tableSql = songDb.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = " + BMSPlaylist.SqlQuoteForTest(tableName) + ";");
        if (string.IsNullOrWhiteSpace(tableSql)
            || tableSql.IndexOf("md5", StringComparison.OrdinalIgnoreCase) < 0
            || tableSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return result;
        }
        foreach (ChartDigestQueryRow row in songDb.Query<ChartDigestQueryRow>("SELECT md5, sha256 FROM " + tableName + " WHERE md5 IS NOT NULL AND TRIM(md5) <> '' AND sha256 IS NOT NULL AND TRIM(sha256) <> '';"))
        {
            string md5 = row.md5;
            string sha256 = row.sha256;
            if (!string.IsNullOrWhiteSpace(md5) && !string.IsNullOrWhiteSpace(sha256))
            {
                result[md5] = sha256;
            }
        }
        return result;
    }

    private static List<string> NormalizeChartInfoLookupKeys(IEnumerable<string> values)
    {
        return (values ?? Enumerable.Empty<string>())
            .Where((string value) => !string.IsNullOrWhiteSpace(value))
            .Select((string value) => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeLookupKey(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }

    private static Dictionary<string, string> LoadChartDigestMapForCandidateSummary(LR2SongDBExtended songDb)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string tableName = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        if (!TableExists(songDb, tableName))
        {
            return result;
        }
        foreach (ChartDigestQueryRow row in songDb.Query<ChartDigestQueryRow>("SELECT md5, sha256 FROM " + tableName + " WHERE md5 IS NOT NULL AND TRIM(md5) <> '' AND sha256 IS NOT NULL AND TRIM(sha256) <> '';"))
        {
            string md5 = NormalizeLookupKey(row.md5);
            string sha256 = NormalizeLookupKey(row.sha256);
            if (!string.IsNullOrWhiteSpace(md5) && !string.IsNullOrWhiteSpace(sha256))
            {
                result[md5] = sha256;
            }
        }
        return result;
    }

    private static void ClassifyChartInfoBackfillCandidate(
        ChartInfoBackfillCandidateSummary summary,
        string md5,
        string sha256,
        HashSet<string> currentChartInfoSha256,
        HashSet<string> anyChartInfoSha256,
        HashSet<string> currentParseFailureMd5,
        bool missingDigestWhenShaMissing)
    {
        if (!string.IsNullOrWhiteSpace(sha256) && currentChartInfoSha256.Contains(sha256))
        {
            summary.CurrentChartInfoOwnerCount++;
            return;
        }
        if (!string.IsNullOrWhiteSpace(md5) && currentParseFailureMd5.Contains(md5))
        {
            summary.CurrentParseFailureOwnerCount++;
            return;
        }
        summary.CandidateOwnerCount++;
        if (string.IsNullOrWhiteSpace(sha256))
        {
            if (missingDigestWhenShaMissing)
            {
                summary.MissingDigestOwnerCount++;
            }
            else
            {
                summary.MissingChartInfoOwnerCount++;
            }
            return;
        }
        if (anyChartInfoSha256.Contains(sha256))
        {
            summary.StaleChartInfoOwnerCount++;
            return;
        }
        summary.MissingChartInfoOwnerCount++;
    }

    private static IEnumerable<LR2SongDBExtended.chart_info> QueryChartInfosByColumn(LR2SongDBExtended songDb, string columnName, IReadOnlyList<string> keys, bool orderBySha256)
    {
        if (songDb == null || keys == null || keys.Count == 0)
        {
            yield break;
        }
        string tableName = SQLiteTable<LR2SongDBExtended.chart_info>.GetTableName();
        for (int offset = 0; offset < keys.Count; offset += ChartInfoLookupChunkSize)
        {
            List<string> chunk = keys.Skip(offset).Take(ChartInfoLookupChunkSize).ToList();
            if (chunk.Count == 0)
            {
                continue;
            }
            string placeholders = string.Join(", ", chunk.Select(_ => "?"));
            string sql = "SELECT * FROM " + tableName + " WHERE " + columnName + " IN (" + placeholders + ")";
            if (orderBySha256)
            {
                sql += " ORDER BY sha256 COLLATE NOCASE ASC";
            }
            foreach (LR2SongDBExtended.chart_info row in songDb.Query<LR2SongDBExtended.chart_info>(sql, chunk.Cast<object>().ToArray()))
            {
                yield return row;
            }
        }
    }

    private static List<LR2SongDBExtended.chart_info_parse_failure> NormalizeChartInfoParseFailureRows(IEnumerable<LR2SongDBExtended.chart_info_parse_failure> rows)
    {
        return (rows ?? Enumerable.Empty<LR2SongDBExtended.chart_info_parse_failure>())
            .Where((LR2SongDBExtended.chart_info_parse_failure row) => row != null && !string.IsNullOrWhiteSpace(row.md5))
            .ToList();
    }

    private static int SafeExecuteScalarInt(LR2SongDBExtended songDb, string sql)
    {
        if (songDb == null || string.IsNullOrWhiteSpace(sql))
        {
            return 0;
        }
        try
        {
            long value = songDb.ExecuteScalar<long>(sql);
            if (value <= 0L)
            {
                return 0;
            }
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }
        catch
        {
            return 0;
        }
    }

    private static string BuildCurrentParseFailureJoinCondition(string alias, TimeSpan parseTimeout)
    {
        string effectiveAlias = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias.Trim() + ".";
        long timeoutMs = Math.Max(0L, (long)Math.Ceiling(parseTimeout.TotalMilliseconds));
        return effectiveAlias + "parser_version = " + CurrentChartInfoParserVersion
            + " AND ("
            + effectiveAlias + "failure_kind IS NULL"
            + " OR lower(" + effectiveAlias + "failure_kind) <> 'timeout'"
            + " OR COALESCE(" + effectiveAlias + "parse_timeout_ms, -1) >= " + timeoutMs
            + ")";
    }

    private static bool IsCurrentChartInfoParseFailure(LR2SongDBExtended.chart_info_parse_failure row, TimeSpan parseTimeout)
    {
        if (row == null || string.IsNullOrWhiteSpace(row.md5))
        {
            return false;
        }
        return IsCurrentChartInfoParseFailure(row.parser_version, row.failure_kind, row.parse_timeout_ms, parseTimeout);
    }

    private static bool IsCurrentChartInfoParseFailure(int parserVersion, string failureKind, int? parseTimeoutMs, TimeSpan parseTimeout)
    {
        if (parserVersion != CurrentChartInfoParserVersion)
        {
            return false;
        }
        if (!string.Equals(failureKind, "timeout", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!parseTimeoutMs.HasValue)
        {
            return false;
        }
        long timeoutMs = Math.Max(0L, (long)Math.Ceiling(parseTimeout.TotalMilliseconds));
        return parseTimeoutMs.Value >= timeoutMs;
    }

    private static void RepairChartDigestMapConsistency(LR2SongDBExtended songDb, Dictionary<string, string> reusableDigests)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        string songTableName = SQLiteTable<LR2SongDB.song>.GetTableName();
        if (!TableExists(songDb, songTableName))
        {
            RebuildChartDigestMap(songDb, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            return;
        }
        Dictionary<string, string> digests = reusableDigests != null
            ? new Dictionary<string, string>(reusableDigests, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (SongDigestSourceRow row in songDb.Query<SongDigestSourceRow>(
                     "SELECT DISTINCT "
                     + SQLiteTable<LR2SongDB.song>.GetColumnName((LR2SongDB.song song) => song.hash) + " AS md5, "
                     + SQLiteTable<LR2SongDB.song>.GetColumnName((LR2SongDB.song song) => song.path) + " AS path "
                     + "FROM " + songTableName + " WHERE "
                     + SQLiteTable<LR2SongDB.song>.GetColumnName((LR2SongDB.song song) => song.hash) + " IS NOT NULL AND TRIM("
                     + SQLiteTable<LR2SongDB.song>.GetColumnName((LR2SongDB.song song) => song.hash) + ") <> '';"))
        {
            string md5 = row.md5;
            string path = row.path;
            if (string.IsNullOrWhiteSpace(md5) || digests.ContainsKey(md5))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                digests[md5] = BMSFile.GetSHA256Hash(path);
            }
        }
        RebuildChartDigestMap(songDb, digests);
    }

    private static bool IsChartInfoTableCompatible(LR2SongDBExtended songDb)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.chart_info>.GetTableName();
        if (!TableExists(songDb, tableName))
        {
            return false;
        }
        HashSet<string> columns = new HashSet<string>(
            songDb.Query<TableInfoRow>("PRAGMA table_info('" + tableName.Replace("'", "''") + "');")
                .Select((TableInfoRow row) => row.name),
            StringComparer.OrdinalIgnoreCase);
        string[] requiredColumns =
        {
            "sha256",
            "md5",
            "charthash",
            "level",
            "difficulty",
            "difficulty_defined",
            "mainbpm",
            "maxbpm",
            "minbpm",
            "length",
            "mode",
            "judge",
            "feature",
            "notes",
            "n",
            "ln",
            "s",
            "ls",
            "total",
            "total_defined",
            "density",
            "peakdensity",
            "enddensity",
            "distribution",
            "speedchange",
            "speedchange_count",
            "lanenotes",
            "parser_version",
            "updated_at"
        };
        return requiredColumns.All((string columnName) => columns.Contains(columnName));
    }

    private static bool IsChartInfoParseFailureTableCompatible(LR2SongDBExtended songDb)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.chart_info_parse_failure>.GetTableName();
        if (!TableExists(songDb, tableName))
        {
            return false;
        }
        HashSet<string> columns = new HashSet<string>(
            songDb.Query<TableInfoRow>("PRAGMA table_info('" + tableName.Replace("'", "''") + "');")
                .Select((TableInfoRow row) => row.name),
            StringComparer.OrdinalIgnoreCase);
        string[] requiredColumns =
        {
            "md5",
            "sha256",
            "path",
            "parser_version",
            "failure_kind",
            "exception_type",
            "message",
            "parse_timeout_ms",
            "updated_at"
        };
        return requiredColumns.All((string columnName) => columns.Contains(columnName));
    }

    private static bool IsChartInfoImportHistoryTableCompatible(LR2SongDBExtended songDb)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.chart_info_import_history>.GetTableName();
        if (!TableExists(songDb, tableName))
        {
            return false;
        }
        HashSet<string> columns = new HashSet<string>(
            songDb.Query<TableInfoRow>("PRAGMA table_info('" + tableName.Replace("'", "''") + "');")
                .Select((TableInfoRow row) => row.name),
            StringComparer.OrdinalIgnoreCase);
        string[] requiredColumns =
        {
            "import_key",
            "bundle_id",
            "bundle_sha256",
            "parser_version",
            "chart_info_imported_count",
            "chart_digest_imported_count",
            "failure_cleared_count",
            "imported_at"
        };
        return requiredColumns.All((string columnName) => columns.Contains(columnName));
    }

    private static void RebuildChartDigestMap(LR2SongDBExtended songDb, IDictionary<string, string> digests)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        if (TableExists(songDb, tableName))
        {
            songDb.DropTable<LR2SongDBExtended.chart_digest_map>();
        }
        songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
        foreach (KeyValuePair<string, string> item in (digests ?? new Dictionary<string, string>()).Where((KeyValuePair<string, string> item) => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value)))
        {
            songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
            {
                md5 = item.Key,
                sha256 = item.Value
            }, typeof(LR2SongDBExtended.chart_digest_map));
        }
    }

    private sealed class ChartDigestQueryRow
    {
        public string md5 { get; set; }

        public string sha256 { get; set; }
    }

    private sealed class ChartInfoSummaryRow
    {
        public string sha256 { get; set; }

        public int parser_version { get; set; }
    }

    private sealed class ChartInfoParseFailureSummaryRow
    {
        public string md5 { get; set; }

        public int parser_version { get; set; }

        public string failure_kind { get; set; }

        public int? parse_timeout_ms { get; set; }
    }

    private sealed class ChartInfoOwnerMd5Row
    {
        public string md5 { get; set; }
    }

    private sealed class BmsonChartInfoOwnerRow
    {
        public string md5 { get; set; }

        public string sha256 { get; set; }
    }

    private sealed class ChartInfoMetadataBundleManifest
    {
        public string bundle_id { get; set; }

        public int format_version { get; set; }

        public string generated_at { get; set; }

        public int chart_info_schema_version { get; set; }

        public int chart_info_parser_version { get; set; }

        public int chart_info_count { get; set; }

        public int chart_digest_count { get; set; }
    }

    private sealed class TableInfoRow
    {
        public string name { get; set; }
    }

    private sealed class SongDigestSourceRow
    {
        public string md5 { get; set; }

        public string path { get; set; }
    }
}
