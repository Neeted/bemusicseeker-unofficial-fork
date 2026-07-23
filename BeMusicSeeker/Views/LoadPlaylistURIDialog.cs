using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Views;

public partial class LoadPlaylistURIDialog : UserControl, IComponentConnector
{
    public LoadPlaylistURIDialog()
    {
        InitializeComponent();
    }

    private MainWindow GetDialogHost()
    {
        return Window.GetWindow(this) as MainWindow
            ?? throw new InvalidOperationException("Load playlist URI dialog is not hosted by MainWindow.");
    }

    private void CancelAndClose(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel)
        {
            GetDialogHost().HideOverlayDialog(this);
            textBoxURIInput.Text = string.Empty;
        }
    }

    private void SaveAndClose(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            ExternalPlaylistUriSubmissionResult submission = viewModel.PlaylistWorkspace
                .SubmitExternalPlaylistUriText(textBoxURIInput.Text);
            if (!submission.HasValidUris)
            {
                UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Playlist_uri_input_no_valid_uri, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                return;
            }
            textBoxURIInput.Text = string.Empty;
            GetDialogHost().HideOverlayDialog(this);
            if (submission.HasInvalidLines)
            {
                ShowInvalidUriLines(submission.InvalidLines);
            }
        }
    }

    private void OpenLocalFile(object sender, RoutedEventArgs e)
    {
        UiFilePickerResult result = new UiDialogCoordinator().PickFileAsync(new UiFilePickerRequest(
            "ヘッダーファイルを開く",
            filter: "Jsonファイル(*.json)|*.json",
            defaultExtension: ".json",
            owner: Window.GetWindow(this)))
            .GetAwaiter()
            .GetResult();
        if (result.Status is not (UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser))
        {
            throw new InvalidOperationException("Playlist URI local file picker failed: " + result.Status, result.Error);
        }
        if (result.Status == UiDialogStatus.Accepted)
        {
            textBoxURIInput.Text = AppendUriInputLine(textBoxURIInput.Text, result.FileName);
            textBoxURIInput.CaretIndex = textBoxURIInput.Text.Length;
        }
    }

    internal static string AppendUriInputLine(string currentText, string line)
    {
        string trimmedLine = (line ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedLine))
        {
            return currentText ?? string.Empty;
        }
        string text = currentText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return trimmedLine;
        }
        return text.TrimEnd('\r', '\n') + Environment.NewLine + trimmedLine;
    }

    private void ShowInvalidUriLines(IReadOnlyList<string> invalidLines)
    {
        const int maxSamples = 5;
        IReadOnlyList<string> samples = [.. (invalidLines ?? []).Where(line => !string.IsNullOrWhiteSpace(line)).Take(maxSamples)];
        string sampleText = string.Join(Environment.NewLine, samples.Select(line => "- " + line));
        if (invalidLines != null && invalidLines.Count > maxSamples)
        {
            sampleText = sampleText + Environment.NewLine + "- ...";
        }
        UiDialogRoute.ShowMessageBox(Window.GetWindow(this), string.Format(BeMusicSeeker.Properties.Resources.Playlist_uri_input_invalid_lines_format, sampleText), BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
    }
}
