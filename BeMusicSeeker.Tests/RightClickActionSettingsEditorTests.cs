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
    public void MissingAndBlankValuesRemainInvalidWithoutCreatingPendingChanges()
    {
        foreach (string? value in new string?[] { null, string.Empty, "   " })
        {
            var editor = new RightClickActionSettingsEditor(value);
            Assert.AreEqual(0, editor.WebActions.Count);
            Assert.AreEqual(0, editor.ProgramActions.Count);
            Assert.IsFalse(editor.IsDirty);
            Assert.IsTrue(editor.IsInvalidPersistedSettings);
        }
    }

    [TestMethod]
    public void InvalidPersistedNullWebNameHasNoFallbackAndExplicitResetRecoversIt()
    {
        const string invalidJson = "{\"webActions\":[{\"id\":\"bms-ir\",\"name\":null,\"urlTemplate\":\"https://example.test/{md5}\",\"enabled\":true,\"chartKind\":\"BmsOnly\"}],\"programActions\":[]}";
        var editor = new RightClickActionSettingsEditor(invalidJson);

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
    public void RestoreDefaultsReplacesValidDraftWithoutMutatingBackingRawValue()
    {
        const string originalJson = """
        {"webActions":[{"id":"custom","name":"Custom","urlTemplate":"https://example.test/{md5}","enabled":true,"chartKind":"All"}],"programActions":[{"id":"viewer","name":"Viewer","executablePath":"C:\\Tools\\viewer.exe","argumentTemplate":"{filePath}","enabled":true}]}
        """;
        var settings = new BeMusicSeeker.Properties.Settings
        {
            RightClickActionsJson = originalJson
        };
        var editor = new RightClickActionSettingsEditor(originalJson);

        Assert.AreEqual(1, editor.WebActions.Count);
        Assert.AreEqual(1, editor.ProgramActions.Count);
        Assert.IsFalse(editor.IsDirty);

        editor.RestoreDefaults();

        Assert.AreEqual(6, editor.WebActions.Count);
        Assert.AreEqual(0, editor.ProgramActions.Count);
        Assert.IsTrue(editor.IsDirty);
        Assert.IsFalse(editor.IsInvalidPersistedSettings);
        Assert.IsTrue(editor.TryPrepareSave(out string restoredJson, out string error), error);
        Assert.AreEqual(RightClickActionSettingsDefaults.SerializedJson, restoredJson);
        Assert.AreEqual(originalJson, settings.RightClickActionsJson);
    }

    [TestMethod]
    public void WebActionsSupportOrderDeletionEnabledStateAndLiteralNames()
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
        Assert.AreEqual(string.Empty, first.DisplayName);
        Assert.AreEqual("Error", first.NameValidationStatus);
    }

    [TestMethod]
    public void AddWebActionImmediatelyMarksRequiredFieldsAsErrors()
    {
        var editor = new RightClickActionSettingsEditor(RightClickActionSettingsDefaults.SerializedJson);

        editor.AddWebAction();

        RightClickWebActionEditorRow action = editor.SelectedWebAction;
        Assert.AreEqual(Resources.RightClick_name_required, action.NameValidationMessage);
        Assert.AreEqual("Error", action.NameValidationStatus);
        Assert.AreEqual(Resources.RightClick_url_required, action.UrlTemplateValidationMessage);
        Assert.AreEqual("Error", action.UrlTemplateValidationStatus);
        Assert.IsFalse(editor.TryPrepareSave(out _, out _));

        action.Name = "Custom";
        action.UrlTemplate = "https://example.test/{sha256}";

        Assert.AreEqual(string.Empty, action.NameValidationMessage);
        Assert.AreEqual(string.Empty, action.NameValidationStatus);
        Assert.AreEqual(string.Empty, action.UrlTemplateValidationMessage);
        Assert.AreEqual(string.Empty, action.UrlTemplateValidationStatus);
        Assert.IsTrue(editor.TryPrepareSave(out _, out string error), error);
    }

    [TestMethod]
    public void ProgramActionStartsWithFilePlaceholderAndPickerNameOnlyFillsBlank()
    {
        var editor = new RightClickActionSettingsEditor("{\"webActions\":[],\"programActions\":[]}");
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
    public void DiscardChangesRestoresValidEmptyAggregateSnapshot()
    {
        var editor = new RightClickActionSettingsEditor("{\"webActions\":[],\"programActions\":[]}");
        editor.AddWebAction();
        Assert.IsTrue(editor.IsDirty);

        editor.DiscardChanges();

        Assert.AreEqual(0, editor.WebActions.Count);
        Assert.AreEqual(0, editor.ProgramActions.Count);
        Assert.IsFalse(editor.IsDirty);
        Assert.IsFalse(editor.IsInvalidPersistedSettings);
    }
}
