using System;
using System.Windows;
using BeMusicSeeker.Models;
using Parago.Windows;

namespace BeMusicSeeker.Views.Dialogs;

internal static class UiDialogPresentationAdapter
{
    internal static MessageBoxButton ToWpf(UiDialogButton button)
        => button switch
        {
            UiDialogButton.OK => MessageBoxButton.OK,
            UiDialogButton.OKCancel => MessageBoxButton.OKCancel,
            UiDialogButton.YesNo => MessageBoxButton.YesNo,
            UiDialogButton.YesNoCancel => MessageBoxButton.YesNoCancel,
            _ => throw new ArgumentOutOfRangeException(nameof(button), button, null),
        };

    internal static MessageBoxImage ToWpf(UiDialogIcon icon)
        => icon switch
        {
            UiDialogIcon.None => MessageBoxImage.None,
            UiDialogIcon.Hand => MessageBoxImage.Hand,
            UiDialogIcon.Question => MessageBoxImage.Question,
            UiDialogIcon.Exclamation => MessageBoxImage.Exclamation,
            UiDialogIcon.Asterisk => MessageBoxImage.Asterisk,
            UiDialogIcon.Information => MessageBoxImage.Information,
            UiDialogIcon.Warning => MessageBoxImage.Warning,
            _ => throw new ArgumentOutOfRangeException(nameof(icon), icon, null),
        };

    internal static MessageBoxResult ToWpf(UiDialogDefaultResult result)
        => result switch
        {
            UiDialogDefaultResult.None => MessageBoxResult.None,
            UiDialogDefaultResult.OK => MessageBoxResult.OK,
            UiDialogDefaultResult.Cancel => MessageBoxResult.Cancel,
            UiDialogDefaultResult.Yes => MessageBoxResult.Yes,
            UiDialogDefaultResult.No => MessageBoxResult.No,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
        };
}

/// <summary>
/// message box 表示要求を表します。owner と表示内容を同じ request に持たせ、call site ごとの owner 解決をなくすために使います。
/// </summary>
internal class UiMessageRequest
{
    /// <summary>
    /// 標準の OK・Hand・OK default の error 通知要求を初期化します。
    /// </summary>
    /// <param name="messageBoxText">表示する本文。</param>
    /// <param name="caption">dialog title。</param>
    /// <returns>error 通知用の表示要求。</returns>
    internal static UiMessageRequest CreateError(string messageBoxText, string caption)
    {
        return new UiMessageRequest(
            messageBoxText,
            caption,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK);
    }

    /// <summary>
    /// 標準の警告通知要求を初期化します。
    /// </summary>
    internal static UiMessageRequest CreateWarning(string messageBoxText, string caption)
    {
        return new UiMessageRequest(
            messageBoxText,
            caption,
            MessageBoxButton.OK,
            MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

    /// <summary>
    /// 標準の処理結果通知要求を初期化します。
    /// </summary>
    internal static UiMessageRequest CreateInformation(
        string messageBoxText,
        string caption,
        bool completedSuccessfully)
    {
        return new UiMessageRequest(
            messageBoxText,
            caption,
            MessageBoxButton.OK,
            completedSuccessfully ? MessageBoxImage.Asterisk : MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

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
    /// <param name="warningMessageBoxText">本文とは別に警告色で表示する補助本文。</param>
    internal UiMessageRequest(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None,
        Window owner = null,
        string warningMessageBoxText = null)
    {
        MessageBoxText = messageBoxText;
        Caption = caption;
        Button = button;
        Icon = icon;
        DefaultResult = defaultResult;
        Options = options;
        Owner = owner;
        WarningMessageBoxText = warningMessageBoxText;
    }

    internal UiMessageRequest(
        string messageBoxText,
        string caption,
        UiDialogButton button,
        UiDialogIcon icon,
        UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None,
        Window owner = null,
        string warningMessageBoxText = null)
        : this(
            messageBoxText,
            caption,
            UiDialogPresentationAdapter.ToWpf(button),
            UiDialogPresentationAdapter.ToWpf(icon),
            UiDialogPresentationAdapter.ToWpf(defaultResult),
            owner: owner,
            warningMessageBoxText: warningMessageBoxText)
    {
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

    /// <summary>
    /// 通常本文とは別に警告色で表示する補助本文です。警告だけを目立たせたい確認に使います。
    /// </summary>
    internal string WarningMessageBoxText { get; }
}

/// <summary>
/// ユーザー判断を必要とする確認 dialog の要求を表します。
/// </summary>
internal sealed class UiConfirmationRequest : UiMessageRequest
{
    /// <summary>
    /// 標準の OK/Cancel 確認要求を初期化します。
    /// </summary>
    internal static UiConfirmationRequest CreateDefault(
        string messageBoxText,
        string caption,
        string warningMessageBoxText = null)
    {
        return new UiConfirmationRequest(
            messageBoxText,
            caption,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel,
            warningMessageBoxText: warningMessageBoxText);
    }

    /// <summary>
    /// 標準の OK/Cancel・Question・Cancel default の確認要求を初期化します。
    /// </summary>
    /// <param name="messageBoxText">表示する本文。</param>
    /// <param name="caption">dialog title。</param>
    internal UiConfirmationRequest(string messageBoxText, string caption)
        : this(
            messageBoxText,
            caption,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel)
    {
    }

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
    /// <param name="warningMessageBoxText">本文とは別に警告色で表示する補助本文。</param>
    internal UiConfirmationRequest(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None,
        Window owner = null,
        string warningMessageBoxText = null)
        : base(messageBoxText, caption, button, icon, defaultResult, options, owner, warningMessageBoxText)
    {
    }

    internal UiConfirmationRequest(
        string messageBoxText,
        string caption,
        UiDialogButton button,
        UiDialogIcon icon,
        UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None,
        Window owner = null,
        string warningMessageBoxText = null)
        : base(messageBoxText, caption, button, icon, defaultResult, owner, warningMessageBoxText)
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
