using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.Win32;

namespace BeMusicSeeker.Views;

public partial class LoadPlaylistURIDialog : UserControl, IComponentConnector
{
    internal sealed class PlaylistUriInputParseResult
    {
        internal PlaylistUriInputParseResult(IEnumerable<Uri> validUris, IEnumerable<string> invalidLines)
        {
            ValidUris = (validUris ?? Enumerable.Empty<Uri>()).ToList();
            InvalidLines = (invalidLines ?? Enumerable.Empty<string>()).ToList();
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

    private void CancelAndClose(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel)
        {
            settingDialog.Visibility = Visibility.Hidden;
            textBoxURIInput.Text = string.Empty;
        }
    }

    private void SaveAndClose(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            PlaylistUriInputParseResult parseResult = ParsePlaylistUriInput(textBoxURIInput.Text);
            if (!parseResult.HasValidUris)
            {
                DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Playlist_uri_input_no_valid_uri, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                return;
            }
            textBoxURIInput.Text = string.Empty;
            settingDialog.Visibility = Visibility.Hidden;
            viewModel.EnqueueExternalPlaylistBMSTableImports(parseResult.ValidUris);
            if (parseResult.HasInvalidLines)
            {
                ShowInvalidUriLines(parseResult.InvalidLines);
            }
        }
    }

    private void OpenLocalFile(object sender, RoutedEventArgs e)
    {
        OpenFileDialog openFileDialog = new OpenFileDialog();
        openFileDialog.Title = "ヘッダーファイルを開く";
        openFileDialog.DefaultExt = ".json";
        string filter = (openFileDialog.Filter = "Jsonファイル(*.json)|*.json");
        openFileDialog.Filter = filter;
        if (openFileDialog.ShowDialog() == true)
        {
            textBoxURIInput.Text = AppendUriInputLine(textBoxURIInput.Text, openFileDialog.FileName);
            textBoxURIInput.CaretIndex = textBoxURIInput.Text.Length;
        }
    }

    internal static PlaylistUriInputParseResult ParsePlaylistUriInput(string input)
    {
        List<Uri> validUris = new List<Uri>();
        List<string> invalidLines = new List<string>();
        string[] lines = (input ?? string.Empty).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
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
        IReadOnlyList<string> samples = (invalidLines ?? Array.Empty<string>()).Where((string line) => !string.IsNullOrWhiteSpace(line)).Take(maxSamples).ToList();
        string sampleText = string.Join(Environment.NewLine, samples.Select((string line) => "- " + line));
        if (invalidLines != null && invalidLines.Count > maxSamples)
        {
            sampleText = sampleText + Environment.NewLine + "- ...";
        }
        DispatcherMessageBox.Show(Window.GetWindow(this), string.Format(BeMusicSeeker.Properties.Resources.Playlist_uri_input_invalid_lines_format, sampleText), BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
    }
}
