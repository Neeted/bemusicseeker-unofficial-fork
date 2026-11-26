using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>一つの名前の判定の結果です。</summary>
    /// <param name="Equivalent">すべての WAV と OGG の組が同等か。</param>
    /// <param name="UnreadableIndexes">読取りの入出力に失敗したファイルの、入力の中の位置です。</param>
    internal sealed record Result(bool Equivalent, IReadOnlyList<int> UnreadableIndexes);

    /// <summary>
    /// 一つの名前の全 WAV と全 OGG を一度ずつ読み、すべての組が同等かを返します。読めなかったファイルがあれば同等にしません。
    /// 復号の結果はこの呼出しの中だけで持ちます。復号できない音源は理由を増やさず同等でないものとし、読取りの入出力の失敗は記録して同等でないものとします。
    /// </summary>
    /// <param name="files">名前に属するファイルのパスと、WAV かどうか（偽なら OGG）。</param>
    internal static Result Judge(IReadOnlyList<(string FullPath, bool IsWav)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var wavs = new List<DuplicateMergeAudioSamples>();
        var oggs = new List<DuplicateMergeAudioSamples>();
        var unreadable = new List<int>();
        bool decodable = true;
        for (int index = 0; index < files.Count; index++)
        {
            (string fullPath, bool isWav) = files[index];
            try
            {
                DuplicateMergeAudioSamples samples = isWav
                    ? DuplicateMergeAudioReader.ReadWave(fullPath)
                    : DuplicateMergeAudioReader.ReadOgg(fullPath);
                if (samples == null) { decodable = false; }
                else { (isWav ? wavs : oggs).Add(samples); }
            }
            catch (Exception failure) when (DuplicateMergeFolderScan.IsInputOutputFailure(failure))
            {
                decodable = false;
                unreadable.Add(index);
            }
        }
        bool equivalent = decodable && wavs.All(wav => oggs.All(ogg => DuplicateMergeAudioEquivalence.Judge(wav, ogg)));
        return new Result(equivalent, unreadable);
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
