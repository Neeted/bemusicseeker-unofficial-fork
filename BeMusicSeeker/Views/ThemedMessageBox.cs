using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeMusicSeeker.Views;

/// <summary>
/// アプリのテーマ resource に従う message box 表示部品です。OS native dialog では表せないテーマ色を通常運用中の確認に使います。
/// </summary>
internal static class ThemedMessageBox
{
    /// <summary>
    /// テーマ付き message box を表示し、legacy message box と同じ結果だけを返します。
    /// </summary>
    /// <param name="owner">owner window。</param>
    /// <param name="messageBoxText">表示する本文。</param>
    /// <param name="caption">dialog title。</param>
    /// <param name="button">表示するボタン。</param>
    /// <param name="icon">表示する icon。</param>
    /// <param name="defaultResult">既定の結果。</param>
    /// <param name="options">WPF message box option。</param>
    /// <returns>legacy message box 互換の結果。</returns>
    public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None, MessageBoxOptions options = MessageBoxOptions.None)
    {
        return ShowWithStatus(owner, messageBoxText, caption, button, icon, defaultResult, options).MessageBoxResult;
    }

    /// <summary>
    /// テーマ付き message box を表示し、ボタン選択と window close を区別できる結果を返します。
    /// </summary>
    /// <param name="owner">owner window。</param>
    /// <param name="messageBoxText">表示する本文。</param>
    /// <param name="caption">dialog title。</param>
    /// <param name="button">表示するボタン。</param>
    /// <param name="icon">表示する icon。</param>
    /// <param name="defaultResult">既定の結果。</param>
    /// <param name="options">WPF message box option。</param>
    /// <param name="warningMessageBoxText">本文とは別に警告色で表示する補助本文。</param>
    /// <returns>message box の表示結果。</returns>
    internal static ThemedMessageBoxResponse ShowWithStatus(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None, MessageBoxOptions options = MessageBoxOptions.None, string warningMessageBoxText = null)
    {
        MessageBoxResult result = NormalizeDefaultResult(button, defaultResult);
        bool wasButtonSelected = false;
        Window dialog = CreateDialog(owner, messageBoxText, caption, button, icon, result, warningMessageBoxText, delegate (MessageBoxResult selected)
        {
            result = selected;
            wasButtonSelected = true;
        });

        if ((options & MessageBoxOptions.RightAlign) == MessageBoxOptions.RightAlign)
        {
            dialog.FlowDirection = FlowDirection.RightToLeft;
        }

        bool? dialogResult = dialog.ShowDialog();
        if (dialogResult == true && wasButtonSelected)
        {
            return new ThemedMessageBoxResponse(result, closedWithoutSelection: false);
        }

        return new ThemedMessageBoxResponse(NormalizeDefaultResult(button, defaultResult), closedWithoutSelection: true);
    }

    /// <summary>
    /// 表示ボタンで選択できない既定値を、window close 時にも安全に返せる結果へ補正します。
    /// </summary>
    /// <param name="button">表示するボタン構成。</param>
    /// <param name="defaultResult">呼び出し側が指定した既定の結果。</param>
    /// <returns>指定ボタン構成で有効な既定の結果。</returns>
    internal static MessageBoxResult NormalizeDefaultResult(MessageBoxButton button, MessageBoxResult defaultResult)
    {
        if (IsValidResultForButton(button, defaultResult))
        {
            return defaultResult;
        }

        return button switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.None,
        };
    }

    /// <summary>
    /// message box 結果が処理続行を表すかどうかを返します。OK/Yes の違いを上位 route から隠すために使います。
    /// </summary>
    /// <param name="result">message box の結果。</param>
    /// <returns>処理続行を表す結果なら true。</returns>
    internal static bool IsAffirmative(MessageBoxResult result)
    {
        return result == MessageBoxResult.OK || result == MessageBoxResult.Yes;
    }

    /// <summary>
    /// message box 結果を確認応答の nullable bool へ変換します。Cancel/close を明示的な拒否と区別するために使います。
    /// </summary>
    /// <param name="result">message box の結果。</param>
    /// <returns>肯定なら true、否定なら false、キャンセルまたは close なら null。</returns>
    internal static bool? ToConfirmationResponse(MessageBoxResult result)
    {
        return result switch
        {
            MessageBoxResult.OK or MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            MessageBoxResult.Cancel or MessageBoxResult.None => null,
            _ => null,
        };
    }

    private static Window CreateDialog(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult initialResult, string warningMessageBoxText, Action<MessageBoxResult> setResult)
    {
        var dialog = new Window
        {
            Title = caption ?? string.Empty,
            Width = 460,
            MinWidth = 360,
            MaxWidth = 720,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            FontFamily = new FontFamily("Meiryo UI"),
        };
        if (owner != null)
        {
            dialog.Owner = owner;
        }
        dialog.SetResourceReference(Control.BackgroundProperty, "App.DialogBackgroundBrush");
        dialog.SetResourceReference(Control.ForegroundProperty, "App.TextBrush");

        var root = new Border
        {
            Padding = new Thickness(14),
            BorderThickness = new Thickness(1),
        };
        root.SetResourceReference(Border.BackgroundProperty, "App.DialogBackgroundBrush");
        root.SetResourceReference(Border.BorderBrushProperty, "App.DialogBorderBrush");

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBlock = new TextBlock
        {
            Text = GetIconText(icon),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Width = 32,
            Margin = new Thickness(0, 0, 10, 12),
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        iconBlock.SetResourceReference(TextBlock.ForegroundProperty, GetIconBrushKey(icon));
        Grid.SetRow(iconBlock, 0);
        Grid.SetColumn(iconBlock, 0);
        layout.Children.Add(iconBlock);

        var message = new TextBlock
        {
            Text = messageBoxText ?? string.Empty,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, "App.TextBrush");

        var messagePanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
        };
        messagePanel.Children.Add(message);
        if (!string.IsNullOrWhiteSpace(warningMessageBoxText))
        {
            var warningMessage = new TextBlock
            {
                Text = warningMessageBoxText,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 0),
            };
            warningMessage.SetResourceReference(TextBlock.ForegroundProperty, "App.WarningTextBrush");
            messagePanel.Children.Add(warningMessage);
        }

        var messageScroll = new ScrollViewer
        {
            Content = messagePanel,
            MaxHeight = Math.Max(180, Math.Min(360, SystemParameters.WorkArea.Height * 0.55)),
            Margin = new Thickness(0, 0, 0, 14),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
        };
        Grid.SetRow(messageScroll, 0);
        Grid.SetColumn(messageScroll, 1);
        layout.Children.Add(messageScroll);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetRow(buttons, 1);
        Grid.SetColumnSpan(buttons, 2);
        layout.Children.Add(buttons);

        foreach (KeyValuePair<MessageBoxResult, string> pair in GetButtons(button))
        {
            var dialogButton = new Button
            {
                Content = pair.Value,
                MinWidth = 72,
                Margin = new Thickness(6, 0, 0, 0),
                IsDefault = pair.Key == initialResult || (initialResult == MessageBoxResult.None && IsAffirmative(pair.Key)),
                IsCancel = pair.Key == MessageBoxResult.Cancel || (button == MessageBoxButton.YesNo && pair.Key == MessageBoxResult.No),
            };
            dialogButton.Click += delegate
            {
                setResult(pair.Key);
                dialog.DialogResult = true;
            };
            buttons.Children.Add(dialogButton);
        }

        root.Child = layout;
        dialog.Content = root;
        return dialog;
    }

    private static IEnumerable<KeyValuePair<MessageBoxResult, string>> GetButtons(MessageBoxButton button)
    {
        return button switch
        {
            MessageBoxButton.OKCancel => new[]
            {
                new KeyValuePair<MessageBoxResult, string>(MessageBoxResult.OK, "OK"),
                new KeyValuePair<MessageBoxResult, string>(MessageBoxResult.Cancel, "Cancel"),
            },
            MessageBoxButton.YesNo =>
            [
                new KeyValuePair<MessageBoxResult, string>(MessageBoxResult.Yes, "Yes"),
                new KeyValuePair<MessageBoxResult, string>(MessageBoxResult.No, "No"),
            ],
            MessageBoxButton.YesNoCancel =>
            [
                new KeyValuePair<MessageBoxResult, string>(MessageBoxResult.Yes, "Yes"),
                new KeyValuePair<MessageBoxResult, string>(MessageBoxResult.No, "No"),
                new KeyValuePair<MessageBoxResult, string>(MessageBoxResult.Cancel, "Cancel"),
            ],
            _ =>
            [
                new KeyValuePair<MessageBoxResult, string>(MessageBoxResult.OK, "OK"),
            ],
        };
    }

    private static bool IsValidResultForButton(MessageBoxButton button, MessageBoxResult result)
    {
        return button switch
        {
            MessageBoxButton.OK => result is MessageBoxResult.OK,
            MessageBoxButton.OKCancel => result is MessageBoxResult.OK or MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => result is MessageBoxResult.Yes or MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => result is MessageBoxResult.Yes or MessageBoxResult.No or MessageBoxResult.Cancel,
            _ => false,
        };
    }

    private static string GetIconText(MessageBoxImage icon)
    {
        return icon switch
        {
            MessageBoxImage.Hand or MessageBoxImage.Stop or MessageBoxImage.Error => "!",
            MessageBoxImage.Exclamation or MessageBoxImage.Warning => "!",
            MessageBoxImage.Question => "?",
            MessageBoxImage.Asterisk or MessageBoxImage.Information => "i",
            _ => string.Empty,
        };
    }

    private static string GetIconBrushKey(MessageBoxImage icon)
    {
        return icon switch
        {
            MessageBoxImage.Hand or MessageBoxImage.Stop or MessageBoxImage.Error => "App.WarningTextBrush",
            MessageBoxImage.Exclamation or MessageBoxImage.Warning => "App.WarningTextBrush",
            MessageBoxImage.Question => "App.AccentSubtleBrush",
            MessageBoxImage.Asterisk or MessageBoxImage.Information => "App.AccentSubtleBrush",
            _ => "App.SubtleTextBrush",
        };
    }
}

/// <summary>
/// <see cref="ThemedMessageBox"/> の結果を表します。coordinator が window close とボタン選択を区別するために使います。
/// </summary>
internal readonly struct ThemedMessageBoxResponse
{
    /// <summary>
    /// message box の結果を初期化します。
    /// </summary>
    /// <param name="messageBoxResult">legacy message box 互換の戻り値。</param>
    /// <param name="closedWithoutSelection">ボタン選択なしで閉じられた場合は true。</param>
    internal ThemedMessageBoxResponse(MessageBoxResult messageBoxResult, bool closedWithoutSelection)
    {
        MessageBoxResult = messageBoxResult;
        ClosedWithoutSelection = closedWithoutSelection;
    }

    /// <summary>
    /// legacy message box 互換の戻り値です。
    /// </summary>
    internal MessageBoxResult MessageBoxResult { get; }

    /// <summary>
    /// ユーザーがボタンを選ばず window close で閉じたかどうかです。
    /// </summary>
    internal bool ClosedWithoutSelection { get; }
}
