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

/// <summary>表示フォントの設定と既定値を分ける用途です。</summary>
internal enum AppFontRole
{
    Ui,
    Table
}

/// <summary>
/// 通常UIと主画面の一覧の表示フォントを、保存値から求めてアプリのリソースへ適用します。
/// 配色辞書とは独立したアプリ直下のリソースを置き換えるため、テーマの切替では失われません。
/// </summary>
internal static class AppFontService
{
    /// <summary>通常UIが動的に参照する表示フォントのリソースキーです。</summary>
    internal const string UiResourceKey = "App.UiFontFamily";

    /// <summary>主画面の三つの一覧が動的に参照する表示フォントのリソースキーです。</summary>
    internal const string TableResourceKey = "App.TableFontFamily";

    private const string DefaultUiFontFamilyName = "Yu Gothic UI";

    /// <summary>一覧の既定と字形の代替に使うフォント名です。DirectWriteの描画も同じ名前を使います。</summary>
    internal const string DefaultTableFontFamilyName = "Meiryo UI";

    /// <summary>
    /// 文字サイズに対する行の高さの倍率です。既定のフォントの行の高さと同じ値に固定し、
    /// フォントを変えても部品の高さと一覧の行内の位置が変わらないようにします。
    /// </summary>
    internal const double LineSpacing = 1.27d;

    private static FontFamily defaultUiFontFamily;
    private static FontFamily defaultTableFontFamily;

    /// <summary>一覧の既定のフォントです。リソースが未適用の一覧もこの値を使います。</summary>
    internal static FontFamily DefaultTableFontFamily => defaultTableFontFamily ??= CreateNormalizedFontFamily(DefaultTableFontFamilyName, DefaultTableFontFamilyName);

    /// <summary>空の保存値と、選択されたフォントの代替に使う用途別のフォント名です。</summary>
    internal static string GetDefaultFontFamilyName(AppFontRole role)
    {
        return role == AppFontRole.Ui ? DefaultUiFontFamilyName : DefaultTableFontFamilyName;
    }

    private static FontFamily GetDefaultFontFamily(AppFontRole role)
    {
        return role == AppFontRole.Ui
            ? defaultUiFontFamily ??= CreateNormalizedFontFamily(DefaultUiFontFamilyName, DefaultUiFontFamilyName)
            : DefaultTableFontFamily;
    }

    /// <summary>保存するフォント名から前後の空白を除きます。nullは既定を表す空文字にします。</summary>
    internal static string NormalizeFontFamilyName(string name)
    {
        return name?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// 保存値から実際に使うフォントを作ります。選択されたフォントを第一候補、既定のフォントを代替候補にするため、
    /// 未インストールのフォントと、選択されたフォントに字形がない文字は、既定のフォントで表示されます。
    /// 行の高さと基線の位置は、フォントが申告する値ではなく固定値にします。
    /// </summary>
    internal static FontFamily CreateFontFamily(string name, AppFontRole role)
    {
        string normalizedName = NormalizeFontFamilyName(name);
        string defaultName = GetDefaultFontFamilyName(role);
        if (normalizedName.Length == 0 || string.Equals(normalizedName, defaultName, StringComparison.OrdinalIgnoreCase))
        {
            return GetDefaultFontFamily(role);
        }
        // WPFは複数候補をコンマで区切るため、名前に含まれるコンマは二重にして区切りと区別する。
        return CreateNormalizedFontFamily(normalizedName.Replace(",", ",,", StringComparison.Ordinal) + ", " + defaultName, defaultName);
    }

    /// <summary>
    /// フォントの選択欄で、候補の名前を描くフォントを作ります。<see cref="CreateFontFamily"/> と同じ規則で候補のフォントを使いますが、
    /// 記号用の文字コード表を持つフォント（Wingdingsなど）は名前が記号の字形になって読めないため、既定のフォントを返します。
    /// アプリ全体へ適用するフォントの規則は変えません。
    /// </summary>
    internal static FontFamily CreatePreviewFontFamily(string name, AppFontRole role)
    {
        string normalizedName = NormalizeFontFamilyName(name);
        if (normalizedName.Length == 0 || IsSymbolFontFamily(normalizedName))
        {
            return GetDefaultFontFamily(role);
        }
        return CreateFontFamily(normalizedName, role);
    }

    /// <summary>フォントが記号用の文字コード表を持つかを返します。未インストールの名前はWPFの代替フォントで判定されます。</summary>
    private static bool IsSymbolFontFamily(string normalizedName)
    {
        var typeface = new Typeface(new FontFamily(normalizedName.Replace(",", ",,", StringComparison.Ordinal)), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        return typeface.TryGetGlyphTypeface(out GlyphTypeface glyphTypeface) && glyphTypeface.Symbol;
    }

    /// <summary>
    /// 候補のフォントを束ね、行の高さと基線を固定した合成フォントを作ります。
    /// WPFは文字をフォントが申告する行の箱で配置し、その高さと箱の中の文字の位置はフォントごとに違います。
    /// そのまま使うと、フォントによって上下中央や下寄せの文字がずれ、行の高さが小さいフォントは一覧で折り返します。
    /// 基線は、第一候補の大文字の高さが行の上下中央に来る位置にします。
    /// </summary>
    private static FontFamily CreateNormalizedFontFamily(string candidates, string defaultName)
    {
        // 候補の先頭が未インストールの場合は、次の候補（既定のフォント）の大文字の高さが返る。
        double capsHeight = new Typeface(new FontFamily(candidates), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).CapsHeight;
        if (!(capsHeight > 0d) || double.IsInfinity(capsHeight))
        {
            capsHeight = new Typeface(new FontFamily(defaultName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).CapsHeight;
        }
        var fontFamily = new FontFamily();
        fontFamily.FamilyMaps.Add(new FontFamilyMap { Target = candidates });
        fontFamily.LineSpacing = LineSpacing;
        fontFamily.Baseline = (LineSpacing + capsHeight) / 2d;
        return fontFamily;
    }

    /// <summary>
    /// フォントが優先順に並べる候補の名前を取り出します。<see cref="CreateFontFamily"/> が組み立てた合成フォントは
    /// 選択されたフォントと既定のフォントの順に、物理フォントは指定された名前を返します。
    /// 一覧の描画が同じ規則でフォントを解決するため、候補の並びの規則の持ち主はこのクラスに保ちます。
    /// </summary>
    /// <param name="fontFamily">候補を取り出すフォント。nullは候補なしです。</param>
    internal static IReadOnlyList<string> GetCandidateFamilyNames(FontFamily fontFamily)
    {
        if (fontFamily == null)
        {
            return [];
        }
        string candidates = fontFamily.FamilyMaps.Count > 0 ? fontFamily.FamilyMaps[0].Target : fontFamily.Source;
        if (string.IsNullOrEmpty(candidates))
        {
            return [];
        }
        var names = new List<string>();
        var current = new System.Text.StringBuilder();
        for (int i = 0; i <= candidates.Length; i++)
        {
            // 名前の中のコンマは二重にして区切りと区別している。
            if (i < candidates.Length && candidates[i] == ',' && i + 1 < candidates.Length && candidates[i + 1] == ',')
            {
                current.Append(',');
                i++;
            }
            else if (i == candidates.Length || candidates[i] == ',')
            {
                string name = current.ToString().Trim();
                int fragment = name.LastIndexOf('#');
                if (fragment >= 0)
                {
                    name = name[(fragment + 1)..].Trim();
                }
                if (name.Length > 0)
                {
                    names.Add(name);
                }
                current.Clear();
            }
            else
            {
                current.Append(candidates[i]);
            }
        }
        return names;
    }

    /// <summary>
    /// 保存値のフォントを用途に対応するアプリのリソースへ適用します。通常UIの設定は、
    /// WPFの標準スタイルを使うメニューなどにも届くようシステムのフォントのキーにも適用します。
    /// </summary>
    internal static void ApplyFontFamily(string name, AppFontRole role)
    {
        Application application = Application.Current;
        if (application == null)
        {
            return;
        }
        FontFamily fontFamily = CreateFontFamily(name, role);
        ResourceDictionary resources = application.Resources;
        if (role == AppFontRole.Table)
        {
            resources[TableResourceKey] = fontFamily;
            return;
        }
        resources[UiResourceKey] = fontFamily;
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
