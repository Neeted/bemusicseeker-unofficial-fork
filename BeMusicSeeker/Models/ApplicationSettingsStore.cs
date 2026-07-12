using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>
/// 起動時の設定 lifecycle が必要とする小さな永続化境界です。
/// </summary>
internal interface IApplicationSettingsStore
{
    SerializableVersion AssemblyVersion { get; set; }

    string Language { get; set; }

    string AppearanceTheme { get; set; }

    void Upgrade();

    void Save();
}

/// <summary>
/// 既存の user.config settings を <see cref="IApplicationSettingsStore"/> へ接続します。
/// </summary>
internal sealed class SettingsApplicationSettingsStore : IApplicationSettingsStore
{
    public SerializableVersion AssemblyVersion
    {
        get => Settings.Default.AssemblyVersion;
        set => Settings.Default.AssemblyVersion = value;
    }

    public string Language
    {
        get => Settings.Default.Lang;
        set => Settings.Default.Lang = value;
    }

    public string AppearanceTheme
    {
        get => Settings.Default.AppearanceTheme;
        set => Settings.Default.AppearanceTheme = value;
    }

    public void Upgrade()
    {
        Settings.Default.Upgrade();
    }

    public void Save()
    {
        Settings.Default.Save();
    }
}
