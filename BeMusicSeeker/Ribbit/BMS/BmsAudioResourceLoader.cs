#nullable enable annotations
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;
using ManagedBass;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace Ribbit.BMS;

/// <summary>BMSで使う一つのWAV indexと解決した音声pathです。</summary>
internal readonly record struct BmsAudioPathRequest(int Index, string ResourceName, string Path);

/// <summary>一つのWAV indexへ対応する音声入力失敗です。</summary>
internal sealed record BmsAudioLoadFailure(
    int Index,
    string ResourceName,
    string Path,
    string NormalizedPath,
    Exception Exception);

/// <summary>BMS譜面から省略した一意の音声pathです。</summary>
internal sealed record BmsAudioSourceOmission(string ResourceName, string Path);

/// <summary>全音源の処理完了後に公開するBMS音源と入力失敗です。</summary>
internal sealed class BmsAudioResourceLoadResult
{
    /// <summary>各WAV indexのresourceとpath単位の入力失敗をロード結果にまとめます。</summary>
    /// <param name="resourcesByIndex">各WAV indexから参照する共有resource配列です。</param>
    /// <param name="failures">path単位に集約した入力失敗一覧です。</param>
    /// <param name="uniquePathCount">今回要求された一意path数です。</param>
    internal BmsAudioResourceLoadResult(
        BmsAudioResource?[] resourcesByIndex,
        IReadOnlyList<BmsAudioLoadFailure> failures,
        int uniquePathCount)
    {
        ResourcesByIndex = resourcesByIndex;
        Failures = failures;
        UniquePathCount = uniquePathCount;
    }

    /// <summary>各WAV indexから参照するpath共有resourceです。</summary>
    internal BmsAudioResource?[] ResourcesByIndex { get; }

    /// <summary>path単位で集約した入力失敗です。</summary>
    internal IReadOnlyList<BmsAudioLoadFailure> Failures { get; }

    /// <summary>今回のロードで要求した一意path数です。</summary>
    internal int UniquePathCount { get; }
}

/// <summary>BMSの音源pathを解決し、unique pathごとに一回で読み込み・復号します。</summary>
internal static class BmsAudioResourceLoader
{
    /// <summary>使用音源をpath単位で並列に読み込み、完了後にindex別resourceを返します。</summary>
    /// <param name="bms">使用音源indexを得る譜面です。</param>
    /// <param name="basePath">譜面ファイルの配置pathです。</param>
    /// <param name="sourceGain">各resourceへ捕捉するgainです。</param>
    internal static BmsAudioResourceLoadResult Load(BMSFile bms, string basePath, float sourceGain)
    {
        ArgumentNullException.ThrowIfNull(bms);
        ArgumentNullException.ThrowIfNull(basePath);

        try
        {
            return LoadCore(bms, basePath, sourceGain);
        }
        catch (AudioSourceFatalException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AudioSourceFatalException(
                "BMS audio loading failed outside a classified input read or decode failure.",
                exception);
        }
    }

    private static BmsAudioResourceLoadResult LoadCore(BMSFile bms, string basePath, float sourceGain)
    {
        int[] requiredIndices = BmsAudioFrameSchedule.GetRequiredAudioIndices(bms);
        BmsAudioPathRequest[] requests = requiredIndices
            .AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(Environment.ProcessorCount)
            .Select(index =>
            {
                string resourceName = bms.WavArray[index];
                string? path = FindAudioPath(basePath, resourceName);
                return path == null ? (BmsAudioPathRequest?)null : new BmsAudioPathRequest(index, resourceName, path);
            })
            .Where(request => request.HasValue)
            .Select(request => request.GetValueOrDefault())
            .ToArray();

        BmsAudioSourceWork[] groupedWork = GroupByNormalizedPath(requests, out BmsAudioLoadFailure[] pathFailures);
        var failures = new ConcurrentQueue<BmsAudioLoadFailure>(pathFailures);
        var resourcesByIndex = new BmsAudioResource?[bms.WavArray.Length];
        BmsAudioSourceWork[] readableWork = groupedWork
            .Where(work => work.PathNormalizationFailure == null)
            .ToArray();

        if (readableWork.Length > 0)
        {
            BassAudioSession expectedSession = CaptureActiveSession();
            try
            {
                Partitioner.Create(readableWork, EnumerablePartitionerOptions.NoBuffering)
                    .AsParallel()
                    .WithDegreeOfParallelism(Environment.ProcessorCount)
                    .ForAll(work => LoadResource(work, expectedSession, resourcesByIndex, failures, sourceGain));
            }
            catch (Exception exception)
            {
                throw new AudioSourceFatalException(
                    "BMS audio loading failed outside an input read or decode failure.",
                    exception);
            }
        }

        return new BmsAudioResourceLoadResult(
            resourcesByIndex,
            failures.OrderBy(failure => failure.Index).ToArray(),
            groupedWork.Length);
    }

    private static BassAudioSession CaptureActiveSession()
    {
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        BassAudioSession? session = BassAudioPlayer.CurrentSessionForAdmittedOperation;
        return session?.State == BassAudioSessionState.Active
            ? session
            : throw new InvalidOperationException("An active audio session is required to load BMS audio.");
    }

    private static BmsAudioSourceWork[] GroupByNormalizedPath(
        IReadOnlyList<BmsAudioPathRequest> requests,
        out BmsAudioLoadFailure[] failures)
    {
        var groupsByPath = new Dictionary<string, BmsAudioSourceWork>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<BmsAudioSourceWork>();
        foreach (BmsAudioPathRequest request in requests)
        {
            string pathKey;
            Exception? pathFailure = null;
            try
            {
                pathKey = Path.GetFullPath(request.Path);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                pathKey = request.Path;
                pathFailure = exception;
            }

            if (!groupsByPath.TryGetValue(pathKey, out BmsAudioSourceWork? group))
            {
                group = new BmsAudioSourceWork(pathKey);
                groupsByPath.Add(pathKey, group);
                groups.Add(group);
            }
            group.Add(request, pathFailure);
        }

        var pathFailures = new List<BmsAudioLoadFailure>();
        foreach (BmsAudioSourceWork group in groups)
        {
            group.SortRequests();
            BmsAudioPathRequest first = group.Requests[0];
            if (group.PathNormalizationFailure is Exception normalizationFailure)
            {
                pathFailures.Add(new BmsAudioLoadFailure(
                    first.Index,
                    first.ResourceName,
                    first.Path,
                    group.Path,
                    new AudioSourceLoadException(
                        AudioSourceLoadStage.InspectContainer,
                        first.Path,
                        "The audio input path could not be resolved.",
                        normalizationFailure)));
            }
        }

        failures = pathFailures.ToArray();
        return groups.ToArray();
    }

    private static void LoadResource(
        BmsAudioSourceWork work,
        BassAudioSession expectedSession,
        BmsAudioResource?[] resourcesByIndex,
        ConcurrentQueue<BmsAudioLoadFailure> failures,
        float sourceGain)
    {
        BmsAudioPathRequest first = work.Requests[0];
        try
        {
            using AudioInputFile input = ReadInput(work.Path);
            using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
            BassAudioSession? currentSession = BassAudioPlayer.CurrentSessionForAdmittedOperation;
            if (!ReferenceEquals(expectedSession, currentSession)
                || currentSession?.State != BassAudioSessionState.Active)
            {
                throw new InvalidOperationException("The audio session changed while BMS resources were loading.");
            }

            if (expectedSession.CoreDeviceIndex >= 0)
            {
                Bass.CurrentDevice = expectedSession.CoreDeviceIndex;
            }
            DecodedAudio decoded = AudioSourceLoader.Decode(input, expectedSession);
            var resource = new BmsAudioResource(work.Path, decoded, sourceGain);
            foreach (BmsAudioPathRequest request in work.Requests)
            {
                resourcesByIndex[request.Index] = resource;
            }
        }
        catch (Exception exception) when (exception is AudioSourceLoadException { IsInputFailure: true })
        {
            failures.Enqueue(new BmsAudioLoadFailure(
                first.Index,
                first.ResourceName,
                first.Path,
                work.Path,
                exception));
        }
    }

    private static AudioInputFile ReadInput(string path)
    {
        try
        {
            return AudioInputFile.Read(path);
        }
        catch (AudioSourceLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            throw new AudioSourceLoadException(
                AudioSourceLoadStage.InspectContainer,
                path,
                "The audio input could not be read.",
                exception);
        }
    }

    private static string? FindAudioPath(string basePath, string resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return null;
        }

        string? firstCandidate = null;
        foreach (string item in Resources.NormalizeExtension(resourceName))
        {
            string path = Path.Combine(basePath, item);
            firstCandidate ??= path;
            if (LongPathFileSystem.FileExists(path))
            {
                return path;
            }
        }

        string fileName = Path.GetFileName(resourceName);
        if (!string.Equals(resourceName, fileName, StringComparison.Ordinal))
        {
            foreach (string item in Resources.NormalizeExtension(fileName))
            {
                string path = Path.Combine(basePath, item);
                firstCandidate ??= path;
                if (LongPathFileSystem.FileExists(path))
                {
                    return path;
                }
            }
        }

        return firstCandidate;
    }

    private sealed class BmsAudioSourceWork(string path)
    {
        private readonly List<BmsAudioPathRequest> requests = [];

        internal string Path { get; } = path;

        internal IReadOnlyList<BmsAudioPathRequest> Requests => requests;

        internal Exception? PathNormalizationFailure { get; private set; }

        internal void Add(BmsAudioPathRequest request, Exception? pathNormalizationFailure)
        {
            requests.Add(request);
            PathNormalizationFailure ??= pathNormalizationFailure;
        }

        internal void SortRequests() => requests.Sort(static (left, right) => left.Index.CompareTo(right.Index));
    }
}
