using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BatteryWatch
{
    /// <summary>HID 设备封装：枚举、打开、带超时的 overlapped 读写。</summary>
    public sealed class HidDevice : IDisposable
    {
        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_RW = 0x03;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        const int ERROR_IO_PENDING = 997;
        const int ERROR_OPERATION_ABORTED = 995;
        const int WAIT_OBJECT_0 = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint Offset;
            public uint OffsetHigh;
            public IntPtr hEvent;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, ref HIDP_CAPS caps);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int f);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr d, IntPtr dd, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr d, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, ref int req, IntPtr dd);
        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr d);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(IntPtr h, byte[] buf, int len, out int written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(IntPtr h, byte[] buf, int len, out int read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateEvent(IntPtr sec, bool manual, bool init, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ResetEvent(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetOverlappedResult(IntPtr h, IntPtr ov, out int n, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CancelIoEx(IntPtr h, IntPtr ov);

        readonly IntPtr _handle;
        readonly IntPtr _writeEvent;
        readonly IntPtr _readEvent;
        bool _disposed;

        public string Path { get; private set; }
        public ushort VendorId { get; private set; }
        public ushort ProductId { get; private set; }
        public ushort UsagePage { get; private set; }
        public ushort Usage { get; private set; }
        public int InputReportLength { get; private set; }
        public int OutputReportLength { get; private set; }

        HidDevice(IntPtr handle, string path)
        {
            _handle = handle;
            Path = path;
            _writeEvent = CreateEvent(IntPtr.Zero, true, false, null);
            _readEvent = CreateEvent(IntPtr.Zero, true, false, null);
        }

        /// <summary>枚举所有 HID 接口，按 vendorId 过滤。</summary>
        public static System.Collections.Generic.List<HidDevice> Enumerate(ushort vendorId)
        {
            var result = new System.Collections.Generic.List<HidDevice>();
            Guid guid;
            HidD_GetHidGuid(out guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

            var did = new SP_DEVICE_INTERFACE_DATA();
            did.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
            int index = 0;

            while (true)
            {
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref did)) break;
                index++;

                int need = 0;
                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref need, IntPtr.Zero);
                if (need <= 0) continue;

                IntPtr buf = Marshal.AllocHGlobal(need);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    int need2 = need;
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref did, buf, need, ref need2, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringAuto(new IntPtr(buf.ToInt64() + 4));
                    if (path == null) continue;

                    var dev = Open(path);
                    if (dev == null) continue;
                    if (dev.VendorId != vendorId) { dev.Dispose(); continue; }
                    result.Add(dev);
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }

            SetupDiDestroyDeviceInfoList(set);
            return result;
        }

        public static HidDevice Open(string path)
        {
            uint access = GENERIC_READ | GENERIC_WRITE;
            IntPtr h = CreateFile(path, access, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h == new IntPtr(-1))
            {
                h = CreateFile(path, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (h == new IntPtr(-1)) return null;
            }

            var dev = new HidDevice(h, path);

            var attr = new HIDD_ATTRIBUTES();
            attr.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
            if (!HidD_GetAttributes(h, ref attr)) { dev.Dispose(); return null; }
            dev.VendorId = attr.VendorID;
            dev.ProductId = attr.ProductID;

            IntPtr pp;
            if (HidD_GetPreparsedData(h, out pp))
            {
                var caps = new HIDP_CAPS();
                if (HidP_GetCaps(pp, ref caps) == 0x00110000)
                {
                    dev.UsagePage = caps.UsagePage;
                    dev.Usage = caps.Usage;
                    dev.InputReportLength = caps.InputReportByteLength;
                    dev.OutputReportLength = caps.OutputReportByteLength;
                }
                HidD_FreePreparsedData(pp);
            }

            return dev;
        }

        /// <summary>写入一个 output report（含 report id）。</summary>
        public bool Write(byte[] buffer, int timeoutMs)
        {
            ResetEvent(_writeEvent);
            int sz = Marshal.SizeOf(typeof(OVERLAPPED));
            IntPtr ov = AllocOverlapped(_writeEvent);
            try
            {
                int written;
                bool ok = WriteFile(_handle, buffer, buffer.Length, out written, ov);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING) return false;
                }
                if (WaitForSingleObject(_writeEvent, (uint)timeoutMs) != WAIT_OBJECT_0)
                {
                    CancelAndWait(ov, _writeEvent);
                    return false;
                }
                int done;
                return GetOverlappedResult(_handle, ov, out done, false);
            }
            finally
            {
                FreeOverlapped(ov, true);
            }
        }

        /// <summary>读取一个 input report。超时返回 null。</summary>
        public byte[] Read(int timeoutMs)
        {
            int len = InputReportLength > 0 ? InputReportLength : 64;
            var buf = new byte[len];
            ResetEvent(_readEvent);
            IntPtr ov = AllocOverlapped(_readEvent);
            try
            {
                int got;
                bool ok = ReadFile(_handle, buf, len, out got, ov);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_OPERATION_ABORTED) return null;
                    if (err != ERROR_IO_PENDING) return null;
                }
                if (WaitForSingleObject(_readEvent, (uint)timeoutMs) != WAIT_OBJECT_0)
                {
                    CancelAndWait(ov, _readEvent);
                    return null;
                }
                int done;
                if (!GetOverlappedResult(_handle, ov, out done, false) || done <= 0) return null;
                var copy = new byte[done];
                Array.Copy(buf, copy, done);
                return copy;
            }
            finally
            {
                FreeOverlapped(ov, true);
            }
        }

        /// <summary>丢弃积压的 input report。</summary>
        public void Drain(int maxReports)
        {
            for (int i = 0; i < maxReports; i++)
            {
                if (Read(25) == null) break;
            }
        }

        IntPtr AllocOverlapped(IntPtr ev)
        {
            int sz = Marshal.SizeOf(typeof(OVERLAPPED));
            IntPtr ov = Marshal.AllocHGlobal(sz);
            for (int i = 0; i < sz; i++) Marshal.WriteByte(ov, i, 0);
            var o = new OVERLAPPED();
            o.hEvent = ev;
            Marshal.StructureToPtr(o, ov, false);
            return ov;
        }

        // CancelIoEx 是异步的：必须等取消真正完成再释放 OVERLAPPED，
        // 否则驱动仍会写入已释放内存，导致堆损坏（进程静默退出）。
        void CancelAndWait(IntPtr ov, IntPtr ev)
        {
            CancelIoEx(_handle, ov);
            WaitForSingleObject(ev, 200);
        }

        void FreeOverlapped(IntPtr ov, bool safe)
        {
            if (ov != IntPtr.Zero) Marshal.FreeHGlobal(ov);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle != IntPtr.Zero && _handle != new IntPtr(-1)) CloseHandleSafe(_handle);
            if (_writeEvent != IntPtr.Zero) CloseHandleSafe(_writeEvent);
            if (_readEvent != IntPtr.Zero) CloseHandleSafe(_readEvent);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);

        static void CloseHandleSafe(IntPtr h)
        {
            try { CloseHandle(h); } catch { }
        }
    }
}
