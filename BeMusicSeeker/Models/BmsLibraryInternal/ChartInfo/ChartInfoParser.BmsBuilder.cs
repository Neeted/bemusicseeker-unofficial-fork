using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static partial class ChartInfoParser
{
    private sealed partial class BmsChartBuilder(string filePath, bool isPms, IList<ChartInfoParser.ChartInfoParseDiagnostic> diagnostics, ChartInfoParser.ParseTimeoutGuard timeoutGuard)
    {
        private const int LaneAutoplay = 1;

        private const int SectionRate = 2;

        private const int BpmChange = 3;

        private const int BgaPlay = 4;

        private const int LayerPlay = 7;

        private const int BpmChangeExtend = 8;

        private const int Stop = 9;

        private const int Scroll = 1020;

        private const int P1KeyBase = 37;

        private const int P2KeyBase = 73;

        private const int P1InvisibleKeyBase = 109;

        private const int P2InvisibleKeyBase = 145;

        private const int P1LongKeyBase = 181;

        private const int P2LongKeyBase = 217;

        private const int P1MineKeyBase = 469;

        private const int P2MineKeyBase = 505;

        private readonly string filePath = filePath;

        private readonly bool isPms = isPms;

        private readonly IList<ChartInfoParseDiagnostic> diagnostics = diagnostics;

        private readonly ParseTimeoutGuard timeoutGuard = timeoutGuard ?? ParseTimeoutGuard.None;

        private readonly List<BmsChannelLine> channelLines = [];

        private readonly Dictionary<int, double> bpmTable = [];

        private readonly Dictionary<int, double> stopTable = [];

        private readonly Dictionary<int, double> scrollTable = [];

        private int maxSection;

        public int Base { get; private set; } = 36;

        public bool HasRandom { get; set; }

        public string Title { get; private set; } = string.Empty;

        public string Subtitle { get; private set; } = string.Empty;

        public double InitialBpm { get; private set; }

        public int? Level { get; private set; }

        public int? Difficulty { get; private set; }

        public bool DifficultyDefined { get; private set; }

        public int JudgeRank { get; private set; } = 2;

        public JudgeRankType JudgeRankType { get; private set; } = JudgeRankType.BmsRank;

        public int? ExLevel { get; private set; } = 0;

        public double Total { get; private set; } = 100.0;

        public bool TotalDefined { get; private set; }

        public int LnObject { get; private set; } = -1;

        public int LnMode { get; private set; } = LongNoteTypeUndefined;

        public void AddChannelLine(BmsInputLine line)
        {
            channelLines.Add(new BmsChannelLine(line.Section, line.Channel, line.Data));
            maxSection = Math.Max(maxSection, line.Section);
        }

        public void TouchSection(int section)
        {
            maxSection = Math.Max(maxSection, section);
        }

        public void ApplyCommand(BmsInputLine line)
        {
            ReadOnlySpan<char> trimmed = line.Trimmed.Span;
            if (line.Command == BmsCommand.BPM)
            {
                if (trimmed.Length > 4 && trimmed[4] == ' ')
                {
                    string argument = line.GetArgument("BPM").ToString();
                    if (TryParseJavaDouble(argument, out double bpm) && bpm > 0)
                    {
                        InitialBpm = bpm;
                    }
                    else
                    {
                        AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_BPM_INVALID", "#BPMに数字が定義されていません");
                    }
                }
                else if (trimmed.Length >= 8)
                {
                    int key = ParseBase(trimmed.Slice(4, 2), Base);
                    string argument = trimmed.Slice(7).Trim().ToString();
                    if (key >= 0 && TryParseJavaDouble(argument, out double bpm) && bpm > 0)
                    {
                        bpmTable[key] = bpm;
                    }
                    else
                    {
                        AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_BPM_INDEXED_INVALID", "#BPMxxに数字が定義されていません");
                    }
                }
                return;
            }
            if (line.Command == BmsCommand.STOP)
            {
                if (trimmed.Length >= 9)
                {
                    int key = ParseBase(trimmed.Slice(5, 2), Base);
                    string argument = trimmed.Slice(8).Trim().ToString();
                    if (key >= 0 && TryParseJavaDouble(argument, out double stop))
                    {
                        stopTable[key] = Math.Abs(stop) / 192.0;
                    }
                    else
                    {
                        AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_STOP_INVALID", "#STOPxxに数字が定義されていません");
                    }
                }
                return;
            }
            if (line.Command == BmsCommand.SCROLL)
            {
                if (trimmed.Length >= 11)
                {
                    int key = ParseBase(trimmed.Slice(7, 2), Base);
                    string argument = trimmed.Slice(10).Trim().ToString();
                    if (key >= 0 && TryParseJavaDouble(argument, out double scroll))
                    {
                        scrollTable[key] = scroll;
                    }
                    else
                    {
                        AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_SCROLL_INVALID", "#SCROLLxxに数字が定義されていません");
                    }
                }
                return;
            }
            if (line.Command == BmsCommand.TITLE)
            {
                Title = line.GetArgument("TITLE").ToString();
                return;
            }
            if (line.Command == BmsCommand.SUBTITLE)
            {
                Subtitle = line.GetArgument("SUBTITLE").ToString();
                return;
            }
            if (line.Command == BmsCommand.PLAYLEVEL)
            {
                ReadOnlySpan<char> argument = line.GetArgument("PLAYLEVEL");
                Level = TryParseJavaIntStrict(argument, out int level) ? level : null;
                return;
            }
            if (line.Command == BmsCommand.DIFFICULTY)
            {
                ReadOnlySpan<char> argument = line.GetArgument("DIFFICULTY");
                if (TryParseJavaIntStrict(argument, out int difficulty))
                {
                    Difficulty = difficulty;
                    DifficultyDefined = difficulty != 0;
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_DIFFICULTY_INVALID", "#DIFFICULTYに数字が定義されていません");
                }
                return;
            }
            if (line.Command == BmsCommand.RANK)
            {
                ReadOnlySpan<char> argument = line.GetArgument("RANK");
                if (TryParseJavaIntStrict(argument, out int rank) && rank >= 0 && rank < 5)
                {
                    JudgeRank = rank;
                    JudgeRankType = JudgeRankType.BmsRank;
                }
                return;
            }
            if (line.Command == BmsCommand.DEFEXRANK)
            {
                ReadOnlySpan<char> argument = line.GetArgument("DEFEXRANK");
                if (TryParseJavaIntStrict(argument, out int defExRank) && defExRank >= 1)
                {
                    JudgeRank = defExRank;
                    JudgeRankType = JudgeRankType.BmsDefExRank;
                }
                return;
            }
            if (line.Command == BmsCommand.EXLEVEL)
            {
                ReadOnlySpan<char> argument = line.GetArgument("EXLEVEL");
                if (TryParseJavaIntStrict(argument, out int exLevel))
                {
                    ExLevel = exLevel;
                }
                return;
            }
            if (line.Command == BmsCommand.TOTAL)
            {
                string argument = line.GetArgument("TOTAL").ToString();
                if (TryParseJavaDouble(argument, out double total) && total > 0)
                {
                    Total = total;
                    TotalDefined = true;
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_TOTAL_INVALID", "#TOTALに数字が定義されていません");
                }
                return;
            }
            if (line.Command == BmsCommand.LNOBJ)
            {
                ReadOnlySpan<char> argument = line.GetArgument("LNOBJ");
                LnObject = ParseBase(argument.Trim(), Base);
                if (LnObject < 0)
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_LNOBJ_INVALID", "#LNOBJに数字が定義されていません");
                }
                return;
            }
            if (line.Command == BmsCommand.LNMODE)
            {
                ReadOnlySpan<char> argument = line.GetArgument("LNMODE");
                if (TryParseJavaIntStrict(argument, out int lnMode) && lnMode >= 0 && lnMode <= 3)
                {
                    LnMode = lnMode;
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_LNMODE_INVALID", "#LNMODEに無効な数字が定義されています");
                }
                return;
            }
            if (line.Command == BmsCommand.BASE)
            {
                ReadOnlySpan<char> argument = line.GetArgument("BASE");
                if (TryParseJavaIntStrict(argument, out int numberBase) && numberBase == 62)
                {
                    Base = numberBase;
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_BASE_INVALID", "#BASEに無効な数字が定義されています");
                }
            }
        }


    }
}
