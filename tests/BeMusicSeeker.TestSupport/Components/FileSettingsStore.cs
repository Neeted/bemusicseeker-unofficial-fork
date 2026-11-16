using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>専有Settingsの版・言語・テーマと実保存を、既存のライフサイクル境界へ接続します。</summary>
internal sealed class FileSettingsStore(Settings settings) : IApplicationSettingsStore
{
    public SerializableVersion? AssemblyVersion { get => settings.AssemblyVersion; set => settings.AssemblyVersion = value; }
    public string Language { get => settings.Lang; set => settings.Lang = value; }
    public string AppearanceTheme { get => settings.AppearanceTheme; set => settings.AppearanceTheme = value; }
    public void Save() => settings.Save();
    public void Upgrade() => settings.Upgrade();
}
