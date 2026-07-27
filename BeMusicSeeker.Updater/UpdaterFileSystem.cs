using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Updater
{
    internal static class UpdaterFileSystem
    {
        private const string ExtendedPathPrefix = @"\\?\";
        private const string ExtendedUncPathPrefix = @"\\?\UNC\";
        private const string UncPathPrefix = @"\\";

        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path is required.");
            }
            return Path.GetFullPath(RemoveExtendedPathPrefix(path));
        }

        public static string ToExtendedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path is required.");
            }

            string normalized = Path.GetFullPath(RemoveExtendedPathPrefix(path));
            if (normalized.StartsWith(UncPathPrefix, StringComparison.Ordinal))
            {
                return ExtendedUncPathPrefix + normalized.Substring(UncPathPrefix.Length);
            }
            return ExtendedPathPrefix + normalized;
        }

        public static bool FileExists(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(ToExtendedPath(path));
        }

        public static bool DirectoryExists(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(ToExtendedPath(path));
        }

        public static bool EntryExists(string path)
        {
            return FileExists(path) || DirectoryExists(path);
        }

        public static FileStream OpenRead(string path)
        {
            return new FileStream(ToExtendedPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share)
        {
            return new FileStream(ToExtendedPath(path), mode, access, share);
        }

        public static void CreateDirectory(string path)
        {
            Directory.CreateDirectory(ToExtendedPath(path));
        }

        public static void CopyFile(string sourcePath, string destinationPath, bool overwrite)
        {
            File.Copy(ToExtendedPath(sourcePath), ToExtendedPath(destinationPath), overwrite);
        }

        public static void MoveFile(string sourcePath, string destinationPath)
        {
            File.Move(ToExtendedPath(sourcePath), ToExtendedPath(destinationPath));
        }

        public static void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        {
            File.Move(ToExtendedPath(sourcePath), ToExtendedPath(destinationPath), overwrite);
        }

        public static void MoveDirectory(string sourcePath, string destinationPath)
        {
            Directory.Move(ToExtendedPath(sourcePath), ToExtendedPath(destinationPath));
        }

        public static void DeleteFile(string path)
        {
            File.Delete(ToExtendedPath(path));
        }

        public static void DeleteDirectory(string path, bool recursive)
        {
            Directory.Delete(ToExtendedPath(path), recursive);
        }

        public static FileAttributes GetAttributes(string path)
        {
            return File.GetAttributes(ToExtendedPath(path));
        }

        public static IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
        {
            return Directory.EnumerateFiles(ToExtendedPath(path), searchPattern, searchOption)
                .Select(RemoveExtendedPathPrefix);
        }

        public static IEnumerable<string> EnumerateDirectories(string path, string searchPattern, SearchOption searchOption)
        {
            return Directory.EnumerateDirectories(ToExtendedPath(path), searchPattern, searchOption)
                .Select(RemoveExtendedPathPrefix);
        }

        public static IEnumerable<string> EnumerateFileSystemEntries(string path)
        {
            return Directory.EnumerateFileSystemEntries(ToExtendedPath(path))
                .Select(RemoveExtendedPathPrefix);
        }

        public static string[] ReadAllLines(string path)
        {
            using FileStream stream = OpenRead(path);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Add(line);
            }
            return [.. lines];
        }

        public static void WriteAllLines(string path, IEnumerable<string> lines)
        {
            using FileStream stream = Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            foreach (string line in lines ?? [])
            {
                writer.WriteLine(line);
            }
        }

        private static string RemoveExtendedPathPrefix(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
            if (path.StartsWith(ExtendedUncPathPrefix, StringComparison.Ordinal))
            {
                return UncPathPrefix + path.Substring(ExtendedUncPathPrefix.Length);
            }
            return path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal)
                ? path.Substring(ExtendedPathPrefix.Length)
                : path;
        }
    }
}
