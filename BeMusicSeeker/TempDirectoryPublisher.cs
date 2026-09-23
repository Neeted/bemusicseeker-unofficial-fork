using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BeMusicSeeker;

public static class TempDirectoryPublisher
{
    private const string AppTempDirectoryName = "BeMusicSeeker";
    private const string SessionDirectoryPrefix = "session-";
    private const string SessionMarkerFileName = ".bemusicseeker-temp-session";

    private static readonly List<string> DirList = [];
    private static readonly object SyncRoot = new();
    private static readonly string ManagedRootPath = Path.Combine(Path.GetTempPath(), AppTempDirectoryName);
    private static readonly string SessionDirectoryName = CreateSessionDirectoryName();
    private static readonly string SessionDirectoryPath = Path.Combine(ManagedRootPath, SessionDirectoryName);

    public static string Get()
    {
        return Get("work");
    }

    public static string Get(string purpose)
    {
        string normalizedPurpose = NormalizePurpose(purpose);
        string text = Path.Combine(SessionDirectoryPath, normalizedPurpose + "-" + Path.GetRandomFileName());
        EnsureSessionDirectory();
        Directory.CreateDirectory(text);
        lock (SyncRoot)
        {
            DirList.Add(text);
        }
        return text;
    }

    public static bool IsManagedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            string sessionPath = Path.GetFullPath(SessionDirectoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(sessionPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), sessionPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryDeleteManagedPath(string path, Action<string> logInfo = null, Action<string, Exception> logWarning = null)
    {
        if (!IsManagedPath(path))
        {
            return false;
        }

        if (TryDeletePath(path, logWarning))
        {
            lock (SyncRoot)
            {
                DirList.RemoveAll(dir => string.Equals(dir, path, StringComparison.OrdinalIgnoreCase));
            }
            logInfo?.Invoke("temp_cleanup deleted path=" + path);
            return true;
        }

        return false;
    }

    public static void StartCleanupStaleDirectoriesAsync(Action<string> logInfo = null, Action<string, Exception> logWarning = null)
    {
        Task.Run(() => CleanupStaleDirectories(logInfo, logWarning));
    }

    public static void RemoveAll()
    {
        RemoveAll(null);
    }

    public static void RemoveAll(Action<string, Exception> logWarning)
    {
        List<string> dirs;
        lock (SyncRoot)
        {
            dirs = [.. DirList];
            DirList.Clear();
        }

        dirs.Add(SessionDirectoryPath);
        foreach (string dir in dirs)
        {
            TryDeletePath(dir, logWarning);
        }
    }

    private static string CreateSessionDirectoryName()
    {
        return SessionDirectoryPrefix + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N");
    }

    private static void EnsureSessionDirectory()
    {
        Directory.CreateDirectory(SessionDirectoryPath);
        string markerPath = Path.Combine(SessionDirectoryPath, SessionMarkerFileName);
        if (!File.Exists(markerPath))
        {
            File.WriteAllText(markerPath, "BeMusicSeeker managed temporary session");
        }
    }

    private static string NormalizePurpose(string purpose)
    {
        string normalized = string.IsNullOrWhiteSpace(purpose) ? "work" : purpose.Trim();
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(invalidChar, '_');
        }
        return string.IsNullOrWhiteSpace(normalized) ? "work" : normalized;
    }

    private static void CleanupStaleDirectories(Action<string> logInfo, Action<string, Exception> logWarning)
    {
        try
        {
            if (!Directory.Exists(ManagedRootPath))
            {
                return;
            }

            int deletedCount = 0;
            foreach (string directoryPath in Directory.EnumerateDirectories(ManagedRootPath).Where(IsStaleSessionDirectory))
            {
                if (TryDeletePath(directoryPath, logWarning))
                {
                    deletedCount++;
                }
            }

            if (deletedCount > 0)
            {
                logInfo?.Invoke("temp_startup_cleanup deletedSessions=" + deletedCount + " root=" + ManagedRootPath);
            }
        }
        catch (Exception ex)
        {
            logWarning?.Invoke(ManagedRootPath, ex);
        }
    }

    private static bool IsStaleSessionDirectory(string path)
    {
        if (IsCurrentSessionDirectory(path))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(ManagedRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(path).StartsWith(SessionDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(path, SessionMarkerFileName));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCurrentSessionDirectory(string path)
    {
        try
        {
            return string.Equals(Path.GetFullPath(path), Path.GetFullPath(SessionDirectoryPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeletePath(string path, Action<string, Exception> logWarning)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
        }
        catch (Exception ex)
        {
            logWarning?.Invoke(path, ex);
        }

        return false;
    }
}
