using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

public partial class SettingsWindow
{
    private void SettingsDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsDialogViewModel previous) { previous.ScoreSelectionChanged -= ScoreSelectionChanged; }
        if (e.NewValue is SettingsDialogViewModel current) { current.ScoreSelectionChanged += ScoreSelectionChanged; }
    }

    private async void ScoreSelectionChanged(object sender, EventArgs e)
    {
        if (!presentationActivated || sender is not SettingsDialogViewModel settings || !settings.HasScoreSelectionChanges
            || !settings.IsScoreSelectionValid || viewOperationInProgress || !settings.IsBeatorajaSelectionEnabled) { return; }
        try { await RunViewOperationAsync(settings.CommitScoreSettingsAsync); }
        catch (Exception failure) { await HandleSettingsRouteFailureAsync(failure, "score selection confirmation"); }
    }

    private async void settingsSectionButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { Tag: string tag } || !tag.Contains(':', StringComparison.Ordinal)) { return; }
        SettingsDialogViewModel settings = GetSettingDialogViewModel();
        bool independentSection = tag[(tag.IndexOf(':') + 1)..] is "audio" or "image" or "player" or "web" or "program";
        if (independentSection ? !settings.IsIndependentSectionEnabled : viewOperationInProgress || !settings.IsEditCompletionEnabled) { return; }
        e.Handled = true;
        try
        {
            // 同期の局所確定はUIスレッドで終わり、受理中のL/P処理のTask・Close抑止を置き換えません。
            if (independentSection) { await RunSectionActionAsync(settings, tag); }
            else { await RunViewOperationAsync(() => RunSectionActionAsync(settings, tag)); }
        }
        catch (Exception exception) { await HandleSettingsRouteFailureAsync(exception, "settings section " + tag); }
    }

    private static Task RunSectionActionAsync(SettingsDialogViewModel settings, string tag)
    {
        switch (tag)
        {
            case "confirm:web": settings.CommitRightClickRecord(true); break;
            case "confirm:program": settings.CommitRightClickRecord(false); break;
            case "cancel:web": settings.CancelRightClickRecord(true); break;
            case "cancel:program": settings.CancelRightClickRecord(false); break;
            case "confirm:bms": return settings.CommitBmsRootsAsync();
            case "retry:score": return settings.CommitScoreSettingsAsync();
            case "confirm:bmt": return settings.CommitBmtSettingsAsync();
            case "confirm:environment": return settings.CommitEnvironmentAsync();
            case "retry:output": return settings.CommitOutputSettingsAsync("pending");
            case "retry:audio": settings.CommitAudioSettings(); break;
            case "retry:player": settings.CommitExternalPlayerSettings(); break;
            case "retry:image": settings.CommitImageSettings(); break;
            case "cancel:bms": settings.CancelBmsRoots(); break;
            case "cancel:bmt": settings.CancelBmtSettings(); break;
            case "cancel:environment": settings.CancelEnvironment(); break;
        }
        return Task.CompletedTask;
    }

    private void settingsInputLostFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox textBox)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }
        else if (GetInputSlider(e.OriginalSource) is Slider { Name: "audioVolumeSlider" } volumeSlider) { CompleteSliderInput(volumeSlider); }
    }

    private void settingsInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is TextBox { AcceptsReturn: false } textBox)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            e.Handled = true;
        }
    }

    private void settingsInputPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (GetInputSlider(e.OriginalSource) is Slider slider
            && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        { CompleteSliderInput(slider); }
    }

    private void settingsInputMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && GetInputSlider(e.OriginalSource) is Slider slider) { CompleteSliderInput(slider); }
    }

    private void settingsSliderDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (GetInputSlider(e.OriginalSource) is Slider slider) { CompleteSliderInput(slider); }
    }

    private static Slider GetInputSlider(object source)
    {
        if (source is DependencyObject element)
        {
            for (DependencyObject item = element; item is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D; item = System.Windows.Media.VisualTreeHelper.GetParent(item))
            {
                if (item is Slider slider) { return slider; }
            }
        }
        return null;
    }

    private void CompleteSliderInput(Slider slider)
    {
        slider.GetBindingExpression(Slider.ValueProperty)?.UpdateSource();
        if (slider.Name == "audioVolumeSlider") { PlaybackPanel?.CompleteVolumeAdjustment(); }
        slider.GetBindingExpression(Slider.ValueProperty)?.UpdateTarget();
    }
}
