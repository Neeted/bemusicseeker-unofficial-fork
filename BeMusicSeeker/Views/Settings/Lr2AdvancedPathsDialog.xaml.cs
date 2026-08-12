using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views.Settings;

/// <summary>Edits custom LR2 child paths inside the parent settings draft session.</summary>
public partial class Lr2AdvancedPathsDialog : ThemedWindow
{
    /// <summary>Identifies the dialog-local song database path draft.</summary>
    public static readonly DependencyProperty SongDbPathProperty = DependencyProperty.Register(
        nameof(SongDbPath),
        typeof(string),
        typeof(Lr2AdvancedPathsDialog),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Identifies the dialog-local LR2 configuration path draft.</summary>
    public static readonly DependencyProperty ConfigPathProperty = DependencyProperty.Register(
        nameof(ConfigPath),
        typeof(string),
        typeof(Lr2AdvancedPathsDialog),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Identifies the validation error presented by this dialog.</summary>
    public static readonly DependencyProperty ValidationErrorProperty = DependencyProperty.Register(
        nameof(ValidationError),
        typeof(string),
        typeof(Lr2AdvancedPathsDialog),
        new PropertyMetadata(string.Empty));

    private readonly SettingsDialogViewModel viewModel;

    /// <summary>Gets or sets the song database path in the dialog-local draft.</summary>
    public string SongDbPath
    {
        get => (string)GetValue(SongDbPathProperty);
        set => SetValue(SongDbPathProperty, value);
    }

    /// <summary>Gets or sets the LR2 configuration path in the dialog-local draft.</summary>
    public string ConfigPath
    {
        get => (string)GetValue(ConfigPathProperty);
        set => SetValue(ConfigPathProperty, value);
    }

    /// <summary>Gets the validation error for the current dialog-local draft.</summary>
    public string ValidationError
    {
        get => (string)GetValue(ValidationErrorProperty);
        private set => SetValue(ValidationErrorProperty, value);
    }

    /// <summary>Initializes the dialog over the supplied shared settings draft.</summary>
    internal Lr2AdvancedPathsDialog(SettingsDialogViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        SetCurrentValue(SongDbPathProperty, viewModel.LR2SongDBPath);
        SetCurrentValue(ConfigPathProperty, viewModel.LR2ConfigXmlPath);
    }

    private async void BrowseSongDb(object sender, RoutedEventArgs e)
    {
        try
        {
            string candidate = await GetOwner().PickLr2AdvancedSongDbPathAsync(SongDbPath);
            if (candidate == null)
            {
                return;
            }
            if (!viewModel.IsLr2SongDbPathCandidateValid(candidate))
            {
                ValidationError = BeMusicSeeker.Properties.Resources.Error_InvalidLR2SongDbOrConfigPath;
                return;
            }
            SetCurrentValue(SongDbPathProperty, candidate);
            ValidationError = string.Empty;
        }
        catch (System.Exception ex)
        {
            await GetOwner().HandleSettingsRouteFailureAsync(ex, "LR2 advanced song database picker");
        }
    }

    private async void BrowseConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            string candidate = await GetOwner().PickLr2AdvancedConfigPathAsync(ConfigPath);
            if (candidate == null)
            {
                return;
            }
            if (!viewModel.IsLr2ConfigPathCandidateValid(candidate))
            {
                ValidationError = BeMusicSeeker.Properties.Resources.Error_InvalidLR2SongDbOrConfigPath;
                return;
            }
            SetCurrentValue(ConfigPathProperty, candidate);
            ValidationError = string.Empty;
        }
        catch (System.Exception ex)
        {
            await GetOwner().HandleSettingsRouteFailureAsync(ex, "LR2 advanced configuration picker");
        }
    }

    private void DoneClick(object sender, RoutedEventArgs e)
    {
        TextBox songDbEditor = GetPathEditor(SongDbPathPicker);
        TextBox configEditor = GetPathEditor(ConfigPathPicker);
        if (!viewModel.TryApplyLr2AdvancedPathDraft(
                songDbEditor.Text,
                configEditor.Text,
                out string rejectedPathPropertyName))
        {
            ValidationError = BeMusicSeeker.Properties.Resources.Error_InvalidLR2SongDbOrConfigPath;
            (rejectedPathPropertyName == nameof(SettingsDialogViewModel.LR2SongDBPath)
                ? songDbEditor
                : configEditor).Focus();
            return;
        }

        DialogResult = true;
    }

    private static TextBox GetPathEditor(SettingsPathPicker picker) => FindVisualDescendant<TextBox>(picker)
        ?? throw new System.InvalidOperationException("The LR2 path picker template requires a text editor.");

    private static T FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }
            T descendant = FindVisualDescendant<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }
        return null;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private SettingsWindow GetOwner() => Owner as SettingsWindow
        ?? throw new System.InvalidOperationException("LR2 advanced paths dialog requires SettingsWindow ownership.");
}
