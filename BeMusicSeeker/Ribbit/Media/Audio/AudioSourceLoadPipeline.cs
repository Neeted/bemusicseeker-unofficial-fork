#nullable enable annotations
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;
using Ribbit.Media;

namespace Ribbit.Media.Audio;

/// <summary>譜面で使う一つのWAV定義と解決済みpathです。</summary>
internal readonly record struct AudioLoadRequest(int Index, string ResourceName, string Path);

/// <summary>一つのWAV indexへ対応する読み込み失敗を保持します。</summary>
internal sealed record AudioLoadFailure(int Index, string ResourceName, string Path, Exception Exception);

/// <summary>音源pipelineの完了時に公開するplayerと対象別失敗です。</summary>
internal sealed class AudioSourceLoadResult<TPlayer> where TPlayer : class
{
    internal AudioSourceLoadResult(TPlayer[] players, IReadOnlyList<AudioLoadFailure> failures)
    {
        Players = players;
        Failures = failures;
    }

    internal TPlayer[] Players { get; }

    internal IReadOnlyList<AudioLoadFailure> Failures { get; }
}

/// <summary>pipelineの実処理段階を決定的に観測する内部境界です。</summary>
internal sealed class AudioSourceLoadPipelineObserver
{
    /// <summary>一回入力readに使うstreamを作成します。未指定ならpathから通常readします。</summary>
    internal Func<string, Stream>? OpenInput { get; init; }

    /// <summary>ファイル内容をnative memoryへ読み終えたときに呼びます。</summary>
    internal Action<string>? InputReadCompleted { get; init; }

    /// <summary>bounded queueへ入力を追加した直後の件数を通知します。</summary>
    internal Action<int>? QueueDepthChanged { get; init; }

    /// <summary>一つの入力の復号を開始するときに呼びます。</summary>
    internal Action<string>? DecodeStarted { get; init; }

    /// <summary>一つの入力の復号が成功したときに呼びます。</summary>
    internal Action<string>? DecodeCompleted { get; init; }

    /// <summary>一つのWAV index用playerの生成が成功したときに呼びます。</summary>
    internal Action<string, int>? PlayerCreated { get; init; }

    /// <summary>decoder workerの処理loopへ入る直前に呼びます。</summary>
    internal Action? DecoderWorkerStarting { get; init; }

    /// <summary>一つのpathで読み込み・復号・player作成が失敗したときに呼びます。</summary>
    internal Action<string, Exception>? ResourceFailed { get; init; }
}

/// <summary>入力の一回読取りとfloat32復号を有界並列で進めます。</summary>
internal static class AudioSourceLoadPipeline
{
    private const int ReadQueueCapacity = 4;

    /// <summary>WAV indexをpath単位へ集約し、成功したときだけ結果を返します。</summary>
    internal static AudioSourceLoadResult<TPlayer> Load<TPlayer>(
        int playerCount,
        IReadOnlyList<AudioLoadRequest> requests,
        bool asParallel,
        Func<DecodedAudio, string, BassAudioSession, TPlayer> createPlayer,
        AudioSourceLoadPipelineObserver? observer = null)
        where TPlayer : class
    {
        ArgumentOutOfRangeException.ThrowIfNegative(playerCount);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(createPlayer);

        var players = new TPlayer[playerCount];
        var failures = new ConcurrentQueue<AudioLoadFailure>();
        List<AudioSourceWork> work = GroupByPath(requests, failures);
        if (work.Count == 0)
        {
            return new AudioSourceLoadResult<TPlayer>(
                players,
                failures.OrderBy(item => item.Index).ToArray());
        }
        BassAudioSession expectedSession = CaptureActiveSession();

        if (!asParallel)
        {
            foreach (AudioSourceWork item in work)
            {
                Process(item, expectedSession, players, failures, createPlayer, input: null, observer);
            }
            return new AudioSourceLoadResult<TPlayer>(players, failures.OrderBy(item => item.Index).ToArray());
        }

        Exception? infrastructureFailure = RunParallel(work, expectedSession, players, failures, createPlayer, observer);
        if (infrastructureFailure != null)
        {
            AudioLoadRequest first = work[0].Requests[0];
            failures.Enqueue(new AudioLoadFailure(
                first.Index,
                first.ResourceName,
                first.Path,
                new InvalidOperationException("The audio input pipeline could not complete its workers.", infrastructureFailure)));
        }
        return new AudioSourceLoadResult<TPlayer>(players, failures.OrderBy(item => item.Index).ToArray());
    }

    private static BassAudioSession CaptureActiveSession()
    {
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        BassAudioSession? session = BassAudioPlayer.CurrentSessionForAdmittedOperation;
        return session?.State == BassAudioSessionState.Active
            ? session
            : throw new InvalidOperationException("An active audio session is required to load BMS audio.");
    }

    private static List<AudioSourceWork> GroupByPath(
        IReadOnlyList<AudioLoadRequest> requests,
        ConcurrentQueue<AudioLoadFailure> failures)
    {
        var groups = new Dictionary<string, AudioSourceWork>(StringComparer.OrdinalIgnoreCase);
        var work = new List<AudioSourceWork>();
        foreach (AudioLoadRequest request in requests)
        {
            try
            {
                string fullPath = Path.GetFullPath(request.Path);
                if (!groups.TryGetValue(fullPath, out AudioSourceWork? group))
                {
                    group = new AudioSourceWork(fullPath);
                    groups.Add(fullPath, group);
                    work.Add(group);
                }
                group.Add(request);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failures.Enqueue(new AudioLoadFailure(request.Index, request.ResourceName, request.Path, exception));
            }
        }

        foreach (AudioSourceWork group in work)
        {
            group.SortRequests();
        }
        return work;
    }

    private static Exception? RunParallel<TPlayer>(
        IReadOnlyList<AudioSourceWork> work,
        BassAudioSession expectedSession,
        TPlayer[] players,
        ConcurrentQueue<AudioLoadFailure> failures,
        Func<DecodedAudio, string, BassAudioSession, TPlayer> createPlayer,
        AudioSourceLoadPipelineObserver? observer)
        where TPlayer : class
    {
        int readerCount = System.Math.Min(2, work.Count);
        int decoderCount = System.Math.Min(
            work.Count,
            System.Math.Max(1, Environment.ProcessorCount - 1));
        using var cancellation = new CancellationTokenSource();
        using var readQueue = new BlockingCollection<ReadResult>(ReadQueueCapacity);
        var infrastructureFailures = new ConcurrentQueue<Exception>();
        int nextIndex = -1;

        Task[] readers = Enumerable.Range(0, readerCount)
            .Select(_ => Task.Run(() => ReadWorker()))
            .ToArray();
        Task readersCompleted = Task.WhenAll(readers).ContinueWith(
            _ => readQueue.CompleteAdding(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Task[] decoders = Enumerable.Range(0, decoderCount)
            .Select(_ => Task.Run(() => DecodeWorker()))
            .ToArray();
        Task.WhenAll(decoders.Append(readersCompleted)).GetAwaiter().GetResult();

        while (readQueue.TryTake(out ReadResult? pending))
        {
            pending.Input?.Dispose();
        }

        void ReadWorker()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    int index = Interlocked.Increment(ref nextIndex);
                    if (index >= work.Count)
                    {
                        return;
                    }

                    AudioSourceWork item = work[index];
                    AudioInputFile? input = null;
                    Exception? readFailure = null;
                    try
                    {
                        input = AudioInputFile.Read(item.Path, observer?.OpenInput);
                        observer?.InputReadCompleted?.Invoke(item.Path);
                    }
                    catch (Exception exception)
                    {
                        input?.Dispose();
                        input = null;
                        readFailure = exception;
                    }

                    try
                    {
                        readQueue.Add(new ReadResult(item, input, readFailure), cancellation.Token);
                        observer?.QueueDepthChanged?.Invoke(readQueue.Count);
                    }
                    catch
                    {
                        input?.Dispose();
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (InvalidOperationException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                infrastructureFailures.Enqueue(exception);
                cancellation.Cancel();
            }
        }

        void DecodeWorker()
        {
            try
            {
                observer?.DecoderWorkerStarting?.Invoke();
                foreach (ReadResult item in readQueue.GetConsumingEnumerable(cancellation.Token))
                {
                    Process(item.Work, expectedSession, players, failures, createPlayer, item, observer);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                infrastructureFailures.Enqueue(exception);
                cancellation.Cancel();
            }
        }

        return infrastructureFailures
            .OrderBy(exception => exception.GetType().FullName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static void Process<TPlayer>(
        AudioSourceWork work,
        BassAudioSession expectedSession,
        TPlayer[] players,
        ConcurrentQueue<AudioLoadFailure> failures,
        Func<DecodedAudio, string, BassAudioSession, TPlayer> createPlayer,
        ReadResult? input,
        AudioSourceLoadPipelineObserver? observer)
        where TPlayer : class
    {
        AudioLoadRequest first = work.Requests[0];
        try
        {
            if (input?.Failure != null)
            {
                throw input.Failure;
            }

            using AudioInputFile ownedInput = input?.Input ?? AudioInputFile.Read(work.Path, observer?.OpenInput);
            if (input == null)
            {
                observer?.InputReadCompleted?.Invoke(work.Path);
            }
            using BassAudioOperationLease workerOperation = BassAudioRuntime.EnterAudioOperation();
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
            observer?.DecodeStarted?.Invoke(work.Path);
            DecodedAudio decoded = AudioSourceLoader.Decode(ownedInput, expectedSession);
            observer?.DecodeCompleted?.Invoke(work.Path);
            foreach (AudioLoadRequest request in work.Requests)
            {
                try
                {
                    players[request.Index] = createPlayer(decoded, work.Path, expectedSession);
                    observer?.PlayerCreated?.Invoke(work.Path, request.Index);
                }
                catch (Exception exception)
                {
                    failures.Enqueue(new AudioLoadFailure(
                        request.Index,
                        request.ResourceName,
                        request.Path,
                        exception));
                    observer?.ResourceFailed?.Invoke(work.Path, exception);
                }
            }
        }
        catch (Exception exception)
        {
            failures.Enqueue(new AudioLoadFailure(first.Index, first.ResourceName, first.Path, exception));
            observer?.ResourceFailed?.Invoke(work.Path, exception);
        }
    }

    private sealed class AudioSourceWork(string path)
    {
        private readonly List<AudioLoadRequest> requests = [];

        internal string Path { get; } = path;

        internal IReadOnlyList<AudioLoadRequest> Requests => requests;

        internal void Add(AudioLoadRequest request) => requests.Add(request);

        internal void SortRequests() => requests.Sort(static (left, right) => left.Index.CompareTo(right.Index));
    }

    private sealed record ReadResult(AudioSourceWork Work, AudioInputFile? Input, Exception? Failure);
}
