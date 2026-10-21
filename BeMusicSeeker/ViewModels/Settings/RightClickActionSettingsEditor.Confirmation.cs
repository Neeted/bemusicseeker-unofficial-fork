using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

public sealed partial class RightClickActionSettingsEditor
{
    /// <summary>一recordの編集または明示削除・並替だけを最新確定JSONへ適用し、他recordの候補を混ぜません。</summary>
    internal bool TryPrepareRecordChange(string rawConfirmed, bool web, string operation, out string json, out string error)
    {
        json = null;
        error = string.Empty;
        RightClickActionSettingsParseResult parsed = RightClickActionSettingsSerializer.Parse(rawConfirmed);
        if (!parsed.Succeeded && operation != "defaults") { error = Resources.RightClick_settings_invalid; return false; }
        var webDefinitions = (parsed.Settings ?? RightClickActionSettingsDefaults.Create()).WebActions.ToList();
        var programDefinitions = (parsed.Settings ?? RightClickActionSettingsDefaults.Create()).ProgramActions.ToList();
        if (operation == "defaults")
        {
            RightClickActionSettings defaults = RightClickActionSettingsDefaults.Create();
            webDefinitions = defaults.WebActions.ToList();
            programDefinitions = defaults.ProgramActions.ToList();
        }
        else if (web)
        {
            RightClickWebActionEditorRow row = selectedWebAction;
            if (row == null) { return false; }
            int index = webDefinitions.FindIndex(item => item.Id == row.Id);
            if (operation == "save")
            {
                var value = new RightClickWebActionDefinition(row.Id, row.Name, row.UrlTemplate, row.Enabled, row.ChartKind);
                if (index < 0) { webDefinitions.Add(value); } else { webDefinitions[index] = value; }
            }
            var saved = webDefinitions.ToDictionary(item => item.Id);
            webDefinitions = PrepareRecordOrder(webActions.Select(item => item.Id), row.Id, operation)
                .Where(saved.ContainsKey).Select(id => saved[id]).ToList();
        }
        else
        {
            RightClickProgramActionEditorRow row = selectedProgramAction;
            if (row == null) { return false; }
            int index = programDefinitions.FindIndex(item => item.Id == row.Id);
            if (operation == "save")
            {
                if (row.TryGetValidationError(out RightClickProgramActionValidationErrorKind kind))
                { error = RightClickProgramActionEditorRow.GetValidationMessage(kind); return false; }
                var value = new RightClickProgramActionDefinition(row.Id, row.Name, row.ExecutablePath, row.ArgumentTemplate, row.Enabled);
                if (index < 0) { programDefinitions.Add(value); } else { programDefinitions[index] = value; }
            }
            var saved = programDefinitions.ToDictionary(item => item.Id);
            programDefinitions = PrepareRecordOrder(programActions.Select(item => item.Id), row.Id, operation)
                .Where(saved.ContainsKey).Select(id => saved[id]).ToList();
        }
        if (RightClickActionSettingsSerializer.TrySerialize(new RightClickActionSettings(webDefinitions, programDefinitions), out json, out _)) { return true; }
        error = Resources.RightClick_settings_invalid;
        return false;
    }

    /// <summary>保存前の画面順に今回の操作だけを反映し、未確定行も含む表示位置を確定IDの投影に使います。</summary>
    private static List<string> PrepareRecordOrder(IEnumerable<string> displayedIds, string selectedId, string operation)
    {
        var order = displayedIds.ToList();
        int index = order.IndexOf(selectedId);
        if (index < 0 || operation == "save") { return order; }
        if (operation == "delete") { order.RemoveAt(index); return order; }
        int target = index + (operation == "up" ? -1 : 1);
        if (target >= 0 && target < order.Count) { (order[index], order[target]) = (order[target], order[index]); }
        return order;
    }

    /// <summary>選択recordだけを最新確定内容へ戻し、別recordの入力を保ちます。</summary>
    internal void DiscardSelectedRecord(string rawConfirmed, bool web)
    {
        RightClickActionSettingsParseResult parsed = RightClickActionSettingsSerializer.Parse(rawConfirmed);
        if (!parsed.Succeeded) { return; }
        if (web && selectedWebAction is { } webRow)
        {
            RightClickWebActionDefinition value = parsed.Settings.WebActions.FirstOrDefault(item => item.Id == webRow.Id);
            if (value == null) { webActions.Remove(webRow); return; }
            webRow.Name = value.Name; webRow.UrlTemplate = value.UrlTemplate; webRow.Enabled = value.Enabled; webRow.ChartKind = value.ChartKind;
        }
        else if (!web && selectedProgramAction is { } programRow)
        {
            RightClickProgramActionDefinition value = parsed.Settings.ProgramActions.FirstOrDefault(item => item.Id == programRow.Id);
            if (value == null) { programActions.Remove(programRow); return; }
            programRow.Name = value.Name; programRow.ExecutablePath = value.ExecutablePath;
            programRow.ArgumentTemplate = value.ArgumentTemplate; programRow.Enabled = value.Enabled;
        }
    }
}
