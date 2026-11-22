using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BeMusicSeeker.Tests;

/// <summary>
/// キーボード操作のフォーカス枠と入力状態の枠について、実部品が採用している契約を観測する表明を提供します。
/// フォーカスの取得や最後の入力機器には依存せず、部品の実効 <see cref="FrameworkElement.FocusVisualStyle"/>、
/// スタイル・テンプレートのトリガー、実体化した枠の画素だけを合否にします。
/// </summary>
internal static class FocusVisualAssert
{
    private const int OuterThickness = 2;
    private const int InnerThickness = 1;

    private static readonly DependencyProperty[] FocusProperties =
    [
        UIElement.IsKeyboardFocusedProperty,
        UIElement.IsKeyboardFocusWithinProperty,
        UIElement.IsFocusedProperty
    ];

    private static readonly string[] FocusPropertyNames =
    [
        nameof(UIElement.IsKeyboardFocused),
        nameof(UIElement.IsKeyboardFocusWithin),
        nameof(UIElement.IsFocused)
    ];

    /// <summary>
    /// 対象の実効 FocusVisualStyle を、対象と同じウィンドウの装飾層へ実体化して描画し、
    /// 部品の内側に収まる直角の二重線（外側2 DIPは <c>App.InputFocusBorderBrush</c>、内側1 DIPは
    /// <c>App.AccentFocusRingBrush</c>、それより内側は透明）であることを画素で確認します。
    /// </summary>
    /// <remarks>
    /// WPF が実際に枠を表示する装飾と同じく、ウィンドウの装飾層の子として描くため、動的資源は同じ経路で解決されます。
    /// Style のオブジェクト同一性は合否にせず、最後の入力機器を作れないため装飾の有無も確認しません。
    /// </remarks>
    public static void AssertKeyboardFocusRing(FrameworkElement target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string name = Describe(target);
        Style? style = target.FocusVisualStyle;
        Assert.IsNotNull(style, name + ": 実効の FocusVisualStyle がありません。");
        Assert.IsTrue(
            target.ActualWidth >= 8 && target.ActualHeight >= 8,
            name + ": 枠を描く寸法がありません。表示した状態で呼び出してください。");
        AdornerLayer layer = AdornerLayer.GetAdornerLayer(target)
            ?? throw new AssertFailedException(name + ": 装飾層がありません。");
        int width = (int)Math.Round(target.ActualWidth);
        int height = (int)Math.Round(target.ActualHeight);
        var adorner = new ProbeAdorner(target, style);
        layer.Add(adorner);
        try
        {
            layer.UpdateLayout();
            Control probe = adorner.Probe;
            Assert.IsNotNull(probe.Template, name + ": 枠のテンプレートが適用されていません。");

            Color outer = ResolveSolidColor(target, "App.InputFocusBorderBrush");
            Color inner = ResolveSolidColor(target, "App.AccentFocusRingBrush");
            Rect descendants = VisualTreeHelper.GetDescendantBounds(probe);
            Assert.IsFalse(descendants.IsEmpty, name + ": 枠が描かれていません。");
            Assert.IsTrue(
                descendants.Left >= -0.01 && descendants.Top >= -0.01
                && descendants.Right <= target.ActualWidth + 0.01 && descendants.Bottom <= target.ActualHeight + 0.01,
                name + ": 枠が部品の外へはみ出しています: " + descendants);

            int[] pixels = Render(probe, width, height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int ring = Math.Min(Math.Min(x, y), Math.Min(width - 1 - x, height - 1 - y));
                    uint argb = (uint)pixels[(y * width) + x];
                    string at = $"{name}: ({x},{y}) ring={ring} argb={argb:X8}";
                    if (ring < OuterThickness)
                    {
                        AssertColor(outer, argb, at + " 外側の線");
                    }
                    else if (ring < OuterThickness + InnerThickness)
                    {
                        AssertColor(inner, argb, at + " 内側の線");
                    }
                    else
                    {
                        Assert.AreEqual(0u, argb >> 24, at + " 線より内側は透明です。");
                    }
                }
            }
        }
        finally
        {
            layer.Remove(adorner);
        }
    }

    /// <summary>
    /// 対象のスタイル（BasedOn の連鎖を含む）とテンプレートが、フォーカスを条件とするトリガーを持たないことを確認します。
    /// 条件は <c>IsKeyboardFocused</c>、<c>IsKeyboardFocusWithin</c>、<c>IsFocused</c> の Trigger・MultiTrigger の条件と、
    /// 自身または TemplatedParent を参照する DataTrigger・MultiDataTrigger です。
    /// </summary>
    /// <param name="target">検査する実部品。</param>
    /// <param name="allowInputFocusWithin">
    /// 入力状態の枠を許可リストとして持つ部品で <c>IsKeyboardFocusWithin</c> の条件を許す場合は true。
    /// <c>IsKeyboardFocused</c> と <c>IsFocused</c> は常に許しません。
    /// </param>
    /// <remarks>
    /// MultiTrigger は、フォーカス以外の条件が対象の現在値と一致しない場合は成立しないため検査対象から外します。
    /// これにより、編集型だけが持つ入力状態の枠を、非編集型の実部品では持たないものとして確認できます。
    /// </remarks>
    public static void AssertNoFocusConditionedTriggers(Control target, bool allowInputFocusWithin = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        List<string> violations = [];
        foreach ((string owner, TriggerBase trigger) in EnumerateTriggers(target))
        {
            foreach (string focusCondition in FindFocusConditions(target, trigger))
            {
                if (allowInputFocusWithin && focusCondition == nameof(UIElement.IsKeyboardFocusWithin))
                {
                    continue;
                }

                violations.Add(owner + " の " + trigger.GetType().Name + " が " + focusCondition + " を条件にしています。");
            }
        }

        Assert.AreEqual(0, violations.Count, Describe(target) + ": " + string.Join(" / ", violations));
    }

    /// <summary>
    /// 入力欄が、キーボード用のフォーカス枠を持たず（FocusVisualStyle が null）、
    /// <c>IsKeyboardFocusWithin</c> を条件とする入力状態の枠のトリガーを持つことを確認します。
    /// </summary>
    public static void AssertInputFocusBorderRoute(Control target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Assert.IsNull(target.FocusVisualStyle, Describe(target) + ": 入力欄はキーボード用のフォーカス枠を持ちません。");
        bool hasInputBorder = EnumerateTriggers(target).Any(item => FindFocusConditions(target, item.Trigger)
            .Contains(nameof(UIElement.IsKeyboardFocusWithin)));
        Assert.IsTrue(hasInputBorder, Describe(target) + ": IsKeyboardFocusWithin を条件とする入力状態の枠がありません。");
    }

    private static IEnumerable<(string Owner, TriggerBase Trigger)> EnumerateTriggers(Control target)
    {
        var visited = new HashSet<Style>();
        Style? style = target.Style;
        while (style != null && visited.Add(style))
        {
            foreach (TriggerBase trigger in style.Triggers)
            {
                yield return ("Style", trigger);
            }

            style = style.BasedOn;
        }

        if (target.Template != null)
        {
            foreach (TriggerBase trigger in target.Template.Triggers)
            {
                yield return ("Template", trigger);
            }
        }
    }

    private static IEnumerable<string> FindFocusConditions(Control target, TriggerBase trigger)
    {
        switch (trigger)
        {
            case Trigger single:
                if (TryGetFocusName(single.Property, out string singleName))
                {
                    yield return singleName;
                }

                break;
            case MultiTrigger multi:
                if (!IsEstablishable(target, multi.Conditions))
                {
                    break;
                }

                foreach (Condition condition in multi.Conditions)
                {
                    if (TryGetFocusName(condition.Property, out string multiName))
                    {
                        yield return multiName;
                    }
                }

                break;
            case DataTrigger data:
                if (IsFocusBinding(data.Binding))
                {
                    yield return DescribeBinding(data.Binding);
                }

                break;
            case MultiDataTrigger multiData:
                foreach (Condition condition in multiData.Conditions)
                {
                    if (IsFocusBinding(condition.Binding))
                    {
                        yield return DescribeBinding(condition.Binding);
                    }
                }

                break;
        }
    }

    private static bool IsEstablishable(Control target, ConditionCollection conditions)
    {
        foreach (Condition condition in conditions)
        {
            if (condition.Property == null || TryGetFocusName(condition.Property, out _))
            {
                continue;
            }

            if (!Equals(target.GetValue(condition.Property), condition.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetFocusName(DependencyProperty? property, out string name)
    {
        int index = property == null ? -1 : Array.IndexOf(FocusProperties, property);
        name = index < 0 ? string.Empty : FocusPropertyNames[index];
        return index >= 0;
    }

    private static bool IsFocusBinding(BindingBase? binding)
    {
        if (binding is not Binding single || single.Path == null)
        {
            return false;
        }

        RelativeSourceMode? mode = single.RelativeSource?.Mode;
        if (mode != RelativeSourceMode.Self && mode != RelativeSourceMode.TemplatedParent)
        {
            return false;
        }

        return FocusPropertyNames.Any(name => single.Path.Path.Contains(name, StringComparison.Ordinal));
    }

    private static string DescribeBinding(BindingBase? binding)
    {
        return binding is Binding single ? "Binding(" + single.Path?.Path + ")" : "Binding";
    }

    private static Color ResolveSolidColor(FrameworkElement owner, string resourceKey)
    {
        object? resource = owner.TryFindResource(resourceKey);
        Assert.IsInstanceOfType<SolidColorBrush>(resource, resourceKey);
        return ((SolidColorBrush)resource).Color;
    }

    private static void AssertColor(Color expected, uint argb, string message)
    {
        Assert.AreEqual(255u, argb >> 24, message + " は不透明です。");
        Assert.AreEqual(expected.R, (argb >> 16) & 0xFF, message + " R");
        Assert.AreEqual(expected.G, (argb >> 8) & 0xFF, message + " G");
        Assert.AreEqual(expected.B, argb & 0xFF, message + " B");
    }

    private static int[] Render(Visual visual, int width, int height)
    {
        var rect = new Rect(0, 0, width, height);
        var brush = new VisualBrush(visual)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect((Point)VisualTreeHelper.GetOffset(visual), rect.Size),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = rect
        };
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
        {
            context.DrawRectangle(brush, null, rect);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        int[] pixels = new int[width * height];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static string Describe(FrameworkElement target)
    {
        return target.GetType().Name + (string.IsNullOrEmpty(target.Name) ? string.Empty : "#" + target.Name);
    }

    /// <summary>WPF の FocusVisualAdorner と同じく、Control に Style を適用した子を装飾層へ置く検査用の装飾です。</summary>
    private sealed class ProbeAdorner : Adorner
    {
        public ProbeAdorner(UIElement adorned, Style style)
            : base(adorned)
        {
            IsHitTestVisible = false;
            Probe = new Control { Style = style, IsTabStop = false, Focusable = false };
            // 部品の祖先だけが持つ資源辞書（部品単体のホスト等）も、同じ文脈で解決できるよう、遠い祖先から順に引き継ぐ。
            var ancestors = new List<FrameworkElement>();
            for (DependencyObject? current = adorned; current != null; current = LogicalTreeHelper.GetParent(current) ?? (current is Visual ? VisualTreeHelper.GetParent(current) : null))
            {
                if (current is FrameworkElement element && element.Resources.Count + element.Resources.MergedDictionaries.Count > 0)
                {
                    ancestors.Add(element);
                }
            }

            ancestors.Reverse();
            foreach (FrameworkElement ancestor in ancestors)
            {
                Probe.Resources.MergedDictionaries.Add(ancestor.Resources);
            }

            AddVisualChild(Probe);
            AddLogicalChild(Probe);
        }

        public Control Probe { get; }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index)
        {
            return index == 0 ? Probe : throw new ArgumentOutOfRangeException(nameof(index));
        }

        protected override Size MeasureOverride(Size constraint)
        {
            Probe.Measure(AdornedElement.RenderSize);
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Probe.Arrange(new Rect(default, finalSize));
            return finalSize;
        }
    }
}
