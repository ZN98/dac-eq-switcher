using System;
using System.Collections.Generic;
using System.Threading;

namespace DacEqSwitcher
{
    /// Keeps one connection to the DAC and tracks its settings.
    /// Costs: one 16-byte query every TickMs, and at most ~60 wake-ups a second
    /// for replies (fewer when idle). Nothing runs while no DAC is connected:
    /// it waits for Windows' device-change notification instead of polling.
    sealed class DeviceMonitor : IDisposable
    {
        public const int TickMs = 150;          // output-mode check interval
        const int ReadThrottleMs = 15;          // min gap between reader wake-ups
        const int LostAfterMs = 4000;           // no reply this long = reconnect
        const int IdleRecheckMs = 60000;        // safety re-scan when no DAC found

        readonly object gate = new object();
        readonly Dictionary<string, int> values = new Dictionary<string, int>();
        readonly ManualResetEvent stopEvt = new ManualResetEvent(false);
        readonly AutoResetEvent wakeEvt = new AutoResetEvent(false);
        readonly Queue<WriteJob> jobs = new Queue<WriteJob>();
        Thread thread;
        volatile bool paused;

        public event Action Changed;
        public string Status { get; private set; }
        public bool Connected { get; private set; }
        public bool Paused { get { return paused; } }
        public ModelInfo Model { get; private set; }
        public ProtocolHandler Handler { get; private set; }

        public DeviceMonitor() { Status = "Looking for your DAC…"; }

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "DAC monitor", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        /// Called on Windows device-change notifications and when pause changes.
        public void Wake() { wakeEvt.Set(); }

        public void SetPaused(bool p) { paused = p; wakeEvt.Set(); }

        /// Queues frames for the DAC; sent before the next poll (immediately in practice).
        public void Submit(WriteJob job)
        {
            lock (jobs) jobs.Enqueue(job);
            wakeEvt.Set();
        }

        WriteJob NextJob()
        {
            lock (jobs) return jobs.Count > 0 ? jobs.Dequeue() : null;
        }

        void FailJobs(string why)
        {
            WriteJob j;
            while ((j = NextJob()) != null) if (j.Done != null) j.Done(why);
        }

        public Dictionary<string, int> Snapshot()
        {
            lock (gate) return new Dictionary<string, int>(values);
        }

        void Raise()
        {
            var h = Changed;
            if (h != null) h();
        }

        void SetStatus(bool connected, string status)
        {
            if (Connected == connected && Status == status) return;
            Connected = connected; Status = status;
            Raise();
        }

        void Set(string id, int v)
        {
            bool changed;
            lock (gate)
            {
                int old;
                changed = !values.TryGetValue(id, out old) || old != v;
                values[id] = v;
            }
            if (changed) Raise();
        }

        void Run()
        {
            while (!stopEvt.WaitOne(0))
            {
                FailJobs(paused ? "Paused: resume to control the DAC." : "The DAC isn't connected.");
                if (paused)
                {
                    SetStatus(false, "Paused: the DAC is free for Topping Tune / Topping Home");
                    WaitHandle.WaitAny(new WaitHandle[] { stopEvt, wakeEvt });
                    continue;
                }
                IDacLink link = null;
                ModelInfo model = null;
                string product = null, unsupported = null, error = null;
                try { link = DacLink.Open(out model, out product, out unsupported); }
                catch (Exception ex) { error = ex.Message; Log.Write("open: " + ex.Message); }

                if (link == null)
                {
                    if (model != null) Model = model;
                    SetStatus(false, error ?? (unsupported != null
                        ? "“" + unsupported + "” isn't a model toppingctl supports"
                        : "No supported Topping DAC connected"));
                    WaitHandle.WaitAny(new WaitHandle[] { stopEvt, wakeEvt }, error != null ? 3000 : IdleRecheckMs);
                    continue;
                }

                var handler = ProtocolHandler.For(model.Protocol);
                Model = model;
                if (handler == null)
                {
                    link.Dispose();
                    Handler = null;
                    SetStatus(false, model.Name + ": toppingctl can't read this model's settings yet, so it can't trigger EQ changes");
                    WaitHandle.WaitAny(new WaitHandle[] { stopEvt, wakeEvt }, IdleRecheckMs);
                    continue;
                }

                lock (gate) values.Clear();
                Handler = handler;
                Log.Write("connected: " + product);
                int began = Environment.TickCount;
                try { Session(link, handler); }
                catch (Exception ex) { Log.Write("session: " + ex.Message); }
                finally { link.Dispose(); }
                if (!paused) SetStatus(false, "Reconnecting…");
                // back off if the DAC opens but keeps failing straight away
                bool quick = unchecked(Environment.TickCount - began) < 3000;
                WaitHandle.WaitAny(new WaitHandle[] { stopEvt }, quick ? 3000 : 500);
            }
        }

        void Session(IDacLink link, ProtocolHandler handler)
        {
            var end = new ManualResetEvent(false);
            int lastReply = Environment.TickCount;
            var reader = new Thread(() =>
            {
                var buf = new byte[Math.Max(link.InputLength, 65)];
                try
                {
                    while (true)
                    {
                        int n = link.Read(buf, end, ReadThrottleMs);
                        if (n == 0) break;
                        int cmd, idx, flen;
                        uint v;
                        if (!Frames.TryParse(buf, n, out cmd, out idx, out flen, out v)) continue;   // idle report
                        lastReply = Environment.TickCount;
                        handler.Parse(cmd, idx, flen, v, Set);
                    }
                }
                catch (Exception ex) { Log.Write("read: " + ex.Message); }
                end.Set();
            }) { IsBackground = true, Name = "DAC reader", Priority = ThreadPriority.AboveNormal };
            reader.Start();

            SetStatus(true, "Connected");
            int tick = 0;
            try
            {
                while (true)
                {
                    WriteJob job;
                    while ((job = NextJob()) != null)
                    {
                        string err = null;
                        try
                        {
                            foreach (var f in job.Frames)
                            {
                                if (f == null) Thread.Sleep(80);          // let the DAC switch slot etc.
                                else { link.Write(f); Thread.Sleep(4); }  // paced like the vendor app
                            }
                            Log.Write("sent " + job.Label + " (" + job.Frames.Count + " frames)");
                        }
                        catch (Exception ex) { err = "Couldn't send to the DAC: " + ex.Message; }
                        if (job.Refresh) { handler.RequestRefresh(); tick = 0; }
                        if (job.Done != null) job.Done(err);
                        if (err != null) throw new System.IO.IOException(err);
                    }
                    foreach (int cmd in handler.Poll(tick)) link.Write(Frames.Request(cmd));
                    tick++;
                    int w = WaitHandle.WaitAny(new WaitHandle[] { stopEvt, end, wakeEvt }, TickMs);
                    if (w == 0 || w == 1 || paused) break;
                    if (unchecked(Environment.TickCount - lastReply) > LostAfterMs) { Log.Write("no reply"); break; }
                }
            }
            catch (Exception ex) { Log.Write("write: " + ex.Message); }
            end.Set();
            reader.Join(2000);
        }

        public void Dispose()
        {
            stopEvt.Set();
            if (thread != null) thread.Join(2000);
        }
    }
}
