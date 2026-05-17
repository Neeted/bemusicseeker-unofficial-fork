#define TRACE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ribbit.Logging;
using Ribbit.Math;
using Ribbit.Util.Extensions;

namespace Ribbit.BMS;

public class BMSFile
{
    public sealed class InvalidBmsFileException : Exception
    {
        public string FileName { get; private set; }

        public InvalidBmsFileException()
        {
        }

        public InvalidBmsFileException(string message, string fileName)
            : base(message + Environment.NewLine + fileName)
        {
            FileName = fileName;
        }

        public InvalidBmsFileException(string message, string fileName, Exception inner)
            : base(message + Environment.NewLine + fileName, inner)
        {
            FileName = fileName;
        }

        public InvalidBmsFileException(string message) : base(message)
        {
        }

        public InvalidBmsFileException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public struct RandomNumber
    {
        public int Value;

        public int Range;

        public bool Used;

        public override readonly string ToString()
        {
            if (!Used)
            {
                return "(" + Value + "/" + Range + ")";
            }
            return Value + "/" + Range;
        }
    }

    [Flags]
    public enum Feature
    {
        NONE = 0,
        LONG_NOTE = 1,
        MINE_NOTE = 2,
        SOFT_LANDING = 4,
        STOP = 8,
        RANDOM = 0x10,
        BGA = 0x10000,
        STAGEFILE = 0x20000,
        BANNER = 0x40000,
        BACKBMP = 0x80000
    }

    public enum KeyType
    {
        KEYS5 = 5,
        KEYS7 = 7,
        KEYS9 = 9,
        KEYS10 = 10,
        KEYS14 = 14
    }

    public class Measure : IReadOnlyCollection<Chart>, IEnumerable<Chart>, IEnumerable
    {
        private class MeasureEnumerator(BMSFile.Measure measure) : IEnumerator<Chart>, IDisposable, IEnumerator
        {
            private readonly Measure measure = measure;

            private int index = -1;

            public Chart Current => measure[index];

            object IEnumerator.Current => Current;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                index++;
                if (index >= measure.Count)
                {
                    return false;
                }
                return true;
            }

            public void Reset()
            {
                index = -1;
            }
        }

        public class AllNotes : IEnumerable<Chart.Note>, IEnumerable
        {
            private class AllNotesEnumerator : IEnumerator<Chart.Note>, IDisposable, IEnumerator
            {
                private int measureIndex = -1;

                private int noteIndex = -1;

                private readonly AllNotes allnotes;

                private IList<Chart.Note> notes;

                public Chart.Note Current => notes[noteIndex];

                object IEnumerator.Current => Current;

                public AllNotesEnumerator(AllNotes allnotes)
                {
                    this.allnotes = allnotes ?? throw new ArgumentNullException("allnotes");
                    Reset();
                }

                public void Dispose()
                {
                    notes = null;
                }

                public bool MoveNext()
                {
                    noteIndex++;
                    while (measureIndex == -1 || noteIndex == notes.Count)
                    {
                        measureIndex++;
                        if (measureIndex == allnotes.measure.Count)
                        {
                            return false;
                        }
                        notes = allnotes.acc(allnotes.measure[measureIndex])();
                        noteIndex = 0;
                    }
                    return true;
                }

                public void Reset()
                {
                    measureIndex = -1;
                    noteIndex = -1;
                    notes = null;
                }
            }

            private readonly Measure measure;

            private readonly Func<Chart, Func<IList<Chart.Note>>> acc;

            private AllNotes()
            {
            }

            internal AllNotes(Measure measure, Func<Chart, Func<IList<Chart.Note>>> acc)
            {
                this.measure = measure ?? throw new ArgumentNullException("measure");
                this.acc = acc ?? throw new ArgumentNullException("acc");
            }

            public IEnumerator<Chart.Note> GetEnumerator()
            {
                return new AllNotesEnumerator(this);
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        private readonly Chart[] scores = new Chart[1000];

        public Chart this[int i]
        {
            get
            {
                if (scores[i] == null)
                {
                    if (LastIndex < i)
                    {
                        LastIndex = i;
                    }
                    scores[i] = new Chart(i);
                }
                return scores[i];
            }
            set
            {
                scores[i] = value ?? throw new InvalidOperationException("value should not be null");
                if (i > LastIndex)
                {
                    LastIndex = i;
                }
            }
        }

        public int Count => LastIndex + 1;

        public int LastIndex { get; private set; } = -1;

        public AllNotes ControlNotes { get; }

        public AllNotes BgaBaseNotes { get; }

        public AllNotes BgaPoorNotes { get; }

        public AllNotes BgaLayerNotes { get; }

        public AllNotes BgmNotes { get; }

        public ReadOnlyCollection<AllNotes> VisibleNotes1P { get; }

        public ReadOnlyCollection<AllNotes> VisibleNotes2P { get; }

        public ReadOnlyCollection<AllNotes> InvisibleNotes1P { get; }

        public ReadOnlyCollection<AllNotes> InvisibleNotes2P { get; }

        public ReadOnlyCollection<AllNotes> LongNotes1P { get; }

        public ReadOnlyCollection<AllNotes> LongNotes2P { get; }

        public ReadOnlyCollection<AllNotes> MineNotes1P { get; }

        public ReadOnlyCollection<AllNotes> MineNotes2P { get; }

        public Measure()
        {
            ControlNotes = new AllNotes(this, c => () => c.Control);
            BgaBaseNotes = new AllNotes(this, c => () => c.BgaBase);
            BgaPoorNotes = new AllNotes(this, c => () => c.BgaPoor);
            BgaLayerNotes = new AllNotes(this, c => () => c.BgaLayer);
            BgmNotes = new AllNotes(this, c => () => c.Bgm);
            VisibleNotes1P = (from i in Enumerable.Range(0, 9)
                              select new AllNotes(this, c => c.GetPropertiesAll1PVisNotes[i])).ToList().AsReadOnly();
            InvisibleNotes1P = (from i in Enumerable.Range(0, 9)
                                select new AllNotes(this, c => c.GetPropertiesAll1PInvNotes[i])).ToList().AsReadOnly();
            LongNotes1P = (from i in Enumerable.Range(0, 9)
                           select new AllNotes(this, c => c.GetPropertiesAll1PLngNotes[i])).ToList().AsReadOnly();
            MineNotes1P = (from i in Enumerable.Range(0, 9)
                           select new AllNotes(this, c => c.GetPropertiesAll1PMineNotes[i])).ToList().AsReadOnly();
            VisibleNotes2P = (from i in Enumerable.Range(0, 9)
                              select new AllNotes(this, c => c.GetPropertiesAll2PVisNotes[i])).ToList().AsReadOnly();
            InvisibleNotes2P = (from i in Enumerable.Range(0, 9)
                                select new AllNotes(this, c => c.GetPropertiesAll2PInvNotes[i])).ToList().AsReadOnly();
            LongNotes2P = (from i in Enumerable.Range(0, 9)
                           select new AllNotes(this, c => c.GetPropertiesAll2PLngNotes[i])).ToList().AsReadOnly();
            MineNotes2P = (from i in Enumerable.Range(0, 9)
                           select new AllNotes(this, c => c.GetPropertiesAll2PMineNotes[i])).ToList().AsReadOnly();
        }

        public IEnumerator<Chart> GetEnumerator()
        {
            return new MeasureEnumerator(this);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    public class Chart
    {
        public class Note
        {
            public enum NoteType : uint
            {
                INVALID = uint.MaxValue,
                BAR_LINE = 0u,
                BGM = 1u,
                BGA_BASE = 4u,
                BGA_POOR = 6u,
                BGA_LAYER = 7u,
                BPM = 3u,
                EX_BPM = 8u,
                STOP = 9u,
                NOTE_NONKEYS_ALL = 0u,
                NOTE_1P_VISIBLE_ALL = 16u,
                NOTE_1P_VISIBLE_01 = 17u,
                NOTE_1P_VISIBLE_02 = 18u,
                NOTE_1P_VISIBLE_03 = 19u,
                NOTE_1P_VISIBLE_04 = 20u,
                NOTE_1P_VISIBLE_05 = 21u,
                NOTE_1P_VISIBLE_06 = 22u,
                NOTE_1P_VISIBLE_07 = 23u,
                NOTE_1P_VISIBLE_08 = 24u,
                NOTE_1P_VISIBLE_09 = 25u,
                NOTE_2P_VISIBLE_ALL = 32u,
                NOTE_2P_VISIBLE_01 = 33u,
                NOTE_2P_VISIBLE_02 = 34u,
                NOTE_2P_VISIBLE_03 = 35u,
                NOTE_2P_VISIBLE_04 = 36u,
                NOTE_2P_VISIBLE_05 = 37u,
                NOTE_2P_VISIBLE_06 = 38u,
                NOTE_2P_VISIBLE_07 = 39u,
                NOTE_2P_VISIBLE_08 = 40u,
                NOTE_2P_VISIBLE_09 = 41u,
                NOTE_1P_INVISIBLE_ALL = 48u,
                NOTE_1P_INVISIBLE_01 = 49u,
                NOTE_1P_INVISIBLE_02 = 50u,
                NOTE_1P_INVISIBLE_03 = 51u,
                NOTE_1P_INVISIBLE_04 = 52u,
                NOTE_1P_INVISIBLE_05 = 53u,
                NOTE_1P_INVISIBLE_06 = 54u,
                NOTE_1P_INVISIBLE_07 = 55u,
                NOTE_1P_INVISIBLE_08 = 56u,
                NOTE_1P_INVISIBLE_09 = 57u,
                NOTE_2P_INVISIBLE_ALL = 64u,
                NOTE_2P_INVISIBLE_01 = 65u,
                NOTE_2P_INVISIBLE_02 = 66u,
                NOTE_2P_INVISIBLE_03 = 67u,
                NOTE_2P_INVISIBLE_04 = 68u,
                NOTE_2P_INVISIBLE_05 = 69u,
                NOTE_2P_INVISIBLE_06 = 70u,
                NOTE_2P_INVISIBLE_07 = 71u,
                NOTE_2P_INVISIBLE_08 = 72u,
                NOTE_2P_INVISIBLE_09 = 73u,
                NOTE_1P_LONG_START_ALL = 80u,
                NOTE_1P_LONG_01 = 81u,
                NOTE_1P_LONG_02 = 82u,
                NOTE_1P_LONG_03 = 83u,
                NOTE_1P_LONG_04 = 84u,
                NOTE_1P_LONG_05 = 85u,
                NOTE_1P_LONG_06 = 86u,
                NOTE_1P_LONG_07 = 87u,
                NOTE_1P_LONG_08 = 88u,
                NOTE_1P_LONG_09 = 89u,
                NOTE_2P_LONG_START_ALL = 96u,
                NOTE_2P_LONG_01 = 97u,
                NOTE_2P_LONG_02 = 98u,
                NOTE_2P_LONG_03 = 99u,
                NOTE_2P_LONG_04 = 100u,
                NOTE_2P_LONG_05 = 101u,
                NOTE_2P_LONG_06 = 102u,
                NOTE_2P_LONG_07 = 103u,
                NOTE_2P_LONG_08 = 104u,
                NOTE_2P_LONG_09 = 105u,
                NOTE_1P_LONG_END_ALL = 336u,
                NOTE_1P_LONG_END_01 = 337u,
                NOTE_1P_LONG_END_02 = 338u,
                NOTE_1P_LONG_END_03 = 339u,
                NOTE_1P_LONG_END_04 = 340u,
                NOTE_1P_LONG_END_05 = 341u,
                NOTE_1P_LONG_END_06 = 342u,
                NOTE_1P_LONG_END_07 = 343u,
                NOTE_1P_LONG_END_08 = 344u,
                NOTE_1P_LONG_END_09 = 345u,
                NOTE_2P_LONG_END_ALL = 352u,
                NOTE_2P_LONG_END_01 = 353u,
                NOTE_2P_LONG_END_02 = 354u,
                NOTE_2P_LONG_END_03 = 355u,
                NOTE_2P_LONG_END_04 = 356u,
                NOTE_2P_LONG_END_05 = 357u,
                NOTE_2P_LONG_END_06 = 358u,
                NOTE_2P_LONG_END_07 = 359u,
                NOTE_2P_LONG_END_08 = 360u,
                NOTE_2P_LONG_END_09 = 361u,
                NOTE_1P_BOMB_ALL = 208u,
                NOTE_1P_BOMB_01 = 209u,
                NOTE_1P_BOMB_02 = 210u,
                NOTE_1P_BOMB_03 = 211u,
                NOTE_1P_BOMB_04 = 212u,
                NOTE_1P_BOMB_05 = 213u,
                NOTE_1P_BOMB_06 = 214u,
                NOTE_1P_BOMB_07 = 215u,
                NOTE_1P_BOMB_08 = 216u,
                NOTE_1P_BOMB_09 = 217u,
                NOTE_2P_BOMB_ALL = 224u,
                NOTE_2P_BOMB_01 = 225u,
                NOTE_2P_BOMB_02 = 226u,
                NOTE_2P_BOMB_03 = 227u,
                NOTE_2P_BOMB_04 = 228u,
                NOTE_2P_BOMB_05 = 229u,
                NOTE_2P_BOMB_06 = 230u,
                NOTE_2P_BOMB_07 = 231u,
                NOTE_2P_BOMB_08 = 232u,
                NOTE_2P_BOMB_09 = 233u,
                NOTE_KEYNUM_MASK = 15u,
                NOTE_TYPE_MASK = 4294967280u,
                NOTE_2P_OFFSET = 16u
            }

            public TimeSpan AbsoluteTime = TimeSpan.Zero;

            public int Index;

            public Fraction MeasurePosition = 0L;

            public Fraction Position = 0L;

            public Fraction PositionTime = 0L;

            public NoteType Type;

            public object Value;

            public Chart Measure { get; }

            public TimeSpan PositionTimeSpan
            {
                get
                {
                    try
                    {
                        return new TimeSpan((600000000L * PositionTime).ToInt64());
                    }
                    catch
                    {
                        NLogWrapper.DebuggerLogger?.Error("BMS Parser: Arithmetic exception occuered on a fraction multiplying.");
                        return new TimeSpan((long)(600000000m * PositionTime.ToDecimal()));
                    }
                }
            }

            public Note(Chart measure, NoteType type)
            {
                Measure = measure ?? throw new ArgumentNullException("measure");
                Type = type;
            }
        }

        public TimeSpan Time = TimeSpan.Zero;

        internal const int numBgaNoteTypes = 3;

        internal const int num1PNoteKeysMax = 9;

        internal const int num2PNoteKeysMax = 9;

        public Fraction Length { get; set; } = 1L;

        public int Index { get; } = int.MaxValue;

        public List<Note> Bpm { get; private set; } = [];

        public List<Note> ExBpm { get; private set; } = [];

        public List<Note> Stop { get; private set; } = [];

        public ReadOnlyCollection<Note> BarLine { get; private set; }

        public List<Note> Control { get; set; } = [];

        public List<Note> BgaBase { get; private set; } = [];

        public List<Note> BgaPoor { get; private set; } = [];

        public List<Note> BgaLayer { get; private set; } = [];

        public List<Note> Bgm { get; private set; } = [];

        public List<Note> Note1PVis01 { get; private set; } = [];

        public List<Note> Note1PVis02 { get; private set; } = [];

        public List<Note> Note1PVis03 { get; private set; } = [];

        public List<Note> Note1PVis04 { get; private set; } = [];

        public List<Note> Note1PVis05 { get; private set; } = [];

        public List<Note> Note1PVis06 { get; private set; } = [];

        public List<Note> Note1PVis07 { get; private set; } = [];

        public List<Note> Note1PVis08 { get; private set; } = [];

        public List<Note> Note1PVis09 { get; private set; } = [];

        public List<Note> Note2PVis01 { get; private set; } = [];

        public List<Note> Note2PVis02 { get; private set; } = [];

        public List<Note> Note2PVis03 { get; private set; } = [];

        public List<Note> Note2PVis04 { get; private set; } = [];

        public List<Note> Note2PVis05 { get; private set; } = [];

        public List<Note> Note2PVis06 { get; private set; } = [];

        public List<Note> Note2PVis07 { get; private set; } = [];

        public List<Note> Note2PVis08 { get; private set; } = [];

        public List<Note> Note2PVis09 { get; private set; } = [];

        public List<Note> Note1PInv01 { get; private set; } = [];

        public List<Note> Note1PInv02 { get; private set; } = [];

        public List<Note> Note1PInv03 { get; private set; } = [];

        public List<Note> Note1PInv04 { get; private set; } = [];

        public List<Note> Note1PInv05 { get; private set; } = [];

        public List<Note> Note1PInv06 { get; private set; } = [];

        public List<Note> Note1PInv07 { get; private set; } = [];

        public List<Note> Note1PInv08 { get; private set; } = [];

        public List<Note> Note1PInv09 { get; private set; } = [];

        public List<Note> Note2PInv01 { get; private set; } = [];

        public List<Note> Note2PInv02 { get; private set; } = [];

        public List<Note> Note2PInv03 { get; private set; } = [];

        public List<Note> Note2PInv04 { get; private set; } = [];

        public List<Note> Note2PInv05 { get; private set; } = [];

        public List<Note> Note2PInv06 { get; private set; } = [];

        public List<Note> Note2PInv07 { get; private set; } = [];

        public List<Note> Note2PInv08 { get; private set; } = [];

        public List<Note> Note2PInv09 { get; private set; } = [];

        public List<Note> Note1PLng01 { get; private set; } = [];

        public List<Note> Note1PLng02 { get; private set; } = [];

        public List<Note> Note1PLng03 { get; private set; } = [];

        public List<Note> Note1PLng04 { get; private set; } = [];

        public List<Note> Note1PLng05 { get; private set; } = [];

        public List<Note> Note1PLng06 { get; private set; } = [];

        public List<Note> Note1PLng07 { get; private set; } = [];

        public List<Note> Note1PLng08 { get; private set; } = [];

        public List<Note> Note1PLng09 { get; private set; } = [];

        public List<Note> Note2PLng01 { get; private set; } = [];

        public List<Note> Note2PLng02 { get; private set; } = [];

        public List<Note> Note2PLng03 { get; private set; } = [];

        public List<Note> Note2PLng04 { get; private set; } = [];

        public List<Note> Note2PLng05 { get; private set; } = [];

        public List<Note> Note2PLng06 { get; private set; } = [];

        public List<Note> Note2PLng07 { get; private set; } = [];

        public List<Note> Note2PLng08 { get; private set; } = [];

        public List<Note> Note2PLng09 { get; private set; } = [];

        public List<Note> Note1PBom01 { get; private set; } = [];

        public List<Note> Note1PBom02 { get; private set; } = [];

        public List<Note> Note1PBom03 { get; private set; } = [];

        public List<Note> Note1PBom04 { get; private set; } = [];

        public List<Note> Note1PBom05 { get; private set; } = [];

        public List<Note> Note1PBom06 { get; private set; } = [];

        public List<Note> Note1PBom07 { get; private set; } = [];

        public List<Note> Note1PBom08 { get; private set; } = [];

        public List<Note> Note1PBom09 { get; private set; } = [];

        public List<Note> Note2PBom01 { get; private set; } = [];

        public List<Note> Note2PBom02 { get; private set; } = [];

        public List<Note> Note2PBom03 { get; private set; } = [];

        public List<Note> Note2PBom04 { get; private set; } = [];

        public List<Note> Note2PBom05 { get; private set; } = [];

        public List<Note> Note2PBom06 { get; private set; } = [];

        public List<Note> Note2PBom07 { get; private set; } = [];

        public List<Note> Note2PBom08 { get; private set; } = [];

        public List<Note> Note2PBom09 { get; private set; } = [];

        public ReadOnlyCollection<Func<IList<Note>>> GetPropertiesAllNotes { get; }

        public ReadOnlyCollection<Func<IList<Note>>> GetPropertiesAllControlNotes { get; }

        public ReadOnlyCollection<Func<IList<Note>>> GetPropertiesAllBgaNotes { get; }

        public ReadOnlyCollection<Func<IList<Note>>> GetPropertiesAllBgmNotes { get; }

        public ReadOnlyCollection<Func<IList<Note>>> GetPropertiesAll1PVisNotes { get; }

        public ReadOnlyCollection<Func<IList<Note>>> GetPropertiesAll2PVisNotes { get; }

        public ReadOnlyCollection<Func<IList<Note>>> GetPropertiesAll1PInvNotes { get; }

        public ReadOnlyCollection<Func<IList<Note>>> GetPropertiesAll2PInvNotes { get; }

        public ReadOnlyCollection<Func<IList<Note>>> GetPropertiesAll1PLngNotes { get; }

        public ReadOnlyCollection<Func<IList<Note>>> GetPropertiesAll2PLngNotes { get; }

        public ReadOnlyCollection<Func<IList<Note>>> GetPropertiesAll1PMineNotes { get; }

        public ReadOnlyCollection<Func<IList<Note>>> GetPropertiesAll2PMineNotes { get; }

        private ReadOnlyCollection<Action<List<Note>>> SetPropertiesAllNotes { get; }

        public Chart(int index)
        {
            Index = index;
            GetPropertiesAllNotes = new List<Func<IList<Note>>>(new ReadOnlyCollection<Func<IList<Note>>>[11]
            {
                (GetPropertiesAllControlNotes = new List<Func<IList<Note>>>
                {
                    () => Bpm,
                    () => ExBpm,
                    () => Stop,
                    () => BarLine
                }.AsReadOnly()),
                (GetPropertiesAllBgaNotes = new List<Func<IList<Note>>>
                {
                    () => BgaBase,
                    () => BgaPoor,
                    () => BgaLayer
                }.AsReadOnly()),
                (GetPropertiesAllBgmNotes = new List<Func<IList<Note>>>
                {
                    () => Bgm
                }.AsReadOnly()),
                (GetPropertiesAll1PVisNotes = new List<Func<IList<Note>>>
                {
                    () => Note1PVis01,
                    () => Note1PVis02,
                    () => Note1PVis03,
                    () => Note1PVis04,
                    () => Note1PVis05,
                    () => Note1PVis06,
                    () => Note1PVis07,
                    () => Note1PVis08,
                    () => Note1PVis09
                }.AsReadOnly()),
                (GetPropertiesAll2PVisNotes = new List<Func<IList<Note>>>
                {
                    () => Note2PVis01,
                    () => Note2PVis02,
                    () => Note2PVis03,
                    () => Note2PVis04,
                    () => Note2PVis05,
                    () => Note2PVis06,
                    () => Note2PVis07,
                    () => Note2PVis08,
                    () => Note2PVis09
                }.AsReadOnly()),
                (GetPropertiesAll1PInvNotes = new List<Func<IList<Note>>>
                {
                    () => Note1PInv01,
                    () => Note1PInv02,
                    () => Note1PInv03,
                    () => Note1PInv04,
                    () => Note1PInv05,
                    () => Note1PInv06,
                    () => Note1PInv07,
                    () => Note1PInv08,
                    () => Note1PInv09
                }.AsReadOnly()),
                (GetPropertiesAll2PInvNotes = new List<Func<IList<Note>>>
                {
                    () => Note2PInv01,
                    () => Note2PInv02,
                    () => Note2PInv03,
                    () => Note2PInv04,
                    () => Note2PInv05,
                    () => Note2PInv06,
                    () => Note2PInv07,
                    () => Note2PInv08,
                    () => Note2PInv09
                }.AsReadOnly()),
                (GetPropertiesAll1PLngNotes = new List<Func<IList<Note>>>
                {
                    () => Note1PLng01,
                    () => Note1PLng02,
                    () => Note1PLng03,
                    () => Note1PLng04,
                    () => Note1PLng05,
                    () => Note1PLng06,
                    () => Note1PLng07,
                    () => Note1PLng08,
                    () => Note1PLng09
                }.AsReadOnly()),
                (GetPropertiesAll2PLngNotes = new List<Func<IList<Note>>>
                {
                    () => Note2PLng01,
                    () => Note2PLng02,
                    () => Note2PLng03,
                    () => Note2PLng04,
                    () => Note2PLng05,
                    () => Note2PLng06,
                    () => Note2PLng07,
                    () => Note2PLng08,
                    () => Note2PLng09
                }.AsReadOnly()),
                (GetPropertiesAll1PMineNotes = new List<Func<IList<Note>>>
                {
                    () => Note1PBom01,
                    () => Note1PBom02,
                    () => Note1PBom03,
                    () => Note1PBom04,
                    () => Note1PBom05,
                    () => Note1PBom06,
                    () => Note1PBom07,
                    () => Note1PBom08,
                    () => Note1PBom09
                }.AsReadOnly()),
                (GetPropertiesAll2PMineNotes = new List<Func<IList<Note>>>
                {
                    () => Note2PBom01,
                    () => Note2PBom02,
                    () => Note2PBom03,
                    () => Note2PBom04,
                    () => Note2PBom05,
                    () => Note2PBom06,
                    () => Note2PBom07,
                    () => Note2PBom08,
                    () => Note2PBom09
                }.AsReadOnly())
            }.SelectMany(i => i)).AsReadOnly();
            SetPropertiesAllNotes = new List<Action<List<Note>>>
            {
                delegate(List<Note> l)
                {
                    Bpm = l;
                },
                delegate(List<Note> l)
                {
                    ExBpm = l;
                },
                delegate(List<Note> l)
                {
                    Stop = l;
                },
                delegate(List<Note> l)
                {
                    BarLine = l.AsReadOnly();
                },
                delegate(List<Note> l)
                {
                    BgaBase = l;
                },
                delegate(List<Note> l)
                {
                    BgaPoor = l;
                },
                delegate(List<Note> l)
                {
                    BgaLayer = l;
                },
                delegate(List<Note> l)
                {
                    Bgm = l;
                },
                delegate(List<Note> l)
                {
                    Note1PVis01 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PVis02 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PVis03 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PVis04 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PVis05 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PVis06 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PVis07 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PVis08 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PVis09 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PVis01 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PVis02 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PVis03 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PVis04 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PVis05 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PVis06 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PVis07 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PVis08 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PVis09 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PInv01 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PInv02 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PInv03 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PInv04 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PInv05 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PInv06 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PInv07 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PInv08 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PInv09 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PInv01 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PInv02 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PInv03 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PInv04 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PInv05 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PInv06 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PInv07 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PInv08 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PInv09 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PLng01 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PLng02 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PLng03 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PLng04 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PLng05 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PLng06 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PLng07 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PLng08 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PLng09 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PLng01 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PLng02 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PLng03 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PLng04 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PLng05 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PLng06 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PLng07 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PLng08 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PLng09 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PBom01 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PBom02 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PBom03 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PBom04 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PBom05 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PBom06 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PBom07 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PBom08 = l;
                },
                delegate(List<Note> l)
                {
                    Note1PBom09 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PBom01 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PBom02 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PBom03 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PBom04 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PBom05 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PBom06 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PBom07 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PBom08 = l;
                },
                delegate(List<Note> l)
                {
                    Note2PBom09 = l;
                }
            }.AsReadOnly();
            BarLine = new List<Note>
            {
                new(this, Note.NoteType.BAR_LINE)
                {
                    Position = 1L,
                    Value = index + 1
                }
            }.AsReadOnly();
        }

        public void SortAllNotes()
        {
            for (int i = 0; i < GetPropertiesAllNotes.Count; i++)
            {
                SetPropertiesAllNotes[i]([.. GetPropertiesAllNotes[i]().OrderByNotes()]);
            }
        }

        public void SortAllVisibleNotes()
        {
            Note1PVis01 = [.. Note1PVis01.OrderByNotes()];
            Note1PVis02 = [.. Note1PVis02.OrderByNotes()];
            Note1PVis03 = [.. Note1PVis03.OrderByNotes()];
            Note1PVis04 = [.. Note1PVis04.OrderByNotes()];
            Note1PVis05 = [.. Note1PVis05.OrderByNotes()];
            Note1PVis06 = [.. Note1PVis06.OrderByNotes()];
            Note1PVis07 = [.. Note1PVis07.OrderByNotes()];
            Note1PVis08 = [.. Note1PVis08.OrderByNotes()];
            Note1PVis09 = [.. Note1PVis09.OrderByNotes()];
            Note2PVis01 = [.. Note2PVis01.OrderByNotes()];
            Note2PVis02 = [.. Note2PVis02.OrderByNotes()];
            Note2PVis03 = [.. Note2PVis03.OrderByNotes()];
            Note2PVis04 = [.. Note2PVis04.OrderByNotes()];
            Note2PVis05 = [.. Note2PVis05.OrderByNotes()];
            Note2PVis06 = [.. Note2PVis06.OrderByNotes()];
            Note2PVis07 = [.. Note2PVis07.OrderByNotes()];
            Note2PVis08 = [.. Note2PVis08.OrderByNotes()];
            Note2PVis09 = [.. Note2PVis09.OrderByNotes()];
        }

        public void SortAllLongNotes()
        {
            Note1PLng01 = [.. Note1PLng01.OrderByNotes()];
            Note1PLng02 = [.. Note1PLng02.OrderByNotes()];
            Note1PLng03 = [.. Note1PLng03.OrderByNotes()];
            Note1PLng04 = [.. Note1PLng04.OrderByNotes()];
            Note1PLng05 = [.. Note1PLng05.OrderByNotes()];
            Note1PLng06 = [.. Note1PLng06.OrderByNotes()];
            Note1PLng07 = [.. Note1PLng07.OrderByNotes()];
            Note1PLng08 = [.. Note1PLng08.OrderByNotes()];
            Note1PLng09 = [.. Note1PLng09.OrderByNotes()];
            Note2PLng01 = [.. Note2PLng01.OrderByNotes()];
            Note2PLng02 = [.. Note2PLng02.OrderByNotes()];
            Note2PLng03 = [.. Note2PLng03.OrderByNotes()];
            Note2PLng04 = [.. Note2PLng04.OrderByNotes()];
            Note2PLng05 = [.. Note2PLng05.OrderByNotes()];
            Note2PLng06 = [.. Note2PLng06.OrderByNotes()];
            Note2PLng07 = [.. Note2PLng07.OrderByNotes()];
            Note2PLng08 = [.. Note2PLng08.OrderByNotes()];
            Note2PLng09 = [.. Note2PLng09.OrderByNotes()];
        }

        public void SortAllMineNotes()
        {
            Note1PBom01 = [.. Note1PBom01.OrderByNotes()];
            Note1PBom02 = [.. Note1PBom02.OrderByNotes()];
            Note1PBom03 = [.. Note1PBom03.OrderByNotes()];
            Note1PBom04 = [.. Note1PBom04.OrderByNotes()];
            Note1PBom05 = [.. Note1PBom05.OrderByNotes()];
            Note1PBom06 = [.. Note1PBom06.OrderByNotes()];
            Note1PBom07 = [.. Note1PBom07.OrderByNotes()];
            Note1PBom08 = [.. Note1PBom08.OrderByNotes()];
            Note1PBom09 = [.. Note1PBom09.OrderByNotes()];
            Note2PBom01 = [.. Note2PBom01.OrderByNotes()];
            Note2PBom02 = [.. Note2PBom02.OrderByNotes()];
            Note2PBom03 = [.. Note2PBom03.OrderByNotes()];
            Note2PBom04 = [.. Note2PBom04.OrderByNotes()];
            Note2PBom05 = [.. Note2PBom05.OrderByNotes()];
            Note2PBom06 = [.. Note2PBom06.OrderByNotes()];
            Note2PBom07 = [.. Note2PBom07.OrderByNotes()];
            Note2PBom08 = [.. Note2PBom08.OrderByNotes()];
            Note2PBom09 = [.. Note2PBom09.OrderByNotes()];
        }

        public void SortBgmNotes()
        {
            Bgm = [.. Bgm.OrderByNotes()];
        }
    }

    private int _lnObj = -1;

    private readonly List<RandomNumber> randomPattern = [];

    private IndexEncoding _indexEncoding = IndexEncoding.Base64;

    private static readonly Regex asciiPattern = new("[\\p{IsBasicLatin}]+", RegexOptions.Compiled);

    private static readonly Regex spaceAndReturnPattern = new("[\\s\\r\\n]", RegexOptions.Compiled);

    private static readonly Regex hangul5charasPattern = new("[\\p{IsHangulSyllables}]{5,}", RegexOptions.Compiled);

    private static readonly Regex japanese2charasPattern = new("[\\p{IsCJKUnifiedIdeographs}々ぁ-んァ-ヶ！-｠]{2,}", RegexOptions.Compiled);

    private static readonly Regex md5HashRegex = new("^[a-fA-F0-9]{32}$", RegexOptions.Compiled);

    private static readonly Encoding sjisEnc = Encoding.GetEncoding(932, new EncoderExceptionFallback(), new DecoderExceptionFallback());

    private static readonly Encoding koreanEnc = Encoding.GetEncoding(949, new EncoderExceptionFallback(), new DecoderExceptionFallback());

    private static readonly Encoding utf8Enc = Encoding.GetEncoding(65001, new EncoderExceptionFallback(), new DecoderExceptionFallback());

    private static readonly Encoding sjisEncDefault = Encoding.GetEncoding("shift_jis", new EncoderReplacementFallback(string.Empty), new DecoderReplacementFallback(string.Empty));

    public ReadOnlyCollection<RandomNumber> RandomPattern => randomPattern.AsReadOnly();

    public string Path { get; private set; }

    public string Md5 { get; private set; }

    public Encoding Encode { get; private set; } = sjisEncDefault;

    public Measure Measures { get; private set; } = new Measure();

    public string Title { get; private set; } = string.Empty;

    public string Subtitle { get; private set; } = string.Empty;

    public string Artist { get; private set; } = string.Empty;

    public string Subartist { get; private set; } = string.Empty;

    public string Genre { get; private set; } = string.Empty;

    public int? Rank { get; private set; }

    public double? Total { get; private set; }

    public double? Playlevel { get; private set; }

    public int? Difficulty { get; private set; }

    public TimeSpan Duration { get; private set; } = TimeSpan.Zero;

    public Fraction? Bpm { get; private set; }

    public Fraction? MinBpm { get; private set; }

    public Fraction? MaxBpm { get; private set; }

    public KeyType Keys { get; private set; } = KeyType.KEYS7;

    public Feature Attribute { get; private set; }

    public string Source { get; private set; }

    public string Stagefile { get; protected set; } = string.Empty;

    public string Banner { get; protected set; } = string.Empty;

    public string Backbmp { get; protected set; } = string.Empty;

    public string[] WavArray { get; private set; } = new string[4096];

    public string[] BmpArray { get; private set; } = new string[4096];

    protected Fraction[] BpmArray { get; private set; } = new Fraction[4096];

    protected Fraction[] StopArray { get; private set; } = new Fraction[4096];

    public Resources Resources { get; } = new Resources();

    public ReadOnlyCollection<int> NoteCount1P { get; private set; } = new List<int>(new int[9]).AsReadOnly();

    public ReadOnlyCollection<int> NoteCount2P { get; private set; } = new List<int>(new int[9]).AsReadOnly();

    public ReadOnlyCollection<int> NoteCount1PLN { get; private set; } = new List<int>(new int[9]).AsReadOnly();

    public ReadOnlyCollection<int> NoteCount2PLN { get; private set; } = new List<int>(new int[9]).AsReadOnly();

    public ReadOnlyCollection<int> NoteCount1PMN { get; private set; } = new List<int>(new int[9]).AsReadOnly();

    public ReadOnlyCollection<int> NoteCount2PMN { get; private set; } = new List<int>(new int[9]).AsReadOnly();

    public int TotalNoteCount => NoteCount1P.Sum() + NoteCount2P.Sum();

    public IndexEncoding IndexEncoding
    {
        get
        {
            return _indexEncoding;
        }
        private set
        {
            _indexEncoding = value;
            IndexMapper = IndexEncoding switch
            {
                IndexEncoding.Base16 => BMSBase64.MapToBase16Subset,
                IndexEncoding.Base36 => BMSBase64.MapToBase36Subset,
                IndexEncoding.Base64 => BMSBase64.MapToBase64Set,
                _ => throw new InvalidOperationException("Invalid program state."),
            };
        }
    }

    public ReadOnlyCollection<int> IndexMapper { get; private set; } = BMSBase64.MapToBase64Set;

    public BMSFile()
    {
        throw new NotSupportedException();
    }

    public BMSFile(string path, IReadOnlyCollection<RandomNumber> randomNumber)
        : this(path, (randomNumber == null) ? null : new Queue<int>(randomNumber.Select(r => r.Value)))
    {
    }

    public BMSFile(string path, Queue<int> randomPattern = null)
    {
        try
        {
            Create(path, randomPattern);
        }
        catch (Exception ex)
        {
            throw new InvalidBmsFileException(ex.Message, path, ex);
        }
    }

    private BMSFile(string path, string source, Encoding encode, string md5, Queue<int> randomPattern = null)
    {
        Path = path;
        Source = source;
        Encode = encode;
        Md5 = md5;
        try
        {
            Analyze(randomPattern);
        }
        catch (Exception ex)
        {
            throw new InvalidBmsFileException(ex.Message, path, ex);
        }
    }

    public void SetDefaultParameter(double playlevel = 0.0, int rank = 2, double? total = 0.0, int difficulty = 1)
    {
        if (playlevel < 0.0)
        {
            throw new ArgumentOutOfRangeException("playlevel");
        }
        if (rank < 0 || rank > 3)
        {
            throw new ArgumentOutOfRangeException("rank");
        }
        if (total < 0.0)
        {
            throw new ArgumentOutOfRangeException("total");
        }
        if (difficulty < 1)
        {
            throw new ArgumentOutOfRangeException("difficulty");
        }
        if (!Playlevel.HasValue)
        {
            Playlevel = playlevel;
        }
        if (!Rank.HasValue)
        {
            Rank = rank;
        }
        if (!Difficulty.HasValue)
        {
            Difficulty = difficulty;
        }
        if (!Total.HasValue)
        {
            if (total == 0.0 && TotalNoteCount > 0)
            {
                Total = getIIDXtotalValue();
            }
            else
            {
                Total = total;
            }
        }
    }

    private double getIIDXtotalValue()
    {
        return GetIIDXTotalValue(TotalNoteCount);
    }

    public static BMSFile Parse(string path)
    {
        return new BMSFile(path);
    }

    public static double GetIIDXTotalValue(int notes)
    {
        if (notes <= 0)
        {
            throw new ArgumentOutOfRangeException("notes");
        }
        return System.Math.Max(System.Math.Round(7.605 / (0.01 * (double)notes + 6.5) / 0.02, 0) * 0.02 * (double)notes, 260.0);
    }

    public BMSFile Create(IReadOnlyCollection<RandomNumber> randomNumber)
    {
        return new BMSFile(Path, Source, Encode, Md5, (randomPattern == null) ? null : new Queue<int>(randomNumber.Select(r => r.Value)));
    }

    public BMSFile Create(Queue<int> randomPattern = null)
    {
        return new BMSFile(Path, Source, Encode, Md5, randomPattern);
    }

    private void Create(string path, Queue<int> randomPattern = null)
    {
        if (path == null)
        {
            throw new ArgumentNullException("path");
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("File does not exist", path);
        }
        Path = path;
        Source = LoadFile(path);
        Analyze(randomPattern);
    }

    private string LoadFile(string filePath)
    {
        using FileStream fileStream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fileStream.Length == 0L)
        {
            throw new InvalidDataException(filePath + " is empty file.");
        }
        byte[] array = new byte[fileStream.Length];
        fileStream.Read(array, 0, array.Length);
        byte[] array2 = MD5.Create().ComputeHash(array);
        var stringBuilder = new StringBuilder();
        byte[] array3 = array2;
        foreach (byte b in array3)
        {
            stringBuilder.Append(b.ToString("x2"));
        }
        string text = stringBuilder.ToString();
        if (!md5HashRegex.IsMatch(text))
        {
            throw new Exception("Invalid MD5 string: " + text);
        }
        Md5 = text.ToLowerInvariant();
        string autoDetectedString = getAutoDetectedString(array, out Encoding enc);
        if (enc == null)
        {
            Encode = sjisEncDefault;
            return sjisEncDefault.GetString(array);
        }
        Encode = enc;
        return autoDetectedString;
    }

    private void Analyze(Queue<int> randomPattern = null)
    {
        if (Source == null)
        {
            throw new InvalidOperationException("Source cannot be null");
        }
        ParseMain(Source, randomPattern);
        SetIndexEncoding();
        ResolveNoteConflict();
        CalculateTiming();
        if (Measures.LastIndex < 0)
        {
            throw new InvalidDataException("Invalid BMS File: " + Path);
        }
    }

    private void ParseMain(string source, Queue<int> pattern = null)
    {
        randomPattern.Clear();
        var genRan = new Random();
        object[] selector(string s, int n)
        {
            int length = s.Length;
            int i;
            for (i = 0; i < length && s[i] != ' ' && s[i] != ':'; i++)
            {
            }
            return (i != length) ?
            [
                n + 1,
                s.Substring(0, i),
                (i == length - 1) ? string.Empty : s.Substring(i + 1, length - i - 1).Trim()
            ] :
            [
                n + 1,
                s,
                string.Empty
            ];
        }
        IEnumerable<object[]> enumerable = source.ReadLine().Select(selector);
        int lineNum = 0;
        IEnumerator<object[]> enumerator = enumerable.GetEnumerator();
        try
        {
            object[] cur = enumerator.Current;
            string command = string.Empty;
            string parameter = string.Empty;
            int ifDepth = 0;
            bool skipLines(Func<string, bool> stopCond)
            {
                while (enumerator.MoveNext())
                {
                    cur = enumerator.Current;
                    lineNum = (int)cur[0];
                    command = (string)cur[1];
                    parameter = (string)cur[2];
                    if (!string.IsNullOrWhiteSpace(command) && command[0] == '#')
                    {
                        string text = command.ToUpperInvariant();
                        switch (text)
                        {
                            case "#ELSEIF":
                            case "#ELSE":
                            case "#ENDIF":
                                ifDepth--;
                                break;
                        }
                        bool flag = stopCond(text);
                        NLogWrapper.DebuggerLogger?.Trace((flag ? "PARSE! " : "SKIPED ") + new string('\t', ifDepth) + command + " " + parameter);
                        switch (text)
                        {
                            case "#IF":
                            case "#ELSEIF":
                            case "#ELSE":
                                ifDepth++;
                                break;
                        }
                        if (flag)
                        {
                            return false;
                        }
                        switch (text)
                        {
                            case "#RANDOM":
                                {
                                    int num = parameter.TryParseOrDefault(0);
                                    if (num < 1)
                                    {
                                        NLogWrapper.GetLogger()?.Warn("BMS Parser: Invalid #RANDOM range l:" + lineNum + "|" + command + " " + parameter);
                                        if (pattern != null && pattern.Count != 0)
                                        {
                                            pattern.Dequeue();
                                        }
                                        randomPattern.Add(new RandomNumber
                                        {
                                            Value = 0,
                                            Range = num,
                                            Used = false
                                        });
                                    }
                                    else
                                    {
                                        int value = 0;
                                        if (pattern != null && pattern.Count != 0)
                                        {
                                            value = pattern.Dequeue();
                                        }
                                        randomPattern.Add(new RandomNumber
                                        {
                                            Value = value,
                                            Range = num,
                                            Used = false
                                        });
                                    }
                                    break;
                                }
                            case "#STAGEFILE":
                                Resources.AddFilePath(parameter);
                                break;
                            case "#BANNER":
                                Resources.AddFilePath(parameter);
                                break;
                            case "#BACKBMP":
                                Resources.AddFilePath(parameter);
                                break;
                            default:
                                if (command.Length == 6)
                                {
                                    string text2 = command.Substring(0, 4).ToUpperInvariant();
                                    if (!(text2 == "#WAV"))
                                    {
                                        if (text2 == "#BMP" && command.ReplaceFromStart("#BMP", string.Empty, isIgnoreCase: true).IsBMSBase64())
                                        {
                                            Resources.AddFilePath(parameter);
                                        }
                                    }
                                    else if (command.ReplaceFromStart("#WAV", string.Empty, isIgnoreCase: true).IsBMSBase64())
                                    {
                                        Resources.AddFilePath(parameter);
                                    }
                                }
                                break;
                        }
                    }
                }
                return true;
            }
            bool parseLines(Func<string, bool> stopCond)
            {
                int? num = null;
                while (enumerator.MoveNext())
                {
                    cur = enumerator.Current;
                    lineNum = (int)cur[0];
                    command = (string)cur[1];
                    parameter = (string)cur[2];
                    if (!string.IsNullOrWhiteSpace(command) && command[0] == '#')
                    {
                        string text = command.ToUpperInvariant();
                        switch (text)
                        {
                            case "#ELSEIF":
                            case "#ELSE":
                            case "#ENDIF":
                                ifDepth--;
                                break;
                        }
                        bool flag = stopCond(text);
                        NLogWrapper.DebuggerLogger?.Trace("PARSE! " + new string('\t', ifDepth) + command + " " + parameter);
                        if (flag)
                        {
                            switch (text)
                            {
                                case "#IF":
                                case "#ELSEIF":
                                case "#ELSE":
                                    ifDepth++;
                                    break;
                            }
                            return false;
                        }
                        switch (text)
                        {
                            case "#RANDOM":
                                {
                                    int num7 = parameter.TryParseOrDefault(0);
                                    if (num7 < 1)
                                    {
                                        NLogWrapper.GetLogger()?.Warn("BMS Parser: Invalid #RANDOM range l:" + lineNum + "|" + command + " " + parameter);
                                        if (pattern != null && pattern.Count != 0)
                                        {
                                            pattern.Dequeue();
                                        }
                                        num = null;
                                        randomPattern.Add(new RandomNumber
                                        {
                                            Value = 0,
                                            Range = num7,
                                            Used = false
                                        });
                                    }
                                    else
                                    {
                                        num = ((pattern == null || pattern.Count == 0) ? new int?(genRan.Next(1, num7 + 1)) : new int?(pattern.Dequeue()));
                                        randomPattern.Add(new RandomNumber
                                        {
                                            Value = num.Value,
                                            Range = num7,
                                            Used = true
                                        });
                                    }
                                    break;
                                }
                            case "#IF":
                                {
                                    int curIfDepth = ifDepth;
                                    ifDepth++;
                                    while (true)
                                    {
                                        int num6 = parameter.TryParseOrDefault(0);
                                        if (num.HasValue && num6 != 0 && num == num6)
                                        {
                                            if (!parseLines(l => ifDepth == curIfDepth && (l == "#ELSEIF" || l == "#ELSE" || l == "#ENDIF")))
                                            {
                                                switch (text)
                                                {
                                                    case "#ELSEIF":
                                                    case "#ELSE":
                                                        if (skipLines(l => ifDepth == curIfDepth && l == "#ENDIF"))
                                                        {
                                                            return true;
                                                        }
                                                        break;
                                                }
                                                break;
                                            }
                                            return true;
                                        }
                                        if (!skipLines(l => ifDepth == curIfDepth && (l == "#ELSEIF" || l == "#ELSE" || l == "#ENDIF")))
                                        {
                                            switch (text)
                                            {
                                                case "#ELSEIF":
                                                    continue;
                                                case "#ELSE":
                                                    if (parseLines(l => ifDepth == curIfDepth && l == "#ENDIF"))
                                                    {
                                                        return true;
                                                    }
                                                    break;
                                            }
                                            break;
                                        }
                                        return true;
                                    }
                                    break;
                                }
                            case "#TITLE":
                                Title = parameter;
                                break;
                            case "#SUBTITLE":
                                Subtitle = parameter;
                                break;
                            case "#ARTIST":
                                Artist = parameter;
                                break;
                            case "#SUBARTIST":
                                Subartist = parameter;
                                break;
                            case "#GENRE":
                                Genre = parameter;
                                break;
                            case "#LNOBJ":
                                if (parameter.IsBMSBase64())
                                {
                                    int num8 = BMSBase64.ToInt(parameter);
                                    if (num8 > 0)
                                    {
                                        _lnObj = num8;
                                    }
                                    else
                                    {
                                        NLogWrapper.GetLogger()?.Warn("BMS Parser: Invalid #LNOBJ l:" + lineNum + "|" + command + " " + parameter);
                                    }
                                }
                                break;
                            case "#BPM":
                                {
                                    if (double.TryParse(parameter, out double result6) && result6 > 0.0)
                                    {
                                        Bpm = new Fraction(result6);
                                    }
                                    else
                                    {
                                        NLogWrapper.GetLogger()?.Warn("BMS Parser: Invalid #BPM l:" + lineNum + "|" + command + " " + parameter);
                                    }
                                    break;
                                }
                            case "#RANK":
                                {
                                    if (int.TryParse(parameter, out int result9) && result9 >= 0)
                                    {
                                        Rank = result9;
                                    }
                                    else
                                    {
                                        NLogWrapper.GetLogger()?.Warn("BMS Parser: Invalid #RANK l:" + lineNum + "|" + command + " " + parameter);
                                    }
                                    break;
                                }
                            case "#TOTAL":
                                {
                                    if (double.TryParse(parameter, out double result7) && result7 > 0.0)
                                    {
                                        Total = result7;
                                    }
                                    else
                                    {
                                        NLogWrapper.GetLogger()?.Warn("BMS Parser: Invalid #TOTAL l:" + lineNum + "|" + command + " " + parameter);
                                    }
                                    break;
                                }
                            case "#STAGEFILE":
                                Stagefile = Resources.AddFilePath(parameter) ?? Stagefile;
                                break;
                            case "#BANNER":
                                Banner = Resources.AddFilePath(parameter) ?? Banner;
                                break;
                            case "#BACKBMP":
                                Backbmp = Resources.AddFilePath(parameter) ?? Backbmp;
                                break;
                            case "#PLAYLEVEL":
                                {
                                    if (double.TryParse(parameter, out double result8) && result8 >= 0.0)
                                    {
                                        Playlevel = result8;
                                    }
                                    else
                                    {
                                        NLogWrapper.GetLogger()?.Warn("BMS Parser: Invalid #PLAYLEVEL l:" + lineNum + "|" + command + " " + parameter);
                                    }
                                    break;
                                }
                            case "#DIFFICULTY":
                                {
                                    if (int.TryParse(parameter, out int result5) && result5 > 0)
                                    {
                                        Difficulty = result5;
                                    }
                                    else
                                    {
                                        NLogWrapper.GetLogger()?.Warn("BMS Parser: Invalid #DIFFICULTY l:" + lineNum + "|" + command + " " + parameter);
                                    }
                                    break;
                                }
                            default:
                                switch (command.Length)
                                {
                                    case 6:
                                        switch (command.Substring(0, 4).ToUpperInvariant())
                                        {
                                            case "#WAV":
                                                {
                                                    string s = command.ReplaceFromStart("#WAV", string.Empty, isIgnoreCase: true);
                                                    if (s.IsBMSBase64())
                                                    {
                                                        int num4 = BMSBase64.ToInt(s);
                                                        if (num4 != 0)
                                                        {
                                                            WavArray[num4] = Resources.AddFilePath(parameter) ?? WavArray[num4];
                                                        }
                                                    }
                                                    break;
                                                }
                                            case "#BMP":
                                                {
                                                    string s = command.ReplaceFromStart("#BMP", string.Empty, isIgnoreCase: true);
                                                    if (s.IsBMSBase64())
                                                    {
                                                        int num3 = BMSBase64.ToInt(s);
                                                        if (num3 != 0)
                                                        {
                                                            BmpArray[num3] = Resources.AddFilePath(parameter) ?? BmpArray[num3];
                                                        }
                                                    }
                                                    break;
                                                }
                                            case "#BPM":
                                                {
                                                    string s = command.ReplaceFromStart("#BPM", string.Empty, isIgnoreCase: true);
                                                    if (s.IsBMSBase64())
                                                    {
                                                        int num5 = BMSBase64.ToInt(s);
                                                        if (num5 != 0)
                                                        {
                                                            if (decimal.TryParse(parameter, out decimal result4) && result4 > 0m)
                                                            {
                                                                BpmArray[num5] = result4;
                                                            }
                                                            else
                                                            {
                                                                NLogWrapper.GetLogger()?.Warn("BMS Parser: Invalid #BPMxx l:" + lineNum + "|" + command + " " + parameter);
                                                            }
                                                        }
                                                    }
                                                    break;
                                                }
                                            default:
                                                {
                                                    string text3 = new(command.ToCharArray(), 1, 3);
                                                    string text4 = command.Substring(4, 2).ToUpperInvariant();
                                                    int num2 = text3.TryParseOrDefault(-1);
                                                    parameter = parameter.Replace(" ", string.Empty);
                                                    List<Chart.Note> list = null;
                                                    Chart.Note.NoteType type = Chart.Note.NoteType.BAR_LINE;
                                                    if (num2 >= 0 && num2 < 1000)
                                                    {
                                                        switch (text4)
                                                        {
                                                            case "02":
                                                                {
                                                                    if (double.TryParse(parameter, out double result2) && result2 > 0.0)
                                                                    {
                                                                        Measures[num2].Length = new Fraction(result2);
                                                                    }
                                                                    goto end_IL_0c52;
                                                                }
                                                            case "03":
                                                                list = Measures[num2].Bpm;
                                                                type = Chart.Note.NoteType.BPM;
                                                                foreach (var item4 in parameter.Split(2).Select((idx, pos) => new
                                                                {
                                                                    Idx = idx,
                                                                    Pos = pos
                                                                }))
                                                                {
                                                                    if (!(item4.Idx == "00") && int.TryParse(item4.Idx, NumberStyles.HexNumber, null, out int result3))
                                                                    {
                                                                        var item2 = new Chart.Note(Measures[num2], type)
                                                                        {
                                                                            Value = result3,
                                                                            Position = new Fraction(item4.Pos, parameter.Length / 2)
                                                                        };
                                                                        list.Add(item2);
                                                                    }
                                                                }
                                                                goto end_IL_0c52;
                                                            case "08":
                                                                list = Measures[num2].ExBpm;
                                                                type = Chart.Note.NoteType.EX_BPM;
                                                                goto case "NOTE_COMMON";
                                                            case "09":
                                                                list = Measures[num2].Stop;
                                                                type = Chart.Note.NoteType.STOP;
                                                                Attribute |= Feature.STOP;
                                                                goto case "NOTE_COMMON";
                                                            case "04":
                                                                list = Measures[num2].BgaBase;
                                                                type = Chart.Note.NoteType.BGA_BASE;
                                                                goto case "NOTE_COMMON";
                                                            case "06":
                                                                list = Measures[num2].BgaPoor;
                                                                type = Chart.Note.NoteType.BGA_POOR;
                                                                goto case "NOTE_COMMON";
                                                            case "07":
                                                                list = Measures[num2].BgaLayer;
                                                                type = Chart.Note.NoteType.BGA_LAYER;
                                                                goto case "NOTE_COMMON";
                                                            case "01":
                                                                list = Measures[num2].Bgm;
                                                                type = Chart.Note.NoteType.BGM;
                                                                goto case "NOTE_COMMON";
                                                            case "11":
                                                                list = Measures[num2].Note1PVis01;
                                                                type = Chart.Note.NoteType.NOTE_1P_VISIBLE_01;
                                                                goto case "NOTE_COMMON";
                                                            case "12":
                                                                list = Measures[num2].Note1PVis02;
                                                                type = Chart.Note.NoteType.NOTE_1P_VISIBLE_02;
                                                                goto case "NOTE_COMMON";
                                                            case "13":
                                                                list = Measures[num2].Note1PVis03;
                                                                type = Chart.Note.NoteType.NOTE_1P_VISIBLE_03;
                                                                goto case "NOTE_COMMON";
                                                            case "14":
                                                                list = Measures[num2].Note1PVis04;
                                                                type = Chart.Note.NoteType.NOTE_1P_VISIBLE_04;
                                                                goto case "NOTE_COMMON";
                                                            case "15":
                                                                list = Measures[num2].Note1PVis05;
                                                                type = Chart.Note.NoteType.NOTE_1P_VISIBLE_05;
                                                                goto case "NOTE_COMMON";
                                                            case "16":
                                                                list = Measures[num2].Note1PVis06;
                                                                type = Chart.Note.NoteType.NOTE_1P_VISIBLE_06;
                                                                goto case "NOTE_COMMON";
                                                            case "17":
                                                                list = Measures[num2].Note1PVis07;
                                                                type = Chart.Note.NoteType.NOTE_1P_VISIBLE_07;
                                                                goto case "NOTE_COMMON";
                                                            case "18":
                                                                list = Measures[num2].Note1PVis08;
                                                                type = Chart.Note.NoteType.NOTE_1P_VISIBLE_08;
                                                                goto case "NOTE_COMMON";
                                                            case "19":
                                                                list = Measures[num2].Note1PVis09;
                                                                type = Chart.Note.NoteType.NOTE_1P_VISIBLE_09;
                                                                goto case "NOTE_COMMON";
                                                            case "21":
                                                                list = Measures[num2].Note2PVis01;
                                                                type = Chart.Note.NoteType.NOTE_2P_VISIBLE_01;
                                                                goto case "NOTE_COMMON";
                                                            case "22":
                                                                list = Measures[num2].Note2PVis02;
                                                                type = Chart.Note.NoteType.NOTE_2P_VISIBLE_02;
                                                                goto case "NOTE_COMMON";
                                                            case "23":
                                                                list = Measures[num2].Note2PVis03;
                                                                type = Chart.Note.NoteType.NOTE_2P_VISIBLE_03;
                                                                goto case "NOTE_COMMON";
                                                            case "24":
                                                                list = Measures[num2].Note2PVis04;
                                                                type = Chart.Note.NoteType.NOTE_2P_VISIBLE_04;
                                                                goto case "NOTE_COMMON";
                                                            case "25":
                                                                list = Measures[num2].Note2PVis05;
                                                                type = Chart.Note.NoteType.NOTE_2P_VISIBLE_05;
                                                                goto case "NOTE_COMMON";
                                                            case "26":
                                                                list = Measures[num2].Note2PVis06;
                                                                type = Chart.Note.NoteType.NOTE_2P_VISIBLE_06;
                                                                goto case "NOTE_COMMON";
                                                            case "27":
                                                                list = Measures[num2].Note2PVis07;
                                                                type = Chart.Note.NoteType.NOTE_2P_VISIBLE_07;
                                                                goto case "NOTE_COMMON";
                                                            case "28":
                                                                list = Measures[num2].Note2PVis08;
                                                                type = Chart.Note.NoteType.NOTE_2P_VISIBLE_08;
                                                                goto case "NOTE_COMMON";
                                                            case "29":
                                                                list = Measures[num2].Note2PVis09;
                                                                type = Chart.Note.NoteType.NOTE_2P_VISIBLE_09;
                                                                goto case "NOTE_COMMON";
                                                            case "31":
                                                                list = Measures[num2].Note1PInv01;
                                                                type = Chart.Note.NoteType.NOTE_1P_INVISIBLE_01;
                                                                goto case "NOTE_COMMON";
                                                            case "32":
                                                                list = Measures[num2].Note1PInv02;
                                                                type = Chart.Note.NoteType.NOTE_1P_INVISIBLE_02;
                                                                goto case "NOTE_COMMON";
                                                            case "33":
                                                                list = Measures[num2].Note1PInv03;
                                                                type = Chart.Note.NoteType.NOTE_1P_INVISIBLE_03;
                                                                goto case "NOTE_COMMON";
                                                            case "34":
                                                                list = Measures[num2].Note1PInv04;
                                                                type = Chart.Note.NoteType.NOTE_1P_INVISIBLE_04;
                                                                goto case "NOTE_COMMON";
                                                            case "35":
                                                                list = Measures[num2].Note1PInv05;
                                                                type = Chart.Note.NoteType.NOTE_1P_INVISIBLE_05;
                                                                goto case "NOTE_COMMON";
                                                            case "36":
                                                                list = Measures[num2].Note1PInv06;
                                                                type = Chart.Note.NoteType.NOTE_1P_INVISIBLE_06;
                                                                goto case "NOTE_COMMON";
                                                            case "37":
                                                                list = Measures[num2].Note1PInv07;
                                                                type = Chart.Note.NoteType.NOTE_1P_INVISIBLE_07;
                                                                goto case "NOTE_COMMON";
                                                            case "38":
                                                                list = Measures[num2].Note1PInv08;
                                                                type = Chart.Note.NoteType.NOTE_1P_INVISIBLE_08;
                                                                goto case "NOTE_COMMON";
                                                            case "39":
                                                                list = Measures[num2].Note1PInv09;
                                                                type = Chart.Note.NoteType.NOTE_1P_INVISIBLE_09;
                                                                goto case "NOTE_COMMON";
                                                            case "41":
                                                                list = Measures[num2].Note2PInv01;
                                                                type = Chart.Note.NoteType.NOTE_2P_INVISIBLE_01;
                                                                goto case "NOTE_COMMON";
                                                            case "42":
                                                                list = Measures[num2].Note2PInv02;
                                                                type = Chart.Note.NoteType.NOTE_2P_INVISIBLE_02;
                                                                goto case "NOTE_COMMON";
                                                            case "43":
                                                                list = Measures[num2].Note2PInv03;
                                                                type = Chart.Note.NoteType.NOTE_2P_INVISIBLE_03;
                                                                goto case "NOTE_COMMON";
                                                            case "44":
                                                                list = Measures[num2].Note2PInv04;
                                                                type = Chart.Note.NoteType.NOTE_2P_INVISIBLE_04;
                                                                goto case "NOTE_COMMON";
                                                            case "45":
                                                                list = Measures[num2].Note2PInv05;
                                                                type = Chart.Note.NoteType.NOTE_2P_INVISIBLE_05;
                                                                goto case "NOTE_COMMON";
                                                            case "46":
                                                                list = Measures[num2].Note2PInv06;
                                                                type = Chart.Note.NoteType.NOTE_2P_INVISIBLE_06;
                                                                goto case "NOTE_COMMON";
                                                            case "47":
                                                                list = Measures[num2].Note2PInv07;
                                                                type = Chart.Note.NoteType.NOTE_2P_INVISIBLE_07;
                                                                goto case "NOTE_COMMON";
                                                            case "48":
                                                                list = Measures[num2].Note2PInv08;
                                                                type = Chart.Note.NoteType.NOTE_2P_INVISIBLE_08;
                                                                goto case "NOTE_COMMON";
                                                            case "49":
                                                                list = Measures[num2].Note2PInv09;
                                                                type = Chart.Note.NoteType.NOTE_2P_INVISIBLE_09;
                                                                goto case "NOTE_COMMON";
                                                            case "51":
                                                                list = Measures[num2].Note1PLng01;
                                                                type = Chart.Note.NoteType.NOTE_1P_LONG_01;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "52":
                                                                list = Measures[num2].Note1PLng02;
                                                                type = Chart.Note.NoteType.NOTE_1P_LONG_02;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "53":
                                                                list = Measures[num2].Note1PLng03;
                                                                type = Chart.Note.NoteType.NOTE_1P_LONG_03;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "54":
                                                                list = Measures[num2].Note1PLng04;
                                                                type = Chart.Note.NoteType.NOTE_1P_LONG_04;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "55":
                                                                list = Measures[num2].Note1PLng05;
                                                                type = Chart.Note.NoteType.NOTE_1P_LONG_05;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "56":
                                                                list = Measures[num2].Note1PLng06;
                                                                type = Chart.Note.NoteType.NOTE_1P_LONG_06;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "57":
                                                                list = Measures[num2].Note1PLng07;
                                                                type = Chart.Note.NoteType.NOTE_1P_LONG_07;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "58":
                                                                list = Measures[num2].Note1PLng08;
                                                                type = Chart.Note.NoteType.NOTE_1P_LONG_08;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "59":
                                                                list = Measures[num2].Note1PLng09;
                                                                type = Chart.Note.NoteType.NOTE_1P_LONG_09;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "61":
                                                                list = Measures[num2].Note2PLng01;
                                                                type = Chart.Note.NoteType.NOTE_2P_LONG_01;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "62":
                                                                list = Measures[num2].Note2PLng02;
                                                                type = Chart.Note.NoteType.NOTE_2P_LONG_02;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "63":
                                                                list = Measures[num2].Note2PLng03;
                                                                type = Chart.Note.NoteType.NOTE_2P_LONG_03;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "64":
                                                                list = Measures[num2].Note2PLng04;
                                                                type = Chart.Note.NoteType.NOTE_2P_LONG_04;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "65":
                                                                list = Measures[num2].Note2PLng05;
                                                                type = Chart.Note.NoteType.NOTE_2P_LONG_05;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "66":
                                                                list = Measures[num2].Note2PLng06;
                                                                type = Chart.Note.NoteType.NOTE_2P_LONG_06;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "67":
                                                                list = Measures[num2].Note2PLng07;
                                                                type = Chart.Note.NoteType.NOTE_2P_LONG_07;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "68":
                                                                list = Measures[num2].Note2PLng08;
                                                                type = Chart.Note.NoteType.NOTE_2P_LONG_08;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "69":
                                                                list = Measures[num2].Note2PLng09;
                                                                type = Chart.Note.NoteType.NOTE_2P_LONG_09;
                                                                Attribute |= Feature.LONG_NOTE;
                                                                goto case "NOTE_COMMON";
                                                            case "D1":
                                                                list = Measures[num2].Note1PBom01;
                                                                type = Chart.Note.NoteType.NOTE_1P_BOMB_01;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "D2":
                                                                list = Measures[num2].Note1PBom02;
                                                                type = Chart.Note.NoteType.NOTE_1P_BOMB_02;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "D3":
                                                                list = Measures[num2].Note1PBom03;
                                                                type = Chart.Note.NoteType.NOTE_1P_BOMB_03;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "D4":
                                                                list = Measures[num2].Note1PBom04;
                                                                type = Chart.Note.NoteType.NOTE_1P_BOMB_04;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "D5":
                                                                list = Measures[num2].Note1PBom05;
                                                                type = Chart.Note.NoteType.NOTE_1P_BOMB_05;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "D6":
                                                                list = Measures[num2].Note1PBom06;
                                                                type = Chart.Note.NoteType.NOTE_1P_BOMB_06;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "D7":
                                                                list = Measures[num2].Note1PBom07;
                                                                type = Chart.Note.NoteType.NOTE_1P_BOMB_07;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "D8":
                                                                list = Measures[num2].Note1PBom08;
                                                                type = Chart.Note.NoteType.NOTE_1P_BOMB_08;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "D9":
                                                                list = Measures[num2].Note1PBom09;
                                                                type = Chart.Note.NoteType.NOTE_1P_BOMB_09;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "E1":
                                                                list = Measures[num2].Note2PBom01;
                                                                type = Chart.Note.NoteType.NOTE_2P_BOMB_01;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "E2":
                                                                list = Measures[num2].Note2PBom02;
                                                                type = Chart.Note.NoteType.NOTE_2P_BOMB_02;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "E3":
                                                                list = Measures[num2].Note2PBom03;
                                                                type = Chart.Note.NoteType.NOTE_2P_BOMB_03;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "E4":
                                                                list = Measures[num2].Note2PBom04;
                                                                type = Chart.Note.NoteType.NOTE_2P_BOMB_04;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "E5":
                                                                list = Measures[num2].Note2PBom05;
                                                                type = Chart.Note.NoteType.NOTE_2P_BOMB_05;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "E6":
                                                                list = Measures[num2].Note2PBom06;
                                                                type = Chart.Note.NoteType.NOTE_2P_BOMB_06;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "E7":
                                                                list = Measures[num2].Note2PBom07;
                                                                type = Chart.Note.NoteType.NOTE_2P_BOMB_07;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "E8":
                                                                list = Measures[num2].Note2PBom08;
                                                                type = Chart.Note.NoteType.NOTE_2P_BOMB_08;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "E9":
                                                                list = Measures[num2].Note2PBom09;
                                                                type = Chart.Note.NoteType.NOTE_2P_BOMB_09;
                                                                Attribute |= Feature.MINE_NOTE;
                                                                break;
                                                            case "NOTE_COMMON":
                                                                foreach (var item5 in parameter.Split(2).Select((idx, pos) => new
                                                                {
                                                                    Idx = idx,
                                                                    Pos = pos
                                                                }))
                                                                {
                                                                    if (!(item5.Idx == "00") && item5.Idx.Length != 1 && item5.Idx.IsBMSBase64())
                                                                    {
                                                                        var item = new Chart.Note(Measures[num2], type)
                                                                        {
                                                                            Index = BMSBase64.ToInt(item5.Idx),
                                                                            Position = new Fraction(item5.Pos, parameter.Length / 2)
                                                                        };
                                                                        list.Add(item);
                                                                    }
                                                                }
                                                                goto end_IL_0c52;
                                                            case "MINE_ONLY":
                                                                break;
                                                            default:
                                                                goto end_IL_0c52;
                                                        }
                                                        foreach (var item6 in parameter.Split(2).Select((idx, pos) => new
                                                        {
                                                            Idx = idx,
                                                            Pos = pos
                                                        }))
                                                        {
                                                            if (!(item6.Idx == "00") && item6.Idx.Length != 1 && item6.Idx.IsBMSBase64())
                                                            {
                                                                var item3 = new Chart.Note(Measures[num2], type)
                                                                {
                                                                    Index = 0,
                                                                    Value = BMSBase36.ToInt(item6.Idx),
                                                                    Position = new Fraction(item6.Pos, parameter.Length / 2)
                                                                };
                                                                list.Add(item3);
                                                            }
                                                        }
                                                    }
                                                    break;
                                                }
                                            end_IL_0c52:
                                                break;
                                        }
                                        break;
                                    case 7:
                                        {
                                            string text2 = command.Substring(0, System.Math.Min(5, command.Length)).ToUpperInvariant();
                                            if (text2 == "#STOP")
                                            {
                                                string s = command.ReplaceFromStart("#STOP", string.Empty, isIgnoreCase: true);
                                                if (s.IsBMSBase64() && double.TryParse(parameter, out double result) && result > 0.0)
                                                {
                                                    StopArray[BMSBase64.ToInt(s)] = result;
                                                }
                                            }
                                            break;
                                        }
                                }
                                break;
                        }
                    }
                }
                if (ifDepth > 0)
                {
                    NLogWrapper.GetLogger()?.Trace("BMS Parser: Syntax error: #ENDIF did not found before EOF");
                }
                return true;
            }

            parseLines(cmd => false);
            NLogWrapper.DebuggerLogger?.Trace("BMS Parser: RANDOM pattern: " + string.Join(", ", [.. randomPattern.Select(i => i.ToString())]));
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    private void SetIndexEncoding()
    {
        IndexEncoding = IndexEncoding.Base64;
        string[] array = new string[4096];
        string[] array2 = new string[4096];
        string[] array3 = new string[4096];
        string[] array4 = new string[4096];
        var array5 = new Fraction[4096];
        var array6 = new Fraction[4096];
        var array7 = new Fraction[4096];
        var array8 = new Fraction[4096];
        static void action(ReadOnlyCollection<int> mapTo1, string[] toAry1, ReadOnlyCollection<int> mapTo2, string[] toAry2, string[] fromAry)
        {
            for (int i = 1; i < mapTo1.Count; i++)
            {
                if (fromAry[i] != null)
                {
                    int num5 = mapTo1[i];
                    int num6 = mapTo2[i];
                    if (num5 != 0 && toAry1[num5] == null)
                    {
                        toAry1[num5] = fromAry[i];
                    }
                    if (num6 != 0 && toAry2[num6] == null)
                    {
                        toAry2[num6] = fromAry[i];
                    }
                }
            }
        }
        static void action2(ReadOnlyCollection<int> mapTo1, Fraction[] toAry1, ReadOnlyCollection<int> mapTo2, Fraction[] toAry2, Fraction[] fromAry)
        {
            for (int i = 1; i < mapTo1.Count; i++)
            {
                if (!fromAry[i].IsDefault())
                {
                    int num5 = mapTo1[i];
                    int num6 = mapTo2[i];
                    if (num5 != 0 && toAry1[num5].IsDefault())
                    {
                        toAry1[num5] = fromAry[i];
                    }
                    if (num6 != 0 && toAry2[num6].IsDefault())
                    {
                        toAry2[num6] = fromAry[i];
                    }
                }
            }
        }
        action(BMSBase64.MapToBase36Subset, array2, BMSBase64.MapToBase16Subset, array, WavArray);
        action(BMSBase64.MapToBase36Subset, array4, BMSBase64.MapToBase16Subset, array3, BmpArray);
        action2(BMSBase64.MapToBase36Subset, array6, BMSBase64.MapToBase16Subset, array5, BpmArray);
        action2(BMSBase64.MapToBase36Subset, array8, BMSBase64.MapToBase16Subset, array7, StopArray);
        int num = WavArray.Count(s => s != null);
        int num2 = BmpArray.Count(s => s != null);
        int num3 = BpmArray.Count(s => !s.IsDefault());
        int num4 = StopArray.Count(s => !s.IsDefault());
        if (num == array2.Count(s => s != null) && num2 == array4.Count(s => s != null) && num3 == array6.Count(s => !s.IsDefault()) && num4 == array8.Count(s => !s.IsDefault()))
        {
            IndexEncoding = IndexEncoding.Base36;
            WavArray = array2;
            BmpArray = array4;
            BpmArray = array6;
            StopArray = array8;
            if (num == array.Count(s => s != null) && num2 == array3.Count(s => s != null) && num3 == array5.Count(s => !s.IsDefault()) && num4 == array7.Count(s => !s.IsDefault()))
            {
                IndexEncoding = IndexEncoding.Base16;
                WavArray = array;
                BmpArray = array3;
                BpmArray = array5;
                StopArray = array7;
            }
        }
    }

    private void ResolveNoteConflict()
    {
        if (Measures.LastIndex < 0)
        {
            return;
        }
        if (_lnObj > 0)
        {
            _lnObj = IndexMapper[_lnObj];
        }
        bool[] array = new bool[18];
        bool[] array2 = new bool[18];
        var array3 = new Chart.Note[18];
        var array4 = new Chart.Note[18];
        for (int i = 0; i <= Measures.LastIndex; i++)
        {
            Measures[i].SortAllNotes();
            foreach (Chart.Note item in Measures[i].GetPropertiesAllNotes.SelectMany(d => d()))
            {
                item.Index = IndexMapper[item.Index];
                if (item.Index == _lnObj)
                {
                    var noteType = (Chart.Note.NoteType)((uint)item.Type & 0xFFFFFFF0u);
                    if (noteType == Chart.Note.NoteType.NOTE_1P_VISIBLE_ALL || noteType == Chart.Note.NoteType.NOTE_2P_VISIBLE_ALL)
                    {
                        item.Type = 320 + item.Type;
                    }
                }
            }
            Measures[i].SortAllVisibleNotes();
            foreach (Func<IList<Chart.Note>> getPropertiesAllNote in Measures[i].GetPropertiesAllNotes)
            {
                Chart.Note note = null;
                IList<Chart.Note> list = getPropertiesAllNote();
                Chart.Note[] array5 = [.. list];
                foreach (Chart.Note note2 in array5)
                {
                    if (note != null && note.Position == note2.Position && note.Type == note2.Type)
                    {
                        switch ((Chart.Note.NoteType)((uint)note2.Type & 0xFFFFFFF0u))
                        {
                            case Chart.Note.NoteType.NOTE_1P_VISIBLE_ALL:
                            case Chart.Note.NoteType.NOTE_2P_VISIBLE_ALL:
                            case Chart.Note.NoteType.NOTE_1P_LONG_START_ALL:
                            case Chart.Note.NoteType.NOTE_2P_LONG_START_ALL:
                                NLogWrapper.GetLogger()?.Warn(string.Concat("BMS Parser: Duplicate note is moved to BGM, Mes:", i, " Pos:", note2.Position.ToString(), " Ch:", note2.Type, " Idx:", BMSBase64.FromInt(note2.Index)));
                                list.Remove(note2);
                                note2.Type = Chart.Note.NoteType.BGM;
                                Measures[i].Bgm.Add(note2);
                                break;
                            case Chart.Note.NoteType.NOTE_1P_INVISIBLE_ALL:
                            case Chart.Note.NoteType.NOTE_2P_INVISIBLE_ALL:
                            case Chart.Note.NoteType.NOTE_1P_BOMB_ALL:
                            case Chart.Note.NoteType.NOTE_2P_BOMB_ALL:
                            case Chart.Note.NoteType.NOTE_1P_LONG_END_ALL:
                            case Chart.Note.NoteType.NOTE_2P_LONG_END_ALL:
                                NLogWrapper.GetLogger()?.Warn(string.Concat("BMS Parser: Duplicate note is removed, Mes:", i, " Pos:", note2.Position.ToString(), " Ch:", note2.Type, " Idx:", BMSBase64.FromInt(note2.Index)));
                                list.Remove(note2);
                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                            case Chart.Note.NoteType.BAR_LINE:
                                break;
                        }
                    }
                    else
                    {
                        note = note2;
                    }
                }
            }
            foreach (Func<IList<Chart.Note>> getPropertiesAll1PLngNote in Measures[i].GetPropertiesAll1PLngNotes)
            {
                foreach (Chart.Note item2 in getPropertiesAll1PLngNote())
                {
                    uint num2 = (uint)(item2.Type - 80 - 1);
                    item2.Type = (array[num2] ? (256 + item2.Type) : item2.Type);
                    array[num2] = !array[num2];
                }
            }
            foreach (Func<IList<Chart.Note>> getPropertiesAll2PLngNote in Measures[i].GetPropertiesAll2PLngNotes)
            {
                foreach (Chart.Note item3 in getPropertiesAll2PLngNote())
                {
                    uint num3 = (uint)(item3.Type - 96 - 1);
                    item3.Type = (array2[num3] ? (256 + item3.Type) : item3.Type);
                    array2[num3] = !array2[num3];
                }
            }
            if (_lnObj <= 0)
            {
                continue;
            }
            foreach (var item4 in Measures[i].GetPropertiesAll1PVisNotes.Select((d, j5) => new
            {
                d,
                j = j5
            }))
            {
                IList<Chart.Note> list2 = item4.d();
                int j = item4.j;
                Chart.Note[] array5 = [.. list2];
                foreach (Chart.Note note3 in array5)
                {
                    Chart.Note note4 = array3[j];
                    var noteType2 = (Chart.Note.NoteType)((uint)note3.Type & 0xFFFFFFF0u);
                    if (noteType2 == Chart.Note.NoteType.NOTE_1P_LONG_END_ALL && note4 != null)
                    {
                        note4.Measure.GetPropertiesAll1PVisNotes[j]().Remove(note4);
                        note4.Type = 64 + note4.Type;
                        note4.Measure.GetPropertiesAll1PLngNotes[j]().Add(note4);
                        list2.Remove(note3);
                        Measures[i].GetPropertiesAll1PLngNotes[j]().Add(note3);
                        Attribute |= Feature.LONG_NOTE;
                        array3[j] = null;
                    }
                    else if (noteType2 == Chart.Note.NoteType.NOTE_1P_VISIBLE_ALL)
                    {
                        array3[j] = note3;
                    }
                    else
                    {
                        NLogWrapper.GetLogger()?.Warn(string.Concat("BMS Parser: Isolated LN end note is removed, Mes:", i, " Pos:", note3.Position.ToString(), " Ch:", note3.Type, " Idx:", BMSBase64.FromInt(note3.Index)));
                        list2.Remove(note3);
                    }
                }
            }
            foreach (var item5 in Measures[i].GetPropertiesAll2PVisNotes.Select((d, j5) => new
            {
                d,
                j = j5
            }))
            {
                IList<Chart.Note> list3 = item5.d();
                int j2 = item5.j;
                Chart.Note[] array5 = [.. list3];
                foreach (Chart.Note note5 in array5)
                {
                    Chart.Note note6 = array4[j2];
                    var noteType3 = (Chart.Note.NoteType)((uint)note5.Type & 0xFFFFFFF0u);
                    if (noteType3 == Chart.Note.NoteType.NOTE_2P_LONG_END_ALL && note6 != null)
                    {
                        note6.Measure.GetPropertiesAll2PVisNotes[j2]().Remove(note6);
                        note6.Type = 64 + note6.Type;
                        note6.Measure.GetPropertiesAll2PLngNotes[j2]().Add(note6);
                        list3.Remove(note5);
                        Measures[i].GetPropertiesAll2PLngNotes[j2]().Add(note5);
                        Attribute |= Feature.LONG_NOTE;
                        array4[j2] = null;
                    }
                    else if (noteType3 == Chart.Note.NoteType.NOTE_2P_VISIBLE_ALL)
                    {
                        array4[j2] = note5;
                    }
                    else
                    {
                        NLogWrapper.GetLogger()?.Warn(string.Concat("BMS Parser: Isolated LN end note is removed, Mes:", i, " Pos:", note5.Position.ToString(), " Ch:", note5.Type, " Idx:", BMSBase64.FromInt(note5.Index)));
                        list3.Remove(note5);
                    }
                }
            }
        }
        int[] array6 = new int[18];
        int[] array7 = new int[18];
        for (int num4 = 0; num4 <= Measures.LastIndex; num4++)
        {
            Measures[num4].SortAllLongNotes();
            foreach (var item6 in Measures[num4].GetPropertiesAll1PLngNotes.Select((d, j5) => new
            {
                d,
                j = j5
            }))
            {
                IList<Chart.Note> list4 = item6.d();
                int j3 = item6.j;
                Chart.Note[] array5 = [.. list4];
                foreach (Chart.Note note7 in array5)
                {
                    switch ((Chart.Note.NoteType)((uint)note7.Type & 0xFFFFFFF0u))
                    {
                        case Chart.Note.NoteType.NOTE_1P_LONG_START_ALL:
                            if (array6[j3] > 0)
                            {
                                NLogWrapper.GetLogger()?.Warn(string.Concat("BMS Parser: LN start note is merged and moved to BGM, Mes:", num4, " Pos:", note7.Position.ToString(), " Ch:", note7.Type, " Idx:", BMSBase64.FromInt(note7.Index)));
                                list4.Remove(note7);
                                note7.Type = Chart.Note.NoteType.BGM;
                                Measures[num4].Bgm.Add(note7);
                            }
                            array6[j3]++;
                            break;
                        case Chart.Note.NoteType.NOTE_1P_LONG_END_ALL:
                            switch (array6[j3])
                            {
                                case 0:
                                    throw new ArgumentOutOfRangeException();
                                default:
                                    NLogWrapper.GetLogger()?.Warn(string.Concat("BMS Parser: LN end note is merged and removed, Mes:", num4, " Pos:", note7.Position.ToString(), " Ch:", note7.Type, " Idx:", BMSBase64.FromInt(note7.Index)));
                                    list4.Remove(note7);
                                    break;
                                case 1:
                                    break;
                            }
                            array6[j3]--;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
            foreach (var item7 in Measures[num4].GetPropertiesAll2PLngNotes.Select((d, j5) => new
            {
                d,
                j = j5
            }))
            {
                IList<Chart.Note> list5 = item7.d();
                int j4 = item7.j;
                Chart.Note[] array5 = [.. list5];
                foreach (Chart.Note note8 in array5)
                {
                    switch ((Chart.Note.NoteType)((uint)note8.Type & 0xFFFFFFF0u))
                    {
                        case Chart.Note.NoteType.NOTE_2P_LONG_START_ALL:
                            if (array7[j4] > 0)
                            {
                                NLogWrapper.GetLogger()?.Warn(string.Concat("BMS Parser: LN start note is merged and moved to BGM, Mes:", num4, " Pos:", note8.Position.ToString(), " Ch:", note8.Type, " Idx:", BMSBase64.FromInt(note8.Index)));
                                list5.Remove(note8);
                                note8.Type = Chart.Note.NoteType.BGM;
                                Measures[num4].Bgm.Add(note8);
                            }
                            array7[j4]++;
                            break;
                        case Chart.Note.NoteType.NOTE_2P_LONG_END_ALL:
                            switch (array7[j4])
                            {
                                case 0:
                                    throw new ArgumentOutOfRangeException();
                                default:
                                    NLogWrapper.GetLogger()?.Warn(string.Concat("BMS Parser: LN end note is merged and removed, Mes:", num4, " Pos:", note8.Position.ToString(), " Ch:", note8.Type, " Idx:", BMSBase64.FromInt(note8.Index)));
                                    list5.Remove(note8);
                                    break;
                                case 1:
                                    break;
                            }
                            array7[j4]--;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
            Measures[num4].SortBgmNotes();
        }
    }

    private void CalculateTiming()
    {
        if (Measures.LastIndex < 0)
        {
            NLogWrapper.GetLogger()?.Warn("BMS Parser: NO NOTE EXISTS");
            return;
        }
        NoteCount1PLN = new List<int>
        {
            Measures.Sum(m => m.Note1PLng01.Count),
            Measures.Sum(m => m.Note1PLng02.Count),
            Measures.Sum(m => m.Note1PLng03.Count),
            Measures.Sum(m => m.Note1PLng04.Count),
            Measures.Sum(m => m.Note1PLng05.Count),
            Measures.Sum(m => m.Note1PLng06.Count),
            Measures.Sum(m => m.Note1PLng07.Count),
            Measures.Sum(m => m.Note1PLng08.Count),
            Measures.Sum(m => m.Note1PLng09.Count)
        }.AsReadOnly();
        NoteCount2PLN = new List<int>
        {
            Measures.Sum(m => m.Note2PLng01.Count),
            Measures.Sum(m => m.Note2PLng02.Count),
            Measures.Sum(m => m.Note2PLng03.Count),
            Measures.Sum(m => m.Note2PLng04.Count),
            Measures.Sum(m => m.Note2PLng05.Count),
            Measures.Sum(m => m.Note2PLng06.Count),
            Measures.Sum(m => m.Note2PLng07.Count),
            Measures.Sum(m => m.Note2PLng08.Count),
            Measures.Sum(m => m.Note2PLng09.Count)
        }.AsReadOnly();
        NoteCount1P = new List<int>
        {
            Measures.Sum(m => m.Note1PVis01.Count) + NoteCount1PLN[0],
            Measures.Sum(m => m.Note1PVis02.Count) + NoteCount1PLN[1],
            Measures.Sum(m => m.Note1PVis03.Count) + NoteCount1PLN[2],
            Measures.Sum(m => m.Note1PVis04.Count) + NoteCount1PLN[3],
            Measures.Sum(m => m.Note1PVis05.Count) + NoteCount1PLN[4],
            Measures.Sum(m => m.Note1PVis06.Count) + NoteCount1PLN[5],
            Measures.Sum(m => m.Note1PVis07.Count) + NoteCount1PLN[6],
            Measures.Sum(m => m.Note1PVis08.Count) + NoteCount1PLN[7],
            Measures.Sum(m => m.Note1PVis09.Count) + NoteCount1PLN[8]
        }.AsReadOnly();
        NoteCount2P = new List<int>
        {
            Measures.Sum(m => m.Note2PVis01.Count) + NoteCount2PLN[0],
            Measures.Sum(m => m.Note2PVis02.Count) + NoteCount2PLN[1],
            Measures.Sum(m => m.Note2PVis03.Count) + NoteCount2PLN[2],
            Measures.Sum(m => m.Note2PVis04.Count) + NoteCount2PLN[3],
            Measures.Sum(m => m.Note2PVis05.Count) + NoteCount2PLN[4],
            Measures.Sum(m => m.Note2PVis06.Count) + NoteCount2PLN[5],
            Measures.Sum(m => m.Note2PVis07.Count) + NoteCount2PLN[6],
            Measures.Sum(m => m.Note2PVis08.Count) + NoteCount2PLN[7],
            Measures.Sum(m => m.Note2PVis09.Count) + NoteCount2PLN[8]
        }.AsReadOnly();
        NoteCount1PMN = new List<int>
        {
            Measures.Sum(m => m.Note1PBom01.Count),
            Measures.Sum(m => m.Note1PBom02.Count),
            Measures.Sum(m => m.Note1PBom03.Count),
            Measures.Sum(m => m.Note1PBom04.Count),
            Measures.Sum(m => m.Note1PBom05.Count),
            Measures.Sum(m => m.Note1PBom06.Count),
            Measures.Sum(m => m.Note1PBom07.Count),
            Measures.Sum(m => m.Note1PBom08.Count),
            Measures.Sum(m => m.Note1PBom09.Count)
        }.AsReadOnly();
        NoteCount2PMN = new List<int>
        {
            Measures.Sum(m => m.Note2PBom01.Count),
            Measures.Sum(m => m.Note2PBom02.Count),
            Measures.Sum(m => m.Note2PBom03.Count),
            Measures.Sum(m => m.Note2PBom04.Count),
            Measures.Sum(m => m.Note2PBom05.Count),
            Measures.Sum(m => m.Note2PBom06.Count),
            Measures.Sum(m => m.Note2PBom07.Count),
            Measures.Sum(m => m.Note2PBom08.Count),
            Measures.Sum(m => m.Note2PBom09.Count)
        }.AsReadOnly();
        Keys = DetectKeyNum();
        if (!Bpm.HasValue)
        {
            NLogWrapper.GetLogger()?.Warn("BMS Parser: #BPM is not defined, set BPM=130");
            Bpm = new Fraction(130L);
        }
        Fraction? fraction = (MaxBpm = Bpm);
        Fraction curBPM = (MinBpm = fraction).Value;
        Fraction func() => 4L / curBPM;
        for (int num = 0; num <= Measures.LastIndex; num++)
        {
            Measures[num].Control = [.. Measures[num].GetPropertiesAllControlNotes.SelectMany(c => c()).OrderByNotes()];
            IList<Chart.Note>[] array = [.. Measures[num].GetPropertiesAllNotes.Select(d => d())];
            int[] array2 = new int[array.Length];
            var fraction3 = new Fraction(0L);
            var fraction4 = new Fraction(0L);
            Measures[num].Time = ((num == 0) ? TimeSpan.Zero : Measures[num - 1].BarLine.First().AbsoluteTime);
            foreach (Chart.Note item in Measures[num].Control)
            {
                bool flag = false;
                for (int num2 = 0; num2 < array.Length; num2++)
                {
                    IList<Chart.Note> list = array[num2];
                    while (array2[num2] < list.Count && list[array2[num2]].Position <= item.Position)
                    {
                        Chart.Note note = list[array2[num2]];
                        _ = array2[num2];
                        _ = 1;
                        note.MeasurePosition = Measures[num].Length * note.Position;
                        note.PositionTime = fraction3 + Measures[num].Length * (note.Position - fraction4) * func();
                        note.AbsoluteTime = Measures[num].Time + note.PositionTimeSpan;
                        if (note.Type == Chart.Note.NoteType.BAR_LINE)
                        {
                            NLogWrapper.DebuggerLogger?.Trace("M:" + num.ToString("000") + " " + note.AbsoluteTime);
                        }
                        if (note == item)
                        {
                            flag = true;
                        }
                        array2[num2]++;
                    }
                }
                Trace.Assert(item.Type != Chart.Note.NoteType.BAR_LINE || flag);
                fraction3 = item.PositionTime;
                fraction4 = item.Position;
                switch (item.Type)
                {
                    case Chart.Note.NoteType.BPM:
                        curBPM = (int)item.Value;
                        if (MinBpm.Value.ToDouble() > curBPM.ToDouble())
                        {
                            MinBpm = curBPM;
                        }
                        if (MaxBpm.Value.ToDouble() < curBPM.ToDouble())
                        {
                            MaxBpm = curBPM;
                        }
                        break;
                    case Chart.Note.NoteType.EX_BPM:
                        if (!BpmArray[item.Index].IsDefault())
                        {
                            curBPM = BpmArray[item.Index];
                            if (MinBpm.Value.ToDouble() > curBPM.ToDouble())
                            {
                                MinBpm = curBPM;
                            }
                            if (MaxBpm.Value.ToDouble() < curBPM.ToDouble())
                            {
                                MaxBpm = curBPM;
                            }
                            item.Value = curBPM;
                        }
                        else
                        {
                            NLogWrapper.GetLogger()?.Warn("BMS Parser: #BPM" + BMSBase64.FromInt(item.Index) + " not found.");
                        }
                        break;
                    case Chart.Note.NoteType.STOP:
                        if (!StopArray[item.Index].IsDefault())
                        {
                            Fraction fraction5 = StopArray[item.Index] * func() / 192L;
                            fraction3 += fraction5;
                            try
                            {
                                item.Value = new TimeSpan((600000000L * fraction5).ToInt64());
                            }
                            catch
                            {
                                NLogWrapper.DebuggerLogger?.Error("BMS Parser: Arithmetic exception occuered on a fraction multiplying.");
                                item.Value = new TimeSpan((long)(600000000m * fraction5.ToDecimal()));
                            }
                        }
                        else
                        {
                            NLogWrapper.GetLogger()?.Warn("BMS Parser: #STOP" + BMSBase64.FromInt(item.Index) + " not found.");
                        }
                        break;
                }
            }
            for (int num3 = 0; num3 < array2.Length; num3++)
            {
            }
        }
        Fraction? minBpm = MinBpm;
        fraction = Bpm;
        if (minBpm.HasValue != fraction.HasValue || (minBpm.HasValue && minBpm.GetValueOrDefault() != fraction.GetValueOrDefault()) || Bpm != MaxBpm)
        {
            Attribute |= Feature.SOFT_LANDING;
        }
        if (randomPattern.Count > 0)
        {
            Attribute |= Feature.RANDOM;
        }
        if (BmpArray.Any(e => !string.IsNullOrEmpty(e)))
        {
            Attribute |= Feature.BGA;
        }
        if (!string.IsNullOrWhiteSpace(Stagefile))
        {
            Attribute |= Feature.STAGEFILE;
        }
        if (!string.IsNullOrWhiteSpace(Banner))
        {
            Attribute |= Feature.BANNER;
        }
        if (!string.IsNullOrWhiteSpace(Backbmp))
        {
            Attribute |= Feature.BACKBMP;
        }
        if (Total == 0.0)
        {
            Total = getIIDXtotalValue();
        }
        Duration = Measures[Measures.LastIndex].Control.Last(n => n.Type == Chart.Note.NoteType.BAR_LINE).AbsoluteTime;
    }

    private KeyType DetectKeyNum()
    {
        int[] source = [7, 8];
        int[] source2 = [0, 1, 2, 3, 4, 5, 6, 7, 8];
        int[] source3 = [0, 1, 2, 3, 4];
        int[] source4 = [5, 6, 7, 8];
        int[] source5 = [0, 5, 6, 7, 8];
        int[] source6 = [7, 8];
        int[] source7 = [7, 8];
        int[] source8 = [0, 1, 2, 3, 4, 5, 6, 7, 8];
        if (source.Sum(i => NoteCount1P[i] + NoteCount1PMN[i]) + source2.Sum(i => NoteCount2P[i] + NoteCount2PMN[i]) == 0)
        {
            if (source3.All(i => NoteCount1P[i] + NoteCount1PMN[i] == 0))
            {
                return KeyType.KEYS7;
            }
            return KeyType.KEYS5;
        }
        if (source4.All(i => NoteCount1P[i] + NoteCount1PMN[i] == 0) && source5.All(i => NoteCount2P[i] + NoteCount2PMN[i] == 0))
        {
            return KeyType.KEYS9;
        }
        if (source6.All(i => NoteCount1P[i] + NoteCount1PMN[i] == 0) && source7.All(i => NoteCount2P[i] + NoteCount2PMN[i] == 0))
        {
            return KeyType.KEYS10;
        }
        if (source8.All(i => NoteCount2P[i] + NoteCount2PMN[i] == 0))
        {
            return KeyType.KEYS7;
        }
        return KeyType.KEYS14;
    }

    private string getAutoDetectedString(byte[] data, out Encoding enc)
    {
        enc = null;
        if (data.Length == 0)
        {
            return string.Empty;
        }
        string text;
        try
        {
            text = sjisEnc.GetString(data);
            if (IsAsciiStringFaster(text))
            {
                enc = Encoding.ASCII;
                return text;
            }
            if (japanese2charasPattern.IsMatch(text))
            {
                enc = sjisEnc;
                return text;
            }
        }
        catch
        {
            try
            {
                string result = koreanEnc.GetString(data);
                enc = koreanEnc;
                return result;
            }
            catch
            {
                try
                {
                    string result2 = utf8Enc.GetString(data);
                    enc = utf8Enc;
                    return result2;
                }
                catch
                {
                    enc = null;
                    return string.Empty;
                }
            }
        }
        try
        {
            string text2 = koreanEnc.GetString(data);
            string input = spaceAndReturnPattern.Replace(text2, string.Empty);
            if (hangul5charasPattern.IsMatch(input))
            {
                enc = koreanEnc;
                return text2;
            }
        }
        catch
        {
        }
        enc = sjisEnc;
        return text;
    }

    private static bool IsAsciiStringFaster(string s)
    {
        if (s == null)
        {
            throw new ArgumentNullException("s");
        }
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] > '\u007f')
            {
                return false;
            }
        }
        return true;
    }
}
