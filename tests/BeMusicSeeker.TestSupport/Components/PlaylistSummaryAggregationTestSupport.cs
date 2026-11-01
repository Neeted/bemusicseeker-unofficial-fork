using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Files;
using Microsoft.VisualBasic.FileIO;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>summary集計の入力hash・実DB・物理媒体を共用し、期待集計値は呼出し側で独立して定義します。</summary>
internal static class PlaylistSummaryAggregationTestSupport
{
    /// <summary>大文字小文字を区別しないhash集合を作り、null配列は空入力として扱います。</summary>
    internal static HashSet<string> CreateHashSet(params string[] values)
    {
        return new HashSet<string>(values ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>指定されたhashと削除状態だけを持つentryを作り、欠落hashもそのまま保持します。</summary>
    internal static BMSTableEntry CreateEntry(string? md5, string? sha256, bool isRemoved = false)
    {
        var entry = new TestablePlaylistEntry
        {
            is_removed = isRemoved
        };
        if (md5 != null)
        {
            entry.SetHash(md5);
        }
        if (sha256 != null)
        {
            entry.SetSha256(sha256);
        }
        return entry;
    }

    /// <summary>物理読取りを行わず、指定pathとhashを持つ集計用譜面を作ります。</summary>
    internal static ChartFile CreateLibraryFile(string path, string md5, string sha256)
    {
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = path
        };
        file = file with { Md5 = md5 };
        file = file with { Sha256 = sha256 };
        return file;
    }


    /// <summary>集計入力のhashを個別に設定し、欠落値を含む条件を用意します。</summary>
    internal sealed class TestablePlaylistEntry : BMSTableEntry
    {
        /// <summary>入力のMD5をそのまま設定します。</summary>
        public void SetHash(string value)
        {
            md5 = value;
        }

        /// <summary>入力のSHA256をそのまま設定します。</summary>
        public void SetSha256(string value)
        {
            sha256 = value;
        }
    }

    /// <summary>通知を発行せずBMS正本を準備し、集計テストが明示する通知境界まで公開を進めません。</summary>
    internal static void SetLibraryFilesWithoutNotification(BMSLibrary library, IEnumerable<ChartFile> files)
    {
        OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, files);
    }

    /// <summary>通知を発行せずBMSON正本を準備し、呼出し側の明示更新と分離します。</summary>
    internal static void SetLibraryBmsonSongsWithoutNotification(BMSLibrary library, IEnumerable<ChartFile> songs)
    {
        OwnedChartCollectionTestSupport.SetLibraryBmsonSongsWithoutNotification(library, songs);
    }

    /// <summary>呼出し側と同じDB所有者で一時曲DBを準備し、処理終端後に回収します。</summary>
    /// <param name="databaseScope">準備と検証対象が共有するfixtureのDB所有者。</param>
    internal static void WithTemporarySongDb(ApplicationDatabaseScope databaseScope, System.Action<string> testAction)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(migrationWorkspace.RootDirectory, "BeMusicSeeker_PlaylistSummaryAggregation_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
            }
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>fixture専有の実媒体に対する移動・コピー・削除を、shell表示なしで実行します。</summary>
    internal sealed class TestFileMutationService : IFileMutationService
    {
        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationDirectoryPath = Path.GetDirectoryName(destinationPath)!;
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
            }
            if (overwrite && File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(sourcePath, destinationPath);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            if (overwrite && Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }
            string destinationParentPath = Path.GetDirectoryName(destinationPath)!;
            if (!string.IsNullOrWhiteSpace(destinationParentPath))
            {
                Directory.CreateDirectory(destinationParentPath);
            }
            CopyDirectory(sourcePath, destinationPath);
            Directory.Delete(sourcePath, recursive: true);
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string? destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
            }
            File.Copy(sourcePath, destinationPath, overwrite);
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (string directoryPath in Directory.GetDirectories(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directoryPath.Replace(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase));
            }
            foreach (string filePath in Directory.GetFiles(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                string destinationFilePath = filePath.Replace(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
                File.Copy(filePath, destinationFilePath, overwrite);
            }
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }

        /// <summary>既存のファイル名と下位構造を保ってfixture内のdirectoryを再帰コピーします。</summary>
        internal static void CopyDirectory(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (string directoryPath in Directory.GetDirectories(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directoryPath.Replace(sourcePath, destinationPath));
            }
            foreach (string filePath in Directory.GetFiles(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                string destinationFilePath = filePath.Replace(sourcePath, destinationPath);
                string destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath)!;
                if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
                {
                    Directory.CreateDirectory(destinationDirectoryPath);
                }
                File.Copy(filePath, destinationFilePath, overwrite: true);
            }
        }
    }

}
