using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
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
using ModelBmsFile = BeMusicSeeker.Models.BMSFile;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SelectedChartAudioConversionWorkflowOwnerTests
{
    [TestMethod]
    public void Request_FiltersCapabilityBmsKindAndExistingFilesInSelectionOrder()
    {
        string root = CreateRoot();
        try
        {
            string firstPath = CreateChartFile(root, "first.bms");
            string secondPath = CreateChartFile(root, "second.bms");
            ChartOperationTarget first = CreateTarget(firstPath, ChartOperationCapabilities.ConvertToAudio);
            ChartOperationTarget ineligible = CreateTarget(CreateChartFile(root, "ignored.bms"), ChartOperationCapabilities.None);
            ChartOperationTarget second = CreateTarget(secondPath, ChartOperationCapabilities.ConvertToAudio);
            ChartOperationTarget missing = CreateTarget(Path.Combine(root, "missing.bms"), ChartOperationCapabilities.ConvertToAudio);

            var request = new SelectedChartAudioConversionRequest([first, ineligible, second, missing]);

            Assert.IsTrue(request.HasTargets);
            CollectionAssert.AreEqual(new[] { first, second }, request.Targets.ToArray());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

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

        Assert.ThrowsException<InvalidOperationException>(
            () => executor.ReleaseConversionSession(session, primaryException: null));
        Assert.AreSame(session, lease.Session);
        CollectionAssert.AreEqual(new[] { "encoder" }, events);

        executor.ReleaseConversionSession(session, primaryException: null);

        Assert.IsNull(lease.Session);
        CollectionAssert.AreEqual(new[] { "encoder", "encoder", "session" }, events);
    }

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
                        files[0].path,
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

            SelectedChartAudioConversionResult result = await owner.RunAsync(new SelectedChartAudioConversionRequest([
                CreateTarget(firstPath, ChartOperationCapabilities.ConvertToAudio),
                CreateTarget(secondPath, ChartOperationCapabilities.ConvertToAudio)
            ]));

            LogManager.Flush();
            Assert.AreEqual(1, result.CompletedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(1, result.UnprocessedCount);
            Assert.AreEqual(1, result.FileResults.Count);
            Assert.IsNotNull(result.FileResults[0].Error);
            StringAssert.Contains(result.FileResults[0].Error.ToString(), firstPath);
            Assert.AreEqual(1, dialogs.MessageCalls);
            StringAssert.Contains(dialogs.LastMessage, Resources.Failure + ": 2");
            StringAssert.Contains(dialogs.LastMessage, "first.bms");
            Assert.IsFalse(dialogs.LastMessage.Contains("second.bms", StringComparison.Ordinal));
            Assert.AreEqual(3, encoderReleaseAttempts);
            Assert.AreEqual(0, sessionReleaseAttempts);
            Assert.IsNotNull(sessionLease.Session, "未確認のnative所有権をsession leaseに保持します。");
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

    [TestMethod]
    public async Task RunAsync_ProductionExecutorSessionCleanupFailureRetainsFileFailuresAndNotifiesOnce()
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

            SelectedChartAudioConversionResult result = await owner.RunAsync(new SelectedChartAudioConversionRequest([
                CreateTarget(firstPath, ChartOperationCapabilities.ConvertToAudio),
                CreateTarget(secondPath, ChartOperationCapabilities.ConvertToAudio)
            ]));

            LogManager.Flush();
            Assert.AreEqual(2, result.CompletedCount);
            Assert.AreEqual(2, result.FailedCount);
            Assert.AreEqual(0, result.UnprocessedCount);
            Assert.AreEqual(2, result.FileResults.Count);
            StringAssert.Contains(result.FileResults[0].Error.ToString(), firstPath);
            StringAssert.Contains(result.FileResults[1].Error.ToString(), secondPath);
            Assert.AreEqual(1, dialogs.MessageCalls);
            StringAssert.Contains(dialogs.LastMessage, Resources.Failure + ": 2");
            StringAssert.Contains(dialogs.LastMessage, "first.bms");
            StringAssert.Contains(dialogs.LastMessage, "second.bms");
            Assert.AreEqual(4, encoderReleaseAttempts);
            Assert.AreEqual(1, sessionReleaseAttempts);
            Assert.IsNotNull(sessionLease.Session, "未確認のnative所有権をsession leaseに保持します。");
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

    [TestMethod]
    public async Task RunAsync_ProductionExecutorLogsTargetAndCauseForEveryFailure()
    {
        string root = CreateRoot();
        string outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(outputDirectory);
        string[] chartPaths = Enumerable.Range(1, 5)
            .Select(index => CreateChartFile(root, "song" + index + ".bms"))
            .ToArray();
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
                () => true,
                session =>
                {
                    sessionReleaseAttempts++;
                    return BassAudioPlayer.Free(session);
                },
                sessionLease);
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, playback, executor, events);

            SelectedChartAudioConversionResult result = await owner.RunAsync(new SelectedChartAudioConversionRequest(
                chartPaths.Select(path => CreateTarget(path, ChartOperationCapabilities.ConvertToAudio))));

            LogManager.Flush();
            Assert.AreEqual(chartPaths.Length, result.FailedCount);
            Assert.AreEqual(0, result.UnprocessedCount);
            Assert.AreEqual(1, dialogs.MessageCalls);
            StringAssert.Contains(dialogs.LastMessage, string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Resources.AudioConversionOtherFailuresRemainingFormat,
                chartPaths.Length,
                chartPaths.Length - 3));
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

    [TestMethod]
    public async Task RunAsync_CompletionSummarizesRangeAndOtherFailuresOnce()
    {
        string root = CreateRoot();
        try
        {
            string[] chartPaths = Enumerable.Range(1, 7)
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
                    report(new SelectedChartAudioConversionFileResult("success.bms"));
                    report(new SelectedChartAudioConversionFileResult("range-lower.bms", new AudioOutputRangeException(1.2d)));
                    report(new SelectedChartAudioConversionFileResult("range-maximum.bms", new AudioOutputRangeException(1.349858d)));
                    report(new SelectedChartAudioConversionFileResult("other1.bms", new IOException("read failed")));
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

            Assert.AreEqual(1, result.SucceededCount);
            Assert.AreEqual(6, result.FailedCount);
            Assert.AreEqual(1, dialogs.MessageCalls);
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

    [DataTestMethod]
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

    [TestMethod]
    public async Task RunAsync_AcceptedRoutePreservesCountsSettingsAndTerminalNotificationOrder()
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
                ProgressResult = new UiProgressResult(UiDialogStatus.Accepted),
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (_, _, _, _, report) =>
                {
                    report(new SelectedChartAudioConversionFileResult("success.bms"));
                    report(new SelectedChartAudioConversionFileResult("failure.bms", new InvalidOperationException("render failed")));
                }
            };
            var playback = new RecordingPlayback(events);
            var fallbackValues = new List<EncoderType>();
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, playback, executor, events, fallbackValues);

            SelectedChartAudioConversionResult result = await owner.RunAsync(
                new SelectedChartAudioConversionRequest([
                    CreateTarget(firstPath, ChartOperationCapabilities.ConvertToAudio),
                    CreateTarget(secondPath, ChartOperationCapabilities.ConvertToAudio)
                ]));

            Assert.AreEqual(SelectedChartAudioConversionStatus.Completed, result.Status);
            Assert.AreEqual(2, result.TotalCount);
            Assert.AreEqual(2, result.CompletedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(1, result.SucceededCount);
            Assert.AreEqual(0, result.UnprocessedCount);
            Assert.AreEqual(1, executor.CallCount);
            Assert.AreEqual(root, executor.SaveDirectory);
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

    [TestMethod]
    public async Task RunAsync_ConcurrentRequestIsRejectedBeforeDialogAndGateReopensAfterCompletion()
    {
        string root = CreateRoot();
        var pickerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePicker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
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
            dialogs.FolderPickerHandler = async () =>
            {
                pickerEntered.TrySetResult();
                await releasePicker.Task;
                return dialogs.FolderResult;
            };
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (_, _, _, _, report) => report(new SelectedChartAudioConversionFileResult("song.bms"))
            };
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, new RecordingPlayback(events), executor, events);
            var request = new SelectedChartAudioConversionRequest([
                CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
            ]);

            Task<SelectedChartAudioConversionResult> first = owner.RunAsync(request);
            await pickerEntered.Task;

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => owner.RunAsync(request));
            Assert.AreEqual(1, dialogs.PickerCalls);
            Assert.AreEqual(0, executor.CallCount);

            releasePicker.TrySetResult();
            Assert.AreEqual(SelectedChartAudioConversionStatus.Completed, (await first).Status);

            Assert.AreEqual(
                SelectedChartAudioConversionStatus.Completed,
                (await owner.RunAsync(request)).Status);
            Assert.AreEqual(2, dialogs.PickerCalls);
            Assert.AreEqual(2, executor.CallCount);
        }
        finally
        {
            releasePicker.TrySetResult();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RunAsync_ProgressExceptionDrainsWorkerBeforeSingleFlightGateReopens()
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
            await cancellationCallbackEntered.Task;

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => owner.RunAsync(request));
            Assert.AreEqual(1, dialogs.PickerCalls);

            releaseWorker.Set();
            InvalidOperationException observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await first);
            Assert.AreSame(progressFailure, observed);

            dialogs.ProgressHandler = null;
            executor.ExecuteAction = (_, _, _, _, report) => report(new SelectedChartAudioConversionFileResult("song.bms"));
            Assert.AreEqual(
                SelectedChartAudioConversionStatus.Completed,
                (await owner.RunAsync(request)).Status);
            Assert.AreEqual(2, dialogs.PickerCalls);
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

    [TestMethod]
    public async Task RunAsync_CompletionNotificationNotShownFailsInsteadOfReturningSuccess()
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
                MessageResult = UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable)
            };
            var executor = new RecordingExecutor(events)
            {
                ExecuteAction = (_, _, _, _, report) => report(new SelectedChartAudioConversionFileResult("song.bms"))
            };
            SelectedChartAudioConversionWorkflowOwner owner = CreateOwner(dialogs, new RecordingPlayback(events), executor, events);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => owner.RunAsync(new SelectedChartAudioConversionRequest([
                    CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
                ])));

            Assert.AreEqual(1, dialogs.MessageCalls);
            Assert.AreEqual(1, executor.CallCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

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
            await cancellationCallbackEntered.Task;

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => owner.RunAsync(request));
            Assert.AreEqual(1, dialogs.PickerCalls);

            releaseWorker.Set();
            Assert.AreEqual(SelectedChartAudioConversionStatus.Cancelled, (await first).Status);

            dialogs.ProgressHandler = null;
            dialogs.ProgressResult = new UiProgressResult(UiDialogStatus.Accepted);
            executor.ExecuteAction = (_, _, _, _, report) => report(new SelectedChartAudioConversionFileResult("song.bms"));
            Assert.AreEqual(
                SelectedChartAudioConversionStatus.Completed,
                (await owner.RunAsync(request)).Status);
            Assert.AreEqual(2, dialogs.PickerCalls);
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
            executor);
    }

    private static ChartOperationTarget CreateTarget(string path, ChartOperationCapabilities capabilities)
    {
        var file = new ModelBmsFile { path = path };
        var chart = new ChartFile(
            ChartFileKind.Bms,
            path,
            "hash-" + Path.GetFileNameWithoutExtension(path),
            null,
            "Title",
            "Title",
            "Artist",
            "Genre",
            "Folder",
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            file,
            null);
        return new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            capabilities);
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

        public void StopPlayback()
        {
            StopCalls++;
            events.Add("playback");
            StopAction?.Invoke();
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

        internal Action<IReadOnlyList<ModelBmsFile>, string, SelectedChartAudioConversionSettingsSnapshot, CancellationToken, Action<SelectedChartAudioConversionFileResult>> ExecuteAction { get; set; } = null!;

        public void Execute(
            IReadOnlyList<ModelBmsFile> bmsFiles,
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

        internal string LastMessage { get; private set; } = string.Empty;

        internal int PickerCalls { get; private set; }

        internal Func<Task<UiFolderPickerResult>>? FolderPickerHandler { get; set; }

        internal Func<Task<UiProgressResult>>? ProgressHandler { get; set; }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            events.Add("message");
            MessageCalls++;
            LastMessage = request.MessageBoxText;
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
