using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

    private static readonly Regex BmsChannelLineRegex = new Regex("^#([0-9]{3})([0-9A-Za-z]{2})\\s*:(.*)$", RegexOptions.Compiled);

    private static readonly Regex BmsChannelHeaderRegex = new Regex("^#([0-9]{3})([0-9A-Za-z]{2}).*$", RegexOptions.Compiled);

    /// <summary>
    /// 指定された譜面ファイルを解析し、chart_info 行を返します。
    /// </summary>
    /// <param name="filePath">解析対象の譜面ファイル。</param>
    /// <param name="md5">既に分かっている MD5。null の場合はファイルから計算します。</param>
    /// <param name="sha256">既に分かっている SHA-256。null の場合はファイルから計算します。</param>
    /// <param name="encodingName">低レベル検証用の BMS decode override。通常の chart_info backfill では null にし、beatoraja 互換の既定 decode を使います。bmson では使用しません。</param>
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
    /// <param name="encodingName">低レベル検証用の BMS decode override。通常の chart_info backfill では null にし、beatoraja 互換の既定 decode を使います。bmson では使用しません。</param>
    /// <returns>保存可能な chart_info 行。</returns>
    public static LR2SongDBExtended.chart_info ParseBytes(byte[] bytes, string fileNameOrExtension, string md5 = null, string sha256 = null, string encodingName = null)
    {
        return ParseBytesDetailed(bytes, fileNameOrExtension, md5, sha256, encodingName).Row;
    }

    /// <summary>
    /// 既に読み込まれた譜面バイト列を解析し、診断情報と検証用 chart string も返します。
    /// backfill では timeout を渡し、協調 checkpoint で長時間解析を parse failure として扱います。
    /// </summary>
    /// <param name="bytes">譜面ファイルのバイト列。</param>
    /// <param name="fileNameOrExtension">拡張子判定に使うファイル名または拡張子。</param>
    /// <param name="md5">既に分かっている MD5。null の場合は bytes から計算します。</param>
    /// <param name="sha256">既に分かっている SHA-256。null の場合は bytes から計算します。</param>
    /// <param name="encodingName">低レベル検証用の BMS decode override。通常の chart_info backfill では null にし、beatoraja 互換の既定 decode を使います。bmson では使用しません。</param>
    /// <param name="timeout">解析 timeout。null の場合は timeout なし。</param>
    /// <returns>保存可能な chart_info 行、診断情報、chart string。</returns>
    /// <exception cref="ChartInfoParseTimeoutException">指定 timeout を超えた場合。</exception>
    internal static ChartInfoParseResult ParseBytesDetailed(byte[] bytes, string fileNameOrExtension, string md5 = null, string sha256 = null, string encodingName = null, TimeSpan? timeout = null)
    {
        if (bytes == null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }
        ParseTimeoutGuard timeoutGuard = ParseTimeoutGuard.Start(timeout);
        timeoutGuard.ThrowIfTimedOut("parse_start");
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
                resolvedSha256,
                timeoutGuard);
        }
        ChartModel model = ParseBmson(DecodeBmson(bytes), chartName, diagnostics, timeoutGuard);
        model.Md5 = resolvedMd5;
        model.Sha256 = resolvedSha256;
        string chartString = model.ToChartString(timeoutGuard);
        return new ChartInfoParseResult(BuildRow(model, chartString, timeoutGuard), diagnostics, chartString);
    }

    private static ChartInfoParseResult ParseBmsBytesDetailed(string text, string chartName, bool isPms, IList<ChartInfoParseDiagnostic> diagnostics, string md5, string sha256, ParseTimeoutGuard timeoutGuard)
    {
        text ??= string.Empty;
        List<int> randomMaxes = ScanRandomMaxes(text, timeoutGuard);
        if (randomMaxes.Count == 0)
        {
            try
            {
                return BuildBmsParseResult(ParseBmsCandidate(text, chartName, isPms, diagnostics, null, timeoutGuard), diagnostics, md5, sha256, timeoutGuard);
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
            timeoutGuard.ThrowIfTimedOut("bms_random_candidate");
            List<ChartInfoParseDiagnostic> candidateDiagnostics = new List<ChartInfoParseDiagnostic>();
            try
            {
                ChartModel model = ParseBmsCandidate(text, chartName, isPms, candidateDiagnostics, selectedRandoms, timeoutGuard);
                ChartInfoParseResult result = BuildBmsParseResult(model, candidateDiagnostics, md5, sha256, timeoutGuard);
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

    private static ChartInfoParseResult BuildBmsParseResult(ChartModel model, IEnumerable<ChartInfoParseDiagnostic> diagnostics, string md5, string sha256, ParseTimeoutGuard timeoutGuard)
    {
        model.Md5 = md5;
        model.Sha256 = sha256;
        timeoutGuard.ThrowIfTimedOut("bms_last_time");
        _ = model.GetLastTimeMilliseconds();
        IList<ChartInfoParseDiagnostic> mutableDiagnostics = diagnostics as IList<ChartInfoParseDiagnostic>;
        if (model.TryGetJavaIntTimeWrap(out long rawTimeMilliseconds, out int wrappedTimeMilliseconds, out double wrappedSection))
        {
            AddDiagnostic(
                mutableDiagnostics,
                ChartInfoParseDiagnosticSeverity.Info,
                "BMS_JAVA_INT_TIME_WRAP",
                "BMS timeline milliseconds exceeded int range and was stored with Java int wrap. rawMs="
                    + rawTimeMilliseconds.ToString(CultureInfo.InvariantCulture)
                    + " wrappedMs=" + wrappedTimeMilliseconds.ToString(CultureInfo.InvariantCulture)
                    + " section=" + FormatDouble(wrappedSection));
        }
        string chartString = model.ToChartString(timeoutGuard);
        IReadOnlyList<ChartInfoParseDiagnostic> readOnlyDiagnostics = diagnostics as IReadOnlyList<ChartInfoParseDiagnostic>
            ?? (diagnostics ?? Enumerable.Empty<ChartInfoParseDiagnostic>()).ToList();
        return new ChartInfoParseResult(BuildRow(model, chartString, timeoutGuard), readOnlyDiagnostics, chartString);
    }

    private static LR2SongDBExtended.chart_info BuildRow(ChartModel model, string chartString, ParseTimeoutGuard timeoutGuard)
    {
        timeoutGuard.ThrowIfTimedOut("build_row");
        int length = model.GetLastTimeMilliseconds();
        if (length < 0)
        {
            throw new BmsRecoverableParseException("BMS timeline length is too large.");
        }
        ChartStatistics statistics = ChartStatistics.Calculate(model, timeoutGuard);
        return new LR2SongDBExtended.chart_info
        {
            sha256 = model.Sha256,
            md5 = model.Md5,
            charthash = ComputeSha256Text(chartString),
            level = model.Level,
            difficulty = model.Difficulty,
            difficulty_defined = model.DifficultyDefined,
            mainbpm = statistics.MainBpm,
            maxbpm = ClampJavaDoubleToIntRangeForHugeBpm(model.GetMaxBpm()),
            minbpm = ClampJavaDoubleToIntRangeForHugeBpm(model.GetMinBpm()),
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

    private static ChartModel ParseBmsCandidate(string text, string chartName, bool isPms, IList<ChartInfoParseDiagnostic> diagnostics, IReadOnlyList<int> selectedRandoms, ParseTimeoutGuard timeoutGuard)
    {
        BmsChartBuilder builder = new BmsChartBuilder(chartName, isPms, diagnostics, timeoutGuard);
        Stack<int> selectedRandomStack = new Stack<int>();
        Stack<bool> skipStack = new Stack<bool>();
        using StringReader reader = new StringReader(text ?? string.Empty);
        string rawLine;
        int randomIndex = 0;
        int lineIndex = 0;
        while ((rawLine = reader.ReadLine()) != null)
        {
            timeoutGuard.ThrowIfTimedOutEvery(++lineIndex, "bms_line_scan");
            string line = (rawLine ?? string.Empty).TrimStart('\uFEFF');
            if (line.Length < 2 || line[0] != '#')
            {
                continue;
            }
            if (MatchesReserveWord(line, "RANDOM"))
            {
                if (TryParseJavaIntStrict(GetReserveWordArgument(line, "RANDOM"), out int randomMax))
                {
                    builder.HasRandom = true;
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
                    if (TryParseJavaIntStrict(GetReserveWordArgument(line, "IF"), out int branch))
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
            if (IsBmsChartLikeLine(line, out int chartLikeSection))
            {
                builder.TouchSection(chartLikeSection);
                Match channelMatch = BmsChannelLineRegex.Match(line);
                if (channelMatch.Success)
                {
                    int channel = ParseBase36(channelMatch.Groups[2].Value);
                    if (channel >= 0)
                    {
                        builder.AddChannelLine(chartLikeSection, channel, channelMatch.Groups[3].Value);
                    }
                    else
                    {
                        AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_CHANNEL_INVALID", "チャンネルに不正な値が定義されています");
                    }
                }
                else
                {
                    channelMatch = BmsChannelHeaderRegex.Match(line);
                    if (channelMatch.Success)
                    {
                        int channel = ParseBase36(channelMatch.Groups[2].Value);
                        if (channel >= 0)
                        {
                            builder.AddChannelLine(chartLikeSection, channel, line);
                        }
                        else
                        {
                            AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_CHANNEL_INVALID", "チャンネルに不正な値が定義されています");
                        }
                    }
                    else
                    {
                        AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_CHANNEL_INVALID", "チャンネルに不正な値が定義されています");
                    }
                }
                continue;
            }
            builder.ApplyCommand(line);
        }
        return builder.Build();
    }

    private static bool IsBmsChartLikeLine(string line, out int section)
    {
        if (!string.IsNullOrEmpty(line)
            && line.Length > 6
            && line[0] == '#'
            && line[1] >= '0'
            && line[1] <= '9'
            && line[2] >= '0'
            && line[2] <= '9'
            && line[3] >= '0'
            && line[3] <= '9')
        {
            section = (line[1] - '0') * 100 + (line[2] - '0') * 10 + (line[3] - '0');
            return true;
        }
        section = 0;
        return false;
    }

    private static List<int> ScanRandomMaxes(string text, ParseTimeoutGuard timeoutGuard)
    {
        List<int> randomMaxes = new List<int>();
        using StringReader reader = new StringReader(text ?? string.Empty);
        string rawLine;
        int lineIndex = 0;
        while ((rawLine = reader.ReadLine()) != null)
        {
            timeoutGuard.ThrowIfTimedOutEvery(++lineIndex, "bms_random_scan");
            string line = (rawLine ?? string.Empty).TrimStart('\uFEFF');
            if (line.Length >= 2
                && line[0] == '#'
                && MatchesReserveWord(line, "RANDOM")
                && TryParseJavaIntStrict(GetReserveWordArgument(line, "RANDOM"), out int randomMax))
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
        return ParseBmson(json, filePath, new List<ChartInfoParseDiagnostic>(), ParseTimeoutGuard.None);
    }

    private static ChartModel ParseBmson(string json, string chartName, IList<ChartInfoParseDiagnostic> diagnostics, ParseTimeoutGuard timeoutGuard)
    {
        timeoutGuard.ThrowIfTimedOut("bmson_parse_start");
        json = (json ?? string.Empty).TrimStart('\uFEFF');
        BmsonDocument document = ParseBmsonDocument(json);
        timeoutGuard.ThrowIfTimedOut("bmson_json_read");
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
            Title = info.Title ?? string.Empty,
            Subtitle = ComposeBmsonSubtitle(info.Subtitle, info.ChartName),
            Level = info.Level,
            Difficulty = null,
            DifficultyDefined = false,
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
        int eventMergeCount = 0;
        while (bpmPosition < bpmEvents.Length || stopPosition < stopEvents.Length || scrollPosition < scrollEvents.Length)
        {
            timeoutGuard.ThrowIfTimedOutEvery(++eventMergeCount, "bmson_timeline_events");
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
        int lineIndex = 0;
        foreach (BmsonBarLine line in document?.Lines ?? Array.Empty<BmsonBarLine>())
        {
            timeoutGuard.ThrowIfTimedOutEvery(++lineIndex, "bmson_bar_lines");
            GetBmsonTimeline(timelinesByY, line.Y, resolution, mode).HasSectionLine = true;
        }

        int[] keyAssign = mode.GetBmsonKeyAssign();
        List<ChartNote>[] longNotesByLane = CreateLaneLists(mode.KeyCount);
        Dictionary<string, ChartNote> pendingLongNoteEnds = new Dictionary<string, ChartNote>(StringComparer.Ordinal);
        int soundId = 0;
        foreach (BmsonSoundChannel channel in document?.SoundChannels ?? Array.Empty<BmsonSoundChannel>())
        {
            timeoutGuard.ThrowIfTimedOutEvery(soundId + 1, "bmson_sound_channels");
            AddBmsonSoundChannel(timelinesByY, resolution, mode, keyAssign, longNotesByLane, pendingLongNoteEnds, channel, soundId, model.LnMode, timeoutGuard);
            soundId++;
        }
        int hiddenChannelIndex = 0;
        foreach (BmsonMineChannel channel in document?.KeyChannels ?? Array.Empty<BmsonMineChannel>())
        {
            timeoutGuard.ThrowIfTimedOutEvery(++hiddenChannelIndex, "bmson_hidden_channels");
            AddBmsonHiddenChannel(timelinesByY, resolution, mode, keyAssign, channel, timeoutGuard);
        }
        int mineChannelIndex = 0;
        foreach (BmsonMineChannel channel in document?.MineChannels ?? Array.Empty<BmsonMineChannel>())
        {
            timeoutGuard.ThrowIfTimedOutEvery(++mineChannelIndex, "bmson_mine_channels");
            AddBmsonMineChannel(timelinesByY, resolution, mode, keyAssign, longNotesByLane, channel, timeoutGuard);
        }
        int bgaIndex = 0;
        foreach (BmsonBgaNote note in document?.Bga?.BgaEvents ?? Array.Empty<BmsonBgaNote>())
        {
            timeoutGuard.ThrowIfTimedOutEvery(++bgaIndex, "bmson_bga_events");
            GetBmsonTimeline(timelinesByY, note.Y, resolution, mode).HasBga = true;
        }

        model.SetTimelines(timelinesByY.Values.ToList());
        int totalNotes = model.GetTotalNotes();
        model.Difficulty = InferBeatorajaDifficulty(info.Title, ComposeBmsonSubtitle(info.Subtitle, info.ChartName), totalNotes);
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

    private static void AddBmsonSoundChannel(SortedList<int, ChartTimeline> timelinesByY, double resolution, ChartMode mode, int[] keyAssign, List<ChartNote>[] longNotesByLane, IDictionary<string, ChartNote> pendingLongNoteEnds, BmsonSoundChannel channel, int soundId, int modelLnMode, ParseTimeoutGuard timeoutGuard)
    {
        BmsonSoundNote[] notes = (channel?.Notes ?? Array.Empty<BmsonSoundNote>()).OrderBy((BmsonSoundNote item) => item.Y).ToArray();
        long startMicroseconds = 0L;
        for (int noteIndex = 0; noteIndex < notes.Length; noteIndex++)
        {
            timeoutGuard.ThrowIfTimedOutEvery(noteIndex + 1, "bmson_sound_notes");
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
                    if (!HasAnyNoteInRange(timelinesByY, lane, note.Y, note.Y + note.Length, timeoutGuard) && timeline.Notes[lane] == null)
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

    private static void AddBmsonHiddenChannel(SortedList<int, ChartTimeline> timelinesByY, double resolution, ChartMode mode, int[] keyAssign, BmsonMineChannel channel, ParseTimeoutGuard timeoutGuard)
    {
        int noteIndex = 0;
        foreach (BmsonMineNote note in channel?.Notes ?? Array.Empty<BmsonMineNote>())
        {
            timeoutGuard.ThrowIfTimedOutEvery(++noteIndex, "bmson_hidden_notes");
            int lane = note.X > 0 && note.X <= keyAssign.Length ? keyAssign[note.X - 1] : -1;
            if (lane >= 0)
            {
                GetBmsonTimeline(timelinesByY, note.Y, resolution, mode).HasHiddenNote = true;
            }
        }
    }

    private static void AddBmsonMineChannel(SortedList<int, ChartTimeline> timelinesByY, double resolution, ChartMode mode, int[] keyAssign, List<ChartNote>[] longNotesByLane, BmsonMineChannel channel, ParseTimeoutGuard timeoutGuard)
    {
        int noteIndex = 0;
        foreach (BmsonMineNote note in channel?.Notes ?? Array.Empty<BmsonMineNote>())
        {
            timeoutGuard.ThrowIfTimedOutEvery(++noteIndex, "bmson_mine_notes");
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
            Bpm = previous.Bpm
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

    private static string GetReserveWordArgument(string line, string word)
    {
        string safeLine = line ?? string.Empty;
        int start = word.Length + 2;
        return safeLine.Length > start ? safeLine.Substring(start).Trim() : string.Empty;
    }

    private static int ParseIntOrDefault(string value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : fallback;
    }

    private static bool TryParseJavaIntStrict(string value, out int result)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            result = 0;
            return false;
        }
        int index = 0;
        int sign = 1;
        if (text[index] == '+' || text[index] == '-')
        {
            sign = text[index] == '-' ? -1 : 1;
            index++;
            if (index >= text.Length)
            {
                result = 0;
                return false;
            }
        }
        long value64 = 0L;
        for (; index < text.Length; index++)
        {
            int digit = CharUnicodeInfo.GetDecimalDigitValue(text[index]);
            if (digit < 0)
            {
                result = 0;
                return false;
            }
            value64 = value64 * 10L + digit;
            long signed = value64 * sign;
            if (signed > int.MaxValue || signed < int.MinValue)
            {
                result = 0;
                return false;
            }
        }
        result = (int)(value64 * sign);
        return true;
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

    private static double ClampJavaDoubleToIntRangeForHugeBpm(double value)
    {
        if (double.IsNaN(value))
        {
            return 0.0;
        }
        if (value > int.MaxValue)
        {
            return int.MaxValue;
        }
        if (value < int.MinValue)
        {
            return int.MinValue;
        }
        return value;
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
        return JavaDoubleFormatCache.GetOrAdd(BitConverter.DoubleToInt64Bits(value), _ => JavaDoubleToStringJdk21.ToString(value));
    }

    private static bool TryParseJavaDouble(string value, out double result)
    {
        return JavaDoubleParserJdk17.TryParseDouble(value, out result);
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

    private static bool IsInsideBmsLongNote(IEnumerable<ChartNote> longNotes, double section)
    {
        return (longNotes ?? Enumerable.Empty<ChartNote>()).Any((ChartNote note) => note.Section <= section && section <= (note.Pair?.Section ?? note.Section));
    }

    private static bool HasNoteInsideLongNote(IEnumerable<ChartNote> longNotes, double startSection, double endSection)
    {
        return (longNotes ?? Enumerable.Empty<ChartNote>()).Any((ChartNote note) => startSection < (note.Pair?.Section ?? note.Section) && note.Section < endSection);
    }

    private static bool HasAnyNoteInRange(SortedList<int, ChartTimeline> timelinesByY, int lane, int startY, int endY, ParseTimeoutGuard timeoutGuard)
    {
        int timelineIndex = 0;
        foreach (KeyValuePair<int, ChartTimeline> entry in timelinesByY)
        {
            timeoutGuard.ThrowIfTimedOutEvery(++timelineIndex, "bmson_note_range_scan");
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

    private static int ToJavaInt(long value)
    {
        return unchecked((int)value);
    }

    /// <summary>
    /// chart_info backfill の協調 timeout に達したことを示します。
    /// 強制停止ではなく parser 内の checkpoint から投げ、worker thread を残さないための例外です。
    /// </summary>
    internal sealed class ChartInfoParseTimeoutException : TimeoutException
    {
        /// <summary>
        /// timeout した解析 phase と制限時間を持つ例外を作成します。
        /// </summary>
        /// <param name="timeoutMilliseconds">許可された解析時間。</param>
        /// <param name="phase">timeout を検出した parser 内 phase。</param>
        public ChartInfoParseTimeoutException(long timeoutMilliseconds, string phase)
            : base("chart info parse timed out after " + timeoutMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms phase=" + (phase ?? string.Empty))
        {
            TimeoutMilliseconds = timeoutMilliseconds;
            Phase = phase ?? string.Empty;
        }

        /// <summary>
        /// 許可された解析時間です。
        /// </summary>
        public long TimeoutMilliseconds { get; }

        /// <summary>
        /// timeout を検出した parser 内 phase です。
        /// </summary>
        public string Phase { get; }
    }

    private sealed class ParseTimeoutGuard
    {
        private const int CheckIntervalMask = 1023;

        public static readonly ParseTimeoutGuard None = new ParseTimeoutGuard(false, 0L, 0L);

        private readonly bool enabled;

        private readonly long deadlineTimestamp;

        private readonly long timeoutMilliseconds;

        private ParseTimeoutGuard(bool enabled, long deadlineTimestamp, long timeoutMilliseconds)
        {
            this.enabled = enabled;
            this.deadlineTimestamp = deadlineTimestamp;
            this.timeoutMilliseconds = timeoutMilliseconds;
        }

        public static ParseTimeoutGuard Start(TimeSpan? timeout)
        {
            if (!timeout.HasValue)
            {
                return None;
            }
            long timeoutMilliseconds = Math.Max(0L, (long)timeout.Value.TotalMilliseconds);
            long timeoutTicks = (long)(timeout.Value.TotalSeconds * Stopwatch.Frequency);
            long deadline = Stopwatch.GetTimestamp() + Math.Max(0L, timeoutTicks);
            return new ParseTimeoutGuard(true, deadline, timeoutMilliseconds);
        }

        public void ThrowIfTimedOutEvery(int iteration, string phase)
        {
            if ((iteration & CheckIntervalMask) == 0)
            {
                ThrowIfTimedOut(phase);
            }
        }

        public void ThrowIfTimedOut(string phase)
        {
            if (enabled && Stopwatch.GetTimestamp() >= deadlineTimestamp)
            {
                throw new ChartInfoParseTimeoutException(timeoutMilliseconds, phase);
            }
        }
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
        Info,
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

        private readonly ParseTimeoutGuard timeoutGuard;

        private readonly List<BmsChannelLine> channelLines = new List<BmsChannelLine>();

        private readonly Dictionary<int, double> bpmTable = new Dictionary<int, double>();

        private readonly Dictionary<int, double> stopTable = new Dictionary<int, double>();

        private readonly Dictionary<int, double> scrollTable = new Dictionary<int, double>();

        private int order;

        private int maxSection;

        public BmsChartBuilder(string filePath, bool isPms, IList<ChartInfoParseDiagnostic> diagnostics, ParseTimeoutGuard timeoutGuard)
        {
            this.filePath = filePath;
            this.isPms = isPms;
            this.diagnostics = diagnostics;
            this.timeoutGuard = timeoutGuard ?? ParseTimeoutGuard.None;
        }

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

        public double Total { get; private set; } = 100.0;

        public bool TotalDefined { get; private set; }

        public int LnObject { get; private set; } = -1;

        public int LnMode { get; private set; } = LongNoteTypeUndefined;

        public void AddChannelLine(int section, int channel, string data)
        {
            channelLines.Add(new BmsChannelLine(section, channel, data ?? string.Empty, order++));
            maxSection = Math.Max(maxSection, section);
        }

        public void TouchSection(int section)
        {
            maxSection = Math.Max(maxSection, section);
        }

        public void ApplyCommand(string line)
        {
            string trimmed = line.Trim();
            if (MatchesReserveWord(trimmed, "BPM"))
            {
                if (trimmed.Length > 4 && trimmed[4] == ' ')
                {
                    string argument = GetReserveWordArgument(trimmed, "BPM");
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
                    int key = ParseBase(trimmed.Substring(4, 2), Base);
                    string argument = trimmed.Substring(7).Trim();
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
            if (MatchesReserveWord(trimmed, "STOP"))
            {
                if (trimmed.Length >= 9)
                {
                    int key = ParseBase(trimmed.Substring(5, 2), Base);
                    string argument = trimmed.Substring(8).Trim();
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
            if (MatchesReserveWord(trimmed, "SCROLL"))
            {
                if (trimmed.Length >= 11)
                {
                    int key = ParseBase(trimmed.Substring(7, 2), Base);
                    string argument = trimmed.Substring(10).Trim();
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
            if (MatchesReserveWord(trimmed, "TITLE"))
            {
                Title = GetReserveWordArgument(trimmed, "TITLE");
                return;
            }
            if (MatchesReserveWord(trimmed, "SUBTITLE"))
            {
                Subtitle = GetReserveWordArgument(trimmed, "SUBTITLE");
                return;
            }
            if (MatchesReserveWord(trimmed, "PLAYLEVEL"))
            {
                string argument = GetReserveWordArgument(trimmed, "PLAYLEVEL");
                Level = TryParseJavaIntStrict(argument, out int level) ? level : null;
                return;
            }
            if (MatchesReserveWord(trimmed, "DIFFICULTY"))
            {
                string argument = GetReserveWordArgument(trimmed, "DIFFICULTY");
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
            if (MatchesReserveWord(trimmed, "RANK"))
            {
                string argument = GetReserveWordArgument(trimmed, "RANK");
                if (TryParseJavaIntStrict(argument, out int rank) && rank >= 0 && rank < 5)
                {
                    JudgeRank = rank;
                    JudgeRankType = JudgeRankType.BmsRank;
                }
                return;
            }
            if (MatchesReserveWord(trimmed, "DEFEXRANK"))
            {
                string argument = GetReserveWordArgument(trimmed, "DEFEXRANK");
                if (TryParseJavaIntStrict(argument, out int defExRank) && defExRank >= 1)
                {
                    JudgeRank = defExRank;
                    JudgeRankType = JudgeRankType.BmsDefExRank;
                }
                return;
            }
            if (MatchesReserveWord(trimmed, "TOTAL"))
            {
                string argument = GetReserveWordArgument(trimmed, "TOTAL");
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
            if (MatchesReserveWord(trimmed, "LNOBJ"))
            {
                string argument = GetReserveWordArgument(trimmed, "LNOBJ");
                LnObject = ParseBase(argument.Trim(), Base);
                if (LnObject < 0)
                {
                    AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_LNOBJ_INVALID", "#LNOBJに数字が定義されていません");
                }
                return;
            }
            if (MatchesReserveWord(trimmed, "LNMODE"))
            {
                string argument = GetReserveWordArgument(trimmed, "LNMODE");
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
            if (MatchesReserveWord(trimmed, "BASE"))
            {
                string argument = GetReserveWordArgument(trimmed, "BASE");
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
            timeoutGuard.ThrowIfTimedOut("bms_build_start");
            ChartMode mode = DetectMode();
            ChartModel model = new ChartModel(filePath, mode)
            {
                InitialBpm = InitialBpm,
                Title = Title,
                Subtitle = Subtitle,
                Level = Level,
                Difficulty = Difficulty,
                DifficultyDefined = DifficultyDefined,
                JudgeRank = NormalizeJudgeRank(JudgeRank, JudgeRankType, mode),
                Total = Total,
                TotalDefined = TotalDefined,
                LnMode = LnMode,
                HasRandom = HasRandom
            };
            double[] sectionStarts = BuildSectionStarts(out double[] sectionRates);
            SortedList<double, ChartTimeline> timelines = new SortedList<double, ChartTimeline>();
            ChartTimeline baseTimeline = new ChartTimeline(0.0, 0.0, mode.KeyCount)
            {
                Bpm = model.InitialBpm
            };
            timelines.Add(0.0, baseTimeline);
            List<ChartNote>[] longNotesByLane = CreateLaneLists(mode.KeyCount);
            ChartNote[] pendingLongStarts = new ChartNote[mode.KeyCount];
            for (int section = 0; section <= maxSection; section++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(section + 1, "bms_sections");
                double sectionStart = sectionStarts[section];
                double rate = section < sectionRates.Length ? sectionRates[section] : 1.0;
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
                if (start != null && start.Owner != null && start.Section != double.MinValue)
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
            int totalNotes = model.GetTotalNotes(timeoutGuard);
            if (!model.DifficultyDefined)
            {
                model.Difficulty = InferBeatorajaDifficulty(model.Title, model.Subtitle, totalNotes);
            }
            if (!TotalDefined)
            {
                model.Total = CalculateDefaultTotal(mode, totalNotes);
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
            int lineIndex = 0;
            foreach (BmsChannelLine line in channelLines)
            {
                timeoutGuard.ThrowIfTimedOutEvery(++lineIndex, "bms_detect_mode");
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

        private double[] BuildSectionStarts(out double[] rates)
        {
            rates = Enumerable.Repeat(1.0, Math.Max(1, maxSection + 1)).ToArray();
            int rateLineIndex = 0;
            foreach (BmsChannelLine line in channelLines.Where((BmsChannelLine item) => item.Channel == SectionRate))
            {
                timeoutGuard.ThrowIfTimedOutEvery(++rateLineIndex, "bms_section_rates");
                if (TryParseJavaDouble(line.Data, out double rate))
                {
                    rates[line.Section] = rate;
                }
            }
            double[] starts = new double[rates.Length + 1];
            for (int i = 1; i < starts.Length; i++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(i, "bms_section_start_accumulate");
                starts[i] = starts[i - 1] + rates[i - 1];
            }
            return starts;
        }

        private void ApplyEvents(SortedList<double, ChartTimeline> timelines, ChartMode mode, int section, double sectionStart, double rate)
        {
            List<BmsTimelineEvent> events = new List<BmsTimelineEvent>();
            int eventLineIndex = 0;
            foreach (BmsChannelLine line in channelLines.Where((BmsChannelLine item) => item.Section == section))
            {
                timeoutGuard.ThrowIfTimedOutEvery(++eventLineIndex, "bms_event_lines");
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
            int eventIndex = 0;
            foreach (BmsTimelineEvent timelineEvent in events.OrderBy((BmsTimelineEvent item) => item.Position).ThenBy((BmsTimelineEvent item) => item.Priority))
            {
                timeoutGuard.ThrowIfTimedOutEvery(++eventIndex, "bms_apply_events");
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
            int pairIndex = 0;
            foreach (BmsDataPair pair in SplitData(line.Data))
            {
                timeoutGuard.ThrowIfTimedOutEvery(++pairIndex, "bms_apply_note_line");
                ChartTimeline timeline = GetBmsTimeline(timelines, sectionStart + rate * pair.Position, mode);
                if (kind == BmsLaneChannelKind.Hidden)
                {
                    timeline.HasHiddenNote = true;
                }
                else if (kind == BmsLaneChannelKind.Normal)
                {
                    ApplyBmsNormalNote(timelines, lane, pair.Value, timeline, longNotesByLane, pendingLongStarts);
                }
                else if (kind == BmsLaneChannelKind.Long)
                {
                    ApplyBmsLongNote(timelines, lane, pair.Value, timeline, longNotesByLane, pendingLongStarts);
                }
                else if (kind == BmsLaneChannelKind.Mine && timeline.Notes[lane] == null && !IsInsideBmsLongNote(longNotesByLane[lane], timeline.Section))
                {
                    timeline.SetNote(lane, ChartNote.CreateMine(pair.Value));
                }
            }
        }

        private void ApplyBmsNormalNote(SortedList<double, ChartTimeline> timelines, int lane, int data, ChartTimeline timeline, List<ChartNote>[] longNotesByLane, ChartNote[] pendingLongStarts)
        {
            if (data == LnObject)
            {
                int previousIndex = 0;
                foreach (ChartTimeline previous in timelines.Values.Reverse().Where((ChartTimeline item) => item.Section < timeline.Section))
                {
                    timeoutGuard.ThrowIfTimedOutEvery(++previousIndex, "bms_lnobj_backscan");
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
                        pendingLongStarts[lane] = null;
                    }
                    break;
                }
                return;
            }
            timeline.SetNote(lane, ChartNote.CreateNormal(data));
        }

        private void ApplyBmsLongNote(SortedList<double, ChartTimeline> timelines, int lane, int data, ChartTimeline timeline, List<ChartNote>[] longNotesByLane, ChartNote[] pendingLongStarts)
        {
            if (IsInsideBmsLongNote(longNotesByLane[lane], timeline.Section))
            {
                ChartNote pending = pendingLongStarts[lane];
                if (pending == null)
                {
                    ChartNote ignored = ChartNote.CreateLong(data, LnMode);
                    ignored.Section = double.MinValue;
                    pendingLongStarts[lane] = ignored;
                }
                else
                {
                    if (pending.Section != double.MinValue && pending.Owner != null)
                    {
                        pending.Owner.SetNote(lane, null);
                    }
                    pendingLongStarts[lane] = null;
                }
                return;
            }
            ChartNote start = pendingLongStarts[lane];
            if (start != null && start.Section == double.MinValue)
            {
                pendingLongStarts[lane] = null;
                return;
            }
            if (start == null)
            {
                ChartNote existing = timeline.Notes[lane];
                if (existing != null && existing.Kind == ChartNoteKind.Normal && existing.Wav != data)
                {
                    timeline.HasBackground = true;
                }
                ChartNote note = ChartNote.CreateLong(data, LnMode);
                timeline.SetNote(lane, note);
                pendingLongStarts[lane] = note;
                return;
            }
            bool foundStart = false;
            int previousIndex = 0;
            foreach (ChartTimeline previous in timelines.Values.Reverse().Where((ChartTimeline item) => item.Section < timeline.Section))
            {
                timeoutGuard.ThrowIfTimedOutEvery(++previousIndex, "bms_long_note_backscan");
                if (previous.Section == start.Section)
                {
                    foundStart = true;
                    break;
                }
                ChartNote existing = previous.Notes[lane];
                if (existing != null)
                {
                    previous.SetNote(lane, null);
                    if (existing.Kind == ChartNoteKind.Normal)
                    {
                        previous.HasBackground = true;
                    }
                }
            }
            if (!foundStart)
            {
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
                timeoutGuard.ThrowIfTimedOutEvery(index + 1, "bms_split_data");
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

        public string Title { get; set; } = string.Empty;

        public string Subtitle { get; set; } = string.Empty;

        public double InitialBpm { get; set; }

        public int? Level { get; set; }

        public int? Difficulty { get; set; }

        public bool DifficultyDefined { get; set; }

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

        public int GetTotalNotes(ParseTimeoutGuard timeoutGuard)
        {
            int total = 0;
            for (int index = 0; index < Timelines.Count; index++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(index + 1, "model_total_notes");
                total += Timelines[index].GetTotalNotes(LntypeLongNote, timeoutGuard);
            }
            return total;
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
            return ToJavaInt(GetLastTimeMillisecondsLong());
        }

        public bool TryGetJavaIntTimeWrap(out long rawTimeMilliseconds, out int wrappedTimeMilliseconds, out double section)
        {
            foreach (ChartTimeline timeline in Timelines)
            {
                long timelineMilliseconds = timeline.TimeMillisecondsLong;
                int wrapped = ToJavaInt(timelineMilliseconds);
                if (timelineMilliseconds != wrapped)
                {
                    rawTimeMilliseconds = timelineMilliseconds;
                    wrappedTimeMilliseconds = wrapped;
                    section = timeline.Section;
                    return true;
                }
            }
            rawTimeMilliseconds = 0L;
            wrappedTimeMilliseconds = 0;
            section = 0.0;
            return false;
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
            return ToChartString(ParseTimeoutGuard.None);
        }

        public string ToChartString(ParseTimeoutGuard timeoutGuard)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("JUDGERANK:").Append(JudgeRank).Append('\n');
            builder.Append("TOTAL:").Append(FormatDouble(Total)).Append('\n');
            if (LnMode != 0)
            {
                builder.Append("LNMODE:").Append(LnMode).Append('\n');
            }
            double? currentBpm = null;
            int timelineIndex = 0;
            foreach (ChartTimeline timeline in Timelines)
            {
                timeoutGuard.ThrowIfTimedOutEvery(++timelineIndex, "chart_string");
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

        public int TimeMilliseconds => ToJavaInt(TimeMillisecondsLong);

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
            return GetTotalNotes(lntype, ParseTimeoutGuard.None);
        }

        public int GetTotalNotes(int lntype, ParseTimeoutGuard timeoutGuard)
        {
            int count = 0;
            for (int index = 0; index < Notes.Length; index++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(index + 1, "timeline_total_notes");
                ChartNote note = Notes[index];
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
            return FormatDouble(Bpm);
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

        public static ChartStatistics Calculate(ChartModel model, ParseTimeoutGuard timeoutGuard)
        {
            ChartStatistics result = new ChartStatistics();
            int laneCount = model.Mode.KeyCount;
            int[][] laneNotes = new int[laneCount][];
            for (int lane = 0; lane < laneCount; lane++)
            {
                laneNotes[lane] = new int[3];
            }
            result.TotalNotes = model.GetTotalNotes(timeoutGuard);
            result.NormalKeyNotes = CountNotes(model, scratch: false, longNotes: false, timeoutGuard);
            result.LongKeyNotes = CountNotes(model, scratch: false, longNotes: true, timeoutGuard);
            result.NormalScratchNotes = CountNotes(model, scratch: true, longNotes: false, timeoutGuard);
            result.LongScratchNotes = CountNotes(model, scratch: true, longNotes: true, timeoutGuard);

            DistributionBuckets distribution = BuildDistribution(model, laneNotes, result.TotalNotes, out int borderPosition, timeoutGuard);
            result.Distribution = EncodeDistribution(distribution, timeoutGuard);
            result.LaneNotes = EncodeLaneNotes(laneNotes);
            CalculateDensity(distribution, borderPosition, result, timeoutGuard);
            CalculateSpeed(model, result, timeoutGuard);
            return result;
        }

        private static int CountNotes(ChartModel model, bool scratch, bool longNotes, ParseTimeoutGuard timeoutGuard)
        {
            int count = 0;
            int timelineIndex = 0;
            foreach (ChartTimeline timeline in model.Timelines)
            {
                timeoutGuard.ThrowIfTimedOutEvery(++timelineIndex, "count_notes");
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

        private static DistributionBuckets BuildDistribution(ChartModel model, int[][] laneNotes, int totalNotes, out int borderPosition, ParseTimeoutGuard timeoutGuard)
        {
            timeoutGuard.ThrowIfTimedOut("build_distribution_start");
            int lastTime = model.GetLastTimeMilliseconds();
            int lastTimeSeconds = lastTime / 1000;
            if (lastTime < 0)
            {
                throw new BmsRecoverableParseException("BMS timeline length is too large.");
            }
            int bucketCount = checked(lastTimeSeconds + 2);
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
            int timelineIndex = 0;
            foreach (ChartTimeline timeline in model.Timelines)
            {
                timeoutGuard.ThrowIfTimedOutEvery(++timelineIndex, "build_distribution_timelines");
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
                            timeoutGuard.ThrowIfTimedOutEvery(fillSecond - second + 1, "build_distribution_long_note");
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

        private static void CalculateDensity(DistributionBuckets data, int borderPosition, ChartStatistics result, ParseTimeoutGuard timeoutGuard)
        {
            int threshold = data.BucketCount > 0 ? result.TotalNotes / data.BucketCount / 4 : 0;
            double density = 0.0;
            double peak = 0.0;
            int count = 0;
            for (int second = 0; second < data.BucketCount; second++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(second + 1, "calculate_density");
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
                    timeoutGuard.ThrowIfTimedOutEvery(second - Math.Max(0, borderPosition) + 1, "calculate_end_density");
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

        private static void CalculateSpeed(ChartModel model, ChartStatistics result, ParseTimeoutGuard timeoutGuard)
        {
            List<double[]> speedList = new List<double[]>();
            Dictionary<double, int> bpmNoteCounts = new Dictionary<double, int>();
            List<double> bpmInsertionOrder = new List<double>();
            double currentSpeed = model.InitialBpm;
            speedList.Add(new[] { currentSpeed, 0.0 });
            int speedChangeCount = 0;
            int timelineIndex = 0;
            foreach (ChartTimeline timeline in model.Timelines)
            {
                timeoutGuard.ThrowIfTimedOutEvery(++timelineIndex, "calculate_speed");
                bpmNoteCounts.TryGetValue(timeline.Bpm, out int noteCount);
                if (!bpmNoteCounts.ContainsKey(timeline.Bpm))
                {
                    bpmInsertionOrder.Add(timeline.Bpm);
                }
                bpmNoteCounts[timeline.Bpm] = noteCount + timeline.GetTotalNotes(LntypeLongNote, timeoutGuard);
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
            result.MainBpm = SelectMainBpmInJavaHashMapOrder(bpmNoteCounts, bpmInsertionOrder);
            result.SpeedChange = string.Join(",", speedList.SelectMany((double[] values) => values).Select(FormatDouble));
            result.SpeedChangeCount = speedChangeCount;
        }

        private static double SelectMainBpmInJavaHashMapOrder(IDictionary<double, int> bpmNoteCounts, IReadOnlyList<double> insertionOrder)
        {
            if (bpmNoteCounts.Count == 0)
            {
                return 0.0;
            }
            int capacity = 16;
            int threshold = 12;
            while (bpmNoteCounts.Count > threshold)
            {
                capacity *= 2;
                threshold = (int)(capacity * 0.75);
            }
            double result = 0.0;
            int maxCount = 0;
            foreach (double bpm in insertionOrder
                .Select((double bpm, int index) => new { Bpm = bpm, Index = index, Bucket = JavaHashMapBucket(bpm, capacity) })
                .OrderBy((item) => item.Bucket)
                .ThenBy((item) => item.Index)
                .Select((item) => item.Bpm))
            {
                int count = bpmNoteCounts[bpm];
                if (count > maxCount)
                {
                    maxCount = count;
                    result = bpm;
                }
            }
            return result;
        }

        private static int JavaHashMapBucket(double value, int capacity)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            int hash = unchecked((int)(bits ^ ((long)((ulong)bits >> 32))));
            hash ^= (int)((uint)hash >> 16);
            return hash & (capacity - 1);
        }

        private static string EncodeDistribution(DistributionBuckets values, ParseTimeoutGuard timeoutGuard)
        {
            StringBuilder builder = new StringBuilder(values.BucketCount * 14 + 1);
            builder.Append('#');
            for (int second = 0; second < values.BucketCount; second++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(second + 1, "encode_distribution");
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
