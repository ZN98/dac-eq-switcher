using System;
using System.Runtime.InteropServices;

namespace DacEqSwitcher
{
    /// The sample rate Windows runs a playback device at (its shared-mode format, which is what
    /// Equalizer APO processes). Looks for the DAC by name, else uses the default playback device.
    static class AudioDevices
    {
        public sealed class Device { public string Name; public int SampleRate; }

        /// Null when it can't be read (not on Windows, or no device).
        public static Device Find(string nameHint)
        {
            if (!Native.OnWindows) return null;
            IMMDeviceEnumerator en = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                Device best = null;
                IMMDeviceCollection col;
                if (en.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out col) == 0)
                {
                    int n;
                    col.GetCount(out n);
                    for (int i = 0; i < n; i++)
                    {
                        IMMDevice dev;
                        if (col.Item(i, out dev) != 0) continue;
                        var d = Read(dev);
                        Marshal.ReleaseComObject(dev);
                        if (d != null && Matches(d.Name, nameHint)) { best = d; break; }
                    }
                    Marshal.ReleaseComObject(col);
                }
                if (best == null)
                {
                    IMMDevice def;
                    if (en.GetDefaultAudioEndpoint(0, 0 /* eConsole */, out def) == 0)
                    {
                        best = Read(def);
                        Marshal.ReleaseComObject(def);
                    }
                }
                return best;
            }
            catch { return null; }
            finally { if (en != null) Marshal.ReleaseComObject(en); }
        }

        static bool Matches(string name, string hint)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!string.IsNullOrEmpty(hint))
            {
                // "Topping DX1 II" -> look for "DX1 II"
                string model = hint.StartsWith("Topping ", StringComparison.OrdinalIgnoreCase) ? hint.Substring(8) : hint;
                if (name.IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return name.IndexOf("Topping", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static readonly PropertyKey FriendlyName = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        static readonly PropertyKey DeviceFormat = new PropertyKey(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);

        static Device Read(IMMDevice dev)
        {
            IPropertyStore ps;
            if (dev.OpenPropertyStore(0 /* STGM_READ */, out ps) != 0) return null;
            var pv = Marshal.AllocHGlobal(32);
            try
            {
                var d = new Device();
                for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
                var k = FriendlyName;
                if (ps.GetValue(ref k, pv) == 0 && Marshal.ReadInt16(pv) == 31 /* VT_LPWSTR */)
                    d.Name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8));
                PropVariantClear(pv);
                k = DeviceFormat;
                if (ps.GetValue(ref k, pv) == 0 && Marshal.ReadInt16(pv) == 65 /* VT_BLOB */)
                {
                    int size = Marshal.ReadInt32(pv, 8);
                    IntPtr blob = Marshal.ReadIntPtr(pv, 8 + IntPtr.Size);
                    if (size >= 8 && blob != IntPtr.Zero) d.SampleRate = Marshal.ReadInt32(blob, 4);   // WAVEFORMATEX.nSamplesPerSec
                }
                PropVariantClear(pv);
                return d.SampleRate > 0 ? d : null;
            }
            finally
            {
                Marshal.FreeHGlobal(pv);
                Marshal.ReleaseComObject(ps);
            }
        }

        [DllImport("ole32.dll")] static extern int PropVariantClear(IntPtr pvar);

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey
        {
            public Guid Fmtid;
            public int Pid;
            public PropertyKey(Guid g, int p) { Fmtid = g; Pid = p; }
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, IntPtr pv);
        }
    }
}
