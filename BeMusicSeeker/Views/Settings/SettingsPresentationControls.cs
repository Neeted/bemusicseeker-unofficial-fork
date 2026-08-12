using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace BeMusicSeeker.Views.Settings;

/// <summary>Presents a major settings section without owning settings state.</summary>
public class SettingsSection : HeaderedContentControl
{
    /// <summary>Identifies the optional section description.</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsSection), new PropertyMetadata(string.Empty));

    /// <summary>Gets or sets the optional explanatory text displayed below the heading.</summary>
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
}

/// <summary>Presents a label, optional description and validation text above one settings editor.</summary>
public class SettingsField : HeaderedContentControl
{
    private readonly Dictionary<DependencyObject, BindingExpressionBase> fallbackAutomationNameBindings = [];

    /// <summary>Identifies the optional field description.</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsField), new PropertyMetadata(string.Empty));
    /// <summary>Identifies the optional field validation text.</summary>
    public static readonly DependencyProperty ValidationMessageProperty = DependencyProperty.Register(nameof(ValidationMessage), typeof(string), typeof(SettingsField), new PropertyMetadata(string.Empty));
    /// <summary>Gets or sets the optional explanatory text.</summary>
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    /// <summary>Gets or sets the optional validation text.</summary>
    public string ValidationMessage { get => (string)GetValue(ValidationMessageProperty); set => SetValue(ValidationMessageProperty, value); }

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ApplyEditorAutomationNames();
    }

    /// <inheritdoc />
    protected override void OnHeaderChanged(object oldHeader, object newHeader)
    {
        base.OnHeaderChanged(oldHeader, newHeader);
        ApplyEditorAutomationNames();
    }

    /// <inheritdoc />
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        ApplyEditorAutomationNames();
    }

    private void ApplyEditorAutomationNames()
    {
        string accessibleName = Header?.ToString() ?? string.Empty;
        HashSet<DependencyObject> editors = FindEditors(Content as DependencyObject);
        foreach (KeyValuePair<DependencyObject, BindingExpressionBase> ownedBinding in fallbackAutomationNameBindings.ToArray())
        {
            if (editors.Contains(ownedBinding.Key))
            {
                continue;
            }
            if (ReferenceEquals(
                BindingOperations.GetBindingExpressionBase(ownedBinding.Key, AutomationProperties.NameProperty),
                ownedBinding.Value))
            {
                BindingOperations.ClearBinding(ownedBinding.Key, AutomationProperties.NameProperty);
            }
            fallbackAutomationNameBindings.Remove(ownedBinding.Key);
        }

        foreach (DependencyObject editor in editors)
        {
            if (fallbackAutomationNameBindings.TryGetValue(editor, out BindingExpressionBase ownedBinding))
            {
                if (ReferenceEquals(
                    BindingOperations.GetBindingExpressionBase(editor, AutomationProperties.NameProperty),
                    ownedBinding))
                {
                    // The owned one-way binding follows Header changes without content DataContext.
                    continue;
                }

                // The caller replaced our fallback with a local value or BindingExpression.
                fallbackAutomationNameBindings.Remove(editor);
            }

            if (!string.IsNullOrWhiteSpace(accessibleName)
                && editor.ReadLocalValue(AutomationProperties.NameProperty) == DependencyProperty.UnsetValue
                && string.IsNullOrWhiteSpace(AutomationProperties.GetName(editor)))
            {
                BindingExpressionBase expression = BindingOperations.SetBinding(
                    editor,
                    AutomationProperties.NameProperty,
                    new Binding(nameof(Header))
                    {
                        Source = this,
                        Mode = BindingMode.OneWay
                    });
                fallbackAutomationNameBindings[editor] = expression;
            }
        }
    }

    private static HashSet<DependencyObject> FindEditors(DependencyObject contentRoot)
    {
        var editors = new HashSet<DependencyObject>();
        if (contentRoot == null)
        {
            return editors;
        }
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(contentRoot);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }
            if (current is TextBoxBase or ComboBox or ListBox or Slider)
            {
                editors.Add(current);
            }
            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
            }
            foreach (object child in LogicalTreeHelper.GetChildren(current))
            {
                if (child is DependencyObject dependencyObject)
                {
                    pending.Push(dependencyObject);
                }
            }
        }
        return editors;
    }
}

/// <summary>Presents one descriptive settings option while leaving selection ownership to its content.</summary>
public class SettingsOptionRow : HeaderedContentControl
{
    /// <summary>Identifies the option description.</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsOptionRow), new PropertyMetadata(string.Empty));
    /// <summary>Gets or sets the explanatory text displayed below the option heading.</summary>
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
}

/// <summary>Presents a path as an explicitly read-only or editable field and raises a view-owned browse request.</summary>
public class SettingsPathPicker : Control
{
    private Button browseButton;

    /// <summary>Identifies the path picker label.</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(SettingsPathPicker), new PropertyMetadata(string.Empty));
    /// <summary>Identifies the optional path picker description.</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsPathPicker), new PropertyMetadata(string.Empty));
    /// <summary>Identifies the selected path.</summary>
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(nameof(Path), typeof(string), typeof(SettingsPathPicker), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    /// <summary>Identifies whether the path text is read-only.</summary>
    public static readonly DependencyProperty IsPathReadOnlyProperty = DependencyProperty.Register(nameof(IsPathReadOnly), typeof(bool), typeof(SettingsPathPicker), new PropertyMetadata(true));
    /// <summary>Identifies the browse button text.</summary>
    public static readonly DependencyProperty BrowseTextProperty = DependencyProperty.Register(nameof(BrowseText), typeof(string), typeof(SettingsPathPicker), new PropertyMetadata(string.Empty));
    /// <summary>Identifies the routed browse request event.</summary>
    public static readonly RoutedEvent BrowseRequestedEvent = EventManager.RegisterRoutedEvent(nameof(BrowseRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SettingsPathPicker));

    /// <summary>Gets or sets the field label.</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    /// <summary>Gets or sets the optional explanatory text.</summary>
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    /// <summary>Gets or sets the displayed path.</summary>
    public string Path { get => (string)GetValue(PathProperty); set => SetValue(PathProperty, value); }
    /// <summary>Gets or sets whether the displayed path can be typed directly.</summary>
    public bool IsPathReadOnly { get => (bool)GetValue(IsPathReadOnlyProperty); set => SetValue(IsPathReadOnlyProperty, value); }
    /// <summary>Gets or sets the browse button text.</summary>
    public string BrowseText { get => (string)GetValue(BrowseTextProperty); set => SetValue(BrowseTextProperty, value); }

    /// <summary>Occurs when the browse button is activated.</summary>
    public event RoutedEventHandler BrowseRequested { add => AddHandler(BrowseRequestedEvent, value); remove => RemoveHandler(BrowseRequestedEvent, value); }

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        if (browseButton != null)
        {
            browseButton.Click -= BrowseButtonClick;
        }
        base.OnApplyTemplate();
        browseButton = GetTemplateChild("PART_BrowseButton") as Button;
        if (browseButton != null)
        {
            browseButton.Click += BrowseButtonClick;
        }
    }

    private void BrowseButtonClick(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(BrowseRequestedEvent, this));
}

/// <summary>Presents a settings-owned collection editor with a reusable heading and description.</summary>
public class SettingsListEditor : SettingsField
{
}

/// <summary>Presents semantic status using both an icon and text so meaning never depends on color alone.</summary>
public class SettingsStatusBanner : ContentControl
{
    static SettingsStatusBanner()
    {
        VisibilityProperty.OverrideMetadata(
            typeof(SettingsStatusBanner),
            new FrameworkPropertyMetadata(Visibility.Visible, null, CoerceVisibility));
    }

    /// <summary>Creates a status banner whose empty initial content is omitted from layout and automation.</summary>
    public SettingsStatusBanner()
    {
        CoerceValue(VisibilityProperty);
    }

    /// <summary>Identifies the status icon text.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(SettingsStatusBanner), new PropertyMetadata("i"));
    /// <summary>Identifies the semantic status category used by automation and styling.</summary>
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status), typeof(string), typeof(SettingsStatusBanner), new PropertyMetadata("Information"));
    /// <summary>Gets or sets the visible status icon.</summary>
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    /// <summary>Gets or sets the semantic status category.</summary>
    public string Status { get => (string)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    /// <inheritdoc />
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        CoerceValue(VisibilityProperty);
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new SettingsStatusBannerAutomationPeer(this);
    }

    private static object CoerceVisibility(DependencyObject dependencyObject, object baseValue)
    {
        if ((Visibility)baseValue != Visibility.Visible)
        {
            return baseValue;
        }

        object content = ((SettingsStatusBanner)dependencyObject).Content;
        return content == null || (content is string text && string.IsNullOrWhiteSpace(text))
            ? Visibility.Collapsed
            : baseValue;
    }
}

/// <summary>
/// Draws the redundant status glyph without creating a standalone automation element;
/// the owning <see cref="SettingsStatusBanner"/> exposes the complete message and status instead.
/// </summary>
public sealed class SettingsStatusIcon : TextBlock
{
    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return null;
    }
}

/// <summary>Exposes a status banner's message and semantic status without treating its icon as accessible text.</summary>
internal sealed class SettingsStatusBannerAutomationPeer : FrameworkElementAutomationPeer
{
    /// <summary>Creates the automation peer for a settings status banner.</summary>
    /// <param name="owner">The banner represented by this peer.</param>
    internal SettingsStatusBannerAutomationPeer(SettingsStatusBanner owner)
        : base(owner)
    {
    }

    /// <inheritdoc />
    protected override string GetNameCore()
    {
        object content = ((SettingsStatusBanner)Owner).Content;
        if (!HasDisplayableContent(content))
        {
            return string.Empty;
        }

        return content is string text ? text : base.GetNameCore();
    }

    /// <inheritdoc />
    protected override string GetItemStatusCore()
    {
        var banner = (SettingsStatusBanner)Owner;
        return HasDisplayableContent(banner.Content) ? banner.Status ?? string.Empty : string.Empty;
    }

    private static bool HasDisplayableContent(object content)
    {
        return content != null && (content is not string text || !string.IsNullOrWhiteSpace(text));
    }
}
