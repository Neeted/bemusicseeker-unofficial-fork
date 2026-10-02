using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using ManagedBass;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.BMS;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AudioContractsTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = new();

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompletionFailure_JoinsRealInputAndDecoderCleanupBeforeRuntimeFree(bool holdDecoderRelease)
    {
        string directory = Path.Combine(Path.GetTempPath(), "bms-completion-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string firstPath = WritePlaybackChart(directory, "A.bms", 120, 50);
        File.AppendAllText(firstPath, "#WAV01 sound.wav\n#00011:0101\n");
        string nextPath = WritePlaybackChart(directory, "B.bms", 120, 50);
        string wavePath = Path.Combine(directory, "sound.wav");
        File.WriteAllBytes(wavePath, BmsRealtimeAudioSchedulerTests.BuildFloatWave(44100, new float[44100 * 2]));
        var inputEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tickEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tickFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inputGate = new ManualResetEventSlim();
        using var tickGate = new ManualResetEventSlim();
        var events = new List<string>();
        var native = new BmsRealtimeAudioSchedulerTests.FaultInjectingScheduledNativeBoundary(0, false);
        CompletionObservedBmsAutoPlayer? active = null;
        AudioInputFile? heldInput = null;
        int heldHandle = 0;
        int exits = 0;
        SampleRate originalFrequency = BassAudioPlayer.Frequency;
        var runtime = new RecordingAudioPlaybackRuntime(events)
        {
            InitializeResult = CreatePlaybackInitializationResult(),
            InitializeAction = snapshot =>
            {
                BassAudioPlayer.Frequency = SampleRate.SAMPLE_RATE_44100Hz;
                BassAudioPlayer.InitializeOwned(BassAudioPlayer.DeviceDriver.NULL_DEVICE, default, 0f, out _);
            },
            FreeFailureForCall = _ =>
            {
                Assert.IsTrue(producerEnded.Task.IsCompletedSuccessfully, "Free試行より先にinput/decoder producerの終端へ合流します。");
                Assert.IsTrue(events.Contains("source-ended"), "現曲sourceの終了確認もFreeより先です。");
                BassAudioPlayer.Free();
                return null;
            }
        };
        var actual = new InternalBMSAutoPlayerSoundOnly(new SettingsPlayerSettingsGateway(() => testSettings), runtime,
            autoPlayerFactory: path => active = new CompletionObservedBmsAutoPlayer(new Ribbit.BMS.BMSFile(path), native,
                tickGate, tickEntered, tickFailure, events),
            songPreparation: (request, token) =>
            {
                using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
                try
                {
                    using var input = AudioInputFile.Read(wavePath, path => holdDecoderRelease
                        ? File.OpenRead(path)
                        : new CompletionInputReadStream(File.ReadAllBytes(path), inputEntered, inputGate));
                    heldInput = input;
                    BassAudioSession session = BassAudioPlayer.ActiveSession ?? throw new AssertFailedException("現sessionが必要です。");
                    _ = AudioSourceLoader.Decode(input, session, handle =>
                    {
                        if (holdDecoderRelease)
                        {
                            heldHandle = handle;
                            inputEntered.TrySetResult();
                            inputGate.Wait();
                        }
                        Bass.CurrentDevice = session.CoreDeviceIndex;
                        bool released = Bass.StreamFree(handle);
                        events.Add("decoder-release");
                        return released;
                    });
                    token.ThrowIfCancellationRequested();
                    throw new AssertFailedException("故障した現曲は準備を取り消します。");
                }
                finally { events.Add("prepare-terminal"); producerEnded.TrySetResult(); }
            });
        PlaybackStartOperation operation = actual.BeginStart(firstPath, (_, _) => exits++, true);
        var failures = new List<Exception>();
        try
        {
            await operation.Ready;
            await ObserveSignalOrFailureAsync(tickEntered.Task, operation.Completion);
            Assert.AreEqual(PlayState.Playing, active?.PlayState);
            await ((INextSongPreloadPlayer)actual).PrepareNextAsync(NextSongPreloadInput.Capture(nextPath), _ => Assert.Fail("予期しない先読み故障です。"));
            await ObserveSignalOrFailureAsync(inputEntered.Task, operation.Completion);
            BassAudioSession session = active?.ResourceSession ?? throw new AssertFailedException("実playerが必要です。");
            session.PublishCallbackOutputHandle(session.MixerHandle);
            var callbackNative = new BassAudioSessionTests.CallbackPcmNative { Fail = !holdDecoderRelease };
            session.CallbackPcmRenderer = new AudioPcmRenderer(session.MixerHandle, 44100, 2, callbackNative);
            session.OutputProcessor = new AudioOutputProcessor(44100, 1d);
            if (holdDecoderRelease) { native.FailNextResume(); }
            int callbackBytes = holdDecoderRelease ? 44100 * 2 * sizeof(float) : 16;
            IntPtr buffer = Marshal.AllocHGlobal(callbackBytes);
            try { Assert.AreEqual(callbackBytes, BassAudioPlayer.ReadPublishedCallbackOutput(session, buffer, callbackBytes)); }
            finally { Marshal.FreeHGlobal(buffer); }
            tickGate.Set();
            await ObserveSignalOrFailureAsync(cancelled.Task, operation.Completion);
            Exception primary = await tickFailure.Task;
            Assert.IsTrue(holdDecoderRelease ? primary is BmsScheduledAudioException : primary is AudioCallbackOutputFailureException);
            Assert.IsFalse(producerEnded.Task.IsCompleted);
            Assert.IsFalse(operation.Completion.IsCompleted);
            Assert.IsTrue(operation.Ready.IsCompletedSuccessfully);
            Assert.AreEqual(0, events.Count(value => value == "free"), "成功だけでなくFree試行がないことを確認します。");
            Assert.AreEqual(1, events.Count(value => value == "initialize"));
            if (holdDecoderRelease)
            {
                Assert.IsNotNull(heldInput);
                BassAudioOwnedStream owner = session.GetPlayerStreams().Single(item => item.Handle == heldHandle);
                Assert.IsTrue(owner.TryGetOwner(out AudioInputFile input));
                Assert.AreSame(heldInput, input);
                using Stream view = input.OpenReadView();
                Assert.AreEqual((int)'R', view.ReadByte(), "取消だけではdecoderと入力所有は終端になりません。");
            }
            inputGate.Set();
            Exception failure = await GetTaskFailureAsync(operation.Completion);
            Assert.IsTrue(ExceptionCauses(failure).Any(cause => ReferenceEquals(cause, primary)));
            Assert.AreEqual(0, exits);
            Assert.IsTrue(events.IndexOf("prepare-terminal") < events.IndexOf("free"));
            Assert.IsTrue(events.IndexOf("source-ended") < events.IndexOf("free"));
            if (holdDecoderRelease) { Assert.ThrowsException<ObjectDisposedException>(() => heldInput?.OpenReadView()); }
        }
        catch (Exception failure) { failures.Add(failure); }
        finally
        {
            tickGate.Set(); inputGate.Set();
            await CaptureTaskCompletionAsync(operation.Completion, failures, failure => ExceptionCauses(failure).Any(cause =>
                cause is AudioCallbackOutputFailureException or BmsScheduledAudioException or OperationCanceledException));
            runtime.FreeFailureForCall = null;
            try { await actual.CloseAsync(); } catch (Exception failure) { failures.Add(failure); }
            CaptureCleanup(failures, () => BassAudioPlayer.Free());
            CaptureCleanup(failures, BassAudioRuntime.Shutdown);
            BassAudioPlayer.Frequency = originalFrequency;
            CaptureCleanup(failures, () => Directory.Delete(directory, true));
        }
        ThrowFailures(failures);
    }

    [TestMethod]
    public Task CompletionOwnedPreloadFatal_AfterStopNotifiesOnceWithBothCauses() =>
        VerifyOwnedPlaybackFailureNotificationAsync(backgroundOwnsFailure: false);

    [TestMethod]
    public Task BackgroundPreloadFatal_WithPlaybackCleanupFailureNotifiesOnce() =>
        VerifyOwnedPlaybackFailureNotificationAsync(backgroundOwnsFailure: true);

    private async Task VerifyOwnedPlaybackFailureNotificationAsync(bool backgroundOwnsFailure)
    {
        string directory = Path.Combine(Path.GetTempPath(), "bms-owned-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string firstPath = WritePlaybackChart(directory, "A.bms", 120, 50);
        File.AppendAllText(firstPath, "#WAV01 sound.wav\n#00011:01\n");
        File.WriteAllBytes(Path.Combine(directory, "sound.wav"),
            BmsRealtimeAudioSchedulerTests.BuildFloatWave(44100, new float[44100 * 2]));
        string nextPath = WritePlaybackChart(directory, "B.bms", 120, 50);
        using var tickGate = new ManualResetEventSlim();
        using var preparationGate = new ManualResetEventSlim();
        var tickEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tickFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preloadFailure = new AudioSourceFatalException("next-song cleanup failed");
        var events = new List<string>();
        var failures = new List<Exception>();
        var native = new BmsRealtimeAudioSchedulerTests.FaultInjectingScheduledNativeBoundary(0, false,
            failRemoveChannel: backgroundOwnsFailure, failStreamFree: backgroundOwnsFailure);
        CompletionObservedBmsAutoPlayer? active = null;
        int starts = 0;
        SampleRate originalFrequency = BassAudioPlayer.Frequency;
        var runtime = new RecordingAudioPlaybackRuntime(events)
        {
            InitializeResult = CreatePlaybackInitializationResult(),
            InitializeAction = settings =>
            {
                BassAudioPlayer.Frequency = SampleRate.SAMPLE_RATE_44100Hz;
                BassAudioPlayer.InitializeOwned(BassAudioPlayer.DeviceDriver.NULL_DEVICE, default, 0f, out _);
            },
            FreeFailureForCall = _ => { BassAudioPlayer.Free(); return null; }
        };
        var player = new ObservedInternalBmsPlayer(new SettingsPlayerSettingsGateway(() => testSettings), runtime,
            path =>
            {
                starts++;
                return active = new CompletionObservedBmsAutoPlayer(new Ribbit.BMS.BMSFile(path), native,
                    tickGate, tickEntered, tickFailure, events);
            },
            (_, token) =>
            {
                using CancellationTokenRegistration registration = token.Register(() => preparationCancelled.TrySetResult());
                preparationEntered.TrySetResult();
                preparationGate.Wait();
                throw preloadFailure;
            });
        player.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(player.Duration) && player.Duration == TimeSpan.MinValue)
            {
                closeEntered.TrySetResult();
            }
        };
        var rows = new MainChartListViewModel
        {
            Rows = new List<object> { new BeMusicSeeker.Models.BMSFile { path = firstPath }, new BeMusicSeeker.Models.BMSFile { path = nextPath } }
        };
        var dialogs = new ObservedPlaybackDialogs(events);
        testSettings.FolderSkipPlayMode = false;
        testSettings.SinglePlayMode = false;
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackDispatcher(), new MainChartListPlaybackQueue(rows),
            new SettingsPlaybackSettingsStore(() => testSettings), dialogs, _ => Assert.Fail("Valid chart failed."),
            new ChartFileOperationSynchronizer());
        Task start = panel.StartAtIndex(0);
        Task? stop = null;
        try
        {
            await start;
            PlaybackStartOperation operation = player.Operation ?? throw new AssertFailedException("実開始操作が必要です。");
            await ObserveSignalOrFailureAsync(tickEntered.Task, operation.Completion);
            await ObserveSignalOrFailureAsync(preparationEntered.Task, operation.Completion);
            if (backgroundOwnsFailure)
            {
                preparationGate.Set();
                await ObserveSignalOrFailureAsync(closeEntered.Task, operation.Completion);
                // 背景処理が現playerを外してから再生Taskのcleanupを失敗させます。
                tickGate.Set();
            }
            else
            {
                BassAudioSession session = active?.ResourceSession ?? throw new AssertFailedException("実sessionが必要です。");
                session.PublishCallbackOutputHandle(session.MixerHandle);
                session.CallbackPcmRenderer = new AudioPcmRenderer(session.MixerHandle, 44100, 2,
                    new BassAudioSessionTests.CallbackPcmNative { Fail = true });
                session.OutputProcessor = new AudioOutputProcessor(44100, 1d);
                IntPtr buffer = Marshal.AllocHGlobal(16);
                try { Assert.AreEqual(16, BassAudioPlayer.ReadPublishedCallbackOutput(session, buffer, 16)); }
                finally { Marshal.FreeHGlobal(buffer); }
                tickGate.Set();
                await ObserveSignalOrFailureAsync(preparationCancelled.Task, operation.Completion);
                stop = panel.StopPlayback(closeProcess: true);
                Assert.IsNull(panel.NowPlayingBmsFile);
                Assert.IsFalse(stop.IsCompleted);
                Assert.IsFalse(operation.Completion.IsCompleted);
                preparationGate.Set();
            }
            Exception completionFailure = await GetTaskFailureAsync(operation.Completion);
            if (stop != null) { await stop; }
            Exception reported = await dialogs.Failure.Task;
            Exception[] causes = ExceptionCauses(reported).ToArray();
            Assert.IsTrue(causes.Any(cause => ReferenceEquals(cause, preloadFailure)));
            if (backgroundOwnsFailure)
            {
                Exception[] cleanupCauses = ExceptionCauses(completionFailure)
                    .Where(cause => cause is not OperationCanceledException).ToArray();
                Assert.IsTrue(cleanupCauses.Any(cause => cause is BassAudioPlaybackException));
                Assert.IsTrue(cleanupCauses.All(cause => causes.Any(reportedCause => ReferenceEquals(reportedCause, cause))));
            }
            else
            {
                Exception primary = await tickFailure.Task;
                Assert.IsTrue(causes.Any(cause => ReferenceEquals(cause, primary)));
                Assert.IsTrue(ExceptionCauses(completionFailure).Any(cause => ReferenceEquals(cause, preloadFailure)));
            }
            Assert.AreEqual(1, dialogs.Notifications);
            Assert.AreEqual(1, starts);
            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.IsFalse(panel.IsPlaying);
        }
        catch (Exception failure) { failures.Add(failure); }
        finally
        {
            tickGate.Set();
            preparationGate.Set();
            await CaptureTaskCompletionAsync(start, failures);
            if (stop != null) { await CaptureTaskCompletionAsync(stop, failures); }
            await CaptureTaskCompletionAsync(player.CloseAsync(), failures, failure => ExceptionCauses(failure).Any(cause =>
                ReferenceEquals(cause, preloadFailure) || cause is AudioCallbackOutputFailureException or BassAudioPlaybackException));
            if (player.Operation != null)
            {
                await CaptureTaskCompletionAsync(player.Operation.Completion, failures, failure => ExceptionCauses(failure).Any(cause =>
                    ReferenceEquals(cause, preloadFailure) || cause is OperationCanceledException or AudioCallbackOutputFailureException or BassAudioPlaybackException));
            }
            CaptureCleanup(failures, () => BassAudioPlayer.Free());
            CaptureCleanup(failures, BassAudioRuntime.Shutdown);
            BassAudioPlayer.Frequency = originalFrequency;
            CaptureCleanup(failures, () => Directory.Delete(directory, true));
        }
        ThrowFailures(failures);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task EventModeNextStart_LoadAndPreparedAdoptionWaitForOwnRealCallback(bool originalEventMode, bool usePreload)
    {
        string directory = Path.Combine(Path.GetTempPath(), "bms-event-next-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SampleRate previousFrequency = BassAudioPlayer.Frequency;
        string firstPath = WritePlaybackChart(directory, "first.bms", 120, 50);
        string nextPath = WritePlaybackChart(directory, "next.bms", 120, 50);
        string wavePath = Path.Combine(directory, "used.wav");
        using var wave = AudioMixerSignalTests.TemporaryFloatWave.Create(48000, 2205, _ => .125f);
        File.Copy(wave.Path, wavePath);
        File.AppendAllText(nextPath, "#WAV01 used.wav\n");
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationEnded = new TaskCompletionSource<PreparedBmsSong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputWaitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var preparationGate = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        var events = new List<string>();
        var sessions = new List<BassAudioSession>();
        int reads = 0;
        int preparations = 0;
        int loads = 0;
        PreparedBmsSong? prepared = null;
        testSettings.PlayerDriver = BassAudioPlayer.DeviceDriver.WASAPI_SHARED;
        testSettings.PlayerWASAPIParam = originalEventMode;
        BassAudioPlayer.Frequency = SampleRate.SAMPLE_RATE_48000Hz;
        BassAudioPlaybackRuntime physicalRuntime = CreateInjectedBassAudioPlaybackRuntime(events, sessions, (settings, session) =>
        {
            if (BassAudioPlayer.ActiveSession == null) { BassAudioPlayer.InitializeOwned(BassAudioPlayer.DeviceDriver.NULL_DEVICE, default, 0f, out _); }
            if (sessions.Count == 1) { ReportRealOutputCallback(session); }
        });
        var runtime = new RecordingAudioPlaybackRuntime(events)
        {
            InitializeResult = CreatePlaybackInitializationResult(),
            InitializeAction = settings => physicalRuntime.Initialize(settings),
            OutputReady = () =>
            {
                Task observed = physicalRuntime.WaitForOutputReadyAsync();
                if (sessions.Count == 2) { outputWaitEntered.TrySetResult(); }
                return observed;
            }
        };
        var player = new InternalBMSAutoPlayerSoundOnly(new SettingsPlayerSettingsGateway(() => testSettings), runtime,
            autoPlayerFactory: path => path == firstPath
                ? new GatedReleaseBMSAutoPlayer(new Ribbit.BMS.BMSFile(path), releaseGate, releaseEntered, releaseEnded)
                : new MeasuredBmsAutoPlayer(Ribbit.BMS.BMSFile.ParseForAudit(path, new BmsParseOptions { InputRead = _ => Interlocked.Increment(ref reads) }), _ => Interlocked.Increment(ref loads)),
            songPreparation: (input, token) =>
            {
                Interlocked.Increment(ref preparations);
                preparationEntered.TrySetResult();
                preparationGate.Wait();
                var chart = Ribbit.BMS.BMSFile.ParseForAudit(input.Path, new BmsParseOptions { InputRead = _ => Interlocked.Increment(ref reads) });
                var result = PreparedBmsSong.Prepare(chart, .4f, token);
                preparationEnded.TrySetResult(result);
                return result;
            });
        PlaybackStartOperation first = player.BeginStart(firstPath, null, true);
        PlaybackStartOperation? next = null;
        try
        {
            await first.Ready;
            BassAudioSession oldSession = sessions[0];
            Assert.IsTrue(oldSession.WaitForOutputReadyAsync().IsCompletedSuccessfully);
            PlayerSettingsSnapshot original = new SettingsPlayerSettingsGateway(() => testSettings).CaptureSnapshot();
            Assert.IsTrue(physicalRuntime.WaitForOutputReadyAsync().IsCompletedSuccessfully);
            Assert.AreSame(physicalRuntime.Initialize(original), physicalRuntime.Initialize(original));
            if (usePreload)
            {
                await ((INextSongPreloadPlayer)player).PrepareNextAsync(NextSongPreloadInput.Capture(nextPath), _ => Assert.Fail("予期しない先読み故障です。"));
                await preparationEntered.Task;
            }
            testSettings.PlayerWASAPIParam = !originalEventMode;
            Assert.AreEqual(1, sessions.Count);
            Assert.AreEqual(BassAudioSessionState.Active, oldSession.State);
            next = player.BeginStart(nextPath, null, true);
            await releaseEntered.Task;
            Assert.AreEqual(1, sessions.Count);
            releaseGate.Set();
            await releaseEnded.Task;
            if (usePreload)
            {
                Assert.AreEqual(1, sessions.Count, "旧source終端だけでは準備中decoderのsessionを交換しません。");
                preparationGate.Set();
                prepared = await preparationEnded.Task;
                File.Delete(wavePath);
            }
            await outputWaitEntered.Task;
            Assert.AreEqual(2, sessions.Count);
            Assert.AreEqual(BassAudioSessionState.Released, oldSession.State);
            BassAudioSession replacement = sessions[1];
            Assert.AreEqual(!originalEventMode, replacement.OutputRequest.EventMode);
            Assert.AreEqual(originalEventMode, oldSession.OutputRequest.EventMode);
            Assert.AreEqual(0, replacement.MaximumCallbackFrames);
            Assert.IsFalse(next.Ready.IsCompleted);
            Assert.IsFalse(next.Completion.IsCompleted);
            Assert.AreEqual(TimeSpan.MinValue, player.Duration, "Load/Adopt済みでも初回観測前は曲情報を公開しません。");
            Assert.AreEqual(TimeSpan.MinValue, player.CurrentTime);
            Assert.AreEqual(1, reads);
            Assert.AreEqual(usePreload ? 1 : 0, preparations);
            Assert.AreEqual(usePreload ? 0 : 1, loads);
            ReportRealOutputCallback(replacement);
            await next.Ready;
            Assert.IsTrue(player.Duration > TimeSpan.Zero);
            Assert.IsFalse(next.Completion.IsCompleted, "Readyは長い曲の演奏cleanup完了を意味しません。");
            if (prepared != null) { Assert.ThrowsException<InvalidOperationException>(() => prepared.TakeResources()); }
            Assert.AreEqual(1, reads);
            Assert.AreEqual(usePreload ? 0 : 1, loads);
            Assert.IsTrue(physicalRuntime.WaitForOutputReadyAsync().IsCompletedSuccessfully);
            Assert.AreEqual(2, sessions.Count);
        }
        finally
        {
            preparationGate.Set(); releaseGate.Set();
            foreach (BassAudioSession session in sessions) { ReportRealOutputCallback(session); }
            await player.CloseAsync();
            try { await first.Completion; } catch (OperationCanceledException) { }
            if (next != null) { try { await next.Completion; } catch (OperationCanceledException) { } }
            physicalRuntime.Free();
            BassAudioPlayer.Free(); BassAudioRuntime.Shutdown();
            BassAudioPlayer.Frequency = previousFrequency;
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task FirstOutputDeadline_PreservesTimeoutAndCleanupFailuresWithoutLateRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bms-output-deadline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = WritePlaybackChart(directory, "chart.bms", 120, 50);
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.ASIO) { ActualBackend = BassAudioPlayer.DeviceDriver.ASIO };
        Task sharedObservation = session.WaitForOutputReadyAsync();
        var events = new List<string>();
        var disposeFailure = new InvalidOperationException("dispose failed");
        var freeFailure = new InvalidOperationException("free failed");
        var runtime = new RecordingAudioPlaybackRuntime(events)
        {
            InitializeResult = CreatePlaybackInitializationResult(),
            OutputReady = () => sharedObservation,
            FreeFailureForCall = call => call == 1 ? freeFailure : null
        };
        var player = new InternalBMSAutoPlayerSoundOnly(new SettingsPlayerSettingsGateway(() => testSettings), runtime,
            autoPlayerFactory: file => new DisposeObservedBmsAutoPlayer(new Ribbit.BMS.BMSFile(file), events, disposeFailure),
            outputReadyTimeout: TimeSpan.Zero);
        PlaybackStartOperation operation = player.BeginStart(path, null, false);
        try
        {
            AggregateException failure = await Assert.ThrowsExceptionAsync<AggregateException>(() => operation.Ready);
            Assert.AreSame(failure, await Assert.ThrowsExceptionAsync<AggregateException>(() => operation.Completion));
            Exception[] causes = failure.Flatten().InnerExceptions.ToArray();
            Assert.AreEqual(1, causes.OfType<TimeoutException>().Count());
            Assert.IsTrue(causes.Contains(disposeFailure)); Assert.IsTrue(causes.Contains(freeFailure));
            Assert.IsFalse(sharedObservation.IsCompleted, "期限はsession共有観測を成功・取消へ変更しません。");
            Assert.AreEqual(TimeSpan.MinValue, player.Duration);
            Assert.AreEqual(TimeSpan.MinValue, player.CurrentTime);
            Assert.AreEqual(1, events.Count(value => value == "dispose"));
            Assert.AreEqual(1, events.Count(value => value == "free"));
            ReportRealOutputCallback(session);
            await sharedObservation;
            Assert.IsTrue(operation.Ready.IsFaulted); Assert.IsTrue(operation.Completion.IsFaulted);
            Assert.AreEqual(TimeSpan.MinValue, player.Duration);
            Assert.AreEqual(TimeSpan.MinValue, player.CurrentTime);
            Assert.AreEqual(1, events.Count(value => value == "dispose"));
            Assert.AreEqual(1, events.Count(value => value == "free"));
        }
        finally
        {
            runtime.FreeFailureForCall = null;
            ReportRealOutputCallback(session);
            await player.CloseAsync();
            try { await operation.Completion; } catch (AggregateException) { }
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task InternalClose_JoinsFirstOutputObservationBeforeRuntimeFree()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bms-output-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = WritePlaybackChart(directory, "chart.bms", 120, 50);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.ASIO) { ActualBackend = BassAudioPlayer.DeviceDriver.ASIO };
        var events = new List<string>();
        var runtime = new RecordingAudioPlaybackRuntime(events)
        {
            InitializeResult = CreatePlaybackInitializationResult(),
            OutputReady = () => { Task observed = session.WaitForOutputReadyAsync(); entered.TrySetResult(); return observed; }
        };
        var player = new InternalBMSAutoPlayerSoundOnly(new SettingsPlayerSettingsGateway(() => testSettings), runtime,
            autoPlayerFactory: file => new StopObservedBmsAutoPlayer(new Ribbit.BMS.BMSFile(file), events));
        PlaybackStartOperation operation = player.BeginStart(path, null, false);
        Task? close = null;
        try
        {
            await entered.Task;
            close = player.CloseAsync();
            Assert.IsFalse(close.IsCompleted); Assert.IsFalse(operation.Ready.IsCompleted);
            Assert.IsFalse(events.Contains("free")); Assert.IsFalse(events.Contains("current-stop"));
            ReportRealOutputCallback(session);
            await operation.Ready;
            await close;
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => operation.Completion);
            Assert.AreEqual(1, events.Count(value => value == "free"));
        }
        finally
        {
            ReportRealOutputCallback(session);
            if (close != null) { await close; } else { await player.CloseAsync(); }
            try { await operation.Completion; } catch (OperationCanceledException) { }
            Directory.Delete(directory, true);
        }
    }

    private static void ReportRealOutputCallback(BassAudioSession session)
    {
        session.PublishCallbackOutputHandle(456);
        session.OutputProcessor = new AudioOutputProcessor(48000, 1);
        session.CallbackPcmRenderer = new AudioPcmRenderer(123, 48000, 2, new BassAudioSessionTests.CallbackPcmNative());
        IntPtr buffer = Marshal.AllocHGlobal(16);
        try { Assert.AreEqual(16, BassAudioPlayer.ReadPublishedCallbackOutput(session, buffer, 16)); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreloadFatal_CloseJoinsPreparationAndNotifiesOnceWithIndependentCleanupFailure(bool failCleanup)
    {
        string directory = Path.Combine(Path.GetTempPath(), "bms-preload-fatal-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string firstPath = WritePlaybackChart(directory, "first.bms", 120, 50);
        string nextPath = WritePlaybackChart(directory, "next.bms", 120, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var fatal = new InvalidOperationException("prepare fatal");
        var cleanupFailure = new InvalidOperationException("runtime cleanup failed");
        var events = new List<string>();
        var runtime = new RecordingAudioPlaybackRuntime(events)
        {
            InitializeResult = CreatePlaybackInitializationResult(),
            FreeException = failCleanup ? cleanupFailure : null
        };
        var player = new InternalBMSAutoPlayerSoundOnly(new SettingsPlayerSettingsGateway(() => testSettings), runtime,
            autoPlayerFactory: path => new StopObservedBmsAutoPlayer(new Ribbit.BMS.BMSFile(path), events),
            songPreparation: (_, _) =>
            {
                entered.TrySetResult();
                release.Wait();
                events.Add("prepare-terminal");
                throw fatal;
            });
        var rows = new MainChartListViewModel
        {
            Rows = new List<object> { new BeMusicSeeker.Models.BMSFile { path = firstPath }, new BeMusicSeeker.Models.BMSFile { path = nextPath } }
        };
        var dialogs = new ObservedPlaybackDialogs(events);
        testSettings.FolderSkipPlayMode = false;
        testSettings.SinglePlayMode = false;
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackDispatcher(), new MainChartListPlaybackQueue(rows),
            new SettingsPlaybackSettingsStore(() => testSettings), dialogs, _ => Assert.Fail("Valid chart failed."), new ChartFileOperationSynchronizer());
        try
        {
            await panel.StartAtIndex(0);
            await entered.Task;
            Assert.IsTrue(panel.IsPlaying);
            Assert.IsFalse(events.Contains("free"));
            release.Set();
            Exception reported = await dialogs.Failure.Task;
            Assert.IsNull(panel.NowPlayingBmsFile);
            Assert.IsFalse(panel.IsPlaying);
            Assert.AreEqual(1, dialogs.Notifications);
            Exception[] causes = reported is AggregateException aggregate ? [.. aggregate.Flatten().InnerExceptions] : [reported];
            Assert.AreEqual(failCleanup ? 2 : 1, causes.Length);
            Assert.AreSame(fatal, causes[0]);
            if (failCleanup) { Assert.AreSame(cleanupFailure, causes[1]); }
            Assert.IsTrue(events.IndexOf("prepare-terminal") < events.IndexOf("current-stop"));
            Assert.IsTrue(events.IndexOf("current-stop") < events.IndexOf("free"));
            Assert.IsTrue(events.IndexOf("free") < events.IndexOf("notify"));
            Assert.AreEqual(1, events.Count(value => value == "free"));
        }
        finally
        {
            release.Set();
            try { await player.CloseAsync(); }
            catch (Exception failure) when (ReferenceEquals(failure, cleanupFailure)) { }
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Performance")]
    public async Task PredecodeSwitchMeasurement()
    {
        SampleRate previousFrequency = BassAudioPlayer.Frequency;
        string directory = Path.Combine(Path.GetTempPath(), "bms-predecode-measure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
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
        try
        {
            foreach (string route in new[] { "manual", "natural", "pending" })
            {
                foreach (bool usePreload in new[] { false, true })
                {
                    for (int round = 0; round < 3; round++)
                    {
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
                        PlaybackStartOperation first = player.BeginStart(firstPath, route == "natural" ? (_, _) =>
                        {
                            clock.Start();
                            switchStarted.TrySetResult(player.BeginStart(nextPath, null, allowPreload: true));
                        }
                        : null, allowPreload: true);
                        PlaybackStartOperation? next = null;
                        try
                        {
                            await first.Ready;
                            if (usePreload)
                            {
                                await ((INextSongPreloadPlayer)player).PrepareNextAsync(NextSongPreloadInput.Capture(nextPath), _ => Assert.Fail("予期しない先読み故障です。"));
                                await preparationEntered.Task;
                                if (route != "pending") { await preparationFinished.Task; }
                            }
                            if (route == "natural") { next = await switchStarted.Task; }
                            else
                            {
                                clock.Start();
                                next = player.BeginStart(nextPath, null, allowPreload: true);
                                preparationGate.Set();
                            }
                            await next.Ready;
                            clock.Stop();
                            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { route, usePreload, round, switchMs = clock.Elapsed.TotalMilliseconds, preparationMs, parses, decodes, initializationCalls = events.Count(value => value == "initialize") }));
                            Assert.AreEqual(1, parses);
                            Assert.AreEqual(1, decodes);
                        }
                        finally
                        {
                            preparationGate.Set();
                            await player.CloseAsync();
                            try { await first.Completion; } catch (OperationCanceledException) { }
                            if (next != null) { try { await next.Completion; } catch (OperationCanceledException) { } }
                            BassAudioPlayer.Free();
                            BassAudioRuntime.Shutdown();
                        }
                    }
                }
            }
        }
        finally
        {
            BassAudioPlayer.Frequency = previousFrequency;
            Directory.Delete(directory, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task InternalSwitch_WaitsForSourceReleaseAndMatchingPreparation(bool releaseFirst)
    {
        string directory = Path.Combine(Path.GetTempPath(), "bms-switch-join-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string firstPath = WritePlaybackChart(directory, "first.bms", 400, 50);
        string nextPath = WritePlaybackChart(directory, "next.bms", 400, 1);
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var preparationGate = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        var events = new List<string>();
        int preparations = 0;
        int initializations = 0;
        bool cancelled = false;
        var runtime = new RecordingAudioPlaybackRuntime(events)
        {
            InitializeResult = CreatePlaybackInitializationResult(),
            InitializeAction = snapshot =>
            {
                Interlocked.Increment(ref initializations);
                if (BassAudioPlayer.ActiveSession == null)
                {
                    BassAudioPlayer.InitializeOwned(BassAudioPlayer.DeviceDriver.NULL_DEVICE, default, 0f, out _);
                }
            }
        };
        var player = new InternalBMSAutoPlayerSoundOnly(new SettingsPlayerSettingsGateway(() => testSettings), runtime,
            autoPlayerFactory: path => new GatedReleaseBMSAutoPlayer(new Ribbit.BMS.BMSFile(path), releaseGate, releaseEntered, releaseFinished),
            songPreparation: (input, token) =>
            {
                Interlocked.Increment(ref preparations);
                using CancellationTokenRegistration registration = token.Register(() => cancelled = true);
                preparationEntered.SetResult();
                preparationGate.Wait();
                var result = PreparedBmsSong.Prepare(new Ribbit.BMS.BMSFile(input.Path), 0.4f, token);
                preparationFinished.SetResult();
                return result;
            });
        PlaybackStartOperation first = player.BeginStart(firstPath, null, allowPreload: true);
        PlaybackStartOperation? next = null;
        try
        {
            await first.Ready;
            await ((INextSongPreloadPlayer)player).PrepareNextAsync(NextSongPreloadInput.Capture(nextPath), _ => Assert.Fail("予期しない先読み故障です。"));
            await preparationEntered.Task;
            next = player.BeginStart(nextPath, null, allowPreload: true);
            await Task.WhenAny(releaseEntered.Task, next.Ready);
            if (next.Ready.IsCompleted) { await next.Ready; }
            await releaseEntered.Task;
            Assert.IsFalse(next.Ready.IsCompleted);
            Assert.AreEqual(1, initializations);
            Assert.IsFalse(cancelled);
            if (releaseFirst)
            {
                releaseGate.Set();
                await releaseFinished.Task;
            }
            else
            {
                preparationGate.Set();
                await preparationFinished.Task;
            }
            Assert.IsFalse(next.Ready.IsCompleted);
            Assert.AreEqual(1, initializations, "One terminal alone cannot authorize output initialization.");
            releaseGate.Set();
            preparationGate.Set();
            await next.Ready;
            Assert.AreEqual(2, initializations);
            Assert.AreEqual(1, preparations);
            Assert.IsFalse(cancelled);
            await next.Completion;
        }
        finally
        {
            preparationGate.Set();
            releaseGate.Set();
            await player.CloseAsync();
            try { await first.Completion; } catch (OperationCanceledException) { }
            if (next != null) { try { await next.Completion; } catch (OperationCanceledException) { } }
            BassAudioPlayer.Free();
            BassAudioRuntime.Shutdown();
            Directory.Delete(directory, recursive: true);
        }
    }
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InternalClose_WaitsForCancelledPreparationCleanupBeforeFree(bool failCleanup)
    {
        string directory = Path.Combine(Path.GetTempPath(), "bms-close-preload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string firstPath = WritePlaybackChart(directory, "first.bms", 400, 50);
        string nextPath = WritePlaybackChart(directory, "next.bms", 400, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cleanup = new ManualResetEventSlim();
        var events = new List<string>();
        var disposeFailure = new InvalidOperationException("current player disposal failed");
        var freeFailure = new InvalidOperationException("runtime free failed");
        var runtime = new RecordingAudioPlaybackRuntime(events)
        {
            InitializeResult = CreatePlaybackInitializationResult(),
            FreeException = failCleanup ? freeFailure : null
        };
        var player = new InternalBMSAutoPlayerSoundOnly(new SettingsPlayerSettingsGateway(() => testSettings), runtime,
            autoPlayerFactory: path => new DisposeObservedBmsAutoPlayer(new Ribbit.BMS.BMSFile(path), events, failCleanup ? disposeFailure : null),
            songPreparation: (_, token) =>
            {
                entered.SetResult();
                token.WaitHandle.WaitOne();
                cancelled.SetResult();
                cleanup.Wait();
                token.ThrowIfCancellationRequested();
                throw new AssertFailedException("Cancellation must end preparation.");
            });
        PlaybackStartOperation first = player.BeginStart(firstPath, null, allowPreload: true);
        Task? close = null;
        try
        {
            await first.Ready;
            await ((INextSongPreloadPlayer)player).PrepareNextAsync(NextSongPreloadInput.Capture(nextPath), _ => Assert.Fail("予期しない先読み故障です。"));
            await entered.Task;
            close = player.CloseAsync();
            await cancelled.Task;
            Assert.IsFalse(close.IsCompleted);
            Assert.IsFalse(events.Contains("free"));
            cleanup.Set();
            if (failCleanup)
            {
                Exception? actual = null;
                try { await close; } catch (Exception failure) { actual = failure; }
                Assert.IsNotNull(actual);
                Exception[] causes = actual is AggregateException aggregate ? [.. aggregate.Flatten().InnerExceptions] : [actual];
                Assert.AreEqual(2, causes.Length);
                if (failCleanup) { CollectionAssert.Contains(causes, disposeFailure); }
                if (failCleanup) { CollectionAssert.Contains(causes, freeFailure); }
            }
            else { await close; }
            Assert.AreEqual(1, events.Count(value => value == "dispose"));
            Assert.AreEqual(1, events.Count(value => value == "free"));
        }
        finally
        {
            cleanup.Set();
            try { await (close ?? player.CloseAsync()); }
            catch (Exception) when (failCleanup) { }
            try { await first.Completion; } catch (OperationCanceledException) { }
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void PersistedAudioEnumValuesRemainStable()
    {
        AssertPersistedEnumValue(EncoderType.WAVE, 0);
        AssertPersistedEnumValue(EncoderType.MP3_LAME, 1);
        AssertPersistedEnumValue(EncoderType.AAC_NERO, 2);
        AssertPersistedEnumValue(EncoderType.OPUS, 3);
        AssertPersistedEnumValue(EncoderType.FLAC, 4);
        AssertPersistedEnumValue(EncoderType.OGG_VORBIS, 5);

        AssertPersistedEnumValue(SampleFormat.UNKNOWN, -1);
        AssertPersistedEnumValue(SampleFormat.AUTO, 0);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_INT_8BIT, 1);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_INT_16BIT, 2);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_INT_24BIT, 3);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_INT_32BIT, 4);
        AssertPersistedEnumValue(SampleFormat.SAMPLE_FLOAT_32BIT, 5);

        AssertPersistedEnumValue(SampleRate.AUTO, 0);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_11025Hz, 11025);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_22050Hz, 22050);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_32000Hz, 32000);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_44100Hz, 44100);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_48000Hz, 48000);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_88200Hz, 88200);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_96000Hz, 96000);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_176400Hz, 176400);
        AssertPersistedEnumValue(SampleRate.SAMPLE_RATE_192000Hz, 192000);

        AssertPersistedEnumValue(AudioDriver.Invalid, -2);
        AssertPersistedEnumValue(AudioDriver.NullDevice, -1);
        AssertPersistedEnumValue(AudioDriver.DirectSound, 0);
        AssertPersistedEnumValue(AudioDriver.WasapiShared, 1);
        AssertPersistedEnumValue(AudioDriver.WasapiExclusive, 2);
        AssertPersistedEnumValue(AudioDriver.Asio, 3);

        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.INVALID, -2);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.NULL_DEVICE, -1);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.DIRECT_SOUND, 0);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.WASAPI_SHARED, 1);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE, 2);
        AssertPersistedEnumValue(BassAudioPlayer.DeviceDriver.ASIO, 3);
    }

    [TestMethod]
    public void EncoderExtensionsSeparatePersistedAndPhysicalAacContracts()
    {
        CollectionAssert.AreEqual(
            new[] { ".wav", ".mp3", ".aac", ".opus", ".flac", ".ogg" },
            Enum.GetValues<EncoderType>().Select(value => value.GetExtension()).ToArray());

        Assert.AreEqual(string.Empty, EncoderType.WAVE.SearchEncoderBinary());
        CollectionAssert.AreEqual(
            new[] { ".wav", ".mp3", ".m4a", ".opus", ".flac", ".ogg" },
            Enum.GetValues<EncoderType>().Select(value => value.GetEncoderOutputExtension()).ToArray());
    }

    [TestMethod]
    public void EncoderBinarySearchHonorsConfiguredDirectory()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerEncoderContracts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            File.WriteAllText(Path.Combine(directoryPath, EncoderType.MP3_LAME.GetEncoderFileName()), string.Empty);

            Assert.AreEqual(directoryPath, EncoderType.MP3_LAME.SearchEncoderBinary(directoryPath));
            Assert.IsNull(EncoderType.OPUS.SearchEncoderBinary(directoryPath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void AudioDriverPolicyExposesThreeSelectableBackendsWithoutLegacyDisplay()
    {
        Assert.AreEqual(0, (int)AudioDriver.DirectSound);
        Assert.AreEqual(0, (int)BassAudioPlayer.DeviceDriver.DIRECT_SOUND);
        CollectionAssert.AreEqual(
            new[] { AudioDriver.WasapiShared, AudioDriver.WasapiExclusive, AudioDriver.Asio },
            AudioDriverPolicy.SelectableDrivers.ToArray());
        Assert.AreEqual(AudioDriver.WasapiShared, AudioDriverPolicy.DefaultDriver);
        Assert.AreEqual(
            "WASAPI (" + BeMusicSeeker.Properties.Resources.Shared + ")",
            AudioDriverDisplayNames.Get(AudioDriver.DirectSound));
        string legacyDisplayName = AudioDriverDisplayNames.Get(AudioDriver.DirectSound);
        Assert.IsFalse(legacyDisplayName.Contains("BASS", System.StringComparison.Ordinal));
        Assert.IsFalse(legacyDisplayName.Contains("DirectSound", System.StringComparison.Ordinal));
    }

    [TestMethod]
    public void AudioDriverPolicy_NormalizesOnlyLegacyDirectSound()
    {
        AudioOutputSelection normalized = AudioDriverPolicy.NormalizePersistedSelection(
            new AudioOutputSelection(AudioDriver.DirectSound, "legacy-id", "Legacy name"));

        Assert.AreEqual(AudioDriver.WasapiShared, normalized.Backend);
        Assert.IsTrue(normalized.IsDefault);
        Assert.IsNull(normalized.DeviceIdentity);
        Assert.IsNull(normalized.DeviceName);

        AudioOutputSelection unknown = new((AudioDriver)47, "unknown-id", "Unknown name");
        AudioOutputSelection invalid = new(AudioDriver.Invalid, "invalid-id", "Invalid name");
        AudioOutputSelection nullDevice = new(AudioDriver.NullDevice, "null-id", "Null name");
        Assert.AreEqual(unknown, AudioDriverPolicy.NormalizePersistedSelection(unknown));
        Assert.AreEqual(invalid, AudioDriverPolicy.NormalizePersistedSelection(invalid));
        Assert.AreEqual(nullDevice, AudioDriverPolicy.NormalizePersistedSelection(nullDevice));

        Assert.AreEqual(0, AudioDriverPolicy.IndexOf(AudioDriver.WasapiShared));
        Assert.AreEqual(1, AudioDriverPolicy.IndexOf(AudioDriver.WasapiExclusive));
        Assert.AreEqual(2, AudioDriverPolicy.IndexOf(AudioDriver.Asio));
        Assert.AreEqual(-1, AudioDriverPolicy.IndexOf(AudioDriver.DirectSound));
    }

    [TestMethod]
    public void AudioOutputRequest_EqualityIncludesEveryOutputConditionButNotVolume()
    {
        PlayerSettingsSnapshot baselineSettings = CreatePlayerSettings(AudioDriver.WasapiExclusive);
        AudioOutputRequest baseline = baselineSettings.AudioOutputRequest;

        Assert.IsTrue(baseline.Equals(CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            playerVolume: 87).AudioOutputRequest));
        Assert.IsFalse(baseline.Equals(CreatePlayerSettings(
            AudioDriver.Asio,
            deviceIdentity: "device-a",
            deviceName: "Device A").AudioOutputRequest));
        Assert.IsFalse(baseline.Equals(CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            playerSampleRate: SampleRate.SAMPLE_RATE_44100Hz).AudioOutputRequest));
        Assert.IsFalse(baseline.Equals(CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            playerFormat: SampleFormat.SAMPLE_INT_24BIT).AudioOutputRequest));
        Assert.IsFalse(baseline.Equals(CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            playerBufferSize: 20).AudioOutputRequest));
        Assert.IsFalse(baseline.Equals(CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            playerWasapiParam: true).AudioOutputRequest));
        Assert.IsFalse(baseline.Equals(CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            sampleRateConversionQuality: 3).AudioOutputRequest));
        Assert.IsTrue(baseline.Equals(CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            sampleRateConversionQuality: 2).AudioOutputRequest));
        Assert.IsFalse(baseline.Equals(CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            playerMixerThreadCount: 2).AudioOutputRequest));
        Assert.AreEqual(AudioOutputPurpose.Playback, baseline.Purpose);
        Assert.AreEqual(1, baseline.PlayerMixerThreadCount);
    }

    [TestMethod]
    public void OutputRequests_IgnoreNonEditableModeConditionsWithoutChangingSavedIntent()
    {
        PlayerSettingsSnapshot shared = CreatePlayerSettings(AudioDriver.WasapiShared,
            playerSampleRate: SampleRate.SAMPLE_RATE_44100Hz,
            playerFormat: SampleFormat.SAMPLE_INT_16BIT, playerBufferSize: 20);
        Assert.AreEqual(SampleRate.AUTO, shared.AudioOutputRequest.Rate);
        Assert.AreEqual(SampleFormat.AUTO, shared.AudioOutputRequest.Format);
        Assert.AreEqual(0f, shared.AudioOutputRequest.BufferSize);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, shared.PlayerSampleRate);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, shared.PlayerFormat);
        Assert.AreEqual(20f, shared.PlayerBufferSize);
        PlayerSettingsSnapshot asio = CreatePlayerSettings(AudioDriver.Asio,
            playerSampleRate: SampleRate.SAMPLE_RATE_44100Hz,
            playerFormat: SampleFormat.SAMPLE_INT_16BIT, playerWasapiParam: true);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, asio.AudioOutputRequest.Rate);
        Assert.AreEqual(SampleFormat.AUTO, asio.AudioOutputRequest.Format);
        Assert.IsFalse(asio.AudioOutputRequest.EventMode);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, asio.PlayerFormat);
        Assert.IsTrue(asio.PlayerWASAPIParam);

        void AssertNoFallback(PlayerSettingsSnapshot settings, SampleRate actualRate)
        {
            BassAudioPlayer.DeviceDriver backend = BassAudioMapping.ToBassDriver(settings.PlayerDriver);
            var device = new BassAudioPlayer.DeviceDescriptor("Selected device", "selected-id");
            var session = new BassAudioSession(backend) { ActualBackend = backend, ActualDevice = device };
            var negotiated = new BassAudioBackendResult(
                new BassAudioNegotiationRequest(backend, device, settings.AudioOutputRequest.Rate,
                    settings.AudioOutputRequest.Format, settings.AudioOutputRequest.BufferSize,
                    playerMixerThreadCount: settings.AudioOutputRequest.PlayerMixerThreadCount),
                device, actualRate, SampleFormat.SAMPLE_FLOAT_32BIT, SampleFormat.SAMPLE_FLOAT_32BIT,
                10, 1, [], null);
            AudioPlaybackInitializationResult result = BassAudioPlaybackRuntime.CreateInitializationResult(
                settings.AudioOutputRequest, settings.PlayerVolume, session, negotiated);
            Assert.IsFalse(result.FallbackOccurred);
            Assert.AreEqual(settings.AudioOutputRequest.Rate, result.RequestedRate);
            Assert.AreEqual(SampleFormat.AUTO, result.RequestedFormat);
            Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, settings.PlayerSampleRate);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, settings.PlayerFormat);
        }
        AssertNoFallback(shared, SampleRate.SAMPLE_RATE_48000Hz);
        AssertNoFallback(asio, SampleRate.SAMPLE_RATE_44100Hz);
    }

    [TestMethod]
    public void SettingsAudioGateway_PreservesKnownAndUnknownPersistedValues()
    {
        BeMusicSeeker.Properties.Settings settings = testSettings;
        BassAudioPlayer.DeviceDriver originalDriver = settings.PlayerDriver;
        string originalDevice = settings.PlayerDevice;
        string originalDeviceName = settings.PlayerDeviceName;
        BMSAutoPlayWriter.Normalization originalNormalization = settings.EncoderNormalization;
        EncoderType originalEncoder = settings.Encoder;
        try
        {
            settings.PlayerDriver = Ribbit.Media.BassAudioPlayer.DeviceDriver.DIRECT_SOUND;
            settings.PlayerDevice = "legacy-device";
            settings.PlayerDeviceName = "Legacy Device";
            SettingsAudioGateway gateway = new(() => settings);

            AudioOutputSelection selection = gateway.CaptureOutputSelection();
            Assert.AreEqual(AudioDriver.WasapiShared, selection.Backend);
            Assert.IsTrue(selection.IsDefault);
            Assert.AreEqual(Ribbit.Media.BassAudioPlayer.DeviceDriver.DIRECT_SOUND, settings.PlayerDriver);

            settings.PlayerDriver = (Ribbit.Media.BassAudioPlayer.DeviceDriver)47;
            settings.PlayerDevice = "persisted-device";
            settings.PlayerDeviceName = "Persisted Device";
            settings.EncoderNormalization = (Ribbit.BMS.BMSAutoPlayWriter.Normalization)53;

            selection = gateway.CaptureOutputSelection();
            Assert.AreEqual((AudioDriver)47, selection.Backend);
            Assert.AreEqual("persisted-device", selection.DeviceIdentity);
            Assert.AreEqual("Persisted Device", selection.DeviceName);
            Assert.AreEqual((AudioNormalization)53, gateway.EncoderNormalization);

            gateway.ApplyOutputSelection(new AudioOutputSelection(
                (AudioDriver)61,
                "replacement-device",
                "Replacement Device"));
            gateway.EncoderNormalization = (AudioNormalization)67;
            gateway.ApplyEncoderFallback(Ribbit.Media.Audio.EncoderType.FLAC);

            Assert.AreEqual((Ribbit.Media.BassAudioPlayer.DeviceDriver)61, settings.PlayerDriver);
            Assert.AreEqual("replacement-device", settings.PlayerDevice);
            Assert.AreEqual("Replacement Device", settings.PlayerDeviceName);
            Assert.AreEqual((Ribbit.BMS.BMSAutoPlayWriter.Normalization)67, settings.EncoderNormalization);
            Assert.AreEqual(Ribbit.Media.Audio.EncoderType.FLAC, settings.Encoder);
        }
        finally
        {
            settings.PlayerDriver = originalDriver;
            settings.PlayerDevice = originalDevice;
            settings.PlayerDeviceName = originalDeviceName;
            settings.EncoderNormalization = originalNormalization;
            settings.Encoder = originalEncoder;
        }
    }

    [TestMethod]
    public void SettingsAudioGateway_CapturesEncodingSettingsWithoutBmsTypes()
    {
        BeMusicSeeker.Properties.Settings settings = testSettings;
        BMSAutoPlayWriter.Normalization originalNormalization = settings.EncoderNormalization;
        int originalResamplingQuality = settings.PlayerResamplingQuality;
        try
        {
            settings.EncoderNormalization = Ribbit.BMS.BMSAutoPlayWriter.Normalization.RMS_VALUE;
            settings.PlayerResamplingQuality = 2;
            SettingsAudioGateway gateway = new(() => settings);

            AudioEncodingSettingsSnapshot snapshot = gateway.CaptureEncodingSettings();

            Assert.AreEqual(AudioNormalization.RmsValue, snapshot.EncoderNormalization);
            Assert.AreEqual(settings.Encoder, snapshot.Encoder);
            Assert.AreEqual(settings.EncoderSampleRate, snapshot.EncoderSampleRate);
            Assert.AreEqual(settings.EncoderFormat, snapshot.EncoderFormat);
            Assert.AreEqual(settings.EncoderExeDir, snapshot.EncoderExeDirectory);
            Assert.AreEqual(settings.EncodeFileNameFormat, snapshot.EncodeFileNameFormat);
            Assert.AreEqual(2, snapshot.SampleRateConversionQuality);
        }
        finally
        {
            settings.EncoderNormalization = originalNormalization;
            settings.PlayerResamplingQuality = originalResamplingQuality;
        }
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(7)]
    public void AudioResamplingQuality_RejectsUnsupportedPersistedValues(int quality)
    {
        BeMusicSeeker.Properties.Settings settings = testSettings;
        int originalResamplingQuality = settings.PlayerResamplingQuality;
        try
        {
            settings.PlayerResamplingQuality = quality;

            Assert.ThrowsException<ArgumentOutOfRangeException>(
                () => new SettingsAudioGateway(() => settings).CaptureEncodingSettings());
            Assert.ThrowsException<ArgumentOutOfRangeException>(
                () => new SettingsPlayerSettingsGateway(() => settings).CaptureSnapshot());
        }
        finally
        {
            settings.PlayerResamplingQuality = originalResamplingQuality;
        }
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(5)]
    public void PlayerMixerThreadCount_RejectsUnsupportedPersistedValues(int threadCount)
    {
        BeMusicSeeker.Properties.Settings settings = testSettings;
        int originalThreadCount = settings.PlayerMixerThreadCount;
        try
        {
            settings.PlayerMixerThreadCount = threadCount;

            Assert.ThrowsException<ArgumentOutOfRangeException>(
                () => new SettingsPlayerSettingsGateway(() => settings).CaptureSnapshot());
        }
        finally
        {
            settings.PlayerMixerThreadCount = originalThreadCount;
        }
    }

    [TestMethod]
    public void AudioResamplingQualityAndMixerThreads_UseTheMissingSettingDefaults()
    {
        var settings = new BeMusicSeeker.Properties.Settings();

        Assert.AreEqual(2, settings.PlayerResamplingQuality);
        Assert.AreEqual(1, settings.PlayerMixerThreadCount);
        CollectionAssert.AreEqual(new[] { 16, 32, 64, 128, 256 },
            new[] { 2, 3, 4, 5, 6 }
                .Select(AudioResamplingQuality.GetSincPointCount)
                .ToArray());
    }

    [TestMethod]
    public void AudioDeviceInfo_PreservesDeviceIdentityAndDefaultDisplayName()
    {
        AudioDeviceInfo defaultDevice = new(null, null);
        AudioDeviceInfo namedDevice = new("device-name", "driver-id");

        Assert.AreEqual(BeMusicSeeker.Properties.Resources.AudioDeviceDefault, defaultDevice.FriendlyName);
        Assert.IsNull(defaultDevice.Driver);
        Assert.AreEqual("device-name", namedDevice.FriendlyName);
        Assert.AreEqual("driver-id", namedDevice.Driver);
        Assert.AreEqual("device-name", namedDevice.Name);
    }

    [TestMethod]
    public void NativeErrorFormatter_PreservesLegacyBassSpellings()
    {
        Assert.AreEqual("BASS_ERROR_MEM", BassNativeErrorFormatter.Format(Errors.Memory));
        Assert.AreEqual("BASS_ERROR_NOPAUSE", BassNativeErrorFormatter.Format(Errors.NotPaused));
        Assert.AreEqual("BASS_ERROR_ILLTYPE", BassNativeErrorFormatter.Format(Errors.Type));
        Assert.AreEqual("BASS_ERROR_NOPLAY", BassNativeErrorFormatter.Format(Errors.NotPlaying));
    }

    [TestMethod]
    public void InternalPlayer_DelegatesVolumeAndCloseLifecycleToPlaybackRuntime()
    {
        BeMusicSeeker.Properties.Settings settings = testSettings;
        int originalVolume = settings.uBMplayVolume;
        try
        {
            settings.uBMplayVolume = 123;
            var events = new List<string>();
            var runtime = new RecordingAudioPlaybackRuntime(events);
            var player = new InternalBMSAutoPlayerSoundOnly(
                new SettingsPlayerSettingsGateway(() => settings),
                runtime);

            player.VolumeChanged();
            player.CloseProcess();

            CollectionAssert.AreEqual(
                new[] { "volume:123", "clear-max-voices", "free" },
                events);
        }
        finally
        {
            settings.uBMplayVolume = originalVolume;
        }
    }

    [TestMethod]
    public async Task InternalPlayer_StartFailureCleansUpPlaybackRuntime()
    {
        string bmsFilePath = Path.GetTempFileName();
        try
        {
            var events = new List<string>();
            var startupFailure = new InvalidOperationException("playback initialization failed");
            var runtime = new RecordingAudioPlaybackRuntime(events)
            {
                InitializeException = startupFailure,
            };
            var player = new InternalBMSAutoPlayerSoundOnly(
                new SettingsPlayerSettingsGateway(() => testSettings),
                runtime);

            InvalidOperationException actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => player.PlayStart(bmsFilePath));

            Assert.AreSame(startupFailure, actual);
            CollectionAssert.AreEqual(
                new[] { "initialize", "clear-max-voices", "free" },
                events);
        }
        finally
        {
            File.Delete(bmsFilePath);
        }
    }

    [TestMethod]
    public async Task InternalPlayer_LateCancelledStartDoesNotCloseTheReplacementSong()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string oldPath = WritePlaybackChart(directory, "old.bms", bpm: 400, measure: 50);
        string newPath = WritePlaybackChart(directory, "new.bms", bpm: 400, measure: 50);
        string candidatePath = WritePlaybackChart(directory, "candidate.bms", bpm: 400, measure: 50);
        var oldDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepareEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepareEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepareCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var prepareGate = new ManualResetEventSlim();
        bool candidateCancelled = false;
        var events = new List<string>();
        var runtime = new RecordingAudioPlaybackRuntime(events)
        {
            InitializeResult = CreatePlaybackInitializationResult(),
        };
        var player = new InternalBMSAutoPlayerSoundOnly(
            new SettingsPlayerSettingsGateway(() => testSettings),
            runtime,
            autoPlayerFactory: path => path == oldPath
                ? new DelayedCompletionBmsAutoPlayer(new Ribbit.BMS.BMSFile(path), oldDelivery.Task, oldTerminal)
                : new LifecycleBMSAutoPlayer(new Ribbit.BMS.BMSFile(path)),
            songPreparation: (_, token) =>
            {
                using CancellationTokenRegistration registration = token.Register(() => { candidateCancelled = true; prepareCancelled.TrySetResult(); });
                prepareEntered.TrySetResult();
                try { prepareGate.Wait(); token.ThrowIfCancellationRequested(); throw new AssertFailedException("cleanupで取消します。"); }
                finally { prepareEnded.TrySetResult(); }
            });
        int oldCallbackCount = 0;
        int newCallbackCount = 0;
        PlaybackStartOperation oldOperation = player.BeginStart(oldPath, (_, _) => oldCallbackCount++, allowPreload: true);
        Task oldStart = oldOperation.Completion;
        await oldOperation.Ready;
        Task newStart = Task.CompletedTask;

        var failures = new List<Exception>();
        try
        {
            PlaybackStartOperation newOperation = player.BeginStart(newPath, (_, _) => newCallbackCount++, allowPreload: true);
            newStart = newOperation.Completion;
            await newOperation.Ready;
            Assert.IsTrue(player.Duration > TimeSpan.Zero);
            await ObserveSignalOrFailureAsync(oldTerminal.Task, newOperation.Completion);
            Assert.IsFalse(oldOperation.Completion.IsCompleted, "旧故障の配送だけをTask境界で遅延します。");
            await ((INextSongPreloadPlayer)player).PrepareNextAsync(NextSongPreloadInput.Capture(candidatePath), _ => Assert.Fail("予期しない先読み故障です。"));
            await ObserveSignalOrFailureAsync(prepareEntered.Task, newOperation.Completion);
            oldDelivery.TrySetResult();

            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => oldStart);

            Assert.IsTrue(player.Duration > TimeSpan.Zero, "The old start's delayed cleanup must leave the new song active.");
            Assert.AreEqual(0, oldCallbackCount);
            Assert.AreEqual(0, newCallbackCount);
            Assert.IsFalse(candidateCancelled, "交換済み旧playerの故障は新曲の先読みを取り消しません。");
            Assert.IsFalse(prepareEnded.Task.IsCompleted);
            Assert.AreEqual(0, events.Count(value => value == "free"));
            Assert.IsFalse(newStart.IsCompleted);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        oldDelivery.TrySetResult();
        Task close = player.CloseAsync();
        try
        {
            if (prepareEntered.Task.IsCompletedSuccessfully) { await ObserveSignalOrFailureAsync(prepareCancelled.Task, close); }
        }
        catch (Exception failure) { failures.Add(failure); }
        finally { prepareGate.Set(); }
        await CaptureTaskCompletionAsync(close, failures);
        await CaptureTaskCompletionAsync(oldStart, failures, exception => exception is OperationCanceledException);
        await CaptureTaskCompletionAsync(newStart, failures, exception => exception is OperationCanceledException);
        CaptureCleanup(failures, player.CloseProcess);
        CaptureCleanup(failures, () => Directory.Delete(directory, recursive: true));
        ThrowFailures(failures);
    }

    [TestMethod]
    public async Task InternalPlayer_ReleasesOldPlayerBeforeChangingOutputSession()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string oldPath = WritePlaybackChart(directory, "old.bms", bpm: 400, measure: 50);
        string newPath = WritePlaybackChart(directory, "new.bms", bpm: 400, measure: 1);
        var events = new List<string>();
        var initializedSessions = new List<BassAudioSession>();
        BeMusicSeeker.Properties.Settings settings = CreateAudiblePlaybackSettings(SampleRate.SAMPLE_RATE_44100Hz);
        BassAudioPlaybackRuntime runtime = CreateInjectedBassAudioPlaybackRuntime(events, initializedSessions);
        ObservingBMSAutoPlayer? oldPlayer = null;
        int factoryCalls = 0;
        var player = new InternalBMSAutoPlayerSoundOnly(
            new SettingsPlayerSettingsGateway(() => settings),
            runtime,
            autoPlayerFactory: path =>
            {
                factoryCalls++;
                events.Add("player-factory:" + factoryCalls);
                if (oldPlayer == null)
                {
                    oldPlayer = new ObservingBMSAutoPlayer(new Ribbit.BMS.BMSFile(path), events);
                    return oldPlayer;
                }
                return new LifecycleBMSAutoPlayer(new Ribbit.BMS.BMSFile(path));
            });
        PlaybackStartOperation oldOperation = player.BeginStart(oldPath, null, allowPreload: true);
        Task oldStart = oldOperation.Completion;
        await oldOperation.Ready;
        Task newStart = Task.CompletedTask;

        var failures = new List<Exception>();
        try
        {
            Assert.AreEqual(1, initializedSessions.Count);
            ObservingBMSAutoPlayer observedOldPlayer = oldPlayer
                ?? throw new AssertFailedException("The first BMS player was not created.");
            var userSeekTime = TimeSpan.FromMilliseconds(250);
            int moveCountBeforeUserSeek = observedOldPlayer.MoveToCount;
            player.CurrentTime = userSeekTime;
            Assert.AreEqual(moveCountBeforeUserSeek + 1, observedOldPlayer.MoveToCount,
                "公開CurrentTime setterは再生中の譜面へseekを届けます。");
            Assert.AreEqual(userSeekTime, observedOldPlayer.LastMoveToTime);
            int oldMoveCountBeforeReplacement = observedOldPlayer.MoveToCount;

            AudioOutputRequest firstRequest = initializedSessions[0].OutputRequest;
            settings.PlayerSampleRate = SampleRate.SAMPLE_RATE_48000Hz;

            PlaybackStartOperation newOperation = player.BeginStart(newPath, null, allowPreload: true);
            newStart = newOperation.Completion;
            await newOperation.Ready;

            Assert.AreEqual(oldMoveCountBeforeReplacement, observedOldPlayer.MoveToCount,
                "次曲切替時の表示リセットは旧曲へのseekを発生させません。");
            Assert.AreEqual(2, initializedSessions.Count);
            Assert.IsFalse(firstRequest.Equals(initializedSessions[1].OutputRequest));
            CollectionAssert.AreEqual(
                new[]
                {
                    "native-initialize:1",
                    "player-factory:1",
                    "old-stop-joined",
                    "old-source-release-complete",
                    "session-release:1",
                    "native-initialize:2",
                    "player-factory:2",
                },
                events);
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => oldStart);
            await newStart;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (!oldStart.IsCompleted || !newStart.IsCompleted)
        {
            CaptureCleanup(failures, player.CloseProcess);
        }
        await CaptureTaskCompletionAsync(oldStart, failures, exception => exception is OperationCanceledException);
        await CaptureTaskCompletionAsync(newStart, failures, exception => exception is OperationCanceledException);
        CaptureCleanup(failures, player.CloseProcess);
        CaptureCleanup(failures, () => Directory.Delete(directory, recursive: true));
        ThrowFailures(failures);
    }

    [TestMethod]
    public async Task InternalPlayer_SourceReleaseFailureStopsBeforeCallingNextPlayerFactory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string oldPath = WritePlaybackChart(directory, "old.bms", bpm: 400, measure: 1);
        string invalidNextPath = Path.Combine(directory, "invalid-next.bms");
        File.WriteAllText(invalidNextPath, "not a BMS chart", System.Text.Encoding.ASCII);
        var expectedFailure = new BassAudioPlaybackException(
            BassAudioPlaybackStage.SourceRelease,
            oldPath,
            41,
            17,
            0,
            "BASS_StreamFree",
            nativeErrorCode: null,
            message: BeMusicSeeker.Properties.Resources.AudioDeviceTestCleanupFailure);
        var events = new List<string>();
        var initializedSessions = new List<BassAudioSession>();
        BeMusicSeeker.Properties.Settings settings = CreateAudiblePlaybackSettings(SampleRate.SAMPLE_RATE_44100Hz);
        ThrowingReleaseBMSAutoPlayer? oldPlayer = null;
        int factoryCalls = 0;
        BassAudioPlaybackRuntime runtime = CreateInjectedBassAudioPlaybackRuntime(events, initializedSessions);
        var player = new InternalBMSAutoPlayerSoundOnly(
            new SettingsPlayerSettingsGateway(() => settings),
            runtime,
            autoPlayerFactory: path =>
            {
                factoryCalls++;
                events.Add("player-factory:" + factoryCalls);
                if (oldPlayer != null)
                {
                    return new LifecycleBMSAutoPlayer(new Ribbit.BMS.BMSFile(path));
                }
                oldPlayer = new ThrowingReleaseBMSAutoPlayer(
                    new Ribbit.BMS.BMSFile(path), expectedFailure, events);
                return oldPlayer;
            });
        PlaybackStartOperation oldOperation = player.BeginStart(oldPath, null, allowPreload: true);
        Task oldStart = oldOperation.Completion;
        await oldOperation.Ready;
        Task rejectedStart = Task.CompletedTask;

        var failures = new List<Exception>();
        try
        {
            await oldStart;
            Assert.AreEqual(1, initializedSessions.Count);
            settings.PlayerSampleRate = SampleRate.SAMPLE_RATE_48000Hz;

            rejectedStart = player.PlayStart(invalidNextPath);
            BassAudioPlaybackException actual = await Assert.ThrowsExceptionAsync<BassAudioPlaybackException>(
                () => rejectedStart);

            Assert.AreSame(expectedFailure, actual);
            Assert.AreEqual(BassAudioPlaybackStage.SourceRelease, actual.Stage);
            Assert.AreEqual(1, factoryCalls, "The next BMS parser/player factory must not run after old-source release fails.");
            Assert.AreEqual(1, initializedSessions.Count, "A failed old-source release must not initialize the changed output request.");
            CollectionAssert.AreEqual(
                new[]
                {
                    "native-initialize:1",
                    "player-factory:1",
                    "old-stop-joined",
                    "old-source-release-complete",
                    "session-release:1",
                },
                events);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (!oldStart.IsCompleted || !rejectedStart.IsCompleted)
        {
            CaptureCleanup(failures, player.CloseProcess);
        }
        await CaptureTaskCompletionAsync(oldStart, failures, exception => exception is OperationCanceledException);
        await CaptureTaskCompletionAsync(rejectedStart, failures, exception => ReferenceEquals(exception, expectedFailure));
        if (oldPlayer != null)
        {
            CaptureCleanup(failures, () => oldPlayer.FailNextRelease = false);
            CaptureCleanup(failures, oldPlayer.Dispose);
        }
        CaptureCleanup(failures, player.CloseProcess);
        CaptureCleanup(failures, () => Directory.Delete(directory, recursive: true));
        ThrowFailures(failures);
    }

    [TestMethod]
    public async Task InternalPlayer_DispatchesTheCapturedExitCallbackAfterTheNextSongStarts()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string oldPath = WritePlaybackChart(directory, "old.bms", bpm: 400, measure: 1);
        string newPath = WritePlaybackChart(directory, "new.bms", bpm: 400, measure: 1);
        var capturedExit = new TaskCompletionSource<(Action<object, EventArgs> Callback, object Sender)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var runtime = new RecordingAudioPlaybackRuntime(events)
        {
            InitializeResult = CreatePlaybackInitializationResult(),
        };
        var player = new InternalBMSAutoPlayerSoundOnly(
            new SettingsPlayerSettingsGateway(() => testSettings),
            runtime,
            (callback, sender) => capturedExit.TrySetResult((callback, sender)),
            path => new LifecycleBMSAutoPlayer(new Ribbit.BMS.BMSFile(path)));
        int generation = 1;
        int oldCallbackCount = 0;
        int newCallbackCount = 0;
        Action<object, EventArgs> oldCallback = (_, _) =>
        {
            if (generation == 1)
            {
                oldCallbackCount++;
                player.CloseProcess();
            }
        };
        PlaybackStartOperation oldOperation = player.BeginStart(oldPath, oldCallback, allowPreload: true);
        Task oldStart = oldOperation.Completion;
        await oldOperation.Ready;
        Task newStart = Task.CompletedTask;

        var failures = new List<Exception>();
        try
        {
            await oldStart;
            (Action<object, EventArgs> callback, object sender) = await capturedExit.Task;
            Assert.AreSame(oldCallback, callback);

            generation = 2;
            PlaybackStartOperation newOperation = player.BeginStart(newPath, (_, _) => newCallbackCount++, allowPreload: true);
            newStart = newOperation.Completion;
            await newOperation.Ready;
            TimeSpan newDuration = player.Duration;
            Assert.IsTrue(newDuration > TimeSpan.Zero);

            callback(sender, EventArgs.Empty);

            Assert.AreEqual(0, oldCallbackCount, "The application's existing generation guard must ignore the stale exit.");
            Assert.AreEqual(0, newCallbackCount, "The captured old callback must not dispatch the new callback.");
            Assert.AreEqual(newDuration, player.Duration, "A stale exit must not close the replacement song.");
            await newStart;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        CaptureCleanup(failures, () => capturedExit.TrySetCanceled());
        await CaptureTaskCompletionAsync(capturedExit.Task, failures, exception => exception is OperationCanceledException);
        if (!oldStart.IsCompleted || !newStart.IsCompleted)
        {
            CaptureCleanup(failures, player.CloseProcess);
        }
        await CaptureTaskCompletionAsync(oldStart, failures, exception => exception is OperationCanceledException);
        await CaptureTaskCompletionAsync(newStart, failures, exception => exception is OperationCanceledException);
        CaptureCleanup(failures, player.CloseProcess);
        CaptureCleanup(failures, () => Directory.Delete(directory, recursive: true));
        ThrowFailures(failures);
    }

    [TestMethod]
    public void PlaybackInitializationResult_SeparatesRequestedAndNegotiatedValues()
    {
        var result = new AudioPlaybackInitializationResult(
            AudioDriver.Asio,
            "asio-requested",
            "Requested ASIO",
            SampleRate.SAMPLE_RATE_96000Hz,
            SampleFormat.SAMPLE_INT_24BIT,
            12,
            true,
            73,
            AudioDriver.WasapiShared,
            "wasapi-actual",
            "Actual WASAPI",
            SampleRate.SAMPLE_RATE_48000Hz,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            SampleFormat.SAMPLE_INT_16BIT,
            6,
            18.5,
            "attemptedBackend=ASIO; fallbackDestination=WASAPI_SHARED",
            isSilentFallback: false,
            endpointContainerBits: 16,
            endpointEffectiveBits: 16,
            attempts:
            [
                new BassAudioBackendAttempt("BASS_ASIO_ChannelGetFormat", "BASSASIO", null, "mismatched"),
                new BassAudioBackendAttempt("BASS_WASAPI_GetInfo", "BASSWASAPI", null, "readback")
            ]);

        Assert.AreEqual(AudioDriver.Asio, result.RequestedBackend);
        Assert.AreEqual(AudioDriver.WasapiShared, result.ActualBackend);
        Assert.AreEqual("asio-requested", result.RequestedDevice);
        Assert.AreEqual("wasapi-actual", result.ActualDevice);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_96000Hz, result.RequestedRate);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_48000Hz, result.ActualRate);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_24BIT, result.RequestedFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EngineFormat);
        Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, result.EndpointFormat);
        Assert.AreEqual(16, result.EndpointContainerBits);
        Assert.AreEqual(16, result.EndpointEffectiveBits);
        Assert.AreEqual(2, result.Attempts.Count);
        Assert.AreEqual("BASS_ASIO_ChannelGetFormat", result.Attempts[0].Stage);
        Assert.AreEqual("BASS_WASAPI_GetInfo", result.Attempts[1].Stage);
        Assert.AreEqual(6, result.ActualChannels);
        Assert.IsTrue(result.FallbackOccurred);
        Assert.IsFalse(result.IsSilentFallback);
        StringAssert.Contains(result.FallbackReason, "fallbackDestination=WASAPI_SHARED");
    }

    [TestMethod]
    public void UnknownWasapiEndpointFormat_DoesNotImplyFallback()
    {
        var result = new AudioPlaybackInitializationResult(
            AudioDriver.WasapiShared,
            string.Empty,
            "Default WASAPI",
            SampleRate.AUTO,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            10,
            false,
            50,
            AudioDriver.WasapiShared,
            string.Empty,
            "Default WASAPI",
            SampleRate.SAMPLE_RATE_48000Hz,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            SampleFormat.UNKNOWN,
            2,
            10,
            null,
            isSilentFallback: false);

        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, result.EngineFormat);
        Assert.AreEqual(SampleFormat.UNKNOWN, result.EndpointFormat);
        Assert.IsFalse(result.FallbackOccurred);
    }

    [TestMethod]
    public void PlaybackRuntime_NullDeviceRequestFailsBeforeAudibleInitialization()
    {
        var runtime = new BassAudioPlaybackRuntime();
        PlayerSettingsSnapshot settings = CreatePlayerSettings(AudioDriver.NullDevice);

        AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
            () => runtime.Initialize(settings));

        Assert.AreEqual(BassAudioPlayer.DeviceDriver.NULL_DEVICE, exception.RequestedBackend);
        Assert.AreEqual(BassAudioPlayer.DeviceDriver.INVALID, exception.ActualBackend);
        Assert.AreEqual("backend selection", exception.Stage);
        Assert.AreNotEqual(BassAudioPlayer.DeviceDriver.DIRECT_SOUND, exception.ActualBackend);
    }

    [TestMethod]
    public async Task PlaybackRuntime_TransientAudioRequestBusyIsTypedAndHasNoNativeSideEffects()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = Task.Run(async () =>
        {
            try
            {
                if (!BassAudioRuntime.TryEnterAudioRequest(out IDisposable admission))
                {
                    throw new InvalidOperationException("The audio request gate was not available for the test blocker.");
                }
                using (admission)
                {
                    accepted.TrySetResult();
                    await release.Task;
                }
            }
            catch (Exception exception)
            {
                accepted.TrySetException(exception);
                throw;
            }
        });
        int initializeCalls = 0;
        int releaseCalls = 0;
        var runtime = new BassAudioPlaybackRuntime(
            (_, _) => initializeCalls++,
            _ =>
            {
                releaseCalls++;
                return true;
            });
        PlayerSettingsSnapshot settings = CreatePlayerSettings(
            AudioDriver.WasapiExclusive,
            deviceIdentity: "requested-device-id",
            deviceName: "Requested device",
            playerSampleRate: SampleRate.SAMPLE_RATE_44100Hz,
            playerFormat: SampleFormat.SAMPLE_INT_24BIT);

        try
        {
            await accepted.Task;

            AudioInitializationException exception = Assert.ThrowsException<AudioInitializationException>(
                () => runtime.Initialize(settings));

            Assert.AreEqual("audio request busy", exception.Stage);
            Assert.AreEqual("BassAudioOperationGate", exception.NativeErrorSource);
            Assert.AreEqual(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE, exception.RequestedBackend);
            Assert.AreEqual("requested-device-id", exception.RequestedDevice.Driver);
            Assert.AreEqual("Requested device", exception.RequestedDevice.Name);
            Assert.AreEqual(0, initializeCalls);
            Assert.AreEqual(0, releaseCalls);
        }
        finally
        {
            release.TrySetResult();
            await blocker;
        }
    }

    [TestMethod]
    public void PlaybackRuntime_ReleasesOldSessionWhenNextStartCapturesDifferentAudioOutputSettings()
    {
        var initializedSessions = new List<BassAudioSession>();
        var releaseAttempts = new List<BassAudioSession>();
        bool confirmRelease = false;
        var runtime = new BassAudioPlaybackRuntime(
            (settings, captureSession) =>
            {
                BassAudioPlayer.DeviceDriver backend = BassAudioMapping.ToBassDriver(settings.PlayerDriver);
                var device = new BassAudioPlayer.DeviceDescriptor("Test endpoint", "test-endpoint");
                var session = new BassAudioSession(
                    backend,
                    device,
                    settings.SampleRateConversionQuality,
                    settings.AudioOutputRequest)
                {
                    ActualBackend = backend,
                    ActualDevice = device,
                    State = BassAudioSessionState.Active,
                    NegotiationResult = new BassAudioBackendResult(
                        new BassAudioNegotiationRequest(
                            backend,
                            device,
                            settings.PlayerSampleRate,
                            settings.PlayerFormat,
                            settings.PlayerBufferSize,
                            playerMixerThreadCount: settings.AudioOutputRequest.PlayerMixerThreadCount),
                        device,
                        SampleRate.SAMPLE_RATE_48000Hz,
                        SampleFormat.SAMPLE_FLOAT_32BIT,
                        SampleFormat.SAMPLE_FLOAT_32BIT,
                        10,
                        1,
                        [],
                        null)
                };
                initializedSessions.Add(session);
                captureSession(session);
            },
            session =>
            {
                releaseAttempts.Add(session);
                if (!confirmRelease)
                {
                    return false;
                }

                session.State = BassAudioSessionState.Released;
                return true;
            });

        PlayerSettingsSnapshot originalSettings = CreatePlayerSettings(
            AudioDriver.WasapiShared,
            sampleRateConversionQuality: 4,
            playerMixerThreadCount: 2);
        AudioPlaybackInitializationResult original = runtime.Initialize(originalSettings);
        Assert.AreSame(original, runtime.Initialize(originalSettings));
        Assert.AreEqual(1, initializedSessions.Count);
        Assert.AreEqual(2, initializedSessions[0].OutputRequest.PlayerMixerThreadCount);
        Assert.AreEqual(2, initializedSessions[0].NegotiationResult.Request.PlayerMixerThreadCount);
        Assert.AreSame(
            original,
            runtime.Initialize(CreatePlayerSettings(
                AudioDriver.WasapiShared,
                sampleRateConversionQuality: 4,
                playerVolume: 83,
                playerMixerThreadCount: 2)));
        Assert.AreEqual(1, initializedSessions.Count);

        PlayerSettingsSnapshot savedSettingsForNextStart = CreatePlayerSettings(
            AudioDriver.WasapiShared,
            sampleRateConversionQuality: 2,
            playerMixerThreadCount: 2);
        Assert.ThrowsException<InvalidOperationException>(
            () => runtime.Initialize(savedSettingsForNextStart));
        Assert.AreSame(original, runtime.Initialize(originalSettings));
        Assert.AreEqual(1, initializedSessions.Count);
        Assert.AreSame(initializedSessions[0], releaseAttempts[0]);
        Assert.AreEqual(BassAudioSessionState.Active, initializedSessions[0].State);

        confirmRelease = true;
        AudioPlaybackInitializationResult replacement = runtime.Initialize(savedSettingsForNextStart);

        Assert.AreNotSame(original, replacement);
        Assert.AreEqual(2, initializedSessions.Count);
        Assert.AreEqual(4, initializedSessions[0].SampleRateConversionQuality);
        Assert.AreEqual(BassAudioSessionState.Released, initializedSessions[0].State);
        Assert.AreEqual(2, initializedSessions[1].SampleRateConversionQuality);
        Assert.AreEqual(2, initializedSessions[1].OutputRequest.PlayerMixerThreadCount);
        Assert.AreSame(replacement, runtime.Initialize(savedSettingsForNextStart));

        PlayerSettingsSnapshot parallelismOnlySettings = CreatePlayerSettings(
            AudioDriver.WasapiShared,
            sampleRateConversionQuality: 2,
            playerMixerThreadCount: 4);
        AudioPlaybackInitializationResult parallelismReplacement = runtime.Initialize(parallelismOnlySettings);
        Assert.AreNotSame(replacement, parallelismReplacement);
        Assert.AreEqual(3, initializedSessions.Count);
        Assert.AreEqual(BassAudioSessionState.Released, initializedSessions[1].State);
        Assert.AreEqual(2, initializedSessions[1].OutputRequest.PlayerMixerThreadCount);
        Assert.AreEqual(2, initializedSessions[2].SampleRateConversionQuality);
        Assert.AreEqual(4, initializedSessions[2].OutputRequest.PlayerMixerThreadCount);

        confirmRelease = false;
        try
        {
            Assert.ThrowsException<InvalidOperationException>(runtime.Free,
                "Unconfirmed native release must fail the stopping caller.");
            Assert.AreEqual(BassAudioSessionState.Active, initializedSessions[2].State);
            Assert.AreSame(parallelismReplacement, runtime.Initialize(parallelismOnlySettings),
                "A failed release must retain the same session and its initialization result.");
            Assert.AreEqual(3, initializedSessions.Count);
            Assert.AreEqual(4, releaseAttempts.Count, "Free must not retry native release automatically.");
            Assert.AreSame(initializedSessions[2], releaseAttempts[3]);
        }
        finally
        {
            confirmRelease = true;
            runtime.Free();
        }
        Assert.AreEqual(BassAudioSessionState.Released, initializedSessions[2].State);
        Assert.AreEqual(5, releaseAttempts.Count);
        Assert.AreSame(initializedSessions[1], releaseAttempts[2]);
        Assert.AreSame(initializedSessions[2], releaseAttempts[4]);
    }

    [TestMethod]
    public void BackendResult_PreservesEarlierFailureWhenFallbackSucceeds()
    {
        var request = new BassAudioNegotiationRequest(
            BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
            default,
            SampleRate.SAMPLE_RATE_48000Hz,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            10,
            playerMixerThreadCount: 4);
        var successfulAttempt = new BassAudioBackendAttempt(
            "BASS_WASAPI_Start",
            "BASSWASAPI",
            null,
            "started");
        var result = new BassAudioBackendResult(
            request,
            new BassAudioPlayer.DeviceDescriptor("Endpoint", "endpoint-id"),
            SampleRate.SAMPLE_RATE_48000Hz,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            10,
            42,
            [successfulAttempt],
            null,
            endpointContainerBits: 32,
            endpointEffectiveBits: 24);
        var firstFailedAttempt = new BassAudioBackendAttempt(
            "BASS_WASAPI_Init",
            "BASSWASAPI",
            Errors.Busy,
            "exclusive failed");
        var secondFailedAttempt = new BassAudioBackendAttempt(
            "BASS_WASAPI_GetDeviceInfo",
            "BASSWASAPI",
            Errors.Device,
            "selected endpoint failed");

        BassAudioBackendResult fallback = result.WithEarlierAttempts(
            [firstFailedAttempt, secondFailedAttempt],
            "attemptedBackend=WASAPI_EXCLUSIVE nativeErrorCode=BASS_ERROR_BUSY; "
            + "fallbackDestination=WASAPI_SHARED");

        Assert.AreEqual(3, fallback.Attempts.Count);
        Assert.AreSame(firstFailedAttempt, fallback.Attempts[0]);
        Assert.AreSame(secondFailedAttempt, fallback.Attempts[1]);
        Assert.AreSame(successfulAttempt, fallback.Attempts[2]);
        Assert.AreEqual(SampleRate.SAMPLE_RATE_48000Hz, fallback.ActualRate);
        Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, fallback.EngineFormat);
        Assert.AreEqual(32, fallback.EndpointContainerBits);
        Assert.AreEqual(24, fallback.EndpointEffectiveBits);
        Assert.AreEqual(4, fallback.Request.PlayerMixerThreadCount);
        StringAssert.Contains(fallback.FallbackReason, "BASS_ERROR_BUSY");
        StringAssert.Contains(fallback.FallbackReason, "fallbackDestination=WASAPI_SHARED");
    }

    private static PlayerSettingsSnapshot CreatePlayerSettings(
        AudioDriver driver,
        int sampleRateConversionQuality = AudioResamplingQuality.Default,
        string deviceIdentity = "",
        string deviceName = "",
        SampleRate playerSampleRate = SampleRate.AUTO,
        SampleFormat playerFormat = SampleFormat.AUTO,
        float playerBufferSize = 10,
        bool playerWasapiParam = false,
        int playerVolume = 50,
        int playerMixerThreadCount = BassMixerThreadConfigurator.RealtimeDefaultThreadCount)
    {
        return new PlayerSettingsSnapshot(
            driver,
            deviceIdentity,
            deviceName,
            playerSampleRate,
            playerFormat,
            playerBufferSize,
            playerWasapiParam,
            playerVolume,
            new PlayerResolution(800, 600),
            false,
            default,
            sampleRateConversionQuality,
            playerMixerThreadCount);
    }

    private static BeMusicSeeker.Properties.Settings CreateAudiblePlaybackSettings(SampleRate sampleRate) => new()
    {
        PlayerDriver = BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE,
        PlayerDevice = "audio-contract-test-device",
        PlayerDeviceName = "Audio contract test endpoint",
        PlayerSampleRate = sampleRate,
        PlayerFormat = SampleFormat.SAMPLE_FLOAT_32BIT,
        PlayerBufferSize = 10,
        PlayerWASAPIParam = false,
        PlayerResamplingQuality = AudioResamplingQuality.Default,
    };

    private static BassAudioPlaybackRuntime CreateInjectedBassAudioPlaybackRuntime(
        List<string> events,
        List<BassAudioSession> initializedSessions,
        Action<PlayerSettingsSnapshot, BassAudioSession>? initialized = null)
    {
        return new BassAudioPlaybackRuntime(
            (settings, captureSession) =>
            {
                BassAudioPlayer.DeviceDriver backend = BassAudioMapping.ToBassDriver(settings.PlayerDriver);
                var device = new BassAudioPlayer.DeviceDescriptor(
                    settings.PlayerDeviceName,
                    settings.PlayerDevice);
                var negotiation = new BassAudioBackendResult(
                    new BassAudioNegotiationRequest(
                        backend,
                        device,
                        settings.PlayerSampleRate,
                        settings.PlayerFormat,
                        settings.PlayerBufferSize),
                    device,
                    SampleRate.SAMPLE_RATE_48000Hz,
                    SampleFormat.SAMPLE_FLOAT_32BIT,
                    SampleFormat.SAMPLE_FLOAT_32BIT,
                    10,
                    1,
                    [],
                    null);
                var session = new BassAudioSession(
                    backend,
                    device,
                    settings.SampleRateConversionQuality,
                    settings.AudioOutputRequest)
                {
                    ActualBackend = backend,
                    ActualDevice = device,
                    State = BassAudioSessionState.Active,
                    NegotiationResult = negotiation,
                };
                initializedSessions.Add(session);
                events.Add("native-initialize:" + initializedSessions.Count);
                if (initialized == null) { ReportRealOutputCallback(session); }
                else { initialized(settings, session); }
                captureSession(session);
            },
            session =>
            {
                events.Add("session-release:" + (initializedSessions.IndexOf(session) + 1));
                session.State = BassAudioSessionState.Released;
                return true;
            });
    }

    private static void AssertPersistedEnumValue<TEnum>(TEnum value, int expected)
        where TEnum : struct, Enum
    {
        Assert.AreEqual(expected, Convert.ToInt32(value, CultureInfo.InvariantCulture), value.ToString());
    }

    private static string WritePlaybackChart(string directory, string name, int bpm, int measure)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(
            path,
            $"#PLAYER 1\n#TITLE lifecycle\n#ARTIST test\n#BPM {bpm}\n#{measure:000}11:01\n",
            System.Text.Encoding.ASCII);
        return path;
    }

    private static async Task CaptureTaskCompletionAsync(
        Task task,
        List<Exception> failures,
        Func<Exception, bool>? expectedException = null)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (expectedException?.Invoke(exception) == true)
        {
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void CaptureCleanup(List<Exception> failures, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        else if (failures.Count > 1)
        {
            throw new AggregateException("The test failure and cleanup failures are retained.", failures);
        }
    }

    private static AudioPlaybackInitializationResult CreatePlaybackInitializationResult() => new(
        AudioDriver.NullDevice,
        string.Empty,
        string.Empty,
        SampleRate.AUTO,
        SampleFormat.AUTO,
        10,
        false,
        50,
        AudioDriver.NullDevice,
        string.Empty,
        string.Empty,
        SampleRate.SAMPLE_RATE_44100Hz,
        SampleFormat.SAMPLE_FLOAT_32BIT,
        SampleFormat.UNKNOWN,
        2,
        0,
        string.Empty,
        isSilentFallback: false);

    private static async Task ObserveSignalOrFailureAsync(Task signal, Task producer)
    {
        await Task.WhenAny(signal, producer);
        if (!signal.IsCompleted) { await producer; Assert.Fail("producerが観測境界より先に終端しました。"); }
        await signal;
    }

    private static async Task<Exception> GetTaskFailureAsync(Task task)
    {
        try { await task; }
        catch (Exception failure) { return failure; }
        throw new AssertFailedException("失敗Taskが必要です。");
    }

    private static IEnumerable<Exception> ExceptionCauses(Exception failure) => failure switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.SelectMany(ExceptionCauses),
        _ => [failure]
    };

    private sealed class CompletionInputReadStream(byte[] bytes, TaskCompletionSource entered,
        ManualResetEventSlim release) : MemoryStream(bytes)
    {
        private int reads;
        public override int Read(byte[] buffer, int offset, int count)
        {
            // signature読取り後、確保済みnative inputへの実readを保持します。
            if (Interlocked.Increment(ref reads) == 2) { entered.TrySetResult(); release.Wait(); }
            return base.Read(buffer, offset, count);
        }
    }

    private sealed class ObservedInternalBmsPlayer(IPlayerSettingsGateway settings, IAudioPlaybackRuntime runtime,
        Func<string, BMSAutoPlayer> factory, Func<NextSongPreloadInput, CancellationToken, PreparedBmsSong> preparation)
        : InternalBMSAutoPlayerSoundOnly(settings, runtime, autoPlayerFactory: factory, songPreparation: preparation), INextSongPreloadPlayer
    {
        internal PlaybackStartOperation? Operation { get; private set; }
        PlaybackStartOperation INextSongPreloadPlayer.BeginStart(string path, Action<object, EventArgs>? onExit,
            bool allowPreload, Action<Exception>? onPlaybackFailure)
        {
            Operation = BeginStart(path, onExit, allowPreload, onPlaybackFailure);
            return Operation;
        }
    }

    private sealed class CompletionObservedBmsAutoPlayer(Ribbit.BMS.BMSFile bms, IBassScheduledMixerNativeBoundary native,
        ManualResetEventSlim tickGate, TaskCompletionSource tickEntered, TaskCompletionSource<Exception> tickFailure,
        List<string> events) : BMSAutoPlayer(bms, native)
    {
        private int firstTick = 1;
        protected override void OnPlaybackTick(TimeSpan playbackTime)
        {
            if (Interlocked.Exchange(ref firstTick, 0) == 1) { tickEntered.TrySetResult(); tickGate.Wait(); }
            try { base.OnPlaybackTick(playbackTime); }
            catch (Exception failure) { tickFailure.TrySetResult(failure); throw; }
        }
        protected override void OnPlaybackStopping()
        {
            base.OnPlaybackStopping();
            events.Add("source-ended");
        }
    }

    private sealed class RecordingAudioPlaybackRuntime : IAudioPlaybackRuntime
    {
        private readonly List<string> events;

        internal RecordingAudioPlaybackRuntime(List<string> events)
        {
            this.events = events;
        }

        public int CurrentVoices => 0;

        public int MaxVoices => 0;

        /// <summary>
        /// 初期化時に送出する例外。例外を注入しない場合は <see langword="null"/>。
        /// </summary>
        internal Exception? InitializeException { get; init; }

        /// <summary>初期化成功時に返す結果です。</summary>
        internal AudioPlaybackInitializationResult? InitializeResult { get; init; }

        internal Action<PlayerSettingsSnapshot>? InitializeAction { get; init; }

        internal Func<Task>? OutputReady { get; init; }

        public Task WaitForOutputReadyAsync() => OutputReady?.Invoke() ?? Task.CompletedTask;

        internal Exception? FreeException { get; init; }
        internal Func<int, Exception?>? FreeFailureForCall { get; set; }

        public AudioPlaybackInitializationResult Initialize(PlayerSettingsSnapshot settings)
        {
            events.Add("initialize");
            InitializeAction?.Invoke(settings);
            if (InitializeException != null)
            {
                throw InitializeException;
            }
            return InitializeResult
                ?? throw new AssertFailedException("Playback initialization should not be called by this lifecycle test.");
        }

        public void ClearMaxVoices()
        {
            events.Add("clear-max-voices");
        }

        public void SetVolume(int volume)
        {
            events.Add("volume:" + volume);
        }

        public void Free()
        {
            events.Add("free");
            Exception? injected = FreeFailureForCall?.Invoke(events.Count(value => value == "free"));
            if (injected != null) { throw injected; }
            if (FreeException != null) { throw FreeException; }
        }
    }

    private sealed class StopObservedBmsAutoPlayer(Ribbit.BMS.BMSFile bms, List<string> events) : LifecycleBMSAutoPlayer(bms)
    {
        protected override void OnPlaybackStopping()
        {
            base.OnPlaybackStopping();
            events.Add("current-stop");
        }
    }

    private sealed class DisposeObservedBmsAutoPlayer(Ribbit.BMS.BMSFile bms, List<string> events, Exception? failure) : LifecycleBMSAutoPlayer(bms)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                events.Add("dispose");
                if (failure != null) { throw failure; }
            }
        }
    }

    private sealed class ImmediatePlaybackDispatcher : IPlaybackUiDispatcher
    {
        public void Dispatch(Action action) => action();
        public Task DispatchAsync(Action action) { action(); return Task.CompletedTask; }
    }

    private sealed class ObservedPlaybackDialogs(List<string> events) : IPlaybackDialogService
    {
        internal TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Notifications { get; private set; }
        public bool ConfirmTemporaryInstallPlayback() => throw new AssertFailedException("Temporary copy is outside this input.");
        public void NotifyPlaybackFailure(Exception exception)
        {
            Notifications++;
            events.Add("notify");
            Failure.TrySetResult(exception);
        }
    }

    private class LifecycleBMSAutoPlayer(Ribbit.BMS.BMSFile bms)
        : BMSAutoPlayer(bms)
    {
        public override void LoadResources()
        {
            // InternalBMSAutoPlayerSoundOnlyの寿命だけを検査するため、再生時間は
            // 音声deviceの初期化や存在しないWAV fixtureに依存させない。
            MusicDuration = TimeSpan.FromMilliseconds(500);
        }

        protected override void OnPlaybackStarting()
        {
        }
    }

    private sealed class DelayedCompletionBmsAutoPlayer(Ribbit.BMS.BMSFile bms, Task delivery,
        TaskCompletionSource terminal) : LifecycleBMSAutoPlayer(bms)
    {
        public override async Task Start()
        {
            Exception? failure = null;
            try { await base.Start(); } catch (Exception cause) { failure = cause; }
            terminal.TrySetResult();
            await delivery;
            if (failure != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
        }
    }

    private sealed class MeasuredBmsAutoPlayer(Ribbit.BMS.BMSFile bms, Action<TimeSpan> report) : BMSAutoPlayer(bms)
    {
        public override void LoadResources()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            base.LoadResources();
            report(clock.Elapsed);
        }
    }

    private sealed class GatedReleaseBMSAutoPlayer(Ribbit.BMS.BMSFile bms, ManualResetEventSlim release,
        TaskCompletionSource entered, TaskCompletionSource finished) : LifecycleBMSAutoPlayer(bms)
    {
        public override void Stop()
        {
            base.Stop();
            entered.TrySetResult();
            release.Wait();
            finished.TrySetResult();
        }
    }

    private class ObservingBMSAutoPlayer(Ribbit.BMS.BMSFile bms, List<string> events)
        : LifecycleBMSAutoPlayer(bms)
    {
        private int moveToCount;
        private long lastMoveToTicks;

        internal int MoveToCount => System.Threading.Volatile.Read(ref moveToCount);

        internal TimeSpan LastMoveToTime => TimeSpan.FromTicks(System.Threading.Interlocked.Read(ref lastMoveToTicks));

        protected override void MoveTo(TimeSpan time)
        {
            System.Threading.Interlocked.Exchange(ref lastMoveToTicks, time.Ticks);
            System.Threading.Interlocked.Increment(ref moveToCount);
            base.MoveTo(time);
        }

        public override void Stop()
        {
            base.Stop();
            events.Add("old-stop-joined");
        }

        internal override void DisposeBeforeNextSong()
        {
            base.DisposeBeforeNextSong();
            events.Add("old-source-release-complete");
        }
    }

    private sealed class ThrowingReleaseBMSAutoPlayer(
        Ribbit.BMS.BMSFile bms,
        BassAudioPlaybackException failure,
        List<string> events)
        : ObservingBMSAutoPlayer(bms, events)
    {
        internal bool FailNextRelease { get; set; } = true;

        internal override void DisposeBeforeNextSong()
        {
            if (FailNextRelease)
            {
                FailNextRelease = false;
                base.DisposeBeforeNextSong();
                throw failure;
            }
            base.DisposeBeforeNextSong();
        }
    }
}
