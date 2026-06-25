using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models.Utils;
using Livet;

namespace BeMusicSeeker.Models.LR2;

public class Backup : NotificationObject
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
        /// 新しいバックアップを保存した場合は true です。
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
    /// LR2 関連ファイルをバックアップし、UI 側で表示する警告を結果として返します。
    /// </summary>
    /// <param name="dstDir">バックアップ出力先ディレクトリ。</param>
    /// <param name="span">前回バックアップから必要な経過時間。</param>
    /// <param name="genNum">保持する世代数。</param>
    /// <param name="_paths">バックアップ対象パス。</param>
    /// <returns>バックアップ保存結果。</returns>
    public static BackupSaveResult SaveBackupsWithResult(string dstDir, TimeSpan span, int genNum, IEnumerable<string> _paths)
    {
        var warnings = new List<string>();
        try
        {
            if (_paths == null)
            {
                return new BackupSaveResult(saved: false, warnings: []);
            }
            if (dstDir == null)
            {
                throw new ArgumentNullException("dstDir", "出力先ディレクトリが与えられていません");
            }
            if (!LongPathFileSystem.DirectoryExists(dstDir))
            {
                throw new DirectoryNotFoundException("出力先ディレクトリが存在しません" + Environment.NewLine + dstDir);
            }
            if (genNum <= 0)
            {
                genNum = 1;
            }
            List<string> list = [.. _paths.Where(f => !string.IsNullOrWhiteSpace(f) && LongPathFileSystem.EntryExists(f))];
            if (list.Count == 0)
            {
                return new BackupSaveResult(saved: false, warnings: []);
            }
            var timeSpan = new TimeSpan(Math.Max(1, span.Days), 0, 0, 0);
            List<DateTime> list2 = [.. (from dp in LongPathFileSystem.EnumerateDirectories(dstDir, "*", System.IO.SearchOption.TopDirectoryOnly)
                                    select Path.GetFileName(dp) into d
                                    where backupFolderRegex.Match(d).Success
                                    select DateTime.ParseExact(d, dateFormat, DateTimeFormatInfo.InvariantInfo, DateTimeStyles.None) into d
                                    orderby d descending
                                    select d)];
            DateTime today = DateTime.Today;
            if (list2.Count > 0)
            {
                DateTime dateTime = list2.First();
                if (today - dateTime < timeSpan)
                {
                    return new BackupSaveResult(saved: false, warnings: []);
                }
                if (list2.Count > genNum)
                {
                    foreach (DateTime item in list2.Skip(genNum))
                    {
                        try
                        {
                            LongPathFileSystem.DeleteDirectory(Path.Combine(dstDir, item.ToString(dateFormat)), recursive: true);
                        }
                        catch
                        {
                            warnings.Add("ディレクトリの削除に失敗しました。" + Environment.NewLine + "読み取り専用属性がついていないか、" + Environment.NewLine + "正常にアクセスできるか確認して下さい。" + Environment.NewLine + Path.Combine(dstDir, item.ToString(dateFormat)));
                        }
                    }
                }
            }
            string text = Path.Combine(dstDir, today.ToString(dateFormat));
            if (LongPathFileSystem.EntryExists(text))
            {
                throw new IOException("バックアップ先ディレクトリが既に存在しています" + Environment.NewLine + text);
            }
            LongPathFileSystem.CreateDirectory(text);
            foreach (string item2 in list)
            {
                string fileName = Path.GetFileName(item2);
                if (LongPathFileSystem.FileExists(item2))
                {
                    LongPathFileSystem.CopyFile(item2, Path.Combine(text, fileName), overwrite: true);
                }
                else if (LongPathFileSystem.DirectoryExists(item2))
                {
                    LongPathFileSystem.CopyDirectory(item2, Path.Combine(text, fileName), overwrite: true);
                }
            }
            return new BackupSaveResult(saved: true, warnings: warnings);
        }
        catch (Exception ex)
        {
            return BackupSaveResult.Failure(ex, warnings);
        }
    }

    public static bool RebuildDatabase(string songDBPath, IEnumerable<string> scoreDBPaths)
    {
        try
        {
            if (songDBPath != null && LongPathFileSystem.FileExists(songDBPath))
            {
                using var lR2SongDBExtended = new LR2SongDBExtended(songDBPath);
                lR2SongDBExtended.Execute("VACUUM;");
                lR2SongDBExtended.Execute("REINDEX;");
            }
            if (scoreDBPaths != null)
            {
                foreach (string item in scoreDBPaths.Where(f => LongPathFileSystem.FileExists(f)))
                {
                    using var lR2ScoreDBExtended = new LR2ScoreDBExtended(item);
                    lR2ScoreDBExtended.Execute("VACUUM;");
                    lR2ScoreDBExtended.Execute("REINDEX;");
                }
            }
        }
        catch (Exception)
        {
            return false;
        }
        return true;
    }
}
