using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 共通の不変詳細情報と解析失敗を生成します。保存行への変換はDB境界だけで行います。
/// 基本譜面情報とは解析の責務が異なるため、BmsChartFileParser/BmsonChartFileParser から独立させています。
/// </summary>
internal static partial class ChartInfoParser
{
    private static readonly ConcurrentDictionary<long, string> JavaDoubleFormatCache = new();

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

    /// <summary>
    /// 指定された譜面ファイルを解析し、chart_info 行を返します。
    /// </summary>
    /// <param name="filePath">解析対象の譜面ファイル。</param>
    /// <param name="md5">既に分かっている MD5。null の場合はファイルから計算します。</param>
    /// <param name="sha256">既に分かっている SHA-256。null の場合はファイルから計算します。</param>
    /// <param name="encodingName">低レベル検証用の BMS decode override。通常の chart_info backfill では null にし、beatoraja 互換の既定 decode を使います。bmson では使用しません。</param>
    /// <returns>保存可能な chart_info 行。</returns>
    public static BeMusicSeeker.Models.ChartDetails Parse(string filePath, string md5 = null, string sha256 = null, string encodingName = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentNullException(nameof(filePath));
        }
        string fullPath = LongPathFileSystem.NormalizePathForStorage(filePath);
        return ParseBytes(LongPathFileSystem.ReadAllBytes(fullPath), fullPath, md5, sha256, encodingName);
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
    public static BeMusicSeeker.Models.ChartDetails ParseBytes(byte[] bytes, string fileNameOrExtension, string md5 = null, string sha256 = null, string encodingName = null)
    {
        return ParseBytesDetailed(bytes, fileNameOrExtension, md5, sha256, encodingName, retainChartString: false).Row;
    }

    /// <summary>
    /// 既に読み込まれた譜面バイト列を解析し、診断情報と検証用 chart string も返します。
    /// chart_info 生成の正規 entry point です。file diff / package install inline 解析と full backfill は、
    /// どちらもこの bytes entry point に寄せ、ファイル読み込みと詳細 metadata 解析を分離します。
    /// timeout を渡すと、協調 checkpoint で長時間解析を parse failure として扱います。
    /// </summary>
    /// <param name="bytes">譜面ファイルのバイト列。</param>
    /// <param name="fileNameOrExtension">拡張子判定に使うファイル名または拡張子。</param>
    /// <param name="md5">既に分かっている MD5。null の場合は bytes から計算します。</param>
    /// <param name="sha256">既に分かっている SHA-256。null の場合は bytes から計算します。</param>
    /// <param name="encodingName">低レベル検証用の BMS decode override。通常の chart_info backfill では null にし、beatoraja 互換の既定 decode を使います。bmson では使用しません。</param>
    /// <param name="timeout">解析 timeout。null の場合は timeout なし。</param>
    /// <param name="retainChartString">同じUTF-8出力から検証用文字列を保持するか。falseではChartStringはnullです。</param>
    /// <returns>保存可能な chart_info 行、診断情報、要求時だけ保持するchart string。</returns>
    /// <exception cref="ChartInfoParseTimeoutException">指定 timeout を超えた場合。</exception>
    internal static ChartInfoParseResult ParseBytesDetailed(byte[] bytes, string fileNameOrExtension, string md5 = null, string sha256 = null, string encodingName = null, TimeSpan? timeout = null, bool retainChartString = true)
    {
        if (bytes == null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }
        var timeoutGuard = ParseTimeoutGuard.Start(timeout);
        timeoutGuard.ThrowIfTimedOut("parse_start");
        List<ChartInfoParseDiagnostic> diagnostics = [];
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
                timeoutGuard,
                retainChartString);
        }
        ChartModel model = ParseBmson(DecodeBmson(bytes), chartName, diagnostics, timeoutGuard);
        model.Md5 = resolvedMd5;
        model.Sha256 = resolvedSha256;
        return BuildParseResult(model, diagnostics, timeoutGuard, retainChartString);
    }

    private static ChartInfoParseResult ParseBmsBytesDetailed(string text, string chartName, bool isPms, IList<ChartInfoParseDiagnostic> diagnostics, string md5, string sha256, ParseTimeoutGuard timeoutGuard, bool retainChartString)
    {
        text ??= string.Empty;
        var input = new BmsInput(text, timeoutGuard);
        List<int> randomMaxes = input.RandomMaxes;
        if (randomMaxes.Count == 0)
        {
            try
            {
                return BuildBmsParseResult(ParseBmsCandidate(input, chartName, isPms, diagnostics, null, timeoutGuard), diagnostics, md5, sha256, timeoutGuard, retainChartString);
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
            List<ChartInfoParseDiagnostic> candidateDiagnostics = [];
            try
            {
                ChartModel model = ParseBmsCandidate(input, chartName, isPms, candidateDiagnostics, selectedRandoms, timeoutGuard);
                ChartInfoParseResult result = BuildBmsParseResult(model, candidateDiagnostics, md5, sha256, timeoutGuard, retainChartString);
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

    private static ChartInfoParseResult BuildBmsParseResult(ChartModel model, IEnumerable<ChartInfoParseDiagnostic> diagnostics, string md5, string sha256, ParseTimeoutGuard timeoutGuard, bool retainChartString)
    {
        model.Md5 = md5;
        model.Sha256 = sha256;
        timeoutGuard.ThrowIfTimedOut("bms_last_time");
        _ = model.GetLastTimeMilliseconds();
        var mutableDiagnostics = diagnostics as IList<ChartInfoParseDiagnostic>;
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
        IReadOnlyList<ChartInfoParseDiagnostic> readOnlyDiagnostics = diagnostics as IReadOnlyList<ChartInfoParseDiagnostic>
            ?? [.. (diagnostics ?? [])];
        return BuildParseResult(model, readOnlyDiagnostics, timeoutGuard, retainChartString);
    }

    private static ChartInfoParseResult BuildParseResult(ChartModel model, IReadOnlyList<ChartInfoParseDiagnostic> diagnostics, ParseTimeoutGuard timeoutGuard, bool retainChartString)
    {
        ChartUtf8Buffer chart = model.WriteChartUtf8(timeoutGuard);
        string chartHash = Convert.ToHexStringLower(SHA256.HashData(chart.WrittenSpan));
        return new ChartInfoParseResult(BuildRow(model, chartHash, timeoutGuard), diagnostics,
            retainChartString ? Encoding.UTF8.GetString(chart.WrittenSpan) : null);
    }

    private static BeMusicSeeker.Models.ChartDetails BuildRow(ChartModel model, string chartHash, ParseTimeoutGuard timeoutGuard)
    {
        timeoutGuard.ThrowIfTimedOut("build_row");
        int length = model.GetLastTimeMilliseconds();
        if (length < 0)
        {
            throw new BmsRecoverableParseException("BMS timeline length is too large.");
        }
        var statistics = ChartStatistics.Calculate(model, timeoutGuard);
        return new BeMusicSeeker.Models.ChartDetails
        {
            sha256 = model.Sha256,
            md5 = model.Md5,
            charthash = chartHash,
            level = model.Level,
            difficulty = model.Difficulty,
            difficulty_defined = model.DifficultyDefined,
            mainbpm = statistics.MainBpm,
            maxbpm = ClampJavaDoubleToIntRangeForHugeBpm(model.GetMaxBpm()),
            minbpm = ClampJavaDoubleToIntRangeForHugeBpm(model.GetMinBpm()),
            length = length,
            mode = model.DisplayMode,
            judge = model.JudgeRank,
            bga = model.HasBga ? 1 : 0,
            exlevel = model.ExLevel,
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

    private static IEnumerable<int[]> BuildRandomCandidates(IReadOnlyList<int> randomMaxes, string md5, string sha256, string chartName)
    {
        List<int[]> candidates = [];
        AddDistinctRandomCandidate(candidates, CreateUniformRandomCandidate(randomMaxes, 1));
        AddDistinctRandomCandidate(candidates, CreateUniformRandomCandidate(randomMaxes, 2));
        AddDistinctRandomCandidate(candidates, CreateUniformRandomCandidate(randomMaxes, 3));
        AddDistinctRandomCandidate(candidates, CreateUniformRandomCandidate(randomMaxes, 4));
        AddDistinctRandomCandidate(candidates, [.. randomMaxes.Select(max => Math.Max(1, max))]);
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
        using (var sha = SHA256.Create())
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
        string signature = string.Join(",", candidate.Select(value => value.ToString(CultureInfo.InvariantCulture)));
        foreach (int[] existing in candidates)
        {
            string existingSignature = string.Join(",", existing.Select(value => value.ToString(CultureInfo.InvariantCulture)));
            if (string.Equals(existingSignature, signature, StringComparison.Ordinal))
            {
                return;
            }
        }
        candidates.Add(candidate);
    }

    private static ChartModel ParseBmson(string filePath)
    {
        string json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(LongPathFileSystem.ReadAllBytes(filePath));
        return ParseBmson(json, filePath, [], ParseTimeoutGuard.None);
    }

    private static ChartModel ParseBmson(string json, string chartName, IList<ChartInfoParseDiagnostic> diagnostics, ParseTimeoutGuard timeoutGuard)
    {
        timeoutGuard.ThrowIfTimedOut("bmson_parse_start");
        json = (json ?? string.Empty).TrimStart('\uFEFF');
        BmsonDocument document = ParseBmsonDocument(json);
        timeoutGuard.ThrowIfTimedOut("bmson_json_read");
        BmsonInfo info = document?.Info ?? new BmsonInfo();
        var resolvedMode = ChartMode.FromBmsonHint(info.ModeHint);
        if (resolvedMode == null)
        {
            AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMSON_MODE_UNSUPPORTED", "非対応のmode_hintです");
        }
        ChartMode mode = resolvedMode ?? ChartMode.Beat7;
        var model = new ChartModel(chartName, mode)
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

        SortedList<int, ChartTimeline> timelinesByY = [];
        var baseTimeline = new ChartTimeline(0.0, 0.0, mode.KeyCount)
        {
            Bpm = model.InitialBpm
        };
        timelinesByY.Add(0, baseTimeline);

        double resolution = info.Resolution > 0 ? info.Resolution * 4.0 : 960.0;
        BmsonBpmEvent[] bpmEvents = [.. (document?.BpmEvents ?? []).OrderBy(item => item.Y)];
        BmsonStopEvent[] stopEvents = [.. (document?.StopEvents ?? []).OrderBy(item => item.Y)];
        BmsonScrollEvent[] scrollEvents = [.. (document?.ScrollEvents ?? []).OrderBy(item => item.Y)];
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
        foreach (BmsonBarLine line in document?.Lines ?? [])
        {
            timeoutGuard.ThrowIfTimedOutEvery(++lineIndex, "bmson_bar_lines");
            GetBmsonTimeline(timelinesByY, line.Y, resolution, mode).HasSectionLine = true;
        }

        int[] keyAssign = mode.GetBmsonKeyAssign();
        List<ChartNote>[] longNotesByLane = CreateLaneLists(mode.KeyCount);
        var pendingLongNoteEnds = new Dictionary<string, ChartNote>(StringComparer.Ordinal);
        int soundId = 0;
        foreach (BmsonSoundChannel channel in document?.SoundChannels ?? [])
        {
            timeoutGuard.ThrowIfTimedOutEvery(soundId + 1, "bmson_sound_channels");
            AddBmsonSoundChannel(timelinesByY, resolution, mode, keyAssign, longNotesByLane, pendingLongNoteEnds, channel, soundId, model.LnMode, timeoutGuard);
            soundId++;
        }
        int hiddenChannelIndex = 0;
        foreach (BmsonMineChannel channel in document?.KeyChannels ?? [])
        {
            timeoutGuard.ThrowIfTimedOutEvery(++hiddenChannelIndex, "bmson_hidden_channels");
            AddBmsonHiddenChannel(timelinesByY, resolution, mode, keyAssign, channel, timeoutGuard);
        }
        int mineChannelIndex = 0;
        foreach (BmsonMineChannel channel in document?.MineChannels ?? [])
        {
            timeoutGuard.ThrowIfTimedOutEvery(++mineChannelIndex, "bmson_mine_channels");
            AddBmsonMineChannel(timelinesByY, resolution, mode, keyAssign, longNotesByLane, channel, timeoutGuard);
        }
        int bgaIndex = 0;
        foreach (BmsonBgaNote note in document?.Bga?.BgaEvents ?? [])
        {
            timeoutGuard.ThrowIfTimedOutEvery(++bgaIndex, "bmson_bga_events");
            GetBmsonTimeline(timelinesByY, note.Y, resolution, mode).HasBga = true;
        }

        model.SetTimelines([.. timelinesByY.Values], timeoutGuard);
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
        BmsonSoundNote[] notes = [.. (channel?.Notes ?? []).OrderBy(item => item.Y)];
        long startMicroseconds = 0L;
        int nextDistinctNoteIndex = 0;
        for (int noteIndex = 0; noteIndex < notes.Length; noteIndex++)
        {
            timeoutGuard.ThrowIfTimedOutEvery(noteIndex + 1, "bmson_sound_notes");
            BmsonSoundNote note = notes[noteIndex];
            BmsonSoundNote next = AdvanceToNextBmsonContinuationNote(
                notes,
                noteIndex,
                ref nextDistinctNoteIndex);
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
                        var start = ChartNote.CreateLong(soundId, note.Type > 0 && note.Type <= 3 ? note.Type : modelLnMode, audioStartMicroseconds, durationMicroseconds);
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

    internal static BmsonSoundNote AdvanceToNextBmsonContinuationNote(
        BmsonSoundNote[] notes,
        int noteIndex,
        ref int nextDistinctNoteIndex)
    {
        if (notes == null
            || noteIndex < 0
            || noteIndex >= notes.Length)
        {
            return null;
        }
        if (nextDistinctNoteIndex <= noteIndex)
        {
            nextDistinctNoteIndex = noteIndex + 1;
        }
        int currentY = notes[noteIndex].Y;
        while (nextDistinctNoteIndex < notes.Length
            && notes[nextDistinctNoteIndex].Y <= currentY)
        {
            nextDistinctNoteIndex++;
        }
        return nextDistinctNoteIndex < notes.Length ? notes[nextDistinctNoteIndex] : null;
    }

    private static string MakeBmsonLongNoteEndKey(int x, int y)
    {
        return x.ToString(CultureInfo.InvariantCulture) + ":" + y.ToString(CultureInfo.InvariantCulture);
    }

    private static ChartNote FindLongNoteEnd(IEnumerable<ChartNote> longNotes, double endSection)
    {
        foreach (ChartNote longNote in longNotes ?? [])
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
        foreach (BmsonMineNote note in channel?.Notes ?? [])
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
        foreach (BmsonMineNote note in channel?.Notes ?? [])
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
        var timeline = new ChartTimeline(section, preciseTime, mode.KeyCount)
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
        return ResolveBmsEncoding(encodingName).GetString(bytes ?? []);
    }

    private static string DecodeBmson(byte[] bytes)
    {
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes ?? []);
    }

    private static bool TryParseJavaIntStrict(string value, out int result)
    {
        return TryParseJavaIntStrict(value.AsSpan(), out result);
    }

    private static bool TryParseJavaIntStrict(ReadOnlySpan<char> value, out int result)
    {
        ReadOnlySpan<char> text = value.Trim();
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
        return ParseBase(value.AsSpan(), numberBase);
    }

    private static int ParseBase(ReadOnlySpan<char> value, int numberBase)
    {
        if (value.IsEmpty)
        {
            return -1;
        }
        int result = 0;
        foreach (char c in value)
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

    private static string ComputeHash(byte[] bytes, HashAlgorithm algorithm)
    {
        using (algorithm)
        {
            byte[] hash = algorithm.ComputeHash(bytes ?? []);
            return ToHex(hash);
        }
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
        var builder = new StringBuilder(hash.Length * 2);
        foreach (byte value in hash)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static string FormatDouble(double value)
    {
        return JavaDoubleFormatCache.GetOrAdd(BitConverter.DoubleToInt64Bits(value), static bits => JavaDoubleToStringJdk21.ToString(BitConverter.Int64BitsToDouble(bits)));
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
        int[] table = mode == ChartMode.Popn9 ? [33, 50, 70, 100, 133] : [25, 50, 75, 100, 125];
        return type switch
        {
            JudgeRankType.BmsRank => judgeRank >= 0 && judgeRank < 5 ? table[judgeRank] : table[2],
            JudgeRankType.BmsDefExRank => judgeRank > 0 ? judgeRank * table[2] / 100 : table[2],
            _ => judgeRank > 0 ? judgeRank : 100,
        };
    }

    private static BmsonDocument ParseBmsonDocument(string json)
    {
        return BmsonJsonParser.Parse(json);
    }

    private static List<ChartNote>[] CreateLaneLists(int laneCount)
    {
        var result = new List<ChartNote>[laneCount];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = [];
        }
        return result;
    }

    private static bool IsInsideLongNote(IEnumerable<ChartNote> longNotes, double section)
    {
        return (longNotes ?? []).Any(note => note.Section < section && section <= (note.Pair?.Section ?? note.Section));
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

        public ChartInfoParseTimeoutException() : base()
        {
        }

        public ChartInfoParseTimeoutException(string message) : base(message)
        {
        }

        public ChartInfoParseTimeoutException(string message, Exception innerException) : base(message, innerException)
        {
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

        public static readonly ParseTimeoutGuard None = new(false, 0L, 0L);

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

#nullable enable
    /// <summary>解析行と診断を保持します。検証用文字列は要求された場合にだけ保持します。</summary>
    internal sealed class ChartInfoParseResult(BeMusicSeeker.Models.ChartDetails row, IReadOnlyList<ChartInfoParseDiagnostic> diagnostics, string? chartString)
    {
        public BeMusicSeeker.Models.ChartDetails Row { get; } = row;

        public IReadOnlyList<ChartInfoParseDiagnostic> Diagnostics { get; } = diagnostics;

        /// <summary>同じhash入力から生成した検証用文字列。保持を要求しなかった場合はnullです。</summary>
        public string? ChartString { get; } = chartString;
    }
#nullable restore

    internal sealed class ChartInfoParseDiagnostic(ChartInfoParser.ChartInfoParseDiagnosticSeverity severity, string code, string message)
    {
        public ChartInfoParseDiagnosticSeverity Severity { get; } = severity;

        public string Code { get; } = code ?? string.Empty;

        public string Message { get; } = message ?? string.Empty;
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

        public BmsRecoverableParseException() : base()
        {
        }
    }

    private enum JudgeRankType
    {
        BmsRank,
        BmsDefExRank,
        BmsonJudgeRank
    }

    private enum BmsLaneChannelKind
    {
        None,
        Normal,
        Hidden,
        Long,
        Mine
    }

    private enum BmsTimelineEventKind
    {
        Scroll,
        Bpm,
        Stop
    }

    private readonly struct BmsTimelineEvent
    {
        public static readonly IComparer<BmsTimelineEvent> Comparer = new BmsTimelineEventComparer();

        private BmsTimelineEvent(double position, int priority, BmsTimelineEventKind kind, double value, int sequence)
        {
            Position = position;
            Priority = priority;
            Kind = kind;
            Value = value;
            Sequence = sequence;
        }

        public double Position { get; }

        public int Priority { get; }

        public BmsTimelineEventKind Kind { get; }

        public double Value { get; }

        public int Sequence { get; }

        public static BmsTimelineEvent CreateScroll(double position, double scroll, int sequence)
        {
            return new BmsTimelineEvent(position, 0, BmsTimelineEventKind.Scroll, scroll, sequence);
        }

        public static BmsTimelineEvent CreateBpm(double position, double bpm, int sequence)
        {
            return new BmsTimelineEvent(position, 1, BmsTimelineEventKind.Bpm, bpm, sequence);
        }

        public static BmsTimelineEvent CreateStop(double position, double stop, int sequence)
        {
            return new BmsTimelineEvent(position, 2, BmsTimelineEventKind.Stop, stop, sequence);
        }

        private sealed class BmsTimelineEventComparer : IComparer<BmsTimelineEvent>
        {
            public int Compare(BmsTimelineEvent x, BmsTimelineEvent y)
            {
                int position = x.Position.CompareTo(y.Position);
                if (position != 0)
                {
                    return position;
                }
                int priority = x.Priority.CompareTo(y.Priority);
                return priority != 0 ? priority : x.Sequence.CompareTo(y.Sequence);
            }
        }
    }

    private sealed class ChartMode
    {
        public static readonly ChartMode Beat5 = new(5, 6, [5], [0, 1, 2, 3, 4, 5, -1, -1, -1, 6, 7, 8, 9, 10, 11, -1, -1, -1], null);

        public static readonly ChartMode Beat7 = new(7, 8, [7], [0, 1, 2, 3, 4, 7, -1, 5, 6, 8, 9, 10, 11, 12, 15, -1, 13, 14], null);

        public static readonly ChartMode Beat10 = new(10, 12, [5, 11], [0, 1, 2, 3, 4, 5, -1, -1, -1, 6, 7, 8, 9, 10, 11, -1, -1, -1], null);

        public static readonly ChartMode Beat14 = new(14, 16, [7, 15], [0, 1, 2, 3, 4, 7, -1, 5, 6, 8, 9, 10, 11, 12, 15, -1, 13, 14], null);

        public static readonly ChartMode Popn9 = new(9, 9, [], [0, 1, 2, 3, 4, -1, -1, -1, -1, -1, 5, 6, 7, 8, -1, -1, -1, -1], null);

        public static readonly ChartMode Keyboard24 = new(25, 26, [24, 25], null, null);

        public static readonly ChartMode Keyboard24Double = new(50, 52, [24, 25, 50, 51], null, null);

        private readonly int[] scratchKeys;

        private readonly bool[] scratchKeyMap;

        private readonly int[] bmsChannelAssign;

        private readonly int[] bmsonKeyAssign;

        private ChartMode(int displayMode, int keyCount, int[] scratchKeys, int[] bmsChannelAssign, int[] bmsonKeyAssign)
        {
            DisplayMode = displayMode;
            KeyCount = keyCount;
            this.scratchKeys = scratchKeys ?? [];
            scratchKeyMap = new bool[Math.Max(0, keyCount)];
            foreach (int scratchKey in this.scratchKeys)
            {
                if (scratchKey >= 0 && scratchKey < scratchKeyMap.Length)
                {
                    scratchKeyMap[scratchKey] = true;
                }
            }
            this.bmsChannelAssign = bmsChannelAssign;
            this.bmsonKeyAssign = bmsonKeyAssign;
        }

        public int DisplayMode { get; }

        public int KeyCount { get; }

        public static ChartMode FromBmsonHint(string modeHint)
        {
            return (modeHint ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "beat-5k" => Beat5,
                "beat-7k" => Beat7,
                "beat-10k" => Beat10,
                "beat-14k" => Beat14,
                "popn-5k" or "popn-9k" => Popn9,
                "keyboard-24k" => Keyboard24,
                "keyboard-24k-double" => Keyboard24Double,
                _ => null,
            };
        }

        public bool IsScratchKey(int lane)
        {
            return lane >= 0 && lane < scratchKeyMap.Length && scratchKeyMap[lane];
        }

        public int[] GetBmsChannelAssign()
        {
            return bmsChannelAssign ?? [.. Enumerable.Range(0, KeyCount).Concat(Enumerable.Repeat(-1, Math.Max(0, 18 - KeyCount))).Take(18)];
        }

        public int[] GetBmsonKeyAssign()
        {
            if (bmsonKeyAssign != null)
            {
                return bmsonKeyAssign;
            }
            if (ReferenceEquals(this, Beat5))
            {
                return [0, 1, 2, 3, 4, -1, -1, 5];
            }
            if (ReferenceEquals(this, Beat10))
            {
                return [0, 1, 2, 3, 4, -1, -1, 5, 6, 7, 8, 9, 10, -1, -1, 11];
            }
            return [.. Enumerable.Range(0, KeyCount)];
        }
    }

    private sealed partial class ChartModel(string path, ChartInfoParser.ChartMode mode)
    {
        private List<ChartTimeline> timelines = [];

        public string Path { get; } = path;

        public ChartMode Mode { get; } = mode;

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

        public int? ExLevel { get; set; }

        public bool HasRandom { get; set; }

        public int DisplayMode => Mode.DisplayMode;

        public IReadOnlyList<ChartTimeline> Timelines => timelines;

        public ChartSummary Summary { get; private set; }

        public bool HasBga => Summary.HasBga;

        public void SetTimelines(List<ChartTimeline> value, ParseTimeoutGuard timeoutGuard)
        {
            timelines = [.. value.OrderBy(timeline => timeline.TimeMicroseconds)];
            Summary = ChartSummary.Create(this, timeoutGuard);
        }

        public int GetTotalNotes() => Summary.TotalNotes;

        public double GetMinBpm() => Summary.MinBpm;

        public double GetMaxBpm() => Summary.MaxBpm;

        public int GetLastTimeMilliseconds() => ToJavaInt(Summary.LastTimeMilliseconds);

        public bool TryGetJavaIntTimeWrap(out long rawTimeMilliseconds, out int wrappedTimeMilliseconds, out double section)
        {
            rawTimeMilliseconds = Summary.WrappedRawTime;
            wrappedTimeMilliseconds = ToJavaInt(rawTimeMilliseconds);
            section = Summary.WrappedSection;
            return Summary.HasTimeWrap;
        }

        public int GetFeatureFlags() => Summary.Features;
    }

    private sealed class ChartTimeline(double section, double preciseTimeMicroseconds, int laneCount)
    {
        public double Section { get; } = section;

        public double PreciseTimeMicroseconds { get; } = preciseTimeMicroseconds;

        public long TimeMicroseconds { get; } = ToCheckedMicroseconds(preciseTimeMicroseconds, "BMS timeline time is out of range.");

        public long TimeMillisecondsLong => TimeMicroseconds / 1000L;

        public int TimeMilliseconds => ToJavaInt(TimeMillisecondsLong);

        public double Bpm { get; set; }

        public long StopMicroseconds { get; set; }

        public int StopMilliseconds => (int)(StopMicroseconds / 1000L);

        public double Scroll { get; set; } = 1.0;

        public bool HasSectionLine { get; set; }

        public bool HasHiddenNote { get; set; }

        public bool HasBackground { get; set; }

        public bool HasBga { get; set; }

        public ChartNote[] Notes { get; } = new ChartNote[laneCount];

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
            var result = new ChartStatistics();
            int laneCount = model.Mode.KeyCount;
            int[][] laneNotes = new int[laneCount][];
            for (int lane = 0; lane < laneCount; lane++)
            {
                laneNotes[lane] = new int[3];
            }
            result.TotalNotes = model.Summary.TotalNotes;
            result.NormalKeyNotes = model.Summary.NormalKeyNotes;
            result.LongKeyNotes = model.Summary.LongKeyNotes;
            result.NormalScratchNotes = model.Summary.NormalScratchNotes;
            result.LongScratchNotes = model.Summary.LongScratchNotes;

            DistributionBuckets distribution = BuildDistribution(model, laneNotes, result.TotalNotes, out int borderPosition, timeoutGuard);
            result.Distribution = EncodeDistribution(distribution, timeoutGuard);
            result.LaneNotes = EncodeLaneNotes(laneNotes);
            CalculateDensity(distribution, borderPosition, result, timeoutGuard);
            CalculateSpeed(model, result, timeoutGuard);
            return result;
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
                        data.AddLongRange(second, endSecond, scratch ? 1 : 4);
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
            data.CompleteLongRanges(timeoutGuard);
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
            var speedText = new StringBuilder();
            double lastSpeedTime = 0.0;
            Dictionary<double, int> bpmNoteCounts = [];
            List<double> bpmInsertionOrder = [];
            double currentSpeed = model.InitialBpm;
            AppendSpeed(speedText, currentSpeed, 0.0);
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
                bpmNoteCounts[timeline.Bpm] = noteCount + model.Summary.TimelineNoteCounts[timelineIndex - 1];
                if (timeline.StopMilliseconds > 0)
                {
                    if (Math.Abs(currentSpeed) > double.Epsilon)
                    {
                        currentSpeed = 0.0;
                        lastSpeedTime = timeline.TimeMilliseconds;
                        AppendSpeed(speedText, currentSpeed, lastSpeedTime);
                        speedChangeCount++;
                    }
                }
                else
                {
                    double timelineSpeed = timeline.Bpm * timeline.Scroll;
                    if (Math.Abs(currentSpeed - timelineSpeed) > double.Epsilon)
                    {
                        currentSpeed = timelineSpeed;
                        lastSpeedTime = timeline.TimeMilliseconds;
                        AppendSpeed(speedText, currentSpeed, lastSpeedTime);
                        speedChangeCount++;
                    }
                }
            }
            if (model.Timelines.Count > 0 && Math.Abs(lastSpeedTime - model.Timelines[model.Timelines.Count - 1].TimeMilliseconds) > double.Epsilon)
            {
                AppendSpeed(speedText, currentSpeed, model.Timelines[model.Timelines.Count - 1].TimeMilliseconds);
            }
            result.MainBpm = SelectMainBpmInJavaHashMapOrder(bpmNoteCounts, bpmInsertionOrder);
            result.SpeedChange = speedText.ToString();
            result.SpeedChangeCount = speedChangeCount;
        }

        private static void AppendSpeed(StringBuilder text, double speed, double time)
        {
            if (text.Length > 0)
            {
                text.Append(',');
            }
            text.Append(FormatDouble(speed)).Append(',').Append(FormatDouble(time));
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
                .Select((bpm, index) => new { Bpm = bpm, Index = index, Bucket = JavaHashMapBucket(bpm, capacity) })
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
            var builder = new StringBuilder(values.BucketCount * 14 + 1);
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
            var text = new StringBuilder();
            foreach (int[] lane in values)
            {
                foreach (int value in lane)
                {
                    if (text.Length > 0)
                    {
                        text.Append(',');
                    }
                    text.Append(value.ToString(CultureInfo.InvariantCulture));
                }
            }
            return text.ToString();
        }
    }

    private sealed class DistributionBuckets
    {
        private const int ColumnCount = 7;

        private readonly int[] values;

        private int[] scratchLongRanges;

        private int[] keyLongRanges;

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

        public void AddLongRange(int first, int last, int column)
        {
            if (first > last)
            {
                return;
            }
            // 元の逐次加算と同じ配列境界で失敗させ、区間を救済しません。
            _ = values[GetIndex(first, column)];
            _ = values[GetIndex(last, column)];
            int[] ranges = column == 1
                ? scratchLongRanges ??= new int[BucketCount + 1]
                : keyLongRanges ??= new int[BucketCount + 1];
            ranges[first]++;
            ranges[last + 1]--;
        }

        public void CompleteLongRanges(ParseTimeoutGuard timeoutGuard)
        {
            int scratch = 0;
            int key = 0;
            for (int second = 0; second < BucketCount; second++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(second + 1, "distribution_long_ranges");
                if (scratchLongRanges != null)
                {
                    scratch += scratchLongRanges[second];
                    values[GetIndex(second, 1)] += scratch;
                }
                if (keyLongRanges != null)
                {
                    key += keyLongRanges[second];
                    values[GetIndex(second, 4)] += key;
                }
            }
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
