#nullable enable

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace BeMusicSeeker.Views;

/// <summary>表示元のBindingを保持したまま、編集期間だけ独立した候補を持つツリー見出しです。</summary>
public partial class EditableTextBlock : UserControl, IComponentConnector, IStyleConnector
{
    private string? editStartText;

    /// <summary>表示元が供給する文字列です。編集・取消からは書き込みません。</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register("Text", typeof(string), typeof(EditableTextBlock), new PropertyMetadata(string.Empty));

    /// <summary>内部TextBoxが編集する短命な候補です。</summary>
    public static readonly DependencyProperty CandidateTextProperty = DependencyProperty.Register("CandidateText", typeof(string), typeof(EditableTextBlock), new PropertyMetadata(string.Empty));

    /// <summary>編集の許可です。変更時には既存候補と未成立の開始要求を破棄します。</summary>
    public static readonly DependencyProperty IsEditableProperty = DependencyProperty.Register("IsEditable", typeof(bool), typeof(EditableTextBlock), new PropertyMetadata(true, OnIsEditableChanged));

    /// <summary>実際の編集開始・終端を通知し、編集不可の間はtrueを受け付けない状態です。</summary>
    public static readonly DependencyProperty IsInEditModeProperty = DependencyProperty.Register("IsInEditMode", typeof(bool), typeof(EditableTextBlock), new FrameworkPropertyMetadata(false, OnIsInEditModeChanged, CoerceIsInEditMode));

    public static readonly DependencyProperty TextFormatProperty = DependencyProperty.Register("TextFormat", typeof(string), typeof(EditableTextBlock), new PropertyMetadata("{0}"));

    /// <summary>開始と終端を通知します。変更を確定した終端だけ確定候補を持ちます。</summary>
    public static readonly RoutedEvent EditModeChangedEvent = EventManager.RegisterRoutedEvent("EditModeChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(EditableTextBlock));

    /// <summary>表示元の文字列を取得・設定します。編集候補とは独立しています。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>内部editorの候補を取得・設定します。編集の終端で破棄されます。</summary>
    public string CandidateText
    {
        get => (string)GetValue(CandidateTextProperty);
        set => SetValue(CandidateTextProperty, value);
    }

    /// <summary>編集の許可を取得・設定します。許可の変更は候補を取り消します。</summary>
    public bool IsEditable
    {
        get => (bool)GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    /// <summary>実編集状態を取得・設定します。同じtrueの再設定は候補を初期化しません。</summary>
    public bool IsInEditMode
    {
        get => (bool)GetValue(IsInEditModeProperty);
        set => SetValue(IsInEditModeProperty, value);
    }

    /// <summary>整形表示の書式を取得・設定します。空文字は既定書式へ戻します。</summary>
    public string TextFormat
    {
        get => (string)GetValue(TextFormatProperty);
        set => SetValue(TextFormatProperty, value == "" ? "{0}" : value);
    }

    public string FormattedText => string.Format(TextFormat, Text);

    /// <summary>開始・取消・無変更はnull、変更確定は空文字を含む確定値を通知します。</summary>
    public event RoutedEventHandler EditModeChanged
    {
        add => AddHandler(EditModeChangedEvent, value);
        remove => RemoveHandler(EditModeChangedEvent, value);
    }

    /// <summary>compiledテンプレートと、対象交換・退役時の候補破棄を接続します。</summary>
    public EditableTextBlock()
    {
        InitializeComponent();
        Focusable = true;
        FocusVisualStyle = null;
        DataContextChanged += EditableTextBlock_DataContextChanged;
        Unloaded += EditableTextBlock_Unloaded;
    }

    private static object CoerceIsInEditMode(DependencyObject d, object value)
        => ((EditableTextBlock)d).IsEditable && (bool)value;

    private static void OnIsInEditModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (EditableTextBlock)d;
        string? committedText = null;
        if ((bool)e.NewValue)
        {
            control.editStartText = control.Text;
            control.CandidateText = control.Text;
        }
        else
        {
            if (control.editStartText != null && control.CandidateText != control.editStartText)
            {
                committedText = control.CandidateText;
            }
            // 同期通知中に次の編集が始まっても、その候補を旧終端が消さないよう先に破棄します。
            control.editStartText = null;
            control.CandidateText = string.Empty;
        }
        control.RaiseEvent(new EditableTextBlockEditModeChangedEventArgs(EditModeChangedEvent, committedText));
    }

    private static void OnIsEditableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 編集不可時のtrue要求のbase値も消し、再許可による意図しない編集再開を防ぎます。
        ((EditableTextBlock)d).CancelEdit();
    }

    private void TextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox editor)
        {
            editor.Focus();
            editor.SelectAll();
        }
    }

    private void TextBox_LostFocus(object sender, RoutedEventArgs e) => IsInEditMode = false;

    private void TextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            IsInEditMode = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelEdit();
            e.Handled = true;
        }
    }

    private void EditableTextBlock_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => CancelEdit();

    private void EditableTextBlock_Unloaded(object sender, RoutedEventArgs e) => CancelEdit();

    private void CancelEdit()
    {
        CandidateText = editStartText ?? string.Empty;
        IsInEditMode = false;
    }
}

/// <summary>同期通知後の別編集や部品再利用に影響されない確定候補を渡します。</summary>
public sealed class EditableTextBlockEditModeChangedEventArgs : RoutedEventArgs
{
    /// <summary>開始・取消・無変更はnull、変更確定は空文字を含む確定値です。</summary>
    public string? CommittedText { get; }

    /// <summary>編集状態の通知と、その通知時点で確定した候補を作ります。</summary>
    public EditableTextBlockEditModeChangedEventArgs(RoutedEvent routedEvent, string? committedText)
        : base(routedEvent)
    {
        CommittedText = committedText;
    }
}
