# PowerDial

A tray app for controlling the power settings that actually affect battery life, with
presets, per-setting explanations, a live watts readout, process telemetry, and a one-click
way back to how things were.

It runs on **any Windows 10 or 11 PC** - gaming laptop, ultrabook or desktop - and adapts
to what it finds. Nothing about the hardware is assumed: the battery, the GPUs, which
settings exist are all detected, and
anything that has to be measured stays blank until it has been measured **here**. You will
never see a watt figure borrowed from someone else's machine.

On a desktop the battery panels stand down and the rest carries on.

## Run it

Double-click **PowerDial** on the Desktop, or run `PowerDial.exe` in this folder.

Closing the window hides it to the notification area. Right-click the tray icon for the
presets, **Restore original settings**, or **Quit**. Only one instance runs at a time.

## Layout

    control-battery\
      PowerDial.exe             the app - run this
      PowerDial.dll             plus the .json files, System.Management.dll, runtimes\
      PowerDial.slnx            solution: both projects
      global.json               pins the .NET SDK
      Directory.Build.props     build settings shared by both projects
      Directory.Packages.props  package versions, centrally
      README.md                 this file
      CLAUDE.md                 the working brief for changing it
      src\PowerDial\            source
      src\PowerDial-selftest\   read-only checks against the live machine

    Desktop\PowerDial.lnk       shortcut to the exe
    Desktop\PowerDial.exe       the portable single-file copy, if you built one

## Building

Needs the **.NET 10 SDK**. `global.json` pins it to 10.0.400 and rolls forward within that
feature band, so a newer 10.0.4xx SDK is fine and a .NET 8 or 9 SDK is refused with a clear
message rather than a strange build error.

    dotnet build -c Release          from the repo root - builds both projects

To rebuild the copy in this folder, publish somewhere else and copy it in:

    cd src\PowerDial
    dotnet publish -c Release -o %TEMP%\pd-publish
    copy /y %TEMP%\pd-publish\* ..\..

**Do not publish straight into the repo root.** `dotnet publish -o ..\..` fails with
`CS5001: Program does not contain a static 'Main'` - the SDK excludes everything under the
publish directory from compilation, so pointing it at the repo root hides the source from
the compiler. Quit the running copy first or the copy fails on a locked file.

To build the portable single-file copy - one 49 MB `.exe` with the runtime inside it, which
runs on a PC with no .NET installed:

    cd src\PowerDial
    dotnet publish -c Release -r win-x64 --self-contained true -o %TEMP%\pd-portable ^
      -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true

The `bin` and `obj` directories under `src` are throwaway and are git-ignored.

## What it does

**Live readout.** Watts, time left, charge, health, and a sparkline of the recent
measurement history. HP's firmware never reports an instantaneous power figure — the ACPI
`DischargeRate` field returns an invalid sentinel — so draw is derived by timing the
battery's own energy counter over a 60-second window. That means:

- the first reading takes ~60 seconds to appear
- draw only means anything **on battery**; on AC the panel says so rather than showing a
  misleading `0.00 W`
- changing any setting resets the window and clears the trace, so what you see next
  reflects the new state instead of averaging across the change

**Profiles.** Longest / Endurance / Balanced / Full speed. *Balanced* is the configuration
that actually measured **6.92 W (6h21m)**. All of them write the **on-battery side only**;
a profile never touches plugged-in behaviour.

**Restore my settings.** Puts back the battery settings as they stood before PowerDial
existed. The snapshot is taken on first run; since the app writes nothing until you touch
a control, that first-run state *is* the pre-app state. It lives in
`%LOCALAPPDATA%\PowerDial\baseline.json`, and if it is ever missing there is a built-in
fallback: the configuration verified by hand during the tuning session.

Restoring a value that was previously *inherited* rather than stored makes it explicit.
The behaviour is identical, only the bookkeeping differs, and the activity log says so
rather than glossing over it.

**Settings, split by whether they matter.** The main list holds the five that change how
much power you draw while using the machine. Timeouts and lid behaviour — which change
nothing while you are actually working — are tucked into a collapsed **Basic settings**
section rather than removed.

**An info icon on every setting and every section**, explaining in plain terms what it
does, what it costs, and which figures were measured rather than assumed.

**Analytics.** All of it measured on your own machine, and written to disk so it survives
restarts rather than starting from nothing each launch:

- *Power draw* - the raw trace, one point a minute, with a dashed running average.
- *Charge over time* - spans previous runs, amber where you were on battery. Gaps of more
  than 15 minutes are left unjoined, because that means the app was not running.
- *Running now* - a live sample of every process, grouped by name, with instance counts and
  a bar proportional to whichever column you sort on. Toggle memory / CPU, and filter to
  background only.
- *Since recording began* - the cumulative tally. Which process has actually burned the
  most CPU across every session, which is the one costing you runtime. This is the view
  that named `OneDrive.Sync.Service` as the top consumer here.
- *Where your watts go* - which processes were busiest while the reading was timed.
- *Battery health* - 43.9 Wh still held against the 70.6 Wh it shipped with, and what the
  missing capacity costs you in hours at the present draw.

*Background* means no instance of that process owns a visible window. Those are the ones
worth questioning, because you are not the one using them.

Recorded to `%LOCALAPPDATA%\PowerDial\history` at about 150 bytes a minute, roughly 6 MB
for a month of continuous use, pruned after three months.

There are deliberately **no modelled analytics**. An earlier version drew predicted
runtime-versus-brightness curves from three hardcoded workload constants; it was removed
because it described a laptop in the abstract instead of the one in front of you, now.

**Discrete GPU watch.** The most useful part, on any laptop with switchable graphics. An
awake-but-idle discrete GPU draws anywhere from about 5 W to over 20 W while reporting 0%
utilisation and 0 MiB allocated — often more than the whole rest of the system at idle, and
invisible in Task Manager. The watch looks for the software that holds it awake: vendor
overlays and capture (NVIDIA, AMD, Intel), the maker's gaming suite (HP, ASUS, Lenovo, MSI,
Acer, Dell/Alienware) and peripheral lighting engines (Razer, Corsair, Logitech,
SteelSeries). Graphics-vendor tools are only flagged when they belong to the *discrete* GPU,
so Radeon Software driving integrated graphics is not blamed for an NVIDIA card.

On a desktop, or a laptop with one GPU, it says so and stands down — there is no wake cost
to avoid.

The worked example is the laptop this was written on: an RTX 3050 Ti held awake at ~17 W by
three separate things at once — NVIDIA Instant Replay, OMEN Gaming Hub and OMEN Light
Studio. Two relaunched at every boot as *packaged background tasks* via `sihost.exe`, so
disabling their scheduled tasks did nothing; the fix that held was the per-app **Background
apps permission → Never**, which a vendor update can silently flip back on. That is why the
watch re-checks processes, permissions and capture flags every poll.

The cost shown is the wake cost **once**, not the sum over offenders — any single one is
enough to keep the GPU up. Until that cost has been measured on your PC, the panel names
the culprits and says the cost is not yet measured, rather than borrowing a number.

## Design notes

**The battery side is the default, and the only side anything writes by itself.** The
advisor, the profiles and the restore point all call the DC path exclusively. The one
exception is user-driven: the **Plugged in** switch in the settings section routes that
section's edits to the AC side. It is opt-in, resets to battery every launch, and says on
screen which side it is writing. Because the app can change AC, the restore point captures
both sides.

**Reads come from the registry, writes go through `powercfg`.** `powercfg /q` refuses to
display hidden settings — it returns just a scheme header for the lid action, for example —
so the app reads
`HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\...` directly, falling back
to `DefaultPowerSchemeValues` when a scheme has no stored value. Four of the ten settings
are ones Windows hides from its own Power Options UI.

**Writes are verified, not assumed.** After every write the value is read back; if it
disagrees, the activity log says so and shows what it actually reads.

**Elevation is `asInvoker`, deliberately.** Most of what this writes (EPP and the rest)
succeeds unelevated, so demanding a UAC prompt at every launch would be gratuitous. Anything that does need admin reports it and offers **Run as admin**.

**Nothing is written just by running it.** Launching the app leaves every registry value
untouched until a control is used. This is a design rule the restore point depends on, not
something the selftest checks — verify it by hand if you change startup.

## Interface

Dark instrument panel rather than a settings dialog. Bahnschrift Condensed — Windows' DIN
derivative, an engineering face — carries anything numeric; Segoe UI Variable carries the
prose; Cascadia Mono the log.

Two accents, and both mean something rather than decorate: **amber is energy leaving,
green is energy kept**, and the readout switches between them based on the actual
measurement. The signature is the **sparkline**: this whole exercise was about watching a
number move over a window rather than trusting a spec, so the header shows the real recent
history instead of a lone digit.

The header is **pinned**, and under the trace it carries the figure the live reading cannot
give you: the average draw actually recorded on this machine, and how long a full charge
lasts at it. The 60-second number answers "what am I drawing this minute" and swings by
several watts as the CPU breathes; the average answers "how long does this thing last".
Both are measurements — see `Runtime.cs` — and the band says how many recorded minutes it
is standing on, so a four-minute average cannot pass itself off as a runtime estimate.

Under the header, also pinned, is *Where your watts go* — the processes that were busiest
across the same window the draw figure was timed over, so the two can be read against each
other. It is deliberately core-seconds and never watts-per-process: splitting a measured
total by CPU share would be a made-up number, and it would point at the wrong thing, since
whatever is holding a discrete GPU awake costs upwards of 17 W while using almost no CPU.

Below both, a bar of section buttons scrolls the column; nothing is hidden behind a tab.

Every interactive control is custom-drawn, because stock Win32 widgets cannot be made to
look like this and, in the case of `TrackBar`, actively misbehave: a focused one
swallows the mouse wheel and moves its own thumb, so scrolling the window would rewrite a
power setting. Every control here refuses the wheel.

## Files

| File | |
|---|---|
| `Machine.cs` | hardware detection: battery, GPUs, form factor, capabilities |
| `Config.cs` | per-machine measured values, persisted |
| `PowerCfg.cs` | the settings, registry reads, `powercfg` writes, info text |
| `Battery.cs` | WMI battery sampling and the watts calculation |
| `GpuWatch.cs` | discrete-GPU waker detection |
| `Baseline.cs` | the restore point |
| `Presets.cs` | the four profiles |
| `Advisor.cs` | the suggestions: what is wrong right now, and what fixes it |
| `Charts.cs` | the charts and the process table |
| `ProcessWatch.cs` | live per-process CPU and memory sampling |
| `History.cs` | the on-disk record and the cumulative per-process tally |
| `Json.cs` | source-generated JSON serialisers for everything persisted |
| `Diag.cs` | where a swallowed write failure goes so the window can report it |
| `Runtime.cs` | the measured average draw, and the runtime it implies |
| `Theme.cs` | palette, type, drawing helpers |
| `Widgets.cs` | Card, PillButton, Slider, Picker, InfoDot, SectionToggle, Sparkline, SteadyPanel |
| `MainForm.cs` | window and readout |

## Tests

The selftest is read-only and safe to run any time.

    cd src\PowerDial-selftest && dotnet run -c Release   # live machine: every setting readable, values match

It snapshots every battery-side value, runs the advisor scan, and compares - so it proves
reading the machine does not change it. There is no longer a UI test; a version that built
`MainForm` off-screen and asserted its structure was removed because keeping it current
cost more than it caught. Build and open the app after touching the window.

## Known limits

- One figure has to be measured on your hardware before it can be shown: what an awake
  discrete GPU costs. Until then that panel says so instead of guessing.
- Discrete-versus-integrated GPU detection is a heuristic based on the adapter name. It is
  right on every common part but could mislabel something unusual.
- The watts figure needs a full window, and reads high at low charge, where a degraded
  pack's internal resistance inflates measured drain relative to actual consumption.
- It cannot fix the background-app permissions itself — that is a UWP permission, not a
  power setting. It detects the regression and opens the right Settings page.
- Battery health is measured against a design capacity taken from the battery report's
  capacity history, because HP rewrites the live `DesignedCapacity` field to equal the
  learned capacity, which hides all degradation.
