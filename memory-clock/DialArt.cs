using System;
using System.Windows;
using System.Windows.Media;

namespace MemoryClock
{
    // One vector face for the dashboard preview and the floating instrument.
    internal static class DialArt
    {
        private static readonly Pen Rail = PenFor(0x41, 0x57, 0x52, 5);
        private static readonly Pen Fill = PenFor(0xCC, 0xF5, 0x9E, 5);
        private static readonly Pen Tick = PenFor(0x8A, 0xA9, 0x9F, 1.25);
        private static Point Polar(double degrees, double radius)
        {
            double a = degrees * Math.PI / 180;
            return new Point(40 + radius * Math.Cos(a), 37 + radius * Math.Sin(a));
        }

        private static Pen PenFor(byte r, byte g, byte b, double width)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(r, g, b)), width)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze(); return pen;
        }

        private static Geometry Arc(double fraction)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(Polar(135, 32), false, false);
                ctx.ArcTo(Polar(135 + 270 * fraction, 32), new Size(32, 32), 0,
                    fraction > 2.0 / 3, SweepDirection.Clockwise, true, false);
            }
            geo.Freeze(); return geo;
        }

        internal static DrawingImage Draw(double? fraction)
        {
            var group = new DrawingGroup();
            using (var d = group.Open())
            {
                // Fixed optical bounds: the artwork must not shift with usage.
                d.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 80, 72));
                d.DrawGeometry(null, Rail, Arc(1));
                double value = Math.Min(1, Math.Max(0, fraction.GetValueOrDefault()));
                if (value > 0) d.DrawGeometry(null, Fill, Arc(value));
                for (int n = 0; n <= 8; n++)
                {
                    double a = 135 + n * 33.75;
                    d.DrawLine(Tick, Polar(a, 26), Polar(a, n % 2 == 0 ? 23 : 24.5));
                }
            }
            var image = new DrawingImage(group); image.Freeze(); return image;
        }
    }
}
