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
    internal sealed class PlaylistUriInputParseResult
    {
        internal PlaylistUriInputParseResult(IEnumerable<Uri> validUris, IEnumerable<string> invalidLines)
        {
            ValidUris = [.. (validUris ?? [])];
            InvalidLines = [.. (invalidLines ?? [])];
        }

        internal IReadOnlyList<Uri> ValidUris { get; }

        internal IReadOnlyList<string> InvalidLines { get; }

        internal bool HasValidUris => ValidUris.Count > 0;

        internal bool HasInvalidLines => InvalidLines.Count > 0;
    }

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
            PlaylistUriInputParseResult parseResult = ParsePlaylistUriInput(textBoxURIInput.Text);
            if (!parseResult.HasValidUris)
            {
                UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Playlist_uri_input_no_valid_uri, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                return;
            }
            textBoxURIInput.Text = string.Empty;
            GetDialogHost().HideOverlayDialog(this);
            viewModel.EnqueueExternalPlaylistBMSTableImports(parseResult.ValidUris);
            if (parseResult.HasInvalidLines)
            {
                ShowInvalidUriLines(parseResult.InvalidLines);
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

    internal static PlaylistUriInputParseResult ParsePlaylistUriInput(string input)
    {
        List<Uri> validUris = [];
        List<string> invalidLines = [];
        string[] lines = (input ?? string.Empty).Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        foreach (string line in lines)
        {
            string trimmedLine = (line ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine))
            {
                continue;
            }
            if (Uri.TryCreate(trimmedLine, UriKind.Absolute, out Uri uri))
            {
                validUris.Add(uri);
            }
            else
            {
                invalidLines.Add(trimmedLine);
            }
        }
        return new PlaylistUriInputParseResult(validUris, invalidLines);
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
