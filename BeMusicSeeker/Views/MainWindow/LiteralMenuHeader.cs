using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

/// <summary>
/// 利用者由来の文字列をメニュー項目の見出しにするときに、名前の「_」をアクセスキーとして扱わず、そのまま表示するための見出しテンプレートです。
/// 既定のメニュー項目スタイルは文字列の見出しの最初の「_」をアクセスキーとして消すため、操作名・ファイル名・フォルダ名の
/// 表示が実際の宛先と食い違わないよう、対象の項目だけがこのテンプレートを使います。見出しの値（クリック時に使う元の文字列）は変えません。
/// </summary>
public static class LiteralMenuHeader
{
    /// <summary>
    /// 見出しの文字列を <see cref="TextBlock"/> へ割り当てるテンプレートです。<see cref="TextBlock"/> はアクセスキーを解釈しません。
    /// </summary>
    public static DataTemplate Template { get; } = CreateTemplate();

    private static DataTemplate CreateTemplate()
    {
        var textBlock = new FrameworkElementFactory(typeof(TextBlock));
        textBlock.SetBinding(TextBlock.TextProperty, new Binding { Mode = BindingMode.OneWay });
        var template = new DataTemplate { VisualTree = textBlock };
        template.Seal();
        return template;
    }
}
