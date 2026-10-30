DAC EQ Switcher
===============

Switches your Equalizer APO EQ automatically when you change a setting on your
Topping DAC: output (headphones / line out), input, headphone gain, PCM filter,
the DAC's own EQ preset, sample rate and more. You choose which settings
trigger which EQ. It also replaces the Topping web app: every DAC setting toppingctl
can change is in the app.

Supported DACs are the ones toppingctl can read: Topping DX1 II and DX5 II.
(toppingctl also knows the D90 III Discrete, but can't read its settings yet.)

REQUIREMENTS
  - Windows 10 or 11 (uses the .NET Framework 4.8 built into Windows; nothing to install)
  - Equalizer APO, installed on your DAC (equalizerapo on SourceForge)

GETTING STARTED
  1. Put DacEqSwitcher.exe anywhere you like (for example a folder in Documents) and run it.
     Windows may show "Windows protected your PC" because the file isn't signed:
     click "More info", then "Run anyway".
  2. Home: click "Set up" at the bottom (asks for administrator permission once).
  3. EQ profiles (second icon on the left): Import your EQs (Peace .peace, Equalizer APO / AutoEQ / REW .txt,
     or an impulse response .wav/.flac for convolution), or click New and type or paste
     Equalizer APO commands. The graph shows the result and warns if it could clip.
  4. Home > EQ > New rule: add rules such as "Output is Headphones -> use EM6L".
     Rules are checked top to bottom (reorder them on the Rules page); the first
     match wins.
  5. Settings (gear icon): tick "Start with Windows". Close the window: the app keeps running in the
     notification area (click its icon to reopen it).

DAC CONTROLS
  Everything toppingctl can change for your model is in the app:
    DX1 II: volume, mute, input, headphone gain, PCM filter (Home), power (top
            right). Settings > DAC: whether mute also mutes the optical output.
    DX5 II: volume, input, headphone gain, PCM filter (Home), power, recalling
            scenes C1/C2 and ~30 more settings (Settings > DAC, each read back from
            the DAC to confirm it changed).
  Volume follows the slider live as you drag it. Each slider position is one of the
  DAC's real steps (DX1 II: 1 dB below -10 dB, 0.5 dB above), and the mouse wheel,
  arrow keys and -/+ buttons move exactly one step. Going above -10 dB asks first:
  while dragging, the DAC holds at -10 dB until you let go and confirm.
  Things toppingctl can't do aren't offered: changing the DX1 II's output (use the
  knob), saving scenes, factory reset or firmware updates.
  toppingctl marks DX1 II control as tested on one unit, so the app asks once before
  sending anything to a DX1 II.

EQUALIZER APO COMMANDS
  An EQ can use anything Equalizer APO understands: Preamp, Filter (PK/PEQ/Modal, LP/HP,
  LPQ/HPQ, BP, NO, AP, LS/HS, LSC/HSC with Q, BW Oct or slope, LS/HS 6dB/12dB, IIR with
  your own coefficients), GraphicEQ, Convolution, Delay, Copy, Channel, Stage, Include,
  Device, If/ElseIf/Else/EndIf, Eval and `inline expressions`, VSTPlugin and
  LoudnessCorrection. The app passes your text to Equalizer APO unchanged.
    - The graph and clipping check follow Equalizer APO's own maths (ported from its
      source): every filter type, GraphicEQ's minimum-phase FIR, convolution impulse
      responses (WAV and FLAC), Delay/Copy (crossfeed), channels, expressions and Ifs.
      VST plug-ins and loudness correction run but can't be drawn; the note under the
      graph says so, and lists anything Equalizer APO would skip.
    - Filters depend on the sample rate. The graph uses the rate Windows runs your DAC
      at (Sound settings > your DAC > Format); pick another rate under the graph to check.
    - Convolution impulse responses must match the DAC's sample rate, or Equalizer APO
      skips them. Use "If: sampleRate == 44100" ... "EndIf:" to pick one per rate.
    - Files an EQ uses (impulse responses, included files) are kept with it: importing
      brings them along, and the Files button adds or removes them. The EQ and its files
      are copied into Equalizer APO's config folder when it's in use.
    - Equalizer APO's command names are case-sensitive ("preamp:" is ignored); importing
      corrects them, and the editor flags lines Equalizer APO won't read.

GOOD TO KNOW
  - EQ on/off: the EQ switch on Home, "EQ on" in the tray menu, or
    middle-click the tray icon. Off bypasses Equalizer APO's EQ (the tray icon becomes
    a hollow ring); on puts back whatever your rules pick. It's remembered across restarts.
  - Pause (tray menu or Home) frees the DAC so Topping Tune / Topping Home can
    connect. Only one program can talk to the DAC at a time.
  - Input, gain and filter on the DX1 II can only be learned when you change them on
    the DAC (asking for them would reset them, so the app never does). Until then the
    app uses the value it last saw.
  - The DAC only changes when you change something in the app; rules only switch the
    Equalizer APO EQ.
  - If Equalizer APO's config.txt also applies another EQ, Home tells you
    and can turn it off (a backup of config.txt is kept).

UPDATES
  The app checks once a day for a new version of itself (one small request to GitHub)
  and of Equalizer APO (one to SourceForge). App updates are offered
  on Home and in the tray menu; one click installs them. For Equalizer APO
  you get a download link (it needs admin rights and a restart, so it isn't automatic). New versions are
  built automatically whenever toppingctl changes, and only released after every DAC
  command has been checked byte-for-byte against toppingctl. Turn the check off in
  Settings if you prefer. Releases: https://github.com/ZN98/dac-eq-switcher/releases

WHERE THINGS ARE
  Your EQs and settings:   %APPDATA%\DacEqSwitcher
  In Equalizer APO:        config\daceqswitcher_active.txt, plus one Include line in config.txt;
                           EQs with files also use config\DAC EQ Switcher\<EQ name>\

REMOVING IT
  Untick "Start with Windows", exit from the tray menu, delete the exe and
  %APPDATA%\DacEqSwitcher, and remove the "DAC EQ Switcher" lines from Equalizer APO's config.txt.

LICENCE AND CREDITS
  DAC EQ Switcher is free software under the GNU General Public License, version 3 or
  later (LICENSE.txt). Source code: https://github.com/ZN98/dac-eq-switcher
  Device support, register map and setting names come from toppingctl
  (github.com/gjcourt/toppingctl, Apache-2.0, see toppingctl-LICENSE.txt).
  The response graph follows Equalizer APO's own processing, ported from its source
  (sourceforge.net/projects/equalizerapo, Jonas Thedering, GPL-2.0-or-later); its
  expressions follow muParserX (Ingo Berg, BSD-2-Clause).
  Not affiliated with Topping or the Equalizer APO project.
