using System;
using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

public partial class SettingsDialogViewModel
{
    private Task settingsOperationCompletion = Task.CompletedTask;
    private bool settingsOperationRequiresPresentation;

    /// <summary>終了受付が開いており、設定から明示する非同期実操作を新たに一件受け付けられるかを示します。</summary>
    public bool IsSettingsOperationAvailable => !operationAdmission.IsAdmissionClosed && settingsOperationCompletion.IsCompleted;

    /// <summary>表示Windowの有無によらず、受理済み処理・後片付け・必須公開と担当通知の実終端を返します。</summary>
    internal Task SettingsOperationCompletionTask => settingsOperationCompletion;

    /// <summary>picker・確認より前に単一操作を受理し、受理済みcoreと失敗回収の終端まで所有します。競合要求は予約せず拒否します。</summary>
    /// <remarks>設定入力と同じUIスレッドで呼びます。処理完了の通知だけから次の受付を先行解禁しません。</remarks>
    /// <param name="operation">同じ設定受付を取り直さず実行する処理。L/Pの実受付と最終検査はこの中で行います。</param>
    /// <param name="recoverFailure">画面入口が担当する失敗通知。指定時も元の失敗は呼出元へ返します。</param>
    /// <param name="requiresPresentation">初回開始・再起動・復元・撤去など、既存の必須終端まで通常閉鎖を保留する操作です。</param>
    internal Task RunSettingsOperationAsync(Func<Task> operation, Func<Exception, Task> recoverFailure = null, bool requiresPresentation = false)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!IsSettingsOperationAvailable) { return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settingsOperationCompletion = completion.Task;
        settingsOperationRequiresPresentation = requiresPresentation;
        RaiseSettingsOperationAvailabilityChanged();
        return CompleteAsync();

        async Task CompleteAsync()
        {
            try { await operation(); }
            catch (Exception failure)
            {
                if (recoverFailure != null) { await recoverFailure(failure); }
                throw;
            }
            finally
            {
                settingsOperationRequiresPresentation = false;
                completion.TrySetResult();
                RaiseSettingsOperationAvailabilityChanged();
            }
        }
    }

    private void RaiseSettingsOperationAvailabilityChanged()
    {
        RaisePropertyChanged(nameof(IsSettingsOperationAvailable));
        RaisePropertyChanged(nameof(IsEditCompletionEnabled));
        RaisePropertyChanged(nameof(CanUninstallApplicationData));
        RaisePropertyChanged(nameof(IsEditCancellationEnabled));
        RaisePropertyChanged(nameof(IsBmsSearchRootEditorEnabled));
        RaisePropertyChanged(nameof(IsAudioDeviceTestAvailable));
        RaisePropertyChanged(nameof(CanInstallOrRepairLr2PlayHistorySchema));
        RaisePropertyChanged(nameof(CanUninstallLr2PlayHistorySchema));
        RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
        RaiseEnvironmentAvailabilityChanged();
    }
}
