using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryWatch
{
    /// <summary>只负责接收托盘回调消息的隐藏窗口。</summary>
    sealed class MessageWindow : Form
    {
        public event Func<Message, bool> MessageReceived;

        public MessageWindow()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(1, 1);
            Location = new Point(-32000, -32000);
            SetStyle(ControlStyles.Opaque, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void WndProc(ref Message m)
        {
            Func<Message, bool> handler = MessageReceived;
            if (handler != null)
            {
                try { if (handler(m)) return; }
                catch { }
            }
            base.WndProc(ref m);
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        const int POLL_MS = 5 * 60 * 1000;
        const int HOVER_DELAY_MS = 500;
        const int LOW_THRESHOLD = 20;
        const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RUN_NAME = "BatteryWatch";

        readonly MessageWindow _host;
        readonly NativeTray _tray;
        readonly TooltipWindow _tip;
        readonly System.Windows.Forms.Timer _pollTimer;
        readonly System.Windows.Forms.Timer _hoverTimer;
        readonly ContextMenuStrip _menu;
        readonly List<IBatteryReader> _readers;
        readonly BatteryInfo[] _last;
        readonly bool[] _wasLow;

        string _lastSig = "";
        int _busy;
        bool _tipArmed;

        public TrayApp()
        {
            _readers = new List<IBatteryReader>();
            _readers.Add(new CherryReader());
            _readers.Add(new PeltaReader());
            _readers.Add(new LogitechReader());

            _last = new BatteryInfo[3];
            _wasLow = new bool[3];
            for (int i = 0; i < 3; i++) _last[i] = BatteryInfo.Unknown("", "");

            _host = new MessageWindow();
            _host.MessageReceived += OnHostMessage;
            IntPtr force = _host.Handle;

            _tip = new TooltipWindow();
            _tip.SetData(_last);

            _menu = BuildMenu();

            _tray = new NativeTray(_host, 1);
            _tray.PopupOpen += OnPopupOpen;
            _tray.PopupClose += OnPopupClose;
            _tray.LeftClick += OnLeftClick;
            _tray.RightClick += OnRightClick;

            _hoverTimer = new System.Windows.Forms.Timer();
            _hoverTimer.Interval = HOVER_DELAY_MS;
            _hoverTimer.Tick += delegate
            {
                _hoverTimer.Stop();
                if (_tipArmed) ShowTip();
            };

            _pollTimer = new System.Windows.Forms.Timer();
            _pollTimer.Interval = POLL_MS;
            _pollTimer.Tick += delegate { RefreshAll(); };

            UpdateTray();
            _pollTimer.Start();

            RefreshAll();
        }

        // ---------------- 托盘消息 ----------------
        bool OnHostMessage(Message m)
        {
            if (_tray.HandleMessage(ref m)) return true;

            if (_tray.IsShellRestart(m.Msg))
            {
                Bitmap bmp = IconRenderer.Render(_last);
                IntPtr h = bmp.GetHicon();
                Icon icon = Icon.FromHandle(h);
                _tray.ReAdd(icon, BuildTip());
                bmp.Dispose();
                _lastSig = "";
                return true;
            }
            return false;
        }

        void OnPopupOpen()
        {
            _tipArmed = true;
            _hoverTimer.Stop();
            _hoverTimer.Start();
        }

        void OnPopupClose()
        {
            _tipArmed = false;
            _hoverTimer.Stop();
            _tip.HideTip();
        }

        void OnLeftClick()
        {
            _hoverTimer.Stop();
            RefreshAll();
            ShowTip();
        }

        void OnRightClick()
        {
            _tipArmed = false;
            _hoverTimer.Stop();
            _tip.HideTip();
            _menu.Show(Cursor.Position);
        }

        void ShowTip()
        {
            _tip.SetData(_last);
            _tip.ShowNear(Cursor.Position);
        }

        // ---------------- 菜单 ----------------
        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.RenderMode = ToolStripRenderMode.System;

            menu.Items.Add("刷新电量", null, delegate { RefreshAll(); });

            var auto = new ToolStripMenuItem("开机自启动");
            auto.Checked = IsAutoStartEnabled();
            auto.Click += delegate
            {
                SetAutoStart(!auto.Checked);
                auto.Checked = IsAutoStartEnabled();
            };
            menu.Items.Add(auto);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApp(); });
            return menu;
        }

        // ---------------- 数据刷新 ----------------
        void RefreshAll()
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;

            Task.Run(delegate
            {
                try
                {
                    for (int i = 0; i < _readers.Count; i++)
                    {
                        BatteryInfo info;
                        try
                        {
                            info = _readers[i].Read();
                        }
                        catch (Exception ex)
                        {
                            info = BatteryInfo.Unknown(_readers[i].Name, ex.Message);
                        }

                        // 「充电中但没有百分比」也是有效状态，必须写进 _last，
                        // 否则会被当成读取失败而落到离线分支。
                        bool valid = info.Percent >= 0 || info.Charging;
                        if (valid)
                        {
                            _last[i] = info;
                        }
                        else if (_last[i].Percent >= 0 || _last[i].Charging)
                        {
                            _last[i].Connected = false;
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                }

                try
                {
                    _host.BeginInvoke((MethodInvoker)delegate
                    {
                        UpdateTray();
                        CheckLowBattery();
                        if (_tip.Visible2) _tip.ShowNear(Cursor.Position);
                    });
                }
                catch { }
            });
        }

        string BuildTip()
        {
            string tip = "";
            for (int i = 0; i < 3; i++)
            {
                string label = (i == 0) ? "键盘" : (i == 1 ? "耳机" : "鼠标");
                string val;
                if (_last[i] != null && _last[i].Percent >= 0) val = _last[i].Percent + "%";
                else if (_last[i] != null && _last[i].Charging) val = "充电中";
                else val = "--";
                tip += label + " " + val;
                if (i < 2) tip += "\n";
            }
            return tip;
        }

        void UpdateTray()
        {
            string sig = "";
            for (int i = 0; i < 3; i++)
            {
                BatteryInfo b = _last[i];
                sig += (b != null ? b.Percent : -1).ToString();
                sig += (b != null && b.Charging) ? "C" : "D";
                sig += ";";
            }

            string tip = BuildTip();

            if (sig == _lastSig)
            {
                _tray.Update(null, tip);
                return;
            }
            _lastSig = sig;

            Bitmap bmp = IconRenderer.Render(_last);
            IntPtr h = bmp.GetHicon();
            Icon icon = Icon.FromHandle(h);
            _tray.Update(icon, tip);
            bmp.Dispose();
        }

        void CheckLowBattery()
        {
            for (int i = 0; i < 3; i++)
            {
                BatteryInfo info = _last[i];
                if (info == null || info.Percent < 0) continue;

                bool low = info.Percent <= LOW_THRESHOLD && !info.Charging;
                if (low && !_wasLow[i])
                {
                    string label = (i == 0) ? "键盘" : (i == 1 ? "耳机" : "鼠标");
                    _tray.ShowBalloon("电量偏低",
                        label + " 剩余 " + info.Percent + "%，该充电了。");
                }
                _wasLow[i] = low;
            }
        }

        // ---------------- 自启动 ----------------
        static bool IsAutoStartEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
            {
                if (k == null) return false;
                return k.GetValue(RUN_NAME) != null;
            }
        }

        static void SetAutoStart(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
            {
                if (k == null) return;
                if (on) k.SetValue(RUN_NAME, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RUN_NAME, false);
            }
        }

        void ExitApp()
        {
            _pollTimer.Stop();
            _hoverTimer.Stop();
            _tip.HideTip();
            _tray.Dispose();
            _tip.Dispose();
            _host.Dispose();
            _menu.Dispose();
            ExitThread();
        }
    }
}
