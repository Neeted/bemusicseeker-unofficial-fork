using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

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
    internal const string BmsonAppSchemaVersionName = "bmson_app_schema";

    internal const int CurrentBmsonAppSchemaVersion = 1;

    internal const string ChartInfoSchemaVersionName = "chart_info_schema";

    internal const int CurrentChartInfoSchemaVersion = 2;

    internal const int CurrentChartInfoParserVersion = 11;

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

    public LR2ScoreDBExtended OpenScoreDb()
    {
        if (string.IsNullOrWhiteSpace(ScoreDbPath))
        {
            throw new InvalidOperationException("Score DB path is not configured.");
        }
        return new LR2ScoreDBExtended(ScoreDbPath);
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
                string previousHash = GetSongHashByPath(songDb, file.path);
                songDb.InsertOrReplace(file, typeof(LR2SongDB.song));
                UpsertChartDigest(songDb, file);
                DeleteChartDigestIfOrphaned(songDb, previousHash, file.hash);
            }
        });
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
            foreach (LR2SongDBExtended.bmson_song entry in entries)
            {
                songDb.Delete<LR2SongDBExtended.bmson_song>(entry.path);
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

    public ScoreTableLoadResult LoadScoresAndPlayerId()
    {
        ScoreTableLoadResult result = new ScoreTableLoadResult();
        if (string.IsNullOrWhiteSpace(ScoreDbPath))
        {
            return result;
        }
        using LR2ScoreDBExtended scoreDb = OpenScoreDb();
        result.Scores.AddRange(scoreDb.Table<BMSScore>().ToList());
        result.LR2Id = scoreDb.Table<LR2ScoreDB.player>().ToList().FirstOrDefault()?.irid ?? 0;
        return result;
    }

    public List<BMSPackage> LoadInstallPackages()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        return songDb.Table<BMSPackage>().ToList();
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
            if (bmsFile.maintenanceInfo != null)
            {
                songDb.InsertOrReplace(bmsFile.maintenanceInfo, typeof(LR2SongDBExtended.maintenance));
            }
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

    public void RunBmsonSchemaMigration()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            RunBmsonSchemaMigration(songDb);
            songDb.Commit();
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    public bool IsBmsonAppSchemaCurrent()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        return IsBmsonAppSchemaCurrent(songDb);
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

    public void MarkBmsonAppSchemaCurrent()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            SetBmsonAppSchemaVersion(songDb, CurrentBmsonAppSchemaVersion);
            songDb.Commit();
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
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
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName()))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.chart_digest_map item in songDb.Table<LR2SongDBExtended.chart_digest_map>())
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
                songDb.InsertOrReplace(row, typeof(LR2SongDBExtended.chart_info));
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

    /// <summary>
    /// chart_info backfill の1 chunk 分を短い transaction で保存します。
    /// digest と chart_info をまとめて保存し、途中終了時は次回 backfill が未保存分だけを再開します。
    /// </summary>
    /// <param name="digestEntries">補完した MD5/SHA-256 対応。</param>
    /// <param name="rows">保存する譜面解析メタデータ。</param>
    public void UpsertChartInfoBackfillChunk(IEnumerable<ChartDigestBackfillEntry> digestEntries, IEnumerable<LR2SongDBExtended.chart_info> rows)
    {
        List<ChartDigestBackfillEntry> sourceDigestEntries = (digestEntries ?? Enumerable.Empty<ChartDigestBackfillEntry>())
            .Where((ChartDigestBackfillEntry entry) => entry != null && !string.IsNullOrWhiteSpace(entry.Md5) && !string.IsNullOrWhiteSpace(entry.Sha256))
            .ToList();
        List<LR2SongDBExtended.chart_info> sourceRows = (rows ?? Enumerable.Empty<LR2SongDBExtended.chart_info>())
            .Where((LR2SongDBExtended.chart_info row) => row != null && !string.IsNullOrWhiteSpace(row.sha256))
            .ToList();
        if (sourceDigestEntries.Count == 0 && sourceRows.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            foreach (ChartDigestBackfillEntry entry in sourceDigestEntries)
            {
                songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = entry.Md5,
                    sha256 = entry.Sha256
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }
            foreach (LR2SongDBExtended.chart_info row in sourceRows)
            {
                songDb.InsertOrReplace(row, typeof(LR2SongDBExtended.chart_info));
            }
        });
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
            songDb.Delete<LR2SongDBExtended.bmson_song>(oldPath);
            songDb.InsertOrReplace(song, typeof(LR2SongDBExtended.bmson_song));
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
        using LR2SongDBExtended songDb = OpenSongDb();
        return (from s in songDb.Table<LR2IRData>().ToList()
                where s.lr2id == lr2Id
                select s).ToList();
    }

    public void UpsertIrData(IEnumerable<LR2IRData> irData)
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
        List<LR2IRData> entries = deduplicated.Values.ToList();
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

    internal static void RunBmsonSchemaMigration(LR2SongDBExtended songDb)
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
        return songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = " + BMSPlaylist.SqlQuoteForTest(tableName) + ";") > 0;
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
        long count = songDb.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM " + SQLiteTable<LR2SongDBExtended.app_schema_version>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDBExtended.app_schema_version>.GetColumnName((LR2SongDBExtended.app_schema_version row) => row.name)
            + " = " + BMSPlaylist.SqlQuoteForTest(ChartInfoSchemaVersionName)
            + " AND " + SQLiteTable<LR2SongDBExtended.app_schema_version>.GetColumnName((LR2SongDBExtended.app_schema_version row) => row.version)
            + " >= " + CurrentChartInfoSchemaVersion + ";");
        return count > 0;
    }

    private static bool IsBmsonAppSchemaCurrent(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            return false;
        }
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.app_schema_version>.GetTableName()))
        {
            return false;
        }
        long count = songDb.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM " + SQLiteTable<LR2SongDBExtended.app_schema_version>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDBExtended.app_schema_version>.GetColumnName((LR2SongDBExtended.app_schema_version row) => row.name)
            + " = " + BMSPlaylist.SqlQuoteForTest(BmsonAppSchemaVersionName)
            + " AND " + SQLiteTable<LR2SongDBExtended.app_schema_version>.GetColumnName((LR2SongDBExtended.app_schema_version row) => row.version)
            + " >= " + CurrentBmsonAppSchemaVersion + ";");
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
