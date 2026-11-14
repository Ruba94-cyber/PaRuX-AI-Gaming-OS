using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PaRuX.Views;

/// <summary>
/// Left-to-right wrapping row where every child keeps its own natural size (unlike a fixed-cell wrap grid, which clips longer labels).
/// Children are arranged in insertion order, so keyboard and controller focus follow the visual reading order.
/// </summary>
internal sealed class WrapRowPanel : Panel
{
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        double maxWidth = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;
        double x = 0, rowHeight = 0, totalHeight = 0, usedWidth = 0;
        foreach (UIElement child in Children)
        {
            child.Measure(new Windows.Foundation.Size(maxWidth, double.PositiveInfinity));
            Windows.Foundation.Size size = child.DesiredSize;
            if (x > 0 && x + size.Width > maxWidth)
            {
                totalHeight += rowHeight;
                x = 0;
                rowHeight = 0;
            }
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
            usedWidth = Math.Max(usedWidth, x);
        }
        return new Windows.Foundation.Size(usedWidth, totalHeight + rowHeight);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (UIElement child in Children)
        {
            Windows.Foundation.Size size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += rowHeight;
                x = 0;
                rowHeight = 0;
            }
            child.Arrange(new Windows.Foundation.Rect(x, y, size.Width, size.Height));
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return finalSize;
    }
}
