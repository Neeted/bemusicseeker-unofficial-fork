using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Tests.Helpers;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using NLog.Config;
using NLog.Targets;
using Parago.Windows;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class SelectedChartAudioConversionWorkflowOwnerTests
{
    /// <summary>同時刻の背景音と演奏noteを持つ実BMSONをWAVEへ出力し、音源末尾の4800frameとsession解放の欠落を検出します。</summary>
    /// <remarks>実BASS・encoder・共有設定を使用して出力や解放の接続を観測するため、native状態とログ設定を共有するケースは並列実行しません。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public void ProductionExecutorExportsBmsonThroughTheSharedWriterAndReleasesItsSession()
    {
        int unexpectedNotificationCount = 0;
        string root = CreateRoot();
        var lease = new BassAudioSessionLease();
        SampleRate previousFrequency = BassAudioPlayer.Frequency;
        SampleFormat previousFormat = BassAudioPlayer.Format;
        string previousEncoderDirectory = BassAudioWriter.EncoderDirectory;
        try
        {
            BassAudioPlayer.Free();
            BassAudioRuntime.Shutdown();
            string path = Path.Combine(root, "chart.bmson");
            File.WriteAllBytes(Path.Combine(root, "tone.wav"), CreatePcmWave());
            File.WriteAllText(path, "{\"info\":{\"init_bpm\":120,\"title\":\"Bmson export\"},\"sound_channels\":[{\"name\":\"tone.wav\",\"notes\":[{\"y\":0},{\"y\":0,\"x\":1}]}]}");
            var executor = new BassSelectedChartAudioConversionExecutor(BassAudioWriter.TryReleaseEncoder, BassAudioPlayer.Free, lease);
            var settings = new SelectedChartAudioConversionSettingsSnapshot(EncoderType.WAVE, SampleRate.SAMPLE_RATE_48000Hz,
                SampleFormat.SAMPLE_FLOAT_32BIT, AudioNormalization.None, 0f, string.Empty, 1f, "%TITLE%", "WAVE", "48000Hz", "float");
            var results = new List<SelectedChartAudioConversionFileResult>();
            executor.Execute([CreateTarget(path, ChartOperationCapabilities.ConvertToAudio).Chart], root, settings,
                CancellationToken.None, _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), results.Add);
            Assert.AreEqual(1, results.Count);
            Assert.IsNull(results[0].Error);
            Assert.IsNull(lease.Session);
            AudioTestWaveFile wave = AudioTestWaveFileReader.Read(File.ReadAllBytes(Path.Combine(root, "Bmson export.wav")));
            Assert.AreEqual(4800, wave.DataLength / (wave.Channels * sizeof(float)));
        }
        finally
        {
            BassAudioWriter.TryReleaseEncoder();
            if (lease.Session != null)
            {
                lease.TryRelease(BassAudioPlayer.Free);
            }

            BassAudioPlayer.Free();
            BassAudioRuntime.Shutdown();
            BassAudioPlayer.Frequency = previousFrequency;
            BassAudioPlayer.Format = previousFormat;
            BassAudioWriter.EncoderDirectory = previousEncoderDirectory;
            DeleteRoot(root);
        }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>実BMSとBMSONを一要求でWAVEへ変換し、独立したframe数・ステレオPCM・設定、解放と次の明示変換を確認します。Busyでは停止・writerを始めず、encoderとsessionの解放中も受付を保持します。</summary>
    /// <remarks>実BASSのBMS/BMSON解析・encoder出力と同じ受付の解放を接続し、共有native状態・設定を使うため並列実行しません。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public async Task RunAsync_RealNativeExecutorRetainsLibraryAdmissionThroughEncoderAndSessionCleanup()
    {
        int unexpectedNotificationCount = 0;
        string root = CreateRoot();
        var nativeLease = new BassAudioSessionLease();
        var gate = new ChartFileOperationSynchronizer();
        SampleRate previousFrequency = BassAudioPlayer.Frequency;
        SampleFormat previousFormat = BassAudioPlayer.Format;
        string previousEncoderDirectory = BassAudioWriter.EncoderDirectory;
        try
        {
            BassAudioPlayer.Free();
            BassAudioRuntime.Shutdown();
            string bmsPath = Path.Combine(root, "chart.bms");
            string path = Path.Combine(root, "chart.bmson");
            File.WriteAllBytes(Path.Combine(root, "bms-tone.wav"), CreateStereoFloatWave(0.25f, -0.125f));
            File.WriteAllBytes(Path.Combine(root, "bmson-tone.wav"), CreateStereoFloatWave(-0.125f, 0.375f));
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE BMS native export\r\n#BPM 120\r\n#WAV01 bms-tone.wav\r\n#00111:01\r\n");
            File.WriteAllText(path, "{\"version\":\"1.0.0\",\"info\":{\"init_bpm\":120,\"title\":\"BMSON native export\"},\"sound_channels\":[{\"name\":\"bmson-tone.wav\",\"notes\":[{\"y\":0,\"x\":1}]}]}");
            var events = new EventLog();
            var playback = new RecordingPlayback(events);
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [root]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted),
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            int encoderCleanup = 0;
            int sessionCleanup = 0;
            var executor = new BassSelectedChartAudioConversionExecutor(
                () => { Assert.IsTrue(gate.IsActive); encoderCleanup++; return BassAudioWriter.TryReleaseEncoder(); },
                session => { Assert.IsTrue(gate.IsActive); sessionCleanup++; return BassAudioPlayer.Free(session); }, nativeLease);
            string prefix = string.Empty;
            var owner = new SelectedChartAudioConversionWorkflowOwner(
                () => new SelectedChartAudioConversionSettingsSnapshot(EncoderType.WAVE, SampleRate.SAMPLE_RATE_48000Hz,
                    SampleFormat.SAMPLE_FLOAT_32BIT, AudioNormalization.None, 0f, string.Empty, 2f, prefix + "%TITLE%", "WAVE", "48000Hz", "float"),
                _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), playback, dialogs, executor, gate);
            var request = new SelectedChartAudioConversionRequest([
                CreateTarget(bmsPath, ChartOperationCapabilities.ConvertToAudio),
                CreateTarget(path, ChartOperationCapabilities.ConvertToAudio)]);
            Assert.IsTrue(gate.TryEnter(out IDisposable busy));
            using (busy)
            {
                Assert.AreEqual(SelectedChartAudioConversionStatus.Busy, (await owner.RunAsync(request)).Status);
                Assert.AreEqual(0, playback.StopCalls);
                Assert.AreEqual(0, encoderCleanup);
                Assert.IsFalse(File.Exists(Path.Combine(root, "BMS native export.wav")));
                Assert.IsFalse(File.Exists(Path.Combine(root, "BMSON native export.wav")));
            }
            SelectedChartAudioConversionResult result = await owner.RunAsync(request);
            Assert.AreEqual(SelectedChartAudioConversionStatus.Completed, result.Status);
            Assert.AreEqual(2, result.CompletedCount);
            Assert.AreEqual(2, result.SucceededCount);
            Assert.AreEqual(0, result.FailedCount);
            Assert.AreEqual(0, result.UnprocessedCount);
            CollectionAssert.AreEqual(new[] { bmsPath, path }, result.FileResults.Select(file => file.FileName).ToArray());
            AssertNativeStereoWave(Path.Combine(root, "BMS native export.wav"), 96000, 100800, 0.08f, -0.04f);
            AssertNativeStereoWave(Path.Combine(root, "BMSON native export.wav"), 0, 4800, -0.04f, 0.12f);
            Assert.IsTrue(encoderCleanup > 0);
            Assert.IsTrue(sessionCleanup > 0);
            Assert.IsNull(nativeLease.Session);
            Assert.IsFalse(gate.IsActive);
            int previousEncoderCleanup = encoderCleanup;
            int previousSessionCleanup = sessionCleanup;
            prefix = "next-";
            SelectedChartAudioConversionResult next = await owner.RunAsync(request);
            Assert.AreEqual(SelectedChartAudioConversionStatus.Completed, next.Status);
            Assert.AreEqual(2, next.SucceededCount);
            Assert.AreEqual(0, next.FailedCount);
            Assert.AreEqual(0, next.UnprocessedCount);
            AssertNativeStereoWave(Path.Combine(root, "next-BMS native export.wav"), 96000, 100800, 0.08f, -0.04f);
            AssertNativeStereoWave(Path.Combine(root, "next-BMSON native export.wav"), 0, 4800, -0.04f, 0.12f);
            Assert.IsTrue(encoderCleanup > previousEncoderCleanup);
            Assert.IsTrue(sessionCleanup > previousSessionCleanup);
            Assert.IsNull(nativeLease.Session);
            Assert.IsFalse(gate.IsActive);
        }
        finally
        {
            BassAudioWriter.TryReleaseEncoder();
            if (nativeLease.Session != null) { nativeLease.TryRelease(BassAudioPlayer.Free); }
            BassAudioPlayer.Free();
            BassAudioRuntime.Shutdown();
            BassAudioPlayer.Frequency = previousFrequency;
            BassAudioPlayer.Format = previousFormat;
            BassAudioWriter.EncoderDirectory = previousEncoderDirectory;
            DeleteRoot(root);
        }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>BMS/BMSONを混ぜた選択から既存の所有ファイルだけを順に残し、保留・不在を変換へ含める誤りを検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public void Request_FiltersCapabilityAndExistingFilesAcrossChartKindsInSelectionOrder()
    {
        string root = CreateRoot();
        try
        {
            ChartOperationTarget first = CreateResolvedTarget(CreateChartFile(root, "first.bmson"));
            ChartOperationTarget second = CreateResolvedTarget(CreateChartFile(root, "second.bms"));
            ChartOperationTarget missingBms = CreateResolvedTarget(Path.Combine(root, "missing.bms"));
            ChartOperationTarget missingBmson = CreateResolvedTarget(Path.Combine(root, "missing.bmson"));

            var request = new SelectedChartAudioConversionRequest([first, missingBms, second, missingBmson]);

            Assert.IsTrue(request.HasTargets);
            CollectionAssert.AreEqual(new[] { first, second }, request.Targets.ToArray());
            Assert.IsTrue(missingBms.HasCapability(ChartOperationCapabilities.ConvertToAudio));
            Assert.IsTrue(missingBmson.HasCapability(ChartOperationCapabilities.ConvertToAudio));

            ChartOperationTarget pendingBms = CreateResolvedTarget(
                CreateChartFile(root, "pending.bms"), ChartOperationSourceScope.PendingPackage);
            ChartOperationTarget pendingBmson = CreateResolvedTarget(
                CreateChartFile(root, "pending.bmson"), ChartOperationSourceScope.PendingPackage);
            var pendingRequest = new SelectedChartAudioConversionRequest([pendingBmson, pendingBms]);

            Assert.IsTrue(pendingBms.IsPending);
            Assert.IsTrue(pendingBmson.IsPending);
            Assert.IsFalse(pendingRequest.HasTargets);
            Assert.AreEqual(0, pendingRequest.Targets.Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>encoderの解放失敗時はsessionを保持し、次の解放でencoderからsessionへ進む順序と所有権の早期放棄を検出します。</summary>
    [TestMethod]
    public void ConversionCleanup_RetainsSessionWhenEncoderReleaseFailsAndRetriesInOrder()
    {
        var events = new List<string>();
        var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.NULL_DEVICE)
        {
            State = BassAudioSessionState.Active
        };
        var lease = new BassAudioSessionLease();
        lease.Attach(session);
        int encoderReleaseAttempts = 0;
        var executor = new BassSelectedChartAudioConversionExecutor(
            () =>
            {
                events.Add("encoder");
                return ++encoderReleaseAttempts > 1;
            },
            _ =>
            {
                events.Add("session");
                return true;
            },
            lease);

        Assert.ThrowsException<AudioSourceFatalException>(
            () => executor.ReleaseConversionSession(session, primaryException: null));
        Assert.AreSame(session, lease.Session);
        CollectionAssert.AreEqual(new[] { "encoder" }, events);

        executor.ReleaseConversionSession(session, primaryException: null);

        Assert.IsNull(lease.Session);
        CollectionAssert.AreEqual(new[] { "encoder", "encoder", "session" }, events);
    }

    /// <summary>encoder解放中のshutdownを拒否し、session解放までにoperationを開放する順序の逆転を検出します。</summary>
    [TestMethod]
    public void ConversionCleanup_HoldsOperationDuringEncoderAndReleasesItBeforeSession()
    {
        var gate = new BassAudioOperationGate(initiallyOpen: true);
        Assert.IsTrue(gate.TryEnterOperation(out BassAudioOperationLease operation));
        try
        {
            var session = new BassAudioSession(BassAudioPlayer.DeviceDriver.NULL_DEVICE)
            {
                State = BassAudioSessionState.Active
            };
            var lease = new BassAudioSessionLease();
            lease.Attach(session);
            var executor = new BassSelectedChartAudioConversionExecutor(
                () =>
                {
                    try
                    {
                        using BassAudioExclusiveLease shutdown = gate.EnterRuntimeShutdown();
                        Assert.Fail("Encoder cleanup ran after the shared operation was released.");
                        return false;
                    }
                    catch (InvalidOperationException)
                    {
                        return true;
                    }
                },
                _ =>
                {
                    using BassAudioExclusiveLease shutdown = gate.EnterRuntimeShutdown();
                    shutdown.Complete(success: true);
                    return true;
                },
                lease);

            executor.ReleaseConversionSession(
                session,
                primaryException: null,
                ref operation,
                operationEntered: true);

            Assert.IsNull(lease.Session);
        }
        finally
        {
            operation.Dispose();
        }
    }

    /// <summary>encoder解放に成功したファイル失敗を一回報告し、元原因・対象の消失や成功扱いを検出します。</summary>
    [TestMethod]
    public void FileCleanup_ReportsFailureAndContinuesWhenEncoderReleaseSucceeds()
    {
        var reports = new List<SelectedChartAudioConversionFileResult>();
        var fileException = new InvalidOperationException("render failed");

        BassSelectedChartAudioConversionExecutor.CompleteFile(
            "song.bms",
            fileException,
            () => true,
            reports.Add);

        Assert.AreEqual(1, reports.Count);
        Assert.AreEqual("song.bms", reports[0].FileName);
        Assert.AreSame(fileException, reports[0].Error);
        Assert.IsFalse(reports[0].Succeeded);
    }

    /// <summary>encoder解放の失敗は元のファイル失敗を保持して継続不可とし、後続処理への誤った続行を検出します。</summary>
    [TestMethod]
    public void FileCleanup_PreservesPrimaryFailureAndStopsWhenEncoderReleaseFails()
    {
        var reports = new List<SelectedChartAudioConversionFileResult>();
        var fileException = new InvalidOperationException("render failed");

        bool canContinue = BassSelectedChartAudioConversionExecutor.CompleteFile(
            "song.bms",
            fileException,
            () => false,
            reports.Add);

        Assert.IsFalse(canContinue);
        Assert.AreEqual(1, reports.Count);
        Assert.AreSame(fileException, reports[0].Error);
        Assert.IsFalse(reports[0].Succeeded);
    }

    /// <summary>一件目のrenderとencoder解放が失敗した時に残件を未処理として集約し、後続名の誤表示や元原因の消失を検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public async Task RunAsync_EncoderCleanupFailureReportsPrimaryCauseAndKeepsUnprocessedCount()
    {
        string root = CreateRoot();
        try
        {
            string firstPath = CreateChartFile(root, "first.bms");
            string secondPath = CreateChartFile(root, "second.bms");
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [root]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted)
            };
            var primaryFailure = new IOException("render failed");
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (files, _, _, _, report) =>
                {
                    bool canContinue = BassSelectedChartAudioConversionExecutor.CompleteFile(
                        files[0].Path,
                        primaryFailure,
                        () => false,
                        report);
                    Assert.IsFalse(canContinue);
                }
            };
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(
                dialogs,
                new RecordingPlayback(events),
                executor,
                events);

            SelectedChartAudioConversionResult result = await owner.RunAsync(new SelectedChartAudioConversionRequest([
                CreateTarget(firstPath, ChartOperationCapabilities.ConvertToAudio),
                CreateTarget(secondPath, ChartOperationCapabilities.ConvertToAudio)
            ]));

            Assert.AreEqual(1, result.CompletedCount);
            Assert.AreEqual(1, result.UnprocessedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(1, result.FileResults.Count);
            Assert.AreSame(primaryFailure, result.FileResults[0].Error);
            Assert.AreEqual(1, dialogs.MessageCalls);
            StringAssert.Contains(dialogs.LastMessage, Resources.Failure + ": 2");
            StringAssert.Contains(dialogs.LastMessage, string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Resources.AudioConversionOtherFailureFormat,
                "first.bms",
                primaryFailure.Message));
            Assert.IsFalse(dialogs.LastMessage.Contains("second.bms", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>受付後に実入力を削除しencoder解放も失敗させ、元入力失敗と解放失敗の保持・native所有権・完了通知の抑止を検出します。</summary>
    /// <remarks>実BASS・encoder・共有設定を使用して出力や解放の接続を観測するため、native状態とログ設定を共有するケースは並列実行しません。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public async Task RunAsync_ProductionExecutorCleanupFailurePreservesPrimaryCauseAndNotifiesOnce()
    {
        string root = CreateRoot();
        string outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(outputDirectory);
        string firstPath = CreateChartFile(root, "first.bms");
        string secondPath = CreateChartFile(root, "second.bms");
        BassAudioRuntime.Shutdown();
        _ = NLogWrapper.GetLogger(nameof(BassSelectedChartAudioConversionExecutor));
        LoggingConfiguration? originalLoggingConfiguration = LogManager.Configuration;
        var audioConversionTarget = new MemoryTarget { Layout = "${message}" };
        var testLoggingConfiguration = new LoggingConfiguration();
        testLoggingConfiguration.AddRule(
            LogLevel.Warn,
            LogLevel.Fatal,
            audioConversionTarget,
            nameof(BassSelectedChartAudioConversionExecutor));
        LogManager.Configuration = testLoggingConfiguration;
        int encoderReleaseAttempts = 0;
        int sessionReleaseAttempts = 0;
        var sessionLease = new BassAudioSessionLease();
        try
        {
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [outputDirectory]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted),
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            var playback = new RecordingPlayback(events)
            {
                StopAction = () =>
                {
                    File.Delete(firstPath);
                    File.Delete(secondPath);
                }
            };
            var executor = new BassSelectedChartAudioConversionExecutor(
                () => ++encoderReleaseAttempts == 1,
                session =>
                {
                    sessionReleaseAttempts++;
                    return BassAudioPlayer.Free(session);
                },
                sessionLease);
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, playback, executor, events);

            AudioSourceFatalException failure = await Assert.ThrowsExceptionAsync<AudioSourceFatalException>(
                () => owner.RunAsync(new SelectedChartAudioConversionRequest([
                    CreateTarget(firstPath, ChartOperationCapabilities.ConvertToAudio),
                    CreateTarget(secondPath, ChartOperationCapabilities.ConvertToAudio)
                ])));

            LogManager.Flush();
            Assert.IsInstanceOfType<AggregateException>(failure.InnerException);
            IReadOnlyList<Exception> causes = ((AggregateException)failure.InnerException).Flatten().InnerExceptions;
            Assert.IsTrue(causes.Any(exception => exception.ToString().Contains(firstPath, StringComparison.Ordinal)));
            Assert.IsTrue(causes.Any(exception => exception.Message.Contains("encoder cleanup failed", StringComparison.Ordinal)));
            Assert.AreEqual(3, encoderReleaseAttempts);
            Assert.AreEqual(0, sessionReleaseAttempts);
            Assert.IsNotNull(sessionLease.Session, "未確認のnative所有権をsession leaseに保持します。");
            Assert.AreEqual(0, dialogs.MessageCalls, "A fatal native cleanup failure aborts the batch before completion notification.");
            string logs = string.Join(Environment.NewLine, audioConversionTarget.Logs);
            StringAssert.Contains(logs, firstPath);
            StringAssert.Contains(logs, "InvalidBmsFileException");
            StringAssert.Contains(logs, "encoder cleanup failed for " + firstPath);
            StringAssert.Contains(logs, "session cleanup failed");
        }
        finally
        {
            try
            {
                BassAudioWriter.TryReleaseEncoder();
                if (sessionLease.Session != null)
                {
                    sessionLease.TryRelease(BassAudioPlayer.Free);
                }
                BassAudioRuntime.Shutdown();
            }
            finally
            {
                LogManager.Flush();
                LogManager.Configuration = originalLoggingConfiguration;
                DeleteRoot(root);
            }
        }
    }

    /// <summary>実入力失敗後にsession解放確認も失敗した場合は両原因と未解放leaseを保持し、成功通知へ進む誤りを検出します。</summary>
    /// <remarks>実BASS・encoder・共有設定を使用して出力や解放の接続を観測するため、native状態とログ設定を共有するケースは並列実行しません。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public async Task RunAsync_ProductionExecutorSessionCleanupFailureRetainsPrimaryAndAborts()
    {
        string root = CreateRoot();
        string outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(outputDirectory);
        string firstPath = CreateChartFile(root, "first.bms");
        string secondPath = CreateChartFile(root, "second.bms");
        BassAudioRuntime.Shutdown();
        _ = NLogWrapper.GetLogger(nameof(BassSelectedChartAudioConversionExecutor));
        LoggingConfiguration? originalLoggingConfiguration = LogManager.Configuration;
        var audioConversionTarget = new MemoryTarget { Layout = "${message}" };
        var testLoggingConfiguration = new LoggingConfiguration();
        testLoggingConfiguration.AddRule(
            LogLevel.Warn,
            LogLevel.Fatal,
            audioConversionTarget,
            nameof(BassSelectedChartAudioConversionExecutor));
        LogManager.Configuration = testLoggingConfiguration;
        int encoderReleaseAttempts = 0;
        int sessionReleaseAttempts = 0;
        var sessionLease = new BassAudioSessionLease();
        try
        {
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [outputDirectory]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted),
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            var playback = new RecordingPlayback(events)
            {
                StopAction = () =>
                {
                    File.Delete(firstPath);
                    File.Delete(secondPath);
                }
            };
            var executor = new BassSelectedChartAudioConversionExecutor(
                () =>
                {
                    encoderReleaseAttempts++;
                    return true;
                },
                _ =>
                {
                    sessionReleaseAttempts++;
                    return false;
                },
                sessionLease);
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, playback, executor, events);

            AudioSourceFatalException failure = await Assert.ThrowsExceptionAsync<AudioSourceFatalException>(
                () => owner.RunAsync(new SelectedChartAudioConversionRequest([
                    CreateTarget(firstPath, ChartOperationCapabilities.ConvertToAudio),
                    CreateTarget(secondPath, ChartOperationCapabilities.ConvertToAudio)
                ])));

            LogManager.Flush();
            Assert.IsInstanceOfType<AggregateException>(failure.InnerException);
            IReadOnlyList<Exception> causes = ((AggregateException)failure.InnerException).Flatten().InnerExceptions;
            Assert.IsTrue(causes.Any(exception => exception.ToString().Contains(firstPath, StringComparison.Ordinal)));
            Assert.IsTrue(causes.Any(exception => exception.Message.Contains("completed without confirming", StringComparison.Ordinal)));
            Assert.AreEqual(4, encoderReleaseAttempts);
            Assert.AreEqual(1, sessionReleaseAttempts);
            Assert.IsNotNull(sessionLease.Session, "未確認のnative所有権をsession leaseに保持します。");
            Assert.AreEqual(0, dialogs.MessageCalls, "A fatal native cleanup failure aborts the batch before completion notification.");
            string logs = string.Join(Environment.NewLine, audioConversionTarget.Logs);
            StringAssert.Contains(logs, "session cleanup failed");
            StringAssert.Contains(logs, "completed without confirming native cleanup");
            StringAssert.Contains(logs, firstPath);
            StringAssert.Contains(logs, secondPath);
            StringAssert.Contains(logs, "InvalidBmsFileException");
        }
        finally
        {
            try
            {
                BassAudioWriter.TryReleaseEncoder();
                if (sessionLease.Session != null)
                {
                    sessionLease.TryRelease(BassAudioPlayer.Free);
                }
                BassAudioRuntime.Shutdown();
            }
            finally
            {
                LogManager.Flush();
                LogManager.Configuration = originalLoggingConfiguration;
                DeleteRoot(root);
            }
        }
    }

    /// <summary>先行ファイル失敗の後に成功通知callbackとsession解放が失敗しても現在のfatalを保持し、三件目の出力や所有権の消失を検出します。</summary>
    /// <remarks>実BASS・encoder・共有設定を使用して出力や解放の接続を観測するため、native状態とログ設定を共有するケースは並列実行しません。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public void ProductionExecutor_PreservesCurrentFatalAndSessionCleanupFailureAfterPriorFileFailure()
    {
        string root = CreateRoot();
        string outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(outputDirectory);
        string firstPath = CreateChartFile(root, "first.bms");
        string secondPath = Path.Combine(root, "second.bms");
        string thirdPath = Path.Combine(root, "third.bms");
        File.WriteAllBytes(Path.Combine(root, "tone.wav"), CreatePcmWave());
        const string validChart = "#PLAYER 1\n#ARTIST Test\n#BPM 120\n#WAV01 tone.wav\n#00001:01\n";
        File.WriteAllText(secondPath, "#TITLE Second\n" + validChart, Encoding.ASCII);
        File.WriteAllText(thirdPath, "#TITLE Third\n" + validChart, Encoding.ASCII);
        BassAudioRuntime.Shutdown();
        _ = NLogWrapper.GetLogger(nameof(BassSelectedChartAudioConversionExecutor));
        LoggingConfiguration? originalLoggingConfiguration = LogManager.Configuration;
        var audioConversionTarget = new MemoryTarget { Layout = "${message}" };
        var testLoggingConfiguration = new LoggingConfiguration();
        testLoggingConfiguration.AddRule(
            LogLevel.Warn,
            LogLevel.Fatal,
            audioConversionTarget,
            nameof(BassSelectedChartAudioConversionExecutor));
        LogManager.Configuration = testLoggingConfiguration;
        int encoderReleaseAttempts = 0;
        int sessionReleaseAttempts = 0;
        var sessionLease = new BassAudioSessionLease();
        var priorFileResults = new List<SelectedChartAudioConversionFileResult>();
        var currentFatal = new OutOfMemoryException("Injected completion callback failure for second chart.");
        try
        {
            var executor = new BassSelectedChartAudioConversionExecutor(
                () =>
                {
                    encoderReleaseAttempts++;
                    return BassAudioWriter.TryReleaseEncoder();
                },
                _ =>
                {
                    sessionReleaseAttempts++;
                    return false;
                },
                sessionLease);
            var settings = new SelectedChartAudioConversionSettingsSnapshot(
                EncoderType.WAVE,
                SampleRate.SAMPLE_RATE_44100Hz,
                SampleFormat.SAMPLE_INT_16BIT,
                AudioNormalization.None,
                0f,
                string.Empty,
                1f,
                "%FILE%",
                "WAVE",
                "44100Hz",
                "16bit");

            AudioSourceFatalException failure = Assert.ThrowsException<AudioSourceFatalException>(() => executor.Execute(
                [
                    (ChartTestValues.Empty() with { Path = firstPath }),
                    (ChartTestValues.Empty() with { Path = secondPath }),
                    (ChartTestValues.Empty() with { Path = thirdPath })
                ],
                outputDirectory,
                settings,
                CancellationToken.None,
                _ => { },
                fileResult =>
                {
                    priorFileResults.Add(fileResult);
                    if (fileResult.FileName == secondPath)
                    {
                        throw currentFatal;
                    }
                }));

            LogManager.Flush();
            Assert.AreEqual(2, priorFileResults.Count, "The third chart must not be reached after the callback fatal.");
            Assert.AreEqual(firstPath, priorFileResults[0].FileName);
            Assert.IsInstanceOfType<Ribbit.BMS.BMSFile.InvalidBmsFileException>(priorFileResults[0].Error);
            Assert.AreEqual(secondPath, priorFileResults[1].FileName);
            Assert.IsTrue(priorFileResults[1].Succeeded);
            Assert.IsInstanceOfType<AggregateException>(failure.InnerException);
            IReadOnlyList<Exception> causes = ((AggregateException)failure.InnerException).Flatten().InnerExceptions;
            Assert.IsTrue(causes.Any(exception => ReferenceEquals(currentFatal, exception)));
            Assert.IsTrue(causes.Any(exception => exception.Message.Contains(
                "completed without confirming native cleanup",
                StringComparison.Ordinal)));
            Assert.AreEqual(4, encoderReleaseAttempts);
            Assert.AreEqual(1, sessionReleaseAttempts);
            Assert.IsNotNull(sessionLease.Session, "未確認のnative所有権をsession leaseに保持します。");
            Assert.IsTrue(File.Exists(Path.Combine(outputDirectory, "second.bms.wav")));
            Assert.IsFalse(File.Exists(Path.Combine(outputDirectory, "third.bms.wav")));
            string logs = string.Join(Environment.NewLine, audioConversionTarget.Logs);
            StringAssert.Contains(logs, firstPath);
            StringAssert.Contains(logs, nameof(Ribbit.BMS.BMSFile.InvalidBmsFileException));
            StringAssert.Contains(logs, "session cleanup failed for " + secondPath);
        }
        finally
        {
            try
            {
                BassAudioWriter.TryReleaseEncoder();
                if (sessionLease.Session != null)
                {
                    sessionLease.TryRelease(BassAudioPlayer.Free);
                }
                BassAudioRuntime.Shutdown();
            }
            finally
            {
                LogManager.Flush();
                LogManager.Configuration = originalLoggingConfiguration;
                DeleteRoot(root);
            }
        }
    }

    /// <summary>実入力不在五件を対象と原因付きで記録し、音源一件欠落の譜面は残る音源を出力して一回通知する集約漏れを検出します。</summary>
    /// <remarks>実BASS・encoder・共有設定を使用して出力や解放の接続を観測するため、native状態とログ設定を共有するケースは並列実行しません。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public async Task RunAsync_ProductionExecutorLogsTargetAndCauseForEveryFailure()
    {
        string root = CreateRoot();
        string outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(outputDirectory);
        string[] chartPaths = Enumerable.Range(1, 5)
            .Select(index => CreateChartFile(root, "song" + index + ".bms"))
            .ToArray();
        string warningChartPath = Path.Combine(root, "warning.bms");
        File.WriteAllBytes(Path.Combine(root, "valid.wav"), CreatePcmWave());
        File.WriteAllText(
            warningChartPath,
            "#PLAYER 1\n#TITLE Warning source\n#ARTIST Test\n#BPM 120\n"
                + "#WAV01 valid.wav\n#WAV02 missing.wav\n#00001:0102\n",
            Encoding.ASCII);
        BassAudioRuntime.Shutdown();
        _ = NLogWrapper.GetLogger(nameof(BassSelectedChartAudioConversionExecutor));
        LoggingConfiguration? originalLoggingConfiguration = LogManager.Configuration;
        var audioConversionTarget = new MemoryTarget { Layout = "${message}" };
        var testLoggingConfiguration = new LoggingConfiguration();
        testLoggingConfiguration.AddRule(
            LogLevel.Warn,
            LogLevel.Fatal,
            audioConversionTarget,
            nameof(BassSelectedChartAudioConversionExecutor));
        LogManager.Configuration = testLoggingConfiguration;
        int sessionReleaseAttempts = 0;
        var sessionLease = new BassAudioSessionLease();
        try
        {
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [outputDirectory]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted),
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            var playback = new RecordingPlayback(events)
            {
                StopAction = () =>
                {
                    foreach (string chartPath in chartPaths)
                    {
                        File.Delete(chartPath);
                    }
                }
            };
            var executor = new BassSelectedChartAudioConversionExecutor(
                BassAudioWriter.TryReleaseEncoder,
                session =>
                {
                    sessionReleaseAttempts++;
                    return BassAudioPlayer.Free(session);
                },
                sessionLease);
            SelectedChartAudioConversionWorkflowOwner owner = new(
                () => new SelectedChartAudioConversionSettingsSnapshot(
                    EncoderType.WAVE,
                    SampleRate.SAMPLE_RATE_44100Hz,
                    SampleFormat.SAMPLE_INT_16BIT,
                    AudioNormalization.None,
                    0f,
                    string.Empty,
                    1f,
                    "%TITLE%",
                    "WAVE",
                    "44100Hz",
                    "16bit"),
                _ => { },
                playback,
                dialogs,
                executor, new ChartFileOperationSynchronizer());

            SelectedChartAudioConversionResult result = await owner.RunAsync(new SelectedChartAudioConversionRequest(
                chartPaths.Append(warningChartPath)
                    .Select(path => CreateTarget(path, ChartOperationCapabilities.ConvertToAudio))));

            LogManager.Flush();
            Assert.AreEqual(chartPaths.Length + 1, result.CompletedCount);
            Assert.AreEqual(chartPaths.Length, result.FailedCount);
            Assert.AreEqual(1, result.SucceededCount);
            Assert.AreEqual(0, result.UnprocessedCount);
            SelectedChartAudioConversionFileResult warningResult = result.FileResults.Single(
                fileResult => fileResult.FileName == warningChartPath);
            Assert.IsNull(warningResult.Error);
            Assert.AreEqual(1, warningResult.OmittedAudioSourceCount);
            Assert.AreEqual(1, dialogs.MessageCalls);
            StringAssert.Contains(dialogs.LastMessage, string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Resources.AudioConversionOtherFailuresRemainingFormat,
                chartPaths.Length,
                chartPaths.Length - 3));
            StringAssert.Contains(dialogs.LastMessage, string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Resources.AudioConversionOmittedSourcesFormat,
                Path.GetFileName(warningChartPath),
                1));
            Assert.AreEqual(1, Directory.GetFiles(outputDirectory, "*.wav").Length,
                "The valid source is converted while the missing source is omitted.");
            string logs = string.Join(Environment.NewLine, audioConversionTarget.Logs);
            foreach (string chartPath in chartPaths)
            {
                StringAssert.Contains(logs, chartPath);
                StringAssert.Contains(logs, "InvalidBmsFileException");
            }
            Assert.AreEqual(1, sessionReleaseAttempts);
            Assert.IsNull(sessionLease.Session);
        }
        finally
        {
            try
            {
                BassAudioWriter.TryReleaseEncoder();
                if (sessionLease.Session != null)
                {
                    sessionLease.TryRelease(BassAudioPlayer.Free);
                }
                BassAudioRuntime.Shutdown();
            }
            finally
            {
                LogManager.Flush();
                LogManager.Configuration = originalLoggingConfiguration;
                DeleteRoot(root);
            }
        }
    }

    /// <summary>成功・音源省略・range超過・四件の別失敗を一回に集約し、最大peakの助言・省略条件・表示件数の取り違えを検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public async Task RunAsync_CompletionSummarizesRangeAndOtherFailuresOnce()
    {
        string root = CreateRoot();
        try
        {
            string[] chartPaths = Enumerable.Range(1, 8)
                .Select(index => CreateChartFile(root, "song" + index + ".bms"))
                .ToArray();
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [root]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted)
            };
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (_, _, _, _, report) =>
                {
                    report(new SelectedChartAudioConversionFileResult("warning-success.bms", omittedAudioSourceCount: 2));
                    report(new SelectedChartAudioConversionFileResult("success.bms"));
                    report(new SelectedChartAudioConversionFileResult("range-lower.bms", new AudioOutputRangeException(1.2d)));
                    report(new SelectedChartAudioConversionFileResult("range-maximum.bms", new AudioOutputRangeException(1.349858d)));
                    report(new SelectedChartAudioConversionFileResult(
                        "other1.bms",
                        new IOException("read failed"),
                        omittedAudioSourceCount: 4));
                    report(new SelectedChartAudioConversionFileResult("other2.bms", new InvalidOperationException("encode failed")));
                    report(new SelectedChartAudioConversionFileResult("other3.bms", new InvalidOperationException("flush failed")));
                    report(new SelectedChartAudioConversionFileResult("other4.bms", new InvalidOperationException("close failed")));
                }
            };
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(
                dialogs,
                new RecordingPlayback(events),
                executor,
                events);

            SelectedChartAudioConversionResult result = await owner.RunAsync(
                new SelectedChartAudioConversionRequest(chartPaths.Select(path => CreateTarget(path, ChartOperationCapabilities.ConvertToAudio))));

            Assert.AreEqual(8, result.CompletedCount);
            Assert.AreEqual(2, result.SucceededCount);
            Assert.AreEqual(6, result.FailedCount);
            Assert.AreEqual(0, result.UnprocessedCount);
            Assert.AreEqual(1, dialogs.MessageCalls);
            StringAssert.Contains(dialogs.LastMessage, string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Resources.AudioConversionOmittedSourcesFormat,
                "warning-success.bms",
                2));
            StringAssert.Contains(dialogs.LastMessage, Resources.AudioConversionRangeFailureFormat.Split('{')[0]);
            StringAssert.Contains(dialogs.LastMessage, Math.Round(
                    20d * Math.Log10(1.349858d),
                    2,
                    MidpointRounding.AwayFromZero)
                .ToString("F2", System.Globalization.CultureInfo.CurrentCulture));
            StringAssert.Contains(dialogs.LastMessage, "range-maximum.bms");
            StringAssert.Contains(dialogs.LastMessage, "74%");
            StringAssert.Contains(dialogs.LastMessage, "0.7");
            StringAssert.Contains(dialogs.LastMessage, string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Resources.AudioConversionOtherFailureFormat,
                "other1.bms",
                "read failed"));
            Assert.IsFalse(dialogs.LastMessage.Contains(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Resources.AudioConversionOmittedSourcesFormat,
                "other1.bms",
                4), StringComparison.Ordinal));
            StringAssert.Contains(dialogs.LastMessage, string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Resources.AudioConversionOtherFailureFormat,
                "other3.bms",
                "flush failed"));
            StringAssert.Contains(dialogs.LastMessage, string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Resources.AudioConversionOtherFailuresRemainingFormat,
                4,
                1));
            Assert.IsFalse(dialogs.LastMessage.Contains("other4.bms", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>利用者指定の変換品質2を受付後のexecutorへ渡し、既定値に戻す誤りを検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public async Task RunAsync_PassesCapturedResamplingQualityToConversionExecutor()
    {
        string root = CreateRoot();
        try
        {
            string chartPath = CreateChartFile(root, "song.bms");
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [root]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted)
            };
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (_, _, settings, _, report) =>
                {
                    Assert.AreEqual(2, settings.SampleRateConversionQuality);
                    report(new SelectedChartAudioConversionFileResult("song.bms"));
                }
            };
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(
                dialogs,
                new RecordingPlayback(events),
                executor,
                events,
                sampleRateConversionQuality: 2);

            SelectedChartAudioConversionResult result = await owner.RunAsync(new SelectedChartAudioConversionRequest([
                CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
            ]));

            Assert.AreEqual(SelectedChartAudioConversionStatus.Completed, result.Status);
            Assert.AreEqual(1, result.SucceededCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>peakと指定増幅率から安全な0.1刻みの助言を表示し、最小値境界を越える入力で実行不能を隠す誤りを検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [DataTestMethod]
    // 通常の減衰、増幅済み、最小0.5で成立する境界、その直外の実行不能を分けます。
    [DataRow(1.349858d, 1f, "74%", "0.7", false)]
    [DataRow(2d, 1.5f, "50%", "0.7", false)]
    [DataRow(2.5d, 1.25f, "40%", "0.5", false)]
    [DataRow(2.5001d, 1.2499f, "40%", null, true)]
    public async Task RunAsync_RangeAdviceUsesCapturedAmplificationAndSafeTenths(
        double peak,
        float amplifier,
        string expectedCurrentGainPercent,
        string expectedSuggestedGain,
        bool expectedImpossible)
    {
        string root = CreateRoot();
        try
        {
            string chartPath = CreateChartFile(root, "range.bms");
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [root]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted)
            };
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (_, _, _, _, report) => report(
                    new SelectedChartAudioConversionFileResult("range.bms", new AudioOutputRangeException(peak)))
            };
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(
                dialogs,
                new RecordingPlayback(events),
                executor,
                events,
                encoderAmplifier: amplifier);

            await owner.RunAsync(new SelectedChartAudioConversionRequest([
                CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
            ]));

            if (expectedImpossible)
            {
                string peakDb = Math.Round(
                        20d * Math.Log10(peak),
                        2,
                        MidpointRounding.AwayFromZero)
                    .ToString("F2", System.Globalization.CultureInfo.CurrentCulture);
                StringAssert.Contains(dialogs.LastMessage, string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Resources.AudioConversionRangeFailureImpossibleFormat,
                    1,
                    peakDb,
                    "range.bms"));
                Assert.IsFalse(dialogs.LastMessage.Contains(expectedCurrentGainPercent, StringComparison.Ordinal));
                Assert.IsFalse(dialogs.LastMessage.Contains(
                    float.Parse("0.5", System.Globalization.CultureInfo.InvariantCulture).ToString(
                        "F1",
                        System.Globalization.CultureInfo.CurrentCulture),
                    StringComparison.Ordinal));
            }
            else
            {
                StringAssert.Contains(dialogs.LastMessage, expectedCurrentGainPercent);
                StringAssert.Contains(dialogs.LastMessage, float.Parse(
                    expectedSuggestedGain,
                    System.Globalization.CultureInfo.InvariantCulture).ToString(
                        "F1",
                        System.Globalization.CultureInfo.CurrentCulture));
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>再生停止の実Taskが未完了の間はwriterを開始せず、停止完了後に一回実行する順序違反を検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public async Task RunAsync_WaitsForPlaybackCleanupBeforeStartingWriter()
    {
        string root = CreateRoot();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SelectedChartAudioConversionResult>? operation = null;
        try
        {
            string chartPath = CreateChartFile(root, "song.bms");
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [root]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted),
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            var executor = new RecordingExecutor(events) { ExecuteAction = (_, _, _, _, report) => report(new SelectedChartAudioConversionFileResult(chartPath)) };
            var playback = new RecordingPlayback(events)
            {
                StopCompletion = () => { entered.SetResult(); return cleanup.Task; }
            };
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, playback, executor, events);
            operation = owner.RunAsync(new SelectedChartAudioConversionRequest([CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)]));
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation, "audio-conversion.playback-stop");
            Assert.IsFalse(operation.IsCompleted);
            Assert.AreEqual(0, executor.CallCount);
            cleanup.SetResult();
            await operation;
            Assert.AreEqual(1, executor.CallCount);
        }
        finally
        {
            cleanup.TrySetResult();
            if (operation != null) { await operation; }
            DeleteRoot(root);
        }
    }

    /// <summary>保存先選択の取消は再生停止・writer・完了通知を始めず、未受理操作の副作用を検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public async Task RunAsync_PickerCancellationDoesNotStopPlaybackOrStartWriter()
    {
        string root = CreateRoot();
        try
        {
            string chartPath = CreateChartFile(root, "song.bms");
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.CancelledByUser)
            };
            var executor = new RecordingExecutor(events);
            var playback = new RecordingPlayback(events);
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, playback, executor, events);

            SelectedChartAudioConversionResult result = await owner.RunAsync(
                new SelectedChartAudioConversionRequest([
                    CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
                ]));

            Assert.AreEqual(SelectedChartAudioConversionStatus.Cancelled, result.Status);
            Assert.AreEqual("picker", events.Join("|"));
            Assert.AreEqual(0, executor.CallCount);
            Assert.AreEqual(0, playback.StopCalls);
            Assert.AreEqual(0, dialogs.MessageCalls);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>二件の実行へ受付時設定と順序を渡し、成功・失敗件数と停止後実行・実行後一回通知の接続漏れを検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public async Task RunAsync_AcceptedRoutePreservesCountsSettingsAndTerminalNotificationOrder()
    {
        string root = CreateRoot();
        try
        {
            string firstPath = CreateChartFile(root, "first.bmson");
            string secondPath = CreateChartFile(root, "second.bms");
            ChartOperationTarget first = CreateResolvedTarget(firstPath);
            ChartOperationTarget second = CreateResolvedTarget(secondPath);
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [root]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted),
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            IReadOnlyList<ChartFile>? executedCharts = null;
            SelectedChartAudioConversionSettingsSnapshot? executedSettings = null;
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (charts, _, settings, cancellationToken, report) =>
                {
                    executedCharts = charts.ToArray();
                    executedSettings = settings;
                    Assert.IsFalse(cancellationToken.IsCancellationRequested);
                    report(new SelectedChartAudioConversionFileResult(firstPath));
                    report(new SelectedChartAudioConversionFileResult(secondPath, new InvalidOperationException("render failed")));
                }
            };
            var playback = new RecordingPlayback(events);
            var fallbackValues = new List<EncoderType>();
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, playback, executor, events, fallbackValues);

            SelectedChartAudioConversionResult result = await owner.RunAsync(
                new SelectedChartAudioConversionRequest([first, second]));

            Assert.AreEqual(SelectedChartAudioConversionStatus.Completed, result.Status);
            Assert.AreEqual(2, result.TotalCount);
            Assert.AreEqual(2, result.CompletedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(1, result.SucceededCount);
            Assert.AreEqual(0, result.UnprocessedCount);
            Assert.AreEqual(1, executor.CallCount);
            Assert.AreEqual(root, executor.SaveDirectory);
            Assert.IsNotNull(executedCharts);
            CollectionAssert.AreEqual(new[] { first.Chart, second.Chart }, executedCharts.ToArray());
            Assert.IsNotNull(executedSettings);
            Assert.AreEqual(EncoderType.MP3_LAME, executedSettings.Encoder);
            Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, executedSettings.EncoderSampleRate);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, executedSettings.EncoderFormat);
            Assert.AreEqual(AudioNormalization.None, executedSettings.EncoderNormalization);
            Assert.AreEqual(0.8f, executedSettings.EncoderQuality);
            Assert.AreEqual(1f, executedSettings.EncoderAmplifier);
            Assert.AreEqual("%TITLE%", executedSettings.EncodeFileNameFormat);
            Assert.AreEqual(AudioResamplingQuality.Default, executedSettings.SampleRateConversionQuality);
            Assert.AreEqual(1, playback.StopCalls);
            Assert.AreEqual(1, dialogs.MessageCalls);
            Assert.AreEqual("MP3 LAME", dialogs.ProgressLabel.Split('-')[0].Trim());
            Assert.IsTrue(events.IndexOf("picker") < events.IndexOf("playback"));
            Assert.IsTrue(events.IndexOf("playback") < events.IndexOf("progress"));
            Assert.IsTrue(events.IndexOf("playback") < events.IndexOf("execute"));
            Assert.IsTrue(events.IndexOf("execute") < events.IndexOf("message"));
            Assert.IsTrue(events.IndexOf("progress") < events.IndexOf("message"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>選択直後の保存先消失は停止前に失敗し、利用者が再作成した次の要求を受け付けることから受付の残留を検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public async Task RunAsync_MissingSelectedOutputDirectoryDoesNotStopPlaybackAndCanRetry()
    {
        string root = CreateRoot();
        try
        {
            string chartPath = CreateChartFile(root, "song.bms");
            string outputDirectory = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDirectory);
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [outputDirectory]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted),
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            bool removeOutputBeforeReturn = true;
            dialogs.FolderPickerHandler = () =>
            {
                if (removeOutputBeforeReturn)
                {
                    Directory.Delete(outputDirectory, recursive: true);
                    removeOutputBeforeReturn = false;
                }
                return Task.FromResult(dialogs.FolderResult);
            };
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (_, _, _, _, report) => report(new SelectedChartAudioConversionFileResult("song.bms"))
            };
            var playback = new RecordingPlayback(events);
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, playback, executor, events);
            var request = new SelectedChartAudioConversionRequest([
                CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
            ]);

            await Assert.ThrowsExceptionAsync<DirectoryNotFoundException>(
                () => owner.RunAsync(request));

            Assert.AreEqual(0, playback.StopCalls);
            Assert.AreEqual(0, executor.CallCount);
            Assert.AreEqual(-1, events.IndexOf("progress"));
            Assert.AreEqual(0, dialogs.MessageCalls);

            Directory.CreateDirectory(outputDirectory);
            SelectedChartAudioConversionResult retryResult = await owner.RunAsync(request);

            Assert.AreEqual(SelectedChartAudioConversionStatus.Completed, retryResult.Status);
            Assert.AreEqual(2, dialogs.PickerCalls);
            Assert.AreEqual(1, playback.StopCalls);
            Assert.AreEqual(1, executor.CallCount);
            Assert.AreEqual(1, dialogs.MessageCalls);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>再生停止中に保存先が消失するとexecutorが出力前に拒否し、native解放や成功通知を誤って開始する経路を検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public async Task RunAsync_OutputDirectoryRemovedWhenPlaybackStopsIsRejectedByExecutor()
    {
        string root = CreateRoot();
        try
        {
            string chartPath = CreateChartFile(root, "song.bms");
            string outputDirectory = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDirectory);
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [outputDirectory]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted),
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            int cleanupCalls = 0;
            var cleanupFailure = new InvalidOperationException("encoder cleanup should not be reached");
            var executor = new BassSelectedChartAudioConversionExecutor(
                () =>
                {
                    cleanupCalls++;
                    throw cleanupFailure;
                },
                _ => true,
                new BassAudioSessionLease());
            var playback = new RecordingPlayback(events)
            {
                StopAction = () => Directory.Delete(outputDirectory, recursive: true)
            };
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, playback, executor, events);

            await Assert.ThrowsExceptionAsync<DirectoryNotFoundException>(
                () => owner.RunAsync(new SelectedChartAudioConversionRequest([
                    CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
                ])));

            Assert.AreEqual(1, playback.StopCalls);
            Assert.AreEqual(0, cleanupCalls);
            Assert.AreEqual(0, dialogs.MessageCalls);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>受理済みworkerの例外を同一の原因として返し、完了通知や成功結果へ読み替える誤りを検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public async Task RunAsync_AcceptedProgressPropagatesWorkerFailureWithoutCompletion()
    {
        string root = CreateRoot();
        try
        {
            string chartPath = CreateChartFile(root, "song.bms");
            var events = new EventLog();
            var workerFailure = new InvalidOperationException("worker cleanup failed");
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [root]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted),
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (_, _, _, _, _) => throw workerFailure
            };
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, new RecordingPlayback(events), executor, events);

            InvalidOperationException observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => owner.RunAsync(new SelectedChartAudioConversionRequest([
                    CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
                ])));

            Assert.AreSame(workerFailure, observed);
            Assert.AreEqual(1, executor.CallCount);
            Assert.AreEqual(0, dialogs.MessageCalls);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>実worker保持中は次要求をBusyで拒否し、worker終端後には再実行できることから早期開放と受付残留を検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)] // 任意Busy通知がthrowしても実workerと次受付を維持。
    public async Task RunAsync_AcceptedWorkerRejectsConcurrentRequestAndGateReopensAfterCompletion(bool busyNoticeThrows)
    {
        string root = CreateRoot();
        var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseWorker = new ManualResetEventSlim();
        Task<SelectedChartAudioConversionResult>? first = null;
        try
        {
            string chartPath = CreateChartFile(root, "song.bms");
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [root]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted)
            };
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (_, _, _, _, report) =>
                {
                    workerEntered.TrySetResult();
                    releaseWorker.Wait();
                    report(new SelectedChartAudioConversionFileResult("song.bms"));
                }
            };
            var playback = new RecordingPlayback(events);
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, playback, executor, events);
            var request = new SelectedChartAudioConversionRequest([
                CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
            ]);
            first = owner.RunAsync(request);
            Task reached = await Task.WhenAny(workerEntered.Task, first);
            if (reached == first) { await first; }
            Assert.AreSame(workerEntered.Task, reached);
            dialogs.MessageFailure = busyNoticeThrows ? new IOException("optional Busy notice") : null;
            Assert.AreEqual(SelectedChartAudioConversionStatus.Busy, (await owner.RunAsync(request)).Status);
            dialogs.MessageFailure = null;
            Assert.AreEqual(1, executor.CallCount);
            Assert.AreEqual(1, playback.StopCalls);
            releaseWorker.Set();
            Assert.AreEqual(SelectedChartAudioConversionStatus.Completed, (await first).Status);
            Assert.AreEqual(SelectedChartAudioConversionStatus.Completed, (await owner.RunAsync(request)).Status);
            Assert.AreEqual(3, dialogs.PickerCalls, "副作用のない保存先選択後に受付を判定します。");
            Assert.AreEqual(2, executor.CallCount);
        }
        finally
        {
            releaseWorker.Set();
            try { if (first != null) { await first; } }
            finally { DeleteRoot(root); }
        }
    }

    /// <summary>進捗経路の例外と取消callbackの例外が重なってもworker終端まで受付を保持し、元進捗失敗の消失や早期再受付を検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public async Task RunAsync_ProgressExceptionDrainsWorkerBeforeLibraryAdmissionReopens()
    {
        string root = CreateRoot();
        using var releaseWorker = new ManualResetEventSlim();
        var workerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationCallbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SelectedChartAudioConversionResult>? first = null;
        try
        {
            string chartPath = CreateChartFile(root, "song.bms");
            var events = new EventLog();
            var progressFailure = new InvalidOperationException("progress route threw");
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [root]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted),
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
                ProgressHandler = async () =>
                {
                    await workerStarted.Task;
                    throw progressFailure;
                }
            };
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (_, _, _, cancellationToken, _) =>
                {
                    using CancellationTokenRegistration registration = cancellationToken.Register(
                        () =>
                        {
                            cancellationCallbackEntered.TrySetResult();
                            throw new InvalidOperationException("cancellation callback failed");
                        });
                    workerStarted.TrySetResult();
                    WaitHandle.WaitAny([cancellationToken.WaitHandle, releaseWorker.WaitHandle]);
                    releaseWorker.Wait();
                }
            };
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, new RecordingPlayback(events), executor, events);
            var request = new SelectedChartAudioConversionRequest([
                CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
            ]);

            first = owner.RunAsync(request);
            await TestUiDispatcherHost.AwaitNotificationAsync(cancellationCallbackEntered.Task, first, "audio-conversion.cancel-callback");

            Assert.AreEqual(SelectedChartAudioConversionStatus.Busy, (await owner.RunAsync(request)).Status);
            Assert.AreEqual(2, dialogs.PickerCalls);

            releaseWorker.Set();
            InvalidOperationException observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await first);
            Assert.AreSame(progressFailure, observed);

            dialogs.ProgressHandler = null;
            executor.ExecuteAction = (_, _, _, _, report) => report(new SelectedChartAudioConversionFileResult("song.bms"));
            Assert.AreEqual(
                SelectedChartAudioConversionStatus.Completed,
                (await owner.RunAsync(request)).Status);
            Assert.AreEqual(3, dialogs.PickerCalls);
            Assert.AreEqual(2, executor.CallCount);
        }
        finally
        {
            releaseWorker.Set();
            if (first != null)
            {
                try
                {
                    await first;
                }
                catch
                {
                }
            }
            DeleteRoot(root);
        }
    }

    /// <summary>未表示・Failed・throwの任意完了通知でも実変換の対象・件数・結果を保持し、通知を変換故障へ合成する誤りを検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [DataTestMethod]
    [DataRow(0)] // owner不在の未表示。
    [DataRow(1)] // 表示窓口がFailedを返す。
    [DataRow(2)] // 表示呼出しが同期throwする。
    public async Task RunAsync_CompletionNotificationFailurePreservesConversionResult(int notificationFailureKind)
    {
        string root = CreateRoot();
        try
        {
            string chartPath = CreateChartFile(root, "song.bms");
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [root]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted),
                MessageResult = UiDialogResult.NotShown(notificationFailureKind == 0 ? UiDialogStatus.OwnerUnavailable : UiDialogStatus.Failed),
                MessageFailure = notificationFailureKind == 2 ? new IOException("optional completion notice") : null
            };
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (_, _, _, _, report) => report(new SelectedChartAudioConversionFileResult("song.bms"))
            };
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, new RecordingPlayback(events), executor, events);

            SelectedChartAudioConversionResult result = await owner.RunAsync(new SelectedChartAudioConversionRequest([
                CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
            ]));

            Assert.AreEqual(SelectedChartAudioConversionStatus.Completed, result.Status);
            Assert.AreEqual(1, result.TotalCount);
            Assert.AreEqual(1, result.CompletedCount);
            Assert.AreEqual(0, result.FailedCount);
            Assert.AreEqual(1, result.FileResults.Count);
            Assert.IsTrue(result.FileResults[0].Succeeded);
            Assert.AreEqual(1, dialogs.MessageCalls);
            Assert.AreEqual(1, executor.CallCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>進捗失敗は実workerへ取消を伝えて終端を待ち、取消callback例外で元原因を失う誤りや成功通知を検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public async Task RunAsync_ProgressFailureCancelsExecutionAndDoesNotShowCompletion()
    {
        string root = CreateRoot();
        var workerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            string chartPath = CreateChartFile(root, "song.bms");
            var events = new EventLog();
            var progressFailure = new InvalidOperationException("progress failed");
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [root]),
                ProgressResult = new UiProgressResult(UiDialogStatus.Failed, error: progressFailure),
                ProgressHandler = async () =>
                {
                    await workerStarted.Task;
                    return new UiProgressResult(UiDialogStatus.Failed, error: progressFailure);
                }
            };
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (_, _, _, cancellationToken, _) =>
                {
                    using CancellationTokenRegistration registration = cancellationToken.Register(
                        () => throw new InvalidOperationException("cancellation callback failed"));
                    workerStarted.TrySetResult();
                    cancellationToken.WaitHandle.WaitOne();
                }
            };
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, new RecordingPlayback(events), executor, events);

            InvalidOperationException observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => owner.RunAsync(new SelectedChartAudioConversionRequest([
                    CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
                ])));

            Assert.AreSame(progressFailure, observed.InnerException);
            Assert.AreEqual(1, executor.CallCount);
            Assert.AreEqual(0, dialogs.MessageCalls);
            StringAssert.Contains(events.Join("|"), "playback");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>利用者取消でcallbackも失敗しても実worker終端までBusyを維持し、取消結果と次要求受付への接続漏れを検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public async Task RunAsync_UserCancellationDrainsWorkerAndSuppressesCallbackFailure()
    {
        string root = CreateRoot();
        using var releaseWorker = new ManualResetEventSlim();
        var workerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationCallbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SelectedChartAudioConversionResult>? first = null;
        try
        {
            string chartPath = CreateChartFile(root, "song.bms");
            var events = new EventLog();
            var dialogs = new RecordingDialogService(events)
            {
                FolderResult = new UiFolderPickerResult(UiDialogStatus.Accepted, [root]),
                ProgressResult = new UiProgressResult(UiDialogStatus.CancelledByUser),
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
                ProgressHandler = async () =>
                {
                    await workerStarted.Task;
                    return new UiProgressResult(UiDialogStatus.CancelledByUser);
                }
            };
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (_, _, _, cancellationToken, _) =>
                {
                    using CancellationTokenRegistration registration = cancellationToken.Register(
                        () =>
                        {
                            cancellationCallbackEntered.TrySetResult();
                            throw new InvalidOperationException("cancellation callback failed");
                        });
                    workerStarted.TrySetResult();
                    cancellationToken.WaitHandle.WaitOne();
                    releaseWorker.Wait();
                }
            };
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, new RecordingPlayback(events), executor, events);
            var request = new SelectedChartAudioConversionRequest([
                CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
            ]);

            first = owner.RunAsync(request);
            await TestUiDispatcherHost.AwaitNotificationAsync(cancellationCallbackEntered.Task, first, "audio-conversion.cancel-callback");

            Assert.AreEqual(SelectedChartAudioConversionStatus.Busy, (await owner.RunAsync(request)).Status);
            Assert.AreEqual(2, dialogs.PickerCalls);

            releaseWorker.Set();
            Assert.AreEqual(SelectedChartAudioConversionStatus.Cancelled, (await first).Status);

            dialogs.ProgressHandler = null;
            dialogs.ProgressResult = new UiProgressResult(UiDialogStatus.Accepted);
            executor.ExecuteAction = (_, _, _, _, report) => report(new SelectedChartAudioConversionFileResult("song.bms"));
            Assert.AreEqual(
                SelectedChartAudioConversionStatus.Completed,
                (await owner.RunAsync(request)).Status);
            Assert.AreEqual(3, dialogs.PickerCalls);
            Assert.AreEqual(2, executor.CallCount);
        }
        finally
        {
            releaseWorker.Set();
            if (first != null)
            {
                try
                {
                    await first;
                }
                catch
                {
                }
            }
            DeleteRoot(root);
        }
    }

    private static SelectedChartAudioConversionWorkflowOwner CreateOwner(
        RecordingDialogService dialogs,
        RecordingPlayback playback,
        ISelectedChartAudioConversionExecutor executor,
        EventLog events,
        List<EncoderType> fallbackValues = null!,
        float encoderAmplifier = 1f,
        int sampleRateConversionQuality = AudioResamplingQuality.Default)
    {
        return new SelectedChartAudioConversionWorkflowOwner(
            () => new SelectedChartAudioConversionSettingsSnapshot(
                EncoderType.MP3_LAME,
                SampleRate.SAMPLE_RATE_44100Hz,
                SampleFormat.SAMPLE_INT_16BIT,
                AudioNormalization.None,
                0.8f,
                string.Empty,
                encoderAmplifier,
                "%TITLE%",
                "MP3 LAME",
                "44100Hz",
                "16bit",
                sampleRateConversionQuality),
            encoder => fallbackValues?.Add(encoder),
            playback,
            dialogs,
            executor, new ChartFileOperationSynchronizer());
    }

    private static ChartOperationTarget CreateTarget(string path, ChartOperationCapabilities capabilities)
    {
        return new ChartOperationTarget(
            CreateChart(path),
            null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            capabilities);
    }

    private static ChartOperationTarget CreateResolvedTarget(
        string path,
        ChartOperationSourceScope sourceScope = ChartOperationSourceScope.Library)
    {
        ChartFile chart = CreateChart(path);
        ChartListSourceRow row = sourceScope == ChartOperationSourceScope.PendingPackage
            ? ChartListSourceRow.FromPackageChartEntry(PackageChartEntry.FromChart(chart))
            : ChartListSourceRow.FromChartFile(chart);
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget target));
        return target;
    }

    private static ChartFile CreateChart(string path)
    {
        bool bmson = string.Equals(Path.GetExtension(path), ".bmson", StringComparison.OrdinalIgnoreCase);
        return new ChartFile(bmson ? ChartFileKind.Bmson : ChartFileKind.Bms, path, "hash-" + Path.GetFileNameWithoutExtension(path), null, "Title", "Title", "Artist", "Genre", "Folder", string.Empty, string.Empty, null, null, null);
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(SelectedChartAudioConversionWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateChartFile(string root, string fileName)
    {
        string path = Path.Combine(root, fileName);
        File.WriteAllText(path, "#TITLE:Test;\n");
        return path;
    }

    private static byte[] CreatePcmWave()
    {
        const int sampleRate = 48000;
        const int frameCount = 4800;
        const int bytesPerFrame = sizeof(short);
        int dataLength = frameCount * bytesPerFrame;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * bytesPerFrame);
            writer.Write((ushort)bytesPerFrame);
            writer.Write((ushort)(bytesPerFrame * 8));
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            for (int frame = 0; frame < frameCount; frame++)
            {
                writer.Write((short)4096);
            }
        }
        return stream.ToArray();
    }

    /// <summary>解析器に依存しない48kHz・4800frameの定値ステレオFloat32入力を作ります。</summary>
    private static byte[] CreateStereoFloatWave(float left, float right)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + 4800 * 8);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((ushort)3);
        writer.Write((ushort)2);
        writer.Write(48000);
        writer.Write(48000 * 8);
        writer.Write((ushort)8);
        writer.Write((ushort)32);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(4800 * 8);
        for (int frame = 0; frame < 4800; frame++) { writer.Write(left); writer.Write(right); }
        return stream.ToArray();
    }

    /// <summary>入力から独立して決めた無音区間と音源末尾まで、実encoder出力の全PCMを検査します。</summary>
    private static void AssertNativeStereoWave(string path, int soundStart, int frameCount, float left, float right)
    {
        AudioTestWaveFile wave = AudioTestWaveFileReader.Read(File.ReadAllBytes(path));
        Assert.AreEqual((ushort)3, wave.Format);
        Assert.AreEqual((ushort)2, wave.Channels);
        Assert.AreEqual(48000, wave.SampleRate);
        Assert.AreEqual((ushort)32, wave.BitsPerSample);
        Assert.AreEqual(0, wave.DataLength % 8);
        Assert.AreEqual(frameCount * 8, wave.DataLength);
        for (int frame = 0; frame < frameCount; frame++)
        {
            float actualLeft = BitConverter.ToSingle(wave.Bytes, wave.DataOffset + frame * 8);
            float actualRight = BitConverter.ToSingle(wave.Bytes, wave.DataOffset + frame * 8 + 4);
            Assert.IsTrue(float.IsFinite(actualLeft) && float.IsFinite(actualRight));
            Assert.AreEqual(frame < soundStart ? 0f : left, actualLeft, 0.000001f, "左PCM frame=" + frame);
            Assert.AreEqual(frame < soundStart ? 0f : right, actualRight, 0.000001f, "右PCM frame=" + frame);
        }
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingPlayback : ISelectedChartAudioConversionPlaybackPort
    {
        private readonly EventLog events;

        internal RecordingPlayback(EventLog events)
        {
            this.events = events;
        }

        internal int StopCalls { get; private set; }

        internal Action? StopAction { get; set; }

        internal Func<Task>? StopCompletion { get; set; }

        public Task StopPlayback()
        {
            StopCalls++;
            events.Add("playback");
            StopAction?.Invoke();
            return StopCompletion?.Invoke() ?? Task.CompletedTask;
        }
    }

    private sealed class RecordingExecutor : ISelectedChartAudioConversionExecutor
    {
        private readonly EventLog events;

        internal RecordingExecutor(EventLog events)
        {
            this.events = events;
        }

        internal int CallCount { get; private set; }

        internal string SaveDirectory { get; private set; } = null!;

        internal Action<IReadOnlyList<ChartFile>, string, SelectedChartAudioConversionSettingsSnapshot, CancellationToken, Action<SelectedChartAudioConversionFileResult>> ExecuteAction { get; set; } = null!;

        public void Execute(
            IReadOnlyList<ChartFile> bmsFiles,
            string saveDirectory,
            SelectedChartAudioConversionSettingsSnapshot settings,
            CancellationToken cancellationToken,
            Action<EncoderType> applyEncoderFallback,
            Action<SelectedChartAudioConversionFileResult> reportFileCompleted)
        {
            CallCount++;
            SaveDirectory = saveDirectory;
            events.Add("execute");
            ExecuteAction?.Invoke(bmsFiles, saveDirectory, settings, cancellationToken, reportFileCompleted);
        }
    }

    private sealed class RecordingDialogService : IUiDialogService
    {
        private readonly EventLog events;

        internal RecordingDialogService(EventLog events)
        {
            this.events = events;
        }

        internal UiFolderPickerResult FolderResult { get; set; } = new(UiDialogStatus.CancelledByUser);

        internal UiProgressResult ProgressResult { get; set; } = new(UiDialogStatus.Accepted);

        internal UiDialogResult MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal string ProgressLabel { get; private set; } = null!;

        internal int MessageCalls { get; private set; }

        internal Exception? MessageFailure { get; set; }

        internal string LastMessage { get; private set; } = string.Empty;

        internal int PickerCalls { get; private set; }

        internal Func<Task<UiFolderPickerResult>>? FolderPickerHandler { get; set; }

        internal Func<Task<UiProgressResult>>? ProgressHandler { get; set; }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            events.Add("message");
            MessageCalls++;
            LastMessage = request.MessageBoxText;
            if (MessageFailure != null) { throw MessageFailure; }
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(
            UiFolderPickerRequest request,
            CancellationToken cancellationToken = default)
        {
            events.Add("picker");
            PickerCalls++;
            return FolderPickerHandler?.Invoke() ?? Task.FromResult(FolderResult);
        }

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default)
        {
            ProgressLabel = request.Label;
            events.Add("progress");
            if (ProgressHandler != null)
            {
                return await ProgressHandler();
            }
            if (ProgressResult.Status == UiDialogStatus.Accepted)
            {
                using var worker = new BackgroundWorker();
                await operation(new UiProgressContext(new ProgressDialogContext(worker, new DoWorkEventArgs(null))));
            }
            return ProgressResult;
        }
    }

    private sealed class EventLog
    {
        private readonly List<string> entries = [];

        internal void Add(string value)
        {
            lock (entries)
            {
                entries.Add(value);
            }
        }

        internal int IndexOf(string value)
        {
            lock (entries)
            {
                return entries.IndexOf(value);
            }
        }

        internal string Join(string separator)
        {
            lock (entries)
            {
                return string.Join(separator, entries);
            }
        }
    }
}
