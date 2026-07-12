using System;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>
/// Provides the mutable application-settings object used by one settings-dialog edit session.
/// </summary>
internal interface ISettingsEditSession
{
    /// <summary>
    /// Gets the in-memory settings values edited by the dialog.
    /// The current UI contract intentionally shares this object with runtime consumers until
    /// those consumers receive their own workflow snapshots. Implementations that provide an
    /// isolated values object are not supported by the remaining runtime settings readers yet.
    /// </summary>
    Settings Values { get; }

    void Reload();

    void Save();
}

/// <summary>
/// Adapts the existing user-config settings object to the settings-dialog composition boundary.
/// It deliberately preserves the existing shared in-memory edit behavior; only the persistence
/// and construction boundary moves here.
/// </summary>
internal sealed class SettingsEditSession : ISettingsEditSession
{
    private readonly Settings values;

    internal static SettingsEditSession CreateDefault()
    {
        return new SettingsEditSession(Settings.Default);
    }

    internal SettingsEditSession(Settings values)
    {
        this.values = values ?? throw new ArgumentNullException(nameof(values));
    }

    public Settings Values => values;

    public void Reload()
    {
        values.Reload();
    }

    public void Save()
    {
        values.Save();
    }
}
