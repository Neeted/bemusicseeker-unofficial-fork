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
    /// built-in 表示名の override を取得します。built-in では null のまま resource 解決へ委譲できます。
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

    /// <summary>
    /// built-in name override を明示的に扱う別名を取得します。
    /// </summary>
    internal string NameOverride => Name;
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

    /// <summary>
    /// 明示的に全 action を無効化した空 aggregate です。
    /// </summary>
    internal static RightClickActionSettings Empty { get; } = new([], []);
}

/// <summary>
/// built-in right-click web action の安定 ID と既定 JSON を一元管理します。
/// </summary>
internal static class RightClickActionSettingsDefaults
{
    /// <summary>
    /// BMS-IR built-in の安定 ID です。
    /// </summary>
    internal const string BmsIrId = "bms-ir";

    /// <summary>
    /// Mocha built-in の安定 ID です。
    /// </summary>
    internal const string MochaId = "mocha";

    /// <summary>
    /// MinIR built-in の安定 ID です。
    /// </summary>
    internal const string MinIrId = "minir";

    /// <summary>
    /// rianIR built-in の安定 ID です。
    /// </summary>
    internal const string RianIrId = "rianir";

    /// <summary>
    /// STELLAVERSE built-in の安定 ID です。
    /// </summary>
    internal const string StellaverseIrId = "stellaverse-ir";

    /// <summary>
    /// BMS-IR built-in の URL template です。
    /// </summary>
    internal const string BmsIrUrl = "https://bms-ir.org/new/song?songmd5={md5}&view=both";

    /// <summary>
    /// Mocha built-in の URL template です。
    /// </summary>
    internal const string MochaUrl = "https://mocha-repository.info/song.php?sha256={sha256}";

    /// <summary>
    /// MinIR built-in の URL template です。
    /// </summary>
    internal const string MinIrUrl = "https://www.gaftalk.com/minir/#/viewer/song/{sha256}/0";

    /// <summary>
    /// rianIR built-in の URL template です。
    /// </summary>
    internal const string RianIrUrl = "https://rianir.link/ranking?sha256={sha256}";

    /// <summary>
    /// STELLAVERSE built-in の URL template です。
    /// </summary>
    internal const string StellaverseIrUrl = "https://ir.stellabms.xyz/charts/{md5}";

    /// <summary>
    /// Settings の missing-property default と app.config が共有する canonical JSON です。
    /// </summary>
    internal const string SerializedJson =
        "{\"webActions\":["
        + "{\"id\":\"bms-ir\",\"name\":null,\"urlTemplate\":\"https://bms-ir.org/new/song?songmd5={md5}&view=both\",\"enabled\":true,\"chartKind\":\"BmsOnly\"},"
        + "{\"id\":\"mocha\",\"name\":null,\"urlTemplate\":\"https://mocha-repository.info/song.php?sha256={sha256}\",\"enabled\":true,\"chartKind\":\"All\"},"
        + "{\"id\":\"minir\",\"name\":null,\"urlTemplate\":\"https://www.gaftalk.com/minir/#/viewer/song/{sha256}/0\",\"enabled\":true,\"chartKind\":\"All\"},"
        + "{\"id\":\"rianir\",\"name\":null,\"urlTemplate\":\"https://rianir.link/ranking?sha256={sha256}\",\"enabled\":true,\"chartKind\":\"All\"},"
        + "{\"id\":\"stellaverse-ir\",\"name\":null,\"urlTemplate\":\"https://ir.stellabms.xyz/charts/{md5}\",\"enabled\":true,\"chartKind\":\"All\"}],"
        + "\"programActions\":[]}";

    /// <summary>
    /// missing settings 用の標準五 action を保存順で生成します。
    /// </summary>
    internal static RightClickActionSettings Create()
    {
        return new RightClickActionSettings(
        [
            new RightClickWebActionDefinition(BmsIrId, null, BmsIrUrl, true, ExternalChartKind.BmsOnly),
            new RightClickWebActionDefinition(MochaId, null, MochaUrl, true, ExternalChartKind.All),
            new RightClickWebActionDefinition(MinIrId, null, MinIrUrl, true, ExternalChartKind.All),
            new RightClickWebActionDefinition(RianIrId, null, RianIrUrl, true, ExternalChartKind.All),
            new RightClickWebActionDefinition(StellaverseIrId, null, StellaverseIrUrl, true, ExternalChartKind.All)
        ],
        []);
    }

    /// <summary>
    /// ID が標準 action のいずれかであるかを判定します。
    /// </summary>
    internal static bool IsBuiltInId(string id)
    {
        return string.Equals(id, BmsIrId, StringComparison.Ordinal)
            || string.Equals(id, MochaId, StringComparison.Ordinal)
            || string.Equals(id, MinIrId, StringComparison.Ordinal)
            || string.Equals(id, RianIrId, StringComparison.Ordinal)
            || string.Equals(id, StellaverseIrId, StringComparison.Ordinal);
    }
}
