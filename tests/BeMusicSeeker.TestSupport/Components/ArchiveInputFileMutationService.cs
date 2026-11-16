using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>実ファイル操作を委譲し、アーカイブの必須日時復元だけを観測・失敗注入する局所境界です。</summary>
internal sealed class ArchiveInputFileMutationService(Action<string> observeTimestamp) : IFileMutationService
{
    private readonly ResilientFileMutationService inner = new();
    /// <summary>必須日時の復元直前に観測または故障注入を行います。</summary>
    internal Action<string>? BeforeTimestamp { get; set; }
    /// <summary>実コピー開始を観測し、取消などを現在の物理処理中に要求します。</summary>
    internal Action<string>? BeforeCopy { get; set; }
    public void EnsureDirectory(string path, FileMutationOptions? options = null) => inner.EnsureDirectory(path, options);
    public void MoveFile(string source, string destination, bool overwrite, FileMutationOptions? options = null) => inner.MoveFile(source, destination, overwrite, options);
    public void MoveDirectory(string source, string destination, bool overwrite, FileMutationOptions? options = null) => inner.MoveDirectory(source, destination, overwrite, options);
    public void CopyFile(string source, string destination, bool overwrite, FileMutationOptions? options = null)
    { BeforeCopy?.Invoke(source); inner.CopyFile(source, destination, overwrite, options); }
    public void CopyDirectory(string source, string destination, bool overwrite, FileMutationOptions? options = null)
    { BeforeCopy?.Invoke(source); inner.CopyDirectory(source, destination, overwrite, options); }
    public void DeleteFileDirect(string path, FileMutationOptions? options = null) => inner.DeleteFileDirect(path, options);
    public void DeleteFileShell(string path, UIOption ui, RecycleOption recycle, FileMutationOptions? options = null) => inner.DeleteFileShell(path, ui, recycle, options);
    public void DeleteDirectoryDirect(string path, bool recursive, FileMutationOptions? options = null) => inner.DeleteDirectoryDirect(path, recursive, options);
    public void DeleteDirectoryShell(string path, UIOption ui, RecycleOption recycle, FileMutationOptions? options = null) => inner.DeleteDirectoryShell(path, ui, recycle, options);
    public void SetTimestamps(string path, bool isDirectory, DateTime? creation, DateTime? modified, FileMutationOptions? options = null)
    {
        if (!isDirectory && modified.HasValue)
        {
            observeTimestamp(path);
            BeforeTimestamp?.Invoke(path);
        }
        inner.SetTimestamps(path, isDirectory, creation, modified, options);
    }
}
