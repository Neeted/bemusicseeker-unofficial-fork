using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FolderFileSourceClassificationRequest
{
    public string FilePath { get; set; }

    public string Lr2RootPath { get; set; }

    public string RootCustomFolderOutputBaseDir { get; set; }

    public IReadOnlyCollection<string> BuiltinSourceDirectories { get; set; } = [];
}

internal sealed class Lr2FolderFileSourceClassification
{
    public string DatabasePath { get; set; }

    public int FolderType { get; set; } = 2;

    public string ParentHash { get; set; }
}

internal static class Lr2FolderFileSourceClassifier
{
    internal static Lr2FolderFileSourceClassification Classify(Lr2FolderFileSourceClassificationRequest request)
    {
        request ??= new Lr2FolderFileSourceClassificationRequest();
        string filePath = NormalizeFilePathOrNull(request.FilePath);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new Lr2FolderFileSourceClassification
            {
                DatabasePath = request.FilePath
            };
        }

        if (IsUnderDirectory(filePath, request.RootCustomFolderOutputBaseDir))
        {
            return new Lr2FolderFileSourceClassification
            {
                DatabasePath = filePath,
                ParentHash = Lr2SongFolderParentNormalizer.RootParentHash
            };
        }

        if (TryClassifyBuiltinSource(filePath, request, out Lr2FolderFileSourceClassification builtinClassification))
        {
            return builtinClassification;
        }

        return new Lr2FolderFileSourceClassification
        {
            DatabasePath = filePath
        };
    }

    private static bool TryClassifyBuiltinSource(
        string filePath,
        Lr2FolderFileSourceClassificationRequest request,
        out Lr2FolderFileSourceClassification classification)
    {
        classification = null;
        List<string> sourceDirectories = [.. (request.BuiltinSourceDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))];
        if (sourceDirectories.Count == 0)
        {
            sourceDirectories = CreateDefaultBuiltinSourceDirectories(request.Lr2RootPath);
        }

        foreach (string sourceDirectory in sourceDirectories)
        {
            if (!IsUnderDirectory(filePath, sourceDirectory))
            {
                continue;
            }

            string databasePath = TryCreateLr2RootRelativePath(filePath, request.Lr2RootPath) ?? filePath;
            classification = new Lr2FolderFileSourceClassification
            {
                DatabasePath = databasePath,
                ParentHash = IsDirectChildFile(filePath, sourceDirectory)
                    ? Lr2SongFolderParentNormalizer.RootParentHash
                    : null
            };
            return true;
        }
        return false;
    }

    private static List<string> CreateDefaultBuiltinSourceDirectories(string lr2RootPath)
    {
        string root = NormalizeDirectoryPathOrNull(lr2RootPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return [];
        }
        return
        [
            Path.Combine(root, "LR2files", "CustomFolder")
        ];
    }

    private static string TryCreateLr2RootRelativePath(string filePath, string lr2RootPath)
    {
        string root = NormalizeDirectoryPathOrNull(lr2RootPath);
        if (string.IsNullOrWhiteSpace(root) || !Lr2FolderPath.IsSameOrDescendant(filePath, root))
        {
            return null;
        }

        string relative = filePath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrWhiteSpace(relative)
            ? null
            : relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static bool IsUnderDirectory(string filePath, string directoryPath)
    {
        string directory = NormalizeDirectoryPathOrNull(directoryPath);
        return !string.IsNullOrWhiteSpace(filePath)
            && !string.IsNullOrWhiteSpace(directory)
            && Lr2FolderPath.IsSameOrDescendant(filePath, directory);
    }

    private static bool IsDirectChildFile(string filePath, string directoryPath)
    {
        string parentDirectory = NormalizeDirectoryPathOrNull(Lr2FolderPath.SafeGetDirectoryName(filePath));
        string sourceDirectory = NormalizeDirectoryPathOrNull(directoryPath);
        return !string.IsNullOrWhiteSpace(parentDirectory)
            && !string.IsNullOrWhiteSpace(sourceDirectory)
            && string.Equals(parentDirectory, sourceDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFilePathOrNull(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizeDirectoryPathOrNull(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Lr2FolderPath.NormalizeDirectoryPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }
}
