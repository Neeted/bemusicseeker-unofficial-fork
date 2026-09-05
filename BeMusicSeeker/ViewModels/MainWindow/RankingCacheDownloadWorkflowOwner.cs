using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

internal interface IRankingCacheDownloadRuntime
{
    int CurrentLr2Id { get; }

    List<BMSLibrary.IRDataCacheInfo> GetIRDataNeedUpdates(IReadOnlyList<string> md5s);

    List<BMSLibrary.IRDataCacheInfo> DownloadIRData(IReadOnlyList<BMSLibrary.IRDataCacheInfo> cacheInfo);
}

internal sealed class BmsRankingCacheDownloadRuntime : IRankingCacheDownloadRuntime
{
    private readonly Func<BMSLibrary> libraryProvider;

    internal BmsRankingCacheDownloadRuntime(Func<BMSLibrary> libraryProvider)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
    }

    public int CurrentLr2Id => libraryProvider()?.LR2ID ?? 0;

    public List<BMSLibrary.IRDataCacheInfo> GetIRDataNeedUpdates(IReadOnlyList<string> md5s)
    {
        return RequireLibrary().GetIRDataNeedUpdates(md5s);
    }

    public List<BMSLibrary.IRDataCacheInfo> DownloadIRData(IReadOnlyList<BMSLibrary.IRDataCacheInfo> cacheInfo)
    {
        return RequireLibrary().DownloadIRData(cacheInfo);
    }

    private BMSLibrary RequireLibrary()
    {
        return libraryProvider()
            ?? throw new InvalidOperationException("Ranking cache download library is not available.");
    }
}

internal sealed class RankingCacheDownloadWorkflowOwner
{
    private readonly IRankingCacheDownloadRuntime runtime;

    private readonly IUiDialogService dialogs;

    private readonly Func<Action, Task> backgroundScheduler;

    private readonly Action<Task, string> taskLogger;

    internal RankingCacheDownloadWorkflowOwner(
        IRankingCacheDownloadRuntime runtime,
        IUiDialogService dialogs,
        Func<Action, Task> backgroundScheduler = null,
        Action<Task, string> taskLogger = null)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.backgroundScheduler = backgroundScheduler ?? (action => Task.Run(action));
        this.taskLogger = taskLogger ?? new Action<Task, string>((task, routeName) => task.ObserveFault(routeName));
    }

    internal bool HasRankingTarget(IEnumerable<ChartOperationTarget> targets)
    {
        return (targets ?? []).Any(target => target?.HasCapability(ChartOperationCapabilities.UpdateRanking) == true);
    }

    internal bool CanRequestRanking(IEnumerable<ChartOperationTarget> targets)
    {
        return runtime.CurrentLr2Id != 0 && HasRankingTarget(targets);
    }

    internal bool Request(IEnumerable<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }

        string[] hashes = [.. targets
            .Where(target => target?.HasCapability(ChartOperationCapabilities.UseLr2Ir) == true)
            .Select(target => target.Chart?.Md5)];
        return RequestHashes(hashes);
    }

    private bool RequestHashes(IEnumerable<string> hashes)
    {
        if (hashes == null)
        {
            throw new ArgumentNullException(nameof(hashes));
        }

        string[] snapshot = hashes.ToArray();
        if (snapshot.All(string.IsNullOrWhiteSpace))
        {
            return false;
        }
        Task task = backgroundScheduler(() => Execute(snapshot));
        taskLogger(task, "tableContextMenuItemUpdateRankingDataClick");
        return true;
    }

    private void Execute(IReadOnlyList<string> hashes)
    {
        try
        {
            List<string> normalizedHashes = [.. hashes
                .Where(hash => !string.IsNullOrWhiteSpace(hash))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            if (normalizedHashes.Count == 0)
            {
                return;
            }

            List<BMSLibrary.IRDataCacheInfo> cacheInfo = runtime.GetIRDataNeedUpdates(normalizedHashes);
            if (cacheInfo.Count == 0)
            {
                ShowMessage(
                    BeMusicSeeker.Properties.Resources.Msg_ranking_cache_notfound,
                    BeMusicSeeker.Properties.Resources.Confirm,
                    MessageBoxImage.Hand,
                    "Ranking cache not found notification");
                return;
            }

            if (!ShowConfirmation(
                BeMusicSeeker.Properties.Resources.Msg_download_ranking_cache
                    + Environment.NewLine
                    + Environment.NewLine
                    + BeMusicSeeker.Properties.Resources.Download
                    + ": "
                    + cacheInfo.Count
                    + Environment.NewLine
                    + BeMusicSeeker.Properties.Resources.Skip
                    + ": "
                    + (normalizedHashes.Count - cacheInfo.Count)
                    + Environment.NewLine
                    + BeMusicSeeker.Properties.Resources.Size
                    + ": "
                    + FileSizeHelper.GetReadableFileSize(cacheInfo.Sum(cache => (long)cache.size)),
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxImage.Asterisk,
                MessageBoxButton.OKCancel,
                "Ranking cache download confirmation",
                MessageBoxResult.OK))
            {
                return;
            }

            List<BMSLibrary.IRDataCacheInfo> failed = runtime.DownloadIRData(cacheInfo);
            ShowMessage(
                BeMusicSeeker.Properties.Resources.Msg_download_completed
                    + Environment.NewLine
                    + Environment.NewLine
                    + BeMusicSeeker.Properties.Resources.Success
                    + ": "
                    + (cacheInfo.Count - failed.Count)
                    + Environment.NewLine
                    + BeMusicSeeker.Properties.Resources.Failure
                    + ": "
                    + failed.Count,
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxImage.Asterisk,
                "Ranking cache download completion notification");
        }
        catch (InvalidOperationException ex) when (ex is not RankingCacheDialogDisplayException)
        {
            ShowMessage(
                BeMusicSeeker.Properties.Resources.Msg_warn_cache_download,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "Ranking cache download warning notification");
        }
        catch (Exception ex) when (ex is not RankingCacheDialogDisplayException)
        {
            ShowMessage(
                BeMusicSeeker.Properties.Resources.Msg_error_cache_download
                    + Environment.NewLine
                    + Environment.NewLine
                    + ex.Message,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Hand,
                "Ranking cache download failure notification");
        }
    }

    private bool ShowConfirmation(
        string messageBoxText,
        string caption,
        MessageBoxImage icon,
        MessageBoxButton button,
        string routeName,
        MessageBoxResult defaultResult)
    {
        UiDialogResult result = dialogs.ConfirmAsync(new UiConfirmationRequest(
            messageBoxText,
            caption,
            button,
            icon,
            defaultResult)).GetAwaiter().GetResult();
        return ToConfirmationDecision(result, routeName);
    }

    private void ShowMessage(
        string messageBoxText,
        string caption,
        MessageBoxImage icon,
        string routeName)
    {
        UiDialogResult result = dialogs.ShowMessageAsync(new UiMessageRequest(
            messageBoxText,
            caption,
            MessageBoxButton.OK,
            icon,
            MessageBoxResult.OK)).GetAwaiter().GetResult();
        ThrowIfMessageNotShown(result, routeName);
    }

    private static bool ToConfirmationDecision(UiDialogResult result, string routeName)
    {
        if (result == null)
        {
            throw new RankingCacheDialogDisplayException(routeName + " returned no result.");
        }

        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.IsPositive,
            _ => throw new RankingCacheDialogDisplayException(
                routeName + " could not be displayed (" + result.Status + ").",
                result.Exception),
        };
    }

    private static void ThrowIfMessageNotShown(UiDialogResult result, string routeName)
    {
        if (result?.Status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }

        throw new RankingCacheDialogDisplayException(
            result == null
                ? routeName + " returned no result."
                : routeName + " could not be displayed (" + result.Status + ").",
            result?.Exception);
    }

    private sealed class RankingCacheDialogDisplayException : InvalidOperationException
    {
        internal RankingCacheDialogDisplayException()
        {
        }

        internal RankingCacheDialogDisplayException(string message)
            : base(message)
        {
        }

        internal RankingCacheDialogDisplayException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        internal RankingCacheDialogDisplayException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
