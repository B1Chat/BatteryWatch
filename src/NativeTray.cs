using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryWatch
{
    /// <summary>
    /// 原生 Shell_NotifyIcon 封装。WinForms 的 NotifyIcon 不暴露鼠标悬停事件，
    /// 而自定义 tooltip 需要 NIN_POPUPOPEN / NIN_POPUPCLOSE，所以必须走原生接口。
    /// </summary>
    sealed class NativeTray : IDisposable
    {
        const int NIM_ADD = 0x00;
        const int NIM_MODIFY = 0x01;
        const int NIM_DELETE = 0x02;
        const int NIM_SETVERSION = 0x04;

        const int NIF_MESSAGE = 0x01;
        const int NIF_ICON = 0x02;
        const int NIF_TIP = 0x04;
        const int NIF_INFO = 0x10;
        const int NIF_SHOWTIP = 0x80;

        const int NOTIFYICON_VERSION_4 = 4;

        // 回调消息标识
        const int NIN_SELECT = 0x0400;
        const int NIN_KEYSELECT = 0x0401;
        const int NIN_POPUPOPEN = 0x0406;
        const int NIN_POPUPCLOSE = 0x0407;

        const int WM_LBUTTONUP = 0x0202;
        const int WM_RBUTTONUP = 0x0205;
        const int WM_CONTEXTMENU = 0x007B;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        readonly int _cbSize;
        readonly int _uID;
        IntPtr _hWnd;
        IntPtr _iconHandle = IntPtr.Zero;
        readonly int _taskbarCreated;
        readonly int _callbackMessage;
        bool _added;

        public event Action PopupOpen;
        public event Action PopupClose;
        public event Action LeftClick;
        public event Action RightClick;

        public NativeTray(Form host, int id)
        {
            _hWnd = host.Handle;
            _uID = id;
            _callbackMessage = 0x0400 + 0x2100;      // WM_APP + 0x100 range, private
            _cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _added = false;
        }

        public int CallbackMessage { get { return _callbackMessage; } }

        public bool IsShellRestart(int msg)
        {
            return msg != 0 && msg == _taskbarCreated;
        }

        public void Add(Icon icon, string tip)
        {
            // 注意：不能带 NIF_SHOWTIP。NOTIFYICON_VERSION_4 本来会抑制系统标准
            // tooltip 交给应用自绘，带上 NIF_SHOWTIP 反而会把系统 tooltip 请回来，
            // 盖住我们的 NIN_POPUPOPEN 卡片。
            var data = BuildData(NIF_MESSAGE | NIF_ICON | NIF_TIP, icon, tip);
            _added = Shell_NotifyIcon(NIM_ADD, ref data);

            if (_added)
            {
                data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
                Shell_NotifyIcon(NIM_SETVERSION, ref data);
            }
        }

        public void Update(Icon icon, string tip)
        {
            if (!_added) { Add(icon, tip); return; }
            var data = BuildData(NIF_ICON | NIF_TIP, icon, tip);
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }

        NOTIFYICONDATA BuildData(int flags, Icon icon, string tip)
        {
            var data = new NOTIFYICONDATA();
            data.cbSize = _cbSize;
            data.hWnd = _hWnd;
            data.uID = _uID;
            data.uFlags = flags;
            data.uCallbackMessage = _callbackMessage;
            data.szTip = (tip != null && tip.Length > 127) ? tip.Substring(0, 127) : (tip ?? "");
            data.szInfo = "";
            data.szInfoTitle = "";
            data.guidItem = Guid.Empty;

            if ((flags & NIF_ICON) != 0 && icon != null)
            {
                if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
                _iconHandle = icon.Handle;
                data.hIcon = _iconHandle;
            }
            else if (_iconHandle != IntPtr.Zero)
            {
                data.hIcon = _iconHandle;
            }

            return data;
        }

        /// <summary>处理托盘回调消息。返回 true 表示消息已被消费。</summary>
        public bool HandleMessage(ref Message m)
        {
            if (m.Msg == _callbackMessage)
            {
                // NOTIFYICON_VERSION_4: lParam 低字 = 鼠标/通知消息, 高字 = 图标 ID
                int lp = m.LParam.ToInt32();
                int evt = lp & 0xFFFF;

                switch (evt)
                {
                    case NIN_POPUPOPEN:
                        if (PopupOpen != null) PopupOpen();
                        return true;
                    case NIN_POPUPCLOSE:
                        if (PopupClose != null) PopupClose();
                        return true;
                    case WM_LBUTTONUP:
                    case NIN_SELECT:
                    case NIN_KEYSELECT:
                        if (LeftClick != null) LeftClick();
                        return true;
                    case WM_RBUTTONUP:
                    case WM_CONTEXTMENU:
                        if (RightClick != null) RightClick();
                        return true;
                }
                return true;
            }

            if (IsShellRestart(m.Msg))
            {
                // Explorer 重启后图标会被清空，需要重新注册
                _added = false;
                return false;
            }

            return false;
        }

        public void ReAdd(Icon icon, string tip)
        {
            _added = false;
            Add(icon, tip);
        }

        /// <summary>低电提醒气泡（NIIF_WARNING）。</summary>
        public void ShowBalloon(string title, string text)
        {
            if (!_added) return;
            var data = BuildData(NIF_INFO, null, null);
            data.szInfoTitle = (title != null && title.Length > 63) ? title.Substring(0, 63) : (title ?? "");
            data.szInfo = (text != null && text.Length > 255) ? text.Substring(0, 255) : (text ?? "");
            data.dwInfoFlags = 0x02;   // NIIF_WARNING
            data.uTimeoutOrVersion = 6000;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }

        public void Dispose()
        {
            if (_added)
            {
                var data = new NOTIFYICONDATA();
                data.cbSize = _cbSize;
                data.hWnd = _hWnd;
                data.uID = _uID;
                Shell_NotifyIcon(NIM_DELETE, ref data);
                _added = false;
            }
            if (_iconHandle != IntPtr.Zero)
            {
                DestroyIcon(_iconHandle);
                _iconHandle = IntPtr.Zero;
            }
        }
    }
}
