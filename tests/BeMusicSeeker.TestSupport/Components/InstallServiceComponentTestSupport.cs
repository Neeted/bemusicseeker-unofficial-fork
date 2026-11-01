using System.Collections.ObjectModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Files;
using Microsoft.VisualBasic.FileIO;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using SearchOption = System.IO.SearchOption;
namespace BeMusicSeeker.TestSupport.Components;

/// <summary>旧テストとCoreテストで同一の入力・期待値・寿命を共有します。</summary>
internal static class InstallServiceComponentTestSupport
{

    /// <summary>保護名の入力ファイルへ既存bytesと比較用の固定更新日時を設定します。</summary>
    internal static string WriteProtectedNameFile(string directory, string relativePath, byte[] bytes, bool newer)
    {
        string path = Path.Combine(directory, relativePath);
        if (Path.GetDirectoryName(path) is string parent) { Directory.CreateDirectory(parent); }
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, newer ? 3 : 1, 0, 0, 0, DateTimeKind.Utc));
        return path;
    }

    /// <summary>保護名の配置結果を全相対パスとbytesで照合し、余分なファイルも検出します。</summary>
    internal static void AssertProtectedNameFiles(string directory, IReadOnlyDictionary<string, byte[]> expected)
    {
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path)).ToArray());
        foreach ((string relativePath, byte[] bytes) in expected) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(directory, relativePath))); }
    }

    /// <summary>導入テストの既定BMS入力を同一の本文で書き込みます。</summary>
    internal static string CreateBmsFile(string directoryPath, string fileName, string titleLine)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, "#PLAYER 1\r\n" + titleLine + "\r\n#ARTIST Test\r\n");
        return filePath;
    }

    /// <summary>同期callbackとその後片付けが完了するまで、ケース専有の可変媒体を所有します。</summary>
    internal static void WithTemporaryDirectory(Action<string> testAction)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectoryPath = Path.Combine(workspace.RootDirectory, "BeMusicSeeker_PackageInstallTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        testAction(tempDirectoryPath);
    }

    /// <summary>同じ順序と所属契約で導入packageの観測用collectionを作ります。</summary>
    internal static ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new ObservableCollection<ChartPackage>([.. (packages ?? [])]);
    }

    /// <summary>実DB接続をcallback内で終端させ、同じ寿命でケース専有の可変媒体を回収します。</summary>
    internal static void WithTemporarySongDb(Action<string, string> testAction)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var databaseScope = new ApplicationDatabaseScope();
        string tempRootPath = Path.Combine(workspace.RootDirectory, "BeMusicSeeker_PackageInstallSongDbTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(databaseScope, songDbPath, songDb =>
        {
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDB.folder>();
            songDb.CreateTable<LR2SongDBExtended.maintenance>();
            songDb.CreateTable<LR2SongDBExtended.bmson_song>();
        });
        testAction(songDbPath, tempRootPath);
    }

    /// <summary>既存の非表示dialog応答を同じ契約で共用します。</summary>
    internal sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public List<string> Messages { get; } = [];

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            Messages.Add(messageBoxText);
            return defaultResult;
        }
    }

    /// <summary>旧テストとCoreテストで同一の実ファイル操作を使います。</summary>
    internal sealed class RealFileMutationService : IFileMutationService
    {
        /// <summary>実ファイル・フォルダのコピー直前に入力を観測し、未設定なら観測を行わない。</summary>
        public Action<string>? BeforeCopy { get; set; }

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string? destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
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
            string? destinationParentDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParentDirectoryPath))
            {
                Directory.CreateDirectory(destinationParentDirectoryPath);
            }
            Directory.Move(sourcePath, destinationPath);
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            BeforeCopy?.Invoke(sourcePath);
            string? destinationParent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParent))
            {
                Directory.CreateDirectory(destinationParent);
            }
            File.Copy(sourcePath, destinationPath, overwrite);
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            BeforeCopy?.Invoke(sourcePath);
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
            if (lastWriteTime.HasValue)
            {
                if (isDirectory)
                {
                    Directory.SetLastWriteTime(path, lastWriteTime.Value);
                }
                else
                {
                    File.SetLastWriteTime(path, lastWriteTime.Value);
                }
            }
        }
    }

}
