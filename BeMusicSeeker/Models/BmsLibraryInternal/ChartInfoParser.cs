using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// chart_info テーブルへ保存する譜面メタデータを生成します。
/// 既存の LR2 song 行生成とは責務が異なるため、BMSFile/BmsonSongParser とは独立した解析器にしています。
/// </summary>
internal static class ChartInfoParser
{
    private static readonly ConcurrentDictionary<long, string> JavaDoubleFormatCache = new ConcurrentDictionary<long, string>();

    private const int FeatureUndefinedLongNote = 1;

    private const int FeatureMineNote = 2;

    private const int FeatureRandom = 4;

    private const int FeatureLongNote = 8;

    private const int FeatureChargeNote = 16;

    private const int FeatureHellChargeNote = 32;

    private const int FeatureStopSequence = 64;

    private const int FeatureScroll = 128;

    private const int LongNoteTypeUndefined = 0;

    private const int LongNoteTypeLongNote = 1;

    private const int LongNoteTypeChargeNote = 2;

    private const int LongNoteTypeHellChargeNote = 3;

    private const int LntypeLongNote = 0;

    private static readonly Regex BmsChannelLineRegex = new Regex("^#([0-9]{3})([0-9A-Za-z]{2})\\s*:\\s*(.*)$", RegexOptions.Compiled);

    /// <summary>
    /// 指定された譜面ファイルを解析し、chart_info 行を返します。
    /// </summary>
    /// <param name="filePath">解析対象の譜面ファイル。</param>
    /// <param name="md5">既に分かっている MD5。null の場合はファイルから計算します。</param>
    /// <param name="sha256">既に分かっている SHA-256。null の場合はファイルから計算します。</param>
    /// <param name="encodingName">BMS テキストの文字コード。bmson では使用しません。</param>
    /// <returns>保存可能な chart_info 行。</returns>
    public static LR2SongDBExtended.chart_info Parse(string filePath, string md5 = null, string sha256 = null, string encodingName = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentNullException(nameof(filePath));
        }
        string fullPath = Path.GetFullPath(filePath);
        return ParseBytes(File.ReadAllBytes(fullPath), fullPath, md5, sha256, encodingName);
    }

    /// <summary>
    /// 既に読み込まれた譜面バイト列を解析し、chart_info 行を返します。
    /// ファイル IO と解析を分離し、digest 生成とメタデータ解析で同じ読み取り結果を共有できるようにします。
    /// </summary>
    /// <param name="bytes">譜面ファイルのバイト列。</param>
    /// <param name="fileNameOrExtension">拡張子判定に使うファイル名または拡張子。</param>
    /// <param name="md5">既に分かっている MD5。null の場合は bytes から計算します。</param>
    /// <param name="sha256">既に分かっている SHA-256。null の場合は bytes から計算します。</param>
    /// <param name="encodingName">BMS テキストの文字コード。bmson では使用しません。</param>
    /// <returns>保存可能な chart_info 行。</returns>
    public static LR2SongDBExtended.chart_info ParseBytes(byte[] bytes, string fileNameOrExtension, string md5 = null, string sha256 = null, string encodingName = null)
    {
        return ParseBytesDetailed(bytes, fileNameOrExtension, md5, sha256, encodingName).Row;
    }

    internal static ChartInfoParseResult ParseBytesDetailed(byte[] bytes, string fileNameOrExtension, string md5 = null, string sha256 = null, string encodingName = null)
    {
        if (bytes == null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }
        List<ChartInfoParseDiagnostic> diagnostics = new List<ChartInfoParseDiagnostic>();
        string chartName = string.IsNullOrWhiteSpace(fileNameOrExtension) ? string.Empty : fileNameOrExtension;
        string extension = Path.GetExtension(chartName);
        if (string.IsNullOrWhiteSpace(extension) && chartName.StartsWith(".", StringComparison.Ordinal))
        {
            extension = chartName;
        }
        string resolvedMd5 = string.IsNullOrWhiteSpace(md5) ? ComputeHash(bytes, MD5.Create()) : md5;
        string resolvedSha256 = string.IsNullOrWhiteSpace(sha256) ? ComputeHash(bytes, SHA256.Create()) : sha256;
        if (!string.Equals(extension, ".bmson", StringComparison.OrdinalIgnoreCase))
        {
            return ParseBmsBytesDetailed(
                DecodeBms(bytes, encodingName),
                chartName,
                string.Equals(extension, ".pms", StringComparison.OrdinalIgnoreCase),
                diagnostics,
                resolvedMd5,
                resolvedSha256);
        }
        ChartModel model = ParseBmson(DecodeBmson(bytes), chartName, diagnostics);
        model.Md5 = resolvedMd5;
        model.Sha256 = resolvedSha256;
        string chartString = model.ToChartString();
        return new ChartInfoParseResult(BuildRow(model, chartString), diagnostics, chartString);
    }

    private static ChartInfoParseResult ParseBmsBytesDetailed(string text, string chartName, bool isPms, IList<ChartInfoParseDiagnostic> diagnostics, string md5, string sha256)
    {
        text ??= string.Empty;
        List<int> randomMaxes = ScanRandomMaxes(text);
        if (randomMaxes.Count == 0)
        {
            try
            {
                return BuildBmsParseResult(ParseBmsCandidate(text, chartName, isPms, diagnostics, null), diagnostics, md5, sha256);
            }
            catch (BmsRecoverableParseException ex)
            {
                throw new InvalidDataException(ex.Message, ex);
            }
        }

        BmsRecoverableParseException lastRecoverable = null;
        List<ChartInfoParseDiagnostic> lastDiagnostics = null;
        foreach (int[] selectedRandoms in BuildRandomCandidates(randomMaxes, md5, sha256, chartName))
        {
            List<ChartInfoParseDiagnostic> candidateDiagnostics = new List<ChartInfoParseDiagnostic>();
            try
            {
                ChartModel model = ParseBmsCandidate(text, chartName, isPms, candidateDiagnostics, selectedRandoms);
                ChartInfoParseResult result = BuildBmsParseResult(model, candidateDiagnostics, md5, sha256);
                foreach (ChartInfoParseDiagnostic diagnostic in candidateDiagnostics)
                {
                    diagnostics.Add(diagnostic);
                }
                return result;
            }
            catch (BmsRecoverableParseException ex)
            {
                lastRecoverable = ex;
                lastDiagnostics = candidateDiagnostics;
            }
        }

        if (lastDiagnostics != null)
        {
            foreach (ChartInfoParseDiagnostic diagnostic in lastDiagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }
        throw lastRecoverable == null ? new InvalidDataException("BMS parse failed.") : new InvalidDataException(lastRecoverable.Message, lastRecoverable);
    }

    private static ChartInfoParseResult BuildBmsParseResult(ChartModel model, IEnumerable<ChartInfoParseDiagnostic> diagnostics, string md5, string sha256)
    {
        model.Md5 = md5;
        model.Sha256 = sha256;
        _ = model.GetLastTimeMilliseconds();
        string chartString = model.ToChartString();
        IReadOnlyList<ChartInfoParseDiagnostic> readOnlyDiagnostics = diagnostics as IReadOnlyList<ChartInfoParseDiagnostic>
            ?? (diagnostics ?? Enumerable.Empty<ChartInfoParseDiagnostic>()).ToList();
        return new ChartInfoParseResult(BuildRow(model, chartString), readOnlyDiagnostics, chartString);
    }

    private static LR2SongDBExtended.chart_info BuildRow(ChartModel model, string chartString)
    {
        int length = model.GetLastTimeMilliseconds();
        ChartStatistics statistics = ChartStatistics.Calculate(model);
        return new LR2SongDBExtended.chart_info
        {
            sha256 = model.Sha256,
            md5 = model.Md5,
            charthash = ComputeSha256Text(chartString),
            level = model.Level,
            difficulty = model.Difficulty,
            mainbpm = statistics.MainBpm,
            maxbpm = model.GetMaxBpm(),
            minbpm = model.GetMinBpm(),
            length = length,
            mode = model.DisplayMode,
            judge = model.JudgeRank,
            feature = model.GetFeatureFlags(),
            notes = statistics.TotalNotes,
            n = statistics.NormalKeyNotes,
            ln = statistics.LongKeyNotes,
            s = statistics.NormalScratchNotes,
            ls = statistics.LongScratchNotes,
            total = model.Total,
            total_defined = model.TotalDefined,
            density = statistics.Density,
            peakdensity = statistics.PeakDensity,
            enddensity = statistics.EndDensity,
            distribution = statistics.Distribution,
            speedchange = statistics.SpeedChange,
            speedchange_count = statistics.SpeedChangeCount,
            lanenotes = statistics.LaneNotes,
            parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
            updated_at = DateTime.UtcNow
        };
    }

    private static ChartModel ParseBmsCandidate(string text, string chartName, bool isPms, IList<ChartInfoParseDiagnostic> diagnostics, IReadOnlyList<int> selectedRandoms)
    {
        BmsChartBuilder builder = new BmsChartBuilder(chartName, isPms, diagnostics);
        Stack<int> selectedRandomStack = new Stack<int>();
        Stack<bool> skipStack = new Stack<bool>();
        using StringReader reader = new StringReader(text ?? string.Empty);
        string rawLine;
        int randomIndex = 0;
        while ((rawLine = reader.ReadLine()) != null)
        {
            string line = (rawLine ?? string.Empty).TrimStart('\uFEFF');
            if (line.Length < 2 || line[0] != '#')
            {
                continue;
            }
            if (MatchesReserveWord(line, "RANDOM"))
            {
                builder.HasRandom = true;
                if (int.TryParse(GetCommandArgument(line), NumberStyles.Integer, CultureInfo.InvariantCulture, out int randomMax))
                {
                    int normalizedMax = Math.Max(1, randomMax);
                    int selected = selectedRandoms != null && randomIndex < selectedRandoms.Count ? selectedRandoms[randomIndex] : 1;
                    selectedRandomStack.Push(Math.Max(1, Math.Min(normalizedMax, selected)));
                    randomIndex++;
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_RANDOM_INVALID", "#RANDOMに数字が定義されていません");
                }
                continue;
            }
            if (MatchesReserveWord(line, "IF"))
            {
                if (selectedRandomStack.Count > 0)
                {
                    if (int.TryParse(GetCommandArgument(line), NumberStyles.Integer, CultureInfo.InvariantCulture, out int branch))
                    {
                        skipStack.Push(selectedRandomStack.Peek() != branch);
                    }
                    else
                    {
                        AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_IF_INVALID", "#IFに数字が定義されていません");
                    }
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_IF_WITHOUT_RANDOM", "#IFに対応する#RANDOMが定義されていません");
                }
                continue;
            }
            if (MatchesReserveWord(line, "ENDIF"))
            {
                if (skipStack.Count > 0)
                {
                    skipStack.Pop();
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_ENDIF_WITHOUT_IF", "ENDIFに対応するIFが存在しません");
                }
                continue;
            }
            if (MatchesReserveWord(line, "ENDRANDOM"))
            {
                if (selectedRandomStack.Count > 0)
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
            Match channelMatch = BmsChannelLineRegex.Match(line);
            if (channelMatch.Success)
            {
                int section = int.Parse(channelMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int channel = ParseBase36(channelMatch.Groups[2].Value);
                if (channel >= 0)
                {
                    builder.AddChannelLine(section, channel, channelMatch.Groups[3].Value);
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_CHANNEL_INVALID", "チャンネルに不正な値が定義されています");
                }
                continue;
            }
            builder.ApplyCommand(line);
        }
        return builder.Build();
    }

    private static List<int> ScanRandomMaxes(string text)
    {
        List<int> randomMaxes = new List<int>();
        using StringReader reader = new StringReader(text ?? string.Empty);
        string rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            string line = (rawLine ?? string.Empty).TrimStart('\uFEFF');
            if (line.Length >= 2
                && line[0] == '#'
                && MatchesReserveWord(line, "RANDOM")
                && int.TryParse(GetCommandArgument(line), NumberStyles.Integer, CultureInfo.InvariantCulture, out int randomMax))
            {
                randomMaxes.Add(Math.Max(1, randomMax));
            }
        }
        return randomMaxes;
    }

    private static IEnumerable<int[]> BuildRandomCandidates(IReadOnlyList<int> randomMaxes, string md5, string sha256, string chartName)
    {
        List<int[]> candidates = new List<int[]>();
        AddDistinctRandomCandidate(candidates, CreateUniformRandomCandidate(randomMaxes, 1));
        AddDistinctRandomCandidate(candidates, CreateUniformRandomCandidate(randomMaxes, 2));
        AddDistinctRandomCandidate(candidates, CreateUniformRandomCandidate(randomMaxes, 3));
        AddDistinctRandomCandidate(candidates, CreateUniformRandomCandidate(randomMaxes, 4));
        AddDistinctRandomCandidate(candidates, randomMaxes.Select((int max) => Math.Max(1, max)).ToArray());
        AddDistinctRandomCandidate(candidates, CreateSeededRandomCandidate(randomMaxes, md5, sha256, chartName));
        return candidates;
    }

    private static int[] CreateUniformRandomCandidate(IReadOnlyList<int> randomMaxes, int selected)
    {
        int[] result = new int[randomMaxes.Count];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = Math.Max(1, Math.Min(Math.Max(1, randomMaxes[index]), selected));
        }
        return result;
    }

    private static int[] CreateSeededRandomCandidate(IReadOnlyList<int> randomMaxes, string md5, string sha256, string chartName)
    {
        string seed = !string.IsNullOrWhiteSpace(sha256)
            ? sha256
            : (!string.IsNullOrWhiteSpace(md5) ? md5 : (chartName ?? string.Empty));
        byte[] hash;
        using (SHA256 sha = SHA256.Create())
        {
            hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
        }
        int[] result = new int[randomMaxes.Count];
        for (int index = 0; index < result.Length; index++)
        {
            int max = Math.Max(1, randomMaxes[index]);
            result[index] = hash[index % hash.Length] % max + 1;
        }
        return result;
    }

    private static void AddDistinctRandomCandidate(ICollection<int[]> candidates, int[] candidate)
    {
        string signature = string.Join(",", candidate.Select((int value) => value.ToString(CultureInfo.InvariantCulture)));
        foreach (int[] existing in candidates)
        {
            string existingSignature = string.Join(",", existing.Select((int value) => value.ToString(CultureInfo.InvariantCulture)));
            if (string.Equals(existingSignature, signature, StringComparison.Ordinal))
            {
                return;
            }
        }
        candidates.Add(candidate);
    }

    private static ChartModel ParseBmson(string filePath)
    {
        string json = File.ReadAllText(filePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false));
        return ParseBmson(json, filePath, new List<ChartInfoParseDiagnostic>());
    }

    private static ChartModel ParseBmson(string json, string chartName, IList<ChartInfoParseDiagnostic> diagnostics)
    {
        json = (json ?? string.Empty).TrimStart('\uFEFF');
        BmsonDocument document = ParseBmsonDocument(json);
        BmsonInfo info = document?.Info ?? new BmsonInfo();
        ChartMode resolvedMode = ChartMode.FromBmsonHint(info.ModeHint);
        if (resolvedMode == null)
        {
            AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMSON_MODE_UNSUPPORTED", "非対応のmode_hintです");
        }
        ChartMode mode = resolvedMode ?? ChartMode.Beat7;
        ChartModel model = new ChartModel(chartName, mode)
        {
            InitialBpm = info.InitBpm,
            Level = ToNullableInt(info.Level),
            Difficulty = null,
            LnMode = (info.LnType > 0 && info.LnType <= 3) ? info.LnType : LongNoteTypeUndefined,
            Total = 100.0,
            TotalDefined = info.Total > 0
        };
        if (info.JudgeRank < 0)
        {
            AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMSON_JUDGE_NEGATIVE", "judge_rankが0以下です");
        }
        else if (info.JudgeRank < 5)
        {
            AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMSON_JUDGE_LEGACY", "judge_rankの定義が仕様通りでない可能性があります");
        }
        if (info.Total <= 0)
        {
            AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMSON_TOTAL_NON_POSITIVE", "totalが0以下です");
        }
        model.JudgeRank = info.JudgeRank < 5
            ? NormalizeJudgeRank(info.JudgeRank, JudgeRankType.BmsRank, mode)
            : NormalizeJudgeRank(info.JudgeRank, JudgeRankType.BmsonJudgeRank, mode);

        double defaultTotal = CalculateDefaultTotal(mode, 0);
        model.Total = info.Total > 0 ? info.Total / 100.0 * defaultTotal : defaultTotal;

        SortedList<int, ChartTimeline> timelinesByY = new SortedList<int, ChartTimeline>();
        ChartTimeline baseTimeline = new ChartTimeline(0.0, 0.0, mode.KeyCount)
        {
            Bpm = model.InitialBpm,
            BpmChartText = info.InitBpmText
        };
        timelinesByY.Add(0, baseTimeline);

        double resolution = info.Resolution > 0 ? info.Resolution * 4.0 : 960.0;
        BmsonBpmEvent[] bpmEvents = (document?.BpmEvents ?? Array.Empty<BmsonBpmEvent>()).OrderBy((BmsonBpmEvent item) => item.Y).ToArray();
        BmsonStopEvent[] stopEvents = (document?.StopEvents ?? Array.Empty<BmsonStopEvent>()).OrderBy((BmsonStopEvent item) => item.Y).ToArray();
        BmsonScrollEvent[] scrollEvents = (document?.ScrollEvents ?? Array.Empty<BmsonScrollEvent>()).OrderBy((BmsonScrollEvent item) => item.Y).ToArray();
        int bpmPosition = 0;
        int stopPosition = 0;
        int scrollPosition = 0;
        while (bpmPosition < bpmEvents.Length || stopPosition < stopEvents.Length || scrollPosition < scrollEvents.Length)
        {
            int bpmY = bpmPosition < bpmEvents.Length ? bpmEvents[bpmPosition].Y : int.MaxValue;
            int stopY = stopPosition < stopEvents.Length ? stopEvents[stopPosition].Y : int.MaxValue;
            int scrollY = scrollPosition < scrollEvents.Length ? scrollEvents[scrollPosition].Y : int.MaxValue;
            if (scrollY <= stopY && scrollY <= bpmY)
            {
                GetBmsonTimeline(timelinesByY, scrollY, resolution, mode).Scroll = scrollEvents[scrollPosition].Rate;
                scrollPosition++;
            }
            else if (bpmY <= stopY)
            {
                BmsonBpmEvent bpmEvent = bpmEvents[bpmPosition];
                if (bpmEvent.Bpm > 0)
                {
                    ChartTimeline timeline = GetBmsonTimeline(timelinesByY, bpmEvent.Y, resolution, mode);
                    timeline.Bpm = bpmEvent.Bpm;
                    timeline.BpmChartText = bpmEvent.BpmText;
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMSON_BPM_NEGATIVE", "negative BPMはサポートされていません");
                }
                bpmPosition++;
            }
            else if (stopY != int.MaxValue)
            {
                BmsonStopEvent stopEvent = stopEvents[stopPosition];
                if (stopEvent.Duration >= 0)
                {
                    ChartTimeline timeline = GetBmsonTimeline(timelinesByY, stopEvent.Y, resolution, mode);
                    if (timeline.Bpm <= 0)
                    {
                        throw new InvalidDataException("bmson BPM is zero or negative.");
                    }
                    timeline.StopMicroseconds = (long)(1000.0 * 1000.0 * 60.0 * 4.0 * stopEvent.Duration / (timeline.Bpm * resolution));
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMSON_STOP_NEGATIVE", "negative STOPはサポートされていません");
                }
                stopPosition++;
            }
        }
        foreach (BmsonBarLine line in document?.Lines ?? Array.Empty<BmsonBarLine>())
        {
            GetBmsonTimeline(timelinesByY, line.Y, resolution, mode).HasSectionLine = true;
        }

        int[] keyAssign = mode.GetBmsonKeyAssign();
        List<ChartNote>[] longNotesByLane = CreateLaneLists(mode.KeyCount);
        Dictionary<string, ChartNote> pendingLongNoteEnds = new Dictionary<string, ChartNote>(StringComparer.Ordinal);
        int soundId = 0;
        foreach (BmsonSoundChannel channel in document?.SoundChannels ?? Array.Empty<BmsonSoundChannel>())
        {
            AddBmsonSoundChannel(timelinesByY, resolution, mode, keyAssign, longNotesByLane, pendingLongNoteEnds, channel, soundId, model.LnMode);
            soundId++;
        }
        foreach (BmsonMineChannel channel in document?.KeyChannels ?? Array.Empty<BmsonMineChannel>())
        {
            AddBmsonHiddenChannel(timelinesByY, resolution, mode, keyAssign, channel);
        }
        foreach (BmsonMineChannel channel in document?.MineChannels ?? Array.Empty<BmsonMineChannel>())
        {
            AddBmsonMineChannel(timelinesByY, resolution, mode, keyAssign, longNotesByLane, channel);
        }
        foreach (BmsonBgaNote note in document?.Bga?.BgaEvents ?? Array.Empty<BmsonBgaNote>())
        {
            GetBmsonTimeline(timelinesByY, note.Y, resolution, mode).HasBga = true;
        }

        model.SetTimelines(timelinesByY.Values.ToList());
        int totalNotes = model.GetTotalNotes();
        if (!model.Difficulty.HasValue || model.Difficulty.Value == 0)
        {
            model.Difficulty = InferBeatorajaDifficulty(info.Title, ComposeBmsonSubtitle(info.Subtitle, info.ChartName), totalNotes);
        }
        defaultTotal = CalculateDefaultTotal(mode, totalNotes);
        model.Total = info.Total > 0 ? info.Total / 100.0 * defaultTotal : defaultTotal;
        return model;
    }

    private static string ComposeBmsonSubtitle(string subtitle, string chartName)
    {
        string safeSubtitle = subtitle ?? string.Empty;
        string safeChartName = chartName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(safeChartName))
        {
            return safeSubtitle;
        }
        if (string.IsNullOrWhiteSpace(safeSubtitle))
        {
            return "[" + safeChartName + "]";
        }
        return safeSubtitle + " [" + safeChartName + "]";
    }

    private static int InferBeatorajaDifficulty(string title, string subtitle, int notes)
    {
        string safeTitle = title ?? string.Empty;
        string safeSubtitle = subtitle ?? string.Empty;
        string fullTitle = (safeTitle + safeSubtitle).ToLowerInvariant();
        string diffName = safeSubtitle.ToLowerInvariant();
        int named = InferNamedDifficulty(diffName);
        if (named != 0)
        {
            return named;
        }
        named = InferNamedDifficulty(fullTitle);
        if (named != 0)
        {
            return named;
        }
        if (notes < 250)
        {
            return 1;
        }
        if (notes < 600)
        {
            return 2;
        }
        if (notes < 1000)
        {
            return 3;
        }
        return notes < 2000 ? 4 : 5;
    }

    private static int InferNamedDifficulty(string value)
    {
        if (value.Contains("beginner"))
        {
            return 1;
        }
        if (value.Contains("normal"))
        {
            return 2;
        }
        if (value.Contains("hyper"))
        {
            return 3;
        }
        if (value.Contains("another"))
        {
            return 4;
        }
        return value.Contains("insane") || value.Contains("leggendaria") ? 5 : 0;
    }

    private static void AddBmsonSoundChannel(SortedList<int, ChartTimeline> timelinesByY, double resolution, ChartMode mode, int[] keyAssign, List<ChartNote>[] longNotesByLane, IDictionary<string, ChartNote> pendingLongNoteEnds, BmsonSoundChannel channel, int soundId, int modelLnMode)
    {
        BmsonSoundNote[] notes = (channel?.Notes ?? Array.Empty<BmsonSoundNote>()).OrderBy((BmsonSoundNote item) => item.Y).ToArray();
        long startMicroseconds = 0L;
        for (int noteIndex = 0; noteIndex < notes.Length; noteIndex++)
        {
            BmsonSoundNote note = notes[noteIndex];
            BmsonSoundNote next = notes.Skip(noteIndex + 1).FirstOrDefault((BmsonSoundNote item) => item.Y > note.Y);
            if (!note.Continue)
            {
                startMicroseconds = 0L;
            }
            ChartTimeline timeline = GetBmsonTimeline(timelinesByY, note.Y, resolution, mode);
            long durationMicroseconds = next != null && next.Continue
                ? GetBmsonTimeline(timelinesByY, next.Y, resolution, mode).TimeMicroseconds - timeline.TimeMicroseconds
                : 0L;
            long audioStartMicroseconds = startMicroseconds;
            int lane = note.X > 0 && note.X <= keyAssign.Length ? keyAssign[note.X - 1] : -1;
            if (lane < 0)
            {
                timeline.HasBackground = true;
            }
            else if (note.Up)
            {
                if (longNotesByLane[lane].Count > 0)
                {
                    ChartNote endNote = FindLongNoteEnd(longNotesByLane[lane], note.Y / resolution);
                    if (endNote != null)
                    {
                        endNote.SetAudio(soundId, audioStartMicroseconds, durationMicroseconds);
                    }
                    else
                    {
                        pendingLongNoteEnds[MakeBmsonLongNoteEndKey(note.X, note.Y)] = ChartNote.CreateLong(soundId, LongNoteTypeUndefined, audioStartMicroseconds, durationMicroseconds);
                    }
                }
            }
            else if (!note.Up)
            {
                if (note.Length > 0)
                {
                    ChartTimeline endTimeline = GetBmsonTimeline(timelinesByY, note.Y + note.Length, resolution, mode);
                    if (!HasAnyNoteInRange(timelinesByY, lane, note.Y, note.Y + note.Length) && timeline.Notes[lane] == null)
                    {
                        ChartNote start = ChartNote.CreateLong(soundId, note.Type > 0 && note.Type <= 3 ? note.Type : modelLnMode, audioStartMicroseconds, durationMicroseconds);
                        string pendingEndKey = MakeBmsonLongNoteEndKey(note.X, note.Y + note.Length);
                        ChartNote end = pendingLongNoteEnds.TryGetValue(pendingEndKey, out ChartNote pendingEnd)
                            ? pendingEnd
                            : ChartNote.CreateLong(-2, start.LongType);
                        pendingLongNoteEnds.Remove(pendingEndKey);
                        timeline.SetNote(lane, start);
                        endTimeline.SetNote(lane, end);
                        start.PairWith(end);
                        longNotesByLane[lane].Add(start);
                    }
                    else
                    {
                        timeline.HasBackground = true;
                    }
                }
                else if (timeline.Notes[lane] == null)
                {
                    timeline.SetNote(lane, ChartNote.CreateNormal(soundId, audioStartMicroseconds, durationMicroseconds));
                }
                else
                {
                    timeline.HasBackground = true;
                }
            }
            startMicroseconds += durationMicroseconds;
        }
    }

    private static string MakeBmsonLongNoteEndKey(int x, int y)
    {
        return x.ToString(CultureInfo.InvariantCulture) + ":" + y.ToString(CultureInfo.InvariantCulture);
    }

    private static ChartNote FindLongNoteEnd(IEnumerable<ChartNote> longNotes, double endSection)
    {
        foreach (ChartNote longNote in longNotes ?? Enumerable.Empty<ChartNote>())
        {
            if (longNote.Pair != null && Math.Abs(longNote.Pair.Section - endSection) <= double.Epsilon)
            {
                return longNote.Pair;
            }
        }
        return null;
    }

    private static void AddBmsonHiddenChannel(SortedList<int, ChartTimeline> timelinesByY, double resolution, ChartMode mode, int[] keyAssign, BmsonMineChannel channel)
    {
        foreach (BmsonMineNote note in channel?.Notes ?? Array.Empty<BmsonMineNote>())
        {
            int lane = note.X > 0 && note.X <= keyAssign.Length ? keyAssign[note.X - 1] : -1;
            if (lane >= 0)
            {
                GetBmsonTimeline(timelinesByY, note.Y, resolution, mode).HasHiddenNote = true;
            }
        }
    }

    private static void AddBmsonMineChannel(SortedList<int, ChartTimeline> timelinesByY, double resolution, ChartMode mode, int[] keyAssign, List<ChartNote>[] longNotesByLane, BmsonMineChannel channel)
    {
        foreach (BmsonMineNote note in channel?.Notes ?? Array.Empty<BmsonMineNote>())
        {
            int lane = note.X > 0 && note.X <= keyAssign.Length ? keyAssign[note.X - 1] : -1;
            if (lane < 0)
            {
                continue;
            }
            double section = note.Y / resolution;
            ChartTimeline timeline = GetBmsonTimeline(timelinesByY, note.Y, resolution, mode);
            if (timeline.Notes[lane] == null && !IsInsideLongNote(longNotesByLane[lane], section))
            {
                timeline.SetNote(lane, ChartNote.CreateMine(note.Damage));
            }
        }
    }

    private static ChartTimeline GetBmsonTimeline(SortedList<int, ChartTimeline> timelinesByY, int y, double resolution, ChartMode mode)
    {
        if (timelinesByY.TryGetValue(y, out ChartTimeline existing))
        {
            return existing;
        }
        int previousIndex = FindPreviousTimelineIndex(timelinesByY.Keys, y);
        int previousY = timelinesByY.Keys[previousIndex];
        ChartTimeline previous = timelinesByY.Values[previousIndex];
        if (previous.Bpm <= 0)
        {
            throw new InvalidDataException("bmson BPM is zero or negative.");
        }
        double section = y / resolution;
        double preciseTime = previous.PreciseTimeMicroseconds
            + previous.StopMicroseconds
            + 240000.0 * 1000.0 * ((y - previousY) / resolution) / previous.Bpm;
        ChartTimeline timeline = new ChartTimeline(section, preciseTime, mode.KeyCount)
        {
            Bpm = previous.Bpm,
            Scroll = previous.Scroll
        };
        timelinesByY.Add(y, timeline);
        return timeline;
    }

    private static int FindPreviousTimelineIndex<T>(IList<T> keys, T key)
        where T : IComparable<T>
    {
        int lower = 0;
        int upper = keys.Count - 1;
        int result = 0;
        while (lower <= upper)
        {
            int middle = lower + (upper - lower) / 2;
            if (keys[middle].CompareTo(key) < 0)
            {
                result = middle;
                lower = middle + 1;
            }
            else
            {
                upper = middle - 1;
            }
        }
        return result;
    }

    private static Encoding ResolveBmsEncoding(string encodingName)
    {
        string normalized = string.IsNullOrWhiteSpace(encodingName) ? "shift_jis" : encodingName.Trim().TrimEnd('?');
        if (string.Equals(normalized, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "shift_jis";
        }
        if (string.Equals(normalized, "MS932", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "windows-31j", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "shift_jis";
        }
        try
        {
            return Encoding.GetEncoding(normalized);
        }
        catch
        {
            return Encoding.GetEncoding("shift_jis");
        }
    }

    private static string DecodeBms(byte[] bytes, string encodingName)
    {
        return ResolveBmsEncoding(encodingName).GetString(bytes ?? Array.Empty<byte>());
    }

    private static string DecodeBmson(byte[] bytes)
    {
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes ?? Array.Empty<byte>());
    }

    private static bool MatchesReserveWord(string line, string word)
    {
        string trimmed = line.TrimStart();
        return trimmed.Length > word.Length + 1 && trimmed[0] == '#'
            && string.Compare(trimmed, 1, word, 0, word.Length, ignoreCase: true, CultureInfo.InvariantCulture) == 0;
    }

    private static string GetCommandArgument(string line)
    {
        string trimmed = line.Trim();
        int whitespace = trimmed.IndexOfAny(new[] { ' ', '\t' });
        return whitespace >= 0 ? trimmed.Substring(whitespace + 1).Trim() : string.Empty;
    }

    private static int ParseIntOrDefault(string value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : fallback;
    }

    private static int? ToNullableInt(double? value)
    {
        if (!value.HasValue)
        {
            return null;
        }
        return (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);
    }

    private static int? ParseLeadingInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        Match match = Regex.Match(value.Trim(), "^[+-]?[0-9]+");
        if (!match.Success)
        {
            return null;
        }
        return int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : null;
    }

    private static int ParseBase36(string value)
    {
        int result = 0;
        foreach (char c in value ?? string.Empty)
        {
            int digit = ParseBase36Digit(c);
            if (digit < 0)
            {
                return -1;
            }
            result = result * 36 + digit;
        }
        return result;
    }

    private static int ParseBase36(char high, char low)
    {
        int highDigit = ParseBase36Digit(high);
        int lowDigit = ParseBase36Digit(low);
        return highDigit < 0 || lowDigit < 0 ? -1 : highDigit * 36 + lowDigit;
    }

    private static int ParseBase(string value, int numberBase)
    {
        if (string.IsNullOrEmpty(value))
        {
            return -1;
        }
        int result = 0;
        foreach (char c in value ?? string.Empty)
        {
            int digit = ParseBaseDigit(c, numberBase);
            if (digit < 0)
            {
                return -1;
            }
            result = result * numberBase + digit;
        }
        return result;
    }

    private static int ParseBase(char high, char low, int numberBase)
    {
        int highDigit = ParseBaseDigit(high, numberBase);
        int lowDigit = ParseBaseDigit(low, numberBase);
        return highDigit < 0 || lowDigit < 0 ? -1 : highDigit * numberBase + lowDigit;
    }

    private static int ParseBaseDigit(char c, int numberBase)
    {
        int digit = numberBase == 62 ? ParseBase62Digit(c) : ParseBase36Digit(c);
        return digit >= 0 && digit < numberBase ? digit : -1;
    }

    private static int ParseBase36Digit(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }
        if (c >= 'a' && c <= 'z')
        {
            return c - 'a' + 10;
        }
        if (c >= 'A' && c <= 'Z')
        {
            return c - 'A' + 10;
        }
        return -1;
    }

    private static int ParseBase62Digit(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }
        if (c >= 'A' && c <= 'Z')
        {
            return c - 'A' + 10;
        }
        if (c >= 'a' && c <= 'z')
        {
            return c - 'a' + 36;
        }
        return -1;
    }

    private static void AddDiagnostic(ICollection<ChartInfoParseDiagnostic> diagnostics, ChartInfoParseDiagnosticSeverity severity, string code, string message)
    {
        diagnostics?.Add(new ChartInfoParseDiagnostic(severity, code, message));
    }

    private static string ComputeHash(string filePath, HashAlgorithm algorithm)
    {
        using (algorithm)
        using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            byte[] hash = algorithm.ComputeHash(stream);
            return ToHex(hash);
        }
    }

    private static string ComputeHash(byte[] bytes, HashAlgorithm algorithm)
    {
        using (algorithm)
        {
            byte[] hash = algorithm.ComputeHash(bytes ?? Array.Empty<byte>());
            return ToHex(hash);
        }
    }

    private static string ComputeSha256Text(string value)
    {
        using SHA256 algorithm = SHA256.Create();
        return ToHex(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
    }

    private static string ToHex(byte[] hash)
    {
        StringBuilder builder = new StringBuilder(hash.Length * 2);
        foreach (byte value in hash)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static string FormatDouble(double value)
    {
        return JavaDoubleFormatCache.GetOrAdd(BitConverter.DoubleToInt64Bits(value), _ => FormatDoubleUncached(value));
    }

    private static string FormatDoubleUncached(double value)
    {
        List<string> candidates = new List<string>();
        for (int precision = 1; precision <= 17; precision++)
        {
            string format = "G" + precision.ToString(CultureInfo.InvariantCulture);
            string text = value.ToString(format, CultureInfo.InvariantCulture);
            AddDoubleFormatCandidate(candidates, value, text);
            AddAdjacentDoubleFormatCandidates(candidates, value, text);
        }
        string best = null;
        foreach (string candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (!IsJavaRoundTripDoubleCandidate(value, candidate))
            {
                continue;
            }
            if (best == null || CompareJavaDoubleCandidate(value, candidate, best) < 0)
            {
                best = candidate;
            }
        }
        return best ?? FormatJavaDoubleFallback(value);
    }

    private static void AddDoubleFormatCandidate(ICollection<string> candidates, double value, string text)
    {
        string candidate = NormalizeDoubleText(text);
        if (UsesExponentAgainstJavaDecimalRange(value, candidate))
        {
            return;
        }
        if (UsesDecimalAgainstJavaExponentRange(value, candidate))
        {
            return;
        }
        candidate = EnsureJavaDecimalPoint(candidate);
        if (!string.IsNullOrEmpty(candidate))
        {
            candidates.Add(candidate);
        }
    }

    private static void AddAdjacentDoubleFormatCandidates(ICollection<string> candidates, double value, string text)
    {
        string normalized = NormalizeDoubleText(text);
        if (normalized.IndexOf('E') >= 0 || normalized.IndexOf('e') >= 0 || UsesExponentAgainstJavaDecimalRange(value, normalized))
        {
            return;
        }
        for (int direction = -1; direction <= 1; direction += 2)
        {
            string adjusted = AdjustLastDecimalDigit(normalized, direction);
            if (!string.IsNullOrEmpty(adjusted))
            {
                candidates.Add(EnsureJavaDecimalPoint(adjusted));
            }
        }
    }

    private static bool UsesExponentAgainstJavaDecimalRange(double value, string text)
    {
        double absolute = Math.Abs(value);
        return absolute >= 0.001 && absolute < 10000000.0 && text.IndexOf('E') >= 0;
    }

    private static bool UsesDecimalAgainstJavaExponentRange(double value, string text)
    {
        double absolute = Math.Abs(value);
        return absolute != 0.0
            && (absolute < 0.001 || absolute >= 10000000.0)
            && text.IndexOf('E') < 0
            && text.IndexOf('e') < 0;
    }

    private static string FormatJavaDoubleFallback(double value)
    {
        string text = NormalizeDoubleText(value.ToString("R", CultureInfo.InvariantCulture));
        if (UsesDecimalAgainstJavaExponentRange(value, text))
        {
            return ToJavaExponentText(text);
        }
        return EnsureJavaDecimalPoint(text);
    }

    private static string ToJavaExponentText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('E') >= 0 || text.IndexOf('e') >= 0)
        {
            return NormalizeDoubleText(text);
        }
        bool negative = text[0] == '-';
        if (negative)
        {
            text = text.Substring(1);
        }
        int decimalIndex = text.IndexOf('.');
        string digits = decimalIndex >= 0 ? text.Remove(decimalIndex, 1) : text;
        int exponent = (decimalIndex >= 0 ? decimalIndex : text.Length) - 1;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            return "0.0";
        }
        string mantissa = digits.Length == 1 ? digits + ".0" : digits[0] + "." + digits.Substring(1);
        return (negative ? "-" : string.Empty) + mantissa + "E" + exponent.ToString(CultureInfo.InvariantCulture);
    }

    private static string AdjustLastDecimalDigit(string text, int direction)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        char[] chars = text.ToCharArray();
        for (int index = chars.Length - 1; index >= 0; index--)
        {
            if (!char.IsDigit(chars[index]))
            {
                continue;
            }
            int digit = chars[index] - '0' + direction;
            if (digit < 0 || digit > 9)
            {
                return null;
            }
            chars[index] = (char)('0' + digit);
            return new string(chars);
        }
        return null;
    }

    private static int CompareJavaDoubleCandidate(double value, string left, string right)
    {
        int lengthCompare = left.Length.CompareTo(right.Length);
        if (lengthCompare != 0)
        {
            return lengthCompare;
        }
        decimal leftDistance = GetDecimalDistance(value, left);
        decimal rightDistance = GetDecimalDistance(value, right);
        int distanceCompare = leftDistance.CompareTo(rightDistance);
        if (distanceCompare != 0)
        {
            return distanceCompare;
        }
        if (decimal.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal leftValue)
            && decimal.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal rightValue))
        {
            int valueCompare = leftValue.CompareTo(rightValue);
            if (valueCompare != 0)
            {
                return valueCompare;
            }
        }
        return string.CompareOrdinal(left, right);
    }

    private static decimal GetDecimalDistance(double value, string candidate)
    {
        if (TryGetExactDecimal(value, out decimal actual)
            && decimal.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed))
        {
            return Math.Abs(actual - parsed);
        }
        return decimal.MaxValue;
    }

    private static bool IsJavaRoundTripDoubleCandidate(double value, string candidate)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return string.Equals(candidate, value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
        if (Math.Abs(value) < 1.0 && DoubleRoundTrips(value, candidate))
        {
            return true;
        }
        if (!decimal.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed)
            || !TryGetExactDecimal(value, out decimal actual))
        {
            return DoubleRoundTrips(value, candidate);
        }
        double lowerDouble = NextDouble(value, -1);
        double upperDouble = NextDouble(value, 1);
        if (!TryGetExactDecimal(lowerDouble, out decimal lowerValue)
            || !TryGetExactDecimal(upperDouble, out decimal upperValue))
        {
            return DoubleRoundTrips(value, candidate);
        }
        decimal lowerBoundary = (lowerValue + actual) / 2m;
        decimal upperBoundary = (upperValue + actual) / 2m;
        if (lowerBoundary > upperBoundary)
        {
            decimal swap = lowerBoundary;
            lowerBoundary = upperBoundary;
            upperBoundary = swap;
        }
        if (parsed < lowerBoundary || parsed > upperBoundary)
        {
            return false;
        }
        bool evenSignificand = (BitConverter.DoubleToInt64Bits(value) & 1L) == 0L;
        if ((parsed == lowerBoundary || parsed == upperBoundary) && !evenSignificand)
        {
            return false;
        }
        return true;
    }

    private static double NextDouble(double value, int direction)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return value;
        }
        if (value == 0.0)
        {
            return direction < 0 ? -double.Epsilon : double.Epsilon;
        }
        long bits = BitConverter.DoubleToInt64Bits(value);
        bits += (value > 0.0) == (direction > 0) ? 1 : -1;
        return BitConverter.ToDouble(BitConverter.GetBytes(bits), 0);
    }

    private static bool TryGetExactDecimal(double value, out decimal result)
    {
        result = 0m;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }
        long bits = BitConverter.DoubleToInt64Bits(value);
        bool negative = (bits & unchecked((long)0x8000000000000000)) != 0;
        int exponentBits = (int)((bits >> 52) & 0x7ffL);
        long fraction = bits & 0x000fffffffffffffL;
        if (exponentBits == 0 && fraction == 0)
        {
            result = 0m;
            return true;
        }
        long significand = exponentBits == 0 ? fraction : fraction | 0x0010000000000000L;
        int exponent = exponentBits == 0 ? -1074 : exponentBits - 1075;
        try
        {
            decimal valueDecimal = significand;
            if (exponent > 0)
            {
                for (int i = 0; i < exponent; i++)
                {
                    valueDecimal *= 2m;
                }
            }
            else
            {
                for (int i = 0; i < -exponent; i++)
                {
                    valueDecimal /= 2m;
                }
            }
            result = negative ? -valueDecimal : valueDecimal;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string NormalizeDoubleText(string text)
    {
        text = (text ?? string.Empty).Replace('e', 'E');
        int exponentIndex = text.IndexOf('E');
        if (exponentIndex >= 0)
        {
            string mantissa = text.Substring(0, exponentIndex);
            string exponent = text.Substring(exponentIndex + 1);
            if (mantissa.IndexOf('.') < 0)
            {
                mantissa += ".0";
            }
            if (exponent.StartsWith("+", StringComparison.Ordinal))
            {
                exponent = exponent.Substring(1);
            }
            bool negativeExponent = exponent.StartsWith("-", StringComparison.Ordinal);
            if (negativeExponent)
            {
                exponent = exponent.Substring(1);
            }
            exponent = exponent.TrimStart('0');
            if (exponent.Length == 0)
            {
                exponent = "0";
            }
            text = mantissa + "E" + (negativeExponent ? "-" : string.Empty) + exponent;
        }
        return text;
    }

    private static bool DoubleRoundTrips(double value, string text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && BitConverter.DoubleToInt64Bits(parsed) == BitConverter.DoubleToInt64Bits(value);
    }

    private static string EnsureJavaDecimalPoint(string text)
    {
        return text.IndexOf('.') < 0 && text.IndexOf('E') < 0 && text.IndexOf('e') < 0 ? text + ".0" : text;
    }

    private static double CalculateDefaultTotal(ChartMode mode, int totalNotes)
    {
        if (mode == ChartMode.Keyboard24 || mode == ChartMode.Keyboard24Double)
        {
            return Math.Max(300.0, 7.605 * (totalNotes + 100) / (0.01 * totalNotes + 6.5));
        }
        return Math.Max(260.0, 7.605 * totalNotes / (0.01 * totalNotes + 6.5));
    }

    private static int NormalizeJudgeRank(int judgeRank, JudgeRankType type, ChartMode mode)
    {
        int[] table = mode == ChartMode.Popn9 ? new[] { 33, 50, 70, 100, 133 } : new[] { 25, 50, 75, 100, 125 };
        switch (type)
        {
            case JudgeRankType.BmsRank:
                return judgeRank >= 0 && judgeRank < 5 ? table[judgeRank] : table[2];
            case JudgeRankType.BmsDefExRank:
                return judgeRank > 0 ? judgeRank * table[2] / 100 : table[2];
            default:
                return judgeRank > 0 ? judgeRank : 100;
        }
    }

    private static BmsonDocument ParseBmsonDocument(string json)
    {
        return BmsonJsonParser.Parse(json);
    }

    private static List<ChartNote>[] CreateLaneLists(int laneCount)
    {
        List<ChartNote>[] result = new List<ChartNote>[laneCount];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new List<ChartNote>();
        }
        return result;
    }

    private static bool IsInsideLongNote(IEnumerable<ChartNote> longNotes, double section)
    {
        return (longNotes ?? Enumerable.Empty<ChartNote>()).Any((ChartNote note) => note.Section < section && section <= (note.Pair?.Section ?? note.Section));
    }

    private static bool HasNoteInsideLongNote(IEnumerable<ChartNote> longNotes, double startSection, double endSection)
    {
        return (longNotes ?? Enumerable.Empty<ChartNote>()).Any((ChartNote note) => startSection < (note.Pair?.Section ?? note.Section) && note.Section < endSection);
    }

    private static bool HasAnyNoteInRange(SortedList<int, ChartTimeline> timelinesByY, int lane, int startY, int endY)
    {
        foreach (KeyValuePair<int, ChartTimeline> entry in timelinesByY)
        {
            if (entry.Key <= startY)
            {
                continue;
            }
            if (entry.Key > endY)
            {
                break;
            }
            if (entry.Value.Notes[lane] != null)
            {
                return true;
            }
        }
        return false;
    }

    private static long ToCheckedMicroseconds(double value, string message)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value > long.MaxValue || value < long.MinValue)
        {
            throw new BmsRecoverableParseException(message);
        }
        return (long)value;
    }

    private static int ToCheckedInt(long value, string message)
    {
        if (value > int.MaxValue || value < int.MinValue)
        {
            throw new BmsRecoverableParseException(message);
        }
        return (int)value;
    }

    internal sealed class ChartInfoParseResult
    {
        public ChartInfoParseResult(LR2SongDBExtended.chart_info row, IReadOnlyList<ChartInfoParseDiagnostic> diagnostics, string chartString)
        {
            Row = row;
            Diagnostics = diagnostics ?? Array.Empty<ChartInfoParseDiagnostic>();
            ChartString = chartString ?? string.Empty;
        }

        public LR2SongDBExtended.chart_info Row { get; }

        public IReadOnlyList<ChartInfoParseDiagnostic> Diagnostics { get; }

        public string ChartString { get; }
    }

    internal sealed class ChartInfoParseDiagnostic
    {
        public ChartInfoParseDiagnostic(ChartInfoParseDiagnosticSeverity severity, string code, string message)
        {
            Severity = severity;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public ChartInfoParseDiagnosticSeverity Severity { get; }

        public string Code { get; }

        public string Message { get; }
    }

    internal enum ChartInfoParseDiagnosticSeverity
    {
        Warning,
        Error
    }

    private sealed class BmsRecoverableParseException : Exception
    {
        public BmsRecoverableParseException(string message)
            : base(message)
        {
        }

        public BmsRecoverableParseException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private enum JudgeRankType
    {
        BmsRank,
        BmsDefExRank,
        BmsonJudgeRank
    }

    private sealed class BmsChartBuilder
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

        private readonly string filePath;

        private readonly bool isPms;

        private readonly IList<ChartInfoParseDiagnostic> diagnostics;

        private readonly List<BmsChannelLine> channelLines = new List<BmsChannelLine>();

        private readonly Dictionary<int, double> bpmTable = new Dictionary<int, double>();

        private readonly Dictionary<int, double> stopTable = new Dictionary<int, double>();

        private readonly Dictionary<int, double> scrollTable = new Dictionary<int, double>();

        private int order;

        private int maxSection;

        public BmsChartBuilder(string filePath, bool isPms, IList<ChartInfoParseDiagnostic> diagnostics)
        {
            this.filePath = filePath;
            this.isPms = isPms;
            this.diagnostics = diagnostics;
        }

        public int Base { get; private set; } = 36;

        public bool HasRandom { get; set; }

        public double InitialBpm { get; private set; }

        public int? Level { get; private set; }

        public int? Difficulty { get; private set; }

        public int JudgeRank { get; private set; } = 2;

        public JudgeRankType JudgeRankType { get; private set; } = JudgeRankType.BmsRank;

        public double Total { get; private set; } = 100.0;

        public bool TotalDefined { get; private set; }

        public int LnObject { get; private set; } = -1;

        public int LnMode { get; private set; } = LongNoteTypeUndefined;

        public void AddChannelLine(int section, int channel, string data)
        {
            channelLines.Add(new BmsChannelLine(section, channel, data ?? string.Empty, order++));
            maxSection = Math.Max(maxSection, section);
        }

        public void ApplyCommand(string line)
        {
            string trimmed = line.Trim();
            SplitCommand(trimmed, out string token, out string argument);
            string command = token.StartsWith("#", StringComparison.Ordinal) ? token.Substring(1) : token;
            if (command.Equals("BPM", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm) && bpm > 0)
                {
                    InitialBpm = bpm;
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_BPM_INVALID", "#BPMに数字が定義されていません");
                }
                return;
            }
            if (command.StartsWith("BPM", StringComparison.OrdinalIgnoreCase) && command.Length >= 5)
            {
                int key = ParseBase(command.Substring(3, 2), Base);
                if (key >= 0 && double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm) && bpm > 0)
                {
                    bpmTable[key] = bpm;
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_BPM_INDEXED_INVALID", "#BPMxxに数字が定義されていません");
                }
                return;
            }
            if (command.StartsWith("STOP", StringComparison.OrdinalIgnoreCase) && command.Length >= 6)
            {
                int key = ParseBase(command.Substring(4, 2), Base);
                if (key >= 0 && double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out double stop))
                {
                    stopTable[key] = Math.Abs(stop) / 192.0;
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_STOP_INVALID", "#STOPxxに数字が定義されていません");
                }
                return;
            }
            if (command.StartsWith("SCROLL", StringComparison.OrdinalIgnoreCase) && command.Length >= 8)
            {
                int key = ParseBase(command.Substring(6, 2), Base);
                if (key >= 0 && double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out double scroll))
                {
                    scrollTable[key] = scroll;
                }
                else
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_SCROLL_INVALID", "#SCROLLxxに数字が定義されていません");
                }
                return;
            }
            switch (command.ToUpperInvariant())
            {
                case "PLAYLEVEL":
                    Level = ParseLeadingInt(argument);
                    break;
                case "DIFFICULTY":
                    Difficulty = ParseLeadingInt(argument);
                    break;
                case "RANK":
                    if (int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rank) && rank >= 0 && rank < 5)
                    {
                        JudgeRank = rank;
                        JudgeRankType = JudgeRankType.BmsRank;
                    }
                    break;
                case "DEFEXRANK":
                    if (int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int defExRank) && defExRank >= 1)
                    {
                        JudgeRank = defExRank;
                        JudgeRankType = JudgeRankType.BmsDefExRank;
                    }
                    break;
                case "TOTAL":
                    if (double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out double total) && total > 0)
                    {
                        Total = total;
                        TotalDefined = true;
                    }
                    else
                    {
                        AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_TOTAL_INVALID", "#TOTALに数字が定義されていません");
                    }
                    break;
                case "LNOBJ":
                    LnObject = ParseBase(argument.Trim(), Base);
                    if (LnObject < 0)
                    {
                        AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_LNOBJ_INVALID", "#LNOBJに数字が定義されていません");
                    }
                    break;
                case "LNMODE":
                    if (int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lnMode) && lnMode >= 0 && lnMode <= 3)
                    {
                        LnMode = lnMode;
                    }
                    else
                    {
                        AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_LNMODE_INVALID", "#LNMODEに無効な数字が定義されています");
                    }
                    break;
                case "BASE":
                    if (int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numberBase) && numberBase == 62)
                    {
                        Base = 62;
                    }
                    else
                    {
                        AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_BASE_INVALID", "#BASEに無効な数字が定義されています");
                    }
                    break;
            }
        }

        private static void SplitCommand(string line, out string token, out string argument)
        {
            line ??= string.Empty;
            int separatorIndex = -1;
            for (int index = 0; index < line.Length; index++)
            {
                char c = line[index];
                if (c == ':' || char.IsWhiteSpace(c))
                {
                    separatorIndex = index;
                    break;
                }
            }
            if (separatorIndex < 0)
            {
                token = line;
                argument = string.Empty;
                return;
            }
            token = line.Substring(0, separatorIndex);
            argument = line.Substring(separatorIndex + 1).Trim();
        }

        public ChartModel Build()
        {
            ChartMode mode = DetectMode();
            ChartModel model = new ChartModel(filePath, mode)
            {
                InitialBpm = InitialBpm,
                Level = Level,
                Difficulty = Difficulty,
                JudgeRank = NormalizeJudgeRank(JudgeRank, JudgeRankType, mode),
                Total = Total,
                TotalDefined = TotalDefined,
                LnMode = LnMode,
                HasRandom = HasRandom
            };
            double[] sectionStarts = BuildSectionStarts();
            SortedList<double, ChartTimeline> timelines = new SortedList<double, ChartTimeline>();
            ChartTimeline baseTimeline = new ChartTimeline(0.0, 0.0, mode.KeyCount)
            {
                Bpm = model.InitialBpm
            };
            timelines.Add(0.0, baseTimeline);
            List<ChartNote>[] longNotesByLane = CreateLaneLists(mode.KeyCount);
            ChartNote[] pendingLongStarts = new ChartNote[mode.KeyCount];
            foreach (int section in Enumerable.Range(0, maxSection + 1))
            {
                double sectionStart = sectionStarts[section];
                double rate = section + 1 < sectionStarts.Length ? sectionStarts[section + 1] - sectionStart : 1.0;
                GetBmsTimeline(timelines, sectionStart, mode).HasSectionLine = true;
                ApplyEvents(timelines, mode, section, sectionStart, rate);
                foreach (BmsChannelLine line in channelLines.Where((BmsChannelLine item) => item.Section == section).OrderBy((BmsChannelLine item) => item.Order))
                {
                    ApplyNoteLine(timelines, mode, line, sectionStart, rate, longNotesByLane, pendingLongStarts);
                }
            }
            for (int lane = 0; lane < pendingLongStarts.Length; lane++)
            {
                ChartNote start = pendingLongStarts[lane];
                if (start != null)
                {
                    start.Owner.Notes[lane] = null;
                }
            }
            if (timelines.Count == 0 || timelines.Values[0].Bpm <= 0)
            {
                AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Error, "BMS_INITIAL_BPM_INVALID", "#BPMが定義されていないか無効です");
                throw new BmsRecoverableParseException("BMS initial BPM is not defined or invalid.");
            }
            model.SetTimelines(timelines.Values.ToList());
            if (!TotalDefined)
            {
                model.Total = CalculateDefaultTotal(mode, model.GetTotalNotes());
            }
            return model;
        }

        private ChartMode DetectMode()
        {
            if (isPms)
            {
                return ChartMode.Popn9;
            }
            bool hasSevenSide = false;
            bool hasSecondPlayer = false;
            foreach (BmsChannelLine line in channelLines)
            {
                if (!HasNonZeroData(line.Data))
                {
                    continue;
                }
                int channel = line.Channel;
                if (IsWithin(channel, P1KeyBase, 9) || IsWithin(channel, P1InvisibleKeyBase, 9) || IsWithin(channel, P1LongKeyBase, 9) || IsWithin(channel, P1MineKeyBase, 9))
                {
                    int offset = channel % 36 - 1;
                    hasSevenSide |= offset == 7 || offset == 8;
                }
                if (IsWithin(channel, P2KeyBase, 9) || IsWithin(channel, P2InvisibleKeyBase, 9) || IsWithin(channel, P2LongKeyBase, 9) || IsWithin(channel, P2MineKeyBase, 9))
                {
                    hasSecondPlayer = true;
                    int offset = channel % 36 - 1;
                    hasSevenSide |= offset == 7 || offset == 8;
                }
            }
            if (hasSecondPlayer)
            {
                return hasSevenSide ? ChartMode.Beat14 : ChartMode.Beat10;
            }
            return hasSevenSide ? ChartMode.Beat7 : ChartMode.Beat5;
        }

        private double[] BuildSectionStarts()
        {
            double[] rates = Enumerable.Repeat(1.0, Math.Max(1, maxSection + 1)).ToArray();
            foreach (BmsChannelLine line in channelLines.Where((BmsChannelLine item) => item.Channel == SectionRate))
            {
                if (double.TryParse(line.Data, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate))
                {
                    rates[line.Section] = rate;
                }
            }
            double[] starts = new double[rates.Length + 1];
            for (int i = 1; i < starts.Length; i++)
            {
                starts[i] = starts[i - 1] + rates[i - 1];
            }
            return starts;
        }

        private void ApplyEvents(SortedList<double, ChartTimeline> timelines, ChartMode mode, int section, double sectionStart, double rate)
        {
            List<BmsTimelineEvent> events = new List<BmsTimelineEvent>();
            foreach (BmsChannelLine line in channelLines.Where((BmsChannelLine item) => item.Section == section))
            {
                if (line.Channel == BpmChange)
                {
                    foreach (BmsDataPair pair in SplitData(line.Data))
                    {
                        int bpmValue = Base == 62 ? ParseBase(pair.Token, 36) : pair.Value;
                        if (bpmValue >= 0)
                        {
                            double bpm = (bpmValue / 36) * 16 + bpmValue % 36;
                            events.Add(new BmsTimelineEvent(pair.Position, 1, (ChartTimeline timeline) => timeline.Bpm = bpm));
                        }
                    }
                }
                else if (line.Channel == BpmChangeExtend)
                {
                    foreach (BmsDataPair pair in SplitData(line.Data))
                    {
                        if (bpmTable.TryGetValue(pair.Value, out double bpm))
                        {
                            events.Add(new BmsTimelineEvent(pair.Position, 1, (ChartTimeline timeline) => timeline.Bpm = bpm));
                        }
                    }
                }
                else if (line.Channel == Stop)
                {
                    foreach (BmsDataPair pair in SplitData(line.Data))
                    {
                        if (stopTable.TryGetValue(pair.Value, out double stop))
                        {
                            events.Add(new BmsTimelineEvent(pair.Position, 2, delegate(ChartTimeline timeline)
                            {
                                if (timeline.Bpm <= 0)
                                {
                                    throw new BmsRecoverableParseException("BMS timeline BPM is not defined before STOP.");
                                }
                                timeline.StopMicroseconds = (long)(1000.0 * 1000.0 * 60.0 * 4.0 * stop / timeline.Bpm);
                            }));
                        }
                    }
                }
                else if (line.Channel == Scroll)
                {
                    foreach (BmsDataPair pair in SplitData(line.Data))
                    {
                        if (scrollTable.TryGetValue(pair.Value, out double scroll))
                        {
                            events.Add(new BmsTimelineEvent(pair.Position, 0, (ChartTimeline timeline) => timeline.Scroll = scroll));
                        }
                    }
                }
            }
            foreach (BmsTimelineEvent timelineEvent in events.OrderBy((BmsTimelineEvent item) => item.Position).ThenBy((BmsTimelineEvent item) => item.Priority))
            {
                timelineEvent.Apply(GetBmsTimeline(timelines, sectionStart + rate * timelineEvent.Position, mode));
            }
        }

        private void ApplyNoteLine(SortedList<double, ChartTimeline> timelines, ChartMode mode, BmsChannelLine line, double sectionStart, double rate, List<ChartNote>[] longNotesByLane, ChartNote[] pendingLongStarts)
        {
            int lane = ResolveLane(line.Channel, mode, out BmsLaneChannelKind kind);
            if (kind == BmsLaneChannelKind.None)
            {
                if (line.Channel == LaneAutoplay)
                {
                    foreach (BmsDataPair pair in SplitData(line.Data))
                    {
                        GetBmsTimeline(timelines, sectionStart + rate * pair.Position, mode).HasBackground = true;
                    }
                }
                else if (line.Channel == BgaPlay || line.Channel == LayerPlay)
                {
                    foreach (BmsDataPair pair in SplitData(line.Data))
                    {
                        GetBmsTimeline(timelines, sectionStart + rate * pair.Position, mode).HasBga = true;
                    }
                }
                return;
            }
            if (lane < 0)
            {
                return;
            }
            foreach (BmsDataPair pair in SplitData(line.Data))
            {
                ChartTimeline timeline = GetBmsTimeline(timelines, sectionStart + rate * pair.Position, mode);
                if (kind == BmsLaneChannelKind.Hidden)
                {
                    timeline.HasHiddenNote = true;
                }
                else if (kind == BmsLaneChannelKind.Normal)
                {
                    ApplyBmsNormalNote(timelines, lane, pair.Value, timeline, longNotesByLane);
                }
                else if (kind == BmsLaneChannelKind.Long)
                {
                    ApplyBmsLongNote(lane, pair.Value, timeline, longNotesByLane, pendingLongStarts);
                }
                else if (kind == BmsLaneChannelKind.Mine && timeline.Notes[lane] == null && !IsInsideLongNote(longNotesByLane[lane], timeline.Section))
                {
                    timeline.SetNote(lane, ChartNote.CreateMine(pair.Value));
                }
            }
        }

        private void ApplyBmsNormalNote(SortedList<double, ChartTimeline> timelines, int lane, int data, ChartTimeline timeline, List<ChartNote>[] longNotesByLane)
        {
            if (data == LnObject)
            {
                foreach (ChartTimeline previous in timelines.Values.Reverse().Where((ChartTimeline item) => item.Section < timeline.Section))
                {
                    ChartNote previousNote = previous.Notes[lane];
                    if (previousNote == null)
                    {
                        continue;
                    }
                    if (previousNote.Kind == ChartNoteKind.Normal)
                    {
                        ChartNote start = ChartNote.CreateLong(previousNote.Wav, LnMode);
                        ChartNote end = ChartNote.CreateLong(-2, LnMode);
                        previous.SetNote(lane, start);
                        timeline.SetNote(lane, end);
                        start.PairWith(end);
                        longNotesByLane[lane].Add(start);
                    }
                    else if (previousNote.Kind == ChartNoteKind.Long && previousNote.Pair == null)
                    {
                        ChartNote end = ChartNote.CreateLong(-2, previousNote.LongType);
                        timeline.SetNote(lane, end);
                        previousNote.PairWith(end);
                        longNotesByLane[lane].Add(previousNote);
                    }
                    break;
                }
                return;
            }
            if (timeline.Notes[lane] == null)
            {
                timeline.SetNote(lane, ChartNote.CreateNormal(data));
            }
            else
            {
                timeline.HasBackground = true;
            }
        }

        private void ApplyBmsLongNote(int lane, int data, ChartTimeline timeline, List<ChartNote>[] longNotesByLane, ChartNote[] pendingLongStarts)
        {
            if (IsInsideLongNote(longNotesByLane[lane], timeline.Section))
            {
                timeline.HasBackground = true;
                return;
            }
            ChartNote start = pendingLongStarts[lane];
            if (start == null)
            {
                ChartNote note = ChartNote.CreateLong(data, LnMode);
                timeline.SetNote(lane, note);
                pendingLongStarts[lane] = note;
                return;
            }
            ChartNote end = ChartNote.CreateLong(data == start.Wav ? -2 : data, start.LongType);
            timeline.SetNote(lane, end);
            start.PairWith(end);
            longNotesByLane[lane].Add(start);
            pendingLongStarts[lane] = null;
        }

        private ChartTimeline GetBmsTimeline(SortedList<double, ChartTimeline> timelines, double section, ChartMode mode)
        {
            if (timelines.TryGetValue(section, out ChartTimeline existing))
            {
                return existing;
            }
            int previousIndex = FindPreviousTimelineIndex(timelines.Keys, section);
            ChartTimeline previous = timelines.Values[previousIndex];
            double previousSection = timelines.Keys[previousIndex];
            if (previous.Bpm <= 0)
            {
                throw new BmsRecoverableParseException("BMS timeline BPM is not defined before a future timeline.");
            }
            double preciseTime = previous.PreciseTimeMicroseconds + previous.StopMicroseconds + 240000.0 * 1000.0 * (section - previousSection) / previous.Bpm;
            ChartTimeline timeline = new ChartTimeline(section, preciseTime, mode.KeyCount)
            {
                Bpm = previous.Bpm,
                Scroll = previous.Scroll
            };
            timelines.Add(section, timeline);
            return timeline;
        }

        private int ResolveLane(int channel, ChartMode mode, out BmsLaneChannelKind kind)
        {
            int[] assignment = mode.GetBmsChannelAssign();
            if (TryResolveLane(channel, P1KeyBase, P2KeyBase, assignment, out int lane))
            {
                kind = BmsLaneChannelKind.Normal;
                return lane;
            }
            if (TryResolveLane(channel, P1InvisibleKeyBase, P2InvisibleKeyBase, assignment, out lane))
            {
                kind = BmsLaneChannelKind.Hidden;
                return lane;
            }
            if (TryResolveLane(channel, P1LongKeyBase, P2LongKeyBase, assignment, out lane))
            {
                kind = BmsLaneChannelKind.Long;
                return lane;
            }
            if (TryResolveLane(channel, P1MineKeyBase, P2MineKeyBase, assignment, out lane))
            {
                kind = BmsLaneChannelKind.Mine;
                return lane;
            }
            kind = BmsLaneChannelKind.None;
            return -1;
        }

        private bool TryResolveLane(int channel, int p1Base, int p2Base, int[] assignment, out int lane)
        {
            lane = -1;
            if (IsWithin(channel, p1Base, 9))
            {
                lane = assignment[channel - p1Base];
                return true;
            }
            if (IsWithin(channel, p2Base, 9))
            {
                lane = assignment[channel - p2Base + 9];
                return true;
            }
            return false;
        }

        private static bool IsWithin(int channel, int start, int count)
        {
            return channel >= start && channel < start + count;
        }

        private bool HasNonZeroData(string data)
        {
            return SplitData(data).Any();
        }

        private IEnumerable<BmsDataPair> SplitData(string data)
        {
            if (string.IsNullOrWhiteSpace(data) || data.Length < 2)
            {
                yield break;
            }
            int pairCount = data.Length / 2;
            for (int index = 0; index < pairCount; index++)
            {
                string token = data.Substring(index * 2, 2);
                int value = ParseBase(data[index * 2], data[index * 2 + 1], Base);
                if (value > 0)
                {
                    yield return new BmsDataPair((double)index / pairCount, value, token);
                }
                else if (value < 0)
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_CHANNEL_DATA_INVALID", "チャンネル定義中の不正な値です");
                }
            }
        }
    }

    private enum BmsLaneChannelKind
    {
        None,
        Normal,
        Hidden,
        Long,
        Mine
    }

    private sealed class BmsDataPair
    {
        public BmsDataPair(double position, int value, string token)
        {
            Position = position;
            Value = value;
            Token = token ?? string.Empty;
        }

        public double Position { get; }

        public int Value { get; }

        public string Token { get; }
    }

    private sealed class BmsTimelineEvent
    {
        public BmsTimelineEvent(double position, int priority, Action<ChartTimeline> apply)
        {
            Position = position;
            Priority = priority;
            Apply = apply;
        }

        public double Position { get; }

        public int Priority { get; }

        public Action<ChartTimeline> Apply { get; }
    }

    private sealed class BmsChannelLine
    {
        public BmsChannelLine(int section, int channel, string data, int order)
        {
            Section = section;
            Channel = channel;
            Data = data;
            Order = order;
        }

        public int Section { get; }

        public int Channel { get; }

        public string Data { get; }

        public int Order { get; }
    }

    private sealed class ChartMode
    {
        public static readonly ChartMode Beat5 = new ChartMode(5, 6, new[] { 5 }, new[] { 0, 1, 2, 3, 4, 5, -1, -1, -1, 6, 7, 8, 9, 10, 11, -1, -1, -1 }, null);

        public static readonly ChartMode Beat7 = new ChartMode(7, 8, new[] { 7 }, new[] { 0, 1, 2, 3, 4, 7, -1, 5, 6, 8, 9, 10, 11, 12, 15, -1, 13, 14 }, null);

        public static readonly ChartMode Beat10 = new ChartMode(10, 12, new[] { 5, 11 }, new[] { 0, 1, 2, 3, 4, 5, -1, -1, -1, 6, 7, 8, 9, 10, 11, -1, -1, -1 }, null);

        public static readonly ChartMode Beat14 = new ChartMode(14, 16, new[] { 7, 15 }, new[] { 0, 1, 2, 3, 4, 7, -1, 5, 6, 8, 9, 10, 11, 12, 15, -1, 13, 14 }, null);

        public static readonly ChartMode Popn9 = new ChartMode(9, 9, Array.Empty<int>(), new[] { 0, 1, 2, 3, 4, -1, -1, -1, -1, -1, 5, 6, 7, 8, -1, -1, -1, -1 }, null);

        public static readonly ChartMode Keyboard24 = new ChartMode(25, 26, new[] { 24, 25 }, null, null);

        public static readonly ChartMode Keyboard24Double = new ChartMode(50, 52, new[] { 24, 25, 50, 51 }, null, null);

        private readonly int[] scratchKeys;

        private readonly int[] bmsChannelAssign;

        private readonly int[] bmsonKeyAssign;

        private ChartMode(int displayMode, int keyCount, int[] scratchKeys, int[] bmsChannelAssign, int[] bmsonKeyAssign)
        {
            DisplayMode = displayMode;
            KeyCount = keyCount;
            this.scratchKeys = scratchKeys ?? Array.Empty<int>();
            this.bmsChannelAssign = bmsChannelAssign;
            this.bmsonKeyAssign = bmsonKeyAssign;
        }

        public int DisplayMode { get; }

        public int KeyCount { get; }

        public static ChartMode FromBmsonHint(string modeHint)
        {
            switch ((modeHint ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "beat-5k":
                    return Beat5;
                case "beat-7k":
                    return Beat7;
                case "beat-10k":
                    return Beat10;
                case "beat-14k":
                    return Beat14;
                case "popn-5k":
                case "popn-9k":
                    return Popn9;
                case "keyboard-24k":
                    return Keyboard24;
                case "keyboard-24k-double":
                    return Keyboard24Double;
                default:
                    return null;
            }
        }

        public bool IsScratchKey(int lane)
        {
            return scratchKeys.Contains(lane);
        }

        public int[] GetBmsChannelAssign()
        {
            return bmsChannelAssign ?? Enumerable.Range(0, KeyCount).Concat(Enumerable.Repeat(-1, Math.Max(0, 18 - KeyCount))).Take(18).ToArray();
        }

        public int[] GetBmsonKeyAssign()
        {
            if (bmsonKeyAssign != null)
            {
                return bmsonKeyAssign;
            }
            if (ReferenceEquals(this, Beat5))
            {
                return new[] { 0, 1, 2, 3, 4, -1, -1, 5 };
            }
            if (ReferenceEquals(this, Beat10))
            {
                return new[] { 0, 1, 2, 3, 4, -1, -1, 5, 6, 7, 8, 9, 10, -1, -1, 11 };
            }
            return Enumerable.Range(0, KeyCount).ToArray();
        }
    }

    private sealed class ChartModel
    {
        private List<ChartTimeline> timelines = new List<ChartTimeline>();

        public ChartModel(string path, ChartMode mode)
        {
            Path = path;
            Mode = mode;
        }

        public string Path { get; }

        public ChartMode Mode { get; }

        public string Md5 { get; set; }

        public string Sha256 { get; set; }

        public double InitialBpm { get; set; }

        public int? Level { get; set; }

        public int? Difficulty { get; set; }

        public int JudgeRank { get; set; }

        public double Total { get; set; }

        public bool TotalDefined { get; set; }

        public int LnMode { get; set; }

        public bool HasRandom { get; set; }

        public int DisplayMode => Mode.DisplayMode;

        public IReadOnlyList<ChartTimeline> Timelines => timelines;

        public void SetTimelines(List<ChartTimeline> value)
        {
            timelines = (value ?? new List<ChartTimeline>()).OrderBy((ChartTimeline timeline) => timeline.TimeMicroseconds).ToList();
        }

        public int GetTotalNotes()
        {
            return Timelines.Sum((ChartTimeline timeline) => timeline.GetTotalNotes(LntypeLongNote));
        }

        public double GetMinBpm()
        {
            double bpm = InitialBpm;
            foreach (ChartTimeline timeline in Timelines)
            {
                if (timeline.Bpm < bpm)
                {
                    bpm = timeline.Bpm;
                }
            }
            return bpm;
        }

        public double GetMaxBpm()
        {
            double bpm = InitialBpm;
            foreach (ChartTimeline timeline in Timelines)
            {
                if (timeline.Bpm > bpm)
                {
                    bpm = timeline.Bpm;
                }
            }
            return bpm;
        }

        public int GetLastTimeMilliseconds()
        {
            return ToCheckedInt(GetLastTimeMillisecondsLong(), "BMS timeline length is too large.");
        }

        public long GetLastTimeMillisecondsLong()
        {
            for (int index = Timelines.Count - 1; index >= 0; index--)
            {
                ChartTimeline timeline = Timelines[index];
                if (timeline.HasPlayableOrResourceEvent())
                {
                    return timeline.TimeMillisecondsLong;
                }
            }
            return 0L;
        }

        public int GetFeatureFlags()
        {
            int feature = HasRandom ? FeatureRandom : 0;
            foreach (ChartTimeline timeline in Timelines)
            {
                if (timeline.StopMilliseconds > 0)
                {
                    feature |= FeatureStopSequence;
                }
                if (Math.Abs(timeline.Scroll - 1.0) > double.Epsilon)
                {
                    feature |= FeatureScroll;
                }
                foreach (ChartNote note in timeline.Notes.Where((ChartNote item) => item != null))
                {
                    if (note.Kind == ChartNoteKind.Mine)
                    {
                        feature |= FeatureMineNote;
                    }
                    else if (note.Kind == ChartNoteKind.Long)
                    {
                        switch (note.LongType)
                        {
                            case LongNoteTypeUndefined:
                                feature |= FeatureUndefinedLongNote;
                                break;
                            case LongNoteTypeLongNote:
                                feature |= FeatureLongNote;
                                break;
                            case LongNoteTypeChargeNote:
                                feature |= FeatureChargeNote;
                                break;
                            case LongNoteTypeHellChargeNote:
                                feature |= FeatureHellChargeNote;
                                break;
                        }
                    }
                }
            }
            return feature;
        }

        public string ToChartString()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("JUDGERANK:").Append(JudgeRank).Append('\n');
            builder.Append("TOTAL:").Append(FormatDouble(Total)).Append('\n');
            if (LnMode != 0)
            {
                builder.Append("LNMODE:").Append(LnMode).Append('\n');
            }
            double? currentBpm = null;
            foreach (ChartTimeline timeline in Timelines)
            {
                StringBuilder line = new StringBuilder();
                bool shouldWrite = false;
                line.Append(timeline.TimeMilliseconds).Append(':');
                if (!currentBpm.HasValue || Math.Abs(currentBpm.Value - timeline.Bpm) > double.Epsilon)
                {
                    currentBpm = timeline.Bpm;
                    line.Append("B(").Append(timeline.GetBpmChartText()).Append(')');
                    shouldWrite = true;
                }
                if (timeline.StopMilliseconds != 0)
                {
                    line.Append("S(").Append(timeline.StopMilliseconds).Append(')');
                    shouldWrite = true;
                }
                if (timeline.HasSectionLine)
                {
                    line.Append('L');
                    shouldWrite = true;
                }
                line.Append('[');
                for (int lane = 0; lane < Mode.KeyCount; lane++)
                {
                    ChartNote note = timeline.Notes[lane];
                    if (note == null)
                    {
                        line.Append('0');
                    }
                    else if (note.Kind == ChartNoteKind.Normal)
                    {
                        line.Append('1');
                        shouldWrite = true;
                    }
                    else if (note.Kind == ChartNoteKind.Long)
                    {
                        if (!note.IsEnd)
                        {
                            char longNoteMarker = new[] { 'l', 'L', 'C', 'H' }[Math.Max(0, Math.Min(3, note.LongType))];
                            line.Append((long)longNoteMarker + note.AudioDurationMilliseconds);
                            shouldWrite = true;
                        }
                    }
                    else if (note.Kind == ChartNoteKind.Mine)
                    {
                        line.Append('m').Append(FormatDouble(note.Damage));
                        shouldWrite = true;
                    }
                    else
                    {
                        line.Append('0');
                    }
                    if (lane < Mode.KeyCount - 1)
                    {
                        line.Append(',');
                    }
                }
                line.Append("]\n");
                if (shouldWrite)
                {
                    builder.Append(line);
                }
            }
            return builder.ToString();
        }
    }

    private sealed class ChartTimeline
    {
        public ChartTimeline(double section, double preciseTimeMicroseconds, int laneCount)
        {
            Section = section;
            PreciseTimeMicroseconds = preciseTimeMicroseconds;
            TimeMicroseconds = ToCheckedMicroseconds(preciseTimeMicroseconds, "BMS timeline time is out of range.");
            Notes = new ChartNote[laneCount];
        }

        public double Section { get; }

        public double PreciseTimeMicroseconds { get; }

        public long TimeMicroseconds { get; }

        public long TimeMillisecondsLong => TimeMicroseconds / 1000L;

        public int TimeMilliseconds => ToCheckedInt(TimeMillisecondsLong, "BMS timeline time is out of range.");

        public double Bpm { get; set; }

        public string BpmChartText { get; set; }

        public long StopMicroseconds { get; set; }

        public int StopMilliseconds => (int)(StopMicroseconds / 1000L);

        public double Scroll { get; set; } = 1.0;

        public bool HasSectionLine { get; set; }

        public bool HasHiddenNote { get; set; }

        public bool HasBackground { get; set; }

        public bool HasBga { get; set; }

        public ChartNote[] Notes { get; }

        public void SetNote(int lane, ChartNote note)
        {
            Notes[lane] = note;
            if (note != null)
            {
                note.Owner = this;
                note.Section = Section;
                note.TimeMicroseconds = TimeMicroseconds;
            }
        }

        public int GetTotalNotes(int lntype)
        {
            int count = 0;
            foreach (ChartNote note in Notes)
            {
                if (note == null)
                {
                    continue;
                }
                if (note.Kind == ChartNoteKind.Normal)
                {
                    count++;
                }
                else if (note.Kind == ChartNoteKind.Long && ShouldCountLongNote(note, lntype))
                {
                    count++;
                }
            }
            return count;
        }

        public bool HasPlayableOrResourceEvent()
        {
            return HasHiddenNote || HasBackground || HasBga || Notes.Any((ChartNote note) => note != null);
        }

        public string GetBpmChartText()
        {
            return string.IsNullOrEmpty(BpmChartText) ? FormatDouble(Bpm) : BpmChartText;
        }
    }

    private enum ChartNoteKind
    {
        Normal,
        Long,
        Mine
    }

    private sealed class ChartNote
    {
        private ChartNote(ChartNoteKind kind, int wav, int longType, double damage, long audioStartMicroseconds, long audioDurationMicroseconds)
        {
            Kind = kind;
            Wav = wav;
            LongType = longType;
            Damage = damage;
            AudioStartMicroseconds = audioStartMicroseconds;
            AudioDurationMicroseconds = audioDurationMicroseconds;
        }

        public ChartNoteKind Kind { get; }

        public int Wav { get; private set; }

        public int LongType { get; set; }

        public double Damage { get; }

        public bool IsEnd { get; private set; }

        public ChartNote Pair { get; private set; }

        public ChartTimeline Owner { get; set; }

        public double Section { get; set; }

        public long TimeMicroseconds { get; set; }

        public long AudioStartMicroseconds { get; private set; }

        public long AudioDurationMicroseconds { get; private set; }

        public long AudioDurationMilliseconds => AudioDurationMicroseconds / 1000L;

        public static ChartNote CreateNormal(int wav)
        {
            return CreateNormal(wav, 0L, 0L);
        }

        public static ChartNote CreateNormal(int wav, long audioStartMicroseconds, long audioDurationMicroseconds)
        {
            return new ChartNote(ChartNoteKind.Normal, wav, LongNoteTypeUndefined, 0.0, audioStartMicroseconds, audioDurationMicroseconds);
        }

        public static ChartNote CreateLong(int wav, int longType)
        {
            return CreateLong(wav, longType, 0L, 0L);
        }

        public static ChartNote CreateLong(int wav, int longType, long audioStartMicroseconds, long audioDurationMicroseconds)
        {
            return new ChartNote(ChartNoteKind.Long, wav, longType, 0.0, audioStartMicroseconds, audioDurationMicroseconds);
        }

        public static ChartNote CreateMine(double damage)
        {
            return new ChartNote(ChartNoteKind.Mine, -1, LongNoteTypeUndefined, damage, 0L, 0L);
        }

        public void SetAudio(int wav, long audioStartMicroseconds, long audioDurationMicroseconds)
        {
            Wav = wav;
            AudioStartMicroseconds = audioStartMicroseconds;
            AudioDurationMicroseconds = audioDurationMicroseconds;
        }

        public void PairWith(ChartNote pair)
        {
            Pair = pair;
            pair.Pair = this;
            pair.LongType = LongType != LongNoteTypeUndefined ? LongType : pair.LongType;
            LongType = pair.LongType;
            pair.IsEnd = pair.Section > Section;
            IsEnd = !pair.IsEnd;
        }
    }

    private static bool ShouldCountLongNote(ChartNote note, int lntype)
    {
        return note.LongType == LongNoteTypeChargeNote
            || note.LongType == LongNoteTypeHellChargeNote
            || (note.LongType == LongNoteTypeUndefined && lntype != LntypeLongNote)
            || !note.IsEnd;
    }

    private sealed class ChartStatistics
    {
        public int TotalNotes { get; private set; }

        public int NormalKeyNotes { get; private set; }

        public int LongKeyNotes { get; private set; }

        public int NormalScratchNotes { get; private set; }

        public int LongScratchNotes { get; private set; }

        public double Density { get; private set; }

        public double PeakDensity { get; private set; }

        public double EndDensity { get; private set; }

        public double MainBpm { get; private set; }

        public string Distribution { get; private set; }

        public string SpeedChange { get; private set; }

        public int SpeedChangeCount { get; private set; }

        public string LaneNotes { get; private set; }

        public static ChartStatistics Calculate(ChartModel model)
        {
            ChartStatistics result = new ChartStatistics();
            int laneCount = model.Mode.KeyCount;
            int[][] laneNotes = new int[laneCount][];
            for (int lane = 0; lane < laneCount; lane++)
            {
                laneNotes[lane] = new int[3];
            }
            result.TotalNotes = model.GetTotalNotes();
            result.NormalKeyNotes = CountNotes(model, scratch: false, longNotes: false);
            result.LongKeyNotes = CountNotes(model, scratch: false, longNotes: true);
            result.NormalScratchNotes = CountNotes(model, scratch: true, longNotes: false);
            result.LongScratchNotes = CountNotes(model, scratch: true, longNotes: true);

            DistributionBuckets distribution = BuildDistribution(model, laneNotes, result.TotalNotes, out int borderPosition);
            result.Distribution = EncodeDistribution(distribution);
            result.LaneNotes = EncodeLaneNotes(laneNotes);
            CalculateDensity(distribution, borderPosition, result);
            CalculateSpeed(model, result);
            return result;
        }

        private static int CountNotes(ChartModel model, bool scratch, bool longNotes)
        {
            int count = 0;
            foreach (ChartTimeline timeline in model.Timelines)
            {
                for (int lane = 0; lane < model.Mode.KeyCount; lane++)
                {
                    if (model.Mode.IsScratchKey(lane) != scratch)
                    {
                        continue;
                    }
                    ChartNote note = timeline.Notes[lane];
                    if (note == null)
                    {
                        continue;
                    }
                    if (!longNotes && note.Kind == ChartNoteKind.Normal)
                    {
                        count++;
                    }
                    else if (longNotes && note.Kind == ChartNoteKind.Long && ShouldCountLongNote(note, LntypeLongNote))
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private static DistributionBuckets BuildDistribution(ChartModel model, int[][] laneNotes, int totalNotes, out int borderPosition)
        {
            long lastTime = model.GetLastTimeMillisecondsLong();
            long lastTimeSeconds = lastTime / 1000L;
            if (lastTime < 0)
            {
                throw new BmsRecoverableParseException("BMS timeline length is too large.");
            }
            int bucketCount = ToCheckedInt(lastTimeSeconds + 2L, "BMS distribution bucket count is too large.");
            DistributionBuckets data;
            try
            {
                data = new DistributionBuckets(bucketCount);
            }
            catch (OverflowException ex)
            {
                throw new BmsRecoverableParseException("BMS distribution bucket count is too large.", ex);
            }
            catch (OutOfMemoryException ex)
            {
                throw new BmsRecoverableParseException("BMS distribution bucket allocation failed.", ex);
            }
            int border = (int)(totalNotes * (1.0 - 100.0 / model.Total));
            borderPosition = 0;
            int position = 0;
            foreach (ChartTimeline timeline in model.Timelines)
            {
                int second = timeline.TimeMilliseconds / 1000;
                if (second != position)
                {
                    position = second;
                }
                for (int lane = 0; lane < model.Mode.KeyCount; lane++)
                {
                    ChartNote note = timeline.Notes[lane];
                    if (note == null)
                    {
                        continue;
                    }
                    bool scratch = model.Mode.IsScratchKey(lane);
                    if (note.Kind == ChartNoteKind.Long && !note.IsEnd && note.Pair != null)
                    {
                        int endSecond = note.Pair.Owner.TimeMilliseconds / 1000;
                        for (int fillSecond = second; fillSecond <= endSecond; fillSecond++)
                        {
                            data.Increment(fillSecond, scratch ? 1 : 4);
                        }
                    }
                    bool skipLongEnd = (model.LnMode == LongNoteTypeLongNote || (model.LnMode == LongNoteTypeUndefined && LntypeLongNote == 0))
                        && note.Kind == ChartNoteKind.Long
                        && note.IsEnd;
                    if (skipLongEnd)
                    {
                        continue;
                    }
                    if (note.Kind == ChartNoteKind.Normal)
                    {
                        data.Increment(second, scratch ? 2 : 5);
                        laneNotes[lane][0]++;
                    }
                    else if (note.Kind == ChartNoteKind.Long)
                    {
                        data.Increment(second, scratch ? 0 : 3);
                        data.Add(second, scratch ? 1 : 4, -1);
                        laneNotes[lane][1]++;
                    }
                    else if (note.Kind == ChartNoteKind.Mine)
                    {
                        data.Increment(second, 6);
                        laneNotes[lane][2]++;
                    }
                    border--;
                    if (border == 0)
                    {
                        borderPosition = position;
                    }
                }
            }
            return data;
        }

        private static void CalculateDensity(DistributionBuckets data, int borderPosition, ChartStatistics result)
        {
            int threshold = data.BucketCount > 0 ? result.TotalNotes / data.BucketCount / 4 : 0;
            double density = 0.0;
            double peak = 0.0;
            int count = 0;
            for (int second = 0; second < data.BucketCount; second++)
            {
                int notes = data.GetPlayableNotes(second);
                peak = Math.Max(peak, notes);
                if (notes >= threshold)
                {
                    density += notes;
                    count++;
                }
            }
            result.Density = count > 0 ? density / count : 0.0;
            result.PeakDensity = peak;
            int window = Math.Min(5, data.BucketCount - borderPosition - 1);
            double endDensity = 0.0;
            if (window > 0)
            {
                for (int second = Math.Max(0, borderPosition); second < data.BucketCount - window; second++)
                {
                    int notes = 0;
                    for (int offset = 0; offset < window; offset++)
                    {
                        notes += data.GetPlayableNotes(second + offset);
                    }
                    endDensity = Math.Max(endDensity, (double)notes / window);
                }
            }
            result.EndDensity = endDensity;
        }

        private static void CalculateSpeed(ChartModel model, ChartStatistics result)
        {
            List<double[]> speedList = new List<double[]>();
            Dictionary<double, int> bpmNoteCounts = new Dictionary<double, int>();
            double currentSpeed = model.InitialBpm;
            speedList.Add(new[] { currentSpeed, 0.0 });
            int speedChangeCount = 0;
            foreach (ChartTimeline timeline in model.Timelines)
            {
                bpmNoteCounts.TryGetValue(timeline.Bpm, out int noteCount);
                bpmNoteCounts[timeline.Bpm] = noteCount + timeline.GetTotalNotes(LntypeLongNote);
                if (timeline.StopMilliseconds > 0)
                {
                    if (Math.Abs(currentSpeed) > double.Epsilon)
                    {
                        currentSpeed = 0.0;
                        speedList.Add(new[] { currentSpeed, (double)timeline.TimeMilliseconds });
                        speedChangeCount++;
                    }
                }
                else
                {
                    double timelineSpeed = timeline.Bpm * timeline.Scroll;
                    if (Math.Abs(currentSpeed - timelineSpeed) > double.Epsilon)
                    {
                        currentSpeed = timelineSpeed;
                        speedList.Add(new[] { currentSpeed, (double)timeline.TimeMilliseconds });
                        speedChangeCount++;
                    }
                }
            }
            if (model.Timelines.Count > 0 && Math.Abs(speedList[speedList.Count - 1][1] - model.Timelines[model.Timelines.Count - 1].TimeMilliseconds) > double.Epsilon)
            {
                speedList.Add(new[] { currentSpeed, (double)model.Timelines[model.Timelines.Count - 1].TimeMilliseconds });
            }
            int maxCount = 0;
            result.MainBpm = 0.0;
            foreach (KeyValuePair<double, int> item in bpmNoteCounts)
            {
                if (item.Value > maxCount)
                {
                    maxCount = item.Value;
                    result.MainBpm = item.Key;
                }
            }
            result.SpeedChange = string.Join(",", speedList.SelectMany((double[] values) => values).Select(FormatDouble));
            result.SpeedChangeCount = speedChangeCount;
        }

        private static string EncodeDistribution(DistributionBuckets values)
        {
            StringBuilder builder = new StringBuilder(values.BucketCount * 14 + 1);
            builder.Append('#');
            for (int second = 0; second < values.BucketCount; second++)
            {
                for (int column = 0; column < 7; column++)
                {
                    int value = Math.Min(values[second, column], 36 * 36 - 1);
                    int high = value / 36;
                    int low = value % 36;
                    builder.Append((char)(high >= 10 ? high - 10 + 'a' : high + '0'));
                    builder.Append((char)(low >= 10 ? low - 10 + 'a' : low + '0'));
                }
            }
            return builder.ToString();
        }

        private static string EncodeLaneNotes(int[][] values)
        {
            return string.Join(",", values.SelectMany((int[] lane) => lane).Select((int value) => value.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private sealed class DistributionBuckets
    {
        private const int ColumnCount = 7;

        private readonly int[] values;

        public DistributionBuckets(int bucketCount)
        {
            if (bucketCount < 0)
            {
                throw new OverflowException("Bucket count must be non-negative.");
            }
            BucketCount = bucketCount;
            values = new int[checked(bucketCount * ColumnCount)];
        }

        public int BucketCount { get; }

        public int this[int second, int column] => values[GetIndex(second, column)];

        public void Increment(int second, int column)
        {
            values[GetIndex(second, column)]++;
        }

        public void Add(int second, int column, int value)
        {
            values[GetIndex(second, column)] += value;
        }

        public int GetPlayableNotes(int second)
        {
            int offset = GetIndex(second, 0);
            return values[offset]
                + values[offset + 1]
                + values[offset + 2]
                + values[offset + 3]
                + values[offset + 4]
                + values[offset + 5];
        }

        private static int GetIndex(int second, int column)
        {
            return checked(second * ColumnCount + column);
        }
    }

}
