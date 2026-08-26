using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>Verifies the dialog-local right-click editor without WPF or settings persistence.</summary>
[TestClass]
public sealed class RightClickActionSettingsEditorTests
{
    [TestMethod]
    public void MissingAndExplicitEmptyValuesLoadWithoutCreatingPendingChanges()
    {
        var missing = new RightClickActionSettingsEditor(null);
        Assert.AreEqual(5, missing.WebActions.Count);
        Assert.AreEqual(0, missing.ProgramActions.Count);
        Assert.IsFalse(missing.IsDirty);
        Assert.IsFalse(missing.IsInvalidPersistedSettings);

        var explicitEmpty = new RightClickActionSettingsEditor(string.Empty);
        Assert.AreEqual(0, explicitEmpty.WebActions.Count);
        Assert.AreEqual(0, explicitEmpty.ProgramActions.Count);
        Assert.IsFalse(explicitEmpty.IsDirty);
        Assert.IsFalse(explicitEmpty.IsInvalidPersistedSettings);
    }

    [TestMethod]
    public void InvalidPersistedValueHasNoFallbackAndExplicitResetRecoversIt()
    {
        var editor = new RightClickActionSettingsEditor("{invalid");

        Assert.IsTrue(editor.IsInvalidPersistedSettings);
        Assert.AreEqual(0, editor.WebActions.Count);
        Assert.AreEqual(0, editor.ProgramActions.Count);
        Assert.IsFalse(editor.IsDirty);
        Assert.IsFalse(editor.TryPrepareSave(out _, out string invalidError));
        Assert.AreEqual(
            string.Format(
                Resources.RightClick_settings_invalid_format,
                Resources.RightClick_settings_invalid),
            invalidError);
        Assert.IsFalse(invalidError.Contains("Argument template", StringComparison.Ordinal));

        editor.ResetToDefaults();

        Assert.IsFalse(editor.IsInvalidPersistedSettings);
        Assert.IsTrue(editor.IsDirty);
        Assert.IsTrue(editor.TryPrepareSave(out string json, out string error), error);
        Assert.AreEqual(RightClickActionSettingsDefaults.SerializedJson, json);
    }

    [TestMethod]
    public void WebActionsSupportOrderDeletionEnabledStateAndBuiltInNameOverride()
    {
        var editor = new RightClickActionSettingsEditor(RightClickActionSettingsDefaults.SerializedJson);
        RightClickWebActionEditorRow first = editor.WebActions[0];
        editor.SelectedWebAction = first;
        editor.AddWebAction();
        RightClickWebActionEditorRow custom = editor.SelectedWebAction;
        custom.Name = "Custom";
        custom.UrlTemplate = "https://example.test/{md5}";
        editor.MoveSelectedWebActionUp();
        Assert.AreSame(custom, editor.WebActions[editor.WebActions.Count - 2]);

        custom.Enabled = false;
        editor.DeleteSelectedWebAction();
        Assert.IsFalse(editor.WebActions.Contains(custom));
        Assert.IsTrue(editor.IsDirty);

        first.Name = "Renamed BMS-IR";
        Assert.AreEqual("Renamed BMS-IR", first.DisplayName);
        first.Name = string.Empty;
        Assert.AreEqual(Resources.RightClick_builtin_bms_ir, first.DisplayName);
    }

    [TestMethod]
    public void BuiltInNameEditorShowsLocalizedDefaultWithoutPersistingAnOverride()
    {
        var editor = new RightClickActionSettingsEditor(RightClickActionSettingsDefaults.SerializedJson);
        RightClickWebActionEditorRow first = editor.WebActions[0];

        Assert.AreEqual(Resources.RightClick_builtin_bms_ir, first.Name);
        Assert.IsNull(first.NameOverride);
        Assert.IsTrue(editor.TryPrepareSave(out string unchangedJson, out string unchangedError), unchangedError);
        RightClickActionSettingsParseResult unchanged = RightClickActionSettingsSerializer.Parse(unchangedJson);
        Assert.IsTrue(unchanged.Succeeded, unchanged.Error?.ToString());
        Assert.IsNull(unchanged.Settings.WebActions[0].NameOverride);

        first.Name = "BMS-IR (custom)";
        Assert.AreEqual("BMS-IR (custom)", first.Name);
        Assert.AreEqual("BMS-IR (custom)", first.NameOverride);
        Assert.IsTrue(editor.TryPrepareSave(out string renamedJson, out string renamedError), renamedError);
        RightClickActionSettingsParseResult renamed = RightClickActionSettingsSerializer.Parse(renamedJson);
        Assert.IsTrue(renamed.Succeeded, renamed.Error?.ToString());
        Assert.AreEqual("BMS-IR (custom)", renamed.Settings.WebActions[0].NameOverride);

        first.Name = string.Empty;
        Assert.AreEqual(Resources.RightClick_builtin_bms_ir, first.Name);
        Assert.IsNull(first.NameOverride);
        Assert.IsTrue(editor.TryPrepareSave(out string restoredJson, out string restoredError), restoredError);
        RightClickActionSettingsParseResult restored = RightClickActionSettingsSerializer.Parse(restoredJson);
        Assert.IsTrue(restored.Succeeded, restored.Error?.ToString());
        Assert.IsNull(restored.Settings.WebActions[0].NameOverride);
    }

    // ResourceService.Current changes process-wide Resources.Culture; isolate this
    // culture-refresh behavior from tests that assert the current UI language.
    [DoNotParallelize]
    [TestMethod]
    public void BuiltInNameRefreshTracksCultureWithoutChangingRawOverride()
    {
        string previousCulture = Resources.Culture?.Name ?? "ja-JP";
        try
        {
            ResourceService.Current.ChangeCulture("en-US");
            var editor = new RightClickActionSettingsEditor(RightClickActionSettingsDefaults.SerializedJson);
            RightClickWebActionEditorRow first = editor.WebActions[0];
            var notifications = 0;
            first.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RightClickWebActionEditorRow.Name))
                {
                    notifications++;
                }
            };

            ResourceService.Current.ChangeCulture("ja-JP");
            editor.RefreshLocalizedDisplayNames();

            Assert.AreEqual(Resources.RightClick_builtin_bms_ir, first.Name);
            Assert.IsTrue(notifications > 0);
            Assert.IsNull(first.NameOverride);
        }
        finally
        {
            ResourceService.Current.ChangeCulture(previousCulture);
        }
    }

    [TestMethod]
    public void ProgramActionStartsWithFilePlaceholderAndPickerNameOnlyFillsBlank()
    {
        var editor = new RightClickActionSettingsEditor(string.Empty);
        editor.AddProgramAction();
        RightClickProgramActionEditorRow action = editor.SelectedProgramAction;

        Assert.AreEqual("{filePath}", action.ArgumentTemplate);
        Assert.AreEqual(string.Empty, action.Name);
        action.SetExecutablePathFromPicker(Path.Combine(Path.GetTempPath(), "Chart Viewer.exe"));
        Assert.AreEqual("Chart Viewer", action.Name);

        action.Name = "User name";
        action.SetExecutablePathFromPicker(Path.Combine(Path.GetTempPath(), "Other.exe"));
        Assert.AreEqual("User name", action.Name);
        Assert.IsTrue(editor.TryPrepareSave(out _, out string error), error);
    }

    [TestMethod]
    public void InvalidDraftIsRejectedAndSavedSettingsValueIsNotMutatedByPrepare()
    {
        string original = RightClickActionSettingsDefaults.SerializedJson;
        var settings = new BeMusicSeeker.Properties.Settings();
        settings.RightClickActionsJson = original;
        var editor = new RightClickActionSettingsEditor(original);
        editor.SelectedWebAction.UrlTemplate = "https://example.test/no-placeholder";

        Assert.IsFalse(editor.TryPrepareSave(out _, out string error));
        Assert.AreEqual(
            string.Format(
                Resources.RightClick_settings_validation_error_format,
                Resources.RightClick_settings_invalid),
            error);
        Assert.IsFalse(error.Contains("requires", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(original, settings.RightClickActionsJson);
    }

    [TestMethod]
    public void DiscardChangesRestoresRawSnapshotAndPreservesExplicitEmpty()
    {
        var editor = new RightClickActionSettingsEditor(string.Empty);
        editor.AddWebAction();
        Assert.IsTrue(editor.IsDirty);

        editor.DiscardChanges();

        Assert.AreEqual(0, editor.WebActions.Count);
        Assert.AreEqual(0, editor.ProgramActions.Count);
        Assert.IsFalse(editor.IsDirty);
        Assert.IsFalse(editor.IsInvalidPersistedSettings);
    }
}
