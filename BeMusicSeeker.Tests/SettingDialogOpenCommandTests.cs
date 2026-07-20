using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using BeMusicSeeker;
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

    [TestMethod]
    public void ApplyCommand_ActiveProfileWithoutChanges_PublishesSingleCloseRequest()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        SetActiveLibraryProfile(viewModel, true);
        var requests = new List<MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind>();
        viewModel.settingDialog.PresentationRequested += (_, request) => requests.Add(request.Kind);

        viewModel.settingDialog.ApplyCommand.Execute();

        CollectionAssert.AreEqual(
            new[] { MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.CloseOverlay },
            requests);
        Assert.IsFalse(viewModel.settingDialog.IsEditCompletionInProgress);
        Assert.IsTrue(viewModel.settingDialog.IsEditCompletionEnabled);
    }

    [TestMethod]
    public void CancelCommand_ChangedDraft_ResetsDraftBeforeClosing()
    {
        bool previousShowRecommUpdatedMsg = BeMusicSeeker.Properties.Settings.Default.ShowRecommUpdatedMsg;
        PropertyInfo availableCulturesProperty = typeof(App).GetProperty("AvailableCultures", BindingFlags.Static | BindingFlags.Public)!;
        object previousAvailableCultures = availableCulturesProperty.GetValue(null);
        try
        {
            if (previousAvailableCultures == null)
            {
                availableCulturesProperty.SetValue(
                    null,
                    new ReadOnlyDictionary<string, string>(
                        new Dictionary<string, string> { ["ja-JP"] = "ja-JP" }));
            }

            MainWindowViewModel viewModel = CreateViewModel();
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            var requests = new List<MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind>();
            dialog.PresentationRequested += (_, request) => requests.Add(request.Kind);

            dialog.ShowRecommUpdatedMsg = !previousShowRecommUpdatedMsg;
            Assert.IsTrue(dialog.HasPendingSettingChanges());

            dialog.CancelCommand.Execute();

            CollectionAssert.AreEqual(
                new[]
                {
                    MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.RefreshAppearanceSelection,
                    MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.CloseOverlay
                },
                requests);
            Assert.IsFalse(dialog.HasPendingSettingChanges());
            Assert.AreEqual(previousShowRecommUpdatedMsg, dialog.ShowRecommUpdatedMsg);
            Assert.IsFalse(dialog.IsEditCompletionInProgress);
        }
        finally
        {
            BeMusicSeeker.Properties.Settings.Default.ShowRecommUpdatedMsg = previousShowRecommUpdatedMsg;
            if (previousAvailableCultures == null)
            {
                availableCulturesProperty.SetValue(null, null);
            }
        }
    }

    private static MainWindowViewModel CreateViewModel()
    {
        return new ApplicationComposition(
            firstStartupProvider: () => false,
            completeFirstStartup: () => { })
            .CreateMainWindowViewModel();
    }

    private static void SetActiveLibraryProfile(MainWindowViewModel viewModel, bool value)
    {
        typeof(MainWindowViewModel)
            .GetField("hasActiveLibraryProfile", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, value);
    }
}
