using System;
using System.IO;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;

namespace BeMusicSeeker.Tests;

/// <summary>既存資源の実退避後に公開と復元を失敗させ、実producerの復元不能を観測する小さなI/O代替です。</summary>
internal sealed class UnrestoredDestinationFileMutationService(string destination) : IFileMutationService
{
    private readonly ResilientFileMutationService inner = new();
    internal string? BackupPath { get; private set; }
    internal int FailedMoveCount { get; private set; }

    public void EnsureDirectory(string path, FileMutationOptions? options = null) => inner.EnsureDirectory(path, options);
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
    {
        if (string.Equals(destinationPath, destination, StringComparison.OrdinalIgnoreCase))
        {
            FailedMoveCount++;
            throw new IOException("既存資源の公開・復元の注入失敗");
        }
        inner.MoveFile(sourcePath, destinationPath, overwrite, options);
        if (string.Equals(sourcePath, destination, StringComparison.OrdinalIgnoreCase)) { BackupPath = destinationPath; }
    }
    public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null) => inner.MoveDirectory(sourcePath, destinationPath, overwrite, options);
    public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null) => inner.CopyFile(sourcePath, destinationPath, overwrite, options);
    public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null) => inner.CopyDirectory(sourcePath, destinationPath, overwrite, options);
    public void DeleteFileDirect(string path, FileMutationOptions? options = null) => inner.DeleteFileDirect(path, options);
    public void DeleteFileShell(string path, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions? options = null) => inner.DeleteFileShell(path, uiOption, recycleOption, options);
    public void DeleteDirectoryDirect(string path, bool recursive, FileMutationOptions? options = null) => inner.DeleteDirectoryDirect(path, recursive, options);
    public void DeleteDirectoryShell(string path, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions? options = null) => inner.DeleteDirectoryShell(path, uiOption, recycleOption, options);
    public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions? options = null) => inner.SetTimestamps(path, isDirectory, creationTime, lastWriteTime, options);
}
