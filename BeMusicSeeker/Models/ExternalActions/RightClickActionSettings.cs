using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace BeMusicSeeker.Models;

/// <summary>
/// 譜面の種類に応じて右クリック外部 action の適用範囲を表します。
/// </summary>
internal enum ExternalChartKind
{
    BmsOnly,
    BmsonOnly,
    All
}

/// <summary>
/// 右クリック web action の保存定義です。
/// </summary>
internal sealed class RightClickWebActionDefinition
{
    /// <summary>
    /// 定義を保存形式内で識別する安定 ID を取得します。
    /// </summary>
    internal string Id { get; }

    /// <summary>
    /// menu と設定画面に表示する名前を取得します。
    /// </summary>
    internal string Name { get; }

    /// <summary>
    /// hash placeholder を含む URL template を取得します。
    /// </summary>
    internal string UrlTemplate { get; }

    /// <summary>
    /// action が有効かどうかを取得します。
    /// </summary>
    internal bool Enabled { get; }

    /// <summary>
    /// action を適用できる譜面種別を取得します。
    /// </summary>
    internal ExternalChartKind ChartKind { get; }

    /// <summary>
    /// web action の保存定義を構築します。
    /// </summary>
    internal RightClickWebActionDefinition(
        string id,
        string name,
        string urlTemplate,
        bool enabled,
        ExternalChartKind chartKind)
    {
        Id = id;
        Name = name;
        UrlTemplate = urlTemplate;
        Enabled = enabled;
        ChartKind = chartKind;
    }
}

/// <summary>
/// 右クリック external program action の保存定義です。
/// </summary>
internal sealed class RightClickProgramActionDefinition
{
    /// <summary>
    /// 定義を保存形式内で識別する安定 ID を取得します。
    /// </summary>
    internal string Id { get; }

    /// <summary>
    /// program の表示名を取得します。
    /// </summary>
    internal string Name { get; }

    /// <summary>
    /// 起動対象 executable の絶対パスを取得します。
    /// </summary>
    internal string ExecutablePath { get; }

    /// <summary>
    /// <c>{filePath}</c> placeholder を含む command-line template を取得します。
    /// </summary>
    internal string ArgumentTemplate { get; }

    /// <summary>
    /// action が有効かどうかを取得します。
    /// </summary>
    internal bool Enabled { get; }

    /// <summary>
    /// external program action の保存定義を構築します。
    /// </summary>
    internal RightClickProgramActionDefinition(
        string id,
        string name,
        string executablePath,
        string argumentTemplate,
        bool enabled)
    {
        Id = id;
        Name = name;
        ExecutablePath = executablePath;
        ArgumentTemplate = argumentTemplate;
        Enabled = enabled;
    }
}

/// <summary>
/// 右クリック action settings の immutable aggregate です。
/// </summary>
internal sealed class RightClickActionSettings
{
    /// <summary>
    /// action 配列を保存順の immutable snapshot として構築します。
    /// </summary>
    internal RightClickActionSettings(
        IEnumerable<RightClickWebActionDefinition> webActions,
        IEnumerable<RightClickProgramActionDefinition> programActions)
    {
        WebActions = new ReadOnlyCollection<RightClickWebActionDefinition>([
            .. (webActions ?? throw new ArgumentNullException(nameof(webActions)))
        ]);
        ProgramActions = new ReadOnlyCollection<RightClickProgramActionDefinition>([
            .. (programActions ?? throw new ArgumentNullException(nameof(programActions)))
        ]);
    }

    /// <summary>
    /// 保存順を正本とする web action 定義を取得します。
    /// </summary>
    internal IReadOnlyList<RightClickWebActionDefinition> WebActions { get; }

    /// <summary>
    /// 保存順を正本とする program action 定義を取得します。
    /// </summary>
    internal IReadOnlyList<RightClickProgramActionDefinition> ProgramActions { get; }
}

/// <summary>
/// 右クリック操作の既定 JSON を一元管理します。
/// </summary>
internal static class RightClickActionSettingsDefaults
{
    /// <summary>
    /// Settings の既定値と app.config が共有する canonical JSON です。
    /// </summary>
    internal const string SerializedJson =
        "{\"webActions\":["
        + "{\"id\":\"bms-ir\",\"name\":\"BMS-IR\",\"urlTemplate\":\"https://bms-ir.org/new/song?songmd5={md5}&view=both\",\"enabled\":true,\"chartKind\":\"BmsOnly\"},"
        + "{\"id\":\"mocha\",\"name\":\"Mocha\",\"urlTemplate\":\"https://mocha-repository.info/song.php?sha256={sha256}\",\"enabled\":true,\"chartKind\":\"All\"},"
        + "{\"id\":\"minir\",\"name\":\"MinIR\",\"urlTemplate\":\"https://www.gaftalk.com/minir/#/viewer/song/{sha256}/0\",\"enabled\":true,\"chartKind\":\"All\"},"
        + "{\"id\":\"rianir\",\"name\":\"rianIR\",\"urlTemplate\":\"https://rianir.link/ranking?sha256={sha256}\",\"enabled\":true,\"chartKind\":\"All\"},"
        + "{\"id\":\"stellaverse-ir\",\"name\":\"STELLAVERSE IR\",\"urlTemplate\":\"https://ir.stellabms.xyz/charts/{md5}\",\"enabled\":true,\"chartKind\":\"All\"},"
        + "{\"id\":\"kaleid-ir\",\"name\":\"Kaleid IR\",\"urlTemplate\":\"https://kaleidir.com/charts/{sha256}\",\"enabled\":true,\"chartKind\":\"All\"}],"
        + "\"programActions\":[]}";

    /// <summary>
    /// canonical JSON から既定 action を生成します。
    /// </summary>
    internal static RightClickActionSettings Create()
    {
        RightClickActionSettingsParseResult parsed = RightClickActionSettingsSerializer.Parse(SerializedJson);
        if (!parsed.Succeeded)
        {
            throw new InvalidOperationException("既定の右クリック設定 JSON が不正です: " + parsed.Error);
        }
        return parsed.Settings;
    }
}
