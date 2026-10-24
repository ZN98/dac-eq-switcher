using System; using System.IO; using System.Threading; using System.Reflection; using DacEqSwitcher;
static class UT { static void Main(string[] a) {
  Console.WriteLine("current " + Updater.Current);
  var u = new Updater(() => Environment.GetEnvironmentVariable("FAKE_EAPO_DIR")); var got = new ManualResetEvent(false); u.Changed += () => got.Set();
  u.CheckNow(); got.WaitOne(8000); Thread.Sleep(1500); bool found = u.Available != null;
  Console.WriteLine(a[0] + ": " + (found ? "update " + u.Available.Tag + " (sha " + (u.Available.Sha256 != null) + ")" : "no update"));
  Console.WriteLine("Equalizer APO: installed " + u.EapoInstalled + ", newer available: " + (u.EapoLatest == null ? "no" : u.EapoLatest.ToString()));
  if (found && a.Length > 1) {
    string err = u.Install(false);
    string exe = Assembly.GetAssembly(typeof(Updater)).Location;
    Console.WriteLine("install: " + (err ?? "ok") + " | exe version now " + AssemblyName.GetAssemblyName(exe).Version + " | old kept: " + File.Exists(exe + ".old") + " | .new left: " + File.Exists(exe + ".new"));
  } } }
