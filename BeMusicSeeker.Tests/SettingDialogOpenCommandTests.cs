using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Threading;
using BeMusicSeeker;
using BeMusicSeeker.Models;
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
        var presentation = new RecordingSettingsDialogPresentationPort();
        viewModel.SettingDialog.AttachPresentationPort(presentation);

        Assert.IsTrue(viewModel.SettingDialog.OpenCommand.CanExecute);
        viewModel.SettingDialog.OpenCommand.Execute();

        CollectionAssert.AreEqual(new[] { "open" }, presentation.Requests);
    }

    [TestMethod]
    public void OpenCommand_WithoutShellSubscriber_IsNoOp()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        viewModel.SettingDialog.OpenCommand.Execute();
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
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            var presentation = new RecordingSettingsDialogPresentationPort();
            dialog.AttachPresentationPort(presentation);

            dialog.ShowRecommUpdatedMsg = !previousShowRecommUpdatedMsg;
            Assert.IsTrue(dialog.HasPendingSettingChanges());

            dialog.CancelCommand.Execute();

            CollectionAssert.AreEqual(
                new[]
                {
                    "refresh",
                    "close"
                },
                presentation.Requests);
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
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog())
            .CreateMainWindowViewModel();
    }

}
