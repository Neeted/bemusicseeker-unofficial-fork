using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Parago.Windows;
using Ribbit.Media.Audio;
using ModelBmsFile = BeMusicSeeker.Models.BMSFile;

namespace BeMusicSeeker.Tests;

[TestClass]
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
            var owner = CreateOwner(dialogs, playback, executor, events);

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
                    report(true);
                    report(false);
                }
            };
            var playback = new RecordingPlayback(events);
            var fallbackValues = new List<EncoderType>();
            var owner = CreateOwner(dialogs, playback, executor, events, fallbackValues);

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
            Assert.IsTrue(events.IndexOf("execute") < events.IndexOf("message"));
            Assert.IsTrue(events.IndexOf("progress") < events.IndexOf("message"));
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
            var owner = CreateOwner(dialogs, new RecordingPlayback(events), executor, events);

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
                ExecuteAction = (_, _, _, _, report) => report(true)
            };
            var owner = CreateOwner(dialogs, new RecordingPlayback(events), executor, events);
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
        var cancellationObserved = new TaskCompletionSource(
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
                        () => throw new InvalidOperationException("cancellation callback failed"));
                    workerStarted.TrySetResult();
                    WaitHandle.WaitAny([cancellationToken.WaitHandle, releaseWorker.WaitHandle]);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancellationObserved.TrySetResult();
                    }
                    releaseWorker.Wait();
                }
            };
            var owner = CreateOwner(dialogs, new RecordingPlayback(events), executor, events);
            var request = new SelectedChartAudioConversionRequest([
                CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
            ]);

            first = owner.RunAsync(request);
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => owner.RunAsync(request));
            Assert.AreEqual(1, dialogs.PickerCalls);

            releaseWorker.Set();
            InvalidOperationException observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await first);
            Assert.AreSame(progressFailure, observed);

            dialogs.ProgressHandler = null;
            executor.ExecuteAction = (_, _, _, _, report) => report(true);
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
                ExecuteAction = (_, _, _, _, report) => report(true)
            };
            var owner = CreateOwner(dialogs, new RecordingPlayback(events), executor, events);

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
            var owner = CreateOwner(dialogs, new RecordingPlayback(events), executor, events);

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
        var cancellationObserved = new TaskCompletionSource(
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
                        () => throw new InvalidOperationException("cancellation callback failed"));
                    workerStarted.TrySetResult();
                    cancellationToken.WaitHandle.WaitOne();
                    cancellationObserved.TrySetResult();
                    releaseWorker.Wait();
                }
            };
            var owner = CreateOwner(dialogs, new RecordingPlayback(events), executor, events);
            var request = new SelectedChartAudioConversionRequest([
                CreateTarget(chartPath, ChartOperationCapabilities.ConvertToAudio)
            ]);

            first = owner.RunAsync(request);
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => owner.RunAsync(request));
            Assert.AreEqual(1, dialogs.PickerCalls);

            releaseWorker.Set();
            Assert.AreEqual(SelectedChartAudioConversionStatus.Cancelled, (await first).Status);

            dialogs.ProgressHandler = null;
            dialogs.ProgressResult = new UiProgressResult(UiDialogStatus.Accepted);
            executor.ExecuteAction = (_, _, _, _, report) => report(true);
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
        RecordingExecutor executor,
        EventLog events,
        List<EncoderType> fallbackValues = null!)
    {
        return new SelectedChartAudioConversionWorkflowOwner(
            () => new SelectedChartAudioConversionSettingsSnapshot(
                EncoderType.MP3_LAME,
                SampleRate.SAMPLE_RATE_44100Hz,
                SampleFormat.SAMPLE_INT_16BIT,
                AudioNormalization.None,
                0.8f,
                string.Empty,
                1f,
                "%TITLE%",
                "MP3 LAME",
                "44100Hz",
                "16bit"),
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

        public void StopPlayback()
        {
            StopCalls++;
            events.Add("playback");
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

        internal Action<IReadOnlyList<ModelBmsFile>, string, SelectedChartAudioConversionSettingsSnapshot, CancellationToken, Action<bool>> ExecuteAction { get; set; } = null!;

        public void Execute(
            IReadOnlyList<ModelBmsFile> bmsFiles,
            string saveDirectory,
            SelectedChartAudioConversionSettingsSnapshot settings,
            CancellationToken cancellationToken,
            Action<EncoderType> applyEncoderFallback,
            Action<bool> reportFileCompleted)
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

        internal int PickerCalls { get; private set; }

        internal Func<Task<UiFolderPickerResult>>? FolderPickerHandler { get; set; }

        internal Func<Task<UiProgressResult>>? ProgressHandler { get; set; }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            events.Add("message");
            MessageCalls++;
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
