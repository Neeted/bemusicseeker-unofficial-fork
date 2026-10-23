using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;

namespace BeMusicSeeker.Views;

/// <summary>標準DatePickerの日付を変更せず、表示言語・短い日付書式・Calendarの表示だけを更新します。</summary>
public static class DatePickerPresentation
{
    /// <summary>言語サービスから受け取る表示文化圏です。別の言語状態を保存しません。</summary>
    public static readonly DependencyProperty CultureProperty = DependencyProperty.RegisterAttached(
        "Culture", typeof(CultureInfo), typeof(DatePickerPresentation), new PropertyMetadata(null, CultureChanged));

    /// <summary>日付部品へ適用する表示文化圏を取得します。</summary>
    /// <param name="element">共通表示を採用する日付部品。</param>
    public static CultureInfo GetCulture(DependencyObject element) => (CultureInfo)element.GetValue(CultureProperty);

    /// <summary>日付部品へ表示文化圏を接続します。選択日・表示月・表示モードは変更しません。</summary>
    /// <param name="element">共通表示を採用する日付部品。</param>
    /// <param name="value">言語サービスから導出した文化圏。</param>
    public static void SetCulture(DependencyObject element, CultureInfo value) => element.SetValue(CultureProperty, value);

    private static void CultureChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not DatePicker picker) { return; }
        if (args.OldValue == null) { picker.Loaded += PickerLoaded; }
        if (args.NewValue == null) { picker.Loaded -= PickerLoaded; return; }
        Refresh(picker);
    }

    private static void PickerLoaded(object sender, RoutedEventArgs args) => Refresh((DatePicker)sender);

    private static void Refresh(DatePicker picker)
    {
        CultureInfo culture = GetCulture(picker);
        if (culture == null) { return; }
        var language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
        CultureInfo dateCulture;
        try { dateCulture = language.GetSpecificCulture(); }
        catch (InvalidOperationException)
        {
            // 空の言語等はWPFのDynamicPropertyReaderと同じen-US解決を使う。
            dateCulture = CultureInfo.GetCultureInfo("en-US");
        }
        DateTimeFormatInfo dateFormat = GetDateFormat(dateCulture);
        // WPFのDateTimeHelperはLanguageの値だけでなく、既定値以外から設定されたかを判定する。
        // SetCurrentValueでは既定値由来のままOS文化圏へ戻るため、この表示接続が明示値を所有する。
        picker.SetValue(FrameworkElement.LanguageProperty, language);
        picker.SetCurrentValue(DatePicker.FirstDayOfWeekProperty, dateFormat.FirstDayOfWeek);

        // DatePicker.Textは再解析を起こすが、標準PART_TextBoxのTextChangedはTextへ無解析で転送する。
        // 現在値から直接整形すれば、月日順が入れ替わる言語でも選択日とBindingを維持できる。
        if (picker.Template?.FindName("PART_TextBox", picker) is DatePickerTextBox editor)
        {
            // DatePickerTextBox自身が再有効化でIsReadOnlyをfalseにするため、標準処理の後で戻す。
            // 言語更新で重複登録せず、このテキスト部品だけが自分のイベント購読を所有する。
            editor.IsEnabledChanged -= EditorIsEnabledChanged;
            editor.IsEnabledChanged += EditorIsEnabledChanged;
            editor.SetValue(TextBox.IsReadOnlyProperty, true);
            editor.SetCurrentValue(TextBox.TextProperty, picker.SelectedDate?.ToString(dateFormat.ShortDatePattern, dateFormat) ?? string.Empty);
        }
        if (picker.Template?.FindName("PART_Popup", picker) is Popup { Child: System.Windows.Controls.Calendar calendar })
        {
            calendar.SetValue(FrameworkElement.LanguageProperty, language);
            // DatePickerが週開始をBindingするため、CalendarのLanguage変更だけではセルが更新されない。
            // 公開の標準更新経路で同じセルを更新する。日付・表示月・表示モードは操作しない。
            calendar.OnApplyTemplate();
        }
    }

    private static void EditorIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs args)
        => ((DatePickerTextBox)sender).SetValue(TextBox.IsReadOnlyProperty, true);

    /// <summary>WPFのDateTimeHelper.GetDateFormatと同じGregorian解決を使い、表示後の標準再解析で別の日付にならないようにします。</summary>
    private static DateTimeFormatInfo GetDateFormat(CultureInfo culture)
    {
        if (culture.Calendar is GregorianCalendar) { return culture.DateTimeFormat; }
        GregorianCalendar gregorian = null;
        foreach (System.Globalization.Calendar calendar in culture.OptionalCalendars)
        {
            if (calendar is GregorianCalendar candidate)
            {
                gregorian ??= candidate;
                if (candidate.CalendarType == GregorianCalendarTypes.Localized)
                {
                    gregorian = candidate;
                    break;
                }
            }
        }
        // Gregorian候補がない場合のInvariantも独自の代替ではなく、使用中WPFと同じ規則。
        var format = (DateTimeFormatInfo)(gregorian == null ? CultureInfo.InvariantCulture.DateTimeFormat : culture.DateTimeFormat).Clone();
        format.Calendar = gregorian ?? new GregorianCalendar();
        return format;
    }
}
