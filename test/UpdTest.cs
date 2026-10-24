using System; using System.IO; using System.Threading; using System.Reflection; using DacEqSwitcher;
static class UT { static void Main(string[] a) {
  Console.WriteLine("current " + Updater.Current);
  var u = new Updater(); var got = new ManualResetEvent(false); u.Changed += () => got.Set();
  u.CheckNow(); bool found = got.WaitOne(8000);
  Console.WriteLine(a[0] + ": " + (found ? "update " + u.Available.Tag + " (sha " + (u.Available.Sha256 != null) + ")" : "no update"));
  if (found && a.Length > 1) {
    string err = u.Install(false);
    string exe = Assembly.GetAssembly(typeof(Updater)).Location;
    Console.WriteLine("install: " + (err ?? "ok") + " | exe version now " + AssemblyName.GetAssemblyName(exe).Version + " | old kept: " + File.Exists(exe + ".old") + " | .new left: " + File.Exists(exe + ".new"));
  } } }
