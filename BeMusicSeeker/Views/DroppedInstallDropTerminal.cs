using System;
using System.Windows;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>
/// Identifies the localized warning selected by the WPF FileDrop terminal boundary.
/// </summary>
internal enum DroppedInstallDropWarningKind
{
    None,
    PlaylistDownloadBlocked,
    UnsupportedFormat,
    IngressFailed,
    QueueUnavailable
}

/// <summary>
/// Describes the observable terminal behavior for one WPF FileDrop event.
/// </summary>
internal sealed class DroppedInstallDropDecision
{
    private DroppedInstallDropDecision(
        DragDropEffects effects,
        bool expandPendingTree,
        DroppedInstallDropWarningKind warningKind,
        bool logUnsupportedFormats,
        Exception exception)
    {
        Effects = effects;
        ExpandPendingTree = expandPendingTree;
        WarningKind = warningKind;
        LogUnsupportedFormats = logUnsupportedFormats;
        Exception = exception;
    }

    /// <summary>
    /// Gets the effect that the Drop event must publish.
    /// </summary>
    internal DragDropEffects Effects { get; }

    /// <summary>
    /// Gets whether the pending-install tree may be expanded.
    /// </summary>
    internal bool ExpandPendingTree { get; }

    /// <summary>
    /// Gets the localized warning category, or <see cref="DroppedInstallDropWarningKind.None"/>.
    /// </summary>
    internal DroppedInstallDropWarningKind WarningKind { get; }

    /// <summary>
    /// Gets whether actual unsupported-drop formats should be summarized to diagnostics.
    /// </summary>
    internal bool LogUnsupportedFormats { get; }

    /// <summary>
    /// Gets the provider or acquisition failure retained for diagnostics.
    /// </summary>
    internal Exception Exception { get; }

    /// <summary>
    /// Creates the only decision that publishes Copy and expands the pending tree.
    /// </summary>
    internal static DroppedInstallDropDecision Accepted()
    {
        return new DroppedInstallDropDecision(
            DragDropEffects.Copy,
            expandPendingTree: true,
            DroppedInstallDropWarningKind.None,
            logUnsupportedFormats: false,
            exception: null);
    }

    /// <summary>
    /// Creates a rejected decision that publishes None and never expands the pending tree.
    /// </summary>
    internal static DroppedInstallDropDecision Rejected(
        DroppedInstallDropWarningKind warningKind,
        Exception exception = null,
        bool logUnsupportedFormats = false)
    {
        return new DroppedInstallDropDecision(
            DragDropEffects.None,
            expandPendingTree: false,
            warningKind,
            logUnsupportedFormats,
            exception);
    }
}

/// <summary>
/// Evaluates WPF FileDrop data and the synchronous ingress result before UI effects are published.
/// </summary>
internal static class DroppedInstallDropTerminal
{
    /// <summary>
    /// Returns the complete terminal decision for a FileDrop event without directly showing UI.
    /// </summary>
    internal static DroppedInstallDropDecision Evaluate(
        IDataObject data,
        bool playlistDownloadBlocked,
        Func<string[], DroppedInstallIngressAcquisitionResult> acquireAndEnqueue)
    {
        if (playlistDownloadBlocked)
        {
            return DroppedInstallDropDecision.Rejected(
                DroppedInstallDropWarningKind.PlaylistDownloadBlocked);
        }

        string[] pathSnapshot;
        try
        {
            if (data == null
                || !data.GetDataPresent(DataFormats.FileDrop, autoConvert: true))
            {
                return DroppedInstallDropDecision.Rejected(
                    DroppedInstallDropWarningKind.UnsupportedFormat,
                    logUnsupportedFormats: true);
            }

            pathSnapshot = data.GetData(DataFormats.FileDrop, autoConvert: true) is string[] filePaths
                ? [.. filePaths]
                : [];
        }
        catch (Exception exception)
        {
            return DroppedInstallDropDecision.Rejected(
                DroppedInstallDropWarningKind.IngressFailed,
                exception);
        }

        if (acquireAndEnqueue == null)
        {
            return DroppedInstallDropDecision.Rejected(
                DroppedInstallDropWarningKind.QueueUnavailable);
        }

        try
        {
            DroppedInstallIngressAcquisitionResult result = acquireAndEnqueue(pathSnapshot);
            if (result.Succeeded)
            {
                return DroppedInstallDropDecision.Accepted();
            }
            return DroppedInstallDropDecision.Rejected(
                result.FailureKind == DroppedInstallIngressFailureKind.QueueRejected
                    ? DroppedInstallDropWarningKind.QueueUnavailable
                    : DroppedInstallDropWarningKind.IngressFailed,
                result.Exception);
        }
        catch (Exception exception)
        {
            return DroppedInstallDropDecision.Rejected(
                DroppedInstallDropWarningKind.IngressFailed,
                exception);
        }
    }
}
