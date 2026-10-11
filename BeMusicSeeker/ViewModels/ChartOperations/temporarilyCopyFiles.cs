using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

/// <summary>一時試聴の相対配置を作り、所有したファイルと空ディレクトリだけを回収します。既存素材と借用ディレクトリは所有しません。</summary>
internal sealed class temporarilyCopyFiles : IDisposable
{
    private readonly int sleep;
    private readonly List<string> copiedFilesDst = [];
    private readonly List<string> createdDirectories = [];
    private int disposed;

    /// <summary>開始境界後の既存保持時間を待ち、所有物の回収を一回行います。</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        if (sleep > 0) { Thread.Sleep(sleep); }
        var failures = new List<(string Path, Exception Failure)>();
        copiedFilesDst.AsParallel().ForAll(dstFile =>
        {
            try
            {
                if (LongPathFileSystem.FileExists(dstFile)) { LongPathFileSystem.DeleteFile(dstFile); }
            }
            catch (Exception exception)
            {
                lock (failures) { failures.Add((dstFile, exception)); }
            }
        });
        // コピー完了とファイル回収の後、子から親へ空の所有ディレクトリだけを除去します。
        // 一時配置へ第三者が追加したファイルは再帰削除で巻き込みません。
        foreach (string directory in createdDirectories.OrderByDescending(path => path.Length))
        {
            try
            {
                if (LongPathFileSystem.DirectoryExists(directory)
                    && !LongPathFileSystem.EnumerateFileSystemEntries(directory).Any())
                {
                    LongPathFileSystem.DeleteDirectory(directory, recursive: false);
                }
            }
            catch (Exception exception) { failures.Add((directory, exception)); }
        }
        var errors = new List<Exception>();
        foreach ((string path, Exception failure) in failures)
        {
            errors.Add(failure);
            try
            {
                if (disposing)
                {
                    string message = Resources.Msg_error_preview + Environment.NewLine + Environment.NewLine
                        + Resources.File + ": " + path + Environment.NewLine + failure.Message;
                    UiDialogRoute.ShowMessageBox(message, Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                }
                else { NLogWrapper.FileLogger?.Warn(failure, "temporary_preview_cleanup_failed path=" + path); }
            }
            catch (Exception notificationFailure)
            {
                if (disposing) { errors.Add(notificationFailure); }
            }
        }
        if (disposing && errors.Count > 0) { throw new AggregateException(errors); }
    }

    /// <summary>入力元ルート内のファイルを相対配置のまま並列コピーします。既存宛先は上書き・回収せず、構築失敗時も開始済みコピーの終端後に所有物を回収します。</summary>
    /// <param name="srcFiles">パッケージ内の対応譜面・音源・画像のファイル。</param>
    /// <param name="sourceRoot">相対配置の基準となるパッケージルート。</param>
    /// <param name="dstDir">既存の試聴先ディレクトリ。</param>
    /// <param name="wait">開始境界後に回収まで保持する既存の時間（ミリ秒）。</param>
    public temporarilyCopyFiles(IEnumerable<string> srcFiles, string sourceRoot, string dstDir, int wait = 0)
    {
        ArgumentNullException.ThrowIfNull(srcFiles);
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(dstDir);
        string[] sources = srcFiles.ToArray();
        if (!sources.All(LongPathFileSystem.FileExists)) { throw new ArgumentException(nameof(srcFiles)); }
        if (!LongPathFileSystem.DirectoryExists(dstDir)) { throw new DirectoryNotFoundException(dstDir); }
        sleep = Math.Max(0, wait);
        try
        {
            var copies = new List<(string Source, string Destination)>();
            foreach (string source in sources)
            {
                string relative = Path.GetRelativePath(sourceRoot, source);
                if (Path.IsPathRooted(relative) || relative == ".."
                    || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new ArgumentException(nameof(srcFiles));
                }
                string destination = Path.Combine(dstDir, relative);
                if (LongPathFileSystem.EntryExists(destination)) { continue; }
                EnsureDestinationDirectory(Path.GetDirectoryName(destination));
                copies.Add((source, destination));
            }
            copies.AsParallel().ForAll(copy =>
            {
                LongPathFileSystem.CopyFile(copy.Source, copy.Destination, overwrite: false);
                lock (copiedFilesDst) { copiedFilesDst.Add(copy.Destination); }
            });
        }
        catch (Exception copyFailure)
        {
            try { Dispose(); }
            catch (Exception cleanupFailure) { throw new AggregateException(copyFailure, cleanupFailure); }
            throw;
        }
    }

    private void EnsureDestinationDirectory(string directory)
    {
        var missing = new Stack<string>();
        while (!string.IsNullOrWhiteSpace(directory) && !LongPathFileSystem.DirectoryExists(directory))
        {
            missing.Push(directory);
            directory = Path.GetDirectoryName(directory);
        }
        while (missing.TryPop(out string path))
        {
            LongPathFileSystem.CreateDirectory(path);
            createdDirectories.Add(path);
        }
    }

    ~temporarilyCopyFiles() { Dispose(disposing: false); }
}
