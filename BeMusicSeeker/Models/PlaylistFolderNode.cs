using System;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

public enum PlaylistFolderNodeSpecialKind
{
    None = 0,
    NotOwned
}

public sealed class PlaylistFolderNode
{
    public string FolderName { get; }

    public PlaylistFolderNodeSpecialKind SpecialKind { get; }

    public bool IsSpecial => SpecialKind != PlaylistFolderNodeSpecialKind.None;

    public bool IsEditable => !IsSpecial;

    public bool IsDroppable => !IsSpecial;

    public string DisplayName
    {
        get
        {
            if (IsSpecial)
            {
                return SpecialKind.ToDisplayName();
            }
            if (!string.IsNullOrWhiteSpace(FolderName))
            {
                return FolderName;
            }
            return "(" + Resources.No_folder_name + ")";
        }
    }

    private PlaylistFolderNode(string folderName, PlaylistFolderNodeSpecialKind specialKind)
    {
        FolderName = folderName ?? string.Empty;
        SpecialKind = specialKind;
    }

    public static PlaylistFolderNode CreateFolder(string folderName)
    {
        return new PlaylistFolderNode(folderName, PlaylistFolderNodeSpecialKind.None);
    }

    public static PlaylistFolderNode CreateSpecial(PlaylistFolderNodeSpecialKind specialKind)
    {
        if (specialKind == PlaylistFolderNodeSpecialKind.None)
        {
            throw new ArgumentOutOfRangeException("specialKind");
        }
        return new PlaylistFolderNode(string.Empty, specialKind);
    }
}

internal static class PlaylistFolderNodeSpecialKindExt
{
    internal static string ToDisplayName(this PlaylistFolderNodeSpecialKind specialKind)
    {
        return specialKind switch
        {
            PlaylistFolderNodeSpecialKind.NotOwned => "[NO SONG]",
            _ => string.Empty
        };
    }
}
