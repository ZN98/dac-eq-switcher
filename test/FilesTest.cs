using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DacEqSwitcher;

// EQs that use files: import brings the files Equalizer APO would read (relative to the imported
// file, Include nested, names picked by expressions), audio files import as Convolution EQs, the
// active EQ and its files are copied into Equalizer APO's config folder and Included from there
// (byte for byte, resynced when they change), and rename/delete keep everything consistent.
static class FT
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) fails++; Console.WriteLine((ok ? "ok    " : "FAIL  ") + what); }
    static string Cfg { get { return Environment.GetEnvironmentVariable("DACEQ_EAPO_CONFIG"); } }
    static string Active() { return File.ReadAllText(Path.Combine(Cfg, Eapo.ActiveName)); }

    static void Wav(string path, int rate, float[] samples)
    {
        var data = samples.SelectMany(BitConverter.GetBytes).ToArray();
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data.Length); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)32);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data.Length); w.Write(data);
        }
    }

    static int Main(string[] a)
    {
        string src = a[0];
        Directory.CreateDirectory(Path.Combine(src, "irs"));
        Wav(Path.Combine(src, "irs", "room.wav"), 48000, new float[] { 0.5f, 0.3f, -0.1f, 0.05f });
        Wav(Path.Combine(src, "bass.wav"), 48000, new float[] { 0.9f, 0.2f });
        Wav(Path.Combine(src, "hp_44100.wav"), 44100, new float[] { 0.7f, 0.1f });
        Wav(Path.Combine(src, "hp_48000.wav"), 48000, new float[] { 0.8f, -0.1f });
        File.WriteAllText(Path.Combine(src, "inc.txt"), "Preamp: -1 dB\r\nConvolution: bass.wav\r\n");
        File.WriteAllText(Path.Combine(src, "Room.txt"), "Preamp: -4 dB\r\nfilter: ON PK Fc 100 Hz Gain 3 dB Q 1\r\nConvolution: irs\\room.wav\r\nInclude: inc.txt\r\nConvolution: hp_`sampleRate`.wav\r\nConvolution: missing.wav\r\n");

        File.WriteAllText(Environment.GetEnvironmentVariable("DACEQ_SIM_STATE"), "output=2");
        var core = new AppCore();
        var notes = new List<string>();
        string name = core.Profiles.Import(Path.Combine(src, "Room.txt"), notes);
        string text = core.Profiles.Read(name);
        var files = core.Profiles.Files(name);
        Check(files.SequenceEqual(new[] { "bass.wav", "hp_44100.wav", "hp_48000.wav", "inc.txt", "room.wav" }), "import brought the referenced files: " + string.Join(", ", files));
        Check(text.Contains("Convolution: room.wav") && text.Contains("Include: inc.txt") && text.Contains("Convolution: hp_`sampleRate`.wav"), "references point at the copies:\n" + text);
        Check(text.Contains("Filter: ON PK Fc 100 Hz"), "lower-case command corrected to Equalizer APO's spelling");
        Check(notes.Any(n => n.Contains("missing.wav")) && notes.Any(n => n.Contains("spelling")), "notes: " + string.Join(" | ", notes));
        Check(File.ReadAllText(Path.Combine(core.Profiles.FilesDir(name), "inc.txt")).Contains("Convolution: bass.wav"), "the included file's own reference was brought along");

        string wavProfile = core.Profiles.Import(Path.Combine(src, "bass.wav"), notes);
        Check(core.Profiles.Read(wavProfile).Trim() == "Convolution: bass.wav" && core.Profiles.HasFiles(wavProfile), "an impulse response imports as a Convolution EQ");

        core.Profiles.Save("Plain", "Preamp: -2 dB\r\n");
        core.Settings.Rules.Add(new Rule { Profile = name, Conditions = { new Condition { Field = Dx1Protocol.Output, Value = 2 } } });
        core.Settings.Rules.Add(new Rule { Profile = "Plain", Conditions = { new Condition { Field = Dx1Protocol.Output, Value = 1 } } });
        core.SaveSettings();
        core.Start();
        for (int i = 0; i < 50 && !core.Monitor.Connected; i++) Thread.Sleep(100);
        Thread.Sleep(800);

        string synced = Path.Combine(Cfg, Eapo.ProfilesFolder, name);
        Check(Active().Contains("Include: " + Eapo.ProfilesFolder + "\\" + name + "\\" + ProfileStore.SyncedName), "the active file includes the EQ from the config folder:\n" + Active());
        Check(File.ReadAllText(Path.Combine(synced, ProfileStore.SyncedName)) == text, "the EQ's text was copied unchanged");
        Check(files.All(f => File.ReadAllBytes(Path.Combine(synced, f)).SequenceEqual(File.ReadAllBytes(Path.Combine(core.Profiles.FilesDir(name), f)))), "its files were copied byte for byte");

        // what Equalizer APO would hear from config.txt -> active file -> synced EQ equals the EQ drawn in the app
        var viaApo = EqText.Analyse(Active().Replace("Include: " + Eapo.ProfilesFolder + "\\" + name + "\\", "Include: " + Eapo.ProfilesFolder + "/" + name + "/"), 300, 20, 20000, Cfg, 48000);
        var inApp = EqText.Analyse(text, 300, 20, 20000, core.BaseDirFor(name), 48000);
        double d = viaApo.L.Zip(inApp.L, (x, y) => Math.Abs(x - y)).Max();
        Check(d < 1e-9 && inApp.Notes.Count(n => n.Contains("missing.wav")) == 1, "the response through Equalizer APO's include matches the app's graph (max diff " + d.ToString("0.000000") + " dB)");

        // a changed file is resynced; a removed one disappears
        Wav(Path.Combine(core.Profiles.FilesDir(name), "room.wav"), 48000, new float[] { 0.1f, 0.1f, 0.1f });
        File.SetLastWriteTimeUtc(Path.Combine(core.Profiles.FilesDir(name), "room.wav"), DateTime.UtcNow.AddMinutes(1));
        core.Profiles.RemoveFile(name, "hp_44100.wav");
        core.Changed();
        Check(File.ReadAllBytes(Path.Combine(synced, "room.wav")).SequenceEqual(File.ReadAllBytes(Path.Combine(core.Profiles.FilesDir(name), "room.wav"))), "a changed impulse response is copied again");
        Check(!File.Exists(Path.Combine(synced, "hp_44100.wav")), "a removed file is removed from the config folder too");

        // other EQs stay inline, exactly as before
        File.WriteAllText(Environment.GetEnvironmentVariable("DACEQ_SIM_STATE"), "output=1");
        Thread.Sleep(1500);
        Check(Active().Contains("Preamp: -2 dB") && !Active().Contains("Include:"), "an EQ without files is written inline as before");

        // rename and delete
        core.Profiles.Rename(name, "Room 2");
        Check(core.Profiles.HasFiles("Room 2") && !Directory.Exists(core.Profiles.FilesDir(name)), "rename moves the files");
        core.Profiles.Delete("Room 2");
        Check(!Directory.Exists(core.Profiles.FilesDir("Room 2")) && !core.Profiles.Exists("Room 2"), "delete removes the files");
        core.Eapo.RemoveSynced(name);
        Check(!Directory.Exists(synced), "the config folder copy is removed");
        core.Dispose();
        Console.WriteLine(fails == 0 ? "files: all checks passed" : "files: " + fails + " FAILED");
        return fails;
    }
}
