using System.Collections.Generic;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SchoolPdf.Services;

public static class InkCompositionService
{
    public static BitmapSource CompositePageWithInk(
        BitmapSource pageBitmap, IReadOnlyList<Stroke> strokes,
        double contentDipW, double contentDipH,
        double inkCoordinateW, double inkCoordinateH, int rotation)
    {
        int contentW = pageBitmap.PixelWidth, contentH = pageBitmap.PixelHeight;
        bool swap = rotation is 90 or 270;
        int outputW = swap ? contentH : contentW;
        int outputH = swap ? contentW : contentH;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (rotation != 0)
            {
                var group = new TransformGroup();
                group.Children.Add(new RotateTransform(rotation));
                var translate = rotation switch
                {
                    90 => new TranslateTransform(contentH, 0),
                    180 => new TranslateTransform(contentW, contentH),
                    270 => new TranslateTransform(0, contentW),
                    _ => new TranslateTransform(0, 0)
                };
                group.Children.Add(translate);
                dc.PushTransform(group);
            }

            dc.DrawImage(pageBitmap, new Rect(0, 0, contentW, contentH));
            if (strokes.Count > 0 && inkCoordinateW > 0 && inkCoordinateH > 0)
            {
                dc.PushTransform(new ScaleTransform(contentW / inkCoordinateW, contentH / inkCoordinateH));
                foreach (var stroke in strokes)
                    stroke.Draw(dc);
                dc.Pop();
            }
            if (rotation != 0) dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(outputW, outputH, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}