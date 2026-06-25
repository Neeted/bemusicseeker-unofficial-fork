using System.Windows;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// message box 表示要求を表します。owner と表示内容を同じ request に持たせ、call site ごとの owner 解決をなくすために使います。
/// </summary>
internal class UiMessageRequest
{
    /// <summary>
    /// message box 表示要求を初期化します。
    /// </summary>
    /// <param name="messageBoxText">表示する本文。</param>
    /// <param name="caption">dialog title。</param>
    /// <param name="button">表示するボタン。</param>
    /// <param name="icon">表示する icon。</param>
    /// <param name="defaultResult">既定の結果。</param>
    /// <param name="options">WPF message box option。</param>
    /// <param name="owner">呼び出し側が既に把握している owner window。</param>
    internal UiMessageRequest(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None,
        Window owner = null)
    {
        MessageBoxText = messageBoxText;
        Caption = caption;
        Button = button;
        Icon = icon;
        DefaultResult = defaultResult;
        Options = options;
        Owner = owner;
    }

    /// <summary>
    /// 表示する本文です。
    /// </summary>
    internal string MessageBoxText { get; }

    /// <summary>
    /// dialog title です。
    /// </summary>
    internal string Caption { get; }

    /// <summary>
    /// 表示するボタン構成です。
    /// </summary>
    internal MessageBoxButton Button { get; }

    /// <summary>
    /// 表示する icon です。
    /// </summary>
    internal MessageBoxImage Icon { get; }

    /// <summary>
    /// 既定の message box 結果です。
    /// </summary>
    internal MessageBoxResult DefaultResult { get; }

    /// <summary>
    /// WPF message box option です。
    /// </summary>
    internal MessageBoxOptions Options { get; }

    /// <summary>
    /// 呼び出し側が明示した owner window です。null の場合は coordinator が解決します。
    /// </summary>
    internal Window Owner { get; }
}

/// <summary>
/// ユーザー判断を必要とする確認 dialog の要求を表します。
/// </summary>
internal sealed class UiConfirmationRequest : UiMessageRequest
{
    /// <summary>
    /// 確認 dialog 表示要求を初期化します。
    /// </summary>
    /// <param name="messageBoxText">表示する本文。</param>
    /// <param name="caption">dialog title。</param>
    /// <param name="button">表示するボタン。</param>
    /// <param name="icon">表示する icon。</param>
    /// <param name="defaultResult">既定の結果。</param>
    /// <param name="options">WPF message box option。</param>
    /// <param name="owner">呼び出し側が既に把握している owner window。</param>
    internal UiConfirmationRequest(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None,
        Window owner = null)
        : base(messageBoxText, caption, button, icon, defaultResult, options, owner)
    {
    }
}

/// <summary>
/// file picker の要求を表します。Unit 5 で詳細な picker option をここへ集約します。
/// </summary>
internal sealed class UiFilePickerRequest
{
}

/// <summary>
/// folder picker の要求を表します。Unit 5 で詳細な picker option をここへ集約します。
/// </summary>
internal sealed class UiFolderPickerRequest
{
}

/// <summary>
/// save file picker の要求を表します。Unit 5 で詳細な picker option をここへ集約します。
/// </summary>
internal sealed class UiSaveFilePickerRequest
{
}

/// <summary>
/// progress operation の要求を表します。Unit 4 で progress 表示と operation context をここへ集約します。
/// </summary>
internal sealed class UiProgressRequest
{
}
