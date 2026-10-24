using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("DAC EQ Switcher")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DacEqTest")]
[assembly: System.Reflection.AssemblyProduct("DAC EQ Switcher")]
[assembly: System.Reflection.AssemblyDescription("Switches Equalizer APO EQs from a Topping DAC's settings (uses toppingctl's protocol tables)")]

namespace DacEqSwitcher
{
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);

        public static bool OnWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        /// Returns memory the hidden app no longer needs to Windows.
        public static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (OnWindows)
                try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1)); } catch { }
        }
    }

    static class Program
    {
        public const string Name = "DAC EQ Switcher";

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--eapo-setup")
                return Eapo.RunElevatedSetup(args[1], args[2]);

            bool first;
            var mutex = new Mutex(true, @"Local\DacEqSwitcher.Instance", out first);
            if (!first && args.Contains("--updated"))
            {
                // the previous version is exiting after starting this one
                try { first = mutex.WaitOne(15000); } catch (AbandonedMutexException) { first = true; }
            }
            var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DacEqSwitcher.Show");
            if (!first)
            {
                showSignal.Set();   // bring the running copy's window up instead
                return 0;
            }

            Updater.CleanUp();
            if (Native.OnWindows) try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (var core = new AppCore())
            {
                bool firstRun = !System.IO.File.Exists(Paths.Settings);
                var app = new TrayApp(core, !args.Contains("--tray") || firstRun);
                if (firstRun) core.SaveSettings();
                ThreadPool.RegisterWaitForSingleObject(showSignal, (s, t) => app.PostShow(), null, -1, false);
                Startup.Refresh();
                Application.Run(app);
            }
            GC.KeepAlive(mutex);
            return 0;
        }
    }
}
