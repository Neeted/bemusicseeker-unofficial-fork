using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static partial class ChartInfoParser
{
    private enum BmsCommand
    {
        Unknown, Channel, RANDOM, IF, ENDIF, ENDRANDOM,
        BPM, STOP, SCROLL, TITLE, SUBTITLE, PLAYLEVEL, DIFFICULTY,
        RANK, DEFEXRANK, EXLEVEL, TOTAL, LNOBJ, LNMODE, BASE
    }

    private readonly struct BmsTextRange(string text, int start, int length)
    {
        public ReadOnlySpan<char> Span => text.AsSpan(start, length);

        public BmsTextRange Slice(int offset)
        {
            return new BmsTextRange(text, start + offset, length - offset);
        }

        public BmsTextRange Trim()
        {
            int first = 0;
            int last = length;
            while (first < last && char.IsWhiteSpace(text[start + first]))
            {
                first++;
            }
            while (last > first && char.IsWhiteSpace(text[start + last - 1]))
            {
                last--;
            }
            return new BmsTextRange(text, start + first, last - first);
        }
    }

    /// <summary>decode文字列を共有し、行の範囲と予約語だけを候補解析前に確定します。</summary>
    private sealed class BmsInput
    {
        public List<BmsInputLine> Lines { get; } = [];

        public List<int> RandomMaxes { get; } = [];

        public BmsInput(string text, ParseTimeoutGuard timeoutGuard)
        {
            int offset = 0;
            int lineIndex = 0;
            while (offset < text.Length)
            {
                timeoutGuard.ThrowIfTimedOutEvery(++lineIndex, "bms_lex_lines");
                int start = offset;
                while (offset < text.Length && text[offset] != '\r' && text[offset] != '\n')
                {
                    timeoutGuard.ThrowIfTimedOutEvery(offset - start + 1, "bms_lex_line");
                    offset++;
                }
                int end = offset;
                if (offset < text.Length && text[offset++] == '\r' && offset < text.Length && text[offset] == '\n')
                {
                    offset++;
                }
                while (start < end && text[start] == '\uFEFF')
                {
                    start++;
                }
                if (end - start < 2 || text[start] != '#')
                {
                    continue;
                }
                var line = new BmsInputLine(new BmsTextRange(text, start, end - start));
                Lines.Add(line);
                if (line.Command == BmsCommand.RANDOM && TryParseJavaIntStrict(line.GetRawArgument("RANDOM"), out int max))
                {
                    RandomMaxes.Add(Math.Max(1, max));
                }
            }
        }
    }

    private sealed class BmsInputLine
    {
        private static readonly (BmsCommand Command, string Word)[] HeaderCommands =
        [
            (BmsCommand.BPM, "BPM"), (BmsCommand.STOP, "STOP"), (BmsCommand.SCROLL, "SCROLL"),
            (BmsCommand.TITLE, "TITLE"), (BmsCommand.SUBTITLE, "SUBTITLE"), (BmsCommand.PLAYLEVEL, "PLAYLEVEL"),
            (BmsCommand.DIFFICULTY, "DIFFICULTY"), (BmsCommand.RANK, "RANK"), (BmsCommand.DEFEXRANK, "DEFEXRANK"),
            (BmsCommand.EXLEVEL, "EXLEVEL"), (BmsCommand.TOTAL, "TOTAL"), (BmsCommand.LNOBJ, "LNOBJ"),
            (BmsCommand.LNMODE, "LNMODE"), (BmsCommand.BASE, "BASE")
        ];

        public BmsTextRange Raw { get; }

        public BmsTextRange Trimmed { get; }

        public BmsCommand Command { get; }

        public int Section { get; }

        public int Channel { get; } = -1;

        public BmsTextRange Data { get; }

        public BmsInputLine(BmsTextRange raw)
        {
            Raw = raw;
            Trimmed = raw.Trim();
            ReadOnlySpan<char> line = raw.Span;
            if (Matches(line, "RANDOM", true))
            {
                Command = BmsCommand.RANDOM;
            }
            else if (Matches(line, "IF", true))
            {
                Command = BmsCommand.IF;
            }
            else if (Matches(line, "ENDIF", false))
            {
                Command = BmsCommand.ENDIF;
            }
            else if (Matches(line, "ENDRANDOM", false))
            {
                Command = BmsCommand.ENDRANDOM;
            }
            else if (line.Length > 6 && line[1] is >= '0' and <= '9' && line[2] is >= '0' and <= '9' && line[3] is >= '0' and <= '9')
            {
                Command = BmsCommand.Channel;
                Section = (line[1] - '0') * 100 + (line[2] - '0') * 10 + line[3] - '0';
                Channel = ParseBase36(line[4], line[5]);
                int dataStart = 6;
                while (dataStart < line.Length && char.IsWhiteSpace(line[dataStart]))
                {
                    dataStart++;
                }
                Data = dataStart < line.Length && line[dataStart] == ':' ? raw.Slice(dataStart + 1) : raw;
            }
            else
            {
                foreach ((BmsCommand command, string word) in HeaderCommands)
                {
                    if (Matches(Trimmed.Span, word, true))
                    {
                        Command = command;
                        break;
                    }
                }
            }
        }

        private static bool Matches(ReadOnlySpan<char> line, string word, bool argument)
        {
            int minimum = word.Length + 1;
            return (argument ? line.Length > minimum : line.Length >= minimum)
                && line.Slice(1, word.Length).Equals(word.AsSpan(), StringComparison.InvariantCultureIgnoreCase);
        }

        public ReadOnlySpan<char> GetArgument(string word)
        {
            return Argument(Trimmed.Span, word.Length + 2);
        }

        public ReadOnlySpan<char> GetRawArgument(string word)
        {
            return Argument(Raw.Span, word.Length + 2);
        }

        private static ReadOnlySpan<char> Argument(ReadOnlySpan<char> line, int start)
        {
            return line.Length > start ? line.Slice(start).Trim() : ReadOnlySpan<char>.Empty;
        }
    }

    private static ChartModel ParseBmsCandidate(BmsInput input, string chartName, bool isPms, IList<ChartInfoParseDiagnostic> diagnostics, IReadOnlyList<int> selectedRandoms, ParseTimeoutGuard timeoutGuard)
    {
        var builder = new BmsChartBuilder(chartName, isPms, diagnostics, timeoutGuard);
        var selectedRandomStack = new Stack<int>();
        var skipStack = new Stack<bool>();
        int randomIndex = 0;
        int lineIndex = 0;
        foreach (BmsInputLine line in input.Lines)
        {
            timeoutGuard.ThrowIfTimedOutEvery(++lineIndex, "bms_candidate_lines");
            if (line.Command == BmsCommand.RANDOM)
            {
                if (TryParseJavaIntStrict(line.GetRawArgument("RANDOM"), out int max))
                {
                    builder.HasRandom = true;
                    int selected = selectedRandoms != null && randomIndex < selectedRandoms.Count ? selectedRandoms[randomIndex] : 1;
                    selectedRandomStack.Push(Math.Max(1, Math.Min(Math.Max(1, max), selected)));
                    randomIndex++;
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_RANDOM_INVALID", "#RANDOMに数字が定義されていません");
                }
                continue;
            }
            if (line.Command == BmsCommand.IF)
            {
                if (selectedRandomStack.Count == 0)
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_IF_WITHOUT_RANDOM", "#IFに対応する#RANDOMが定義されていません");
                }
                else if (TryParseJavaIntStrict(line.GetRawArgument("IF"), out int branch))
                {
                    skipStack.Push(selectedRandomStack.Peek() != branch);
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_IF_INVALID", "#IFに数字が定義されていません");
                }
                continue;
            }
            if (line.Command == BmsCommand.ENDIF || line.Command == BmsCommand.ENDRANDOM)
            {
                if (line.Command == BmsCommand.ENDIF)
                {
                    if (skipStack.Count > 0)
                    {
                        skipStack.Pop();
                    }
                    else
                    {
                        AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_ENDIF_WITHOUT_IF", "ENDIFに対応するIFが存在しません");
                    }
                }
                else if (selectedRandomStack.Count > 0)
                {
                    selectedRandomStack.Pop();
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_ENDRANDOM_WITHOUT_RANDOM", "ENDRANDOMに対応するRANDOMが存在しません");
                }
                continue;
            }
            if (skipStack.Count > 0 && skipStack.Peek())
            {
                continue;
            }
            if (line.Command == BmsCommand.Channel)
            {
                builder.TouchSection(line.Section);
                if (line.Channel >= 0)
                {
                    builder.AddChannelLine(line);
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_CHANNEL_INVALID", "チャンネルに不正な値が定義されています");
                }
            }
            else
            {
                builder.ApplyCommand(line);
            }
        }
        return builder.Build();
    }
}
