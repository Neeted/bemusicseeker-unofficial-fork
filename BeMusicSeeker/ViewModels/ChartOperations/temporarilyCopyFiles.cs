using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

/// <summary>一時試聴の相対配置を作り、所有したファイルと空ディレクトリだけを回収します。既存素材と借用ディレクトリは所有しません。</summary>
internal sealed class temporarilyCopyFiles : IDisposable
{
    private readonly int sleep;
    private readonly List<string> copiedFilesDst = [];
    private readonly List<string> createdDirectories = [];
    private int disposed;
    private int cleanupFailed;

    /// <summary>回収終端後、所有した一時物を回収できなかったかを返します。詳細はログに残し、操作所有者が一度だけ通知します。</summary>
    internal bool HasCleanupFailure => Volatile.Read(ref cleanupFailed) != 0;

    /// <summary>コピー前に回収の所有者を作成します。コピー失敗時にも呼出元が回収結果を参照できます。</summary>
    /// <param name="wait">開始境界後に回収まで保持する既存の時間（ミリ秒）。</param>
    internal temporarilyCopyFiles(int wait = 0)
    {
        sleep = Math.Max(0, wait);
    }

    /// <summary>開始境界後の既存保持時間を待ち、所有物の回収を一回行います。回収できない一時物は元原因とpathを診断し、他の回収を続けます。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        if (sleep > 0) { Thread.Sleep(sleep); }
        copiedFilesDst.AsParallel().ForAll(dstFile =>
        {
            try
            {
                if (LongPathFileSystem.FileExists(dstFile)) { LongPathFileSystem.DeleteFile(dstFile); }
            }
            catch (Exception exception)
            {
                RecordCleanupFailure(exception, dstFile);
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
            catch (Exception exception) { RecordCleanupFailure(exception, directory); }
        }
    }

    private void RecordCleanupFailure(Exception failure, string path)
    {
        Interlocked.Exchange(ref cleanupFailed, 1);
        try { NLogWrapper.FileLogger?.Warn(failure, "temporary_preview_cleanup_failed path=" + path); }
        catch { /* 任意の診断失敗で、残る回収や元のコピー・再生結果を変更しません。 */ }
    }

    /// <summary>入力元ルート内のファイルを相対配置のまま並列コピーします。必ずusing内で呼び、成功分の所有物を失敗時も回収します。</summary>
    /// <param name="srcFiles">パッケージ内の対応譜面・音源・画像のファイル。</param>
    /// <param name="sourceRoot">相対配置の基準となるパッケージルート。</param>
    /// <param name="dstDir">既存の試聴先ディレクトリ。</param>
    /// <exception cref="ObjectDisposedException">既に回収を終えた場合。</exception>
    internal void CopyFiles(IEnumerable<string> srcFiles, string sourceRoot, string dstDir)
    {
        if (Volatile.Read(ref disposed) != 0) { throw new ObjectDisposedException(nameof(temporarilyCopyFiles)); }
        ArgumentNullException.ThrowIfNull(srcFiles);
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(dstDir);
        string[] sources = srcFiles.ToArray();
        if (!sources.All(LongPathFileSystem.FileExists)) { throw new ArgumentException(nameof(srcFiles)); }
        if (!LongPathFileSystem.DirectoryExists(dstDir)) { throw new DirectoryNotFoundException(dstDir); }
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
}
