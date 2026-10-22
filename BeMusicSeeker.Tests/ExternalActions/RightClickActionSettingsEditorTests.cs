using System;
using System.Globalization;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>右クリック設定の編集・入力検査・取消を画面と永続化から分けて確認します。</summary>
[TestClass]
public sealed class RightClickActionSettingsEditorTests
{
    /// <summary>選択行だけの差分・検証で確定と取消を判断し、別行の編集を混ぜず無効入力と新規行も取消できます。</summary>
    [DataTestMethod]
    [DataRow(true)] // Webの必須URL欄。
    [DataRow(false)] // Programの必須実行path欄。
    public void SelectedRecordAvailability_IgnoresOtherEditsAndAllowsInvalidAndNewCancellation(bool web)
    {
        const string json = """
        {"webActions":[{"id":"web1","name":"One","urlTemplate":"https://example.test/{md5}","enabled":true,"chartKind":"All"},{"id":"web2","name":"Two","urlTemplate":"https://example.test/{sha256}","enabled":true,"chartKind":"All"}],"programActions":[{"id":"program1","name":"One","executablePath":"C:\\Tools\\one.exe","argumentTemplate":"{filePath}","enabled":true},{"id":"program2","name":"Two","executablePath":"C:\\Tools\\two.exe","argumentTemplate":"{filePath}","enabled":true}]}
        """;
        var editor = new RightClickActionSettingsEditor(json);
        bool CanConfirm() => web ? editor.CanConfirmWebRecord : editor.CanConfirmProgramRecord;
        bool CanCancel() => web ? editor.CanCancelWebRecord : editor.CanCancelProgramRecord;
        if (web) { editor.SelectedWebAction = null; } else { editor.SelectedProgramAction = null; }
        Assert.IsFalse(CanConfirm()); Assert.IsFalse(CanCancel());
        if (web) { editor.SelectedWebAction = editor.WebActions[0]; editor.WebActions[1].Name = "Other draft"; }
        else { editor.SelectedProgramAction = editor.ProgramActions[0]; editor.ProgramActions[1].Name = "Other draft"; }
        Assert.IsFalse(CanConfirm()); Assert.IsFalse(CanCancel());
        if (web) { editor.WebActions[0].Name = "Edited"; } else { editor.ProgramActions[0].Name = "Edited"; }
        Assert.IsTrue(CanConfirm()); Assert.IsTrue(CanCancel());
        if (web) { editor.WebActions[0].UrlTemplate = "https://example.test/{unknown}"; } else { editor.ProgramActions[0].ExecutablePath = "relative.exe"; }
        Assert.IsFalse(CanConfirm()); Assert.IsTrue(CanCancel());
        editor.DiscardSelectedRecord(json, web);
        Assert.IsFalse(CanConfirm()); Assert.IsFalse(CanCancel());
        if (web) { editor.SelectedWebAction = editor.WebActions[1]; } else { editor.SelectedProgramAction = editor.ProgramActions[1]; }
        Assert.IsTrue(CanConfirm()); Assert.IsTrue(CanCancel());
        int count = web ? editor.WebActions.Count : editor.ProgramActions.Count;
        if (web) { editor.AddWebAction(); } else { editor.AddProgramAction(); }
        Assert.IsFalse(CanConfirm()); Assert.IsTrue(CanCancel());
        editor.DiscardSelectedRecord(json, web);
        Assert.AreEqual(count, web ? editor.WebActions.Count : editor.ProgramActions.Count);
    }

    /// <summary>不正な保存rawから追加した選択行も取消で除去し、別種の新規行と保存エラーを保ちます。全取消後も不正な保存snapshotへ戻り、既定値へ修復しません。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void InvalidPersistedSnapshot_AllowsNewRecordCancellationWithoutRepairingSavedSnapshot(bool web)
    {
        const string invalidJson = "{broken JSON";
        var editor = new RightClickActionSettingsEditor(invalidJson);
        string persistedError = editor.PersistedSettingsError;
        editor.AddWebAction();
        editor.AddProgramAction();
        RightClickWebActionEditorRow webRow = editor.SelectedWebAction;
        RightClickProgramActionEditorRow programRow = editor.SelectedProgramAction;
        Assert.IsTrue(web ? editor.CanCancelWebRecord : editor.CanCancelProgramRecord);
        Assert.IsFalse(web ? editor.CanConfirmWebRecord : editor.CanConfirmProgramRecord);

        editor.DiscardSelectedRecord(invalidJson, web);

        Assert.AreEqual(web ? 0 : 1, editor.WebActions.Count);
        Assert.AreEqual(web ? 1 : 0, editor.ProgramActions.Count);
        if (web) { Assert.IsNull(editor.SelectedWebAction); Assert.AreSame(programRow, editor.ProgramActions.Single()); }
        else { Assert.IsNull(editor.SelectedProgramAction); Assert.AreSame(webRow, editor.WebActions.Single()); }
        Assert.IsFalse(web ? editor.CanCancelWebRecord : editor.CanCancelProgramRecord);
        Assert.IsTrue(editor.IsInvalidPersistedSettings);
        Assert.AreEqual(persistedError, editor.PersistedSettingsError);
        Assert.IsFalse(editor.TryPrepareSave(out _, out _));
        editor.DiscardChanges();
        Assert.AreEqual(0, editor.WebActions.Count);
        Assert.AreEqual(0, editor.ProgramActions.Count);
        Assert.IsTrue(editor.IsInvalidPersistedSettings);
        Assert.AreEqual(persistedError, editor.PersistedSettingsError);
        Assert.IsFalse(editor.IsDirty);
    }

    /// <summary>未指定・空・空白の保存入力を不正として保持し、既定定義や未保存変更を自動生成しません。</summary>
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

    /// <summary>不正な保存名を診断付きで保存拒否し、明示resetだけで有効な保存draftへ復旧します。</summary>
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

    /// <summary>利用者の有効draftを明示restoreで既定定義へ置き換え、保存可能な未保存変更として扱います。</summary>
    [TestMethod]
    public void RestoreDefaultsReplacesValidDraftAndMarksPendingSave()
    {
        const string originalJson = """
        {"webActions":[{"id":"custom","name":"Custom","urlTemplate":"https://example.test/{md5}","enabled":true,"chartKind":"All"}],"programActions":[{"id":"viewer","name":"Viewer","executablePath":"C:\\Tools\\viewer.exe","argumentTemplate":"{filePath}","enabled":true}]}
        """;
        var editor = new RightClickActionSettingsEditor(originalJson);

        Assert.AreEqual(1, editor.WebActions.Count);
        Assert.AreEqual(1, editor.ProgramActions.Count);
        Assert.IsFalse(editor.IsDirty);

        editor.RestoreDefaults();

        Assert.AreEqual(0, editor.ProgramActions.Count);
        Assert.IsTrue(editor.IsDirty);
        Assert.IsFalse(editor.IsInvalidPersistedSettings);
        Assert.IsTrue(editor.TryPrepareSave(out string restoredJson, out string error), error);
        Assert.AreEqual(RightClickActionSettingsDefaults.SerializedJson, restoredJson);
    }

    /// <summary>追加行を移動・削除して変更状態を保持し、名前の編集を表示へ反映して空名をエラーにします。</summary>
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

    /// <summary>新規Web行の未入力名とURLを直ちにエラー表示して保存拒否し、有効入力後に各エラーを解除します。</summary>
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

    /// <summary>新規programの必須項目を検査し、pickerで空名だけを補完して利用者が付けた名前を保持します。</summary>
    [TestMethod]
    public void ProgramActionStartsWithFilePlaceholderAndPickerNameOnlyFillsBlank()
    {
        var editor = new RightClickActionSettingsEditor("{\"webActions\":[],\"programActions\":[]}");
        editor.AddProgramAction();
        RightClickProgramActionEditorRow action = editor.SelectedProgramAction;

        Assert.AreEqual("{filePath}", action.ArgumentTemplate);
        Assert.AreEqual(string.Empty, action.Name);
        Assert.AreEqual(Resources.RightClick_name_required, action.NameValidationMessage);
        Assert.AreEqual("Error", action.NameValidationStatus);
        Assert.AreEqual(Resources.RightClick_executable_required, action.ExecutablePathValidationMessage);
        Assert.AreEqual("Error", action.ExecutablePathValidationStatus);
        Assert.AreEqual(string.Empty, action.ArgumentTemplateValidationMessage);
        Assert.AreEqual(string.Empty, action.ArgumentTemplateValidationStatus);
        action.SetExecutablePathFromPicker(Path.Combine(Path.GetTempPath(), "Chart Viewer.exe"));
        Assert.AreEqual("Chart Viewer", action.Name);
        Assert.AreEqual(string.Empty, action.NameValidationMessage);
        Assert.AreEqual(string.Empty, action.ExecutablePathValidationMessage);

        action.Name = "User name";
        action.SetExecutablePathFromPicker(Path.Combine(Path.GetTempPath(), "Other.exe"));
        Assert.AreEqual("User name", action.Name);
        Assert.IsTrue(editor.TryPrepareSave(out _, out string error), error);
    }

    /// <summary>引数・名前・実行pathの各エラーを入力変更直後に更新し、無効化された不正行も保存拒否します。</summary>
    [TestMethod]
    public void ProgramActionValidationClearsEachFieldImmediatelyAndRejectsDisabledRows()
    {
        var editor = new RightClickActionSettingsEditor("{\"webActions\":[],\"programActions\":[]}");
        editor.AddProgramAction();
        RightClickProgramActionEditorRow action = editor.SelectedProgramAction;
        action.Name = "Viewer";
        action.ExecutablePath = @"C:\Tools\viewer.exe";

        action.ArgumentTemplate = string.Empty;
        Assert.AreEqual(Resources.RightClick_arguments_required, action.ArgumentTemplateValidationMessage);
        action.ArgumentTemplate = "--fixed";
        Assert.AreEqual(
            string.Format(CultureInfo.CurrentCulture, Resources.RightClick_arguments_file_path_required, "{filePath}"),
            action.ArgumentTemplateValidationMessage);
        action.ArgumentTemplate = "{FilePath}";
        Assert.AreEqual(Resources.RightClick_arguments_unknown_placeholder, action.ArgumentTemplateValidationMessage);
        action.ArgumentTemplate = "{filePath";
        Assert.AreEqual(Resources.RightClick_arguments_unbalanced_placeholder, action.ArgumentTemplateValidationMessage);
        action.ArgumentTemplate = "\"{filePath}";
        Assert.AreEqual(Resources.RightClick_arguments_unbalanced_quote, action.ArgumentTemplateValidationMessage);

        action.ArgumentTemplate = "{filePath}";
        Assert.AreEqual(string.Empty, action.ArgumentTemplateValidationMessage);
        action.Name = "   ";
        Assert.AreEqual(Resources.RightClick_name_required, action.NameValidationMessage);
        action.Name = "Viewer";
        action.ExecutablePath = "tools\\viewer.exe";
        Assert.AreEqual(Resources.RightClick_executable_absolute_required, action.ExecutablePathValidationMessage);
        action.ExecutablePath = @"C:\Tools\viewer.exe";
        Assert.AreEqual(string.Empty, action.ExecutablePathValidationMessage);

        action.Enabled = false;
        action.ArgumentTemplate = "--fixed";
        Assert.IsFalse(editor.TryPrepareSave(out _, out string error));
        Assert.AreEqual(
            string.Format(CultureInfo.CurrentCulture, Resources.RightClick_arguments_file_path_required, "{filePath}"),
            error);
    }

    /// <summary>複数不正行の保存では順に最初の不正行を選択し、修正完了後は元の順と無効状態を保存します。</summary>
    [TestMethod]
    public void SaveSelectsFirstInvalidProgramRowInArrayOrderAndKeepsValidInputOrder()
    {
        const string source = """
        {"webActions":[],"programActions":[
          {"id":"first","name":"First","executablePath":"C:\\Tools\\first.exe","argumentTemplate":"{filePath}","enabled":true},
          {"id":"second","name":"Second","executablePath":"C:\\Tools\\second.exe","argumentTemplate":"{filePath}","enabled":true},
          {"id":"third","name":"Third","executablePath":"C:\\Tools\\third.exe","argumentTemplate":"{filePath}","enabled":false}]}
        """;
        var editor = new RightClickActionSettingsEditor(source);
        editor.ProgramActions[1].Name = "   ";
        editor.ProgramActions[2].ArgumentTemplate = "{other}";
        editor.SelectedProgramAction = editor.ProgramActions[0];

        Assert.IsFalse(editor.TryPrepareSave(out _, out string error));
        Assert.AreSame(editor.ProgramActions[1], editor.SelectedProgramAction);
        Assert.AreEqual(Resources.RightClick_name_required, error);

        editor.ProgramActions[1].Name = "Second";
        Assert.IsFalse(editor.TryPrepareSave(out _, out error));
        Assert.AreSame(editor.ProgramActions[2], editor.SelectedProgramAction);
        Assert.AreEqual(Resources.RightClick_arguments_unknown_placeholder, error);

        editor.ProgramActions[2].ArgumentTemplate = "{filePath}";
        Assert.IsTrue(editor.TryPrepareSave(out string json, out error), error);
        RightClickActionSettingsParseResult parsed = RightClickActionSettingsSerializer.Parse(json);
        Assert.IsTrue(parsed.Succeeded, parsed.Error?.ToString());
        CollectionAssert.AreEqual(
            new[] { "first", "second", "third" },
            parsed.Settings.ProgramActions.Select(action => action.Id).ToArray());
        Assert.IsFalse(parsed.Settings.ProgramActions[2].Enabled);
    }

    /// <summary>placeholderを欠くURLdraftを保存拒否し、内部英語診断ではなく表示用リソースの検査結果を返します。</summary>
    [TestMethod]
    public void InvalidDraftIsRejectedWithLocalizedValidationMessage()
    {
        string original = RightClickActionSettingsDefaults.SerializedJson;
        var editor = new RightClickActionSettingsEditor(original);
        editor.SelectedWebAction.UrlTemplate = "https://example.test/no-placeholder";

        Assert.IsFalse(editor.TryPrepareSave(out _, out string error));
        Assert.AreEqual(
            string.Format(
                Resources.RightClick_settings_validation_error_format,
                Resources.RightClick_settings_invalid),
            error);
        Assert.IsFalse(error.Contains("requires", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>空の有効設定へ追加後に取消すると両一覧とdirty状態を元へ戻し、不正設定と取り違えません。</summary>
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
