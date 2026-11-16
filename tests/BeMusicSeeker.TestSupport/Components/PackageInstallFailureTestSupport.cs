using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;
using static BeMusicSeeker.TestSupport.Components.InstallServiceComponentTestSupport;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>同じ機能の複数moduleが必要とする入力・観測だけを共用します。</summary>
internal static class PackageInstallFailureTestSupport
{

    /// <summary>実削除を保ち、指定した一つのパスだけ削除失敗を注入する。</summary>
    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal sealed class FailingDestinationDeleteFileMutationService(string failurePath) : IFileMutationService
    {
        internal readonly RealFileMutationService inner = new();

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
            => inner.EnsureDirectory(directoryPath, options);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.MoveFile(sourcePath, destinationPath, overwrite, options);

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.MoveDirectory(sourcePath, destinationPath, overwrite, options);

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.CopyFile(sourcePath, destinationPath, overwrite, options);

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.CopyDirectory(sourcePath, destinationPath, overwrite, options);

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (string.Equals(filePath, failurePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("injected-destination-delete-failure");
            }
            inner.DeleteFileDirect(filePath, options);
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
            => inner.DeleteFileShell(filePath, uiOption, recycleOption, options);

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (string.Equals(directoryPath, failurePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("injected-destination-delete-failure");
            }
            inner.DeleteDirectoryDirect(directoryPath, recursive, options);
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
            => inner.DeleteDirectoryShell(directoryPath, uiOption, recycleOption, options);

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
            => inner.SetTimestamps(path, isDirectory, creationTime, lastWriteTime, options);
    }

}
