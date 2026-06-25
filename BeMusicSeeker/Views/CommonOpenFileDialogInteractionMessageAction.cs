using System;
using System.Collections.Generic;
using System.Windows;
using BeMusicSeeker.Views.Dialogs;
using Livet.Behaviors.Messaging;
using Livet.Messaging;
using Livet.Messaging.IO;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace BeMusicSeeker.Views;

internal class CommonOpenFileDialogInteractionMessageAction : InteractionMessageAction<FrameworkElement>
{
    protected override void InvokeAction(InteractionMessage message)
    {
        if (message is FolderSelectionMessage folderSelectionMessage)
        {
            ShowFolderSelectionDialog(folderSelectionMessage);
        }
        else if (message is OpeningFileSelectionMessage fileSelectionMessage)
        {
            ShowFileSelectionDialog(fileSelectionMessage);
        }
    }

    private void ShowFolderSelectionDialog(FolderSelectionMessage message)
    {
        UiFolderPickerResult result = new UiDialogCoordinator()
            .PickFolderAsync(new UiFolderPickerRequest(
                message.Title,
                message.SelectedPath,
                multiselect: false,
                ensurePathExists: true,
                Window.GetWindow(AssociatedObject)))
            .GetAwaiter()
            .GetResult();
        ThrowIfPickerFailed(result.Status, result.Error, "Folder selection interaction picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            message.Response = result.FolderPath;
        }
    }

    private void ShowFileSelectionDialog(OpeningFileSelectionMessage message)
    {
        UiFilePickerResult result = new UiDialogCoordinator()
            .PickFileAsync(new UiFilePickerRequest(
                message.Title,
                message.FileName,
                message.InitialDirectory,
                message.Filter,
                defaultExtension: null,
                message.MultiSelect,
                ensureFileExists: true,
                ensurePathExists: true,
                Window.GetWindow(AssociatedObject)))
            .GetAwaiter()
            .GetResult();
        ThrowIfPickerFailed(result.Status, result.Error, "File selection interaction picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            message.Response = [.. result.FileNames];
        }
    }

    private static void ThrowIfPickerFailed(UiDialogStatus status, Exception exception, string routeName)
    {
        if (status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + status, exception);
    }

    internal static IReadOnlyList<Tuple<string, string>> ParseFilterPairsForTest(string filter)
    {
        return UiFilePickerUtilities.ParseFilterPairs(filter);
    }

    internal static string InferDefaultExtensionForTest(string fileName, string filter)
    {
        return UiFilePickerUtilities.InferDefaultExtension(fileName, filter);
    }

    internal static string ResolveInitialDirectoryForTest(string path)
    {
        return UiFilePickerUtilities.ResolveInitialDirectory(path);
    }

    internal static void SetInitialDirectory(CommonOpenFileDialog dialog, string path)
    {
        UiFilePickerUtilities.SetInitialDirectory(dialog, path);
    }
}
