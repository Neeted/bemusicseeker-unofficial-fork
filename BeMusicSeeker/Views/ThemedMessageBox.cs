using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeMusicSeeker.Views;

internal static class ThemedMessageBox
{
    public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None, MessageBoxOptions options = MessageBoxOptions.None)
    {
        MessageBoxResult result = NormalizeDefaultResult(button, defaultResult);
        Window dialog = CreateDialog(owner, messageBoxText, caption, button, icon, result, delegate (MessageBoxResult selected)
        {
            result = selected;
        });

        if ((options & MessageBoxOptions.RightAlign) == MessageBoxOptions.RightAlign)
        {
            dialog.FlowDirection = FlowDirection.RightToLeft;
        }

        bool? dialogResult = dialog.ShowDialog();
        return dialogResult == true ? result : NormalizeDefaultResult(button, defaultResult);
    }

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

    internal static bool IsAffirmative(MessageBoxResult result)
    {
        return result == MessageBoxResult.OK || result == MessageBoxResult.Yes;
    }

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

    private static Window CreateDialog(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult initialResult, Action<MessageBoxResult> setResult)
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
            Margin = new Thickness(0, 2, 0, 14),
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, "App.TextBrush");
        Grid.SetRow(message, 0);
        Grid.SetColumn(message, 1);
        layout.Children.Add(message);

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
