using System;
using System.ComponentModel;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using D2D = Vortice.Direct2D1;
using RawRectF = Vortice.RawRectF;

namespace BeMusicSeeker.Views;

// セル内のアイコンと並べ替えの矢印です。形・位置・色は、DIPの座標で決め、描くときだけ表示倍率の変換をかけます。
internal sealed partial class CustomTableSurface
{
    private Rect ToDip(PixelRect rect)
    {
        return new Rect(rect.Left / pixelsPerDip, rect.Top / pixelsPerDip, Math.Max(0d, rect.Width / pixelsPerDip), Math.Max(0d, rect.Height / pixelsPerDip));
    }

    private void BeginDipDrawing()
    {
        target.Transform = Matrix3x2.CreateScale((float)pixelsPerDip);
    }

    private void EndDipDrawing()
    {
        target.Transform = Matrix3x2.Identity;
    }

    private static D2D.ID2D1PathGeometry CreateGeometry(Action<D2D.ID2D1GeometrySink> build)
    {
        D2D.ID2D1PathGeometry geometry = CustomTableRenderResources.D2DFactory.CreatePathGeometry();
        using (D2D.ID2D1GeometrySink sink = geometry.Open())
        {
            build(sink);
            sink.Close();
        }
        return geometry;
    }

    private static Vector2 ToVector(double x, double y)
    {
        return new Vector2((float)x, (float)y);
    }

    private void FillTriangle(D2D.ID2D1Brush brush, Vector2 p1, Vector2 p2, Vector2 p3)
    {
        using D2D.ID2D1PathGeometry geometry = CreateGeometry(sink =>
        {
            sink.BeginFigure(p1, D2D.FigureBegin.Filled);
            sink.AddLine(p2);
            sink.AddLine(p3);
            sink.EndFigure(D2D.FigureEnd.Closed);
        });
        target.FillGeometry(geometry, brush);
    }

    private void DrawDownloadIcon(string text, PixelRect interior, Brush foreground)
    {
        Rect cellRect = ToDip(interior);
        if (string.IsNullOrEmpty(text) || cellRect.Width <= 8d || cellRect.Height <= 8d)
        {
            return;
        }
        D2D.ID2D1SolidColorBrush iconBrush = ResolveTextBrush(foreground);
        double size = Math.Max(8d, Math.Min(13d, Math.Min(cellRect.Width - 6d, cellRect.Height - 4d)));
        double centerX = cellRect.Left + cellRect.Width / 2d;
        double top = cellRect.Top + Math.Max(1d, (cellRect.Height - size) / 2d);
        double bottom = top + size;
        double trayY = bottom - 2d;
        using D2D.ID2D1PathGeometry arrow = CreateGeometry(sink =>
        {
            sink.BeginFigure(ToVector(centerX, top), D2D.FigureBegin.Hollow);
            sink.AddLine(ToVector(centerX, trayY - 3d));
            sink.EndFigure(D2D.FigureEnd.Open);
            sink.BeginFigure(ToVector(centerX - 4d, trayY - 7d), D2D.FigureBegin.Hollow);
            sink.AddLine(ToVector(centerX, trayY - 3d));
            sink.AddLine(ToVector(centerX + 4d, trayY - 7d));
            sink.EndFigure(D2D.FigureEnd.Open);
            sink.BeginFigure(ToVector(centerX - 6d, trayY), D2D.FigureBegin.Hollow);
            sink.AddLine(ToVector(centerX - 6d, bottom));
            sink.AddLine(ToVector(centerX + 6d, bottom));
            sink.AddLine(ToVector(centerX + 6d, trayY));
            sink.EndFigure(D2D.FigureEnd.Open);
        });
        BeginDipDrawing();
        target.DrawGeometry(arrow, iconBrush, 1.7f, CustomTableRenderResources.SquareCapStroke);
        EndDipDrawing();
    }

    private void DrawStatusIcon(string text, PixelRect interior, Brush foreground)
    {
        Rect cellRect = ToDip(interior);
        if (string.IsNullOrEmpty(text) || cellRect.Width <= 6d || cellRect.Height <= 6d)
        {
            return;
        }
        if (!Enum.TryParse(text, out CustomTableStatusIconKind iconKind) || iconKind == CustomTableStatusIconKind.None)
        {
            return;
        }
        D2D.ID2D1SolidColorBrush iconBrush = ResolveTextBrush(foreground);
        double size = Math.Max(7d, Math.Min(12d, Math.Min(cellRect.Width - 4d, cellRect.Height - 4d)));
        double left = cellRect.Left + (cellRect.Width - size) / 2d;
        double top = cellRect.Top + (cellRect.Height - size) / 2d;
        var iconRect = new Rect(left, top, size, size);
        BeginDipDrawing();
        try
        {
            switch (iconKind)
            {
                case CustomTableStatusIconKind.Forward:
                    DrawTrianglePair(iconBrush, iconRect, forward: true);
                    break;
                case CustomTableStatusIconKind.Backward:
                    DrawTrianglePair(iconBrush, iconRect, forward: false);
                    break;
                case CustomTableStatusIconKind.Play:
                    FillTriangle(iconBrush, ToVector(iconRect.Left + 2d, iconRect.Top + 1d), ToVector(iconRect.Left + 2d, iconRect.Bottom - 1d), ToVector(iconRect.Right - 1d, iconRect.Top + iconRect.Height / 2d));
                    break;
                case CustomTableStatusIconKind.Loading:
                    target.FillEllipse(new D2D.Ellipse(ToVector(iconRect.Left + 2d, iconRect.Top + iconRect.Height / 2d), 1.2f, 1.2f), iconBrush);
                    target.FillEllipse(new D2D.Ellipse(ToVector(iconRect.Left + iconRect.Width / 2d, iconRect.Top + iconRect.Height / 2d), 1.2f, 1.2f), iconBrush);
                    target.FillEllipse(new D2D.Ellipse(ToVector(iconRect.Right - 2d, iconRect.Top + iconRect.Height / 2d), 1.2f, 1.2f), iconBrush);
                    break;
                case CustomTableStatusIconKind.Pause:
                    target.FillRectangle(new RawRectF((float)(iconRect.Left + 2d), (float)(iconRect.Top + 1d), (float)(iconRect.Left + 4d), (float)(iconRect.Bottom - 1d)), iconBrush);
                    target.FillRectangle(new RawRectF((float)(iconRect.Right - 4d), (float)(iconRect.Top + 1d), (float)(iconRect.Right - 2d), (float)(iconRect.Bottom - 1d)), iconBrush);
                    break;
                case CustomTableStatusIconKind.Searching:
                    target.DrawEllipse(
                        new D2D.Ellipse(ToVector(iconRect.Left + iconRect.Width * 0.43d, iconRect.Top + iconRect.Height * 0.43d), (float)(iconRect.Width * 0.28d), (float)(iconRect.Height * 0.28d)),
                        iconBrush,
                        1.5f,
                        CustomTableRenderResources.RoundCapStroke);
                    target.DrawLine(
                        ToVector(iconRect.Left + iconRect.Width * 0.63d, iconRect.Top + iconRect.Height * 0.63d),
                        ToVector(iconRect.Right - 1d, iconRect.Bottom - 1d),
                        iconBrush,
                        1.5f,
                        CustomTableRenderResources.RoundCapStroke);
                    break;
                case CustomTableStatusIconKind.ScoreUnsent:
                    DrawRefreshIcon(iconBrush, iconRect);
                    break;
            }
        }
        finally
        {
            EndDipDrawing();
        }
    }

    /// <summary>
    /// チェックボックスを描きます。無効（<paramref name="enabled"/> がfalse）なら、背景を塗らず、枠とチェックを無効色で描きます。
    /// チェックの色は、行や現在セルの強調による文字色・列の前景色によらず、有効ならパレットのチェックの色です。
    /// </summary>
    private void DrawCheckBox(bool? isChecked, bool enabled, PixelRect interior)
    {
        Rect cellRect = ToDip(interior);
        if (!isChecked.HasValue || cellRect.Width <= 8d || cellRect.Height <= 8d)
        {
            return;
        }
        double size = Math.Max(8d, Math.Min(12d, Math.Min(cellRect.Width - 4d, cellRect.Height - 4d)));
        double left = cellRect.Left + (cellRect.Width - size) / 2d;
        double top = cellRect.Top + (cellRect.Height - size) / 2d;
        var rect = new Rect(left, top, size, size);
        var boxRect = new RawRectF((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom);
        BeginDipDrawing();
        try
        {
            if (enabled)
            {
                D2D.ID2D1SolidColorBrush background = display.GetBrush(palette.CheckBoxBackground);
                if (background != null)
                {
                    target.FillRectangle(boxRect, background);
                }
            }
            Pen borderPen = enabled ? palette.CheckBoxBorder : palette.CheckBoxDisabledBorder;
            D2D.ID2D1SolidColorBrush border = display.GetBrush(borderPen.Brush);
            if (border != null)
            {
                target.DrawRectangle(boxRect, border, (float)borderPen.Thickness);
            }
            if (isChecked.Value)
            {
                (D2D.ID2D1SolidColorBrush checkBrush, float thickness) = ResolveCheckMark(enabled);
                target.DrawLine(ToVector(rect.Left + 2.5d, rect.Top + size * 0.55d), ToVector(rect.Left + size * 0.43d, rect.Bottom - 2.5d), checkBrush, thickness);
                target.DrawLine(ToVector(rect.Left + size * 0.43d, rect.Bottom - 2.5d), ToVector(rect.Right - 2d, rect.Top + 2.5d), checkBrush, thickness);
            }
        }
        finally
        {
            EndDipDrawing();
        }
    }

    /// <summary>
    /// ラジオボタンを描きます。選択済みは円の輪郭と中央の塗りの円、未選択は円の輪郭だけで、nullは何も描きません。
    /// 大きさと色はチェックボックスと同じパレットで、無効なら背景を塗らず、枠と塗りを無効色で描きます。
    /// 中央の塗りは、行や現在セルの強調による文字色によらず、有効ならパレットのチェックの色です。
    /// </summary>
    private void DrawRadioButton(bool? isChecked, bool enabled, PixelRect interior)
    {
        Rect cellRect = ToDip(interior);
        if (!isChecked.HasValue || cellRect.Width <= 8d || cellRect.Height <= 8d)
        {
            return;
        }
        double size = Math.Max(8d, Math.Min(12d, Math.Min(cellRect.Width - 4d, cellRect.Height - 4d)));
        double centerX = cellRect.Left + cellRect.Width / 2d;
        double centerY = cellRect.Top + cellRect.Height / 2d;
        Pen borderPen = enabled ? palette.CheckBoxBorder : palette.CheckBoxDisabledBorder;
        // チェックボックスの枠は正方形の辺を線の中心にするため、同じ大きさに見えるよう、円の線の中心も外接の正方形の辺に置く。
        float radius = (float)(size / 2d);
        var circle = new D2D.Ellipse(ToVector(centerX, centerY), radius, radius);
        BeginDipDrawing();
        try
        {
            if (enabled)
            {
                D2D.ID2D1SolidColorBrush background = display.GetBrush(palette.CheckBoxBackground);
                if (background != null)
                {
                    target.FillEllipse(circle, background);
                }
            }
            D2D.ID2D1SolidColorBrush border = display.GetBrush(borderPen.Brush);
            if (border != null)
            {
                target.DrawEllipse(circle, border, (float)borderPen.Thickness);
            }
            if (isChecked.Value)
            {
                (D2D.ID2D1SolidColorBrush dotBrush, _) = ResolveCheckMark(enabled);
                float dotRadius = (float)(size * 0.25d);
                target.FillEllipse(new D2D.Ellipse(ToVector(centerX, centerY), dotRadius, dotRadius), dotBrush);
            }
        }
        finally
        {
            EndDipDrawing();
        }
    }

    /// <summary>チェックと、ラジオボタンの中央の塗りに使うブラシと線の太さを返します。有効ならパレットのチェックの色、無効なら無効色です。</summary>
    private (D2D.ID2D1SolidColorBrush Brush, float Thickness) ResolveCheckMark(bool enabled)
    {
        Pen pen = enabled ? palette.CheckBoxCheck : palette.CheckBoxDisabledCheck;
        D2D.ID2D1SolidColorBrush brush = display.GetBrush(pen.Brush) ?? ResolveTextBrush(null);
        return (brush, (float)pen.Thickness);
    }

    private void DrawRefreshIcon(D2D.ID2D1Brush brush, Rect rect)
    {
        Vector2 start = ToVector(rect.Right - 2d, rect.Top + rect.Height * 0.45d);
        Vector2 end = ToVector(rect.Left + 2d, rect.Top + rect.Height * 0.58d);
        using D2D.ID2D1PathGeometry arc = CreateGeometry(sink =>
        {
            sink.BeginFigure(start, D2D.FigureBegin.Hollow);
            sink.AddArc(new D2D.ArcSegment
            {
                Point = end,
                Size = new Vortice.Mathematics.Size((float)(rect.Width * 0.42d), (float)(rect.Height * 0.42d)),
                RotationAngle = 0f,
                SweepDirection = D2D.SweepDirection.CounterClockwise,
                ArcSize = D2D.ArcSize.Large
            });
            sink.EndFigure(D2D.FigureEnd.Open);
        });
        target.DrawGeometry(arc, brush, 1.5f, CustomTableRenderResources.RoundCapStroke);
        FillTriangle(brush, end, new Vector2(end.X + 4f, end.Y - 1f), new Vector2(end.X + 2f, end.Y + 3f));
    }

    private void DrawTrianglePair(D2D.ID2D1Brush brush, Rect rect, bool forward)
    {
        double mid = rect.Left + rect.Width / 2d;
        if (forward)
        {
            FillTriangle(brush, ToVector(rect.Left, rect.Top + 1d), ToVector(rect.Left, rect.Bottom - 1d), ToVector(mid, rect.Top + rect.Height / 2d));
            FillTriangle(brush, ToVector(mid - 1d, rect.Top + 1d), ToVector(mid - 1d, rect.Bottom - 1d), ToVector(rect.Right, rect.Top + rect.Height / 2d));
        }
        else
        {
            FillTriangle(brush, ToVector(rect.Right, rect.Top + 1d), ToVector(rect.Right, rect.Bottom - 1d), ToVector(mid, rect.Top + rect.Height / 2d));
            FillTriangle(brush, ToVector(mid + 1d, rect.Top + 1d), ToVector(mid + 1d, rect.Bottom - 1d), ToVector(rect.Left, rect.Top + rect.Height / 2d));
        }
    }

    private void DrawSortGlyph(CustomTableColumn column, PixelRect interior)
    {
        Rect cellRect = ToDip(interior);
        if (column == null
            || string.IsNullOrWhiteSpace(column.SortMemberPath)
            || !string.Equals(column.SortMemberPath, owner.SortColumnName, StringComparison.Ordinal)
            || !owner.SortDirection.HasValue
            || cellRect.Width < 8d
            || cellRect.Height < 8d)
        {
            return;
        }
        D2D.ID2D1SolidColorBrush glyphBrush = display.GetBrush(palette.SortGlyph);
        if (glyphBrush == null)
        {
            return;
        }
        double centerX = cellRect.Left + cellRect.Width / 2d;
        double top = cellRect.Top + 1d;
        double bottom = top + 3.5d;
        bool ascending = owner.SortDirection == ListSortDirection.Ascending;
        Vector2 p1 = ascending ? ToVector(centerX, top) : ToVector(centerX, bottom);
        Vector2 p2 = ascending ? ToVector(centerX - 3d, bottom) : ToVector(centerX - 3d, top);
        Vector2 p3 = ascending ? ToVector(centerX + 3d, bottom) : ToVector(centerX + 3d, top);
        BeginDipDrawing();
        try
        {
            FillTriangle(glyphBrush, p1, p2, p3);
        }
        finally
        {
            EndDipDrawing();
        }
    }
}
