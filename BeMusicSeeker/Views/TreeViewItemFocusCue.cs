#nullable enable

using System;
using System.Windows;
using System.Windows.Input;

namespace BeMusicSeeker.Views;

/// <summary>
/// TreeViewItemのキーボード操作のフォーカス枠を、見出し行だけへ描くための表示指定を保持します。
/// TreeViewItemは展開中に子孫の項目まで含む大きさになるため、WPFのFocusVisualStyleを使うと
/// 項目全体に枠が出ます。そこで項目自身はFocusVisualStyleを持たず、テンプレートが見出し行へ重ねた
/// 枠を、この添付プロパティが真の間だけ表示します。
/// </summary>
/// <remarks>
/// 表示の時機は、WPFのFocusVisualStyleと同じ判定に従います。つまり、項目自身がフォーカスを得た時点で
/// 最後の入力機器がキーボード、またはOSがキーボードの手がかりを常に表示する設定
/// (<see cref="SystemParameters.KeyboardCues"/>)のときだけ表示し、項目自身がフォーカスを失うと消します。
/// アプリで入力機器を独自に追跡せず、AltキーやTabによる再表示も行いません。
/// </remarks>
public static class TreeViewItemFocusCue
{
    /// <summary>
    /// 見出し行のフォーカス枠を表示するかを示します。既定値はfalseです。
    /// 通常はクラスハンドラーが設定するため、値の直接設定は表示の確認用です。
    /// </summary>
    public static readonly DependencyProperty IsVisibleProperty =
        DependencyProperty.RegisterAttached(
            "IsVisible",
            typeof(bool),
            typeof(TreeViewItemFocusCue),
            new FrameworkPropertyMetadata(false));

    /// <summary>
    /// 型の初期化でTreeViewItemのフォーカス変更を購読します。テンプレートが添付プロパティを参照した時点で
    /// 型が初期化され、項目がフォーカスを得る前にクラスハンドラーが登録済みになります。
    /// 静的フィールドの初期化順に依存せず登録するため、明示的な静的コンストラクターを使います。
    /// </summary>
    static TreeViewItemFocusCue()
    {
        // TreeViewItem自身が既にイベントを処理済みにしても購読を失わないよう、処理済みも受け取る。
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.TreeViewItem),
            Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnGotKeyboardFocus),
            true);
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.TreeViewItem),
            Keyboard.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnLostKeyboardFocus),
            true);
    }

    /// <summary>対象の項目で、見出し行のフォーカス枠を表示しているかを取得します。</summary>
    public static bool GetIsVisible(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsVisibleProperty);
    }

    /// <summary>対象の項目で、見出し行のフォーカス枠を表示するかを設定します。</summary>
    public static void SetIsVisible(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsVisibleProperty, value);
    }

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 子孫の項目から伝播したイベントでは、親の表示を変えない。
        if (sender is not DependencyObject item || !ReferenceEquals(e.NewFocus, sender))
        {
            return;
        }

        // WPFのFocusVisualStyleを表示する判定と同じ条件。
        bool showsKeyboardCue = InputManager.Current.MostRecentInputDevice is KeyboardDevice
            || SystemParameters.KeyboardCues;
        SetIsVisible(item, showsKeyboardCue);
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not DependencyObject item || !ReferenceEquals(e.OldFocus, sender))
        {
            return;
        }

        SetIsVisible(item, false);
    }
}
