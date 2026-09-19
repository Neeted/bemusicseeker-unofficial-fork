using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>
/// Modeless themed window that presents one playlist lamp aggregation session.
/// The manager starts the session before showing the window so no loading placeholder
/// is exposed as the first presentable state.
/// </summary>
public partial class PlaylistLampViewerWindow : ThemedWindow
{
    private const double MaximumFolderLabelColumnWidth = 170d;

    private readonly PlaylistLampViewerViewModel viewModel;

    private DispatcherOperation folderColumnWidthUpdate;

    private bool isFolderRowsSubscribed;

    private bool isClosed;

    /// <summary>
    /// Gets the shared width of the clear and rank folder-label columns.
    /// The view recalculates it from the current rendered typeface and caps it so bars
    /// retain usable space for long folder names.
    /// </summary>
    public static readonly DependencyProperty FolderLabelColumnWidthProperty =
        DependencyProperty.Register(
            nameof(FolderLabelColumnWidth),
            typeof(double),
            typeof(PlaylistLampViewerWindow),
            new FrameworkPropertyMetadata(MaximumFolderLabelColumnWidth));

    /// <summary>
    /// Gets the shared width of the clear and rank folder-count columns.
    /// The view measures the current localized numeric count text and caps it at the
    /// width needed for the localized N0 representation of 9999.
    /// </summary>
    public static readonly DependencyProperty FolderCountColumnWidthProperty =
        DependencyProperty.Register(
            nameof(FolderCountColumnWidth),
            typeof(double),
            typeof(PlaylistLampViewerWindow),
            new FrameworkPropertyMetadata(0d));

    /// <summary>
    /// Creates a viewer temporarily owned by the supplied main window for initial placement.
    /// The manager releases that owner after the first modeless presentation attempt.
    /// </summary>
    /// <param name="owner">Main-window shell.</param>
    /// <param name="viewModel">Dispatcher-bound viewer projection.</param>
    internal PlaylistLampViewerWindow(Window owner, PlaylistLampViewerViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        DataContext = viewModel;
        Loaded += WindowLoaded;
        Unloaded += WindowUnloaded;
        ContentRendered += WindowContentRendered;
        Closed += WindowClosed;
    }

    /// <summary>Gets the projection displayed by this window.</summary>
    internal PlaylistLampViewerViewModel ViewModel => viewModel;

    /// <summary>Gets or sets the width shared by both folder-label columns.</summary>
    public double FolderLabelColumnWidth
    {
        get => (double)GetValue(FolderLabelColumnWidthProperty);
        private set => SetValue(FolderLabelColumnWidthProperty, value);
    }

    /// <summary>Gets or sets the width shared by both folder-count columns.</summary>
    public double FolderCountColumnWidth
    {
        get => (double)GetValue(FolderCountColumnWidthProperty);
        private set => SetValue(FolderCountColumnWidthProperty, value);
    }

    private void SegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PlaylistLampViewerSegmentViewModel segment })
        {
            viewModel.InvokeSegment(segment);
        }
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeFolderRows();
        QueueFolderColumnWidthUpdate();
    }

    private void WindowUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFolderRows();
        CancelFolderColumnWidthUpdate();
    }

    private void WindowContentRendered(object sender, EventArgs e)
    {
        UpdateFolderColumnWidths();
    }

    private void WindowClosed(object sender, EventArgs e)
    {
        isClosed = true;
        UnsubscribeFolderRows();
        CancelFolderColumnWidthUpdate();
    }

    private void SubscribeFolderRows()
    {
        if (isClosed || isFolderRowsSubscribed)
        {
            return;
        }
        viewModel.FolderRows.CollectionChanged += FolderRowsChanged;
        isFolderRowsSubscribed = true;
    }

    private void UnsubscribeFolderRows()
    {
        if (!isFolderRowsSubscribed)
        {
            return;
        }
        viewModel.FolderRows.CollectionChanged -= FolderRowsChanged;
        isFolderRowsSubscribed = false;
    }

    private void FolderRowsChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        QueueFolderColumnWidthUpdate();
    }

    private void QueueFolderColumnWidthUpdate()
    {
        if (isClosed || !IsLoaded || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }
        CancelFolderColumnWidthUpdate();
        try
        {
            folderColumnWidthUpdate = Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(() =>
                {
                    folderColumnWidthUpdate = null;
                    if (!isClosed && IsLoaded)
                    {
                        UpdateFolderColumnWidths();
                    }
                }));
        }
        catch (InvalidOperationException)
        {
            // Window teardown can race a collection notification; no later layout is needed.
        }
    }

    private void CancelFolderColumnWidthUpdate()
    {
        if (folderColumnWidthUpdate == null)
        {
            return;
        }
        try
        {
            folderColumnWidthUpdate.Abort();
        }
        catch (InvalidOperationException)
        {
        }
        folderColumnWidthUpdate = null;
    }

    private void UpdateFolderColumnWidths()
    {
        if (isClosed || !IsLoaded)
        {
            return;
        }
        TextBlock labelFormatSource = FindDescendants<TextBlock>(this)
            .FirstOrDefault(textBlock =>
                textBlock.DataContext is PlaylistLampViewerFolderRowViewModel
                && AutomationProperties.GetAutomationId(textBlock) == "PlaylistLampViewerFolderLabel");
        TextBlock countFormatSource = FindDescendants<TextBlock>(this)
            .FirstOrDefault(textBlock =>
                textBlock.DataContext is PlaylistLampViewerFolderRowViewModel
                && AutomationProperties.GetAutomationId(textBlock) == "PlaylistLampViewerFolderCount");

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double maximumLabelWidth = MeasureFolderTextColumn(
            labelFormatSource,
            row => row.FolderName,
            fallbackFontWeight: FontWeights.SemiBold,
            pixelsPerDip);
        double maximumCountWidth = MeasureFolderTextColumn(
            countFormatSource,
            row => row.CountText,
            fallbackFontWeight: FontWeights.Normal,
            pixelsPerDip);
        double countCapWidth = MeasureFolderText(
            countFormatSource,
            9999.ToString("N0", CultureInfo.CurrentCulture),
            fallbackFontWeight: FontWeights.Normal,
            pixelsPerDip);

        FolderLabelColumnWidth = Math.Min(MaximumFolderLabelColumnWidth, maximumLabelWidth);
        FolderCountColumnWidth = Math.Min(maximumCountWidth, countCapWidth);
    }

    private double MeasureFolderTextColumn(
        TextBlock formatSource,
        Func<PlaylistLampViewerFolderRowViewModel, string> textSelector,
        FontWeight fallbackFontWeight,
        double pixelsPerDip)
    {
        double maximumWidth = 0d;
        foreach (PlaylistLampViewerFolderRowViewModel row in viewModel.FolderRows)
        {
            if (row == null)
            {
                continue;
            }
            string text = textSelector(row);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            maximumWidth = Math.Max(
                maximumWidth,
                MeasureFolderText(formatSource, text, fallbackFontWeight, pixelsPerDip));
        }
        return maximumWidth;
    }

    private double MeasureFolderText(
        TextBlock formatSource,
        string text,
        FontWeight fallbackFontWeight,
        double pixelsPerDip)
    {
        FontFamily fontFamily = formatSource?.FontFamily ?? new FontFamily("Segoe UI");
        FontStyle fontStyle = formatSource?.FontStyle ?? FontStyles.Normal;
        FontWeight fontWeight = formatSource?.FontWeight ?? fallbackFontWeight;
        FontStretch fontStretch = formatSource?.FontStretch ?? FontStretches.Normal;
        double fontSize = formatSource != null && formatSource.FontSize > 0d
            ? formatSource.FontSize
            : 12d;
        var typeface = new Typeface(fontFamily, fontStyle, fontWeight, fontStretch);
        var measuredText = new FormattedText(
            text ?? string.Empty,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            formatSource != null
                ? VisualTreeHelper.GetDpi(formatSource).PixelsPerDip
                : pixelsPerDip);
        return measuredText.WidthIncludingTrailingWhitespace;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root == null)
        {
            yield break;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (T descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}

/// <summary>
/// Chooses an opaque black or white foreground for a filled viewer element by comparing
/// the WCAG sRGB relative luminance contrast of each candidate against its resolved
/// solid-color background.
/// </summary>
public sealed class PlaylistLampContrastForegroundConverter : IValueConverter
{
    /// <summary>Returns the higher-contrast opaque foreground brush.</summary>
    /// <param name="value">The resolved element background.</param>
    /// <param name="targetType">Binding target type.</param>
    /// <param name="parameter">Unused binding parameter.</param>
    /// <param name="culture">Unused binding culture.</param>
    /// <returns>An opaque black or white brush.</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SolidColorBrush { Color.A: 255 } background)
        {
            return Brushes.Black;
        }

        double luminance = RelativeLuminance(background.Color);
        double blackContrast = (luminance + 0.05d) / 0.05d;
        double whiteContrast = 1.05d / (luminance + 0.05d);
        return blackContrast >= whiteContrast ? Brushes.Black : Brushes.White;
    }

    /// <summary>One-way converter; reverse conversion is unsupported.</summary>
    /// <param name="value">Unused target value.</param>
    /// <param name="targetType">Unused source type.</param>
    /// <param name="parameter">Unused binding parameter.</param>
    /// <param name="culture">Unused binding culture.</param>
    /// <returns>Never returns.</returns>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.03928d
                ? value / 12.92d
                : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
        }

        return 0.2126d * Linearize(color.R)
            + 0.7152d * Linearize(color.G)
            + 0.0722d * Linearize(color.B);
    }
}

/// <summary>
/// Provides a width-aware, localized percentage label for a positive lamp segment.
/// Narrow segments intentionally remain unlabeled in the bar; their complete semantic
/// detail remains available through the legend, tooltip, and automation name.
/// </summary>
public sealed class PlaylistLampSegmentPercentageLabelConverter : IMultiValueConverter
{
    /// <summary>Returns the percentage label when it can fit without clipping.</summary>
    /// <param name="values">Segment view model, arranged button width, and the button itself.</param>
    /// <param name="targetType">Binding target type.</param>
    /// <param name="parameter">Unused binding parameter.</param>
    /// <param name="culture">Binding culture.</param>
    /// <returns>A complete localized percentage or an empty string for a narrow segment.</returns>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not { Length: >= 3 }
            || values[0] is not PlaylistLampViewerSegmentViewModel segment
            || !segment.HasPositiveWidth
            || values[1] is not double width
            || width <= 0d
            || values[2] is not Button button)
        {
            return string.Empty;
        }

        CultureInfo displayCulture = culture ?? CultureInfo.CurrentCulture;
        string label = segment.PercentageText;
        double textWidth = new FormattedText(
            label,
            displayCulture,
            FlowDirection.LeftToRight,
            new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch),
            double.IsNaN(button.FontSize) || button.FontSize <= 0d ? 12d : button.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(button).PixelsPerDip).WidthIncludingTrailingWhitespace;

        // Leave room for the button border and a small breathing margin. Returning no
        // content below this threshold prevents partial labels in narrow weighted bars.
        return width >= textWidth + 8d ? label : string.Empty;
    }

    /// <summary>Multi-binding conversion in the reverse direction is unsupported.</summary>
    /// <param name="value">Unused target value.</param>
    /// <param name="targetTypes">Unused source types.</param>
    /// <param name="parameter">Unused binding parameter.</param>
    /// <param name="culture">Unused binding culture.</param>
    /// <returns>Always throws because the presentation is one-way.</returns>
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Arranges weighted children into a bounded horizontal stack.
/// Each positive child receives a fraction of the available width based on its weight;
/// the final child receives the remaining pixels, preventing rounding overflow.
/// </summary>
public sealed class PlaylistLampWeightedStackPanel : Panel
{
    /// <summary>Attached weight used by the panel. Viewer segments bind this to chart count.</summary>
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight",
        typeof(double),
        typeof(PlaylistLampWeightedStackPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    /// <summary>Gets the attached child weight.</summary>
    /// <param name="element">Child element.</param>
    /// <returns>Non-negative weight.</returns>
    public static double GetWeight(DependencyObject element)
        => (double)(element?.GetValue(WeightProperty) ?? 0d);

    /// <summary>Sets the attached child weight.</summary>
    /// <param name="element">Child element.</param>
    /// <param name="value">Non-negative weight.</param>
    public static void SetWeight(DependencyObject element, double value)
        => element?.SetValue(WeightProperty, Math.Max(0d, value));

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double desiredWidth = 0d;
        double desiredHeight = 0d;
        foreach (UIElement child in InternalChildren.Cast<UIElement>())
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            desiredWidth += child.DesiredSize.Width;
            desiredHeight = Math.Max(desiredHeight, child.DesiredSize.Height);
        }

        double width = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
            ? desiredWidth
            : Math.Max(0d, availableSize.Width);
        double height = double.IsInfinity(availableSize.Height) || double.IsNaN(availableSize.Height)
            ? desiredHeight
            : Math.Max(0d, availableSize.Height);
        return new Size(width, Math.Max(height, desiredHeight));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        UIElement[] children = InternalChildren
            .Cast<UIElement>()
            .Where(child => child.Visibility != Visibility.Collapsed && GetWeight(child) > 0d)
            .ToArray();
        double totalWeight = children.Sum(GetWeight);
        if (children.Length == 0 || totalWeight <= 0d)
        {
            foreach (UIElement child in InternalChildren.Cast<UIElement>())
            {
                child.Arrange(new Rect(0d, 0d, 0d, Math.Max(0d, finalSize.Height)));
            }
            return finalSize;
        }

        double remaining = Math.Max(0d, finalSize.Width);
        double x = 0d;
        for (int index = 0; index < children.Length; index++)
        {
            UIElement child = children[index];
            double width = index == children.Length - 1
                ? remaining
                : Math.Max(0d, finalSize.Width * GetWeight(child) / totalWeight);
            child.Arrange(new Rect(x, 0d, width, Math.Max(0d, finalSize.Height)));
            x += width;
            remaining = Math.Max(0d, finalSize.Width - x);
        }

        foreach (UIElement child in InternalChildren.Cast<UIElement>()
            .Where(child => child.Visibility == Visibility.Collapsed || GetWeight(child) <= 0d))
        {
            child.Arrange(new Rect(0d, 0d, 0d, Math.Max(0d, finalSize.Height)));
        }
        return finalSize;
    }
}
