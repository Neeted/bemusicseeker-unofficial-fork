using System;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>専用画面のBinding・picker・確定を代表操作で確認します。拒否条件の網羅はVMの通常テストへ分担します。</summary>
[TestClass]
[DoNotParallelize]
public sealed class PlaylistJsonExportDialogTests
{
    /// <summary>両TextBoxの変更が説明と出力可否へ反映され、各pickerの選択先が対応する欄へ入り、修正後だけモーダルを確定できます。</summary>
    [TestMethod]
    public void SavePaths_BindValidationAndPickersBeforeAccepting()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            using var input = new PlaylistJsonExportDialogViewModel();
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            var dialog = new PlaylistJsonExportDialog(input, dialogs, "source-header.json", "source-data.json");
            Exception? failure = null;
            scope.PrepareForOwnedPresentation(dialog);
            dialog.ContentRendered += (_, _) =>
            {
                try
                {
                    var header = (TextBox)dialog.FindName("headerPathInput");
                    var data = (TextBox)dialog.FindName("dataPathInput");
                    var message = (TextBlock)dialog.FindName("validationMessage");
                    var export = (Button)dialog.FindName("exportButton");
                    Assert.IsFalse(export.IsEnabled);
                    Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_json_export_paths_required, message.Text);

                    SetInput(header, @"C:\tables\same.json");
                    SetInput(data, @"C:\tables\.\SAME.JSON");
                    dialog.UpdateLayout();
                    Assert.IsFalse(export.IsEnabled);
                    Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_json_export_paths_same, message.Text);

                    dialogs.SaveFilePickerResults.Enqueue(new(UiDialogStatus.Accepted, @"C:\tables\header.json"));
                    ((Button)dialog.FindName("browseHeaderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    dialogs.SaveFilePickerResults.Enqueue(new(UiDialogStatus.Accepted, @"C:\tables\data.json"));
                    ((Button)dialog.FindName("browseDataButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    dialog.UpdateLayout();
                    Assert.AreEqual(@"C:\tables\header.json", header.Text);
                    Assert.AreEqual(@"C:\tables\data.json", data.Text);
                    Assert.IsTrue(export.IsEnabled);
                    Assert.AreEqual("", message.Text);
                    Assert.AreEqual(2, dialogs.SaveFilePickerRequests.Count);
                    Assert.AreEqual(BeMusicSeeker.Properties.Resources.Save_header_file, dialogs.SaveFilePickerRequests[0].Title);
                    Assert.AreEqual(BeMusicSeeker.Properties.Resources.Save_data_file, dialogs.SaveFilePickerRequests[1].Title);
                    foreach (UiSaveFilePickerRequest request in dialogs.SaveFilePickerRequests)
                    {
                        Assert.AreSame(dialog, request.Owner);
                        Assert.AreEqual(".json", request.DefaultExtension);
                        Assert.AreEqual("*.json", request.Filter.Split('|')[1]);
                        Assert.IsTrue(request.AddExtension);
                    }
                    export.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception exception) { failure = exception; dialog.Close(); }
            };
            try
            {
                bool? accepted = dialog.ShowDialog();
                if (failure != null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
                Assert.AreEqual(true, accepted);
                Assert.AreEqual(new PlaylistJsonExportPaths(@"C:\tables\header.json", @"C:\tables\data.json"), input.CapturePaths());
            }
            finally { dialog.Close(); }
        });
    }

    private static void SetInput(TextBox input, string value)
    {
        input.SetCurrentValue(TextBox.TextProperty, value);
        input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
