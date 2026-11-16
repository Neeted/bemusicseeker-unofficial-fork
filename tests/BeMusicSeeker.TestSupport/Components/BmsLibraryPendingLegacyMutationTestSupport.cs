using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Files;
using Microsoft.VisualBasic.FileIO;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class BmsLibraryPendingLegacyMutationTestSupport
{
    /// <summary>同期callbackだけが変更する領域を準備し、その終了後に回収します。</summary>
    internal static void WithTemporaryDirectory(Action<string> action)
    {
        using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directoryPath = Path.Combine(
            caseWorkspace1.RootDirectory,
            "BeMusicSeeker_PendingLegacyTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            action(directoryPath);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class RecordingDeleteFileMutationService : IFileMutationService
    {
        private readonly string? failurePath;

        private readonly string? directoryFailurePath;

        /// <summary>実変更を記録し、指定された一つのファイルまたはディレクトリを変更前に失敗させる。</summary>
        internal RecordingDeleteFileMutationService(string? failurePath = null, string? directoryFailurePath = null)
        {
            this.failurePath = failurePath;
            this.directoryFailurePath = directoryFailurePath;
        }

        internal List<string> DeleteFileDirectPaths { get; } = [];

        internal List<string> DeleteFileShellPaths { get; } = [];

        internal List<string> DeleteDirectoryDirectPaths { get; } = [];

        internal List<string> DeleteDirectoryShellPaths { get; } = [];

        /// <summary>実機のごみ箱を使わず、要求された削除方針を記録する。</summary>
        internal List<RecycleOption> DeleteDirectoryRecycleOptions { get; } = [];

        /// <summary>ディレクトリ操作へ渡された属性正規化の範囲を記録する。</summary>
        internal List<ReadOnlyNormalizationScope> DeleteDirectoryNormalizationScopes { get; } = [];

        internal List<string> MoveFilePaths { get; } = [];

        public void EnsureDirectory(string directoryPath, FileMutationOptions? options = null)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
        {
            MoveFilePaths.Add(sourcePath);
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
        {
            throw new NotSupportedException();
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
        {
            throw new NotSupportedException();
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
        {
            throw new NotSupportedException();
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions? options = null)
        {
            DeleteFileDirectPaths.Add(filePath);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions? options = null)
        {
            DeleteFileShellPaths.Add(filePath);
            if (string.Equals(filePath, failurePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("injected child delete failure");
            }
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions? options = null)
        {
            DeleteDirectoryDirectPaths.Add(directoryPath);
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions? options = null)
        {
            DeleteDirectoryShellPaths.Add(directoryPath);
            DeleteDirectoryRecycleOptions.Add(recycleOption);
            DeleteDirectoryNormalizationScopes.Add(options?.ReadOnlyNormalizationScope ?? ReadOnlyNormalizationScope.None);
            if (string.Equals(directoryPath, directoryFailurePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("injected package delete failure");
            }
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions? options = null)
        {
        }
    }

}
