using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Logging;

namespace BeMusicSeeker.Models;

internal sealed class ApplicationSettingsInitializationResult
{
    internal ApplicationSettingsInitializationResult(bool firstStartup, string appearanceTheme, CultureInfo culture)
    {
        FirstStartup = firstStartup;
        AppearanceTheme = appearanceTheme;
        Culture = culture;
    }

    internal bool FirstStartup { get; }

    internal string AppearanceTheme { get; }

    internal CultureInfo Culture { get; }
}

/// <summary>
/// 起動時の legacy migration、設定 version upgrade、言語 / theme 正規化を一つの lifecycle として実行します。
/// </summary>
internal sealed class ApplicationSettingsLifecycle
{
    private readonly Action<ISet<string>, string> legacyMigration;

    private readonly Action upgradeSettings;

    private readonly Action saveSettings;

    private readonly IApplicationSettingsStore settingsStore;

    private readonly Action normalizeSettings;

    private readonly Action<Exception> warnSaveFailure;

    /// <summary>
    /// 設定の初回読取り前の正規化と、起動時のバージョン・言語・テーマ反映を構成します。
    /// </summary>
    /// <param name="legacyMigration">必要な場合に旧ユーザー設定を移行します。</param>
    /// <param name="upgradeSettings">設定ストアを現在のバージョンへ更新します。</param>
    /// <param name="saveSettings">バージョン更新後の設定を保存します。</param>
    /// <param name="settingsStore">起動時のバージョン・言語・テーマを読み書きするストア。</param>
    /// <param name="warnSaveFailure">読取り可能な設定の保存失敗を診断します。診断先の例外は初期化を停止しません。</param>
    /// <param name="normalizeSettings">ストアの初回読取り前に保存設定を正規化します。</param>
    internal ApplicationSettingsLifecycle(
        Action<ISet<string>, string> legacyMigration = null,
        Action upgradeSettings = null,
        Action saveSettings = null,
        IApplicationSettingsStore settingsStore = null,
        Action normalizeSettings = null,
        Action<Exception> warnSaveFailure = null)
    {
        this.legacyMigration = legacyMigration ?? LegacyUserConfigMigrator.MigrateIfNeeded;
        this.settingsStore = settingsStore ?? new SettingsApplicationSettingsStore();
        this.upgradeSettings = upgradeSettings ?? this.settingsStore.Upgrade;
        this.saveSettings = saveSettings ?? this.settingsStore.Save;
        this.warnSaveFailure = warnSaveFailure ?? (exception => NLogWrapper.TraceLogger?.Warn(exception, "Settings save failed"));
        this.normalizeSettings = normalizeSettings ?? (() => PortableSettingsProvider.NormalizeCurrentPortableConfig());
    }

    /// <summary>
    /// 保存設定の正規化後にストアを読み、バージョン・文化圏・テーマを反映します。保存診断の失敗は結果を変えません。
    /// </summary>
    /// <param name="availableCultureNames">アプリが対応する文化圏名。</param>
    /// <param name="serializableVersionFactory">現在のアプリバージョンを返す処理。</param>
    /// <param name="firstStartupObserver">初回起動の判定後に通知する任意の購読先。</param>
    internal ApplicationSettingsInitializationResult Initialize(
        IEnumerable<string> availableCultureNames,
        Func<SerializableVersion> serializableVersionFactory,
        Action<bool> firstStartupObserver = null)
    {
        if (availableCultureNames == null)
        {
            throw new ArgumentNullException(nameof(availableCultureNames));
        }
        if (serializableVersionFactory == null)
        {
            throw new ArgumentNullException(nameof(serializableVersionFactory));
        }

        HashSet<string> availableCultures = new(availableCultureNames.Where(value => value != null));
        // Only publication of an already valid document may fail non-fatally.
        try
        {
            normalizeSettings();
        }
        catch (PortableSettingsException exception) when (exception.Operation == "Save")
        {
            WarnSaveFailure(exception);
        }
        bool firstStartup = false;
        SerializableVersion serializableVersion = null;
        try
        {
            serializableVersion = serializableVersionFactory();
            if (serializableVersion == null)
            {
                throw new InvalidOperationException("Serializable version factory returned null.");
            }
        }
        catch (Exception ex)
        {
            // Version discovery is independent of reading the user-owned document.
            try
            {
                NLogWrapper.TraceLogger?.Warn(ex, "Settings version discovery failed");
            }
            catch
            {
                // 任意の診断失敗で読取り可能な設定の初期化を止めません。
            }
        }
        if (serializableVersion != null)
        {
            SerializableVersion savedVersion = settingsStore.AssemblyVersion;
            firstStartup = savedVersion == null;
            firstStartupObserver?.Invoke(firstStartup);
            if (savedVersion == null || savedVersion != serializableVersion)
            {
                upgradeSettings();
                settingsStore.AssemblyVersion = serializableVersion;
                try
                {
                    saveSettings();
                }
                catch (Exception exception) when (exception is not PortableSettingsException portableFailure
                    || portableFailure.Operation == "Save")
                {
                    // Save rereads the file: losing readable input is fatal even after initial materialization.
                    WarnSaveFailure(exception);
                }
            }
        }

        if (!availableCultures.Contains(settingsStore.Language))
        {
            settingsStore.Language = "ja-JP";
        }
        string appearanceTheme = AppThemeService.NormalizeTheme(settingsStore.AppearanceTheme);
        settingsStore.AppearanceTheme = appearanceTheme;
        var culture = CultureInfo.GetCultureInfo(settingsStore.Language);
        Resources.Culture = culture;
        return new ApplicationSettingsInitializationResult(firstStartup, appearanceTheme, culture);
    }

    private void WarnSaveFailure(Exception exception)
    {
        try
        {
            warnSaveFailure(exception);
        }
        catch
        {
            // 保存警告の診断先を再通知せず、読取り可能な設定の反映を続けます。
        }
    }

    /// <summary>起動ファイル所有者がポータブル設定の欠落を確認した後に、旧設定の移行を実行します。</summary>
    internal void MigrateLegacy(IEnumerable<string> availableCultureNames, string currentCultureName)
    {
        if (availableCultureNames == null)
        {
            throw new ArgumentNullException(nameof(availableCultureNames));
        }
        HashSet<string> availableCultures = new(availableCultureNames.Where(value => value != null));
        legacyMigration(availableCultures, currentCultureName);
    }

    /// <summary>設定準備の成功後に表示へ適用する初期化済みテーマを返します。</summary>
    internal string GetCurrentAppearanceTheme()
    {
        return settingsStore.AppearanceTheme;
    }
}
