using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 同名のWAVとOGGの組の診断（<see cref="DuplicateMergeAudioPairDiagnostic"/>）を、STATUSのツールチップの内訳・音質の目安・名前ごとの理由と、
/// 検査結果の詳細のTSVへ書式化します。統合候補一覧と整理候補一覧が共用します。書式化は読取りのたびに現在の表示言語で行います。
/// 書式の正本は <c>devdocs/spec/library/duplicate-files.md</c> です。
/// </summary>
internal static class DuplicateMergeAudioDiagnosticPresentation
{
    /// <summary>検査結果の詳細のTSVの見出しです。表示言語によらない固定の英字で、列の順は書式の定義のとおりです。</summary>
    internal static readonly IReadOnlyList<string> TsvHeaders =
    [
        "FOLDER", "NAME", "RESULT", "REASON", "QUALITY_HINT",
        "WAV_PATH", "WAV_FORMAT", "WAV_BITS", "WAV_RATE", "WAV_CHANNELS", "WAV_FRAMES", "WAV_SECONDS", "WAV_BYTES",
        "OGG_PATH", "OGG_RATE", "OGG_CHANNELS", "OGG_FRAMES", "OGG_SECONDS", "OGG_BYTES", "OGG_KBPS",
        "FRAME_DIFF", "EXCESS_SILENT", "CORRELATION", "VOLUME_DB", "LOW_VOLUME_DB", "LOW_ERROR_1K", "LOW_ERROR_4K", "SEGMENT_MIN_DB"
    ];

    private static readonly DuplicateMergeAudioQualityHints[] AllHints =
    [
        DuplicateMergeAudioQualityHints.OggLowBitrate,
        DuplicateMergeAudioQualityHints.Wav8Bit,
        DuplicateMergeAudioQualityHints.WavLowSampleRate,
        DuplicateMergeAudioQualityHints.OggLowerSampleRate,
        DuplicateMergeAudioQualityHints.WavOnlyMono,
        DuplicateMergeAudioQualityHints.OggOnlyMono
    ];

    /// <summary>
    /// 同等と判定しなかった一つの名前の要約です。<paramref name="Reason"/> は、名前の理由（非同等の組の理由のうち順位が最も早いもの）で、
    /// 同等でない組がすべて読取り失敗なら <see cref="DuplicateMergeAudioPairReason.None"/> です（<paramref name="IsReadFailure"/> が真）。
    /// </summary>
    internal sealed record NameSummary(
        string Name, DuplicateMergeAudioPairReason Reason, bool IsReadFailure, DuplicateMergeAudioQualityHints Hints);

    /// <summary>
    /// 診断から、同等と判定しなかった名前（いずれかの組が同等でない名前）の要約を求めます。名前の比較は大文字小文字を無視します。
    /// 名前の音質の目安は、同等でない組（読取り失敗を除く）のいずれかで成立した目安の和集合です。
    /// </summary>
    internal static IReadOnlyDictionary<string, NameSummary> SummarizeNonEquivalentNames(IEnumerable<DuplicateMergeAudioPairDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var result = new Dictionary<string, NameSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, DuplicateMergeAudioPairDiagnostic> name in diagnostics.GroupBy(pair => pair.Name, StringComparer.OrdinalIgnoreCase))
        {
            DuplicateMergeAudioPairMeasurement[] nonEquivalent =
                [.. name.Select(pair => pair.Measurement).Where(measurement => measurement.Result != DuplicateMergeAudioPairResult.Equivalent)];
            if (nonEquivalent.Length == 0) { continue; }
            DuplicateMergeAudioPairMeasurement[] withReason =
                [.. nonEquivalent.Where(measurement => measurement.Result == DuplicateMergeAudioPairResult.NonEquivalent)];
            DuplicateMergeAudioQualityHints hints = DuplicateMergeAudioQualityHints.None;
            foreach (DuplicateMergeAudioPairMeasurement measurement in withReason) { hints |= measurement.Hints; }
            result[name.Key] = new NameSummary(
                name.Key,
                withReason.Length == 0 ? DuplicateMergeAudioPairReason.None : withReason.Min(measurement => measurement.Reason),
                withReason.Length == 0,
                hints);
        }
        return result;
    }

    /// <summary>
    /// 「内訳：&lt;理由&gt; &lt;件数&gt;／…」と「音質の目安：&lt;目安&gt; &lt;件数&gt;／…」の行です。件数は同等と判定しなかった名前の数で、内訳は理由の順位の順（読取り失敗は最後）、
    /// 音質の目安は目安の並びの順です。0件の項目は出さず、該当がなければ行を出しません。
    /// </summary>
    /// <param name="names">同等と判定しなかった名前の要約。</param>
    internal static IReadOnlyList<string> FormatSummaryLines(IReadOnlyCollection<NameSummary> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var lines = new List<string>(2);
        if (names.Count == 0) { return lines; }
        string separator = Resources.Duplicate_merge_candidate_audio_list_separator;
        string[] breakdown =
        [
            .. names
                .GroupBy(name => name.IsReadFailure ? int.MaxValue : (int)name.Reason)
                .OrderBy(group => group.Key)
                .Select(group => string.Format(
                    Resources.Duplicate_merge_candidate_audio_count_item_format,
                    group.First().IsReadFailure ? ReadFailureName : ReasonName(group.First().Reason),
                    group.Count()))
        ];
        lines.Add(string.Format(Resources.Duplicate_merge_candidate_audio_breakdown_format, string.Join(separator, breakdown)));
        string[] hints =
        [
            .. AllHints
                .Select(hint => (Hint: hint, Count: names.Count(name => name.Hints.HasFlag(hint))))
                .Where(item => item.Count > 0)
                .Select(item => string.Format(Resources.Duplicate_merge_candidate_audio_count_item_format, HintName(item.Hint), item.Count))
        ];
        if (hints.Length > 0)
        {
            lines.Add(string.Format(Resources.Duplicate_merge_candidate_audio_hint_format, string.Join(separator, hints)));
        }
        return lines;
    }

    /// <summary>ツールチップの名前の行の本文「&lt;名前&gt;：&lt;理由&gt;」です。</summary>
    internal static string FormatNameWithReason(NameSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return string.Format(
            Resources.Duplicate_merge_candidate_audio_name_reason_format,
            summary.Name,
            summary.IsReadFailure ? ReadFailureName : ReasonName(summary.Reason));
    }

    /// <summary>「読取り失敗」の文言です。結果名と、名前の理由名で共通です。</summary>
    internal static string ReadFailureName => Resources.Duplicate_merge_candidate_audio_result_read_failure;

    /// <summary>組の結果名です。</summary>
    internal static string ResultName(DuplicateMergeAudioPairResult result) => result switch
    {
        DuplicateMergeAudioPairResult.Equivalent => Resources.Duplicate_merge_candidate_audio_result_equivalent,
        DuplicateMergeAudioPairResult.NonEquivalent => Resources.Duplicate_merge_candidate_audio_result_non_equivalent,
        DuplicateMergeAudioPairResult.ReadFailure => ReadFailureName,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Unknown audio pair result.")
    };

    /// <summary>同等でない理由の名前です。<see cref="DuplicateMergeAudioPairReason.None"/> は空です。</summary>
    internal static string ReasonName(DuplicateMergeAudioPairReason reason) => reason switch
    {
        DuplicateMergeAudioPairReason.None => string.Empty,
        DuplicateMergeAudioPairReason.WavUndecodable => Resources.Duplicate_merge_candidate_audio_reason_wav_undecodable,
        DuplicateMergeAudioPairReason.OggUndecodable => Resources.Duplicate_merge_candidate_audio_reason_ogg_undecodable,
        DuplicateMergeAudioPairReason.ChannelCountMismatch => Resources.Duplicate_merge_candidate_audio_reason_channel_mismatch,
        DuplicateMergeAudioPairReason.MultiChannel => Resources.Duplicate_merge_candidate_audio_reason_multi_channel,
        DuplicateMergeAudioPairReason.UnsupportedSampleRates => Resources.Duplicate_merge_candidate_audio_reason_unsupported_rates,
        DuplicateMergeAudioPairReason.LengthMismatch => Resources.Duplicate_merge_candidate_audio_reason_length_mismatch,
        DuplicateMergeAudioPairReason.LengthExcessAudible => Resources.Duplicate_merge_candidate_audio_reason_length_excess_audible,
        DuplicateMergeAudioPairReason.NonFinite => Resources.Duplicate_merge_candidate_audio_reason_non_finite,
        DuplicateMergeAudioPairReason.OneSideSilent => Resources.Duplicate_merge_candidate_audio_reason_one_side_silent,
        DuplicateMergeAudioPairReason.LowBandWaveform => Resources.Duplicate_merge_candidate_audio_reason_low_band_waveform,
        DuplicateMergeAudioPairReason.Correlation => Resources.Duplicate_merge_candidate_audio_reason_correlation,
        DuplicateMergeAudioPairReason.Volume => Resources.Duplicate_merge_candidate_audio_reason_volume,
        DuplicateMergeAudioPairReason.SegmentVolume => Resources.Duplicate_merge_candidate_audio_reason_segment_volume,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown audio pair reason.")
    };

    /// <summary>音質の目安一つの名前です。</summary>
    internal static string HintName(DuplicateMergeAudioQualityHints hint) => hint switch
    {
        DuplicateMergeAudioQualityHints.OggLowBitrate => Resources.Duplicate_merge_candidate_audio_hint_ogg_low_bitrate,
        DuplicateMergeAudioQualityHints.Wav8Bit => Resources.Duplicate_merge_candidate_audio_hint_wav_8bit,
        DuplicateMergeAudioQualityHints.WavLowSampleRate => Resources.Duplicate_merge_candidate_audio_hint_wav_low_rate,
        DuplicateMergeAudioQualityHints.OggLowerSampleRate => Resources.Duplicate_merge_candidate_audio_hint_ogg_lower_rate,
        DuplicateMergeAudioQualityHints.WavOnlyMono => Resources.Duplicate_merge_candidate_audio_hint_wav_only_mono,
        DuplicateMergeAudioQualityHints.OggOnlyMono => Resources.Duplicate_merge_candidate_audio_hint_ogg_only_mono,
        _ => throw new ArgumentOutOfRangeException(nameof(hint), hint, "Unknown audio quality hint.")
    };

    /// <summary>成立した目安の名前を、目安の並びの順に「, 」でつないだ文字列です。成立がなければ空です。</summary>
    internal static string FormatHints(DuplicateMergeAudioQualityHints hints)
        => string.Join(DuplicateMergeVerificationPresentation.ReasonSeparator, AllHints.Where(hint => hints.HasFlag(hint)).Select(HintName));

    /// <summary>
    /// 検査結果の詳細のTSVを作ります。見出しの一行と、組ごとの一行（列はTAB、行はCRLF区切りで、末尾の行に改行を付けません）です。
    /// 行の順は、<paramref name="targets"/> の順のフォルダ（グループ）、その中で衝突名の昇順（大文字小文字を無視）、WAVのフルパス、OGGのフルパスの昇順（大文字小文字を無視）です。
    /// 同等の組・読取り失敗の組も含めて全量を出します。セル内の制御文字は空白1個へ置き換えます。
    /// </summary>
    /// <param name="targets">表示順の対象。フォルダ列に出す値と、そのフォルダ（グループ）の診断です。診断が <c>null</c> のものは行を出しません。</param>
    /// <returns>TSV。組の診断が一件もなければ <c>null</c>（クリップボードを変更しない合図です）。</returns>
    internal static string BuildTsv(IEnumerable<(string Folder, IReadOnlyList<DuplicateMergeAudioPairDiagnostic> Diagnostics)> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var builder = new StringBuilder();
        AppendRow(builder, TsvHeaders);
        bool any = false;
        foreach ((string folder, IReadOnlyList<DuplicateMergeAudioPairDiagnostic> diagnostics) in targets)
        {
            if (diagnostics == null) { continue; }
            foreach (DuplicateMergeAudioPairDiagnostic pair in diagnostics
                .OrderBy(pair => pair.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pair => pair.WavPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pair => pair.OggPath, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("\r\n");
                AppendRow(builder, BuildRow(folder, pair));
                any = true;
            }
        }
        return any ? builder.ToString() : null;
    }

    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> cells)
    {
        for (int index = 0; index < cells.Count; index++)
        {
            if (index > 0) { builder.Append('\t'); }
            builder.Append(CustomTableDataTransfer.NormalizeCellText(cells[index]));
        }
    }

    private static string[] BuildRow(string folder, DuplicateMergeAudioPairDiagnostic pair)
    {
        DuplicateMergeAudioPairMeasurement measurement = pair.Measurement;
        DuplicateMergeWavInfo wav = measurement.Wav;
        DuplicateMergeOggInfo ogg = measurement.Ogg;
        DuplicateMergeAudioComparison? comparison = measurement.Comparison;
        return
        [
            folder,
            pair.Name,
            ResultName(measurement.Result),
            measurement.Result == DuplicateMergeAudioPairResult.NonEquivalent ? ReasonName(measurement.Reason) : string.Empty,
            FormatHints(measurement.Hints),
            pair.WavPath,
            FormatWavFormat(wav.FormatTag),
            Integer(wav.BitsPerSample),
            Integer(wav.SampleRate),
            Integer(wav.Channels),
            Integer(wav.Frames),
            Number(wav.Seconds, 6),
            Integer(wav.FileBytes),
            pair.OggPath,
            Integer(ogg.SampleRate),
            Integer(ogg.Channels),
            Integer(ogg.Frames),
            Number(ogg.Seconds, 6),
            Integer(ogg.FileBytes),
            Number(ogg.AverageKbps, 3),
            Integer(measurement.FrameDifference),
            measurement.ExcessSilent is { } silent ? (silent ? "TRUE" : "FALSE") : string.Empty,
            Number(comparison?.Correlation, 4),
            Number(comparison?.VolumeDb, 3),
            Number(comparison?.LowVolumeDb, 3),
            Number(comparison?.LowErrorRatio1k, 4),
            Number(comparison?.LowErrorRatio4k, 4),
            Number(comparison?.SegmentMinDb, 3)
        ];
    }

    /// <summary>WAVの形式です。<c>PCM</c>・<c>FLOAT</c>、それ以外は <c>0x</c> に続く4桁の大文字16進の形式タグです。</summary>
    private static string FormatWavFormat(int? tag) => tag switch
    {
        null => string.Empty,
        1 => "PCM",
        3 => "FLOAT",
        { } other => "0x" + other.ToString("X4", CultureInfo.InvariantCulture)
    };

    private static string Integer(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Integer(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>不変カルチャの固定小数です。正・負の無限大は <c>Infinity</c>・<c>-Infinity</c> で、未測定は空です。</summary>
    private static string Number(double? value, int decimals)
    {
        if (value is not { } number) { return string.Empty; }
        if (double.IsPositiveInfinity(number)) { return "Infinity"; }
        if (double.IsNegativeInfinity(number)) { return "-Infinity"; }
        if (double.IsNaN(number)) { return "NaN"; }
        return number.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
