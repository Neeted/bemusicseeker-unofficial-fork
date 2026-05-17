using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BeMusicSeeker.Views;

public class DragAdorner : Adorner
{
    private UIElement _ghost;

    protected Vector _move;

    private Point _position;

    public Point Position
    {
        get
        {
            return _position;
        }
        set
        {
            _position = value + _move;
            UpdatePosition();
        }
    }

    protected override int VisualChildrenCount => 1;

    public DragAdorner(UIElement element, double opacity, Point point)
        : base(element)
    {
        Rect descendantBounds = VisualTreeHelper.GetDescendantBounds(element);
        _ghost = new Rectangle
        {
            Height = descendantBounds.Height,
            Width = descendantBounds.Width,
            Fill = new VisualBrush(element)
            {
                Opacity = opacity
            }
        };
        _move = (Vector)Window.GetWindow(element).PointFromScreen(element.PointToScreen(point));
        _move.Negate();
        AdornerLayer.GetAdornerLayer((Visual)WPFUtil.FindVisualParent<Window>(base.AdornedElement).Content)?.Add(this);
    }

    public DragAdorner(UIElement element, IEnumerable<UIElement> elements, double opacity, Point point)
        : base(element)
    {
        _ghost = new StackPanel();
        foreach (UIElement element3 in elements)
        {
            try
            {
                Rect descendantBounds = VisualTreeHelper.GetDescendantBounds(element3);
                Rectangle element2 = new Rectangle
                {
                    Height = descendantBounds.Height,
                    Width = descendantBounds.Width,
                    Fill = new VisualBrush(element3)
                    {
                        Opacity = opacity
                    }
                };
                ((StackPanel)_ghost).Children.Add(element2);
            }
            catch
            {
            }
        }
        _move = (Vector)Window.GetWindow(element).PointFromScreen(element.PointToScreen(point));
        _move.Negate();
        AdornerLayer.GetAdornerLayer((Visual)WPFUtil.FindVisualParent<Window>(base.AdornedElement).Content)?.Add(this);
    }

    public DragAdorner(UIElement adornedElement, UIElement ghost, Point point)
        : base(adornedElement)
    {
        _ghost = ghost;
        _move = (Vector)Window.GetWindow(adornedElement).PointFromScreen(adornedElement.PointToScreen(point));
        _move.Negate();
        AdornerLayer.GetAdornerLayer((Visual)WPFUtil.FindVisualParent<Window>(base.AdornedElement).Content)?.Add(this);
    }

    public DragAdorner(UIElement adornedElement, UIElement ghost, Vector cursorOffset)
        : base(adornedElement)
    {
        _ghost = ghost;
        _move = (Vector)Window.GetWindow(adornedElement).PointFromScreen(adornedElement.PointToScreen(new Point()));
        _move.Negate();
        _move += cursorOffset;
        AdornerLayer.GetAdornerLayer((Visual)WPFUtil.FindVisualParent<Window>(base.AdornedElement).Content)?.Add(this);
    }

    public void Remove()
    {
        if (base.Parent is AdornerLayer adornerLayer)
        {
            adornerLayer.Remove(this);
        }
    }

    protected void UpdatePosition()
    {
        if (base.Parent is AdornerLayer adornerLayer)
        {
            adornerLayer.Update(base.AdornedElement);
        }
    }

    protected override Visual GetVisualChild(int index)
    {
        return _ghost;
    }

    protected override Size MeasureOverride(Size finalSize)
    {
        _ghost.Measure(finalSize);
        return _ghost.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _ghost.Arrange(new Rect(_ghost.DesiredSize));
        return finalSize;
    }

    public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
    {
        return new GeneralTransformGroup
        {
            Children =
            {
                base.GetDesiredTransform(transform),
                (GeneralTransform)new TranslateTransform(Position.X, Position.Y)
            }
        };
    }
}
