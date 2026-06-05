using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2BuiltinCustomFolderSettings
{
    private const int RandomSelectMask = 0x1;
    private const int FavoriteMask = 0x2;
    private const int Top10Mask = 0x4;
    private const int PlayLevelMask = 0x8;
    private const int ClearMask = 0x10;
    private const int RankMask = 0x20;
    private const int IgnoreMask = 0x40;
    private const int InsaneMask = 0x80;

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public int CustomFolderMask { get; }

    public int TitleFlashHours { get; }

    public bool IncludeNewSongFolder { get; }

    public Lr2BuiltinCustomFolderSettings(int customFolderMask, int titleFlashHours, bool includeNewSongFolder)
    {
        CustomFolderMask = customFolderMask;
        TitleFlashHours = Math.Max(0, titleFlashHours);
        IncludeNewSongFolder = includeNewSongFolder;
    }

    public static Lr2BuiltinCustomFolderSettings Create(
        LR2Config config,
        IEnumerable<BMSFile> songRows,
        DateTime nowUtc)
    {
        int customFolderMask = Math.Max(0, config?.GetCustomFolderMask() ?? 0);
        int titleFlashHours = Math.Max(0, config?.GetTitleFlashHours() ?? 24);
        bool includeNewSongFolder = HasRecentSong(songRows, titleFlashHours, nowUtc);
        return new Lr2BuiltinCustomFolderSettings(customFolderMask, titleFlashHours, includeNewSongFolder);
    }

    public bool ShouldIncludeCustomFolderFile(string filePath, string lr2RootPath)
    {
        string relativePath = NormalizeCustomFolderRelativePath(filePath, lr2RootPath);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return true;
        }

        if (IsCourseFolder(relativePath))
        {
            return true;
        }
        if (IsExactCustomFolder(relativePath, "newsong.lr2folder"))
        {
            return IncludeNewSongFolder;
        }
        if (HasMask(RandomSelectMask) && IsUnderCustomFolderDirectory(relativePath, "RANDOM"))
        {
            return true;
        }
        if (HasMask(FavoriteMask) && IsExactCustomFolder(relativePath, "favorite.lr2folder"))
        {
            return true;
        }
        if (HasMask(Top10Mask) && IsExactCustomFolder(relativePath, "TOP10.lr2folder"))
        {
            return true;
        }
        if (HasMask(PlayLevelMask) && IsUnderCustomFolderDirectory(relativePath, "PLAYLEVEL"))
        {
            return true;
        }
        if (HasMask(ClearMask) && IsUnderCustomFolderDirectory(relativePath, "CLEAR"))
        {
            return true;
        }
        if (HasMask(RankMask) && IsUnderCustomFolderDirectory(relativePath, "RANK"))
        {
            return true;
        }
        if (HasMask(IgnoreMask) && IsExactCustomFolder(relativePath, "ignore.lr2folder"))
        {
            return true;
        }
        if (HasMask(InsaneMask)
            && (IsUnderCustomFolderDirectory(relativePath, "INSANE01")
                || IsUnderCustomFolderDirectory(relativePath, "INSANE02")))
        {
            return true;
        }
        return false;
    }

    internal static int ResolveBuiltinFolderType(string databasePath)
    {
        string relativePath = NormalizeCustomFolderRelativePath(databasePath, null);
        if (IsExactCustomFolder(relativePath, "newsong.lr2folder"))
        {
            return 3;
        }
        if (IsCourseFolder(relativePath))
        {
            return 6;
        }
        return 2;
    }

    private bool HasMask(int mask)
    {
        return (CustomFolderMask & mask) != 0;
    }

    private static bool HasRecentSong(IEnumerable<BMSFile> songRows, int titleFlashHours, DateTime nowUtc)
    {
        if (titleFlashHours <= 0)
        {
            return false;
        }

        int cutoff = ToUnixSeconds(nowUtc.ToUniversalTime()) - (titleFlashHours * 60 * 60);
        return (songRows ?? []).Any(song =>
        {
            if (song == null)
            {
                return false;
            }
            if (!song.adddate.HasValue || song.adddate <= 0)
            {
                return true;
            }
            return song.adddate > cutoff;
        });
    }

    private static int ToUnixSeconds(DateTime utc)
    {
        double seconds = Math.Floor((utc.ToUniversalTime() - UnixEpoch).TotalSeconds);
        if (seconds <= 0)
        {
            return 0;
        }
        return seconds >= int.MaxValue ? int.MaxValue : (int)seconds;
    }

    private static bool IsCourseFolder(string relativePath)
    {
        return IsExactCustomFolder(relativePath, "course1.lr2folder")
            || IsExactCustomFolder(relativePath, "course2.lr2folder")
            || IsExactCustomFolder(relativePath, "course3.lr2folder");
    }

    private static bool IsExactCustomFolder(string relativePath, string fileName)
    {
        return string.Equals(
            relativePath,
            "LR2files" + Path.DirectorySeparatorChar + "CustomFolder" + Path.DirectorySeparatorChar + fileName,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderCustomFolderDirectory(string relativePath, string directoryName)
    {
        string prefix = "LR2files" + Path.DirectorySeparatorChar
            + "CustomFolder" + Path.DirectorySeparatorChar
            + directoryName + Path.DirectorySeparatorChar;
        return !string.IsNullOrWhiteSpace(relativePath)
            && relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCustomFolderRelativePath(string path, string lr2RootPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized = path.Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            string root = NormalizeDirectoryPathOrNull(lr2RootPath);
            if (string.IsNullOrWhiteSpace(root) || !Lr2FolderPath.IsSameOrDescendant(normalized, root))
            {
                return null;
            }
            normalized = Path.GetFullPath(normalized)
                .Substring(root.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return normalized.StartsWith("LR2files" + Path.DirectorySeparatorChar + "CustomFolder" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? normalized
            : null;
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
