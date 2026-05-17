using System;
using System.Collections.Generic;
using System.Linq;

namespace Ribbit.BMS;

public static class IEnumerableExt
{
    public static IOrderedEnumerable<BMSFile.Chart.Note> OrderByNotes(this IEnumerable<BMSFile.Chart.Note> src)
    {
        if (src == null)
        {
            throw new ArgumentNullException("src");
        }
        return src.OrderBy((BMSFile.Chart.Note n) => n.Position).ThenBy(delegate (BMSFile.Chart.Note n)
        {
            switch ((BMSFile.Chart.Note.NoteType)((uint)n.Type & 0xFFFFFFF0u))
            {
                case BMSFile.Chart.Note.NoteType.NOTE_1P_LONG_END_ALL:
                case BMSFile.Chart.Note.NoteType.NOTE_2P_LONG_END_ALL:
                    return 0u;
                case BMSFile.Chart.Note.NoteType.NOTE_1P_LONG_START_ALL:
                case BMSFile.Chart.Note.NoteType.NOTE_2P_LONG_START_ALL:
                    return 1u;
                case BMSFile.Chart.Note.NoteType.NOTE_1P_VISIBLE_ALL:
                case BMSFile.Chart.Note.NoteType.NOTE_2P_VISIBLE_ALL:
                    return 2u;
                case BMSFile.Chart.Note.NoteType.NOTE_1P_BOMB_ALL:
                case BMSFile.Chart.Note.NoteType.NOTE_2P_BOMB_ALL:
                    return 3u;
                case BMSFile.Chart.Note.NoteType.NOTE_1P_INVISIBLE_ALL:
                case BMSFile.Chart.Note.NoteType.NOTE_2P_INVISIBLE_ALL:
                    return 4u;
                default:
                    return (uint)n.Type << 16;
            }
        });
    }
}
