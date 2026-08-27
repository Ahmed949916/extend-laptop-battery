# PowerDial

A tray app for controlling the power settings that actually affect battery life, with
presets, per-setting explanations, a live watts readout, process telemetry, and a one-click
way back to how things were.

It runs on **any Windows 10 or 11 PC** - gaming laptop, ultrabook or desktop - and adapts
to what it finds. Nothing about the hardware is assumed: the battery, the GPUs, which
settings exist and whether the display brightness can be controlled are all detected, and
anything that has to be measured stays blank until it has been measured **here**. You will
never see a watt figure borrowed from someone else's machine.

On a desktop the battery panels stand down and the rest carries on.

## Run it

Double-click **PowerDial** on the Desktop, or run `PowerDial.exe` in this folder.

Closing the window hides it to the notification area. Right-click the tray icon for the
presets, **Restore original settings**, or **Quit**. Only one instance runs at a time.

## Layout

    control-battery\
      PowerDial.exe              the app - run this
      PowerDial.dll             plus the .json files, System.Management.dll, runtimes\
      README.md                 this file
      src\PowerDial\            source
      src\PowerDial-selftest\   read-only checks against the live machine
      src\PowerDial-uitest\     drives the real window, 42 checks

    Desktop\PowerDial.lnk       shortcut to the exe

Rebuild after editing anything under `src\PowerDial`:

    cd src\PowerDial
    dotnet publish -c Release -o ..\..

This folder is inside OneDrive, so it syncs. The `bin` and `obj` directories that appear
under `src` when you build are throwaway - delete them if the sync noise bothers you.

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

**Profiles.** Endurance / Balanced / Full speed. *Balanced* is the configuration that
actually measured **6.92 W (6h21m)**. All of them write the **on-battery side only** —
plugged-in behaviour is never touched, which is what kept this safe to experiment with.

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
- *Where your watts go* - the live draw split into backlight and everything else.
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

**Only the DC side is ever written.** No code path touches AC values.

**Reads come from the registry, writes go through `powercfg`.** `powercfg /q` refuses to
display hidden settings — it returns just a scheme header for the lid action, for example —
so the app reads
`HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\...` directly, falling back
to `DefaultPowerSchemeValues` when a scheme has no stored value. Four of the ten settings
are ones Windows hides from its own Power Options UI.

**Writes are verified, not assumed.** After every write the value is read back; if it
disagrees, the activity log says so and shows what it actually reads.

**Elevation is `asInvoker`, deliberately.** Most of what this writes (EPP, brightness)
succeeds unelevated, so demanding a UAC prompt at every launch would be gratuitous. Anything that does need admin reports it and offers **Run as admin**.

**Nothing is written just by running it.** Verified: launching the app leaves every
registry value untouched until a control is used.

## Interface

Dark instrument panel rather than a settings dialog. Bahnschrift Condensed — Windows' DIN
derivative, an engineering face — carries anything numeric; Segoe UI Variable carries the
prose; Cascadia Mono the log.

Two accents, and both mean something rather than decorate: **amber is energy leaving,
green is energy kept**, and the readout switches between them based on the actual
measurement. The signature is the **sparkline**: this whole exercise was about watching a
number move over a window rather than trusting a spec, so the header shows the real recent
history instead of a lone digit.

Every interactive control is custom-drawn, because stock Win32 widgets cannot be made to
look like this and, in the case of `TrackBar`, actively misbehave (see below).

## Bugs found and fixed while building this

Kept here because each one is a trap worth remembering.

**The UI lied about the machine.** Applying on `MouseUp` meant wheel and keyboard changes
never committed — the EPP slider drifted to 90 while the registry still held 80. Commits
are now debounced 600 ms after *any* value change.

**The mouse wheel silently rewrote system settings.** A focused Win32 `TrackBar` swallows
wheel events and moves its own thumb, so scrolling the window changed a power setting.
Every control here refuses the wheel.

**The window scrolled itself away from its own header.** An `AutoScroll` panel calls
`ScrollToControl` whenever focus moves. `SteadyPanel` overrides it to stay put.

**It cost 3.75% of a CPU core** — absurd for something whose job is saving power — by
constructing a `ManagementObjectSearcher` on every poll. Now **0.16%**.

**`0.00 watts drawn` while charging.** The energy counter barely moves on AC, so the
computed figure read as "using no power". It now says what it actually knows.

**A crash with no message.** `BeginInvoke` in the constructor, before the window handle
exists. There is now a global handler that writes `%LOCALAPPDATA%\PowerDial\crash.log`.

**An `AutoSize` Card wrapping a docked `AutoSize` panel** made the form open full-screen
and paint the GPU section twice.

## Files

| File | |
|---|---|
| `Machine.cs` | hardware detection: battery, GPUs, form factor, capabilities |
| `Config.cs` | per-machine measured values, persisted |
| `PowerCfg.cs` | the settings, registry reads, `powercfg` writes, info text |
| `Battery.cs` | WMI battery sampling and the watts calculation |
| `Brightness.cs` | WMI backlight get/set |
| `GpuWatch.cs` | discrete-GPU waker detection |
| `Baseline.cs` | the restore point |
| `Presets.cs` | the three profiles |
| `Charts.cs` | the charts and the process table |
| `ProcessWatch.cs` | live per-process CPU and memory sampling |
| `History.cs` | the on-disk record and the cumulative per-process tally |
| `Theme.cs` | palette, type, drawing helpers |
| `Widgets.cs` | Card, PillButton, Slider, Picker, InfoDot, SectionToggle, Sparkline, SteadyPanel |
| `MainForm.cs` | window and readout |

## Tests

Both are read-only and safe to run any time.

    cd src\PowerDial-selftest && dotnet run -c Release   # live machine: every setting readable, values match
    cd src\PowerDial-uitest   && dotnet run -c Release   # drives the real window: 42 checks

The UI test builds the actual `MainForm` off-screen and drives the controls directly
rather than firing synthetic mouse events, which kept missing and once snapped the window
to half the screen. Set `PD_SHOTS` to a directory to have it render the collapsed and
expanded states to PNGs.

## Known limits

- Some figures have to be measured on your hardware before they can be shown: the backlight
  cost per brightness point, and what an awake discrete GPU costs. Until then those panels
  say so instead of guessing.
- Discrete-versus-integrated GPU detection is a heuristic based on the adapter name. It is
  right on every common part but could mislabel something unusual.
- The watts figure needs a full window, and reads high at low charge, where a degraded
  pack's internal resistance inflates measured drain relative to actual consumption.
- It cannot fix the background-app permissions itself — that is a UWP permission, not a
  power setting. It detects the regression and opens the right Settings page.
- Battery health is measured against a design capacity taken from the battery report's
  capacity history, because HP rewrites the live `DesignedCapacity` field to equal the
  learned capacity, which hides all degradation.
