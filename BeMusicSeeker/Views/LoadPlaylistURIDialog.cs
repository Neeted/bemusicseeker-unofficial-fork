using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Views;

public partial class LoadPlaylistURIDialog : UserControl, IComponentConnector
{
    private readonly IUiDialogService dialogService;

    public LoadPlaylistURIDialog()
        : this(new UiDialogCoordinator())
    {
    }

    /// <summary>
    /// 指定した UI dialog service を使う playlist URI dialog を初期化します。
    /// </summary>
    /// <param name="dialogService">local playlist file picker を実行する dialog service。</param>
    /// <exception cref="ArgumentNullException"><paramref name="dialogService"/> が null の場合。</exception>
    internal LoadPlaylistURIDialog(IUiDialogService dialogService)
    {
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        InitializeComponent();
    }

    private MainWindow GetDialogHost()
    {
        return Window.GetWindow(this) as MainWindow
            ?? throw new InvalidOperationException("Load playlist URI dialog is not hosted by MainWindow.");
    }

    private void CancelAndClose(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is PlaylistWorkspaceViewModel)
        {
            GetDialogHost().HideOverlayDialog(this);
            textBoxURIInput.Text = string.Empty;
        }
    }

    private void SaveAndClose(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is PlaylistWorkspaceViewModel playlistWorkspace)
        {
            ExternalPlaylistUriSubmissionResult submission = playlistWorkspace
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

    private async void OpenLocalFile(object sender, RoutedEventArgs e)
    {
        await HandleOpenLocalFileAsync();
    }

    /// <summary>
    /// local playlist JSON file picker を表示し、受理された path を URI 入力へ追加します。
    /// </summary>
    /// <returns>picker route と入力更新が完了した task。</returns>
    /// <exception cref="InvalidOperationException">picker が cancel/close 以外の status で終了した場合。</exception>
    internal async Task HandleOpenLocalFileAsync()
    {
        UiFilePickerResult result = await dialogService.PickFileAsync(new UiFilePickerRequest(
            "ヘッダーファイルを開く",
            filter: "Jsonファイル(*.json)|*.json",
            defaultExtension: ".json",
            owner: Window.GetWindow(this)));
        if (result == null)
        {
            throw new InvalidOperationException("Playlist URI local file picker returned no result.");
        }
        if (result.Status is UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }
        if (result.Status != UiDialogStatus.Accepted)
        {
            throw new InvalidOperationException("Playlist URI local file picker failed: " + result.Status, result.Error);
        }

        textBoxURIInput.Text = AppendUriInputLine(textBoxURIInput.Text, result.FileName);
        textBoxURIInput.CaretIndex = textBoxURIInput.Text.Length;
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
