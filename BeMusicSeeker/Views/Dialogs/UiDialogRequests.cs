using System;
using System.Windows;
using Parago.Windows;

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
/// window modal dialog の要求を表します。owner 解決と active modal 登録を coordinator に寄せるために使います。
/// </summary>
internal sealed class UiWindowDialogRequest<TWindow, TResult>
    where TWindow : Window
{
    internal UiWindowDialogRequest(
        Func<TWindow> createWindow,
        Func<TWindow, TResult> createResult,
        Window owner = null)
    {
        CreateWindow = createWindow ?? throw new ArgumentNullException(nameof(createWindow));
        CreateResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
        Owner = owner;
    }

    internal Func<TWindow> CreateWindow { get; }

    internal Func<TWindow, TResult> CreateResult { get; }

    internal Window Owner { get; }
}

/// <summary>
/// file picker の要求を表します。Unit 5 で詳細な picker option をここへ集約します。
/// </summary>
internal sealed class UiFilePickerRequest
{
    internal UiFilePickerRequest(
        string title = null,
        string fileName = null,
        string initialDirectory = null,
        string filter = null,
        string defaultExtension = null,
        bool multiselect = false,
        bool ensureFileExists = true,
        bool ensurePathExists = true,
        Window owner = null)
    {
        Title = title;
        FileName = fileName;
        InitialDirectory = initialDirectory;
        Filter = filter;
        DefaultExtension = defaultExtension;
        Multiselect = multiselect;
        EnsureFileExists = ensureFileExists;
        EnsurePathExists = ensurePathExists;
        Owner = owner;
    }

    internal string Title { get; }

    internal string FileName { get; }

    internal string InitialDirectory { get; }

    internal string Filter { get; }

    internal string DefaultExtension { get; }

    internal bool Multiselect { get; }

    internal bool EnsureFileExists { get; }

    internal bool EnsurePathExists { get; }

    internal Window Owner { get; }
}

/// <summary>
/// folder picker の要求を表します。Unit 5 で詳細な picker option をここへ集約します。
/// </summary>
internal sealed class UiFolderPickerRequest
{
    internal UiFolderPickerRequest(
        string title = null,
        string selectedPath = null,
        bool multiselect = false,
        bool ensurePathExists = true,
        Window owner = null)
    {
        Title = title;
        SelectedPath = selectedPath;
        Multiselect = multiselect;
        EnsurePathExists = ensurePathExists;
        Owner = owner;
    }

    internal string Title { get; }

    internal string SelectedPath { get; }

    internal bool Multiselect { get; }

    internal bool EnsurePathExists { get; }

    internal Window Owner { get; }
}

/// <summary>
/// save file picker の要求を表します。Unit 5 で詳細な picker option をここへ集約します。
/// </summary>
internal sealed class UiSaveFilePickerRequest
{
    internal UiSaveFilePickerRequest(
        string title = null,
        string fileName = null,
        string defaultExtension = null,
        string filter = null,
        bool addExtension = true,
        Window owner = null)
    {
        Title = title;
        FileName = fileName;
        DefaultExtension = defaultExtension;
        Filter = filter;
        AddExtension = addExtension;
        Owner = owner;
    }

    internal string Title { get; }

    internal string FileName { get; }

    internal string DefaultExtension { get; }

    internal string Filter { get; }

    internal bool AddExtension { get; }

    internal Window Owner { get; }
}

/// <summary>
/// progress operation の要求を表します。Unit 4 で progress 表示と operation context をここへ集約します。
/// </summary>
internal sealed class UiProgressRequest
{
    internal UiProgressRequest(
        string title,
        string label,
        ProgressDialogSettings settings = null,
        Window owner = null)
    {
        Title = title;
        Label = label;
        Settings = settings;
        Owner = owner;
    }

    internal string Title { get; }

    internal string Label { get; }

    internal ProgressDialogSettings Settings { get; }

    internal Window Owner { get; }
}
