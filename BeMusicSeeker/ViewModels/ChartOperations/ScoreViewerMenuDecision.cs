using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

/// <summary>譜面ビューアの項目の扱いです。見出し・表示・可否とクリック時の処理を決めます。</summary>
internal enum ScoreViewerMenuMode
{
    /// <summary>項目を表示しません。表示して無効にする状態は作りません。</summary>
    Hidden,

    /// <summary>1行の対象を開きます。「譜面ビューアで開く」を有効に表示します。</summary>
    Open,

    /// <summary>所持BMSだけを登録し、ビューアは開きません。「譜面ビューアに登録する」を有効に表示します。</summary>
    Register,
}

/// <summary>
/// 譜面ビューアの項目の判定へ渡す、1行分の入力です。
/// 各入口は行を譜面の種類・MD5・パス・タイトルへ写すだけで、判定の規則は持ちません。
/// </summary>
/// <param name="Kind">譜面の種類。種類を確定できない行は null で、対象になりません。</param>
/// <param name="Md5">譜面のMD5。</param>
/// <param name="Path">譜面ファイルのパス。未所持の行はなくて構いません。</param>
/// <param name="Title">確認の表示に使うタイトル。</param>
internal readonly record struct ScoreViewerCandidate(ChartFileKind? Kind, string Md5, string Path, string Title)
{
    /// <summary>譜面操作の対象へ解決できない行など、譜面ビューアの対象外の1行を表します。</summary>
    internal static ScoreViewerCandidate NotTarget => default;

    /// <summary>解決済みの譜面から候補を作ります。</summary>
    internal static ScoreViewerCandidate FromChart(ChartFile chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return new ScoreViewerCandidate(chart.Kind, chart.Md5, chart.Path, chart.Title);
    }

    /// <summary>種類がBMSで有効なMD5（32桁の16進）を持つ、譜面ビューアの対象かを返します。</summary>
    internal bool IsTarget => Kind == ChartFileKind.Bms && RightClickActionSettingsSerializer.IsValidMd5(Md5);

    /// <summary>譜面ファイルが実在する所持BMSの対象かを返します。ファイルの実在を確認します。</summary>
    internal bool IsOwnedTarget => IsTarget
        && !string.IsNullOrWhiteSpace(Path)
        && LongPathFileSystem.FileExists(Path);

    internal ScoreViewerTarget ToTarget() => new(Md5, Path, Title);
}

/// <summary>
/// 譜面ビューアの項目の判定結果です。通常の譜面一覧、未所持のプレイリスト項目、プレイログの3つのメニューが、
/// 見出し・表示・可否とクリック時の処理を同じこの判定で決めます。
/// </summary>
internal sealed class ScoreViewerMenuDecision
{
    private static readonly ScoreViewerMenuDecision HiddenDecision = new(ScoreViewerMenuMode.Hidden, []);

    private ScoreViewerMenuDecision(ScoreViewerMenuMode mode, IReadOnlyList<ScoreViewerTarget> targets)
    {
        Mode = mode;
        Targets = targets;
    }

    /// <summary>項目の扱いです。</summary>
    internal ScoreViewerMenuMode Mode { get; }

    /// <summary>
    /// 処理する対象です。開く操作では1件、登録する操作では所持BMSだけで、非表示では空です。
    /// 同じMD5の重複は畳みません。
    /// </summary>
    internal IReadOnlyList<ScoreViewerTarget> Targets { get; }

    /// <summary>
    /// 選択行の候補から判定します。クリック時は、その時点の候補からこの関数で判定し直します。
    /// 1行は、BMSで有効なMD5を持てば所持の有無によらず開く操作で、それ以外は非表示です。
    /// 2行以上は、ファイルが実在する所持BMSを1件以上含むときだけ、その所持BMSを登録する操作で、含まなければ非表示です。
    /// </summary>
    /// <param name="candidates">選択した行ごとの候補。解決できない行も1行として含めます。</param>
    internal static ScoreViewerMenuDecision Resolve(IReadOnlyList<ScoreViewerCandidate> candidates)
        => Evaluate(candidates, collectAllOwned: true);

    /// <summary>
    /// メニューを開くときの項目の扱いだけを返します。<see cref="Resolve"/> と同じ規則ですが、
    /// 所持BMSを1件見つけた時点で打ち切り、UIスレッドで全件のファイルの実在を確認しません。
    /// </summary>
    /// <param name="candidates">選択した行ごとの候補。</param>
    internal static ScoreViewerMenuMode ResolveMode(IReadOnlyList<ScoreViewerCandidate> candidates)
        => Evaluate(candidates, collectAllOwned: false).Mode;

    private static ScoreViewerMenuDecision Evaluate(
        IReadOnlyList<ScoreViewerCandidate> candidates,
        bool collectAllOwned)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 1)
        {
            return candidates[0].IsTarget
                ? new ScoreViewerMenuDecision(ScoreViewerMenuMode.Open, [candidates[0].ToTarget()])
                : HiddenDecision;
        }
        List<ScoreViewerTarget> owned = [];
        foreach (ScoreViewerCandidate candidate in candidates)
        {
            if (!candidate.IsOwnedTarget)
            {
                continue;
            }
            owned.Add(candidate.ToTarget());
            if (!collectAllOwned)
            {
                break;
            }
        }
        return owned.Count == 0
            ? HiddenDecision
            : new ScoreViewerMenuDecision(ScoreViewerMenuMode.Register, owned);
    }
}
