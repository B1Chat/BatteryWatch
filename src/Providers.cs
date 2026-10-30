using System;
using System.Collections.Generic;
using System.Threading;

namespace BatteryWatch
{
    public sealed class BatteryInfo
    {
        public string Name;
        public int Percent;          // -1 = 未知
        public bool Charging;
        public bool Connected;
        public string Detail;        // 调试/说明文本

        public static BatteryInfo Unknown(string name, string detail)
        {
            var b = new BatteryInfo();
            b.Name = name;
            b.Percent = -1;
            b.Detail = detail;
            return b;
        }

        public string Display()
        {
            if (Percent < 0) return "--";
            return Percent.ToString() + "%";
        }
    }

    public interface IBatteryReader
    {
        string Id { get; }
        string Name { get; }
        BatteryInfo Read();
    }

    // ==================================================================
    // ROG Pelta (0B05:1B84) — vendor channel FF00:0001, report id 0xCC
    //   send:    CC 12 07 00 00 + zeros
    //   receive: CC 12 07 00 00 | sleepMin | percent | threshold | voice
    // ==================================================================
    public sealed class PeltaReader : IBatteryReader
    {
        const ushort VID = 0x0B05;
        const ushort PID = 0x1B84;
        const ushort CMD_USAGE_PAGE = 0xFF00;

        public string Id { get { return "pelta"; } }
        public string Name { get { return "ROG 破风"; } }

        public BatteryInfo Read()
        {
            List<HidDevice> devices = HidDevice.Enumerate(VID);
            try
            {
                foreach (HidDevice dev in devices)
                {
                    if (dev.ProductId != PID) continue;
                    if (dev.UsagePage != CMD_USAGE_PAGE) continue;
                    if (dev.InputReportLength < 8 || dev.OutputReportLength < 8) continue;

                    BatteryInfo info = Query(dev);
                    if (info != null) return info;
                }
            }
            finally
            {
                foreach (HidDevice d in devices) d.Dispose();
            }
            return BatteryInfo.Unknown(Name, "未找到耳机接收器通道");
        }

        BatteryInfo Query(HidDevice dev)
        {
            dev.Drain(4);

            var frame = new byte[dev.OutputReportLength];
            frame[0] = 0xCC;
            frame[1] = 0x12;
            frame[2] = 0x07;

            if (!dev.Write(frame, 300)) return null;

            for (int i = 0; i < 4; i++)
            {
                byte[] resp = dev.Read(320);
                if (resp == null || resp.Length < 8) continue;
                if (resp[0] != 0xCC || resp[1] != 0x12 || resp[2] != 0x07) continue;

                var info = new BatteryInfo();
                info.Name = Name;
                info.Connected = true;
                info.Percent = resp[6];                       // 0xFE = unknown
                if (info.Percent == 0xFE || info.Percent > 100) info.Percent = -1;
                info.Detail = string.Format("sleep={0}min threshold={1}% voice=0x{2:X2}",
                                            resp[5], resp[7], resp[8]);
                return info;
            }
            return null;
        }
    }

    // ==================================================================
    // CHERRY PIXIU75 (046A:01E4) — vendor channel FF1C:0092, report id 4
    //   frame:  [0]=4 [1..2]=checksum LE [3]=payloadType [4..]=payload
    //           checksum = sum(bytes[3..63]) & 0xFFFF
    //   query:  payloadType 0x1A, payload [03,00,00,55]
    //   reply:  63B (no report id) -> data starts at index 7
    //           wire index 8 = battery %, 9 = usb powered
    // ==================================================================
    public sealed class CherryReader : IBatteryReader
    {
        const ushort VID = 0x046A;
        const ushort CMD_USAGE_PAGE = 0xFF1C;

        public string Id { get { return "cherry"; } }
        public string Name { get { return "Cherry PIXIU 75"; } }

        public BatteryInfo Read()
        {
            List<HidDevice> devices = HidDevice.Enumerate(VID);
            try
            {
                foreach (HidDevice dev in devices)
                {
                    if (dev.UsagePage != CMD_USAGE_PAGE) continue;
                    if (dev.InputReportLength < 10 || dev.OutputReportLength < 10) continue;

                    BatteryInfo info = Query(dev);
                    if (info != null) return info;
                }
            }
            finally
            {
                foreach (HidDevice d in devices) d.Dispose();
            }
            return BatteryInfo.Unknown(Name, "未找到键盘接收器通道");
        }

        BatteryInfo Query(HidDevice dev)
        {
            dev.Drain(4);

            int len = dev.OutputReportLength;
            var frame = new byte[len];
            frame[0] = 0x04;      // report id
            frame[3] = 0x1A;      // payload type: status / battery
            frame[4] = 0x03;
            frame[5] = 0x00;
            frame[6] = 0x00;
            frame[7] = 0x55;

            int sum = 0;
            for (int i = 3; i < len; i++) sum += frame[i];
            ushort cs = (ushort)(sum & 0xFFFF);
            frame[1] = (byte)(cs & 0xFF);
            frame[2] = (byte)((cs >> 8) & 0xFF);

            if (!dev.Write(frame, 300)) return null;

            for (int i = 0; i < 4; i++)
            {
                byte[] resp = dev.Read(320);
                if (resp == null || resp.Length < 10) continue;

                int off = (resp[0] == 0x04) ? 1 : 0;
                if (resp.Length < off + 8) continue;
                if (resp[off + 2] != 0x1A) continue;

                var info = new BatteryInfo();
                info.Name = Name;
                info.Connected = true;
                info.Percent = resp[off + 7];
                if (info.Percent > 100) info.Percent = -1;
                info.Charging = resp[off + 8] != 0;
                info.Detail = string.Format("usbPowered={0}", resp[off + 8]);
                return info;
            }
            return null;
        }
    }

    // ==================================================================
    // Logitech Lightspeed receiver (046D:C539 / C547) — HID++ 2.0
    //   long report FF00:0002, 20 bytes, report id 0x11, slots 1..6
    //   battery feature: 0x1001 BATTERY_VOLTAGE on GPW1 (C539)
    //                    0x1004 UNIFIED_BATTERY on PRO X SUPERLIGHT (C547)
    //   0x1001 func0 -> big-endian uint16 millivolts
    //   getFeature params are big-endian [hi, lo]
    //   frame[2]==0xFF -> transient BUSY (err 0x07/0x08), retry
    // ==================================================================
    public sealed class LogitechReader : IBatteryReader
    {
        const ushort VID = 0x046D;
        const ushort CMD_USAGE_PAGE = 0xFF00;
        const byte SW_BASE = 0x01;

        static readonly int[,] Curve = {
            {4186,100},{4067,90},{3989,80},{3922,70},{3859,60},{3811,50},
            {3778,40},{3751,30},{3717,20},{3671,10},{3646,5},{3579,2},{3500,0}
        };

        public string Id { get { return "logitech"; } }
        public string Name { get { return "罗技 G PRO"; } }

        public BatteryInfo Read()
        {
            List<HidDevice> devices = HidDevice.Enumerate(VID);
            try
            {
                HidDevice target = null;
                foreach (HidDevice dev in devices)
                {
                    if (dev.UsagePage != CMD_USAGE_PAGE) continue;
                    if (dev.InputReportLength != 20) continue;   // long report only
                    target = dev;
                    break;
                }
                if (target == null) return BatteryInfo.Unknown(Name, "未找到接收器长报文通道");

                for (byte slot = 1; slot <= 6; slot++)
                {
                    BatteryInfo info = Query(target, slot);
                    if (info != null) return info;
                }
                return BatteryInfo.Unknown(Name, "接收器在线，鼠标可能休眠");
            }
            finally
            {
                foreach (HidDevice d in devices) d.Dispose();
            }
        }

        BatteryInfo Query(HidDevice dev, byte slot)
        {
            byte featureIndex;
            string trace;
            if (!GetFeature(dev, slot, 0x1001, out featureIndex, out trace)) return null;
            if (featureIndex == 0) return null;

            byte[] resp = Request(dev, slot, featureIndex, 0x00, null, out trace);
            if (resp == null || resp.Length < 8) return null;

            int mv = (resp[4] << 8) | resp[5];
            if (mv < 3000 || mv > 4500) return null;

            // byte[6] bit7 是充电标志：未充电实测 0x00，充电中实测 0x80。
            // 充电时端电压被充电电流抬高（3.7V -> 3.9V+），按放电曲线换算会严重虚高
            // （实测真实 17% 会算成 67%），所以此时不报百分比，只报充电状态。
            bool charging = (resp[6] & 0x80) != 0;

            var info = new BatteryInfo();
            info.Name = Name;
            info.Connected = true;
            info.Charging = charging;
            info.Percent = charging ? -1 : VoltageToPercent(mv);
            info.Detail = charging
                ? string.Format("{0} mV, charging (SOC unavailable)", mv)
                : string.Format("{0} mV", mv);
            return info;
        }

        bool GetFeature(HidDevice dev, byte slot, ushort featureId, out byte index, out string trace)
        {
            index = 0;
            byte[] parms = new byte[3];
            parms[0] = (byte)(featureId >> 8);
            parms[1] = (byte)(featureId & 0xFF);
            byte[] resp = Request(dev, slot, 0x00, 0x00, parms, out trace);
            if (resp == null || resp.Length < 5) return false;
            index = resp[4];
            return true;
        }

        byte[] Request(HidDevice dev, byte slot, byte feature, byte func, byte[] parms, out string trace)
        {
            trace = "";
            for (int attempt = 0; attempt < 6; attempt++)
            {
                byte sw = (byte)(((SW_BASE + attempt) % 15) + 1);
                byte cmd = (byte)((func << 4) | (sw & 0x0F));

                dev.Drain(3);

                var frame = new byte[dev.OutputReportLength];
                frame[0] = 0x11;
                frame[1] = slot;
                frame[2] = feature;
                frame[3] = cmd;
                if (parms != null)
                {
                    int n = parms.Length;
                    if (n > frame.Length - 4) n = frame.Length - 4;
                    Array.Copy(parms, 0, frame, 4, n);
                }

                if (!dev.Write(frame, 250)) continue;

                for (int t = 0; t < 5; t++)
                {
                    byte[] resp = dev.Read(260);
                    if (resp == null || resp.Length < 7) continue;
                    if (resp[1] != slot) continue;

                    if (resp[2] == feature && resp[3] == cmd) return resp;

                    if (resp[2] == 0xFF || resp[2] == 0x8F)
                    {
                        if (resp[3] == feature && resp[4] == cmd)
                        {
                            int err = resp[5];
                            trace = string.Format("busy/err 0x{0:X2}", err);
                            if (err == 0x07 || err == 0x08) break;   // retry
                            return null;
                        }
                    }
                }
                Thread.Sleep(70);
            }
            return null;
        }

        static int VoltageToPercent(int mv)
        {
            if (mv >= Curve[0, 0]) return 100;
            int n = Curve.GetLength(0);
            for (int i = 0; i < n - 1; i++)
            {
                int v1 = Curve[i, 0], p1 = Curve[i, 1];
                int v2 = Curve[i + 1, 0], p2 = Curve[i + 1, 1];
                if (mv >= v2)
                {
                    double ratio = (double)(mv - v2) / (v1 - v2);
                    int p = (int)Math.Round(p2 + ratio * (p1 - p2));
                    if (p < 0) return 0;
                    if (p > 100) return 100;
                    return p;
                }
            }
            return 0;
        }
    }
}
