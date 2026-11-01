using System.Globalization;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Media;
using Ribbit.Media.Audio;
using static BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class SelectedChartAudioConversionWorkflowOwnerTests
{
    /// <summary>BMS/BMSONを混ぜた選択から既存の所有ファイルだけを順に残し、保留・不在を変換へ含める誤りを検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public void Request_FiltersCapabilityAndExistingFilesAcrossChartKindsInSelectionOrder()
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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

        Assert.ThrowsExactly<AudioSourceFatalException>(
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
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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

    /// <summary>成功・音源省略・range超過・四件の別失敗を一回に集約し、最大peakの助言・省略条件・表示件数の取り違えを検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public async Task RunAsync_CompletionSummarizesRangeAndOtherFailuresOnce()
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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

    /// <summary>fr-FRでもdB・割合・安全な0.1刻みの助言を固定表記で表示し、最小値境界を越える入力で実行不能を隠す誤りを検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    // 通常の減衰、増幅済み、最小0.5で成立する境界、その直外の実行不能を分けます。
    [DataRow(1.349858d, "2.61", 1f, "74%", "0.7", false)]
    [DataRow(2d, "6.02", 1.5f, "50%", "0.7", false)]
    [DataRow(2.5d, "7.96", 1.25f, "40%", "0.5", false)]
    [DataRow(2.5001d, "7.96", 1.2499f, "40%", null, true)]
    public async Task RunAsync_RangeAdviceUsesCapturedAmplificationAndSafeTenths(
        double peak,
        string expectedPeakDb,
        float amplifier,
        string expectedCurrentGainPercent,
        string expectedSuggestedGain,
        bool expectedImpossible)
    {
        CultureInfo previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
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
                string peakDb = expectedPeakDb;
                StringAssert.Contains(dialogs.LastMessage, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    Resources.AudioConversionRangeFailureImpossibleFormat,
                    1,
                    peakDb,
                    "range.bms"));
                Assert.IsFalse(dialogs.LastMessage.Contains(expectedCurrentGainPercent, StringComparison.Ordinal));
                Assert.IsFalse(dialogs.LastMessage.Contains(
                    "0.5",
                    StringComparison.Ordinal));
            }
            else
            {
                StringAssert.Contains(dialogs.LastMessage, expectedCurrentGainPercent);
                StringAssert.Contains(dialogs.LastMessage, expectedSuggestedGain);
                StringAssert.Contains(dialogs.LastMessage, expectedPeakDb);
            }
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
            DeleteRoot(root);
        }
    }

    /// <summary>再生停止の実Taskが未完了の間はwriterを開始せず、停止完了後に一回実行する順序違反を検出します。</summary>
    /// <remarks>実ファイルの存在で対象受付と保存先の再確認を観測し、変換・表示のportは局所記録へ差し替えます。</remarks>
    [TestMethod]
    public async Task RunAsync_WaitsForPlaybackCleanupBeforeStartingWriter()
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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

            await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(
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
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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

            await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(
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
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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

            InvalidOperationException observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
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
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)] // 任意Busy通知がthrowしても実workerと次受付を維持。
    public async Task RunAsync_AcceptedWorkerRejectsConcurrentRequestAndGateReopensAfterCompletion(bool busyNoticeThrows)
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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
            InvalidOperationException observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
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
    [TestMethod]
    [DataRow(0)] // owner不在の未表示。
    [DataRow(1)] // 表示窓口がFailedを返す。
    [DataRow(2)] // 表示呼出しが同期throwする。
    public async Task RunAsync_CompletionNotificationFailurePreservesConversionResult(int notificationFailureKind)
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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

            InvalidOperationException observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
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
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
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

}
