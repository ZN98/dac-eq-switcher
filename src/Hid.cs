using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DacEqSwitcher
{
    /// A connection to the DAC's control interface.
    interface IDacLink : IDisposable
    {
        int InputLength { get; }
        /// Sends one 16-byte toppingctl frame (report id 0 + 15 bytes, as toppingctl's _wire()).
        void Write(byte[] frame);
        /// Blocks until a report arrives (returns its length) or `cancel` is set (returns 0).
        /// Never wakes more often than once per minIntervalMs: reports that arrive in
        /// between wait in the driver's buffer and are drained in one go.
        int Read(byte[] buffer, WaitHandle cancel, int minIntervalMs);
    }

    static class DacLink
    {
        /// Finds a connected Topping DAC. Returns null if none (or none toppingctl supports).
        public static IDacLink Open(out ModelInfo model, out string product, out string unsupported)
        {
            string sim = Environment.GetEnvironmentVariable("DACEQ_SIM");
            if (!string.IsNullOrEmpty(sim)) return SimLink.Open(sim, out model, out product, out unsupported);
            return HidLink.Open(out model, out product, out unsupported);
        }
    }

    sealed class HidLink : IDacLink
    {
        // ------------------------------------------------------------ native --
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps,
                NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetNumInputBuffers(SafeFileHandle h, int n);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int required, IntPtr info);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, IntPtr written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out int n, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);

        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, SHARE_RW = 3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        const int DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10, ERROR_IO_PENDING = 997;

        sealed class Candidate
        {
            public string Path, Product;
            public int Vid, Pid, UsagePage, InLen, OutLen;
        }

        static string Product(SafeFileHandle h)
        {
            var buf = new byte[256];
            if (!HidD_GetProductString(h, buf, buf.Length)) return null;
            return Encoding.Unicode.GetString(buf).Split('\0')[0];
        }

        static List<Candidate> Enumerate(int vid)
        {
            var list = new List<Candidate>();
            Guid g;
            HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
            string vidTag = "vid_" + vid.ToString("x4");
            try
            {
                var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
                {
                    int need;
                    SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(need);
                    string path;
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref d, detail, need, out need, IntPtr.Zero)) continue;
                        path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                    // Don't touch unrelated devices (keyboards, mice ...)
                    if (path == null || path.IndexOf(vidTag, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    using (var h = CreateFile(path, 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                    {
                        if (h.IsInvalid) continue;
                        var a = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)) };
                        if (!HidD_GetAttributes(h, ref a)) continue;
                        var c = new Candidate { Path = path, Vid = a.VendorID, Pid = a.ProductID };
                        c.Product = Product(h);
                        if (c.Product == null)
                            using (var h2 = CreateFile(path, GENERIC_READ | GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                                if (!h2.IsInvalid) c.Product = Product(h2);
                        IntPtr pp;
                        if (HidD_GetPreparsedData(h, out pp))
                        {
                            HIDP_CAPS caps;
                            HidP_GetCaps(pp, out caps);
                            HidD_FreePreparsedData(pp);
                            c.UsagePage = caps.UsagePage; c.InLen = caps.InputReportByteLength; c.OutLen = caps.OutputReportByteLength;
                        }
                        list.Add(c);
                    }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        public static IDacLink Open(out ModelInfo model, out string product, out string unsupported)
        {
            model = null; product = unsupported = null;
            var usable = new List<KeyValuePair<Candidate, ModelInfo>>();
            foreach (var c in Enumerate(ToppingTables.ThesyconVid))
            {
                if (c.OutLen < 16 || c.InLen < 16) continue;   // can't carry a protocol frame
                var m = ModelInfo.Find(c.Vid, c.Pid, c.Product);
                if (m == null) { unsupported = unsupported ?? c.Product; continue; }
                usable.Add(new KeyValuePair<Candidate, ModelInfo>(c, m));
            }
            if (usable.Count == 0) return null;
            // toppingctl: the control protocol lives on usage page 1, so try that interface first
            usable.Sort((a, b) => (a.Key.UsagePage == ToppingTables.ProtocolUsagePage ? 0 : 1)
                                  .CompareTo(b.Key.UsagePage == ToppingTables.ProtocolUsagePage ? 0 : 1));
            int err = 0;
            foreach (var u in usable)
            {
                var h = CreateFile(u.Key.Path, GENERIC_READ | GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (h.IsInvalid) { err = Marshal.GetLastWin32Error(); h.Dispose(); continue; }
                model = u.Value;
                product = u.Key.Product;
                HidD_SetNumInputBuffers(h, 512);   // room to batch reports between wake-ups
                return new HidLink(h, u.Key.InLen, u.Key.OutLen);
            }
            model = usable[0].Value;
            throw new IOException(model.Name + " is busy (Topping Tune or Topping Home may be using it). " + new Win32Exception(err).Message);
        }

        // ------------------------------------------------------------ instance --
        readonly SafeFileHandle h;
        readonly int inLen, outLen;
        readonly IntPtr inBuf, outBuf, readOv, writeOv;
        readonly ManualResetEvent readEvt = new ManualResetEvent(false), writeEvt = new ManualResetEvent(false);
        readonly object writeLock = new object();
        int lastWake = Environment.TickCount;
        bool disposed;

        HidLink(SafeFileHandle h, int inLen, int outLen)
        {
            this.h = h; this.inLen = inLen; this.outLen = outLen;
            inBuf = Marshal.AllocHGlobal(inLen);
            outBuf = Marshal.AllocHGlobal(outLen);
            readOv = NewOverlapped(readEvt);
            writeOv = NewOverlapped(writeEvt);
        }

        static IntPtr NewOverlapped(ManualResetEvent e)
        {
            int size = Marshal.SizeOf(typeof(NativeOverlapped));
            IntPtr p = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteIntPtr(p, IntPtr.Size * 2 + 8, e.SafeWaitHandle.DangerousGetHandle());
            return p;
        }

        public int InputLength { get { return inLen; } }

        public void Write(byte[] frame)
        {
            var buf = new byte[outLen];
            Array.Copy(frame, 0, buf, 1, Math.Min(15, outLen - 1));   // buf[0] = report id 0
            lock (writeLock)
            {
                Marshal.Copy(buf, 0, outBuf, outLen);
                if (!WriteFile(h, outBuf, outLen, IntPtr.Zero, writeOv))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING) throw new Win32Exception(err);
                    if (!writeEvt.WaitOne(1000)) { CancelIoEx(h, writeOv); int n0; GetOverlappedResult(h, writeOv, out n0, true); throw new IOException("write timed out"); }
                }
                int n;
                if (!GetOverlappedResult(h, writeOv, out n, true)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        public int Read(byte[] buffer, WaitHandle cancel, int minIntervalMs)
        {
            if (!ReadFile(h, inBuf, inLen, IntPtr.Zero, readOv))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) throw new Win32Exception(err);
                // Nothing buffered: this is a real wait. Throttle wake-ups first.
                int since = unchecked(Environment.TickCount - lastWake);
                if (since >= 0 && since < minIntervalMs && cancel.WaitOne(minIntervalMs - since)) return CancelRead();
                if (WaitHandle.WaitAny(new[] { readEvt, cancel }) == 1) return CancelRead();
                lastWake = Environment.TickCount;
            }
            int n;
            if (!GetOverlappedResult(h, readOv, out n, true)) throw new Win32Exception(Marshal.GetLastWin32Error());
            n = Math.Min(n, buffer.Length);
            Marshal.Copy(inBuf, buffer, 0, n);
            return n;
        }

        int CancelRead()
        {
            CancelIoEx(h, readOv);
            int n;
            GetOverlappedResult(h, readOv, out n, true);
            return 0;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { CancelIoEx(h, IntPtr.Zero); } catch { }
            h.Dispose();
            Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf);
            Marshal.FreeHGlobal(readOv); Marshal.FreeHGlobal(writeOv);
            readEvt.Dispose(); writeEvt.Dispose();
        }
    }
}
