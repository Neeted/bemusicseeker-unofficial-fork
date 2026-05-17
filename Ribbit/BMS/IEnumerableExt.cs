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
        return src.OrderBy(n => n.Position).ThenBy(delegate (BMSFile.Chart.Note n)
        {
            return (BMSFile.Chart.Note.NoteType)((uint)n.Type & 0xFFFFFFF0u) switch
            {
                BMSFile.Chart.Note.NoteType.NOTE_1P_LONG_END_ALL or BMSFile.Chart.Note.NoteType.NOTE_2P_LONG_END_ALL => 0u,
                BMSFile.Chart.Note.NoteType.NOTE_1P_LONG_START_ALL or BMSFile.Chart.Note.NoteType.NOTE_2P_LONG_START_ALL => 1u,
                BMSFile.Chart.Note.NoteType.NOTE_1P_VISIBLE_ALL or BMSFile.Chart.Note.NoteType.NOTE_2P_VISIBLE_ALL => 2u,
                BMSFile.Chart.Note.NoteType.NOTE_1P_BOMB_ALL or BMSFile.Chart.Note.NoteType.NOTE_2P_BOMB_ALL => 3u,
                BMSFile.Chart.Note.NoteType.NOTE_1P_INVISIBLE_ALL or BMSFile.Chart.Note.NoteType.NOTE_2P_INVISIBLE_ALL => 4u,
                _ => (uint)n.Type << 16,
            };
        });
    }
}
