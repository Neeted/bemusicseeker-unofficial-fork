using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

/// <summary>設定画面の11分類です。値はページ一覧の並び順と一致し、不備の並び替えとページ選択の位置に使います。</summary>
public enum SettingsCategory
{
    /// <summary>一般。</summary>
    General = 0,
    /// <summary>外観。</summary>
    Appearance = 1,
    /// <summary>再生。</summary>
    Playback = 2,
    /// <summary>音声。</summary>
    Audio = 3,
    /// <summary>録音。</summary>
    Recording = 4,
    /// <summary>プレイリスト。</summary>
    Playlist = 5,
    /// <summary>インストール。</summary>
    Install = 6,
    /// <summary>バックアップ。</summary>
    Backup = 7,
    /// <summary>詳細設定。</summary>
    Advanced = 8,
    /// <summary>右クリックメニュー。</summary>
    RightClick = 9,
    /// <summary>このソフトについて。</summary>
    About = 10
}

/// <summary>利用開始前に修正が必要な設定の欄です。案内の行から移る先と、欄の直下に理由を出す対象を識別します。</summary>
public enum SettingsDeficiencyField
{
    /// <summary>一般ページの環境（LR2のパス）。</summary>
    Lr2Environment,
    /// <summary>一般ページのBMSディレクトリ。</summary>
    BmsDirectories,
    /// <summary>一般ページのbeatorajaのルート。</summary>
    BeatorajaRoot,
    /// <summary>一般ページのbeatorajaのスコアDB。</summary>
    BeatorajaScoreDb,
    /// <summary>一般ページのbeatorajaのBMT出力。</summary>
    BeatorajaBmtOutput,
    /// <summary>外観ページの外部画像。</summary>
    ExternalImage,
    /// <summary>再生ページのuBMplay。</summary>
    PlayeruBMplay,
    /// <summary>再生ページのBMIIDXView。</summary>
    PlayerBMIIDXView,
    /// <summary>再生ページのLR2本体。</summary>
    PlayerLR2body,
    /// <summary>プレイリストページの通常出力先。</summary>
    NormalOutput,
    /// <summary>プレイリストページのROOT出力先。</summary>
    RootOutput,
    /// <summary>プレイリストページの追加出力先。</summary>
    AdditionalOutput,
    /// <summary>インストールページのインストール先。</summary>
    InstallDirectory,
    /// <summary>バックアップページのLR2バックアップの出力先。</summary>
    Lr2BackupOutput
}

/// <summary>
/// 確定値から求めた設定の不備一件です。作成時の表示言語でページ名・理由・行の文言を固定し、変更不能な値として受け渡します。
/// 起動時検査、開始ボタンの可否、設定画面の案内・ページの印・欄の理由、起動時の失敗通知は同じ一覧のこの値を使います。
/// </summary>
public sealed class SettingsDeficiency
{
    /// <summary>不備の項目を作成し、現在の表示言語でページ名と行の文言を固定します。</summary>
    /// <param name="category">修正する欄があるページ。</param>
    /// <param name="field">修正する欄。</param>
    /// <param name="reason">現行の検査が使う表示リソースの理由。</param>
    internal SettingsDeficiency(SettingsCategory category, SettingsDeficiencyField field, string reason)
    {
        Category = category;
        Field = field;
        Reason = reason ?? string.Empty;
        CategoryName = GetCategoryName(category);
        DisplayText = string.Format(CultureInfo.CurrentCulture, Resources.SettingValidation_SectionMessageFormat, CategoryName, Reason);
    }

    /// <summary>修正する欄があるページです。</summary>
    public SettingsCategory Category { get; }

    /// <summary>修正する欄です。</summary>
    public SettingsDeficiencyField Field { get; }

    /// <summary>作成時の表示言語の理由です。</summary>
    public string Reason { get; }

    /// <summary>作成時の表示言語のページ名です。ページ一覧の表示名と同じ文言を使います。</summary>
    public string CategoryName { get; }

    /// <summary>案内欄と起動時の警告に並べる「ページ名: 理由」の行です。</summary>
    public string DisplayText { get; }

    /// <summary>ページ一覧と同じ表示名を現在の表示言語で返します。</summary>
    /// <param name="category">表示名を求めるページ。</param>
    internal static string GetCategoryName(SettingsCategory category) => category switch
    {
        SettingsCategory.General => Resources.General,
        SettingsCategory.Appearance => Resources.Appearance,
        SettingsCategory.Playback => Resources.Playback,
        SettingsCategory.Audio => Resources.Audio,
        SettingsCategory.Recording => Resources.Recording,
        SettingsCategory.Playlist => Resources.Playlist,
        SettingsCategory.Install => Resources.Install,
        SettingsCategory.Backup => Resources.Backup,
        SettingsCategory.Advanced => Resources.Advanced_settings,
        SettingsCategory.RightClick => Resources.RightClick_actions,
        SettingsCategory.About => Resources.About_this_app,
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    /// <summary>起動時検査が不備で止まったときの警告を、<c>Msg_init_settings_check</c> の後に一覧順の行を並べて作ります。</summary>
    /// <param name="deficiencies">起動時検査が作り、設定画面へ公開した一覧。</param>
    internal static string FormatStartupWarning(IEnumerable<SettingsDeficiency> deficiencies)
    {
        ArgumentNullException.ThrowIfNull(deficiencies);
        return string.Join(Environment.NewLine,
            new[] { Resources.Msg_init_settings_check }.Concat(deficiencies.Select(deficiency => deficiency.DisplayText)));
    }

    /// <summary>診断ログ用に、ページの識別子と理由を一行へまとめます。</summary>
    /// <param name="deficiencies">記録する一覧。</param>
    internal static string FormatDiagnostic(IEnumerable<SettingsDeficiency> deficiencies)
    {
        ArgumentNullException.ThrowIfNull(deficiencies);
        return string.Join(" | ", deficiencies.Select(deficiency =>
            deficiency.Category + ": " + deficiency.Reason.Replace(Environment.NewLine, " / ", StringComparison.Ordinal)));
    }
}

/// <summary>
/// 文字列の鍵で引く表示用の文言です。WPFの索引付きBindingへ渡し、元の状態が変わったときに全鍵の再評価を通知します。
/// 判定は持たず、所有者が渡した解決関数の結果を表示するだけです。
/// </summary>
public sealed class SettingsKeyedText : INotifyPropertyChanged
{
    private readonly Func<string, string> resolve;

    /// <summary>鍵から文言を求める解決関数で作成します。</summary>
    /// <param name="resolve">鍵に対応する文言を返す関数。該当がなければ空文字列またはnullを返します。</param>
    internal SettingsKeyedText(Func<string, string> resolve)
    {
        this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    /// <summary>鍵に対応する文言です。該当がなければ空文字列を返します。</summary>
    /// <param name="key">ページまたは欄の列挙名。</param>
    public string this[string key] => resolve(key) ?? string.Empty;

    /// <inheritdoc />
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>全鍵の文言を再評価するよう表示へ通知します。</summary>
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}
