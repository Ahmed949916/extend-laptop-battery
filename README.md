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

Double-click `PowerDial.exe` in this folder, or the shortcut if you made one.

Closing the window hides it to the notification area. Right-click the tray icon for the
profiles, **Original settings**, **Open PowerDial** or **Quit PowerDial** - quitting is the
only one that actually ends it. Only one instance runs at a time; launching a second copy
raises the window that already exists rather than starting another.

## Pages

A sidebar down the left, six entries, nothing hidden behind a mode. The app opens on
**Overview** every time rather than restoring whichever page you closed on.

**Overview** - the one screen that answers "is anything wrong, and what should I do".
The battery stated once; the single most worthwhile change with a button that makes it;
what your current settings are worth against the ones this laptop started with; the mode in
effect; and what is keeping the processor busy.

**Power modes** - five cards, each stating the trade in a sentence, listing what it
actually changes, and showing what it measured **here**. *Max battery*, *Battery saver*,
*Balanced*, *Performance*, and **Original settings** - how this laptop was set up before
PowerDial wrote anything, read from the restore point captured on first run. That last one
is a profile like any other: you can pick it, it lights up when you are on it, and minutes
recorded while you are get grouped under it, which is what makes the comparison on Overview
and Insights possible at all.

**Insights** - what your last change was worth, in watts and in hours per charge; what each
mode has cost, ranked, with the span the recording covers; the draw and charge charts; the
apps using the most power; and the cumulative per-process tally across every session.

**Advanced** - every individual power setting with what it does and what it costs, plus the
full list of what is worth changing.

**Battery health** - how much charge the pack still holds against what it shipped with, and
the one honest thing there is to say about wear: nothing in this app or in Windows gives
back capacity that is already gone.

**Diagnostics** - the GPU watch, the activity log, and **Run as admin** for the few settings
that need it.

Every figure on every page is measured on your machine. Nothing is predicted, and where
there is not enough recorded yet it says so instead of guessing.

## Layout

    control-battery\
      PowerDial.exe             the app - run this. One self-contained file.
      PowerDial.slnx            solution: both projects
      global.json               pins the .NET SDK
      Directory.Build.props     build settings shared by both projects
      Directory.Packages.props  package versions, centrally
      README.md                 this file
      CLAUDE.md                 the working brief for changing it
      scripts\publish.ps1       build, replace PowerDial.exe, start it
      scripts\sign.ps1          Authenticode signing
      src\PowerDial\            source
      src\PowerDial-selftest\   read-only checks against the live machine

`bin\` and `obj\` are throwaway and git-ignored. They used to be committed - if you are
looking at an old clone, `git rm -r --cached` them.

## Building

Needs the **.NET 10 SDK**. `global.json` pins it to 10.0.400 and rolls forward within that
feature band, so a newer 10.0.4xx SDK is fine and a .NET 8 or 9 SDK is refused with a clear
message rather than a strange build error.

    .\scripts\publish.ps1

That is the whole thing, from any clone on any Windows PC. It builds, replaces
`PowerDial.exe` at the repo root, prints the new timestamp, and starts the app. It also
stops a running copy first, since a running one locks the exe and its single-instance mutex
would make the new one exit silently anyway - and it tells you what to do when it cannot,
which happens whenever the running copy is elevated.

    .\scripts\publish.ps1 -SelfContained    # bundle the runtime: larger, runs without .NET
    .\scripts\publish.ps1 -NoRun            # build and replace, do not start it
    .\scripts\publish.ps1 -Configuration Debug

For a plain compile without replacing anything:

    dotnet build -c Release

**Do not publish into the repo root.** `dotnet publish -o .` fails with `CS5001: Program
does not contain a static 'Main'`. The SDK excludes its own output directory from source
globbing, and the root is an ancestor of `src\PowerDial`, so every `.cs` file gets excluded
and nothing compiles. The script publishes to `.publish\` and copies one file up.

**`dotnet build` does not update `PowerDial.exe`.** It refreshes `bin\`. The exe people
double-click is the published single file at the root, and that only changes when you
publish - so building and then running the old exe looks exactly like a change that did
nothing. Use the script.

### Signing

The portable copy is Authenticode-signed in place, so sign it *after* copying it out:

    .\scripts\sign.ps1 "$env:USERPROFILE\Desktop\PowerDial.exe"

The certificate is currently **self-signed**, which means it proves the file has not been
altered and names a publisher, but no other machine trusts that publisher - so someone you
send it to still gets the *"Windows protected your PC"* warning. Removing that needs a
certificate from a CA (DigiCert, Sectigo, SSL.com and others); an OV one earns SmartScreen
reputation over its first downloads, an EV one is trusted immediately. Since June 2023 the
private key has to live on a hardware token or cloud HSM. When you have one, nothing in the
build changes - pass its thumbprint:

    .\scripts\sign.ps1 "$env:USERPROFILE\Desktop\PowerDial.exe" -Thumbprint <thumbprint>

Verify a signed copy with `Get-AuthenticodeSignature`. While self-signed it reports
*UnknownError - terminated in a root certificate which is not trusted*: the signature is
good, the issuer is not trusted. That is the expected result, not a failure.

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

**Profiles.** Longest / Endurance / Balanced / Full speed - shown as *Max battery* /
*Battery saver* / *Balanced* / *Performance*. *Balanced* is the configuration that actually
measured **6.92 W (6h21m)** here. All of them write the **on-battery side only**; a profile
never touches plugged-in behaviour.

**Original settings.** A fifth profile, and the way back. It is how this laptop was set up
before PowerDial wrote anything: the snapshot is taken on first run, before the UI is
built, and since the app writes nothing until you touch a control, that first-run state
*is* the pre-app state. It lives in `%LOCALAPPDATA%\PowerDial\baseline.json`, written once
and never overwritten, so it survives restarts, updates and reinstalls.

None of it is hard-coded, because it cannot be - every machine's defaults differ, and a
"factory default" written into the source would be exactly the borrowed number this app
refuses to show. Choosing it restores both sides of every setting, not just the battery
side, and puts back whether each value was stored in the scheme or inherited from a Windows
default. That is why it calls the restore rather than writing the values like an ordinary
profile would.

Because it is a profile with its own history code, minutes recorded while you are on it
group under it like any other mode - which is what makes *Compared with your original
settings* on Overview, and its row in the mode ranking, possible at all. It starts empty on
an install that predates it: history recorded before the profile existed filed those
minutes as a custom mix, and that cannot be backfilled honestly.

Restoring a value that was previously *inherited* rather than stored makes it explicit.
The behaviour is identical, only the bookkeeping differs, and the activity log says so
rather than glossing over it.

**Settings, split by whether they matter.** The main list holds the five that change how
much power you draw while using the machine. Timeouts and lid behaviour - which change
nothing while you are actually working - are tucked into a collapsed section rather than
removed.

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
- *What your last change was worth* - the average measured before the change against the
  average measured since, in watts and in hours per charge.
- *What each mode has cost you* - every profile ranked by what it actually drew here, with
  the recorded minutes behind each and the span they were gathered over.

**Battery health**, on its own page: how much charge the pack still holds against what it
shipped with, and what the missing capacity costs you in hours at the present draw. It is a
fact about the hardware rather than a record of your usage, it changes over months rather
than minutes, and nothing in the app can act on it - so it says that plainly instead of
implying a setting somewhere could undo wear.

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

Dark instrument panel rather than a settings dialog. Bahnschrift Condensed - Windows' DIN
derivative, an engineering face - carries anything numeric; Segoe UI Variable carries the
prose; Cascadia Mono the log.

**A sidebar, and nothing else that navigates.** It replaced two strips that looked alike
and did different things: a Basic/Advanced switch that decided what *existed*, and a
section bar three hundred pixels below it that only scrolled. One list now, and every
section is one click from every other. The current entry carries a bar and a heavier label
as well as a colour, so "you are here" never rests on colour alone.

**Three accents, each meaning one thing.** *Amber is energy leaving* - draw figures, the
thirstiest mode's bar, the capacity a worn pack has lost. *Green is energy kept* - a
measured saving, the mode in use, the cheapest mode recorded. *Yellow says where you are* -
the page title and the current sidebar entry, and nothing else. Green was doing double duty
as "selected" until the yellow arrived, which spent an accent that should only ever mean a
saving.

**Nothing is pinned above the column.** A header instrument and a processes strip used to
be, costing about 300 px of permanent chrome on every page, including the ones that had
nothing to do with either - and between them they stated the charge, the draw and the time
left twice on the same screen. Overview says all of it once, and the full process ranking
lives on Insights.

**Measurements name their own provenance.** A figure is never shown without the recorded
minutes behind it, and Insights states the span those minutes are drawn from - sixteen
minutes gathered this afternoon and sixteen gathered over three weeks are worth different
amounts of trust. Processes are ranked in core-seconds and never watts-per-process:
splitting a measured total by CPU share would be a made-up number, and it would point at
the wrong thing, since whatever is holding a discrete GPU awake costs upwards of 17 W while
using almost no CPU.

Every interactive control is custom-drawn, because stock Win32 widgets cannot be made to
look like this and, in the case of `TrackBar`, actively misbehave: a focused one swallows
the mouse wheel and moves its own thumb, so scrolling the window would rewrite a power
setting. Every control here refuses the wheel. Everything is keyboard-reachable and names
itself to a screen reader, including the painted cards, which are otherwise silent.

## Files

| File | |
|---|---|
| `Machine.cs` | hardware detection: battery, GPUs, form factor, capabilities |
| `Config.cs` | per-machine measured values, persisted |
| `PowerCfg.cs` | the settings, registry reads, `powercfg` writes, info text |
| `Battery.cs` | WMI battery sampling and the watts calculation |
| `GpuWatch.cs` | discrete-GPU waker detection |
| `Baseline.cs` | the restore point |
| `Presets.cs` | the four designed profiles, and Original settings read from the restore point |
| `Advisor.cs` | the suggestions: what is wrong right now, and what fixes it |
| `Charts.cs` | the charts and the process table |
| `ProcessWatch.cs` | live per-process CPU and memory sampling |
| `History.cs` | the on-disk record and the cumulative per-process tally |
| `Json.cs` | source-generated JSON serialisers for everything persisted |
| `Diag.cs` | where a swallowed write failure goes so the window can report it |
| `Runtime.cs` | the measured average draw, and the runtime it implies |
| `Theme.cs` | palette, type, drawing helpers |
| `Widgets.cs` | Card, PillButton, Slider, Picker, InfoDot, SectionToggle, Sparkline, SteadyPanel |
| `Shell.cs` | the sidebar and the battery summary under it |
| `Pages.cs` | the page cards: status, advice, mode summary, top apps, saving, mode cost, original-vs-now |
| `MainForm.cs` | the window, the pages, and everything that refreshes them |

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
