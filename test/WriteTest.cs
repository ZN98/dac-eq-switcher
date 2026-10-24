using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DacEqSwitcher;

static class W
{
    static AppCore core;
    static void Run(WriteJob j)
    {
        var done = new ManualResetEvent(false); string err = "timeout";
        j.Done = e => { err = e; done.Set(); };
        core.Monitor.Submit(j);
        done.WaitOne(5000);
        Console.WriteLine("  " + j.Label + ": " + (err ?? "ok"));
    }
    static string V(string k, Func<int, string> f = null) { int v; return core.Monitor.Snapshot().TryGetValue(k, out v) ? (f != null ? f(v) : v.ToString()) : "-"; }

    static void Main(string[] a)
    {
        core = new AppCore();
        core.Start();
        for (int i = 0; i < 50 && !core.Monitor.Connected; i++) Thread.Sleep(100);
        Thread.Sleep(600);
        Console.WriteLine(core.Monitor.Status + " " + core.Monitor.Model.Name);
        string profile = "Preamp: -6.2 dB\nFilter 1: ON LSC Fc 105 Hz Gain 5.5 dB Q 0.70\nFilter 2: ON PK Fc 3000 Hz Gain -3.1 dB Q 2.0\nFilter 3: OFF HSC Fc 10000 Hz Gain -1.0 dB Q 0.70\n";
        List<PeqBand> bands; double pre;
        var problems = Peq.Plan(profile, core.Monitor.Model.Protocol, out bands, out pre);
        Console.WriteLine("plan: " + bands.Count + " bands, preamp " + pre + ", problems " + problems.Count);
        var perChannel = "Preamp: -3 dB\nChannel: L\nFilter: ON PK Fc 100 Hz Gain 1 dB Q 1\n";
        if (Peq.Plan(perChannel, Protocol.Dx1, out bands, out pre).Count == 0) Console.WriteLine("ERROR: per-channel EQ accepted");
        Peq.Plan(profile, core.Monitor.Model.Protocol, out bands, out pre);
        if (a[0] == "dx1")
        {
            Run(Dx1Protocol.SetVolume(3, -30));
            Run(Dx1Protocol.SetMute(1));
            Run(Dx1Protocol.SetInput(1));
            Run(Dx1Protocol.SetFilter(2));
            Run(Dx1Protocol.SetGain(1));
            Run(Dx1Protocol.SelectEq(1));
            Run(Dx1Protocol.SelectEq(-1));
            Run(Dx1Protocol.SetPower(false));
            Run(Dx1Protocol.UploadEq(2, bands, pre, -1));
            Thread.Sleep(2000);
            var h = (Dx1Protocol)core.Monitor.Handler;
            Console.WriteLine("state: volHp " + V(Dx1Protocol.VolHp, r => Dx1Protocol.VolumeDb(r) + " dB") + ", mute " + V(Dx1Protocol.MuteBits) + ", input " + V(Dx1Protocol.Input)
                + ", filter " + V(Dx1Protocol.Filter) + ", gain " + V(Dx1Protocol.Gain) + ", dacEq " + V(Dx1Protocol.DacEq) + ", power " + V(Dx1Protocol.Power));
            Console.WriteLine("slots: " + string.Join(", ", h.StoredSlots.Select(s => s.Name + " (" + s.ActiveBands + " bands, " + s.PreampDb.ToString("0.0") + " dB)")));
        }
        else
        {
            Run(Dx5Protocol.SetVolume(60));
            Run(Dx5Protocol.SetGain(true));
            Run(Dx5Protocol.SetPower(false));
            Run(Dx5Protocol.Recall(ToppingTables.RecallC1));
            Run(Dx5Protocol.Set(0x710D, 2));
            Run(Dx5Protocol.UploadEq(bands, pre));
            var act = new DacActions(core, new SynchronizationContext());
            var done = new ManualResetEvent(false);
            act.SendVerified(Dx5Protocol.Set(0x7104, 1), "dx5.inputType", 1, Dx5Protocol.All["dx5.inputType"].Format, e => { Console.WriteLine("  verified input: " + (e ?? "confirmed")); done.Set(); });
            done.WaitOne(5000); done.Reset();
            act.SendVerified(Dx5Protocol.Set(0x7103, 1), "dx5.muted", 1, Dx5Protocol.All["dx5.muted"].Format, e => { Console.WriteLine("  verified mute (DAC ignores it): " + (e ?? "confirmed")); done.Set(); });
            done.WaitOne(5000);
            Console.WriteLine("state: volume " + V("dx5.volume") + ", gain " + V("dx5.highGain") + ", powered " + V("dx5.powered") + ", filter " + V("dx5.pcmFilter") + ", input " + V("dx5.inputType"));
        }
        core.Dispose();
    }
}
