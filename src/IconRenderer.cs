using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace BatteryWatch
{
    /// <summary>托盘图标渲染：三段圆角电量条，抗锯齿绘制，系统按 DPI 缩放。</summary>
    public static class IconRenderer
    {
        public const int Canvas = 32;

        public static Bitmap Render(BatteryInfo[] items)
        {
            var bmp = new Bitmap(Canvas, Canvas, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                const float barW = 6f;
                const float gap = 2f;
                const float totalW = 3 * barW + 2 * gap;   // 22
                const float x0 = (Canvas - totalW) / 2f;   // 5
                const float top = 6f;
                const float bottom = 26f;
                const float fullH = bottom - top;
                const float radius = 1.6f;

                for (int i = 0; i < 3; i++)
                {
                    float x = x0 + i * (barW + gap);

                    // track
                    using (var path = RoundedRect(new RectangleF(x, top, barW, fullH), radius))
                    using (var track = new SolidBrush(Color.FromArgb(38, 255, 255, 255)))
                        g.FillPath(track, path);

                    BatteryInfo item = (items != null && i < items.Length) ? items[i] : null;
                    int pct = (item != null) ? item.Percent : -1;
                    bool charging = item != null && item.Charging;

                    if (item == null || (pct < 0 && !charging))
                    {
                        // offline: hollow ring only, no fill
                        using (var path = RoundedRect(new RectangleF(x, top, barW, fullH), radius))
                        using (var pen = new Pen(Color.FromArgb(110, 150, 155, 160), 1f))
                            g.DrawPath(pen, path);
                        continue;
                    }

                    Color baseColor = ColorFor(item, pct);
                    // 充电中但拿不到百分比时，用一小段蓝色表示正在充电
                    float ratio = (pct >= 0) ? Math.Max(0, Math.Min(100, pct)) / 100f : 0.22f;
                    float fillH = fullH * ratio;
                    if (fillH < 1.5f) fillH = 1.5f;           // keep a visible nub
                    float fillTop = bottom - fillH;

                    RectangleF fillRect = new RectangleF(x, fillTop, barW, fillH);
                    float r = Math.Min(radius, fillH / 2f);

                    using (var path = RoundedRect(fillRect, r))
                    {
                        // subtle vertical gradient: brighter on top
                        Color light = Lighten(baseColor, 0.22f);
                        using (var brush = new LinearGradientBrush(
                                   new RectangleF(x, fillTop - 1, barW, fillH + 2),
                                   light, baseColor, LinearGradientMode.Vertical))
                        {
                            g.FillPath(brush, path);
                        }
                    }

                    // crisp outline for definition at small sizes
                    using (var path = RoundedRect(new RectangleF(x, top, barW, fullH), radius))
                    using (var pen = new Pen(Color.FromArgb(75, 255, 255, 255), 0.8f))
                        g.DrawPath(pen, path);
                }
            }
            return bmp;
        }

        static Color ColorFor(BatteryInfo item, int pct)
        {
            if (item.Charging) return Color.FromArgb(72, 152, 245);       // blue
            if (pct > 50) return Color.FromArgb(64, 200, 120);            // green
            if (pct > 20) return Color.FromArgb(232, 170, 60);            // amber
            return Color.FromArgb(224, 92, 92);                           // red
        }

        public static Color BarColor(BatteryInfo item, int pct)
        {
            if (item == null) return Color.FromArgb(120, 128, 132);
            if (item.Charging) return Color.FromArgb(72, 152, 245);
            if (pct < 0) return Color.FromArgb(120, 128, 132);
            return ColorFor(item, pct);
        }

        static Color Lighten(Color c, float amount)
        {
            int r = c.R + (int)((255 - c.R) * amount);
            int g = c.G + (int)((255 - c.G) * amount);
            int b = c.B + (int)((255 - c.B) * amount);
            return Color.FromArgb(c.A, Math.Min(255, r), Math.Min(255, g), Math.Min(255, b));
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0.1f)
            {
                path.AddRectangle(r);
                return path;
            }
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;

            path.AddArc(r.X, r.Y, d, d, 180f, 90f);
            path.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            path.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }
    }
}
