using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ScoreViewerRegistrationWorkflowOwnerTests
{
    /// <summary>登録状態のOK・拒否・nullと型不正、uploadの応答hashを区別し、非OKを成功として受理する誤りを検出します。</summary>
    [TestMethod]
    public void AppGatewayJsonParsers_PreserveStatusAndUploadContracts()
    {
        Assert.IsTrue(AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse(
            "{\"status\":\"OK\"}"));
        Assert.IsFalse(AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse(
            "{\"status\":\"REJECTED\"}"));
        Assert.IsFalse(AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse(
            "{\"status\":null}"));
        Assert.ThrowsException<FormatException>(() =>
            AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse("{\"status\":1}"));

        ScoreViewerUploadResponse accepted = AppScoreViewerRegistrationGateway.ParseUploadResponse(
            "{\"status\":\"OK\",\"md5\":\"server-hash\"}");
        Assert.IsTrue(accepted.Accepted);
        Assert.AreEqual("server-hash", accepted.Hash);
        Assert.AreEqual("OK", accepted.Status);

        ScoreViewerUploadResponse rejected = AppScoreViewerRegistrationGateway.ParseUploadResponse(
            "{\"status\":\"REJECTED\"}");
        Assert.IsFalse(rejected.Accepted);
        Assert.AreEqual("REJECTED", rejected.Status);
        Assert.IsNull(rejected.Hash);

        ScoreViewerUploadResponse nullStatus = AppScoreViewerRegistrationGateway.ParseUploadResponse(
            "{\"status\":null}");
        Assert.IsFalse(nullStatus.Accepted);
        Assert.AreEqual(string.Empty, nullStatus.Status);
    }

    /// <summary>必須status・成功時md5の欠落と連結JSONを拒否し、不完全な応答を正常扱いする誤りを検出します。</summary>
    [TestMethod]
    public void AppGatewayJsonParsers_RejectMissingPropertiesAndTrailingDocuments()
    {
        Assert.ThrowsException<FormatException>(() =>
            AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse("{}"));
        Assert.ThrowsException<FormatException>(() =>
            AppScoreViewerRegistrationGateway.ParseUploadResponse("{\"status\":\"OK\"}"));
        Assert.ThrowsException<JsonReaderException>(() =>
            AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse(
                "{\"status\":\"OK\"} {\"status\":\"OK\"}"));
    }

    /// <summary>ローカルファイル・hashのみ・非対応の選択を区別し、表示可能性とupload可能性の取り違えを検出します。</summary>
    [TestMethod]
    public void SelectionQueries_UseScoreViewerCapabilityAndLocalFileAvailability()
    {
        string localPath = Path.GetTempFileName();
        try
        {
            ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(
                new RecordingGateway(),
                new RecordingInteraction());
            ChartOperationTarget localTarget = CreateTarget(
                "local",
                localPath,
                ChartOperationCapabilities.UseScoreViewer);
            ChartOperationTarget hashOnlyTarget = CreateTarget(
                "hash-only",
                null,
                ChartOperationCapabilities.UseScoreViewer);
            ChartOperationTarget unsupportedTarget = CreateTarget(
                "unsupported",
                localPath,
                ChartOperationCapabilities.None);

            Assert.IsTrue(owner.HasScoreViewerTarget([localTarget, unsupportedTarget]));
            Assert.IsTrue(owner.HasScoreViewerTarget([hashOnlyTarget]));
            Assert.IsFalse(owner.HasScoreViewerTarget([unsupportedTarget]));
            Assert.IsTrue(owner.CanRegisterScoreViewer([localTarget]));
            Assert.IsFalse(owner.CanRegisterScoreViewer([hashOnlyTarget]));
            Assert.IsFalse(owner.CanRegisterScoreViewer([unsupportedTarget]));
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    /// <summary>非対応・hash欠落を除いた有効な一件だけを表示し、無効選択が単件判定やURL起動を妨げる誤りを検出します。</summary>
    [TestMethod]
    public async Task RunAsync_ChartTargetsProjectsEffectiveSingleTargetAndOpensViewer()
    {
        var gateway = new RecordingGateway();
        var interaction = new RecordingInteraction();
        ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(gateway, interaction);

        ScoreViewerRegistrationResult result = await owner.RunAsync(
            [
                CreateTarget("ignored", null, ChartOperationCapabilities.None),
                CreateTarget("effective", null, ChartOperationCapabilities.UseScoreViewer),
                CreateTarget(null, null, ChartOperationCapabilities.UseScoreViewer)
            ],
            "chart_targets_single");

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual("effective", result.Items[0].Hash);
        Assert.AreEqual(ScoreViewerRegistrationItemStatus.HashOnly, result.Items[0].Status);
        Assert.AreEqual(1, interaction.ConfirmationCount);
        Assert.AreEqual(result.LastViewUrl, interaction.OpenedUrl);
        Assert.AreEqual(0, gateway.StatusHashes.Count);
    }

    /// <summary>有効な複数hashの選択順を保ち、自動で単件viewerを開く誤りを検出します。</summary>
    [TestMethod]
    public async Task RunAsync_ChartTargetsKeepsMultipleEffectiveTargetsWithoutAutoOpen()
    {
        var interaction = new RecordingInteraction();
        ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(
            new RecordingGateway(),
            interaction);

        ScoreViewerRegistrationResult result = await owner.RunAsync(
            [
                CreateTarget("first", null, ChartOperationCapabilities.UseScoreViewer),
                CreateTarget("ignored", null, ChartOperationCapabilities.None),
                CreateTarget("second", null, ChartOperationCapabilities.UseScoreViewer)
            ],
            "chart_targets_multiple");

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(
            new[] { "first", "second" },
            result.Items.Select(item => item.Hash).ToArray());
        Assert.IsNull(interaction.OpenedUrl);
    }

    /// <summary>hashのみの対象は状態取得・uploadを行わず確認後にviewerへ渡し、不要な通信や表示漏れを検出します。</summary>
    [TestMethod]
    public async Task RunAsync_HashOnlyTargetSkipsNetworkAndOpensSingleViewer()
    {
        var gateway = new RecordingGateway();
        var interaction = new RecordingInteraction();
        ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(gateway, interaction);
        var target = new ScoreViewerTarget("hash-only", path: null, "Hash only");

        ScoreViewerRegistrationResult result = await owner.RunAsync(
            [target],
            openSingleViewerOnSuccess: true,
            "hash_only");

        Assert.IsNotNull(result);
        Assert.AreEqual(ScoreViewerRegistrationItemStatus.HashOnly, result.Items.Single().Status);
        Assert.AreEqual("?md5=hash-only", new Uri(result.LastViewUrl).Query);
        Assert.AreEqual(0, gateway.StatusHashes.Count);
        Assert.AreEqual(0, gateway.UploadPaths.Count);
        Assert.AreEqual(1, interaction.ConfirmationCount);
        Assert.AreSame(result, interaction.PresentedResult);
        Assert.AreEqual(result.LastViewUrl, interaction.OpenedUrl);
    }

    /// <summary>登録済み・状態取得失敗・upload拒否を対象別に保持し、拒否したuploadの実行や失敗の消失を検出します。</summary>
    [TestMethod]
    public async Task RunAsync_PreservesRegisteredStatusFailureAndDeclinedUpload()
    {
        string registeredPath = Path.GetTempFileName();
        string uploadPath = Path.GetTempFileName();
        string failedStatusPath = Path.GetTempFileName();
        try
        {
            var gateway = new RecordingGateway
            {
                RegistrationStatus = hash => hash switch
                {
                    "registered" => true,
                    "status-failure" => throw new IOException("status failed"),
                    _ => false,
                }
            };
            var interaction = new RecordingInteraction { ConfirmationResult = false };
            var warnings = new List<(Exception? Exception, string Message)>();
            ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(
                gateway,
                interaction,
                warnings.Add);

            ScoreViewerRegistrationResult result = await owner.RunAsync(
                [
                    new ScoreViewerTarget("registered", registeredPath, "Registered"),
                    new ScoreViewerTarget("needs-upload", uploadPath, "Needs upload"),
                    new ScoreViewerTarget("status-failure", failedStatusPath, "Status failure")
                ],
                openSingleViewerOnSuccess: false,
                "mixed_preflight");

            CollectionAssert.AreEqual(
                new[]
                {
                    ScoreViewerRegistrationItemStatus.AlreadyRegistered,
                    ScoreViewerRegistrationItemStatus.UploadDeclined,
                    ScoreViewerRegistrationItemStatus.StatusCheckFailed
                },
                result.Items.Select(item => item.Status).ToArray());
            Assert.AreEqual(0, gateway.UploadPaths.Count);
            Assert.AreEqual(1, interaction.ConfirmationCount);
            Assert.IsTrue(result.HasFailures);
            Assert.IsTrue(result.HasUploadDeclined);
            Assert.AreEqual(1, warnings.Count(item => item.Message.Contains("score_viewer_status_failed")));
        }
        finally
        {
            File.Delete(registeredPath);
            File.Delete(uploadPath);
            File.Delete(failedStatusPath);
        }
    }

    /// <summary>upload成功のserver hashと拒否・例外を個別に報告し、一件の失敗による後続欠落や成功扱いを検出します。</summary>
    [TestMethod]
    public async Task RunAsync_ReportsUploadSuccessRejectionAndExceptionPerTarget()
    {
        string successPath = Path.GetTempFileName();
        string rejectedPath = Path.GetTempFileName();
        string failedPath = Path.GetTempFileName();
        try
        {
            var gateway = new RecordingGateway
            {
                UploadResult = path => path == successPath
                    ? ScoreViewerUploadResponse.Success("server-hash")
                    : path == rejectedPath
                        ? ScoreViewerUploadResponse.Rejected("REJECTED")
                        : throw new IOException("upload failed")
            };
            var interaction = new RecordingInteraction();
            var warnings = new List<(Exception? Exception, string Message)>();
            ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(
                gateway,
                interaction,
                warnings.Add);

            ScoreViewerRegistrationResult result = await owner.RunAsync(
                [
                    new ScoreViewerTarget("success", successPath, "Success"),
                    new ScoreViewerTarget("rejected", rejectedPath, "Rejected"),
                    new ScoreViewerTarget("failed", failedPath, "Failed")
                ],
                openSingleViewerOnSuccess: false,
                "upload_results");

            CollectionAssert.AreEqual(
                new[]
                {
                    ScoreViewerRegistrationItemStatus.Uploaded,
                    ScoreViewerRegistrationItemStatus.UploadFailed,
                    ScoreViewerRegistrationItemStatus.UploadFailed
                },
                result.Items.Select(item => item.Status).ToArray());
            Assert.AreEqual("server-hash", result.Items[0].Hash);
            Assert.AreEqual("Score Viewer upload status: REJECTED", result.Items[1].FailureMessage);
            Assert.AreEqual("upload failed", result.Items[2].FailureMessage);
            Assert.AreEqual(3, gateway.UploadPaths.Count);
            Assert.IsTrue(result.HasUploadedRegistration);
            Assert.IsTrue(result.HasFailures);
            Assert.IsTrue(warnings.Any(item => item.Message.Contains("score_viewer_upload_rejected")));
            Assert.IsTrue(warnings.Any(item => item.Message.Contains("score_viewer_upload_failed")));
        }
        finally
        {
            File.Delete(successPath);
            File.Delete(rejectedPath);
            File.Delete(failedPath);
        }
    }

    /// <summary>確認処理の例外を失敗表示へ渡してuploadを抑止し、確認失敗を成功として処理する誤りを検出します。</summary>
    [TestMethod]
    public async Task RunAsync_ConfirmationFailureUsesFailurePresentationAndDoesNotUpload()
    {
        string path = Path.GetTempFileName();
        try
        {
            var gateway = new RecordingGateway();
            var interaction = new RecordingInteraction
            {
                ConfirmationException = new InvalidOperationException("confirmation failed")
            };
            var warnings = new List<(Exception? Exception, string Message)>();
            ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(
                gateway,
                interaction,
                warnings.Add,
                showSingleTargetConfirmation: false);

            ScoreViewerRegistrationResult result = await owner.RunAsync(
                [new ScoreViewerTarget("target", path, "Target")],
                openSingleViewerOnSuccess: true,
                "confirmation_failure");

            Assert.IsNull(result);
            Assert.IsFalse(interaction.LastShowSingleTargetConfirmation);
            Assert.AreEqual("confirmation failed", interaction.PresentedFailure?.Message);
            Assert.AreEqual(0, gateway.UploadPaths.Count);
            Assert.IsTrue(warnings.Any(item => item.Message.Contains("score_viewer_registration_failed")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Yes・Noの返答と閉じる操作を確認要求の契約に従って解釈し、拒否を承認扱いする誤りを検出します。</summary>
    [TestMethod]
    public async Task WpfInteraction_MapsConfirmationChoicesAndRecordsTypedRequest()
    {
        var dialogs = new RecordingUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
        };
        var interaction = new WpfScoreViewerRegistrationInteraction(
            (_, _) => { },
            new RecordingExternalShellGateway(),
            dialogs);
        ScoreViewerRegistrationPlan plan = CreateUploadPlan();

        Assert.IsTrue(await interaction.ConfirmUploadAsync(plan, showSingleTargetConfirmation: true));
        Assert.AreEqual(1, dialogs.ConfirmationRequests.Count);
        Assert.AreEqual(MessageBoxButton.YesNo, dialogs.ConfirmationRequests[0].Button);
        Assert.AreEqual(MessageBoxImage.Asterisk, dialogs.ConfirmationRequests[0].Icon);
        Assert.AreEqual(Resources.Confirm, dialogs.ConfirmationRequests[0].Caption);

        dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No);
        Assert.IsFalse(await interaction.ConfirmUploadAsync(plan, showSingleTargetConfirmation: true));

        dialogs.ConfirmationResult = UiDialogResult.ClosedByUser(MessageBoxResult.Yes);
        Assert.IsTrue(await interaction.ConfirmUploadAsync(plan, showSingleTargetConfirmation: true));
        dialogs.ConfirmationResult = UiDialogResult.ClosedByUser(MessageBoxResult.No);
        Assert.IsFalse(await interaction.ConfirmUploadAsync(plan, showSingleTargetConfirmation: true));
    }

    /// <summary>利用者が単件確認を無効にした場合は確認を表示せず承認し、設定を無視する誤りを検出します。</summary>
    [TestMethod]
    public async Task WpfInteraction_SkipsSingleTargetConfirmationWhenSettingDisablesIt()
    {
        var dialogs = new RecordingUiDialogService();
        var interaction = new WpfScoreViewerRegistrationInteraction(
            (_, _) => { },
            new RecordingExternalShellGateway(),
            dialogs);

        Assert.IsTrue(await interaction.ConfirmUploadAsync(CreateUploadPlan(), showSingleTargetConfirmation: false));
        Assert.AreEqual(0, dialogs.ConfirmationRequests.Count);
    }

    /// <summary>表示失敗・owner不在を例外として伝え、利用者の拒否へ読み替える誤りを検出します。</summary>
    [TestMethod]
    public async Task WpfInteraction_PreservesConfirmationDisplayFailures()
    {
        var dialogs = new RecordingUiDialogService
        {
            ConfirmationResult = UiDialogResult.Failed(new InvalidOperationException("display failed"))
        };
        var interaction = new WpfScoreViewerRegistrationInteraction(
            (_, _) => { },
            new RecordingExternalShellGateway(),
            dialogs);

        InvalidOperationException failed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => interaction.ConfirmUploadAsync(CreateUploadPlan(), showSingleTargetConfirmation: true));
        StringAssert.Contains(failed.Message, "dialog failed");

        dialogs.ConfirmationResult = UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable);
        InvalidOperationException unavailable = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => interaction.ConfirmUploadAsync(CreateUploadPlan(), showSingleTargetConfirmation: true));
        StringAssert.Contains(unavailable.Message, UiDialogStatus.OwnerUnavailable.ToString());
    }

    /// <summary>成功と対象失敗をそれぞれ通知し、viewerのURLを外部shellへ渡す接続の欠落を検出します。</summary>
    [TestMethod]
    public async Task WpfInteraction_PresentsSuccessAndFailureMessagesAndOpensViewer()
    {
        var dialogs = new RecordingUiDialogService();
        var shell = new RecordingExternalShellGateway();
        var interaction = new WpfScoreViewerRegistrationInteraction((_, _) => { }, shell, dialogs);
        var result = new ScoreViewerRegistrationResult([
            ScoreViewerRegistrationItem.Uploaded(
                new ScoreViewerTarget("hash", "chart.bms", "Chart"),
                "hash",
                "https://viewer/hash"),
            ScoreViewerRegistrationItem.UploadFailed(
                new ScoreViewerTarget("failed", "failed.bms", "Failed"),
                "failed",
                "upload failed")]);

        await interaction.PresentResultAsync(result);
        Assert.AreEqual(2, dialogs.MessageRequests.Count);
        Assert.AreEqual(Resources.Msg_success_register_chart, dialogs.MessageRequests[0].MessageBoxText);
        Assert.AreEqual(Resources.Error, dialogs.MessageRequests[1].Caption);

        interaction.OpenViewer("https://viewer/hash");
        Assert.AreEqual(ExternalShellRequestKind.Url, shell.LastRequest.Kind);
        Assert.AreEqual("https://viewer/hash", shell.LastRequest.Target);
    }

    /// <summary>結果通知の表示失敗を呼出元へ伝え、通常終了として隠す誤りを検出します。</summary>
    [TestMethod]
    public async Task WpfInteraction_DoesNotTreatResultDisplayFailureAsUserRejection()
    {
        var dialogs = new RecordingUiDialogService
        {
            MessageResult = UiDialogResult.Failed(new InvalidOperationException("message failed"))
        };
        var interaction = new WpfScoreViewerRegistrationInteraction((_, _) => { }, new RecordingExternalShellGateway(), dialogs);
        var result = new ScoreViewerRegistrationResult([
            ScoreViewerRegistrationItem.Uploaded(
                new ScoreViewerTarget("hash", "chart.bms", "Chart"),
                "hash",
                "https://viewer/hash")]);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => interaction.PresentResultAsync(result));
    }

    private static ScoreViewerRegistrationWorkflowOwner CreateOwner(
        RecordingGateway gateway,
        RecordingInteraction interaction,
        Action<(Exception? Exception, string Message)>? warning = null,
        bool showSingleTargetConfirmation = true)
    {
        return new ScoreViewerRegistrationWorkflowOwner(
            gateway,
            interaction,
            () => showSingleTargetConfirmation,
            (exception, message) => warning?.Invoke((exception, message)));
    }

    private static ChartOperationTarget CreateTarget(
        string? md5,
        string? path,
        ChartOperationCapabilities capabilities)
    {
        ChartFile chart = new(
            ChartFileKind.Bms,
            path!,
            md5!,
            sha256: null,
            title: md5 ?? string.Empty,
            rawTitle: md5 ?? string.Empty,
            artist: string.Empty,
            genre: string.Empty,
            folder: string.Empty,
            tag: string.Empty,
            levelText: string.Empty,
            level: null,
            mode: null,
            chartInfo: null);
        return new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            capabilities);
    }

    private static ScoreViewerRegistrationPlan CreateUploadPlan()
    {
        return new ScoreViewerRegistrationPlan([
            ScoreViewerRegistrationItem.NeedsUpload(
                new ScoreViewerTarget("hash", "chart.bms", "Chart"),
                "hash")]);
    }

    private sealed class RecordingGateway : IScoreViewerRegistrationGateway
    {
        internal Func<string, bool> RegistrationStatus { get; set; } = _ => false;

        internal Func<string, ScoreViewerUploadResponse> UploadResult { get; set; } = _ => ScoreViewerUploadResponse.Success(string.Empty);

        internal List<string> StatusHashes { get; } = [];

        internal List<string> UploadPaths { get; } = [];

        public bool IsRegistered(string hash)
        {
            StatusHashes.Add(hash);
            return RegistrationStatus(hash);
        }

        public ScoreViewerUploadResponse Upload(string path)
        {
            UploadPaths.Add(path);
            return UploadResult(path);
        }
    }

    private sealed class RecordingInteraction : IScoreViewerRegistrationInteraction
    {
        internal bool ConfirmationResult { get; set; } = true;

        internal Exception? ConfirmationException { get; set; }

        internal int ConfirmationCount { get; private set; }

        internal bool LastShowSingleTargetConfirmation { get; private set; }

        internal ScoreViewerRegistrationResult? PresentedResult { get; private set; }

        internal Exception? PresentedFailure { get; private set; }

        internal string? OpenedUrl { get; private set; }

        public Task<bool> ConfirmUploadAsync(
            ScoreViewerRegistrationPlan plan,
            bool showSingleTargetConfirmation)
        {
            ConfirmationCount++;
            LastShowSingleTargetConfirmation = showSingleTargetConfirmation;
            return ConfirmationException == null
                ? Task.FromResult(ConfirmationResult)
                : Task.FromException<bool>(ConfirmationException);
        }

        public Task PresentResultAsync(ScoreViewerRegistrationResult result)
        {
            PresentedResult = result;
            return Task.CompletedTask;
        }

        public Task PresentFailureAsync(Exception exception)
        {
            PresentedFailure = exception;
            return Task.CompletedTask;
        }

        public void OpenViewer(string url)
        {
            OpenedUrl = url;
        }
    }

    private sealed class RecordingUiDialogService : IUiDialogService
    {
        internal UiDialogResult ConfirmationResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes);

        internal UiDialogResult MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal List<UiConfirmationRequest> ConfirmationRequests { get; } = [];

        internal List<UiMessageRequest> MessageRequests { get; } = [];

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, System.Threading.CancellationToken cancellationToken = default)
        {
            MessageRequests.Add(request);
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, System.Threading.CancellationToken cancellationToken = default)
        {
            ConfirmationRequests.Add(request);
            return Task.FromResult(ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, System.Threading.CancellationToken cancellationToken = default)
            where TWindow : Window
            => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, System.Threading.CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, System.Threading.CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, System.Threading.CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, System.Threading.CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingExternalShellGateway : IExternalShellGateway
    {
        internal ExternalShellRequest LastRequest { get; private set; } = null!;

        public void Open(ExternalShellRequest request) => LastRequest = request;

        public ExplorerOpenResult OpenFileAndSelect(string filePath) => new();

        public ExplorerOpenResult OpenDirectory(string directoryPath) => new();

        public bool TryOpenDirectoryWithExplorerProcess(string directoryPath, out string failureReason)
        {
            failureReason = string.Empty;
            return true;
        }
    }
}
