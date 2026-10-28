using System;
using System.Threading;
using System.Windows.Forms;

namespace DacEqSwitcher
{
    /// Sends commands to the DAC and reports back on the UI thread.
    sealed class DacActions
    {
        readonly AppCore core;
        readonly SynchronizationContext ui;

        public DacActions(AppCore core, SynchronizationContext ui) { this.core = core; this.ui = ui; }

        /// toppingctl marks DX1 II control "unverified": ask once before the first command.
        public bool Consent(IWin32Window owner)
        {
            if (!(core.Monitor.Handler is Dx1Protocol) || core.Settings.Dx1WriteConsent) return true;
            var r = MessageBox.Show(owner,
                "toppingctl, which this app's DAC control comes from, marks DX1 II control as \"unverified\": " +
                "it was worked out on one unit (firmware 3.07) and each command was checked on that DAC's front panel.\n\n" +
                "The app only sends the commands toppingctl implements, and never factory reset, firmware or scene-save commands.\n\n" +
                "Control your DX1 II from this app?", Program.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r != DialogResult.Yes) return false;
            core.Settings.Dx1WriteConsent = true;
            core.SaveSettings();
            return true;
        }

        public void Send(WriteJob job, Action<string> done)
        {
            job.Done = err => ui.Post(_ => { if (done != null) done(err); }, null);
            core.Monitor.Submit(job);
        }

        /// setctl.py semantics: write, then confirm the DAC reports the new value.
        public void SendVerified(WriteJob job, string fieldId, int expected, Func<int, string> format, Action<string> done)
        {
            job.Done = err =>
            {
                if (err != null) { ui.Post(_ => done(err), null); return; }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    int v = int.MinValue;
                    for (int i = 0; i < 25; i++)
                    {
                        Thread.Sleep(100);
                        if (core.Monitor.Snapshot().TryGetValue(fieldId, out v) && v == expected) { ui.Post(__ => done(null), null); return; }
                    }
                    string got = v == int.MinValue ? "nothing" : format(v);
                    ui.Post(__ => done("The DAC didn't take the change (it still reports " + got + ")."), null);
                });
            };
            core.Monitor.Submit(job);
        }
    }
}
