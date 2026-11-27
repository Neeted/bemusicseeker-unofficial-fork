using System;
using System.Globalization;
using BeMusicSeeker.Properties;
using Livet;

namespace BeMusicSeeker.ViewModels;

public partial class SettingsDialogViewModel
{
    private SettingsIntegerField externalTableSyncMaxConcurrencyField;
    private SettingsIntegerField urlDownloadMaxFileSizeMiBField;
    private SettingsIntegerField urlDownloadIdleTimeoutSecondsField;
    private SettingsIntegerField externalTableRequestTimeoutSecondsField;
    private SettingsIntegerField externalTableHtmlLimitMiBField;
    private SettingsIntegerField externalTableJsonLimitMiBField;

    /// <summary>外部難度表の同時取得数（表）の入力欄です。</summary>
    public SettingsIntegerField ExternalTableSyncMaxConcurrencyField => externalTableSyncMaxConcurrencyField ??= CreateNetworkField(
        nameof(Settings.ExternalTableSyncMaxConcurrency), Settings.ExternalTableSyncMaxConcurrencyRange, () => ApplicationSettings.ExternalTableSyncMaxConcurrency);

    /// <summary>譜面・アーカイブの1ファイル上限（MiB）の入力欄です。</summary>
    public SettingsIntegerField UrlDownloadMaxFileSizeMiBField => urlDownloadMaxFileSizeMiBField ??= CreateNetworkField(
        nameof(Settings.UrlDownloadMaxFileSizeMiB), Settings.UrlDownloadMaxFileSizeMiBRange, () => ApplicationSettings.UrlDownloadMaxFileSizeMiB);

    /// <summary>ファイル取得の応答待ち・無受信時間（秒）の入力欄です。</summary>
    public SettingsIntegerField UrlDownloadIdleTimeoutSecondsField => urlDownloadIdleTimeoutSecondsField ??= CreateNetworkField(
        nameof(Settings.UrlDownloadIdleTimeoutSeconds), Settings.UrlDownloadIdleTimeoutSecondsRange, () => ApplicationSettings.UrlDownloadIdleTimeoutSeconds);

    /// <summary>外部難度表の取得期限（秒）の入力欄です。</summary>
    public SettingsIntegerField ExternalTableRequestTimeoutSecondsField => externalTableRequestTimeoutSecondsField ??= CreateNetworkField(
        nameof(Settings.ExternalTableRequestTimeoutSeconds), Settings.ExternalTableRequestTimeoutSecondsRange, () => ApplicationSettings.ExternalTableRequestTimeoutSeconds);

    /// <summary>外部難度表のHTML本文上限（MiB）の入力欄です。JSON上限との大小関係は検査しません。</summary>
    public SettingsIntegerField ExternalTableHtmlLimitMiBField => externalTableHtmlLimitMiBField ??= CreateNetworkField(
        nameof(Settings.ExternalTableHtmlLimitMiB), Settings.ExternalTableHtmlLimitMiBRange, () => ApplicationSettings.ExternalTableHtmlLimitMiB);

    /// <summary>外部難度表のJSON本文上限（MiB）の入力欄です。HTML上限との大小関係は検査しません。</summary>
    public SettingsIntegerField ExternalTableJsonLimitMiBField => externalTableJsonLimitMiBField ??= CreateNetworkField(
        nameof(Settings.ExternalTableJsonLimitMiB), Settings.ExternalTableJsonLimitMiBRange, () => ApplicationSettings.ExternalTableJsonLimitMiB);

    private SettingsIntegerField CreateNetworkField(string settingName, Settings.IntegerRange range, Func<int> currentValue)
        => new(range, currentValue, value => TryCommitSetting(settingName, (settingName, value)));

    /// <summary>設定画面を開き直したとき、保存値を表示してすべての通信設定欄の入力エラーを消します。</summary>
    private void ClearNetworkFieldInputState()
    {
        foreach (SettingsIntegerField field in new[]
        {
            externalTableSyncMaxConcurrencyField, urlDownloadMaxFileSizeMiBField, urlDownloadIdleTimeoutSecondsField,
            externalTableRequestTimeoutSecondsField, externalTableHtmlLimitMiBField, externalTableJsonLimitMiBField
        })
        {
            field?.ResetInput();
        }
    }

    /// <summary>表示言語の変更で、すべての通信設定欄のエラー文言を現在の言語へ更新します。</summary>
    private void RaiseNetworkFieldValidationChanged()
    {
        externalTableSyncMaxConcurrencyField?.RaiseValidationChanged();
        urlDownloadMaxFileSizeMiBField?.RaiseValidationChanged();
        urlDownloadIdleTimeoutSecondsField?.RaiseValidationChanged();
        externalTableRequestTimeoutSecondsField?.RaiseValidationChanged();
        externalTableHtmlLimitMiBField?.RaiseValidationChanged();
        externalTableJsonLimitMiBField?.RaiseValidationChanged();
    }
}

/// <summary>
/// 範囲を持つ整数設定一欄の入力と検証の状態です。
/// 有効な値だけを項目ごとに即時保存し、無効な入力は保存せず、入力した文字列を残して欄をエラーにします。
/// </summary>
public sealed class SettingsIntegerField : ViewModel
{
    private readonly Settings.IntegerRange range;
    private readonly Func<int> currentValue;
    private readonly Func<int, bool> commit;
    private string rejectedText;

    internal SettingsIntegerField(Settings.IntegerRange range, Func<int> currentValue, Func<int, bool> commit)
    {
        this.range = range;
        this.currentValue = currentValue;
        this.commit = commit;
    }

    /// <summary>入力欄の文字列です。拒否した入力は、直前の保存値へ戻さず次の入力まで残します。</summary>
    public string Text
    {
        get => rejectedText ?? currentValue().ToString(CultureInfo.InvariantCulture);
        set
        {
            string input = value ?? string.Empty;
            // 前後の空白だけを許す10進整数です。空・整数以外・int範囲を超える桁・範囲外は保存せず、例外も出しません。
            if (!int.TryParse(input, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out int parsed)
                || !range.Contains(parsed))
            {
                rejectedText = input;
                RaiseValidationChanged();
                return;
            }
            // 保存に失敗した場合は、保存失敗の通知を既存の経路へ任せ、保存値を表示へ戻します。
            rejectedText = null;
            commit(parsed);
            RaiseValidationChanged();
        }
    }

    /// <summary>下限・上限を埋め込んだエラー文言です。有効な入力では空です。</summary>
    public string ValidationMessage => rejectedText == null
        ? string.Empty
        : string.Format(Resources.Network_setting_range_error_format, range.Minimum, range.Maximum);

    /// <summary>入力欄の検証表示種別です。無効な入力は <c>Error</c>、それ以外は空です。</summary>
    public string ValidationStatus => rejectedText == null ? string.Empty : "Error";

    /// <summary>保存値を表示してエラーを消します。</summary>
    internal void ResetInput()
    {
        rejectedText = null;
        RaiseValidationChanged();
    }

    /// <summary>文字列・エラー文言・表示種別の変更を通知します。</summary>
    internal void RaiseValidationChanged()
    {
        RaisePropertyChanged(nameof(Text));
        RaisePropertyChanged(nameof(ValidationMessage));
        RaisePropertyChanged(nameof(ValidationStatus));
    }
}
