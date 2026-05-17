using System.Collections.Generic;
using System.Linq;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.LR2;

internal static class EntryUnitTypeExt
{
    private static readonly Dictionary<LR2SongDBExtended.playlist.EntryUnitType, string[]> table = new Dictionary<LR2SongDBExtended.playlist.EntryUnitType, string[]>
    {
        {
            LR2SongDBExtended.playlist.EntryUnitType.File,
            new string[2]
            {
                string.Empty,
                "ファイル"
            }
        },
        {
            LR2SongDBExtended.playlist.EntryUnitType.Folder,
            new string[2] { "folder", "フォルダ" }
        }
    };

    private static readonly Dictionary<string, LR2SongDBExtended.playlist.EntryUnitType> tableReverse0 = table.ToDictionary((KeyValuePair<LR2SongDBExtended.playlist.EntryUnitType, string[]> kv) => kv.Value[0], (KeyValuePair<LR2SongDBExtended.playlist.EntryUnitType, string[]> kv) => kv.Key);

    private static readonly Dictionary<string, LR2SongDBExtended.playlist.EntryUnitType> tableReverse1 = table.ToDictionary((KeyValuePair<LR2SongDBExtended.playlist.EntryUnitType, string[]> kv) => kv.Value[1], (KeyValuePair<LR2SongDBExtended.playlist.EntryUnitType, string[]> kv) => kv.Key);

    public static string ToStringName(this LR2SongDBExtended.playlist.EntryUnitType utype)
    {
        return table[utype][0];
    }

    public static string ToDisplayName(this LR2SongDBExtended.playlist.EntryUnitType utype)
    {
        return table[utype][1];
    }

    public static LR2SongDBExtended.playlist.EntryUnitType FromStringName(string sname)
    {
        return tableReverse0.TryGetValue(sname ?? string.Empty, LR2SongDBExtended.playlist.EntryUnitType.File);
    }

    public static LR2SongDBExtended.playlist.EntryUnitType FromDisplayName(string dname)
    {
        return tableReverse1.TryGetValue(dname ?? string.Empty, LR2SongDBExtended.playlist.EntryUnitType.File);
    }

    public static IEnumerable<LR2SongDBExtended.playlist.EntryUnitType> GetEnumerable()
    {
        return table.Select((KeyValuePair<LR2SongDBExtended.playlist.EntryUnitType, string[]> kv) => kv.Key);
    }

    public static IEnumerable<string> GetDisplayNames()
    {
        return table.Select((KeyValuePair<LR2SongDBExtended.playlist.EntryUnitType, string[]> kv) => kv.Value[1]);
    }

    public static IEnumerable<string> GetTypeNames()
    {
        return from e in GetEnumerable()
               select e.ToString();
    }
}
