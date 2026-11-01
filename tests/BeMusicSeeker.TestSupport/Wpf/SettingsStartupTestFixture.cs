using System.ComponentModel;
using System.Reflection;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>実rootの起動後保守が同じTask境界で終端するまで待ちます。</summary>
internal static class SettingsStartupTestFixture
{

    /// <summary>次の明示操作の準備として、起動後にLを取得し得る受理済み保守の実終端を待ちます。必須起動成功の条件は変更しません。</summary>
    internal static void AwaitStartupBackgroundCompletionOnDispatcher(MainWindowViewModel viewModel, string operationName)
    {
        StartupBackgroundTaskSchedulerOwner scheduler = typeof(MainWindowViewModel)
            .GetField("startupBackgroundTaskScheduler", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel)
            as StartupBackgroundTaskSchedulerOwner ?? throw new InvalidOperationException("Actual startup scheduler is unavailable.");
        var backgroundTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void BackgroundChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (!viewModel.ProgressHub.IsStartupBackgroundInitializationActive && scheduler.IsFullyIdle)
            {
                backgroundTerminal.TrySetResult();
            }
        }
        viewModel.ProgressHub.PropertyChanged += BackgroundChanged;
        try
        {
            if (viewModel.ProgressHub.IsStartupBackgroundInitializationActive || !scheduler.IsFullyIdle)
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(backgroundTerminal.Task, operationName);
            }
        }
        finally
        {
            viewModel.ProgressHub.PropertyChanged -= BackgroundChanged;
        }
    }
}
