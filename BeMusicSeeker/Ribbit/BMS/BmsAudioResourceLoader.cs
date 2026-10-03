#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace Ribbit.BMS;

/// <summary>一つの音源参照へ対応する、全候補失敗後の代表入力失敗です。</summary>
internal sealed record BmsAudioLoadFailure(int Index, string ResourceName, string Path, string NormalizedPath, Exception Exception);

/// <summary>譜面から省略した一意の音声pathです。</summary>
internal sealed record BmsAudioSourceOmission(string ResourceName, string Path);

/// <summary>全候補の処理完了後に公開する共有音源と最終入力失敗です。</summary>
internal sealed class BmsAudioResourceLoadResult
{
    /// <summary>成功したindex別resourceと、未解決参照だけのpath集約失敗をまとめます。</summary>
    /// <param name="resourcesByIndex">各音源indexが参照する、同pathで共有した成功resourceです。</param>
    /// <param name="failures">全候補失敗後に正規化pathで集約した代表失敗です。</param>
    internal BmsAudioResourceLoadResult(BmsAudioResource?[] resourcesByIndex, IReadOnlyList<BmsAudioLoadFailure> failures)
    {
        ResourcesByIndex = resourcesByIndex;
        Failures = failures;
    }

    /// <summary>各音源indexから参照するpath共有resourceです。0frameも成功として保持します。</summary>
    internal BmsAudioResource?[] ResourcesByIndex { get; }

    /// <summary>全候補に失敗した参照の、正規化path単位の代表失敗です。</summary>
    internal IReadOnlyList<BmsAudioLoadFailure> Failures { get; }
}

/// <summary>BMSとbmsonの候補を順に復号し、曲内の同一絶対pathを一回だけ読み込みます。</summary>
internal static class BmsAudioResourceLoader
{
    /// <summary>BMSで実際に使用する非空音源参照を共通pipelineへ渡します。</summary>
    /// <param name="bms">使用音源indexと定義を持つBMS譜面です。</param>
    /// <param name="basePath">譜面の配置フォルダです。</param>
    /// <param name="sourceGain">共有resourceのgainです。</param>
    /// <param name="cancellationToken">候補処理の前後と結果公開前の取消です。</param>
    /// <param name="expectedSession">先読み開始時のsessionです。未指定なら現在sessionを捕捉します。</param>
    internal static BmsAudioResourceLoadResult Load(BMSFile bms, string basePath, float sourceGain,
        CancellationToken cancellationToken = default, BassAudioSession? expectedSession = null)
        => Load(PlaybackChart.FromBms(bms), basePath, sourceGain, cancellationToken, expectedSession);

    /// <summary>未解決参照の候補を段階ごとに並列処理し、最初の復号成功を採用します。</summary>
    /// <param name="bms">使用する音源参照と定義を持つ譜面です。</param>
    /// <param name="basePath">譜面の配置フォルダです。</param>
    /// <param name="sourceGain">共有resourceのgainです。</param>
    /// <param name="cancellationToken">read/decodeの前後と結果公開前の取消です。native呼出しは強制終了しません。</param>
    /// <param name="expectedSession">先読み開始時のsessionです。未指定なら現在sessionを捕捉します。</param>
    /// <param name="readInput">呼出し単位の入力read境界です。未指定なら実ファイルを読み込みます。</param>
    /// <param name="decode">呼出し単位の復号境界です。未指定なら実decoderを使います。</param>
    internal static BmsAudioResourceLoadResult Load(PlaybackChart bms, string basePath, float sourceGain,
        CancellationToken cancellationToken = default, BassAudioSession? expectedSession = null,
        Func<string, AudioInputFile>? readInput = null, Func<AudioInputFile, BassAudioSession, DecodedAudio>? decode = null)
    {
        ArgumentNullException.ThrowIfNull(bms);
        ArgumentNullException.ThrowIfNull(basePath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            AudioReference[] references = bms.AudioEvents.Select(item => item.ResourceIndex).Distinct().Order()
                .Where(index => !string.IsNullOrWhiteSpace(bms.ResourceNames[index]))
                .Select(index => new AudioReference(index, bms.ResourceNames[index], GetCandidates(basePath, bms.ResourceNames[index])))
                .ToArray();
            var resources = new BmsAudioResource?[bms.ResourceNames.Count];
            // 並列workerは辞書へ書かず、段階の完了結果をこの呼出しだけが追加します。
            var completed = new Dictionary<string, PathResult>(StringComparer.OrdinalIgnoreCase);
            int stages = references.Length == 0 ? 0 : references.Max(item => item.Candidates.Length);
            for (int stage = 0; stage < stages; stage++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (AudioReference Reference, Candidate Candidate)[] candidates = references.Where(item => resources[item.Index] == null && stage < item.Candidates.Length)
                    .Select(item => (Reference: item, Candidate: NormalizeCandidate(item.Candidates[stage])))
                    .ToArray();
                Candidate[] work = candidates.Select(item => item.Candidate)
                    .Where(item => !completed.ContainsKey(item.Path))
                    .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                if (work.Length > 0)
                {
                    expectedSession ??= CaptureActiveSession();
                    BassAudioSession session = expectedSession;
                    PathResult[] results = Partitioner.Create(work, EnumerablePartitionerOptions.NoBuffering)
                        .AsParallel().WithCancellation(cancellationToken)
                        .WithDegreeOfParallelism(Environment.ProcessorCount)
                        .Select(item => ReadAndDecode(item, session, sourceGain, cancellationToken, readInput, decode)).ToArray();
                    foreach (PathResult result in results) completed.Add(result.Path, result);
                }
                foreach ((AudioReference reference, Candidate candidate) in candidates)
                {
                    PathResult result = completed[candidate.Path];
                    if (result.Resource is BmsAudioResource resource) resources[reference.Index] = resource;
                    else if (result.Failure is AudioSourceLoadException failure)
                    {
                        // 後続欠落で実際の復号・アクセス失敗を上書きしません。全欠落なら最初の要求を残します。
                        if (reference.Failure == null || (IsMissing(reference.Failure.Exception) && !IsMissing(failure)))
                            reference.Failure = new BmsAudioLoadFailure(reference.Index, reference.Name,
                                candidate.Path, candidate.Path, failure);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            BmsAudioLoadFailure[] failures = references.Where(item => resources[item.Index] == null)
                .Select(item => item.Failure ?? throw new InvalidOperationException("An unresolved audio reference has no failure."))
                .GroupBy(item => item.NormalizedPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.MinBy(item => item.Index) ?? throw new InvalidOperationException("An audio failure group is empty."))
                .OrderBy(item => item.Index).ToArray();
            return new BmsAudioResourceLoadResult(resources, failures);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AudioSourceFatalException) { throw; }
        catch (Exception exception)
        {
            throw new AudioSourceFatalException("Audio loading failed outside a classified input read or decode failure.", exception);
        }
    }

    /// <summary>各復号で照合する現在の音声sessionを、操作権を長期占有せず捕捉します。</summary>
    internal static BassAudioSession CaptureActiveSession()
    {
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        BassAudioSession? session = BassAudioPlayer.CurrentSessionForAdmittedOperation;
        return session?.State == BassAudioSessionState.Active ? session
            : throw new InvalidOperationException("An active audio session is required to load audio.");
    }

    private static string[] GetCandidates(string basePath, string name)
    {
        IEnumerable<string> names = Resources.NormalizeExtension(name);
        string basename = Path.GetFileName(name);
        if (!string.Equals(name, basename, StringComparison.Ordinal)) names = names.Concat(Resources.NormalizeExtension(basename));
        return names.Select(item => Path.Combine(basePath, item)).ToArray();
    }

    private static Candidate NormalizeCandidate(string path)
    {
        try { return new Candidate(Path.GetFullPath(path), null); }
        catch (Exception cause) when (cause is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new Candidate(path, new AudioSourceLoadException(AudioSourceLoadStage.InspectContainer, path,
                "The audio input path could not be resolved.", cause));
        }
    }

    private static PathResult ReadAndDecode(Candidate candidate, BassAudioSession session, float gain,
        CancellationToken cancellationToken, Func<string, AudioInputFile>? readInput,
        Func<AudioInputFile, BassAudioSession, DecodedAudio>? decode)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Failure is AudioSourceLoadException pathFailure) throw pathFailure;
            AudioInputFile input;
            try { input = readInput == null ? AudioInputFile.Read(candidate.Path) : readInput(candidate.Path); }
            catch (Exception cause) when (cause is not AudioSourceLoadException
                && (cause is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException))
            {
                throw new AudioSourceLoadException(AudioSourceLoadStage.InspectContainer, candidate.Path,
                    "The audio input could not be read.", cause);
            }
            using (input)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DecodedAudio audio = decode == null ? AudioSourceLoader.Decode(input, session) : decode(input, session);
                cancellationToken.ThrowIfCancellationRequested();
                return new PathResult(candidate.Path, new BmsAudioResource(candidate.Path, audio, gain), null);
            }
        }
        catch (AudioSourceLoadException failure) when (failure.IsInputFailure)
        {
            return new PathResult(candidate.Path, null, failure);
        }
    }

    private static bool IsMissing(Exception exception) => exception is FileNotFoundException or DirectoryNotFoundException
        || (exception.InnerException is Exception cause && IsMissing(cause));

    private sealed class AudioReference(int index, string name, string[] candidates)
    {
        internal int Index { get; } = index;
        internal string Name { get; } = name;
        internal string[] Candidates { get; } = candidates;
        internal BmsAudioLoadFailure? Failure { get; set; }
    }
    private readonly record struct Candidate(string Path, AudioSourceLoadException? Failure);
    private sealed record PathResult(string Path, BmsAudioResource? Resource, AudioSourceLoadException? Failure);
}
