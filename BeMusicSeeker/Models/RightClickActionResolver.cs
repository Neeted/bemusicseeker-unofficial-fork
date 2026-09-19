using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models;

/// <summary>
/// UI/runtime owner から受け取る immutable chart context です。
/// </summary>
internal sealed class RightClickActionResolutionInput
{
    /// <summary>
    /// optional digest、local chart path、判定できる場合だけ指定する chart kind の immutable context を構築します。
    /// </summary>
    internal RightClickActionResolutionInput(
        string md5,
        string sha256,
        string localFilePath,
        ExternalChartKind? chartKind)
    {
        Md5 = md5;
        Sha256 = sha256;
        LocalFilePath = localFilePath;
        ChartKind = chartKind;
    }

    /// <summary>
    /// MD5 digest を取得します。
    /// </summary>
    internal string Md5 { get; }

    /// <summary>
    /// SHA-256 digest を取得します。
    /// </summary>
    internal string Sha256 { get; }

    /// <summary>
    /// 所持 chart の local absolute path を取得します。
    /// </summary>
    internal string LocalFilePath { get; }

    /// <summary>
    /// 解決対象 chart の種別を取得します。null は BMS/bmson を確定できない入力を表します。
    /// </summary>
    internal ExternalChartKind? ChartKind { get; }
}

/// <summary>
/// hash placeholder を展開済み web action 候補です。
/// </summary>
internal sealed class ResolvedRightClickWebAction
{
    /// <summary>
    /// definition と hash 展開済み URL を結び付けます。
    /// </summary>
    internal ResolvedRightClickWebAction(RightClickWebActionDefinition definition, string url)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Url = url ?? throw new ArgumentNullException(nameof(url));
    }

    /// <summary>
    /// 元の保存定義を取得します。
    /// </summary>
    internal RightClickWebActionDefinition Definition { get; }

    /// <summary>
    /// 安定 ID を取得します。
    /// </summary>
    internal string Id => Definition.Id;

    /// <summary>
    /// 表示名を取得します。
    /// </summary>
    internal string Name => Definition.Name;

    /// <summary>
    /// 展開済み absolute URL を取得します。
    /// </summary>
    internal string Url { get; }
}

/// <summary>
/// filePath placeholder を token 化した external program action 候補です。
/// </summary>
internal sealed class ResolvedRightClickProgramAction
{
    /// <summary>
    /// definition と filePath 展開済み argument token 列を結び付けます。
    /// </summary>
    internal ResolvedRightClickProgramAction(
        RightClickProgramActionDefinition definition,
        IReadOnlyList<string> arguments)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
    }

    /// <summary>
    /// 元の保存定義を取得します。
    /// </summary>
    internal RightClickProgramActionDefinition Definition { get; }

    /// <summary>
    /// 安定 ID を取得します。
    /// </summary>
    internal string Id => Definition.Id;

    /// <summary>
    /// 表示名を取得します。
    /// </summary>
    internal string Name => Definition.Name;

    /// <summary>
    /// executable の absolute path を取得します。
    /// </summary>
    internal string ExecutablePath => Definition.ExecutablePath;

    /// <summary>
    /// ArgumentList に渡す exact token 列を取得します。
    /// </summary>
    internal IReadOnlyList<string> Arguments { get; }
}

/// <summary>
/// resolver の web/program 候補を保持します。各配列は settings の order を維持します。
/// </summary>
internal sealed class RightClickActionResolution
{
    /// <summary>
    /// resolver output を保存順の immutable snapshot として構築します。
    /// </summary>
    internal RightClickActionResolution(
        IEnumerable<ResolvedRightClickWebAction> webActions,
        IEnumerable<ResolvedRightClickProgramAction> programActions)
    {
        WebActions = new ReadOnlyCollection<ResolvedRightClickWebAction>([.. webActions ?? throw new ArgumentNullException(nameof(webActions))]);
        ProgramActions = new ReadOnlyCollection<ResolvedRightClickProgramAction>([.. programActions ?? throw new ArgumentNullException(nameof(programActions))]);
    }

    /// <summary>
    /// 有効かつ capability を満たす web action を保存順で取得します。
    /// </summary>
    internal IReadOnlyList<ResolvedRightClickWebAction> WebActions { get; }

    /// <summary>
    /// 有効かつ local path を持つ program action を保存順で取得します。
    /// </summary>
    internal IReadOnlyList<ResolvedRightClickProgramAction> ProgramActions { get; }
}

/// <summary>
/// persisted definitions と chart context の capability 差を web/program 候補へ解決します。
/// </summary>
internal static class RightClickActionResolver
{
    /// <summary>
    /// settings と単一 chart context から実行可能な action 候補を解決します。
    /// </summary>
    internal static RightClickActionResolution Resolve(
        RightClickActionSettings settings,
        RightClickActionResolutionInput input)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        string md5 = RightClickActionSettingsSerializer.IsValidMd5(input.Md5)
            ? input.Md5.ToLowerInvariant()
            : null;
        string sha256 = RightClickActionSettingsSerializer.IsValidSha256(input.Sha256)
            ? input.Sha256.ToLowerInvariant()
            : null;
        bool hasAbsoluteFilePath = IsAbsoluteFilePath(input.LocalFilePath);

        var webActions = new List<ResolvedRightClickWebAction>();
        foreach (RightClickWebActionDefinition action in settings.WebActions)
        {
            if (action == null || !action.Enabled || !IsEligible(action.ChartKind, input.ChartKind))
            {
                continue;
            }
            if (!TryExpandUrl(action.UrlTemplate, md5, sha256, out string url))
            {
                continue;
            }
            webActions.Add(new ResolvedRightClickWebAction(action, url));
        }

        var programActions = new List<ResolvedRightClickProgramAction>();
        if (hasAbsoluteFilePath)
        {
            foreach (RightClickProgramActionDefinition action in settings.ProgramActions)
            {
                if (action == null || !action.Enabled
                    || !Path.IsPathFullyQualified(action.ExecutablePath)
                    || !ExternalProgramArgumentTemplate.TryParse(action.ArgumentTemplate, out ExternalProgramArgumentTemplate template, out _))
                {
                    continue;
                }
                programActions.Add(new ResolvedRightClickProgramAction(action, template.Expand(input.LocalFilePath)));
            }
        }

        return new RightClickActionResolution(webActions, programActions);
    }

    /// <summary>
    /// action の chart kind が対象 chart に適用可能かを判定します。
    /// </summary>
    internal static bool IsEligible(ExternalChartKind actionKind, ExternalChartKind? chartKind)
    {
        return actionKind == ExternalChartKind.All
            || actionKind == chartKind;
    }

    private static bool IsAbsoluteFilePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);
    }

    private static bool TryExpandUrl(
        string template,
        string md5,
        string sha256,
        out string url)
    {
        url = null;
        if (template == null)
        {
            return false;
        }
        bool needsMd5 = template.Contains("{md5}", StringComparison.Ordinal);
        bool needsSha256 = template.Contains("{sha256}", StringComparison.Ordinal);
        if ((needsMd5 && md5 == null) || (needsSha256 && sha256 == null))
        {
            return false;
        }
        url = template
            .Replace("{md5}", md5 ?? string.Empty, StringComparison.Ordinal)
            .Replace("{sha256}", sha256 ?? string.Empty, StringComparison.Ordinal);
        return true;
    }
}
