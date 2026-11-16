using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Views;

/// <summary>行やcontrolを実体化せず、UI Automationの対象と現在位置を照合する一覧の契約です。</summary>
internal interface ICustomTableAutomationSource : ICustomTableSelectionIdentitySource
{
    /// <summary>表示位置の寿命識別と検索用IDを返します。範囲外または識別不能ならnullです。</summary>
    CustomTableAutomationItem? GetAutomationItem(int index);

    /// <summary>捕捉した同じ生存対象の現在位置を返します。別対象への置換・消失時は-1です。</summary>
    int IndexOfAutomationIdentity(object identity);

    /// <summary>IDが一致し、startAfterIndexより後にある対象を返します。該当なしは-1です。</summary>
    int FindAutomationItem(string automationId, int startAfterIndex);
}

/// <summary>内部の生存対象識別と、外部から再検索するためのdomain IDを分離します。</summary>
internal readonly record struct CustomTableAutomationItem(object Identity, string AutomationId);

/// <summary>保存済みdomain値を区切りと衝突しないID成分にします。参照番号や全行の索引を保持しません。</summary>
internal static class CustomTableAutomationId
{
    /// <summary>既存scopeで使う型だけを捕捉します。libraryとpackageは保存先を使い、未知型は識別不可としてnullを返します。</summary>
    internal static string Scope(object value) => value switch
    {
        null => "n",
        BMSLibrary library => "library:" + Part(library.InitializationSongDbPath),
        ChartPackage package => "package:" + Part(package.path),
        ITuple tuple => Tuple(tuple),
        string text => "s:" + Part(text),
        bool flag => flag ? "true" : "false",
        Enum kind => kind.GetType().FullName + ":" + Part(kind.ToString("D")),
        _ => null
    };

    /// <summary>UTF-8のexact値を可逆に表し、空値とnullも区別します。</summary>
    internal static string Part(string value) => value == null ? "~" : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Tuple(ITuple tuple)
    {
        var result = new StringBuilder("(");
        for (int index = 0; index < tuple.Length; index++)
        {
            string component = Scope(tuple[index]);
            if (component == null) { return null; }
            result.Append(component.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(component);
        }
        return result.Append(')').ToString();
    }
}
