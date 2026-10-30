using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryWatch
{
    /// <summary>
    /// 仿 Win11 风格的深色圆角卡片。用分层窗口 + UpdateLayeredWindow 做逐像素
    /// alpha 合成，圆角边缘由 GDI+ 抗锯齿产生，不是硬裁剪的 Region。
    /// 不抢焦点、不在 Alt+Tab 出现。
    /// </summary>
    public sealed class TooltipWindow : Form
    {
        const int WS_EX_LAYERED = 0x00080000;
        const int SW_SHOWNOACTIVATE = 4;
        const int SW_HIDE = 0;
        const int ULW_ALPHA = 0x02;
        const byte AC_SRC_OVER = 0x00;
        const byte AC_SRC_ALPHA = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx; public int cy; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey,
            ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int cmd);

        readonly float _scale;

        const float BaseWidth = 300f;
        const float BasePadding = 16f;
        const float BaseTitleH = 18f;
        const float BaseRowH = 42f;
        const float BaseBarH = 7f;

        BatteryInfo[] _items;
        bool _visible;

        public TooltipWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            SetStyle(ControlStyles.Opaque, true);

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                _scale = g.DpiX / 96f;
            if (_scale < 1f) _scale = 1f;

            IntPtr force = Handle;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | 0x08000000 | 0x00000080;
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        public bool Visible2 { get { return _visible; } }

        public void SetData(BatteryInfo[] items)
        {
            _items = items;
        }

        /// <summary>按鼠标所在屏幕定位到任务栏上方，不遮挡任务栏。</summary>
        public bool ShowNear(Point anchor)
        {
            Bitmap bmp = RenderCard(_items, _scale);
            if (bmp == null) return false;

            Screen scr = Screen.FromPoint(anchor);
            Rectangle wa = scr.WorkingArea;

            int w = bmp.Width;
            int h = bmp.Height;
            int margin = (int)(8 * _scale);

            int x = wa.Right - w - margin;
            int y = wa.Bottom - h - margin;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;

            Apply(bmp, new Point(x, y));
            bmp.Dispose();

            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            _visible = true;
            return true;
        }

        public void HideTip()
        {
            if (!_visible) return;
            ShowWindow(Handle, SW_HIDE);
            _visible = false;
        }

        void Apply(Bitmap bmp, Point location)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(memDc, hBitmap);

            try
            {
                var size = new SIZE();
                size.cx = bmp.Width;
                size.cy = bmp.Height;
                var dst = new POINT();
                dst.X = location.X;
                dst.Y = location.Y;
                var src = new POINT();
                src.X = 0;
                src.Y = 0;
                var blend = new BLENDFUNCTION();
                blend.BlendOp = AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AC_SRC_ALPHA;

                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                SelectObject(memDc, old);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>渲染卡片位图。公开以便离线预览与视觉验证。</summary>
        public static Bitmap RenderCard(BatteryInfo[] items, float scale)
        {
            if (scale < 1f) scale = 1f;

            const int count = 3;
            float w = BaseWidth * scale;
            float pad = BasePadding * scale;
            float titleH = BaseTitleH * scale;
            float rowH = BaseRowH * scale;
            float barH = BaseBarH * scale;
            float h = pad + titleH + 6f * scale + count * rowH + pad - 4f * scale;

            var bmp = new Bitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                var card = new RectangleF(0.5f, 0.5f, bmp.Width - 1f, bmp.Height - 1f);

                using (var path = IconRenderer.RoundedRect(card, 8f * scale))
                {
                    using (var bg = new LinearGradientBrush(
                               new RectangleF(0, 0, bmp.Width, bmp.Height),
                               Color.FromArgb(246, 40, 41, 44),
                               Color.FromArgb(246, 29, 30, 32),
                               LinearGradientMode.Vertical))
                        g.FillPath(bg, path);

                    using (var pen = new Pen(Color.FromArgb(38, 255, 255, 255), 1f))
                        g.DrawPath(pen, path);
                }

                float titleSize = 9.5f * scale;
                float nameSize = 11.5f * scale;
                float pctSize = 13f * scale;
                float barX = pad;
                float barW = w - pad * 2f;

                using (var titleFont = new Font("Microsoft YaHei UI", titleSize, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var nameFont = new Font("Microsoft YaHei UI", nameSize, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var pctFont = new Font("Consolas", pctSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var dim = new SolidBrush(Color.FromArgb(150, 200, 204, 210)))
                {
                    g.DrawString("外设电量", titleFont, dim, pad, pad - 2f * scale);

                    float y = pad + titleH + 4f * scale;

                    for (int i = 0; i < count; i++)
                    {
                        BatteryInfo item = (items != null && i < items.Length) ? items[i] : null;
                        bool hasPct = item != null && item.Percent >= 0;
                        bool charging = item != null && item.Charging;
                        bool online = hasPct || charging;

                        string name = (item != null && !string.IsNullOrEmpty(item.Name))
                                      ? item.Name : DefaultName(i);
                        string pct = hasPct ? (item.Percent.ToString() + "%")
                                           : (charging ? "充电中" : "--");

                        using (var nb = new SolidBrush(online ? Color.FromArgb(240, 245, 246, 248)
                                                              : Color.FromArgb(125, 150, 155, 160)))
                            g.DrawString(name, nameFont, nb, barX, y);

                        Color pctColor = online ? IconRenderer.BarColor(item, item.Percent)
                                                : Color.FromArgb(115, 150, 155, 160);

                        using (var pb = new SolidBrush(pctColor))
                        {
                            SizeF sz = g.MeasureString(pct, pctFont);
                            g.DrawString(pct, pctFont, pb, barX + barW - sz.Width, y - 1f * scale);
                        }

                        if (!online)
                        {
                            using (var sb = new SolidBrush(Color.FromArgb(145, 160, 165, 172)))
                            {
                                SizeF ss = g.MeasureString("离线", titleFont);
                                SizeF ps = g.MeasureString(pct, pctFont);
                                g.DrawString("离线", titleFont, sb,
                                    barX + barW - ps.Width - ss.Width - 6f * scale, y + 2f * scale);
                            }
                        }

                        float barY = y + nameSize + 5f * scale;
                        var track = new RectangleF(barX, barY, barW, barH);
                        using (var tp = IconRenderer.RoundedRect(track, barH / 2f))
                        using (var tb = new SolidBrush(Color.FromArgb(44, 255, 255, 255)))
                            g.FillPath(tb, tp);

                        // 有百分比按比例；充电中拿不到百分比时画一小段蓝色示意
                        float ratio = hasPct ? Math.Max(0, Math.Min(100, item.Percent)) / 100f
                                             : (charging ? 0.22f : 0f);
                        if (ratio > 0)
                        {
                            float fw = barW * ratio;
                            if (fw < barH) fw = barH;
                            var fill = new RectangleF(barX, barY, fw, barH);
                            Color c = IconRenderer.BarColor(item, item.Percent);

                            using (var fp = IconRenderer.RoundedRect(fill, barH / 2f))
                            using (var fb = new LinearGradientBrush(
                                       new RectangleF(fill.X, fill.Y - 1, fill.Width, fill.Height + 2),
                                       Lighten(c, 0.25f), c, LinearGradientMode.Vertical))
                                g.FillPath(fb, fp);
                        }

                        y += rowH;
                    }
                }
            }
            return bmp;
        }

        static string DefaultName(int i)
        {
            if (i == 0) return "键盘";
            if (i == 1) return "耳机";
            return "鼠标";
        }

        static Color Lighten(Color c, float amount)
        {
            int r = c.R + (int)((255 - c.R) * amount);
            int gg = c.G + (int)((255 - c.G) * amount);
            int b = c.B + (int)((255 - c.B) * amount);
            return Color.FromArgb(c.A, Math.Min(255, r), Math.Min(255, gg), Math.Min(255, b));
        }
    }
}
