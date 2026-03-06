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

internal sealed class BmsLibraryDbGateway
{
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
        songDb.BeginTransaction();
        action(songDb);
        songDb.Commit();
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
            foreach (BMSFile file in files)
            {
                songDb.InsertOrReplace(file, typeof(LR2SongDB.song));
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
            foreach (BMSFile file in files)
            {
                songDb.Delete<LR2SongDB.song>(file.path);
                songDb.Delete<LR2SongDBExtended.maintenance>(file.path);
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
            songDb.Delete<LR2SongDB.song>(oldPath);
            songDb.Delete<LR2SongDBExtended.maintenance>(oldPath);
            if (bmsFile.maintenanceInfo != null)
            {
                songDb.InsertOrReplace(bmsFile.maintenanceInfo, typeof(LR2SongDBExtended.maintenance));
            }
            songDb.InsertOrReplace(bmsFile, typeof(LR2SongDB.song));
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
}
