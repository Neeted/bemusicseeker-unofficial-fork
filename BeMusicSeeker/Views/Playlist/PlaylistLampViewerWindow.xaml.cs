using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>
/// 一つのプレイリストのランプ集計セッションを表示するモードレスのテーマ対応窓です。
/// 管理主体が初回結果を確定してから表示し、表示直前のOwnerと実DPIで初期配置します。
/// </summary>
public partial class PlaylistLampViewerWindow : ThemedWindow
{
    private const double MaximumFolderLabelColumnWidth = 170d;

    private readonly PlaylistLampViewerViewModel viewModel;

    private readonly Size desiredSize;

    private readonly Size normalMinimum;

    private DispatcherOperation folderColumnWidthUpdate;

    private bool isFolderRowsSubscribed;

    private bool isClosed;

    /// <summary>
    /// 左右のフォルダ名列で共有する幅です。
    /// 現在の書体とDPIで再計測し、長い名前でもバーの領域を確保する上限を適用します。
    /// </summary>
    public static readonly DependencyProperty FolderLabelColumnWidthProperty =
        DependencyProperty.Register(
            nameof(FolderLabelColumnWidth),
            typeof(double),
            typeof(PlaylistLampViewerWindow),
            new FrameworkPropertyMetadata(MaximumFolderLabelColumnWidth));

    /// <summary>
    /// 左右のフォルダ件数列で共有する幅です。
    /// 現在の書体とDPIで件数を測り、ローカライズしたN0形式の9999の幅を上限にします。
    /// </summary>
    public static readonly DependencyProperty FolderCountColumnWidthProperty =
        DependencyProperty.Register(
            nameof(FolderCountColumnWidth),
            typeof(double),
            typeof(PlaylistLampViewerWindow),
            new FrameworkPropertyMetadata(0d));

    /// <summary>
    /// 初期配置に使うメイン画面を一時Ownerとして、ビューアを作ります。
    /// 希望寸法と通常の最小寸法を保持し、管理主体が初回表示を試みた後にOwnerを解除します。
    /// </summary>
    /// <param name="owner">初期配置の基準となるメイン画面。</param>
    /// <param name="viewModel">同じDispatcher上で更新される表示用モデル。</param>
    internal PlaylistLampViewerWindow(Window owner, PlaylistLampViewerViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        desiredSize = new Size(Width, Height);
        normalMinimum = new Size(MinWidth, MinHeight);
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        DataContext = viewModel;
        Loaded += WindowLoaded;
        Unloaded += WindowUnloaded;
        ContentRendered += WindowContentRendered;
        Closed += WindowClosed;
        LocationChanged += WindowLocationChanged;
    }

    /// <summary>この窓の表示用モデルです。</summary>
    internal PlaylistLampViewerViewModel ViewModel => viewModel;

    /// <summary>両方のフォルダ名列が共有する計測幅です。</summary>
    public double FolderLabelColumnWidth
    {
        get => (double)GetValue(FolderLabelColumnWidthProperty);
        private set => SetValue(FolderLabelColumnWidthProperty, value);
    }

    /// <summary>両方のフォルダ件数列が共有する計測幅です。</summary>
    public double FolderCountColumnWidth
    {
        get => (double)GetValue(FolderCountColumnWidthProperty);
        private set => SetValue(FolderCountColumnWidthProperty, value);
    }

    /// <summary>区間ラベルのBindingと独自文字計測で共有する現在のピクセル密度です。</summary>
    public static readonly DependencyProperty PixelsPerDipProperty = DependencyProperty.Register(
        nameof(PixelsPerDip), typeof(double), typeof(PlaylistLampViewerWindow), new PropertyMetadata(1d));

    /// <summary>DPIだけが変化した場合にもラベルの収容判定を更新する現在値です。</summary>
    public double PixelsPerDip
    {
        get => (double)GetValue(PixelsPerDipProperty);
        private set => SetValue(PixelsPerDipProperty, value);
    }

    /// <summary>初回結果の確定後、Showの直前にOwnerの現在位置と実DPIで外形を配置します。</summary>
    internal void PrepareInitialPlacement()
        => PlaylistLampWindowPlacement.Prepare(this, desiredSize, normalMinimum);

    /// <summary>WPFの標準DPI処理を保ち、独自の文字計測だけを現在の密度で更新します。</summary>
    /// <param name="oldDpi">変更前のDPI。</param>
    /// <param name="newDpi">変更後のDPI。</param>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (isClosed) { return; }
        PixelsPerDip = newDpi.PixelsPerDip;
        if (IsLoaded) { PlaylistLampWindowPlacement.UpdateMinimum(this, normalMinimum); }
        QueueFolderColumnWidthUpdate();
    }

    private void WindowLocationChanged(object sender, EventArgs e)
    {
        // 同じDPIの別作業領域への移動も対象にします。列走査や初期位置への復帰は行いません。
        if (!isClosed && IsLoaded) { PlaylistLampWindowPlacement.UpdateMinimum(this, normalMinimum); }
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
        PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
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

        double pixelsPerDip = PixelsPerDip;
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
            pixelsPerDip);
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
/// 正の区間に、幅に収まる割合または曲数だけを表示します。
/// 狭い区間の詳細はツールチップとAutomation名に残します。
/// </summary>
public sealed class PlaylistLampSegmentLabelConverter : IMultiValueConverter
{
    /// <summary>選択モードのラベルを、全体が切れずに収まる場合だけ返します。</summary>
    /// <param name="values">区間、配置幅、ボタン、窓の曲数表示状態、現在のPixelsPerDip。</param>
    /// <param name="targetType">Binding先の型。</param>
    /// <param name="parameter">使用しないBinding引数。</param>
    /// <param name="culture">Bindingのカルチャ。</param>
    /// <returns>完全な割合または曲数。狭い区間は空文字列。</returns>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not { Length: >= 5 }
            || values[0] is not PlaylistLampViewerSegmentViewModel segment
            || !segment.HasPositiveWidth
            || values[1] is not double width
            || width <= 0d
            || values[2] is not Button button
            || values[4] is not double pixelsPerDip
            || !double.IsFinite(pixelsPerDip) || pixelsPerDip <= 0d)
        {
            return string.Empty;
        }

        CultureInfo displayCulture = culture ?? CultureInfo.CurrentCulture;
        string label = values[3] is true
            ? segment.Count.ToString("N0", CultureInfo.CurrentCulture)
            : segment.PercentageText;
        double textWidth = new FormattedText(
            label,
            displayCulture,
            FlowDirection.LeftToRight,
            new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch),
            double.IsNaN(button.FontSize) || button.FontSize <= 0d ? 12d : button.FontSize,
            Brushes.Black,
            pixelsPerDip).WidthIncludingTrailingWhitespace;

        // 枠線と余白も含めて収まる場合だけ表示し、狭い区間の文字切れを防ぎます。
        return width >= textWidth + 8d ? label : string.Empty;
    }

    /// <summary>逆変換は対応しません。</summary>
    /// <param name="value">使用しない変換先の値。</param>
    /// <param name="targetTypes">使用しない変換元の型。</param>
    /// <param name="parameter">使用しないBinding引数。</param>
    /// <param name="culture">使用しないBindingのカルチャ。</param>
    /// <returns>一方向の表示変換のため常に例外を返します。</returns>
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 正の重みを横方向に配置します。外部尺度が正なら共通尺度、0なら自己正規化を使います。
/// 最後の区間で行の占有幅までの端数を調整し、外部尺度で生じた余白は埋めません。
/// </summary>
public sealed class PlaylistLampWeightedStackPanel : Panel
{
    /// <summary>Attached weight used by the panel. Viewer segments bind this to chart count.</summary>
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight",
        typeof(double),
        typeof(PlaylistLampWeightedStackPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    /// <summary>正の値で共通尺度を指定し、0で子の重みの合計へ自己正規化します。</summary>
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(
        nameof(Scale),
        typeof(double),
        typeof(PlaylistLampWeightedStackPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>曲数を幅に変換する外部尺度。全体バーと割合表示は0を使います。</summary>
    public double Scale
    {
        get => (double)GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

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

        double denominator = Scale > 0d ? Scale : totalWeight;
        double occupiedWidth = Math.Max(0d, finalSize.Width * totalWeight / denominator);
        double remaining = occupiedWidth;
        double x = 0d;
        for (int index = 0; index < children.Length; index++)
        {
            UIElement child = children[index];
            double width = index == children.Length - 1
                ? remaining
                : Math.Max(0d, finalSize.Width * GetWeight(child) / denominator);
            child.Arrange(new Rect(x, 0d, width, Math.Max(0d, finalSize.Height)));
            x += width;
            remaining = Math.Max(0d, occupiedWidth - x);
        }

        foreach (UIElement child in InternalChildren.Cast<UIElement>()
            .Where(child => child.Visibility == Visibility.Collapsed || GetWeight(child) <= 0d))
        {
            child.Arrange(new Rect(0d, 0d, 0d, Math.Max(0d, finalSize.Height)));
        }
        return finalSize;
    }
}
