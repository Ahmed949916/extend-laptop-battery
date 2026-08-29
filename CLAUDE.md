# control-battery — PowerDial

A WinForms tray app for controlling the power settings that affect battery life, plus live
process telemetry and a persistent record of what the machine has been doing.

It runs on **any Windows 10/11 PC** - gaming laptop, ultrabook or desktop. It began life
tuned to one laptop (HP OMEN 16-c0xxx); everything specific to that machine has been
replaced by detection or by per-machine measurement. Do not put hardware facts back into
the code.

`README.md` has the full narrative. This file is the working brief.

## Build, run, test

    PowerDial.exe                                       run it

    dotnet build -c Release                             from the repo root: both projects

    cd src\PowerDial
    dotnet publish -c Release -o %TEMP%\pd-publish      then copy over the runnable copy
    copy /y %TEMP%\pd-publish\* ..\..

    cd src\PowerDial-selftest && dotnet run -c Release   read-only, checks the live machine

The portable copy - one self-contained .exe that runs on a PC with no .NET installed. This
is what gets sent to someone; it is **not** rebuilt by the commands above and goes stale
silently if you forget it:

    cd src\PowerDial
    dotnet publish -c Release -r win-x64 --self-contained true -o %TEMP%\pd-portable ^
      -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
    copy /y %TEMP%\pd-portable\PowerDial.exe %USERPROFILE%\Desktop\

**Releasing, in order.** Skipping a step here is how the runnable copy and the source end
up disagreeing: build → run the selftest → quit the running copy → publish and copy in →
rebuild the portable exe → launch it and look at the window.

.NET 10 SDK, pinned by `global.json` to 10.0.400 with `rollForward: latestFeature`.
`net10.0-windows`, `UseWindowsForms`. `msbuild` is not on PATH — use `dotnet build`.

**Build settings live at the repo root, not in the .csproj files.** `Directory.Build.props`
holds the target framework, the plain-C# switches, the analyzer settings and the whole
`NoWarn` list with the reasoning for each suppression; `Directory.Packages.props` holds the
one package version centrally. Both .csproj files used to carry their own copy of all of
it, and since the selftest compiles the app's own code, the two drifting apart meant
building the same source under different rules. `PowerDial.slnx` ties them together — build
from the root and both are covered.

**Warnings are errors.** The build is clean, so a new warning is news rather than noise.
Suppressions go in the `NoWarn` list at the root, with a comment saying why — never a
`#pragma` at the call site.

**The selftest references the app project.** It used to list the app's source files
one by one, which went stale silently every time the app gained a file. It enables
`UseWindowsForms` only because the app it references is a WinExe.

**Run the selftest after any change.** It is read-only and takes about half a minute.

What it actually covers, so you know what is not protected: every knob reads back and is in
range; the lid and sleep settings are not set to something that flattens the pack in a bag;
the advisor scan changes nothing; every suggestion is ranked, has a legal target, has a
button, explains itself in 80+ characters, and quotes no unmeasured saving; every profile
references only knobs that exist; and the live battery window fills and resets. It does
**not** touch the window, the widgets, the tray, persistence or the restore point.

**Quit the running copy first** if anything writes `offenders.json`. Two processes each
save from their own in-memory tally, so whichever writes last wins and the file can come
out smaller than it went in.

**There is no UI test any more.** It drove the real form and asserted its structure, and it
was removed to cut maintenance: every feature change meant updating it. Nothing now checks
the window builds, so build and open the app after touching `MainForm` or `Widgets`.

**Never publish straight into the repo root.** `dotnet publish -o ..\..` used to work and now
fails with `CS5001: Program does not contain a static 'Main'`, which is a genuinely
confusing way to say what went wrong. The SDK's default compile glob excludes everything
under `$(PublishDir)`; point that at the repo root and the exclusion swallows the whole
repository, `Program.cs` included, so the compiler is handed zero source files. `dotnet
build` is unaffected because it never sets `PublishDir`. Publish to a directory outside the
project and copy the payload in — that is what the two commands above do.

## Changing things

Where each kind of change goes, and the one thing about each that is easy to get wrong.

**A power setting** — add a `Knob` to the array in `PowerCfg.cs`. It needs `Key` (short id
that presets reference), `SubGroup` + `Guid` (the Windows setting), and either `Min`/`Max`
/`Unit` for a slider or `Choices` for a picker. `Info` is required by convention - nothing
checks it, so it is on you. Set `Basic = true` for timeouts and lid behaviour, which collapse under
*Basic settings*; leave it false for anything that changes draw while you are using the
machine. `PowerCfg.Exists` drops it automatically on a PC where Windows does not define it,
so never guard it by hand.

**A suggestion** — add a check to `Advisor.cs`. It must read the live value and return
nothing when the setting is already sensible; a list that always has twelve items is one
nobody reads. Leave `Gain` null unless you have a figure measured on this PC. Use
`FixKind.Advisory` when there is no setting to write, which opens the relevant Windows page
instead of pretending there is a button.

**A profile** — add a `Preset` to `Presets.cs`: a name, a blurb, and a dictionary of knob
key to battery-side value. Every key must exist in `PowerCfg`; the selftest checks that.

**A tab** — the strip is built in `BuildNav`, from two parallel arrays: `names` are section
headings registered by `Heading`/`WithInfo`, `labels` are what is drawn. A name that does
not match a registered heading is skipped silently, so check the spelling. Every section
stays on the one page — tabs scroll, they do not switch pages.

**A persisted field** — add it to the class in `History.cs`, `Config.cs` or `Baseline.cs`,
then make sure the type is listed in `Json.cs`. Serialisation is source-generated, so a new
*type* needs a `[JsonSerializable]` line; a new field on a type already listed needs
nothing. Keep the JSON short — `HistPoint` writes one line a minute forever, which is why
its property names are `T`, `W`, `Pct`, and why `When` is `[JsonIgnore]`.

**A colour or a font** — `Theme.cs`, and read the note there first. Amber is energy leaving,
green is energy kept; `Raise` and `Hair` are neutral steps for surfaces, not accents. Do not
add a third colour that carries meaning.

**A widget** — `Widgets.cs`. It must refuse the mouse wheel (a focused `TrackBar` silently
rewrites a system setting when someone means to scroll), and any public property on a
`Control` needs `[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]`
or the build fails on WFO1000.

**A build setting** — `Directory.Build.props` at the root, not the .csproj files. A package
version goes in `Directory.Packages.props`; add packages with `dotnet add package`, never by
editing the XML.

## Hard invariants — do not break these

1. **The DC (on-battery) side is the default, and the only side anything writes by
   itself.** The advisor, the three profiles and the restore point are all about battery
   life and call `PowerCfg.WriteDc` exclusively — none of them may ever touch AC. The one
   exception is deliberate and user-driven: the *Plugged in* switch in the settings
   section, which routes that section's edits through `PowerCfg.WriteAc`. It is opt-in,
   resets to battery on every launch, and says on screen which side it is writing.
   Because the app can now change AC, **the restore point captures both sides** — a
   snapshot covering only battery would silently fail to undo half of what the app can do.
   Snapshots taken before that existed have `AcValue = null` and restore battery only,
   saying so rather than guessing.
2. **Read from the registry, write with `powercfg`.** `powercfg /q` refuses to display
   hidden settings — it returns just a scheme header for the lid action — so reads go to
   `HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\<scheme>\<sub>\<setting>`,
   falling back to `DefaultPowerSchemeValues`. Four of the ten settings are hidden.
3. **Verify every write by reading it back.** If it disagrees, say so in the Activity log.
   Never report success you have not confirmed.
4. **Nothing is written just by launching the app.** The restore point is captured on first
   run and depends on this. Nothing tests it any more - the selftest proves only that
   *scanning* writes nothing. Check by hand if you touch startup.
5. **Elevation is `asInvoker` on purpose.** EPP succeeds unelevated here.
   Anything needing admin reports it and offers *Run as admin*.

## Nothing about the hardware is hardcoded

`Machine.cs` detects it; `Config.cs` stores what has to be measured. The rule: **a value
that varies by hardware is either detected or measured, never assumed.** Where neither has
happened the interface says "not measured on this PC" rather than showing a borrowed number.

| Fact | Where it comes from |
|---|---|
| Battery present, form factor, vendor, model | `Machine.Detect` via WMI + chassis type |
| GPUs, vendor, discrete vs integrated, hybrid | `Win32_VideoController` + PNP vendor id |
| Which power settings exist | `PowerCfg.Exists` — the registry settings store |
| **Original design capacity** | see below |
| Discrete GPU wake cost | `Config` — measured, null until then |

**Design capacity** is the interesting one. Several vendors rewrite the live
design-capacity field to equal the learned capacity, which hides all degradation — on the
original test laptop every live source reported 43,905 mWh for a pack that shipped at
70,562. So the chain is: `Win32_Battery` → `BatteryStaticData` → **the highest capacity
ever recorded in `powercfg /batteryreport /xml`**, which recovers the true figure. If none
of that works, health reports as unknown rather than 100%.

**How draw is measured:** many machines never report an instantaneous rate — the ACPI
`DischargeRate` field returns an invalid sentinel. Draw is therefore timed from
`RemainingCapacity` over a 60-second window. Consequences: the first reading takes 60 s;
readings are meaningless on AC; every setting change must reset the window; and
`nvidia-smi` **wakes the dGPU**, so never call it inside a measurement window (allow
60–90 s afterwards for RTD3).

## The suggestions section

`Advisor.cs` reads how the PC is set up right now and returns what is actually wrong with
it, ranked. `MainForm` renders one card per suggestion with a button that carries it out.

Four rules, and all four exist because breaking them makes the section worthless:

1. **Only report what is wrong.** Every check reads the live value and stays quiet when it
   is already sensible. An empty list has to mean "nothing left to do", not "not
   implemented" - a list that always has twelve items is one nobody reads.
2. **Never invent a saving.** `Suggestion.Gain` is only ever filled from something measured
   on this PC, so it is usually null and no figure is drawn. Rank carries the priority
   instead. A borrowed watt figure is worse than none, because a figure is what people act
   on.
3. **Scanning writes nothing.** The selftest snapshots every battery-side value, scans,
   and compares. The whole section is worthless if reading it changes the machine.
4. **Say so when there is no button.** Background apps and whatever is holding a discrete
   GPU awake cannot be fixed by writing a setting - those are `FixKind.Advisory` and open
   the Windows page rather than pretending. Ending such a process achieves nothing anyway;
   it relaunches via `sihost.exe`.

`Apply` writes with `PowerCfg.WriteDc`, reads straight back and compares before claiming
anything.

The scan runs on startup, once more on the first poll, and after any change - not on the
poll timer. The extra pass exists because CPU is a delta: the startup sample has memory
figures and zero CPU, so anything judging a process by what it burns was reading zeroes.

## Telemetry and persistence

`ProcessWatch` samples every process each poll, grouped by executable name. CPU is a
**delta** — `Process.TotalProcessorTime` is cumulative, so the first sample after start has
memory figures and zero CPU, and everything after is real. `TotalProcessorTime` throws
Access Denied on protected processes; that is expected and swallowed. A process group is
*background* when no instance of it owns a `MainWindowHandle`.

`History` writes one JSON line a minute to `%LOCALAPPDATA%\PowerDial\history\YYYY-MM.jsonl`:

    {"T":1787800337,"W":8.27,"Pct":79,"Ac":false,"Br":42,"Cpu":53,"BgMb":7554,
     "Top":"claude 40.6|nvcontainer 4.4|uihost 2.2"}

Roughly 150 bytes a minute, ~6 MB for a month of continuous use. Files older than
`History.KeepMonths` (3) are deleted at startup. `offenders.json` alongside it is the
cumulative per-process tally — core-seconds burned and peak memory — which is what makes
"what has actually been eating the battery" survive reboots instead of being re-guessed.

Rules that matter here:

- `HistPoint.When` is `[JsonIgnore]`. System.Text.Json serialises get-only properties by
  default, which was writing a redundant ISO timestamp on every line.
- The watts chart is driven by history (one point a minute), **not** by the 15 s poll. The
  header sparkline is the live 15 s trace. Do not mix them.
- Gaps greater than 15 minutes in the charge chart are left unjoined — that means the app
  was not running, not that the battery teleported.
- Process sampling costs about **0.2% of a core**, taking the app from 0.16% to ~0.36%.
  If that grows, sample processes less often than the battery.

**There are no modelled analytics.** A `CurveChart` predicting runtime-vs-brightness from
three hardcoded workload constants used to live in `Charts.cs`; it was removed because it
described a laptop in the abstract rather than the one in front of you. What replaced it is
`Model.WattsPerPoint`, which returns `Config.BacklightWattsPerPoint` — a value measured on
this PC or `null` — and is applied to the live reading to split the current draw, not to
predict one. Do not reintroduce modelled numbers as analytics.

**`RuntimeAverage` is not a model either, and the distinction matters.** `Runtime.cs` takes
the recorded points, keeps the ones that were on battery and actually drawing, and divides
the capacity the pack holds *now* by the mean of those measured watts. Every input is a
measurement from this machine; there is no workload constant and nothing is extrapolated.
It exists because the 60-second reading answers "what am I drawing this minute", which
swings by several watts as the CPU breathes, while the question people actually have is
"how long does this thing last". Two rules keep it honest:

- **`Minutes` travels with the figure and is shown.** An average over four recorded minutes
  is not a runtime estimate, and the header says what it is standing on.
- **The spread is the 10th and 90th percentile, not min and max.** One spiky minute should
  not become "your best case". `Low`/`High` are deliberately robust, and when nothing has
  been recorded on battery the band reads *not measured yet* rather than showing zero.

## Portability rules

- **Never reintroduce a hardware constant.** If a number varies by machine it belongs in
  `Config`, measured, or it does not get shown.
- **Guard everything by capability.** No battery means the draw, charge and health panels
  stand down. No discrete GPU means the GPU watch says so instead of inventing work. A
  setting Windows does not define on this PC is dropped, not shown dead.
- **A desktop is a supported target**, not an edge case. It has no battery and its GPU
  cannot be powered down, so most of the battery machinery is inert - but the settings,
  process telemetry, history and GPU listing all still apply.
- Vendor detection in `GpuWatch` covers HP, ASUS, Lenovo, MSI, Acer, Dell/Alienware plus
  Razer, Corsair, Logitech and SteelSeries. Add to the tables; do not special-case.

## The thing that actually matters

On a hybrid laptop, **what wakes the discrete GPU matters far more than what uses the
CPU.** An awake but idle discrete GPU can draw anywhere from about 5 W to over 20 W while
reporting 0% utilisation, which is why it hides so well in Task Manager.

The original test machine is the worked example: three separate things were each holding an
RTX 3050 Ti awake for roughly 17 W — NVIDIA Instant Replay, OMEN Gaming Hub and OMEN Light
Studio — and none looked expensive. Two of them relaunched at every boot as **packaged
background tasks via `sihost.exe`**, so disabling their scheduled tasks achieved nothing;
the only fix that held was the per-app **Background apps permission → Never**, which a
vendor update can silently switch back on. That is what the GPU watch exists to catch.

Two things follow, and both are baked into the code:

- The cost is the GPU wake cost **once**, not the sum over offenders — any single one is
  enough to hold it awake. An earlier version summed them and badly overstated the total.
- Never reason about power from CPU%. A/B a drain measurement instead.

## WinForms traps already hit here — do not reintroduce

- **A focused `TrackBar` swallows the mouse wheel** and moves its own thumb, silently
  rewriting a system setting when the user meant to scroll. Every control refuses the
  wheel. Use `Slider` / `Picker`, never stock `TrackBar` / `ComboBox`. Nothing enforces this
  since the UI test was removed, so it is on review.
- **Committing on `MouseUp` loses wheel and keyboard changes**, so the UI drifted out of
  sync with the registry (slider showed 90, registry held 80). Commits are debounced 600 ms
  after *any* value change.
- **`AutoScroll` panels call `ScrollToControl` on every focus change**, which kept scrolling
  the header out of view. `SteadyPanel` overrides it.
- **`BeginInvoke` in a Form constructor throws** — the handle does not exist yet. Do
  window-position work in `OnShown`.
- **An `AutoSize` Card wrapping a docked `AutoSize` panel** made the form open full-screen
  and paint a section twice. Prefer explicit heights.
- **Constructing a `ManagementObjectSearcher` per poll cost 3.75% of a CPU core.** Build
  once, reuse. Poll every 15 s against the 60 s window. Now ~0.16%.
- **`"0.1"` is not a one-decimal format string** in .NET — only `0` and `#` are digit
  placeholders. It rendered 43.905 as "441". Use `"0.0"`.
- **Docking a Fill panel next to a Top panel depends on z-order**, which is easy to get
  backwards and silently paints one over the other. The header and the scroll column use
  explicit bounds plus `Anchor` instead, so the order they are added to `Controls` decides
  nothing about layout. It still decides `Controls[0]`, which the removed UI test read as
  the scroll column — harmless now, but keep adding **`_root` to the form first** anyway.
- **A `FlowLayoutPanel` ignores `Anchor` on its own children.** Cards are therefore given a
  width explicitly by `Relayout`; the controls *inside* each card are anchored and follow on
  their own. Buttons are skipped — stretching *Run as admin* across the column made it look
  like the primary action.
- **Setting `AutoScrollPosition` in code raises neither `Scroll` nor a wheel event**, and
  the stock `Scroll` event does not fire for the wheel either. Anything that tracks the view
  must watch the *position*, not the cause — which is what `SteadyPanel.CheckMoved` does.
  `Scrolled` is raised from `OnScroll`, `OnMouseWheel` and `OnPaint`, and the position
  compare makes calling it from anywhere free.
- **`OnShown` parks the column at the top 60 ms after `Show`** so the header cannot open
  below the fold. Anything that scrolls the column programmatically within that window gets
  yanked back to the top — wait ~200 ms first. This is what made the old UI test's
  section-bar checks pass or fail on timing.
- **A child control paints over its parent.** The section strip's baseline rule is drawn by
  the bar, so the tabs are two pixels shorter than it; drawn at equal height the rule hid
  behind them and showed through only in the gaps, as a row of dashes.
- **A tray app that only hides on close needs a way back in.** Closing the window hides it,
  so the shortcut is what people reach for next — and the single-instance guard used to
  answer with a message box telling them to look in the notification area, which is a dead
  end when the icon is in the overflow. A second launch now sets the named event
  `MainForm.WakeEvent` and exits; the running copy waits on it and shows itself. The wait is
  on a background thread — `WaitOne` blocks — and marshals back with `BeginInvoke`, started
  from `OnShown` because in the constructor there is no handle yet. The first hide also
  balloons once to say where the window went and how to quit for good.

## The window

One scrolling column, about three screens long, with three things pinned above it in
`_chrome`, in this order: the header instrument, the *Where your watts go* strip, and the
bar of section buttons.

- **The header does not scroll.** Draw, what is left, charge, health and the measured
  average are why the app is open; they used to leave the screen within one flick of the
  wheel, so you could not see the effect of the control you had just moved.
- **Nor does *Where your watts go*.** It sits directly under the header because the two
  describe the same measurement window and are meant to be read together; as a card a screen
  and a half down the column it was out of sight exactly when it mattered. Pinning it costs
  ~124px of permanent chrome, which is why it is denser than the card was: the measured
  total sits on the title row, the note is one line, and it lists four processes not five.
  **It still shows core-seconds and never watts per process** - see the suggestions rules.
  The bar is scaled against the busiest process, not the total, because a share-of-total bar
  would read as a share of the watts. `bg` marks a process that owns no window.
- **The section bar only moves you.** It scrolls the column — nothing is hidden behind a
  tab. That is partly principle and partly the test suite: it toggles *Basic settings* and
  asserts `Visible` flips, and `Control.Visible` is false whenever an ancestor is hidden, so
  putting sections on separate pages would break it. Keep every section on the one page.
- **The current tab follows the scroll**, both ways, and clicking one scrolls there. Tests
  cover all of it. `MarkNav` picks the last section whose top has passed the viewport top.
- **The active thing is outlined in green** - the section tab, the battery/plugged-in
  switch, the profile buttons and the process-list sort toggles all share one marker, so
  "you are here" reads the same everywhere. `PillButton.Tab` renders a tab strip: `Dim`
  until hovered or current, then `Raise` plus a green underline on the bar's baseline - the
  strip carries no outline, only the rule under the current tab; `Selected` on ordinary
  buttons is `Raise` plus a green border. This was
  once deliberately neutral, on the grounds that amber and green mean energy leaving and
  energy kept and should not be spent on decoration - it was changed on request because
  nothing else marked the current tab clearly enough. The cost is real, so **hover must not
  also be green**: it uses `Hair`, or hover and active look identical.
- **Section headings are a rule, a gap, then a 16px title.** The gap above is much larger
  than the gap below — that is what attaches a heading to the cards under it. As a 13px
  inline label it read as one more line of text floating between two sections.
- **`Relayout` stretches the design width to the window** and caps the column at `WMax`; a
  chart three thousand pixels wide is wider, not clearer. Everything is still built once at
  `W`, so all the geometry arithmetic stays in one place.

- **Work shows on the button that started it, not on a bar elsewhere.** `PillButton.Busy`
  spins an arc where the label goes; `BeginBusy`/`BusyStep`/`EndBusy` drive it and
  `RunBusy(btn, work)` wraps any single blocking call. Where the work is a countable list of
  writes the count rides beside the arc, so applying a profile still says how far along it
  is. The column is disabled throughout - queued clicks onto a half-written scheme are how
  you get settings nobody asked for. **`EndBusy` must null-check and `IsDisposed`-check the
  button**: `RefreshFixes` rebuilds the suggestion cards, so the button that started the
  work is often gone by the time it finishes.

## Editing notes

- **JSON goes through `Json.cs`.** `PrettyJson` and `CompactJson` are source-generated
  contexts — no reflection on the startup or once-a-minute paths, and the on-disk shape is
  identical to what reflection produced. Adding a persisted type means adding a
  `[JsonSerializable]` line, not a `JsonSerializer.Serialize<T>` call.
- **A swallowed write failure goes to `Diag`.** Persistence catches everything on purpose —
  a full or locked disk must not take a tray app down — but silence meant the tally could
  stop saving and nothing said so. `Diag.WriteFailed` records the reason, the poll drains it
  into the Activity log, and each distinct reason is reported once. Reads are deliberately
  not routed there: a missing file on first run is normal, not a fault.
- Plain C#: `ImplicitUsings` and `Nullable` are **disabled**. No file-scoped namespaces, no
  target-typed `new`, no `?.` on the WinForms tree where the old style is used.
- Writing C# via a bash heredoc in this environment mangles apostrophes and backslashes —
  a trailing `\` before a newline gets eaten as a line continuation, and `\\n` collapses to
  a real newline inside string literals. Use the file-writing tool for `.cs` files.
- `Theme.cs` owns the palette and type. Two accents carry meaning: **amber is energy
  leaving, green is energy kept.** Do not add a third decorative colour. `Raise` and `Hair`
  are two more steps up the same neutral ramp as Ink/Panel/Inset, not accents — they exist
  so "this surface is raised" and "this tab is current" can be said without spending amber
  or green on decoration.
- Settings are split by `Knob.Basic`: `false` = changes draw while in use (shown), `true` =
  timeouts and lid behaviour (collapsed under *Basic settings*).
- Every `Knob` needs an `Info` string. Nothing enforces that - the 80-character check in the
  selftest is on `Suggestion.Info`, not on knobs.

## Known open item

The restore point captured `sleepidle = 0` — **sleep after: never** on battery. That
regressed outside this app, and the snapshot at
`%LOCALAPPDATA%\PowerDial\baseline.json` now holds it as the "good" state. Fixing it means
setting it back to 600 s and deleting the snapshot so it re-captures. Not yet approved by
the user.
