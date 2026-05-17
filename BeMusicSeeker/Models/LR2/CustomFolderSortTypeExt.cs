using System.Collections.Generic;
using System.Linq;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.LR2;

internal static class CustomFolderSortTypeExt
{
    private static readonly Dictionary<LR2SongDBExtended.playlist.CustomFolderSortType, string[]> table = new Dictionary<LR2SongDBExtended.playlist.CustomFolderSortType, string[]>
    {
        {
            LR2SongDBExtended.playlist.CustomFolderSortType.NONE,
            new string[2]
            {
                string.Empty,
                "(無し)"
            }
        },
        {
            LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL,
            new string[2]
            {
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.level),
                "レベル"
            }
        },
        {
            LR2SongDBExtended.playlist.CustomFolderSortType.TITLE,
            new string[2]
            {
                SQLiteTable<LR2SongDB.song>.GetTableName() + "." + SQLiteTable<LR2SongDB.song>.GetColumnName((LR2SongDB.song e) => e.title),
                "タイトル"
            }
        },
        {
            LR2SongDBExtended.playlist.CustomFolderSortType.ARTIST,
            new string[2]
            {
                SQLiteTable<LR2SongDB.song>.GetTableName() + "." + SQLiteTable<LR2SongDB.song>.GetColumnName((LR2SongDB.song e) => e.artist),
                "アーティスト"
            }
        },
        {
            LR2SongDBExtended.playlist.CustomFolderSortType.SCORE,
            new string[2]
            {
                SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.rate),
                "スコア"
            }
        },
        {
            LR2SongDBExtended.playlist.CustomFolderSortType.MISS,
            new string[2]
            {
                SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.minbp),
                "ミスカウント"
            }
        },
        {
            LR2SongDBExtended.playlist.CustomFolderSortType.PLAYCOUNT,
            new string[2]
            {
                SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.playcount),
                "プレイカウント"
            }
        },
        {
            LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE,
            new string[2]
            {
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.adddate),
                "追加日時"
            }
        }
    };

    private static readonly Dictionary<string, LR2SongDBExtended.playlist.CustomFolderSortType> tableReverse0 = table.ToDictionary((KeyValuePair<LR2SongDBExtended.playlist.CustomFolderSortType, string[]> kv) => kv.Value[0], (KeyValuePair<LR2SongDBExtended.playlist.CustomFolderSortType, string[]> kv) => kv.Key);

    private static readonly Dictionary<string, LR2SongDBExtended.playlist.CustomFolderSortType> tableReverse1 = table.ToDictionary((KeyValuePair<LR2SongDBExtended.playlist.CustomFolderSortType, string[]> kv) => kv.Value[1], (KeyValuePair<LR2SongDBExtended.playlist.CustomFolderSortType, string[]> kv) => kv.Key);

    public static string ToColumnName(this LR2SongDBExtended.playlist.CustomFolderSortType ftype)
    {
        return table[ftype][0];
    }

    public static string ToDisplayName(this LR2SongDBExtended.playlist.CustomFolderSortType ftype)
    {
        return table[ftype][1];
    }

    public static string ToTypeName(this LR2SongDBExtended.playlist.CustomFolderSortType ftype)
    {
        return ftype.ToString();
    }

    public static LR2SongDBExtended.playlist.CustomFolderSortType FromColumnName(string cname)
    {
        return tableReverse0.TryGetValue(cname ?? string.Empty, LR2SongDBExtended.playlist.CustomFolderSortType.NONE);
    }

    public static LR2SongDBExtended.playlist.CustomFolderSortType FromDisplayName(string dname)
    {
        return tableReverse1.TryGetValue(dname ?? string.Empty, LR2SongDBExtended.playlist.CustomFolderSortType.NONE);
    }

    public static IEnumerable<LR2SongDBExtended.playlist.CustomFolderSortType> GetEnumerable()
    {
        return table.Select((KeyValuePair<LR2SongDBExtended.playlist.CustomFolderSortType, string[]> kv) => kv.Key);
    }

    public static IEnumerable<string> GetColumnNames()
    {
        return table.Select((KeyValuePair<LR2SongDBExtended.playlist.CustomFolderSortType, string[]> kv) => kv.Value[0]);
    }

    public static IEnumerable<string> GetDisplayNames()
    {
        return table.Select((KeyValuePair<LR2SongDBExtended.playlist.CustomFolderSortType, string[]> kv) => kv.Value[1]);
    }

    public static IEnumerable<string> GetTypeNames()
    {
        return from e in GetEnumerable()
               select e.ToString();
    }
}
