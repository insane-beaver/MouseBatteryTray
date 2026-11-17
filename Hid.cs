using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MouseBatteryTray
{
    class HidInfo
    {
        public string Path;
        public string Product;
        public ushort Vid, Pid, UsagePage, Usage, InLen, OutLen, FeatLen;
        public override string ToString() =>
            $"{Vid:X4}:{Pid:X4} \"{Product}\" page=0x{UsagePage:X4} usage=0x{Usage:X2} in={InLen} out={OutLen} feat={FeatLen}\r\n  {Path}";
    }

    class BatteryReading
    {
        public int Percent, Firmware;
        public bool Charging;
        public int MilliVolts;
        public HidInfo Device;
    }

    static class Hid
    {
        public const ushort CompxVid = 0x3554;

        public static List<HidInfo> Enumerate(ushort vid)
        {
            var list = new List<HidInfo>();
            HidD_GetHidGuid(out Guid guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return list;
            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
                {
                    string path = GetPath(set, ref ifData);
                    if (path == null || path.IndexOf($"vid_{vid:x4}", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var info = Describe(path);
                    if (info != null) list.Add(info);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        static string GetPath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData)
        {
            SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out int size, IntPtr.Zero);
            if (size == 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref ifData, buf, size, out _, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(buf + 4);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static HidInfo Describe(string path)
        {
            using (var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (!HidD_GetAttributes(h, ref attr)) return null;
                var name = new StringBuilder(128);
                string product = HidD_GetProductString(h, name, name.Capacity * 2) ? name.ToString() : "";
                if (!HidD_GetPreparsedData(h, out IntPtr pp)) return null;
                try
                {
                    if (HidP_GetCaps(pp, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS) return null;
                    return new HidInfo
                    {
                        Path = path, Product = product, Vid = attr.VendorID, Pid = attr.ProductID,
                        UsagePage = caps.UsagePage, Usage = caps.Usage,
                        InLen = caps.InputReportByteLength, OutLen = caps.OutputReportByteLength, FeatLen = caps.FeatureReportByteLength
                    };
                }
                finally { HidD_FreePreparsedData(pp); }
            }
        }

        // CompX vendor channel: usage page 0xFF02, 17-byte reports with report ID 8
        public static bool IsCompxConfig(HidInfo d) => d.UsagePage == 0xFF02 && d.InLen == 17 && d.OutLen == 17;

        public static BatteryReading ReadBattery(HidInfo dev, StringBuilder log = null)
        {
            var req = new byte[17];
            req[0] = 0x08;
            req[1] = 0x04;
            req[16] = Checksum(req);

            using (var h = CreateFile(dev.Path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                       IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero))
            {
                if (h.IsInvalid) { log?.AppendLine($"  open failed, err {Marshal.GetLastWin32Error()}"); return null; }
                if (!Io(h, req, true, 500, out _)) { log?.AppendLine($"  write failed, err {Marshal.GetLastWin32Error()}"); return null; }

                var deadline = Environment.TickCount + 1500;
                var buf = new byte[dev.InLen];
                while (true)
                {
                    int left = deadline - Environment.TickCount;
                    if (left <= 0) { log?.AppendLine("  timeout, no reply"); return null; }
                    if (!Io(h, buf, false, left, out int n) || n < 10) { Thread.Sleep(20); continue; }
                    log?.AppendLine("  rx: " + BitConverter.ToString(buf, 0, n));
                    if (buf[0] != 0x08 || buf[1] != 0x04) continue;

                    if (buf[2] != 0) { log?.AppendLine($"  status {buf[2]:X2} (mouse asleep/offline?)"); return null; }
                    if (Checksum(buf) != buf[16]) log?.AppendLine("  warning: checksum mismatch");
                    int pct = buf[6], mv = buf[8] << 8 | buf[9];
                    if (pct == 0 && mv == 0) { log?.AppendLine("  empty reply (mouse asleep?)"); return null; }
                    bool charging = buf[7] != 0;
                    // Same as the official hub: voltage curve wins over the firmware percent byte
                    int level = mv > 0 ? VoltToPercent(mv, charging) : Math.Min(pct, 100);
                    log?.AppendLine($"  firmware {pct}%, curve {level}%");
                    return new BatteryReading { Percent = level, Firmware = pct, Charging = charging, MilliVolts = mv, Device = dev };
                }
            }
        }

        static readonly Queue<string> pushLog = new Queue<string>();

        public static string PushLog() { lock (pushLog) return pushLog.Count == 0 ? "  (none yet)\r\n" : string.Concat(pushLog); }

        // Blocks reading unsolicited reports until the device goes away
        public static void Listen(HidInfo dev, Action onStatusChange)
        {
            using (var h = CreateFile(dev.Path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                       IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero))
            {
                if (h.IsInvalid) return;
                var buf = new byte[dev.InLen];
                while (Io(h, buf, false, Timeout.Infinite, out int n))
                {
                    if (n < 2 || buf[0] != 0x08 || buf[1] == 0x04) continue; // 04 = battery replies, ours or the hub's
                    lock (pushLog)
                    {
                        pushLog.Enqueue($"  {DateTime.Now:HH:mm:ss} {dev.Pid:X4}: {BitConverter.ToString(buf, 0, n)}\r\n");
                        while (pushLog.Count > 20) pushLog.Dequeue();
                    }
                    if (buf[1] == 0x03 || buf[1] == 0x0A) onStatusChange(); // DeviceOnLine / StatusChanged
                }
            }
        }

        // Voltage table from the official Attack Shark hub, 5% per step
        static readonly int[] Curve = { 3050, 3420, 3480, 3540, 3600, 3660, 3720, 3760, 3800, 3840, 3880, 3920, 3940, 3960, 3980, 4000, 4020, 4040, 4060, 4080, 4110 };

        public static int VoltToPercent(int mv, bool charging)
        {
            if (mv > Curve[Curve.Length - 1]) return charging ? 99 : 100;
            int i = Array.FindIndex(Curve, v => mv < v);
            if (i <= 0) return 0;
            double step = (Curve[i] - Curve[i - 1]) / 5.0;
            double n = (mv - Curve[i - 1]) / step + (i - 1) * 5;
            return (int)Math.Round(n, MidpointRounding.AwayFromZero);
        }

        static byte Checksum(byte[] b)
        {
            int sum = 0;
            for (int i = 0; i < 16; i++) sum += b[i];
            return (byte)(0x55 - sum);
        }

        static unsafe bool Io(SafeFileHandle h, byte[] data, bool write, int timeoutMs, out int transferred)
        {
            transferred = 0;
            using (var ev = new ManualResetEvent(false))
            {
                var ov = (NativeOverlapped*)Marshal.AllocHGlobal(sizeof(NativeOverlapped));
                var buf = Marshal.AllocHGlobal(data.Length);
                try
                {
                    *ov = new NativeOverlapped { EventHandle = ev.SafeWaitHandle.DangerousGetHandle() };
                    if (write) Marshal.Copy(data, 0, buf, data.Length);

                    bool ok = write ? WriteFile(h, buf, data.Length, IntPtr.Zero, ov) : ReadFile(h, buf, data.Length, IntPtr.Zero, ov);
                    if (!ok && Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;

                    if (!ev.WaitOne(timeoutMs))
                    {
                        CancelIoEx(h, ov);
                        GetOverlappedResult(h, ov, out _, true);
                        return false;
                    }
                    if (!GetOverlappedResult(h, ov, out transferred, false)) return false;
                    if (!write) Marshal.Copy(buf, data, 0, Math.Min(transferred, data.Length));
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                    Marshal.FreeHGlobal((IntPtr)ov);
                }
            }
        }

        #region Win32
        const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        const int ERROR_IO_PENDING = 997;
        const int HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)] public ushort[] Rest;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder buf, int bytes);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern unsafe bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, NativeOverlapped* ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern unsafe bool WriteFile(SafeFileHandle h, IntPtr buf, int len, IntPtr written, NativeOverlapped* ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern unsafe bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* ov, out int n, bool wait);
        [DllImport("kernel32.dll")]
        static extern unsafe bool CancelIoEx(SafeFileHandle h, NativeOverlapped* ov);
        #endregion
    }
}
