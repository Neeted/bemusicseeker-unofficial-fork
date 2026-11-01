using System.IO;
using System.Text;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;
using static BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;

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
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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

    /// <summary>受付後に実入力を削除しencoder解放も失敗させ、元入力失敗と解放失敗の保持・native所有権・完了通知の抑止を検出します。</summary>
    /// <remarks>実BASS・encoder・共有設定を使用して出力や解放の接続を観測するため、native状態とログ設定を共有するケースは並列実行しません。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public async Task RunAsync_ProductionExecutorCleanupFailurePreservesPrimaryCauseAndNotifiesOnce()
    {
        await ApplicationLoggingTestScope.RunAsync(async loggingScope =>
        {
            string root = loggingScope.RootDirectory;
            string outputDirectory = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDirectory);
            string firstPath = CreateChartFile(root, "first.bms");
            string secondPath = CreateChartFile(root, "second.bms");
            BassAudioRuntime.Shutdown();
            _ = NLogWrapper.GetLogger(nameof(BassSelectedChartAudioConversionExecutor));
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

                AudioSourceFatalException failure = await Assert.ThrowsExactlyAsync<AudioSourceFatalException>(
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
                }
            }

        });
    }

    /// <summary>実入力失敗後にsession解放確認も失敗した場合は両原因と未解放leaseを保持し、成功通知へ進む誤りを検出します。</summary>
    /// <remarks>実BASS・encoder・共有設定を使用して出力や解放の接続を観測するため、native状態とログ設定を共有するケースは並列実行しません。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public async Task RunAsync_ProductionExecutorSessionCleanupFailureRetainsPrimaryAndAborts()
    {
        await ApplicationLoggingTestScope.RunAsync(async loggingScope =>
        {
            string root = loggingScope.RootDirectory;
            string outputDirectory = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDirectory);
            string firstPath = CreateChartFile(root, "first.bms");
            string secondPath = CreateChartFile(root, "second.bms");
            BassAudioRuntime.Shutdown();
            _ = NLogWrapper.GetLogger(nameof(BassSelectedChartAudioConversionExecutor));
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

                AudioSourceFatalException failure = await Assert.ThrowsExactlyAsync<AudioSourceFatalException>(
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
                }
            }

        });
    }

    /// <summary>先行ファイル失敗の後に成功通知callbackとsession解放が失敗しても現在のfatalを保持し、三件目の出力や所有権の消失を検出します。</summary>
    /// <remarks>実BASS・encoder・共有設定を使用して出力や解放の接続を観測するため、native状態とログ設定を共有するケースは並列実行しません。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public Task ProductionExecutor_PreservesCurrentFatalAndSessionCleanupFailureAfterPriorFileFailure()
    {
        return ApplicationLoggingTestScope.RunAsync(loggingScope =>
        {
            string root = loggingScope.RootDirectory;
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

                AudioSourceFatalException failure = Assert.ThrowsExactly<AudioSourceFatalException>(() => executor.Execute(
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
                }
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>実入力不在五件を対象と原因付きで記録し、音源一件欠落の譜面は残る音源を出力して一回通知する集約漏れを検出します。</summary>
    /// <remarks>実BASS・encoder・共有設定を使用して出力や解放の接続を観測するため、native状態とログ設定を共有するケースは並列実行しません。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public async Task RunAsync_ProductionExecutorLogsTargetAndCauseForEveryFailure()
    {
        await ApplicationLoggingTestScope.RunAsync(async loggingScope =>
        {
            string root = loggingScope.RootDirectory;
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
                }
            }

        });
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

}
