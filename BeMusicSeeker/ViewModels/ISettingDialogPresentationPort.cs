namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Provides the small shell surface required to present the settings dialog.
/// </summary>
internal interface ISettingDialogPresentationPort
{
    void OpenSettingsDialog();

    void OpenInitialSetupLanguageDialog();

    void CloseSettingsDialog();

    void RefreshAppearanceSelection();
}
