using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace BeMusicSeeker.Models;

/// <summary>インストール済みのフォントファミリー一つの、保存する名前と言語別の表示名です。</summary>
/// <param name="Name">設定へ保存する基本の名前。</param>
/// <param name="LocalizedNames">言語タグ（<c>ja-jp</c> など）から表示名への対応。</param>
internal sealed record AppInstalledFontFamily(string Name, IReadOnlyDictionary<string, string> LocalizedNames);

/// <summary>フォントの選択欄に並べる候補一つの、保存する名前と表示名です。</summary>
/// <param name="Name">設定へ保存する名前。</param>
/// <param name="DisplayName">選択欄に表示する名前。</param>
internal sealed record AppFontChoice(string Name, string DisplayName);

/// <summary>
/// 通常の文字に使う表示フォントを、保存値から求めてアプリのリソースへ適用します。
/// 配色辞書とは独立したアプリ直下のリソースを置き換えるため、テーマの切替では失われません。
/// </summary>
internal static class AppFontService
{
    /// <summary>ウィンドウと一覧が動的に参照する、表示フォントのリソースキーです。</summary>
    internal const string ResourceKey = "App.UiFontFamily";

    /// <summary>保存値が空のときに使い、選択されたフォントの代替候補にもなる既定のフォント名です。</summary>
    internal const string DefaultFontFamilyName = "Meiryo UI";

    /// <summary>既定のフォントです。リソースが未適用の表示部品も、この値を使います。</summary>
    internal static FontFamily DefaultFontFamily { get; } = new(DefaultFontFamilyName);

    /// <summary>保存するフォント名から前後の空白を除きます。nullは既定を表す空文字にします。</summary>
    internal static string NormalizeFontFamilyName(string name)
    {
        return name?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// 保存値から実際に使うフォントを作ります。選択されたフォントを第一候補、既定のフォントを代替候補にするため、
    /// 未インストールのフォントと、選択されたフォントに字形がない文字は、既定のフォントで表示されます。
    /// </summary>
    internal static FontFamily CreateFontFamily(string name)
    {
        string normalizedName = NormalizeFontFamilyName(name);
        if (normalizedName.Length == 0 || string.Equals(normalizedName, DefaultFontFamilyName, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultFontFamily;
        }
        // WPFは複数候補をコンマで区切るため、名前に含まれるコンマは二重にして区切りと区別する。
        return new FontFamily(normalizedName.Replace(",", ",,", StringComparison.Ordinal) + ", " + DefaultFontFamilyName);
    }

    /// <summary>
    /// 保存値のフォントをアプリのリソースへ適用します。WPFの標準スタイルがシステムのフォントを指定する
    /// メニューやツールチップも従うよう、システムのフォントのキーも同じ値で置き換えます。
    /// </summary>
    internal static void ApplyFontFamily(string name)
    {
        Application application = Application.Current;
        if (application == null)
        {
            return;
        }
        FontFamily fontFamily = CreateFontFamily(name);
        ResourceDictionary resources = application.Resources;
        resources[ResourceKey] = fontFamily;
        resources[SystemFonts.MenuFontFamilyKey] = fontFamily;
        resources[SystemFonts.StatusFontFamilyKey] = fontFamily;
        resources[SystemFonts.MessageFontFamilyKey] = fontFamily;
    }

    /// <summary>Windowsにインストールされているフォントファミリーを列挙します。</summary>
    internal static IReadOnlyList<AppInstalledFontFamily> GetInstalledFontFamilies()
    {
        return Fonts.SystemFontFamilies
            .Select(family => new AppInstalledFontFamily(
                family.Source,
                family.FamilyNames.ToDictionary(pair => pair.Key.IetfLanguageTag, pair => pair.Value, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
    }

    /// <summary>
    /// インストール済みのフォントを、表示言語の名前（なければ基本の名前）の順に並べた候補を作ります。
    /// 保存済みの名前がどの候補とも一致しない場合は、現在の選択として表示できるよう先頭へ加えます。
    /// </summary>
    /// <param name="installedFamilies">インストール済みのフォントファミリー。</param>
    /// <param name="displayCulture">表示名と並び順に使う表示言語。</param>
    /// <param name="savedName">保存済みのフォント名。空文字は既定を表し、候補へは加えません。</param>
    internal static IReadOnlyList<AppFontChoice> BuildFontChoices(
        IEnumerable<AppInstalledFontFamily> installedFamilies,
        CultureInfo displayCulture,
        string savedName)
    {
        ArgumentNullException.ThrowIfNull(installedFamilies);
        ArgumentNullException.ThrowIfNull(displayCulture);
        var choices = installedFamilies
            .Select(family => new AppFontChoice(family.Name, ResolveDisplayName(family, displayCulture)))
            .OrderBy(choice => choice.DisplayName, StringComparer.Create(displayCulture, ignoreCase: true))
            .ThenBy(choice => choice.Name, StringComparer.Ordinal)
            .ToList();
        string normalizedSavedName = NormalizeFontFamilyName(savedName);
        if (normalizedSavedName.Length > 0 && !choices.Any(choice => string.Equals(choice.Name, normalizedSavedName, StringComparison.Ordinal)))
        {
            choices.Insert(0, new AppFontChoice(normalizedSavedName, normalizedSavedName));
        }
        return choices;
    }

    private static string ResolveDisplayName(AppInstalledFontFamily family, CultureInfo displayCulture)
    {
        for (CultureInfo culture = displayCulture; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
        {
            if (family.LocalizedNames.TryGetValue(culture.IetfLanguageTag, out string displayName) && !string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }
        }
        return family.Name;
    }
}
