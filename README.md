# DAC EQ Switcher

A small Windows tray app for Topping DACs that:

- **switches your Equalizer APO EQ automatically** when you change a setting on the DAC
  (output, input, headphone gain, PCM filter, the DAC's own EQ preset, sample rate …),
  using rules you set up;
- **replaces the Topping web app**: every DAC setting [toppingctl](https://github.com/gjcourt/toppingctl)
  can change is on the *DAC controls* tab;
- lets you import, type or paste EQs (Peace, Equalizer APO, AutoEQ, REW) with a live response graph.

Supported: the models toppingctl can read, currently the **Topping DX1 II** and **DX5 II**.

## Download

Get `DacEqSwitcher.zip` from the [latest release](../../releases/latest), unzip, and run
`DacEqSwitcher.exe`. Windows 10/11, no install, nothing else needed besides
[Equalizer APO](https://sourceforge.net/projects/equalizerapo/). See `README.txt` in the zip.

## Always on the latest toppingctl

A [daily workflow](.github/workflows/release.yml) clones the latest toppingctl, regenerates
the app's device tables from it (`tools/gen_tables.py`), rebuilds, and runs
[`ci/check.sh`](ci/check.sh): the app is driven against simulated DX1 II and DX5 II units and
every frame it sends must be byte-identical to what toppingctl's own Python builds for the
same command. Only then is a release published; installed copies pick it up within a day.
If toppingctl changes something the app ports by hand, the check fails, no release goes out,
and an issue is opened.

## Everything else it depends on

| Dependency | How it stays current |
|---|---|
| toppingctl (DAC support) | Daily build against its latest commit, released only if every frame still matches |
| .NET Framework 4.8 and Windows USB/HID functions | Part of Windows; kept current by Windows Update |
| Equalizer APO | Not bundled (installing it needs admin rights and a restart). The app checks SourceForge daily and tells you when a newer version is out |
| Build tools (Mono, Python, Ubuntu runner) | Installed fresh on GitHub's current Ubuntu image for every build; no third-party GitHub Actions are used |

## Efficiency

One native 160 KB executable on the .NET Framework built into Windows. While running in
the tray it keeps one connection to the DAC with throttled, event-driven reads (well under
1 % of a core), does nothing at all while no DAC is connected, and only touches Equalizer
APO's file when the EQ actually changes.

## Building

`build.sh <toppingctl checkout> [version]` on Linux with Mono (`mono-complete`), then
`ci/check.sh <toppingctl checkout>`.

## Licence

Apache-2.0. Includes data and logic derived from toppingctl (Apache-2.0); see `NOTICE`.
Not affiliated with Topping.
