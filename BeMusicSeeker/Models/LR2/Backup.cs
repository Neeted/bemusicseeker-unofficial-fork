using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.LR2;

public class Backup : ObservableObject
{
    /// <summary>
    /// バックアップ保存処理の結果を表します。Model 内で警告 dialog を直接表示しないため、UI に返す message を保持します。
    /// </summary>
    public sealed class BackupSaveResult
    {
        internal BackupSaveResult(bool saved, IReadOnlyList<string> warnings, Exception failureException = null)
        {
            Saved = saved;
            Warnings = warnings ?? [];
            FailureException = failureException;
        }

        /// <summary>
        /// 新しいバックアップの公開が完了した場合は true です。公開後の世代削除失敗では false に戻りません。
        /// </summary>
        public bool Saved { get; }

        /// <summary>
        /// バックアップ処理中に UI 側で表示する警告です。
        /// </summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>
        /// バックアップ保存そのものが失敗した場合の例外です。UI 側で表示するため、Task fault としては流しません。
        /// </summary>
        public Exception FailureException { get; }

        internal static BackupSaveResult Failure(Exception failureException, IReadOnlyList<string> warnings = null)
        {
            return new BackupSaveResult(saved: false, warnings: warnings ?? [], failureException: failureException);
        }
    }

    [Flags]
    public enum Target
    {
        None = 0,
        Config = 2,
        SongDB = 4,
        ScoreDB = 8,
        All = 0xF
    }

    private static readonly Regex backupFolderRegex = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.Compiled);

    private static readonly string dateFormat = "yyyy-MM-dd";

    /// <summary>
    /// LR2 バックアップを公開します。保存失敗は例外として呼出元へ伝える互換 API です。
    /// </summary>
    /// <param name="dstDir">バックアップ出力先ディレクトリ。</param>
    /// <param name="span">前回バックアップから必要な経過時間。</param>
    /// <param name="genNum">新しいバックアップを含む保持世代数。1 未満は 1 とします。</param>
    /// <param name="_paths">選択済みのバックアップ対象パス。</param>
    /// <returns>新しい世代の公開が完了した場合は true。</returns>
    public static bool SaveBackups(string dstDir, TimeSpan span, int genNum, IEnumerable<string> _paths)
    {
        BackupSaveResult result = SaveBackupsWithResult(dstDir, span, genNum, _paths);
        if (result.FailureException != null)
        {
            ExceptionDispatchInfo.Capture(result.FailureException).Throw();
        }
        return result.Saved;
    }

    /// <summary>
    /// 設定された対象フラグからバックアップ対象を組み立て、既存の保存処理へ渡します。
    /// 選択済みパスの存在確認と保存結果の扱いは <see cref="SaveBackupsWithResult" /> に委譲します。
    /// </summary>
    /// <param name="dstDir">バックアップ出力先ディレクトリ。</param>
    /// <param name="span">前回バックアップから必要な経過時間。</param>
    /// <param name="genNum">新しいバックアップを含む保持世代数。</param>
    /// <param name="targets">保存する対象のフラグ。</param>
    /// <param name="configPath">LR2 の設定ファイルパス。</param>
    /// <param name="songDbPath">LR2 の曲データベースパス。</param>
    /// <param name="scoreDirectoryPath">LR2 のスコアデータベースディレクトリパス。</param>
    /// <returns>既存の保存処理による公開結果、削除警告、および公開前の主失敗例外。</returns>
    internal static BackupSaveResult SaveSelectedBackupsWithResult(
        string dstDir,
        TimeSpan span,
        int genNum,
        Target targets,
        string configPath,
        string songDbPath,
        string scoreDirectoryPath)
    {
        if (targets == Target.None)
        {
            return new BackupSaveResult(saved: false, warnings: []);
        }

        List<string> selectedPaths = [];
        if (targets.HasFlag(Target.Config))
        {
            selectedPaths.Add(configPath);
        }
        if (targets.HasFlag(Target.SongDB))
        {
            selectedPaths.Add(songDbPath);
        }
        if (targets.HasFlag(Target.ScoreDB))
        {
            selectedPaths.Add(scoreDirectoryPath);
        }

        return SaveBackupsWithResult(dstDir, span, genNum, selectedPaths);
    }

    /// <summary>
    /// 選択対象を一時ディレクトリへ全てコピーしてから日付世代として公開し、古い世代を削除します。
    /// 公開前の失敗では既存世代と主原因を保持し、公開後の削除失敗は保存成功と警告を返します。
    /// 後片付けの存在確認・削除と任意診断の失敗では公開結果を変更しません。
    /// ディレクトリ再解析ポイントはコピー前に拒否し、未来日付の世代は間隔・保持・削除の対象外とします。
    /// </summary>
    /// <param name="dstDir">バックアップ出力先ディレクトリ。</param>
    /// <param name="span">前回バックアップから必要な経過時間。日単位で最低 1 日とします。</param>
    /// <param name="genNum">新しいバックアップを含む保持世代数。1 未満は 1 とします。</param>
    /// <param name="_paths">選択済みの対象パス。非空パスの欠落は保存失敗とします。</param>
    /// <returns>公開結果、削除警告、および公開前の主失敗例外。</returns>
    public static BackupSaveResult SaveBackupsWithResult(string dstDir, TimeSpan span, int genNum, IEnumerable<string> _paths)
    {
        var warnings = new List<string>();
        string stagingDirectory = null;
        List<DateTime> generations;
        try
        {
            if (_paths == null)
            {
                return new BackupSaveResult(saved: false, warnings: []);
            }
            if (dstDir == null)
            {
                throw new ArgumentNullException("dstDir", BeMusicSeeker.Properties.Resources.Error_OutputDirectoryNotSet);
            }
            if (!LongPathFileSystem.DirectoryExists(dstDir))
            {
                throw new DirectoryNotFoundException(string.Format(BeMusicSeeker.Properties.Resources.Error_OutputDirectoryNotFoundFormat, dstDir));
            }
            genNum = Math.Max(1, genNum);
            List<string> sources = [.. _paths.Where(path => !string.IsNullOrWhiteSpace(path))];
            if (sources.Count == 0)
            {
                return new BackupSaveResult(saved: false, warnings: []);
            }
            foreach (string source in sources)
            {
                if (!LongPathFileSystem.EntryExists(source))
                {
                    throw new FileNotFoundException(Properties.Resources.Error_FileNotFound + Environment.NewLine + source, source);
                }
            }
            var minimumInterval = TimeSpan.FromDays(Math.Max(1, span.Days));
            DateTime today = DateTime.Today;
            generations = [.. (from directory in LongPathFileSystem.EnumerateDirectories(dstDir, "*", SearchOption.TopDirectoryOnly)
                               select Path.GetFileName(directory) into name
                               where backupFolderRegex.Match(name).Success
                               select DateTime.ParseExact(name, dateFormat, DateTimeFormatInfo.InvariantInfo, DateTimeStyles.None) into date
                               where date <= today
                               orderby date descending
                               select date)];
            if (generations.Count > 0 && today - generations[0] < minimumInterval)
            {
                return new BackupSaveResult(saved: false, warnings: []);
            }

            string publishedDirectory = Path.Combine(dstDir, today.ToString(dateFormat, DateTimeFormatInfo.InvariantInfo));
            // A sibling stays on the destination filesystem, so publication is one non-overwriting rename.
            stagingDirectory = LongPathFileSystem.CreateMutationSiblingPath(publishedDirectory, "backup");
            LongPathFileSystem.CreateDirectory(stagingDirectory);
            foreach (string source in sources)
            {
                string destination = Path.Combine(stagingDirectory, Path.GetFileName(source));
                if (LongPathFileSystem.FileExists(source))
                {
                    LongPathFileSystem.CopyFile(source, destination, overwrite: true);
                }
                else if (LongPathFileSystem.DirectoryExists(source))
                {
                    LongPathFileSystem.CopyDirectory(source, destination, overwrite: true, rejectDirectoryReparsePoints: true);
                }
                else
                {
                    // A selected source disappearing after validation must not publish an incomplete backup.
                    throw new FileNotFoundException(Properties.Resources.Error_FileNotFound + Environment.NewLine + source, source);
                }
            }
            LongPathFileSystem.MoveDirectory(stagingDirectory, publishedDirectory, overwrite: false);
        }
        catch (Exception exception)
        {
            if (stagingDirectory != null)
            {
                TryDeleteBackupDirectory(stagingDirectory, warnings, onlyIfExists: true);
            }
            return BackupSaveResult.Failure(exception, warnings);
        }

        // The new generation owns one retention slot. Pruning cannot turn a published backup into failure.
        foreach (DateTime generation in generations.Skip(genNum - 1).Reverse())
        {
            string directory = Path.Combine(dstDir, generation.ToString(dateFormat, DateTimeFormatInfo.InvariantInfo));
            TryDeleteBackupDirectory(directory, warnings);
        }
        return new BackupSaveResult(saved: true, warnings: warnings);
    }

    private static void TryDeleteBackupDirectory(string directory, List<string> warnings, bool onlyIfExists = false)
    {
        try
        {
            // 属性取得の欠落だけを不存在とし、アクセス等の失敗は後片付けの診断へ渡します。
            if (onlyIfExists)
            {
                try
                {
                    if ((LongPathFileSystem.GetAttributes(directory) & FileAttributes.Directory) == 0)
                    {
                        // 作業領域と同名のファイルへ削除対象を広げません。
                        return;
                    }
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    return;
                }
            }
            LongPathFileSystem.DeleteDirectory(directory, recursive: true);
        }
        catch (Exception exception)
        {
            warnings.Add(string.Format(Properties.Resources.Warn_FileOrDirDeleteFailed, directory));
            ReportFailureBestEffort(exception, onlyIfExists ? "backup_stage_cleanup" : "backup_generation_prune", directory);
        }
    }

    /// <summary>
    /// 存在するLR2の楽曲・スコアDBを最適化します。失敗は対象パスと元原因を診断してfalseを返し、公開済みバックアップを変更しません。
    /// </summary>
    /// <param name="songDBPath">最適化する楽曲DBのパス。nullまたは欠損なら省略します。</param>
    /// <param name="scoreDBPaths">最適化するスコアDBのパス。nullまたは欠損の対象は省略します。</param>
    /// <returns>指定された既存DBの最適化が正常に完了した場合はtrue。処理または列挙に失敗した場合はfalse。</returns>
    public static bool RebuildDatabase(string songDBPath, IEnumerable<string> scoreDBPaths)
    {
        string databasePath = songDBPath;
        try
        {
            if (songDBPath != null && LongPathFileSystem.FileExists(songDBPath))
            {
                using var lR2SongDBExtended = new LR2SongDBExtended(songDBPath);
                lR2SongDBExtended.Execute("VACUUM;");
                lR2SongDBExtended.Execute("REINDEX;");
            }
            databasePath = null;
            if (scoreDBPaths != null)
            {
                foreach (string item in scoreDBPaths)
                {
                    databasePath = item;
                    if (LongPathFileSystem.FileExists(item))
                    {
                        using var lR2ScoreDBExtended = new LR2ScoreDBExtended(item);
                        lR2ScoreDBExtended.Execute("VACUUM;");
                        lR2ScoreDBExtended.Execute("REINDEX;");
                    }
                    // 次の列挙自体の失敗を、既に完了したDBの失敗として診断しません。
                    databasePath = null;
                }
            }
        }
        catch (Exception exception)
        {
            ReportFailureBestEffort(exception, "backup_database_optimization", databasePath);
            return false;
        }
        return true;
    }

    private static void ReportFailureBestEffort(Exception exception, string operation, string path)
    {
        try
        {
            NLogWrapper.FileLogger?.Warn(exception, $"LR2 backup failed: operation={operation}, path={path}");
        }
        catch
        {
            // 任意診断の故障を追加診断せず、保存結果と元の失敗を保持します。
        }
    }
}
