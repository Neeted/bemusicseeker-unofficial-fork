#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.Runtime;

/// <summary>一つの所有者の一時領域と生存lease、開始済み回収の終端を管理します。</summary>
internal sealed class TemporaryDirectorySession
{
    private const string MarkerFileName = ".bemusicseeker-temp-session";
    private const string MarkerContent = "BeMusicSeeker managed temporary session v2";
    private readonly object syncRoot = new();
    private readonly string managedRootPath;
    private readonly string sessionPath;
    private readonly string cleanupMutexName;
    private FileStream? lease;
    private Task? cleanupTask;
    private Task? removeTask;
    private bool stopping;

    /// <summary>指定scopeに新しいセッションを割り当てます。既存セッションには再接続しません。</summary>
    internal TemporaryDirectorySession(string managedRootPath)
    {
        this.managedRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRootPath));
        sessionPath = Path.Combine(this.managedRootPath, "session-" + Guid.NewGuid().ToString("N"));
        cleanupMutexName = "Local\\BeMusicSeeker.TempCleanup." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(this.managedRootPath.ToUpperInvariant())));
    }

    /// <summary>指定用途の独立した領域を、作成印の書込みと排他保持の確立後に返します。I/O失敗は呼出し元へ伝播します。</summary>
    internal string Get(string? purpose)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            if (lease == null)
            {
                Directory.CreateDirectory(sessionPath);
                var marker = new FileStream(Path.Combine(sessionPath, MarkerFileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    marker.Write(Encoding.UTF8.GetBytes(MarkerContent));
                    marker.Flush(flushToDisk: true);
                    lease = marker;
                }
                catch
                {
                    marker.Dispose();
                    throw;
                }
            }

            string path = Path.Combine(sessionPath, NormalizePurpose(purpose) + "-" + Path.GetRandomFileName());
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>この所有者が割り当てたセッションと、その配下のパスかを判定します。</summary>
    internal bool IsManagedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return false; }
        try
        {
            string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return string.Equals(fullPath, sessionPath, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(sessionPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>所有するパスだけを削除します。通常の削除失敗は警告を通知し、falseを返します。</summary>
    internal bool TryDeleteManagedPath(string? path, Action<string>? logInfo = null, Action<string, Exception>? logWarning = null)
    {
        if (path == null || !IsManagedPath(path)) { return false; }
        if (!TryDeletePath(path, logWarning)) { return false; }
        logInfo?.Invoke("temp_cleanup deleted path=" + path);
        return true;
    }

    /// <summary>scope内の既知の非生存セッションを短期排他下で回収します。一度開始した処理の実完了を保持します。</summary>
    internal Task StartCleanupStaleDirectoriesAsync(Action<string>? logInfo = null, Action<string, Exception>? logWarning = null)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            return cleanupTask ??= Task.Run(() => CleanupStaleDirectories(logInfo, logWarning));
        }
    }

    /// <summary>取得を停止し、開始済み回収の実完了、生存leaseの解放、自セッション削除の順に一度だけ終端します。</summary>
    internal Task RemoveAllAsync(Action<string, Exception>? logWarning = null)
    {
        lock (syncRoot)
        {
            if (removeTask != null) { return removeTask; }
            stopping = true;
            Task cleanup = cleanupTask ?? Task.CompletedTask;
            return removeTask = Task.Run(() => CompleteAsync(cleanup, logWarning));
        }
    }

    private async Task CompleteAsync(Task cleanup, Action<string, Exception>? logWarning)
    {
        try { await cleanup.ConfigureAwait(false); }
        finally
        {
            lease?.Dispose();
            lease = null;
            TryDeletePath(sessionPath, logWarning);
        }
    }

    private void CleanupStaleDirectories(Action<string>? logInfo, Action<string, Exception>? logWarning)
    {
        try
        {
            // Mutexは取得・解放を同じ同期workerで行い、所有者の生存leaseとは独立させる。
            using var cleanupMutex = new Mutex(false, cleanupMutexName);
            try { cleanupMutex.WaitOne(); }
            catch (AbandonedMutexException) { }
            try
            {
                if (!Directory.Exists(managedRootPath) || IsReparsePoint(managedRootPath)) { return; }
                int deletedCount = 0;
                foreach (string path in Directory.EnumerateDirectories(managedRootPath, "session-*"))
                {
                    if (!string.Equals(path, sessionPath, StringComparison.OrdinalIgnoreCase) && TryDeleteStaleSession(path, logWarning))
                    {
                        deletedCount++;
                    }
                }
                if (deletedCount > 0)
                {
                    logInfo?.Invoke("temp_startup_cleanup deletedSessions=" + deletedCount + " root=" + managedRootPath);
                }
            }
            finally { cleanupMutex.ReleaseMutex(); }
        }
        catch (Exception ex) { logWarning?.Invoke(managedRootPath, ex); }
    }

    private static bool TryDeleteStaleSession(string path, Action<string, Exception>? logWarning)
    {
        string markerPath = Path.Combine(path, MarkerFileName);
        FileStream marker;
        try
        {
            if (IsReparsePoint(path) || IsReparsePoint(markerPath)) { return false; }
            // 共有違反・権限不明は所有を確認できないため、その候補を保持する。
            marker = new FileStream(markerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        try
        {
            using (marker)
            {
                if (marker.Length != Encoding.UTF8.GetByteCount(MarkerContent)) { return false; }
                using var reader = new StreamReader(marker, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                if (!string.Equals(reader.ReadToEnd(), MarkerContent, StringComparison.Ordinal)) { return false; }
            }
            // 新ownerは既存sessionへ再接続しない。markerを閉じた後もscope排他を保持して削除する。
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) { logWarning?.Invoke(path, ex); return false; }
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string NormalizePurpose(string? purpose)
    {
        string normalized = string.IsNullOrWhiteSpace(purpose) ? "work" : purpose.Trim();
        foreach (char invalidChar in Path.GetInvalidFileNameChars()) { normalized = normalized.Replace(invalidChar, '_'); }
        return string.IsNullOrWhiteSpace(normalized) ? "work" : normalized;
    }

    private static bool TryDeletePath(string path, Action<string, Exception>? logWarning)
    {
        try
        {
            if (File.Exists(path)) { File.Delete(path); return true; }
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); return true; }
        }
        catch (Exception ex) { logWarning?.Invoke(path, ex); }
        return false;
    }
}
