using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.Models;

/// <summary>
/// 同じ名前の WAV と OGG の波形が同等かを調べる、統合の安全性確認と重複音源の確認が共用する部品です。
/// 判定そのものは <see cref="DuplicateMergeAudioEquivalence"/> が行い、ここは読取りと、復号できない音源・入出力の失敗の扱いと、名前ごとの並列実行を定めます。
/// </summary>
internal static class DuplicateMergeAudioNameJudge
{
    /// <summary>名前の中の一組（WAV一つとOGG一つ）の判定の結果です。</summary>
    /// <param name="WavIndex">WAVの、入力の中の位置。</param>
    /// <param name="OggIndex">OGGの、入力の中の位置。</param>
    /// <param name="Measurement">組の結果・理由・測定値。</param>
    internal sealed record PairResult(int WavIndex, int OggIndex, DuplicateMergeAudioPairMeasurement Measurement);

    /// <summary>一つの名前の判定の結果です。</summary>
    /// <param name="Equivalent">すべての WAV と OGG の組が同等か。</param>
    /// <param name="UnreadableIndexes">読取りの入出力に失敗したファイルの、入力の中の位置です。</param>
    /// <param name="Pairs">全 WAV と全 OGG のすべての組の結果です。WAV の位置、OGG の位置の昇順です。</param>
    internal sealed record Result(bool Equivalent, IReadOnlyList<int> UnreadableIndexes, IReadOnlyList<PairResult> Pairs);

    /// <summary>一つのファイルの読取りの結果です。</summary>
    private readonly record struct FileRead(DuplicateMergeAudioSamples Samples, DuplicateMergeWavHeader Header, bool Failed);

    /// <summary>
    /// 一つの名前の全 WAV と全 OGG を一度ずつ読み、すべての組が同等かを返します。読めなかったファイルがあれば同等にしません。
    /// 復号の結果はこの呼出しの中だけで持ちます。復号できない音源は同等でないものとし、読取りの入出力の失敗は記録して同等でないものとします。
    /// 同等でない組が見つかった後も全組を判定し（復号はファイルごとに一回）、組ごとの結果・理由・測定値を返します。
    /// 復号できない音源の組の理由は復号不可で、段階の理由（解析不能など）は増やしません。測定値のファイルサイズは、呼出し側が走査で得た値を使い、追加の読取りをしません。
    /// </summary>
    /// <param name="files">名前に属するファイルのパス、WAV かどうか（偽なら OGG）、走査で得たファイルサイズ。</param>
    internal static Result Judge(IReadOnlyList<(string FullPath, bool IsWav, long Length)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var reads = new FileRead[files.Count];
        var unreadable = new List<int>();
        bool decodable = true;
        for (int index = 0; index < files.Count; index++)
        {
            (string fullPath, bool isWav, _) = files[index];
            try
            {
                DuplicateMergeAudioSamples samples;
                DuplicateMergeWavHeader header = default;
                if (isWav)
                {
                    DuplicateMergeWaveReadResult read = DuplicateMergeAudioReader.ReadWave(fullPath);
                    samples = read.Samples;
                    header = read.Header;
                }
                else
                {
                    samples = DuplicateMergeAudioReader.ReadOgg(fullPath);
                }
                if (samples == null) { decodable = false; }
                reads[index] = new FileRead(samples, header, Failed: false);
            }
            catch (Exception failure) when (DuplicateMergeFolderScan.IsInputOutputFailure(failure))
            {
                decodable = false;
                unreadable.Add(index);
                reads[index] = new FileRead(null, default, Failed: true);
            }
        }

        var pairs = new List<PairResult>();
        bool equivalent = decodable;
        for (int wavIndex = 0; wavIndex < files.Count; wavIndex++)
        {
            if (!files[wavIndex].IsWav) { continue; }
            for (int oggIndex = 0; oggIndex < files.Count; oggIndex++)
            {
                if (files[oggIndex].IsWav) { continue; }
                DuplicateMergeAudioPairMeasurement measurement = Measure(
                    reads[wavIndex], files[wavIndex].Length, reads[oggIndex], files[oggIndex].Length);
                pairs.Add(new PairResult(wavIndex, oggIndex, measurement));
                if (measurement.Result != DuplicateMergeAudioPairResult.Equivalent) { equivalent = false; }
            }
        }
        return new Result(equivalent, unreadable, pairs);
    }

    private static DuplicateMergeAudioPairMeasurement Measure(FileRead wav, long wavBytes, FileRead ogg, long oggBytes)
    {
        DuplicateMergeWavInfo wavInfo = wav.Failed
            ? new DuplicateMergeWavInfo(null, null, null, null, null, wavBytes)
            : new DuplicateMergeWavInfo(
                wav.Header.FormatTag, wav.Header.BitsPerSample, wav.Header.SampleRate, wav.Header.Channels, wav.Samples?.FrameCount, wavBytes);
        DuplicateMergeOggInfo oggInfo = ogg.Failed
            ? new DuplicateMergeOggInfo(null, null, null, oggBytes)
            : new DuplicateMergeOggInfo(ogg.Samples?.SampleRate, ogg.Samples?.Channels, ogg.Samples?.FrameCount, oggBytes);
        if (wav.Failed || ogg.Failed)
        {
            return new DuplicateMergeAudioPairMeasurement(
                DuplicateMergeAudioPairResult.ReadFailure, DuplicateMergeAudioPairReason.None, DuplicateMergeAudioQualityHints.None,
                wavInfo, oggInfo, null, null, null);
        }
        DuplicateMergeAudioQualityHints hints = DuplicateMergeAudioQuality.Evaluate(wavInfo, oggInfo);
        if (wav.Samples == null || ogg.Samples == null)
        {
            return new DuplicateMergeAudioPairMeasurement(
                DuplicateMergeAudioPairResult.NonEquivalent,
                wav.Samples == null ? DuplicateMergeAudioPairReason.WavUndecodable : DuplicateMergeAudioPairReason.OggUndecodable,
                hints, wavInfo, oggInfo, null, null, null);
        }
        DuplicateMergeAudioJudgement judgement = DuplicateMergeAudioEquivalence.Judge(wav.Samples, ogg.Samples);
        return new DuplicateMergeAudioPairMeasurement(
            judgement.Equivalent ? DuplicateMergeAudioPairResult.Equivalent : DuplicateMergeAudioPairResult.NonEquivalent,
            judgement.Reason, hints, wavInfo, oggInfo, judgement.FrameDifference, judgement.ExcessSilent, judgement.Comparison);
    }

    /// <summary>
    /// <paramref name="count"/> 件の名前を、論理プロセッサ数を上限に並列で処理します。並列処理の包みを外し、入出力以外の予期しない例外
    /// （音声ランタイムの致命的な失敗を含む）を元の型のまま呼出元へ伝えます。取消は <see cref="OperationCanceledException"/> です。
    /// </summary>
    internal static void RunParallel(int count, Action<int> body, CancellationToken cancellationToken = default)
    {
        try
        {
            Parallel.For(0, count,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
                body);
        }
        catch (AggregateException failure) when (failure.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(failure.InnerExceptions[0]).Throw();
            throw;
        }
    }
}
