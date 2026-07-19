using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SettingDialogOpenCommandTests
{
    [TestMethod]
    public void OpenCommand_PublishesExactlyOneOwnerRequest()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        int requestCount = 0;
        object requestSender = null!;
        viewModel.settingDialog.OpenRequested += (sender, _) =>
        {
            requestCount++;
            requestSender = sender;
        };

        Assert.IsTrue(viewModel.settingDialog.OpenCommand.CanExecute);
        viewModel.settingDialog.OpenCommand.Execute();

        Assert.AreEqual(1, requestCount);
        Assert.AreSame(viewModel.settingDialog, requestSender);
    }

    [TestMethod]
    public void OpenCommand_WithoutShellSubscriber_IsNoOp()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        viewModel.settingDialog.OpenCommand.Execute();
    }

    private static MainWindowViewModel CreateViewModel()
    {
        return new ApplicationComposition(
            firstStartupProvider: () => false,
            completeFirstStartup: () => { })
            .CreateMainWindowViewModel();
    }
}
