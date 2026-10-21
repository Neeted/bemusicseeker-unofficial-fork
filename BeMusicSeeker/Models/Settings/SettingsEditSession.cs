using System;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>構成が共有する確定値の読取りと、起動・終了の既存保存入口です。設定候補は各セクションが別に所有します。</summary>
internal interface ISettingsEditSession
{
    /// <summary>確定値を読む既存wrapper。設定画面の候補を保管せず、実処理は開始時snapshotを捕捉します。</summary>
    Settings Values { get; }

    /// <summary>構成開始時に一度、保存済み設定を読み込みます。画面の再表示・局所確定・通常保存からは呼びません。</summary>
    void Reload();

    /// <summary>画面外で変更済みの項目だけを保存します。</summary>
    void Save();

    /// <summary>既存の環境再起動要求の対象だけを保存し、成功後に公開します。</summary>
    void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string historyIdentity);
}

/// <summary>既存構成へ確定設定と起動・終了保存を接続します。編集全体のcopy・差分・復元は所有しません。</summary>
internal sealed class SettingsEditSession : ISettingsEditSession
{
    private readonly Settings values;

    internal static SettingsEditSession CreateDefault() => new(Settings.Default);

    internal SettingsEditSession(Settings values)
    {
        this.values = values ?? throw new ArgumentNullException(nameof(values));
    }

    public Settings Values => values;

    public void Reload() => values.Reload();

    public void Save() => values.Save();

    /// <inheritdoc />
    public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string historyIdentity)
    {
        // 環境を一回保存し、その後の終端保存が現在選択中の履歴表示を保持します。
        environment.Commit(values);
        values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
    }
}
