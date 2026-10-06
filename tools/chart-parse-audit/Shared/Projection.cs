#nullable enable
using System.IO;
using Ribbit.BMS;
using Ribbit.Math;

namespace ChartParseAudit;

/// <summary>raw結果を共通の型付き位置列へ遅延射影します。整列用に保持するのは一小節のnote参照と元の順序だけです。</summary>
internal static class Projection
{
    /// <summary>初期BPM、小節Length/starttickと全元noteを位置・種別・Indexで突合できる順へ列挙します。Controlの重複列は含めません。</summary>
    public static IEnumerable<PositionRow> Rows(BMSFile file)
    {
        Number zero = Number.Ratio(0, 1);
        yield return new(new(-1, 0, zero, 0, 0, 0), "bpm", null, new("rational", Engine.Scalar(file.Bpm)));
        foreach (BMSFile.Chart measure in file.Measures)
        {
            yield return new(new(measure.Index, 1, zero, 0, 0, 0), "measure", measure.Time.Ticks, new("rational", Engine.Scalar(measure.Length)));
            var notes = new List<(BMSFile.Chart.Note Note, int Order)>();
            foreach (Func<IList<BMSFile.Chart.Note>> get in measure.GetPropertiesAllNotes)
                foreach (BMSFile.Chart.Note note in get()) notes.Add((note, notes.Count));
            notes.Sort((left, right) =>
            {
                int order = Engine.Scalar(left.Note.Position).CompareTo(Engine.Scalar(right.Note.Position));
                if (order == 0) order = ((uint)left.Note.Type).CompareTo((uint)right.Note.Type);
                if (order == 0) order = left.Note.Index.CompareTo(right.Note.Index);
                return order == 0 ? left.Order.CompareTo(right.Order) : order;
            });
            PositionKey? previous = null;
            int occurrence = 0;
            foreach ((BMSFile.Chart.Note note, _) in notes)
            {
                var key = new PositionKey(measure.Index, 2, Engine.Scalar(note.Position), (uint)note.Type, note.Index, 0);
                occurrence = previous == key ? occurrence + 1 : 0;
                previous = key;
                yield return new(key with { Occurrence = occurrence }, note.Type == BMSFile.Chart.Note.NoteType.BAR_LINE ? "bar-line" : "note", note.AbsoluteTime.Ticks, Typed(note.Value));
            }
        }
    }
    private static NoteValue Typed(object? value) => value switch
    {
        null => new("undefined"),
        Fraction fraction => new("rational", Engine.Scalar(fraction)),
#if CURRENT
        BmsNumber number => new("rational", Engine.Scalar(number)),
#endif
        TimeSpan span => new("ticks", Integer: span.Ticks),
        int number => new("int", Integer: number),
        double number => new("double", Real: number),
        _ => throw new InvalidDataException("Unsupported parser value: " + value.GetType().FullName)
    };
}
