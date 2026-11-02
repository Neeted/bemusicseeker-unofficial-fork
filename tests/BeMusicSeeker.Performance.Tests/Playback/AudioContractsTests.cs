using System.IO;
using System.Runtime.ExceptionServices;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Processes;
using Ribbit.BMS;
using Ribbit.Media;
using Ribbit.Media.Audio;
using static BeMusicSeeker.TestSupport.Components.AudioContractFixture;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AudioContractsTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

    /// <summary>手動・自然終了・準備保留の各切替を先読み有無で測定し、次曲の解析と復号を重複させず一回ずつ行う。</summary>
    /// <remarks>実NULL_DEVICEで解析・復号・切替を測定する性能区分。時間の閾値を固定せず処理重複だけを失敗とする。</remarks>
    [TestMethod]
    [TestCategory("Suite.audio")]
    public async Task PredecodeSwitchMeasurement()
    {
        var deadline = ProcessDeadline.FromSuiteEnvironment(TimeSpan.FromSeconds(300));
        ProcessDeadline? failureCleanup = null;
        int unexpectedNotificationCount = 0;
        SampleRate previousFrequency = BassAudioPlayer.Frequency;
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = workspace.RootDirectory;
        ExceptionDispatchInfo? failure = null;
        try
        {
            string nextPath = WritePlaybackChart(directory, "next.bms", 400, 1);
            File.AppendAllText(nextPath, "#WAV01 audio.wav\n");
            using (var wave = new BinaryWriter(File.Create(Path.Combine(directory, "audio.wav"))))
            {
                const int frames = 441000;
                wave.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); wave.Write(36 + frames * 2);
                wave.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); wave.Write(16);
                wave.Write((short)1); wave.Write((short)1); wave.Write(44100); wave.Write(88200);
                wave.Write((short)2); wave.Write((short)16); wave.Write(System.Text.Encoding.ASCII.GetBytes("data")); wave.Write(frames * 2);
                for (int frame = 0; frame < frames; frame++) { wave.Write((short)8192); }
            }
            foreach (string route in new[] { "manual", "natural", "pending" })
            {
                foreach (bool usePreload in new[] { false, true })
                {
                    for (int round = 0; round < 3; round++)
                    {
                        if (deadline.Remaining == TimeSpan.Zero) { throw new TimeoutException("Predecodeの共有期限を超えています。"); }
                        string firstPath = WritePlaybackChart(directory, "first.bms", 400, route == "natural" ? 1 : 50);
                        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var preparationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        using var preparationGate = new ManualResetEventSlim(route != "pending");
                        var switchStarted = new TaskCompletionSource<PlaybackStartOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var clock = new System.Diagnostics.Stopwatch();
                        int parses = 0;
                        int decodes = 0;
                        double preparationMs = 0;
                        var events = new List<string>();
                        var runtime = new RecordingAudioPlaybackRuntime(events)
                        {
                            InitializeResult = CreatePlaybackInitializationResult(),
                            InitializeAction = snapshot =>
                            {
                                BassAudioPlayer.Frequency = SampleRate.SAMPLE_RATE_44100Hz;
                                if (BassAudioPlayer.ActiveSession == null) { BassAudioPlayer.InitializeOwned(BassAudioPlayer.DeviceDriver.NULL_DEVICE, default, 0f, out _); }
                            }
                        };
                        var player = new InternalBMSAutoPlayerSoundOnly(new SettingsPlayerSettingsGateway(() => testSettings), runtime,
                            autoPlayerFactory: path =>
                            {
                                if (path == nextPath) { parses++; }
                                var parseClock = System.Diagnostics.Stopwatch.StartNew();
                                var chart = new Ribbit.BMS.BMSFile(path);
                                if (path == nextPath) { preparationMs += parseClock.Elapsed.TotalMilliseconds; }
                                return new MeasuredBmsAutoPlayer(chart, elapsed =>
                                {
                                    if (path == nextPath) { decodes++; preparationMs += elapsed.TotalMilliseconds; }
                                });
                            },
                            songPreparation: (input, token) =>
                            {
                                preparationEntered.SetResult();
                                preparationGate.Wait();
                                var prepareClock = System.Diagnostics.Stopwatch.StartNew();
                                parses++;
                                var chart = new Ribbit.BMS.BMSFile(input.Path);
                                decodes++;
                                var result = PreparedBmsSong.Prepare(chart, 0.4f, token);
                                preparationMs += prepareClock.Elapsed.TotalMilliseconds;
                                preparationFinished.SetResult();
                                return result;
                            });
                        object switchGate = new();
                        bool closing = false;
                        Task? preloadRequest = null;
                        PlaybackStartOperation? first = null;
                        PlaybackStartOperation? next = null;
                        ExceptionDispatchInfo? roundFailure = null;
                        try
                        {
                            first = player.BeginStart(firstPath, route == "natural" ? (_, _) =>
                            {
                                lock (switchGate)
                                {
                                    // 回収開始後の遅い自然終了通知は、新しい開始を所有させません。
                                    if (closing) { return; }
                                    clock.Start();
                                    try
                                    {
                                        next = player.BeginStart(nextPath, null, allowPreload: true);
                                        switchStarted.TrySetResult(next);
                                    }
                                    catch (Exception exception) { switchStarted.TrySetException(exception); }
                                }
                            }
                            : null, allowPreload: true);
                            await first.Ready.WaitAsync(deadline.Remaining);
                            if (usePreload)
                            {
                                preloadRequest = ((INextSongPreloadPlayer)player).PrepareNextAsync(NextSongPreloadInput.Capture(nextPath), _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount));
                                await preloadRequest.WaitAsync(deadline.Remaining);
                                await preparationEntered.Task.WaitAsync(deadline.Remaining);
                                if (route != "pending") { await preparationFinished.Task.WaitAsync(deadline.Remaining); }
                            }
                            if (route == "natural") { next = await switchStarted.Task.WaitAsync(deadline.Remaining); }
                            else
                            {
                                clock.Start();
                                next = player.BeginStart(nextPath, null, allowPreload: true);
                                preparationGate.Set();
                            }
                            await next.Ready.WaitAsync(deadline.Remaining);
                            clock.Stop();
                            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { route, usePreload, round, switchMs = clock.Elapsed.TotalMilliseconds, preparationMs, parses, decodes, initializationCalls = events.Count(value => value == "initialize") }));
                            Assert.AreEqual(1, parses);
                            Assert.AreEqual(1, decodes);
                        }
                        catch (Exception exception)
                        {
                            roundFailure = ExceptionDispatchInfo.Capture(exception);
                            failureCleanup ??= deadline.AddGrace(TimeSpan.FromSeconds(10)).LimitRemaining(TimeSpan.FromSeconds(10));
                        }
                        finally
                        {
                            PlaybackStartOperation? startedNext;
                            lock (switchGate)
                            {
                                closing = true;
                                startedNext = next;
                            }
                            if (switchStarted.Task.Exception is AggregateException callbackFailure
                                && !callbackFailure.InnerExceptions.Any(exception => ReferenceEquals(exception, roundFailure?.SourceException)))
                            {
                                CaptureFailure(ref roundFailure, callbackFailure);
                                failureCleanup ??= deadline.AddGrace(TimeSpan.FromSeconds(10)).LimitRemaining(TimeSpan.FromSeconds(10));
                            }
                            preparationGate.Set();
                            void RecordCleanupFailure(Exception exception)
                            {
                                CaptureFailure(ref roundFailure, exception);
                                failureCleanup ??= deadline.AddGrace(TimeSpan.FromSeconds(10)).LimitRemaining(TimeSpan.FromSeconds(10));
                            }
                            async Task ObserveCleanupAsync(Task owned, bool allowCancellation = false)
                            {
                                try { await owned.WaitAsync((failureCleanup ?? deadline).Remaining); }
                                catch (OperationCanceledException) when (allowCancellation && owned.IsCompleted) { }
                                catch (Exception exception)
                                {
                                    RecordCleanupFailure(exception);
                                    // WaitAsyncの期限切れは元Taskの終端ではありません。同じ回収cutoffで実終端を確認します。
                                    if (!owned.IsCompleted || exception is TimeoutException)
                                    {
                                        try { await owned.WaitAsync((failureCleanup ?? throw new InvalidOperationException("回収期限が確定していません。")).Remaining); }
                                        catch (OperationCanceledException) when (allowCancellation && owned.IsCompleted) { }
                                        catch (Exception cleanup) { RecordCleanupFailure(cleanup); }
                                    }
                                }
                            }
                            Task close = player.CloseAsync();
                            await ObserveCleanupAsync(close);
                            if (first != null) { await ObserveCleanupAsync(first.Completion, allowCancellation: true); }
                            if (startedNext != null) { await ObserveCleanupAsync(startedNext.Completion, allowCancellation: true); }
                            if (preloadRequest != null) { await ObserveCleanupAsync(preloadRequest); }
                            List<string> unterminated = [];
                            if (!close.IsCompleted) { unterminated.Add("CloseAsync"); }
                            if (first?.Completion.IsCompleted == false) { unterminated.Add("first.Completion"); }
                            if (startedNext?.Completion.IsCompleted == false) { unterminated.Add("next.Completion"); }
                            if (preloadRequest?.IsCompleted == false) { unterminated.Add("PrepareNextAsync"); }
                            if (unterminated.Count != 0)
                            {
                                // 専有MTPの後続caseやAssemblyCleanupへ未終端nativeを渡さず、親Jobの非成功回収へ委ねます。
                                workspace.PreserveArtifacts();
                                string message = $"Predecodeの実Taskが回収期限後も未終端です。性能test processを非正常終了します。route={route} usePreload={usePreload} round={round} unterminated={string.Join(",", unterminated)}";
                                try { File.WriteAllText(workspace.GetPath("predecode-cleanup-failure.txt"), message + Environment.NewLine + roundFailure?.SourceException); }
                                catch (Exception diagnosticFailure) { RecordCleanupFailure(diagnosticFailure); }
                                try { Console.Error.WriteLine(message + Environment.NewLine + roundFailure?.SourceException); }
                                catch (Exception diagnosticFailure) { RecordCleanupFailure(diagnosticFailure); }
                                Environment.FailFast(message, roundFailure?.SourceException);
                            }
                            try { BassAudioPlayer.Free(); }
                            catch (Exception exception) { CaptureFailure(ref roundFailure, exception); }
                            try { BassAudioRuntime.Shutdown(); }
                            catch (Exception exception) { CaptureFailure(ref roundFailure, exception); }
                        }
                        roundFailure?.Throw();
                    }
                }
            }
        }
        catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        finally
        {
            try { BassAudioPlayer.Frequency = previousFrequency; }
            catch (Exception exception) { CaptureFailure(ref failure, exception); }
            if (failure != null) { workspace.PreserveArtifacts(); }
            try { workspace.Dispose(); }
            catch (Exception exception) { CaptureFailure(ref failure, exception); }
        }
        failure?.Throw();
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }
    /// <summary>本文と後続の回収の原因を両方保持し、回収失敗だけで元原因を置換しません。</summary>
    private static void CaptureFailure(ref ExceptionDispatchInfo? primary, Exception failure)
    {
        primary = primary == null
            ? ExceptionDispatchInfo.Capture(failure)
            : ExceptionDispatchInfo.Capture(new AggregateException(primary.SourceException, failure));
    }
}
