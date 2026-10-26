using System;
using System.IO;
using System.Threading;
using DacEqSwitcher;

// EQ on/off switch: bypass, rules keep tracking while off, restore, and staying off across restarts.
static class TT
{
    static string Active() { return File.ReadAllLines(Path.Combine(Environment.GetEnvironmentVariable("DACEQ_EAPO_CONFIG"), Eapo.ActiveName))[0]; }
    static void State(string s) { File.WriteAllText(Environment.GetEnvironmentVariable("DACEQ_SIM_STATE"), s.Replace(";", "\n")); }
    static void Wait(AppCore c) { for (int i = 0; i < 50 && !c.Monitor.Connected; i++) Thread.Sleep(100); Thread.Sleep(800); }
    static int fails;
    static void Expect(string what, string got, string want)
    {
        bool ok = got.Contains(want);
        if (!ok) fails++;
        Console.WriteLine((ok ? "ok    " : "FAIL  ") + what + ": " + got);
    }

    static int Main()
    {
        State("output=2");
        var core = new AppCore();
        core.Profiles.Save("Phones", "Preamp: -3 dB\n");
        core.Profiles.Save("Speakers", "Preamp: -1 dB\n");
        core.Settings.Rules.Add(new Rule { Profile = "Phones", Conditions = { new Condition { Field = Dx1Protocol.Output, Value = 2 } } });
        core.Settings.Rules.Add(new Rule { Profile = "Speakers", Conditions = { new Condition { Field = Dx1Protocol.Output, Value = 1 } } });
        core.SaveSettings();
        core.Start();
        Wait(core);
        Expect("on, headphones", Active(), "\"Phones\"");
        core.SetEqOn(false);
        Expect("switched off", Active(), "EQ switched off");
        State("output=1");
        Thread.Sleep(1000);
        Expect("output changed while off", Active(), "EQ switched off");
        Expect("rules still tracked while off", core.ActiveProfile, "Speakers");
        core.SetEqOn(true);
        Expect("switched back on", Active(), "\"Speakers\"");
        core.SetEqOn(false);
        core.Dispose();

        State("absent=1");   // restart with no DAC connected: must stay off
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("DACEQ_EAPO_CONFIG"), Eapo.ActiveName), "# something else\r\n");
        var again = new AppCore();
        again.Start();
        Thread.Sleep(500);
        Expect("off remembered after restart", Active(), "EQ switched off");
        again.SetEqOn(true);
        Expect("on with no DAC connected restores the last EQ", Active(), "\"Speakers\"");
        State("output=2");
        again.Monitor.Wake();
        Wait(again);
        Expect("DAC connects after switching on", Active(), "\"Phones\"");
        again.Dispose();
        return fails;
    }
}
