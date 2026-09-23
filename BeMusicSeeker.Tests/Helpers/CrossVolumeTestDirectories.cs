using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

internal sealed class CrossVolumeTestDirectories : IDisposable
{
    private CrossVolumeTestDirectories(string sourceBaseDirectory, string destinationBaseDirectory)
    {
        SourceBaseDirectory = sourceBaseDirectory;
        DestinationBaseDirectory = destinationBaseDirectory;
    }

    public string SourceBaseDirectory { get; }

    public string DestinationBaseDirectory { get; }

    public static CrossVolumeTestDirectories CreateOrInconclusive(string testName)
    {
        List<string> baseDirectories = GetWritableBaseDirectories();
        for (int i = 0; i < baseDirectories.Count; i++)
        {
            for (int j = 0; j < baseDirectories.Count; j++)
            {
                if (i == j || IsSameVolumeRoot(baseDirectories[i], baseDirectories[j]))
                {
                    continue;
                }

                string suffix = SanitizePathPart(testName) + "_" + Guid.NewGuid().ToString("N");
                string sourceBaseDirectory = Path.Combine(baseDirectories[i], "BeMusicSeeker_CrossVolume_Source_" + suffix);
                string destinationBaseDirectory = Path.Combine(baseDirectories[j], "BeMusicSeeker_CrossVolume_Destination_" + suffix);
                LongPathFileSystem.CreateDirectory(sourceBaseDirectory);
                LongPathFileSystem.CreateDirectory(destinationBaseDirectory);
                return new CrossVolumeTestDirectories(sourceBaseDirectory, destinationBaseDirectory);
            }
        }

        Assert.Inconclusive("No writable cross-volume test directories were found.");
        throw new InvalidOperationException("Assert.Inconclusive should abort the test.");
    }

    public void Dispose()
    {
        DeleteDirectoryIfExists(SourceBaseDirectory);
        DeleteDirectoryIfExists(DestinationBaseDirectory);
    }

    private static List<string> GetWritableBaseDirectories()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in EnumerateCandidateBaseDirectories())
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string normalizedCandidate;
            try
            {
                normalizedCandidate = LongPathFileSystem.NormalizePathForStorage(candidate);
            }
            catch
            {
                continue;
            }

            if (!seen.Add(normalizedCandidate) || !CanWriteDirectory(normalizedCandidate))
            {
                continue;
            }

            result.Add(normalizedCandidate);
        }
        return result;
    }

    private static IEnumerable<string> EnumerateCandidateBaseDirectories()
    {
        yield return Path.GetTempPath();
        yield return Directory.GetCurrentDirectory();
        yield return AppDomain.CurrentDomain.BaseDirectory;

        string? directoryPath = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directoryPath))
        {
            if (File.Exists(Path.Combine(directoryPath, "BeMusicSeeker.sln")))
            {
                yield return directoryPath!;
                yield break;
            }

            directoryPath = Directory.GetParent(directoryPath)?.FullName;
        }
    }

    private static bool CanWriteDirectory(string directoryPath)
    {
        string testDirectoryPath = Path.Combine(directoryPath, ".bemusicseeker-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            LongPathFileSystem.CreateDirectory(testDirectoryPath);
            LongPathFileSystem.DeleteDirectory(testDirectoryPath, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameVolumeRoot(string firstPath, string secondPath)
    {
        string firstRoot = NormalizeRoot(firstPath);
        string secondRoot = NormalizeRoot(secondPath);
        return string.Equals(firstRoot, secondRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string path)
    {
        string normalizedPath = LongPathFileSystem.NormalizePathForStorage(path);
        string root = Path.GetPathRoot(normalizedPath) ?? string.Empty;
        return LongPathFileSystem.TrimTrailingDirectorySeparators(root);
    }

    private static string SanitizePathPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "test";
        }

        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidChar, '_');
        }
        return value;
    }

    private static void DeleteDirectoryIfExists(string directoryPath)
    {
        if (!LongPathFileSystem.DirectoryExists(directoryPath))
        {
            return;
        }

        foreach (string childFilePath in LongPathFileSystem.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            LongPathFileSystem.SetAttributes(childFilePath, FileAttributes.Normal);
        }

        foreach (string childDirectoryPath in LongPathFileSystem.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories))
        {
            LongPathFileSystem.SetAttributes(childDirectoryPath, FileAttributes.Normal);
        }

        LongPathFileSystem.SetAttributes(directoryPath, FileAttributes.Normal);
        LongPathFileSystem.DeleteDirectory(directoryPath, recursive: true);
    }
}
