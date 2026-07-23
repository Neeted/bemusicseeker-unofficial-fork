using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;
using Codeplex.Data;
using Ribbit.Net;

namespace BeMusicSeeker.ViewModels;

internal enum ScoreViewerRegistrationItemStatus
{
    HashOnly,
    AlreadyRegistered,
    NeedsUpload,
    Uploaded,
    StatusCheckFailed,
    UploadFailed,
    UploadDeclined,
}

internal sealed class ScoreViewerRegistrationItem
{
    private ScoreViewerRegistrationItem(
        ScoreViewerTarget target,
        ScoreViewerRegistrationItemStatus status,
        string hash,
        string viewUrl,
        string failureMessage,
        Exception exception)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Status = status;
        Hash = hash ?? string.Empty;
        ViewUrl = viewUrl;
        FailureMessage = failureMessage;
        Exception = exception;
    }

    internal ScoreViewerTarget Target { get; }

    internal ScoreViewerRegistrationItemStatus Status { get; }

    internal string Hash { get; }

    internal string ViewUrl { get; }

    internal string FailureMessage { get; }

    internal Exception Exception { get; }

    internal bool IsUploadCandidate => Status == ScoreViewerRegistrationItemStatus.NeedsUpload;

    internal bool IsFailure => Status is ScoreViewerRegistrationItemStatus.StatusCheckFailed or ScoreViewerRegistrationItemStatus.UploadFailed;

    internal bool IsUploaded => Status == ScoreViewerRegistrationItemStatus.Uploaded;

    internal bool IsUploadDeclined => Status == ScoreViewerRegistrationItemStatus.UploadDeclined;

    internal static ScoreViewerRegistrationItem HashOnly(ScoreViewerTarget target, string hash, string viewUrl)
    {
        return new ScoreViewerRegistrationItem(target, ScoreViewerRegistrationItemStatus.HashOnly, hash, viewUrl, null, null);
    }

    internal static ScoreViewerRegistrationItem AlreadyRegistered(ScoreViewerTarget target, string hash, string viewUrl)
    {
        return new ScoreViewerRegistrationItem(target, ScoreViewerRegistrationItemStatus.AlreadyRegistered, hash, viewUrl, null, null);
    }

    internal static ScoreViewerRegistrationItem NeedsUpload(ScoreViewerTarget target, string hash)
    {
        return new ScoreViewerRegistrationItem(target, ScoreViewerRegistrationItemStatus.NeedsUpload, hash, null, null, null);
    }

    internal static ScoreViewerRegistrationItem Uploaded(ScoreViewerTarget target, string hash, string viewUrl)
    {
        return new ScoreViewerRegistrationItem(target, ScoreViewerRegistrationItemStatus.Uploaded, hash, viewUrl, null, null);
    }

    internal static ScoreViewerRegistrationItem StatusCheckFailed(ScoreViewerTarget target, string hash, Exception exception)
    {
        return new ScoreViewerRegistrationItem(target, ScoreViewerRegistrationItemStatus.StatusCheckFailed, hash, null, exception?.Message, exception);
    }

    internal static ScoreViewerRegistrationItem UploadFailed(ScoreViewerTarget target, string hash, string failureMessage, Exception exception = null)
    {
        return new ScoreViewerRegistrationItem(target, ScoreViewerRegistrationItemStatus.UploadFailed, hash, null, failureMessage, exception);
    }

    internal static ScoreViewerRegistrationItem UploadDeclined(ScoreViewerTarget target, string hash)
    {
        return new ScoreViewerRegistrationItem(target, ScoreViewerRegistrationItemStatus.UploadDeclined, hash, null, null, null);
    }
}

internal sealed class ScoreViewerRegistrationPlan
{
    internal ScoreViewerRegistrationPlan(IReadOnlyList<ScoreViewerRegistrationItem> items)
    {
        Items = items ?? [];
    }

    internal IReadOnlyList<ScoreViewerRegistrationItem> Items { get; }

    internal int TargetCount => Items.Count;

    internal IReadOnlyList<ScoreViewerRegistrationItem> UploadCandidates => [.. Items.Where(item => item.IsUploadCandidate)];

    internal int UploadCandidateCount => Items.Count(item => item.IsUploadCandidate);

    internal bool HasUploadCandidates => UploadCandidateCount > 0;
}

internal sealed class ScoreViewerRegistrationResult
{
    internal ScoreViewerRegistrationResult(IReadOnlyList<ScoreViewerRegistrationItem> items)
    {
        Items = items ?? [];
    }

    internal IReadOnlyList<ScoreViewerRegistrationItem> Items { get; }

    internal string LastViewUrl => Items.LastOrDefault(item => !string.IsNullOrWhiteSpace(item.ViewUrl))?.ViewUrl;

    internal int UploadedCount => Items.Count(item => item.IsUploaded);

    internal int FailureCount => Items.Count(item => item.IsFailure);

    internal int UploadDeclinedCount => Items.Count(item => item.IsUploadDeclined);

    internal bool HasUploadedRegistration => UploadedCount > 0;

    internal bool HasFailures => FailureCount > 0;

    internal bool HasUploadDeclined => UploadDeclinedCount > 0;
}

internal sealed class ScoreViewerUploadResponse
{
    private ScoreViewerUploadResponse(bool accepted, string hash, string status)
    {
        Accepted = accepted;
        Hash = hash;
        Status = status;
    }

    internal bool Accepted { get; }

    internal string Hash { get; }

    internal string Status { get; }

    internal static ScoreViewerUploadResponse Success(string hash)
    {
        return new ScoreViewerUploadResponse(accepted: true, hash, status: "OK");
    }

    internal static ScoreViewerUploadResponse Rejected(string status)
    {
        return new ScoreViewerUploadResponse(accepted: false, hash: null, status);
    }
}

internal interface IScoreViewerRegistrationGateway
{
    bool IsRegistered(string hash);

    ScoreViewerUploadResponse Upload(string path);
}

internal interface IScoreViewerRegistrationInteraction
{
    Task<bool> ConfirmUploadAsync(ScoreViewerRegistrationPlan plan, bool showSingleTargetConfirmation);

    Task PresentResultAsync(ScoreViewerRegistrationResult result);

    Task PresentFailureAsync(Exception exception);

    void OpenViewer(string url);
}

internal sealed class ScoreViewerRegistrationWorkflowOwner
{
    private const string ViewUrl = "https://bms-score-viewer.pages.dev/view?md5=";
    private readonly IScoreViewerRegistrationGateway gateway;
    private readonly IScoreViewerRegistrationInteraction interaction;
    private readonly Func<bool> showSingleTargetConfirmationProvider;
    private readonly Action<Exception, string> warningLog;

    internal ScoreViewerRegistrationWorkflowOwner(
        IScoreViewerRegistrationGateway gateway,
        IScoreViewerRegistrationInteraction interaction,
        Func<bool> showSingleTargetConfirmationProvider,
        Action<Exception, string> warningLog)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        this.showSingleTargetConfirmationProvider = showSingleTargetConfirmationProvider
            ?? throw new ArgumentNullException(nameof(showSingleTargetConfirmationProvider));
        this.warningLog = warningLog ?? throw new ArgumentNullException(nameof(warningLog));
    }

    internal bool HasScoreViewerTarget(IEnumerable<ChartOperationTarget> targets)
    {
        return (targets ?? []).Any(target => target?.HasCapability(ChartOperationCapabilities.UseScoreViewer) == true);
    }

    internal bool CanRegisterScoreViewer(IEnumerable<ChartOperationTarget> targets)
    {
        return (targets ?? []).Any(target =>
            target?.HasCapability(ChartOperationCapabilities.UseScoreViewer) == true
            && !string.IsNullOrWhiteSpace(target.Chart.Path)
            && LongPathFileSystem.FileExists(target.Chart.Path));
    }

    internal async Task<ScoreViewerRegistrationResult> RunAsync(
        IReadOnlyList<ChartOperationTarget> targets,
        string logName)
    {
        if (targets == null || targets.Count == 0)
        {
            return null;
        }

        List<ScoreViewerTarget> projectedTargets = ProjectTargets(targets);
        if (projectedTargets.Count == 0)
        {
            return null;
        }

        return await RunAsync(
            projectedTargets,
            openSingleViewerOnSuccess: projectedTargets.Count == 1,
            logName);
    }

    internal async Task<ScoreViewerRegistrationResult> RunAsync(
        IReadOnlyList<ScoreViewerTarget> targets,
        bool openSingleViewerOnSuccess,
        string logName)
    {
        if (targets == null || targets.Count == 0)
        {
            return null;
        }
        try
        {
            ScoreViewerRegistrationPlan plan = await Task.Run(() => Prepare(targets));
            if (plan.TargetCount == 0)
            {
                return null;
            }
            bool uploadConfirmed = await interaction.ConfirmUploadAsync(
                plan,
                showSingleTargetConfirmationProvider());
            ScoreViewerRegistrationResult result = await Task.Run(() => Complete(plan, uploadConfirmed));
            await interaction.PresentResultAsync(result);
            if (openSingleViewerOnSuccess && !string.IsNullOrWhiteSpace(result.LastViewUrl))
            {
                interaction.OpenViewer(result.LastViewUrl);
            }
            return result;
        }
        catch (Exception ex)
        {
            warningLog(ex, "score_viewer_registration_failed route=" + (logName ?? string.Empty));
            try
            {
                await interaction.PresentFailureAsync(ex);
            }
            catch (Exception notificationException)
            {
                warningLog(
                    notificationException,
                    "score_viewer_notification_failed route=Score Viewer registration failure notification");
            }
            return null;
        }
    }

    internal ScoreViewerRegistrationPlan Prepare(IReadOnlyList<ScoreViewerTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }
        var items = new List<ScoreViewerRegistrationItem>();
        foreach (ScoreViewerTarget target in targets.Where(target => target != null && !string.IsNullOrWhiteSpace(target.Hash)))
        {
            string hash = target.Hash;
            if (string.IsNullOrWhiteSpace(target.Path) || !LongPathFileSystem.FileExists(target.Path))
            {
                items.Add(ScoreViewerRegistrationItem.HashOnly(target, hash, ViewUrl + hash));
                continue;
            }
            try
            {
                if (gateway.IsRegistered(hash))
                {
                    items.Add(ScoreViewerRegistrationItem.AlreadyRegistered(target, hash, ViewUrl + hash));
                    continue;
                }
                items.Add(ScoreViewerRegistrationItem.NeedsUpload(target, hash));
            }
            catch (Exception ex)
            {
                warningLog(ex, "score_viewer_status_failed path=" + (target.Path ?? string.Empty) + " md5=" + hash);
                items.Add(ScoreViewerRegistrationItem.StatusCheckFailed(target, hash, ex));
            }
        }
        return new ScoreViewerRegistrationPlan(items);
    }

    private static List<ScoreViewerTarget> ProjectTargets(IEnumerable<ChartOperationTarget> targets)
    {
        return [.. (targets ?? [])
            .Where(target =>
                target?.HasCapability(ChartOperationCapabilities.UseScoreViewer) == true
                && !string.IsNullOrWhiteSpace(target.Chart.Md5))
            .Select(target => new ScoreViewerTarget(
                target.Chart.Md5,
                target.Chart.Path,
                target.Chart.Title))];
    }

    internal ScoreViewerRegistrationResult Complete(ScoreViewerRegistrationPlan plan, bool uploadConfirmed)
    {
        if (plan == null)
        {
            throw new ArgumentNullException(nameof(plan));
        }
        var items = new List<ScoreViewerRegistrationItem>();
        foreach (ScoreViewerRegistrationItem item in plan.Items)
        {
            if (!item.IsUploadCandidate)
            {
                items.Add(item);
                continue;
            }
            if (!uploadConfirmed)
            {
                items.Add(ScoreViewerRegistrationItem.UploadDeclined(item.Target, item.Hash));
                continue;
            }
            try
            {
                ScoreViewerUploadResponse response = gateway.Upload(item.Target.Path);
                if (response?.Accepted == true)
                {
                    string hash = string.IsNullOrWhiteSpace(response.Hash) ? item.Hash : response.Hash;
                    items.Add(ScoreViewerRegistrationItem.Uploaded(item.Target, hash, ViewUrl + hash));
                    continue;
                }
                string status = response?.Status;
                string failureMessage = string.IsNullOrWhiteSpace(status)
                    ? "Unexpected Score Viewer upload response."
                    : "Score Viewer upload status: " + status;
                warningLog(
                    null,
                    "score_viewer_upload_rejected path=" + (item.Target.Path ?? string.Empty)
                    + " md5=" + (item.Hash ?? string.Empty)
                    + " status=" + (status ?? string.Empty));
                items.Add(ScoreViewerRegistrationItem.UploadFailed(item.Target, item.Hash, failureMessage));
            }
            catch (Exception ex)
            {
                warningLog(ex, "score_viewer_upload_failed path=" + (item.Target.Path ?? string.Empty) + " md5=" + (item.Hash ?? string.Empty));
                items.Add(ScoreViewerRegistrationItem.UploadFailed(item.Target, item.Hash, ex.Message, ex));
            }
        }
        return new ScoreViewerRegistrationResult(items);
    }
}

internal sealed class AppScoreViewerRegistrationGateway : IScoreViewerRegistrationGateway
{
    private static readonly Uri RegisterUrl = new("https://bms-score-viewer-backend.sayakaisbaka.workers.dev/bms/score/register");
    private const string StatusUrl = "https://bms-score-viewer-backend.sayakaisbaka.workers.dev/bms/score/status?md5=";

    public bool IsRegistered(string hash)
    {
        string json = AppHttpClient.Shared.GetString(new Uri(StatusUrl + hash), Encoding.UTF8);
        dynamic response = DynamicJson.Parse(json);
        return response.status == "OK";
    }

    public ScoreViewerUploadResponse Upload(string path)
    {
        string json = AppHttpClient.Shared.PostFile(
            RegisterUrl,
            path,
            responseEncoding: Encoding.UTF8,
            headers: new Dictionary<string, string> { { "Accept", "application/json" } },
            logErrorResponseBody: true);
        dynamic response = DynamicJson.Parse(json);
        string status = Convert.ToString(response.status, CultureInfo.InvariantCulture);
        return status == "OK"
            ? ScoreViewerUploadResponse.Success(Convert.ToString(response.md5, CultureInfo.InvariantCulture))
            : ScoreViewerUploadResponse.Rejected(status);
    }
}

internal sealed class WpfScoreViewerRegistrationInteraction : IScoreViewerRegistrationInteraction
{
    private readonly Action<Exception, string> warningLog;

    internal WpfScoreViewerRegistrationInteraction(Action<Exception, string> warningLog)
    {
        this.warningLog = warningLog ?? throw new ArgumentNullException(nameof(warningLog));
    }

    public async Task<bool> ConfirmUploadAsync(
        ScoreViewerRegistrationPlan plan,
        bool showSingleTargetConfirmation)
    {
        if (plan == null || !plan.HasUploadCandidates)
        {
            return true;
        }
        if (plan.TargetCount == 1 && plan.UploadCandidateCount == 1 && !showSingleTargetConfirmation)
        {
            return true;
        }
        string message;
        if (plan.UploadCandidateCount > 1)
        {
            message = BeMusicSeeker.Properties.Resources.Msg_register_chart
                + Environment.NewLine
                + Environment.NewLine
                + plan.UploadCandidateCount
                + " "
                + BeMusicSeeker.Properties.Resources.Num_chart;
        }
        else
        {
            ScoreViewerRegistrationItem item = plan.UploadCandidates[0];
            message = BeMusicSeeker.Properties.Resources.Msg_show_chart
                + Environment.NewLine
                + Environment.NewLine
                + (item.Target.Title ?? string.Empty)
                + Environment.NewLine
                + "MD5: "
                + item.Hash
                + Environment.NewLine
                + Environment.NewLine
                + "("
                + BeMusicSeeker.Properties.Resources.Msg_hide_message
                + ")";
        }
        UiDialogResult result = await new UiDialogCoordinator().ConfirmAsync(new UiConfirmationRequest(
            message,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.YesNo,
            MessageBoxImage.Asterisk));
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.MessageBoxResult is MessageBoxResult.OK or MessageBoxResult.Yes,
            UiDialogStatus.Failed => throw new InvalidOperationException(
                "Score Viewer registration confirmation dialog failed: "
                + (result.Exception?.Message ?? result.Status.ToString()),
                result.Exception),
            _ => throw new InvalidOperationException("Score Viewer registration confirmation dialog was not shown: " + result.Status),
        };
    }

    public async Task PresentResultAsync(ScoreViewerRegistrationResult result)
    {
        if (result?.HasUploadedRegistration == true)
        {
            await ShowMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_success_register_chart,
                BeMusicSeeker.Properties.Resources.Information,
                MessageBoxImage.Asterisk,
                "Score Viewer registration success notification");
        }
        if (result?.HasFailures == true)
        {
            await ShowMessageAsync(
                "譜面ビューアへの登録または状態確認に失敗した譜面があります。詳細はログを確認してください。",
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Exclamation,
                "Score Viewer registration partial failure notification");
        }
    }

    public Task PresentFailureAsync(Exception exception)
    {
        return ShowMessageAsync(
            "譜面ビューアへの登録処理に失敗しました。" + Environment.NewLine + exception?.Message,
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxImage.Hand,
            "Score Viewer registration failure notification");
    }

    public void OpenViewer(string url)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                Process.Start(url);
            }
        }
        catch (Exception ex)
        {
            warningLog(ex, "score_viewer_open_failed url=" + (url ?? string.Empty));
        }
    }

    private static async Task ShowMessageAsync(
        string message,
        string caption,
        MessageBoxImage icon,
        string routeName)
    {
        UiDialogResult result = await new UiDialogCoordinator().ShowMessageAsync(new UiMessageRequest(
            message,
            caption,
            MessageBoxButton.OK,
            icon,
            MessageBoxResult.OK));
        if (result.Status is not (UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser))
        {
            throw new InvalidOperationException(
                routeName + " failed: " + (result.Exception?.Message ?? result.Status.ToString()),
                result.Exception);
        }
    }
}
