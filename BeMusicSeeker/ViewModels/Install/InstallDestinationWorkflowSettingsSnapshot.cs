using System;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Captures the persisted choices that affect one install-destination workflow invocation.
/// </summary>
internal sealed class InstallDestinationWorkflowSettingsSnapshot
{
    internal InstallDestinationWorkflowSettingsSnapshot(
        bool showManualInstallConfirmation,
        bool deletePendingPackageSourceAfterInstall)
    {
        ShowManualInstallConfirmation = showManualInstallConfirmation;
        DeletePendingPackageSourceAfterInstall = deletePendingPackageSourceAfterInstall;
    }

    internal bool ShowManualInstallConfirmation { get; }

    internal bool DeletePendingPackageSourceAfterInstall { get; }

    internal static InstallDestinationWorkflowSettingsSnapshot CreateCurrent(
        BeMusicSeeker.Properties.Settings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }
        return new InstallDestinationWorkflowSettingsSnapshot(
            settings.ShowDiffBMSInstallConfirmMsg,
            settings.DeletePendingPackageSourceAfterInstall);
    }
}
