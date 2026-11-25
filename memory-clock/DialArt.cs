using System;
using System.Windows;
using System.Windows.Media;

namespace MemoryClock
{
    internal enum LoadLevel { Normal, Elevated, High }

    // One vector face for the dashboard preview and the floating instrument.
    internal static class DialArt
    {
        private static readonly Brush Well = BrushFor(0x0C, 0x17, 0x15);
        private static readonly Pen Rail = PenFor(0x36, 0x53, 0x4C, 4.5);
        private static readonly Pen MajorTick = PenFor(0x7D, 0xA1, 0x98, 1.25);
        private static readonly Pen MinorTick = PenFor(0x4B, 0x69, 0x61, 0.95);

        private static readonly Brush NormalAccent = BrushFor(0xC8, 0xFA, 0x8E);
        private static readonly Brush NormalTip = BrushFor(0xF3, 0xFF, 0xE6);
        private static readonly Brush NormalMeter = BrushFor(0x11, 0x2B, 0x22);
        private static readonly Pen NormalFill = PenFor(0xC8, 0xFA, 0x8E, 4.5);
        private static readonly Pen NormalMajorLit = PenFor(0xA6, 0xD6, 0x8E, 1.25);
        private static readonly Pen NormalMinorLit = PenFor(0x6B, 0x94, 0x67, 0.95);

        private static readonly Brush ElevatedAccent = BrushFor(0xF4, 0xC8, 0x6A);
        private static readonly Brush ElevatedTip = BrushFor(0xFF, 0xF4, 0xD2);
        private static readonly Brush ElevatedMeter = BrushFor(0xA8, 0x64, 0x14);
        private static readonly Pen ElevatedFill = PenFor(0xF4, 0xC8, 0x6A, 4.5);
        private static readonly Pen ElevatedMajorLit = PenFor(0xD7, 0xAE, 0x5A, 1.25);
        private static readonly Pen ElevatedMinorLit = PenFor(0x8C, 0x72, 0x3C, 0.95);

        private static readonly Brush HighAccent = BrushFor(0xF2, 0x84, 0x72);
        private static readonly Brush HighTip = BrushFor(0xFF, 0xE4, 0xDF);
        private static readonly Brush HighMeter = BrushFor(0xB0, 0x36, 0x29);
        private static readonly Pen HighFill = PenFor(0xF2, 0x84, 0x72, 4.5);
        private static readonly Pen HighMajorLit = PenFor(0xD6, 0x76, 0x68, 1.25);
        private static readonly Pen HighMinorLit = PenFor(0x8A, 0x4E, 0x46, 0.95);

        private static readonly Point Center = new Point(40, 38);

        internal static LoadLevel LevelFor(double? fraction)
        {
            double value = fraction.GetValueOrDefault();
            return value >= 0.90 ? LoadLevel.High : value >= 0.80 ? LoadLevel.Elevated : LoadLevel.Normal;
        }

        internal static Brush AccentBrush(double? fraction)
        {
            LoadLevel level = LevelFor(fraction);
            return level == LoadLevel.High ? HighAccent : level == LoadLevel.Elevated ? ElevatedAccent : NormalAccent;
        }

        internal static Brush MeterBrush(double? fraction)
        {
            LoadLevel level = LevelFor(fraction);
            return level == LoadLevel.High ? HighMeter : level == LoadLevel.Elevated ? ElevatedMeter : NormalMeter;
        }

        private static Point Polar(double degrees, double radius)
        {
            double a = degrees * Math.PI / 180;
            return new Point(Center.X + radius * Math.Cos(a), Center.Y + radius * Math.Sin(a));
        }

        private static Brush BrushFor(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze(); return brush;
        }

        private static Pen PenFor(byte r, byte g, byte b, double width)
        {
            var pen = new Pen(BrushFor(r, g, b), width)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze(); return pen;
        }

        private static Geometry Arc(double fraction)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(Polar(135, 31.5), false, false);
                ctx.ArcTo(Polar(135 + 270 * fraction, 31.5), new Size(31.5, 31.5), 0,
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
                d.DrawEllipse(Well, null, Center, 21.2, 21.2);
                double value = Math.Min(1, Math.Max(0, fraction.GetValueOrDefault()));
                LoadLevel level = LevelFor(value);
                Pen fill = level == LoadLevel.High ? HighFill : level == LoadLevel.Elevated ? ElevatedFill : NormalFill;
                Pen majorLit = level == LoadLevel.High ? HighMajorLit : level == LoadLevel.Elevated ? ElevatedMajorLit : NormalMajorLit;
                Pen minorLit = level == LoadLevel.High ? HighMinorLit : level == LoadLevel.Elevated ? ElevatedMinorLit : NormalMinorLit;
                Brush tip = level == LoadLevel.High ? HighTip : level == LoadLevel.Elevated ? ElevatedTip : NormalTip;

                for (int n = 0; n <= 12; n++)
                {
                    double a = 135 + n * 22.5;
                    bool major = n % 3 == 0;
                    bool lit = value > 0 && n / 12.0 <= value + 0.01;
                    Pen pen = major ? (lit ? majorLit : MajorTick) : (lit ? minorLit : MinorTick);
                    d.DrawLine(pen, Polar(a, 26.8), Polar(a, major ? 24.0 : 25.0));
                }
                d.DrawGeometry(null, Rail, Arc(1));
                if (value > 0)
                {
                    d.DrawGeometry(null, fill, Arc(value));
                    if (value > 0.02) d.DrawEllipse(tip, null, Polar(135 + 270 * value, 31.5), 1.5, 1.5);
                }
            }
            var image = new DrawingImage(group); image.Freeze(); return image;
        }
    }
}
