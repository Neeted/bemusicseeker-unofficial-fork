using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BeMusicSeeker.Models.Utils;

internal static class LongPathFileSystem
{
    private const string ExtendedPathPrefix = @"\\?\";
    private const string ExtendedUncPathPrefix = @"\\?\UNC\";
    private const string UncPathPrefix = @"\\";

    public readonly struct FileMetadata
    {
        public FileMetadata(long length, DateTime lastWriteTimeUtc)
        {
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }

        public long Length { get; }

        public DateTime LastWriteTimeUtc { get; }
    }

    public static string NormalizePathForStorage(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        string normalized = RemoveExtendedPathPrefix(path);
        return IsFullyQualifiedWindowsPath(normalized)
            ? NormalizeFullyQualifiedWindowsPath(normalized)
            : Path.GetFullPath(normalized);
    }

    public static string TrimTrailingDirectorySeparators(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string root = Path.GetPathRoot(path);
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return root.TrimEnd(Path.AltDirectorySeparatorChar);
        }
        return trimmed;
    }

    public static bool IsSameOrDescendantDirectoryPath(string candidatePath, string ancestorPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(ancestorPath))
        {
            return false;
        }

        string normalizedCandidate = TrimTrailingDirectorySeparators(NormalizePathForStorage(candidatePath));
        string normalizedAncestor = TrimTrailingDirectorySeparators(NormalizePathForStorage(ancestorPath));
        return IsSameOrDescendantNormalizedDirectoryPath(normalizedCandidate, normalizedAncestor);
    }

    public static bool IsSameOrDescendantNormalizedDirectoryPath(string normalizedCandidate, string normalizedAncestor)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(normalizedAncestor))
        {
            return false;
        }
        if (string.Equals(normalizedCandidate, normalizedAncestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedCandidate.Length > normalizedAncestor.Length
            && normalizedCandidate.StartsWith(normalizedAncestor, StringComparison.OrdinalIgnoreCase)
            && (EndsWithDirectorySeparator(normalizedAncestor)
                || IsDirectorySeparator(normalizedCandidate[normalizedAncestor.Length]));
    }

    public static string ToExtendedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        string fullPath = RemoveExtendedPathPrefix(path);
        fullPath = IsFullyQualifiedWindowsPath(fullPath)
            ? NormalizeFullyQualifiedWindowsPath(fullPath)
            : Path.GetFullPath(fullPath);
        if (IsExtendedPath(fullPath) || IsDevicePath(fullPath))
        {
            return fullPath;
        }
        if (fullPath.StartsWith(UncPathPrefix, StringComparison.Ordinal))
        {
            return ExtendedUncPathPrefix + fullPath.Substring(UncPathPrefix.Length);
        }
        return ExtendedPathPrefix + fullPath;
    }

    public static byte[] ReadAllBytes(string path)
    {
        using FileStream stream = OpenRead(path);
        long length = stream.Length;
        if (length > int.MaxValue)
        {
            throw new IOException("File is too large to read into memory.");
        }

        var bytes = new byte[(int)length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of file.");
            }
            offset += read;
        }
        return bytes;
    }

    public static FileStream OpenRead(string path)
    {
        return new FileStream(ToExtendedPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share)
    {
        return new FileStream(ToExtendedPath(path), mode, access, share);
    }

    public static IEnumerable<string> ReadLines(string path, Encoding encoding)
    {
        using var reader = new StreamReader(OpenRead(path), encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            yield return line;
        }
    }

    public static bool FileExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            return File.Exists(ToExtendedPath(path));
        }
        catch (Exception ex) when (ex is IOException
            || ex is UnauthorizedAccessException
            || ex is ArgumentException
            || ex is NotSupportedException)
        {
            return false;
        }
    }

    public static bool DirectoryExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            return Directory.Exists(ToExtendedPath(path));
        }
        catch (Exception ex) when (ex is IOException
            || ex is UnauthorizedAccessException
            || ex is ArgumentException
            || ex is NotSupportedException)
        {
            return false;
        }
    }

    public static bool EntryExists(string path)
    {
        return FileExists(path) || DirectoryExists(path);
    }

    public static void CreateDirectory(string path)
    {
        Directory.CreateDirectory(ToExtendedPath(path));
    }

    public static void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        ThrowIfSamePath(sourcePath, destinationPath);
        if (!IsSameVolumeRoot(sourcePath, destinationPath))
        {
            MoveFileAcrossVolumeRoots(sourcePath, destinationPath, overwrite);
            return;
        }

        if (overwrite && FileExists(destinationPath))
        {
            ReplaceFileByMovingSource(sourcePath, destinationPath);
            return;
        }
        File.Move(ToExtendedPath(sourcePath), ToExtendedPath(destinationPath));
    }

    public static void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        File.Copy(ToExtendedPath(sourcePath), ToExtendedPath(destinationPath), overwrite);
    }

    public static void CopyDirectory(string sourcePath, string destinationPath, bool overwrite)
    {
        ThrowIfSamePath(sourcePath, destinationPath);
        ThrowIfDestinationIsInsideSourceDirectory(sourcePath, destinationPath);
        if (DirectoryExists(destinationPath) && !overwrite)
        {
            throw new IOException("Destination directory already exists.");
        }

        CreateDirectory(destinationPath);
        foreach (string sourceDirectoryPath in GetDirectories(sourcePath))
        {
            string destinationChildPath = Path.Combine(destinationPath, Path.GetFileName(sourceDirectoryPath));
            CopyDirectory(sourceDirectoryPath, destinationChildPath, overwrite);
        }
        foreach (string sourceFilePath in GetFiles(sourcePath))
        {
            string destinationFilePath = Path.Combine(destinationPath, Path.GetFileName(sourceFilePath));
            CopyFile(sourceFilePath, destinationFilePath, overwrite);
        }
    }

    public static void MoveDirectory(string sourcePath, string destinationPath, bool overwrite)
    {
        ThrowIfSamePath(sourcePath, destinationPath);
        ThrowIfDestinationIsInsideSourceDirectory(sourcePath, destinationPath);
        bool destinationExists = DirectoryExists(destinationPath);
        if (!overwrite && destinationExists)
        {
            throw new IOException("Destination directory already exists.");
        }

        if (!destinationExists)
        {
            if (IsSameVolumeRoot(sourcePath, destinationPath))
            {
                Directory.Move(ToExtendedPath(sourcePath), ToExtendedPath(destinationPath));
            }
            else
            {
                MoveDirectoryAcrossVolumeRoots(sourcePath, destinationPath, overwrite);
            }
            return;
        }

        MergeDirectory(sourcePath, destinationPath);
        DeleteDirectory(sourcePath, recursive: false);
    }

    public static void DeleteFile(string path)
    {
        File.Delete(ToExtendedPath(path));
    }

    public static void DeleteDirectory(string path, bool recursive)
    {
        Directory.Delete(ToExtendedPath(path), recursive);
    }

    public static string[] GetFiles(string path)
    {
        return Directory.GetFiles(ToExtendedPath(path))
            .Select(NormalizePathForStorage)
            .ToArray();
    }

    public static string[] GetDirectories(string path)
    {
        return Directory.GetDirectories(ToExtendedPath(path))
            .Select(NormalizePathForStorage)
            .ToArray();
    }

    public static IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        foreach (string filePath in Directory.EnumerateFiles(ToExtendedPath(path), searchPattern, searchOption))
        {
            yield return NormalizePathForStorage(filePath);
        }
    }

    public static IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        foreach (string directoryPath in Directory.EnumerateDirectories(ToExtendedPath(path), searchPattern, searchOption))
        {
            yield return NormalizePathForStorage(directoryPath);
        }
    }

    public static IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        foreach (string entryPath in Directory.EnumerateFileSystemEntries(ToExtendedPath(path), searchPattern, searchOption))
        {
            yield return NormalizePathForStorage(entryPath);
        }
    }

    public static FileAttributes GetAttributes(string path)
    {
        return File.GetAttributes(ToExtendedPath(path));
    }

    public static void SetAttributes(string path, FileAttributes attributes)
    {
        File.SetAttributes(ToExtendedPath(path), attributes);
    }

    public static void SetCreationTime(string path, bool isDirectory, DateTime creationTime)
    {
        if (isDirectory)
        {
            Directory.SetCreationTime(ToExtendedPath(path), creationTime);
        }
        else
        {
            File.SetCreationTime(ToExtendedPath(path), creationTime);
        }
    }

    public static void SetLastWriteTime(string path, bool isDirectory, DateTime lastWriteTime)
    {
        if (isDirectory)
        {
            Directory.SetLastWriteTime(ToExtendedPath(path), lastWriteTime);
        }
        else
        {
            File.SetLastWriteTime(ToExtendedPath(path), lastWriteTime);
        }
    }

    public static void SetLastAccessTime(string path, bool isDirectory, DateTime lastAccessTime)
    {
        if (isDirectory)
        {
            Directory.SetLastAccessTime(ToExtendedPath(path), lastAccessTime);
        }
        else
        {
            File.SetLastAccessTime(ToExtendedPath(path), lastAccessTime);
        }
    }

    public static DateTime GetLastWriteTimeUtc(string path)
    {
        return File.GetLastWriteTimeUtc(ToExtendedPath(path));
    }

    public static DateTime GetLastWriteTimeUtc(string path, bool isDirectory)
    {
        return isDirectory
            ? Directory.GetLastWriteTimeUtc(ToExtendedPath(path))
            : File.GetLastWriteTimeUtc(ToExtendedPath(path));
    }

    public static DateTime GetLastWriteTime(string path, bool isDirectory)
    {
        return isDirectory
            ? Directory.GetLastWriteTime(ToExtendedPath(path))
            : File.GetLastWriteTime(ToExtendedPath(path));
    }

    public static long GetFileLength(string path)
    {
        return new FileInfo(ToExtendedPath(path)).Length;
    }

    public static FileMetadata GetFileMetadata(string path)
    {
        var fileInfo = new FileInfo(ToExtendedPath(path));
        return new FileMetadata(fileInfo.Length, fileInfo.LastWriteTimeUtc);
    }

    private static void ReplaceFileByMovingSource(string sourcePath, string destinationPath)
    {
        string replacementPath = CreateTemporarySiblingPath(destinationPath);
        bool sourceMovedToReplacement = false;
        try
        {
            File.Move(ToExtendedPath(sourcePath), ToExtendedPath(replacementPath));
            sourceMovedToReplacement = true;
            try
            {
                File.Replace(ToExtendedPath(replacementPath), ToExtendedPath(destinationPath), null, ignoreMetadataErrors: true);
            }
            catch (Exception replaceException)
            {
                Exception restoreException = TryRestoreSourceAfterReplaceFailure(replacementPath, sourcePath);
                if (restoreException != null)
                {
                    throw new IOException("Failed to replace destination file and failed to restore source file.", new AggregateException(replaceException, restoreException));
                }
                throw;
            }
        }
        catch
        {
            if (!sourceMovedToReplacement)
            {
                TryDeleteTemporaryReplacement(replacementPath);
            }
            throw;
        }
    }

    private static void MoveFileAcrossVolumeRoots(string sourcePath, string destinationPath, bool overwrite)
    {
        if (overwrite && FileExists(destinationPath))
        {
            CopyAndReplaceFileAcrossVolumeRoots(sourcePath, destinationPath);
            DeleteFile(sourcePath);
            return;
        }

        CopyFile(sourcePath, destinationPath, overwrite);
        DeleteFile(sourcePath);
    }

    private static void CopyAndReplaceFileAcrossVolumeRoots(string sourcePath, string destinationPath)
    {
        string replacementPath = CreateTemporarySiblingPath(destinationPath);
        try
        {
            CopyFile(sourcePath, replacementPath, overwrite: false);
            File.Replace(ToExtendedPath(replacementPath), ToExtendedPath(destinationPath), null, ignoreMetadataErrors: true);
        }
        catch
        {
            TryDeleteTemporaryReplacement(replacementPath);
            throw;
        }
    }

    private static void MoveDirectoryAcrossVolumeRoots(string sourcePath, string destinationPath, bool overwrite)
    {
        CopyDirectory(sourcePath, destinationPath, overwrite);
        DeleteDirectory(sourcePath, recursive: true);
    }

    private static string CreateTemporarySiblingPath(string path)
    {
        string directoryPath = Path.GetDirectoryName(path) ?? string.Empty;
        string candidatePath;
        do
        {
            candidatePath = Path.Combine(directoryPath, ".bemusicseeker-replace-" + Guid.NewGuid().ToString("N") + ".tmp");
        }
        while (EntryExists(candidatePath));
        return candidatePath;
    }

    private static Exception TryRestoreSourceAfterReplaceFailure(string replacementPath, string sourcePath)
    {
        try
        {
            if (!FileExists(sourcePath) && FileExists(replacementPath))
            {
                File.Move(ToExtendedPath(replacementPath), ToExtendedPath(sourcePath));
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void TryDeleteTemporaryReplacement(string replacementPath)
    {
        try
        {
            if (FileExists(replacementPath))
            {
                DeleteFile(replacementPath);
            }
        }
        catch
        {
        }
    }

    private static void MergeDirectory(string sourcePath, string destinationPath)
    {
        CreateDirectory(destinationPath);
        foreach (string sourceDirectoryPath in GetDirectories(sourcePath))
        {
            string destinationChildPath = Path.Combine(destinationPath, Path.GetFileName(sourceDirectoryPath));
            if (DirectoryExists(destinationChildPath))
            {
                MergeDirectory(sourceDirectoryPath, destinationChildPath);
                DeleteDirectory(sourceDirectoryPath, recursive: false);
            }
            else
            {
                MoveDirectory(sourceDirectoryPath, destinationChildPath, overwrite: true);
            }
        }

        foreach (string sourceFilePath in GetFiles(sourcePath))
        {
            string destinationFilePath = Path.Combine(destinationPath, Path.GetFileName(sourceFilePath));
            MoveFile(sourceFilePath, destinationFilePath, overwrite: true);
        }
    }

    private static void ThrowIfSamePath(string sourcePath, string destinationPath)
    {
        string normalizedSourcePath = NormalizePathForStorage(sourcePath);
        string normalizedDestinationPath = NormalizePathForStorage(destinationPath);
        if (string.Equals(normalizedSourcePath, normalizedDestinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Source path and destination path are the same.");
        }
    }

    private static bool IsSameVolumeRoot(string sourcePath, string destinationPath)
    {
        string sourceRoot = NormalizePathRootForComparison(sourcePath);
        string destinationRoot = NormalizePathRootForComparison(destinationPath);
        return string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathRootForComparison(string path)
    {
        string normalizedPath = NormalizePathForStorage(path);
        string root = Path.GetPathRoot(normalizedPath) ?? string.Empty;
        return TrimTrailingDirectorySeparators(root);
    }

    private static void ThrowIfDestinationIsInsideSourceDirectory(string sourcePath, string destinationPath)
    {
        string normalizedSourcePath = NormalizeDirectoryPathForComparison(sourcePath);
        string normalizedDestinationPath = NormalizeDirectoryPathForComparison(destinationPath);
        if (!string.Equals(normalizedSourcePath, normalizedDestinationPath, StringComparison.OrdinalIgnoreCase)
            && IsSameOrDescendantNormalizedDirectoryPath(normalizedDestinationPath, normalizedSourcePath))
        {
            throw new IOException("Destination path is inside the source directory.");
        }
    }

    private static string NormalizeDirectoryPathForComparison(string path)
    {
        return TrimTrailingDirectorySeparators(NormalizePathForStorage(path));
    }

    private static bool EndsWithDirectorySeparator(string path)
    {
        return !string.IsNullOrEmpty(path)
            && IsDirectorySeparator(path[path.Length - 1]);
    }

    private static string RemoveExtendedPathPrefix(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }
        if (path.StartsWith(ExtendedUncPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return UncPathPrefix + path.Substring(ExtendedUncPathPrefix.Length);
        }
        if (path.StartsWith(ExtendedPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string suffix = path.Substring(ExtendedPathPrefix.Length);
            return IsDriveRootedPath(suffix)
                ? suffix
                : path;
        }
        return path;
    }

    private static bool IsDriveRootedPath(string path)
    {
        return path?.Length >= 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && IsDirectorySeparator(path[2]);
    }

    private static bool IsExtendedPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && path.StartsWith(ExtendedPathPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDevicePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && path.StartsWith(@"\\.\", StringComparison.Ordinal);
    }

    private static bool IsFullyQualifiedWindowsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        if (IsExtendedPath(path) || IsDevicePath(path))
        {
            return true;
        }
        if (IsDriveRootedPath(path))
        {
            return true;
        }
        return path.StartsWith(UncPathPrefix, StringComparison.Ordinal);
    }

    private static bool IsDirectorySeparator(char value)
    {
        return value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
    }

    private static string NormalizeFullyQualifiedWindowsPath(string path)
    {
        if (!NeedsFullPathNormalization(path))
        {
            return path;
        }

        if (IsDevicePath(path))
        {
            return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string root = GetFullyQualifiedRoot(normalized);
        if (string.IsNullOrWhiteSpace(root))
        {
            return normalized;
        }

        var parts = new List<string>();
        string remainder = normalized.Substring(root.Length);
        foreach (string part in remainder.Split(Path.DirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(part) || string.Equals(part, ".", StringComparison.Ordinal))
            {
                continue;
            }
            if (string.Equals(part, "..", StringComparison.Ordinal))
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                continue;
            }
            parts.Add(part);
        }

        return parts.Count == 0
            ? root
            : root + string.Join(Path.DirectorySeparatorChar.ToString(), parts);
    }

    private static bool NeedsFullPathNormalization(string path)
    {
        return path.IndexOf(Path.AltDirectorySeparatorChar) >= 0
            || path.IndexOf(@"\.", StringComparison.Ordinal) >= 0
            || path.EndsWith(@"\.", StringComparison.Ordinal)
            || path.IndexOf(@"\..", StringComparison.Ordinal) >= 0
            || path.EndsWith(@"\..", StringComparison.Ordinal);
    }

    private static string GetFullyQualifiedRoot(string path)
    {
        if (IsDriveRootedPath(path))
        {
            return path.Substring(0, 3);
        }
        if (!path.StartsWith(UncPathPrefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        int serverEnd = path.IndexOf(Path.DirectorySeparatorChar, UncPathPrefix.Length);
        if (serverEnd < 0)
        {
            return path;
        }
        int shareEnd = path.IndexOf(Path.DirectorySeparatorChar, serverEnd + 1);
        return shareEnd < 0
            ? path
            : path.Substring(0, shareEnd + 1);
    }
}
