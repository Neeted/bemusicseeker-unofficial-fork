using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>実rootの起動後保守が同じTask境界で終端するまで待ちます。</summary>
internal static class SettingsStartupTestFixture
{

    /// <summary>次の明示操作の準備として、起動後にLを取得し得る受理済み保守の実終端を待ちます。必須起動成功の条件は変更しません。</summary>
    internal static void AwaitStartupBackgroundCompletionOnDispatcher(MainWindowViewModel viewModel, string operationName)
    {
        TestUiDispatcherHost.AwaitTaskOnDispatcher(StartupCompletionTestFixture.WaitForStartupBackgroundTerminalAsync(viewModel), operationName);
    }
}
