using System;
using System.Configuration;
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

    /// <summary>Persists only the confirmed mode and runtime identity/placement, then reloads after success.</summary>
    void SaveOperationModeForRestart(bool operationMode, string historyIdentity);
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

    /// <summary>Publishes the restart selection without saving unrelated dialog drafts.</summary>
    public void SaveOperationModeForRestart(bool operationMode, string historyIdentity)
    {
        var selectedValues = new SettingsPropertyValueCollection();
        void Add(string name, object value)
        {
            selectedValues.Add(new SettingsPropertyValue(values.Properties[name]) { PropertyValue = value });
        }
        Add(nameof(Settings.OperationModeLR2DB), operationMode);
        Add(nameof(Settings.PlayHistorySelectedDisplayTargetIdentity), historyIdentity);
        // Placement belongs to runtime, so reloading must not discard a completed player capture.
        Add(nameof(Settings.LR2bodyWindowPlacement), values.LR2bodyWindowPlacement);
        values.Properties[nameof(Settings.OperationModeLR2DB)].Provider.SetPropertyValues(values.Context, selectedValues);
        values.Reload();
    }

    public void Save()
    {
        values.Save();
    }
}

/// <summary>Reports the second file failure after user.config was already saved successfully.</summary>
internal sealed class PartialSettingsSaveException(string filePath, Exception cause)
    : Exception("Application settings were saved, but LR2 configuration could not be saved: " + filePath, cause)
{
    /// <summary>Gets the LR2 configuration path that remains unsaved.</summary>
    internal string FilePath { get; } = filePath;
}
