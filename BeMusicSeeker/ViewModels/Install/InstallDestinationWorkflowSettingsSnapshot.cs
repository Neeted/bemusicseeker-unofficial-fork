using System;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 一回の導入先ワークフローで使用する保存済みの選択を保持します。
/// </summary>
internal sealed class InstallDestinationWorkflowSettingsSnapshot
{
    /// <summary>差分導入・新規導入の確認と入力元の後片付け方針を固定します。</summary>
    internal InstallDestinationWorkflowSettingsSnapshot(
        bool showManualInstallConfirmation,
        bool deletePendingPackageSourceAfterInstall,
        bool showNewPackageInstallConfirmation = true)
    {
        ShowManualInstallConfirmation = showManualInstallConfirmation;
        ShowNewPackageInstallConfirmation = showNewPackageInstallConfirmation;
        DeletePendingPackageSourceAfterInstall = deletePendingPackageSourceAfterInstall;
    }

    internal bool ShowManualInstallConfirmation { get; }

    /// <summary>導入先未設定の保留パッケージの新規導入を確認するかを取得します。</summary>
    internal bool ShowNewPackageInstallConfirmation { get; }

    internal bool DeletePendingPackageSourceAfterInstall { get; }

    /// <summary>現在の利用者設定から変更不能な導入設定を作成します。</summary>
    internal static InstallDestinationWorkflowSettingsSnapshot CreateCurrent(
        BeMusicSeeker.Properties.Settings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }
        return new InstallDestinationWorkflowSettingsSnapshot(
            settings.ShowDiffBMSInstallConfirmMsg,
            settings.DeletePendingPackageSourceAfterInstall,
            settings.ShowNewPackageInstallConfirmMsg);
    }
}
