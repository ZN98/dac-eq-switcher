using System;
using System.Threading;

namespace DacEqSwitcher
{
    /// Sends commands to the DAC and reports back on the UI thread.
    sealed class DacActions
    {
        readonly AppCore core;
        readonly SynchronizationContext ui;

        public DacActions(AppCore core, SynchronizationContext ui) { this.core = core; this.ui = ui; }

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
