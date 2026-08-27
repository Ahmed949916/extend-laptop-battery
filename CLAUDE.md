# control-battery — PowerDial

A WinForms tray app for controlling the power settings that affect battery life on **one
specific laptop**: HP OMEN 16-c0xxx, Ryzen 7 5800H + RTX 3050 Ti, Windows 11 25H2.

`README.md` has the full narrative. This file is the working brief.

## Build, run, test

    PowerDial.exe                                       run it

    cd src\PowerDial
    dotnet build -c Release
    dotnet publish -c Release -o ..\..                  publish over the runnable copy

    cd src\PowerDial-selftest && dotnet run -c Release   read-only, checks the live machine
    cd src\PowerDial-uitest   && dotnet run -c Release   builds the real form, 34 checks

.NET 6 SDK (6.0.428), `net6.0-windows`, `UseWindowsForms`. NuGet works; only dependency is
`System.Management`. `msbuild` is not on PATH — use `dotnet build`.

Set `PD_SHOTS` to a directory and the UI test renders the collapsed and expanded window to
PNGs. Note `DrawToBitmap` does not render plain `Label` children, so slider value captions
are missing from those images — that is the capture, not the app.

**Run both suites after any change.** They are read-only and take about a minute.

## Hard invariants — do not break these

1. **Only the DC (on-battery) side is ever written.** No code path may touch AC values.
2. **Read from the registry, write with `powercfg`.** `powercfg /q` refuses to display
   hidden settings — it returns just a scheme header for the lid action — so reads go to
   `HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\<scheme>\<sub>\<setting>`,
   falling back to `DefaultPowerSchemeValues`. Four of the ten settings are hidden.
3. **Verify every write by reading it back.** If it disagrees, say so in the Activity log.
   Never report success you have not confirmed.
4. **Nothing is written just by launching the app.** The restore point is captured on first
   run and depends on this. There is a test for it.
5. **Elevation is `asInvoker` on purpose.** EPP and brightness succeed unelevated here.
   Anything needing admin reports it and offers *Run as admin*.

## Measured constants — these came from this machine, not documentation

| | |
|---|---|
| Backlight | **0.04 W per brightness point** (~4 W across the range) |
| Idle base (reading, PDFs) | **5.72 W** at 0% backlight |
| Browsing / chat / docs base | **7.44 W** |
| Video / active dev base | **10.13 W** |
| Usable capacity | **43,905 mWh** |
| Original design capacity | **70,562 mWh** → **62% health** |
| Awake-but-idle RTX 3050 Ti | **~17 W** |

Sanity checks the UI test asserts: 30% idle → 6.92 W → 6h21m; 70% active → 12.93 W.

**How draw is measured:** HP firmware never reports an instantaneous rate — ACPI
`DischargeRate` returns an invalid sentinel. Draw is timed from `RemainingCapacity` over a
60-second window. Consequences: the first reading takes 60 s; readings are meaningless on
AC; every setting change must reset the window; and `nvidia-smi` **wakes the dGPU**, so
never call it inside a measurement window (allow 60–90 s afterwards for RTD3).

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
described a laptop in the abstract rather than this one now. The only surviving constant is
`Model.WattsPerPoint` (0.04 W), which is a measurement applied to the live reading to split
the current draw — not a prediction. Do not reintroduce modelled numbers as analytics.

## The thing that actually matters

On this muxless hybrid laptop, **what wakes the discrete GPU matters far more than what
uses the CPU.** Three separate things were caught doing it and none looked expensive in
Task Manager:

| | |
|---|---|
| NVIDIA Instant Replay / overlay | ~11.8 W |
| OMEN Command Center background | ~11.1 W |
| OMEN Light Studio background (the RGB engine) | ~12.0 W |

The two OMEN ones relaunch at every boot as **packaged background tasks via `sihost.exe`**,
so disabling their scheduled tasks achieves nothing. The only fix that holds is the per-app
**Background apps permission → Never**, which an OMEN update can silently flip back on.
That is what the GPU watch panel exists to catch — an 11 W silent regression.

Corollary: never reason about power from CPU% . A/B a drain measurement instead.

## WinForms traps already hit here — do not reintroduce

- **A focused `TrackBar` swallows the mouse wheel** and moves its own thumb, silently
  rewriting a system setting when the user meant to scroll. Every control refuses the
  wheel. Use `Slider` / `Picker`, never stock `TrackBar` / `ComboBox`. A test enforces this.
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

## Editing notes

- Plain C#: `ImplicitUsings` and `Nullable` are **disabled**. No file-scoped namespaces, no
  target-typed `new`, no `?.` on the WinForms tree where the old style is used.
- Writing C# via a bash heredoc in this environment mangles apostrophes and backslashes —
  a trailing `\` before a newline gets eaten as a line continuation, and `\\n` collapses to
  a real newline inside string literals. Use the file-writing tool for `.cs` files.
- `Theme.cs` owns the palette and type. Two accents carry meaning: **amber is energy
  leaving, green is energy kept.** Do not add a third decorative colour.
- Settings are split by `Knob.Basic`: `false` = changes draw while in use (shown), `true` =
  timeouts and lid behaviour (collapsed under *Basic settings*).
- Every `Knob` needs an `Info` string; a test fails if one is missing or under 80 chars.

## Known open item

The restore point captured `sleepidle = 0` — **sleep after: never** on battery. That
regressed outside this app, and the snapshot at
`%LOCALAPPDATA%\PowerDial\baseline.json` now holds it as the "good" state. Fixing it means
setting it back to 600 s and deleting the snapshot so it re-captures. Not yet approved by
the user.
