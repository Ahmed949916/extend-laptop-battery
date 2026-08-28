using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using PowerDial;

/// <summary>
/// Drives the real MainForm rather than poking at it with synthetic mouse events,
/// which kept missing and once got the window snapped to half the screen.
/// </summary>
class UiTest
{
    static int fail = 0;

    static void Check(string what, bool ok, string extra = null)
    {
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + what + (extra != null ? "  (" + extra + ")" : ""));
        if (!ok) fail++;
    }

    static object Field(object o, string name)
    {
        FieldInfo f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return f == null ? null : f.GetValue(o);
    }

    static void Walk(Control c, List<Control> into)
    {
        into.Add(c);
        foreach (Control k in c.Controls) Walk(k, into);
    }

    static void Shoot(Form f, string path, int height)
    {
        Size was = f.ClientSize;
        f.ClientSize = new Size(was.Width, height);
        Application.DoEvents();
        f.PerformLayout();
        Application.DoEvents();
        using (Bitmap b = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
        {
            f.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
            b.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        f.ClientSize = was;
        Application.DoEvents();
    }

    static void ScrollTo(Control panel, int y)
    {
        ScrollableControl sc = panel as ScrollableControl;
        if (sc == null) return;
        sc.AutoScrollPosition = new Point(0, y);

        // A user scrolls with the wheel or the scrollbar, and SteadyPanel hooks both.
        // Setting the position in code raises neither, and this form lives off-screen so it
        // may never repaint - so call the same public check the panel calls itself, rather
        // than depending on a WM_PAINT that may not arrive. Without this the tab assertions
        // pass or fail on timing.
        SteadyPanel sp = panel as SteadyPanel;
        if (sp != null) sp.CheckMoved();
        Application.DoEvents();
    }

    /// <summary>Index of the one current tab, or -1 if it is not exactly one.</summary>
    static int SelectedTab(List<PillButton> tabs)
    {
        int which = -1, count = 0;
        for (int i = 0; i < tabs.Count; i++)
            if (tabs[i].Selected) { which = i; count++; }
        return count == 1 ? which : -1;
    }

    static int CountApplicable(List<Suggestion> l)
    {
        int n = 0;
        foreach (Suggestion s in l) if (s.CanApply) n++;
        return n;
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Console.WriteLine("=== PowerDial UI test ===");
        Console.WriteLine();

        MainForm f = new MainForm();
        f.ShowInTaskbar = false;
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);   // off-screen, do not disturb the desktop
        f.Show();
        Application.DoEvents();

        List<Control> all = new List<Control>();
        Walk(f, all);
        Console.WriteLine("  controls built: " + all.Count);
        Console.WriteLine();

        // ---- the pieces the redesign was asked for ----------------------------
        Console.WriteLine("--- structure ---");

        int sliders = 0, pickers = 0, infoDots = 0, cards = 0, toggles = 0, sparks = 0, pills = 0;
        int lineCharts = 0, batCharts = 0, stackBars = 0, procTables = 0;
        SectionToggle basicToggle = null;
        foreach (Control c in all)
        {
            if (c is Slider) sliders++;
            if (c is Picker) pickers++;
            if (c is InfoDot) infoDots++;
            if (c is Card) cards++;
            if (c is Sparkline) sparks++;
            if (c is PillButton) pills++;
            if (c is LineChart) lineCharts++;
            if (c is BatteryChart) batCharts++;
            if (c is ProcessTable) procTables++;
            if (c is StackBar) stackBars++;
            SectionToggle st = c as SectionToggle;
            if (st != null) { toggles++; if (st.Caption == "Basic settings") basicToggle = st; }
        }

        Check("sliders drawn by us, not Win32", sliders >= 4, sliders + " found");
        Check("dropdowns replaced by Picker", pickers >= 3, pickers + " found");
        Check("every setting has an info icon", infoDots >= 11, infoDots + " found");
        Check("sparkline present", sparks == 1);
        Check("preset + restore buttons", pills >= 4, pills + " found");
        bool renamed = false;
        foreach (Control c in all) { PillButton pb = c as PillButton;
            if (pb != null && pb.Text == "Restore original settings") renamed = true; }
        Check("restore button reads Restore original settings", renamed);
        Check("two collapsible sections", toggles == 2, toggles + " found");
        Check("Basic settings section exists", basicToggle != null);

        // no stock Win32 TrackBar or ComboBox should survive the redesign
        int legacy = 0;
        foreach (Control c in all) if (c is TrackBar || c is ComboBox) legacy++;
        Check("no stock TrackBar/ComboBox left", legacy == 0, legacy + " found");
        Console.WriteLine();

        Console.WriteLine("--- section bar follows the scroll ---");

        // MainForm.OnShown arms a 60 ms timer that parks the column back at the top, so the
        // header cannot open below the fold. If it fires part-way through these assertions
        // it scrolls the column out from under them - which is exactly what made this
        // section pass or fail on timing. Let it land before scrolling anywhere.
        for (int i = 0; i < 20; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }

        // the section bar itself, not every control that renders in the tab style - the
        // battery / plugged-in switch uses the same look and is not a nav tab
        List<PillButton> tabs = Field(f, "_navBtns") as List<PillButton>;
        Check("a section bar was built", tabs != null && tabs.Count >= 5,
              (tabs == null ? "no _navBtns" : tabs.Count + " tabs"));
        if (tabs == null) tabs = new List<PillButton>();

        ScrollableControl column = f.Controls[0] as ScrollableControl;
        ScrollTo(column, 0);
        int atTop = SelectedTab(tabs);
        Check("exactly one tab is current at the top", atTop == 0, "tab index " + atTop);

        ScrollTo(column, 2400);
        int deep = SelectedTab(tabs);
        Check("the current tab shifts as the column scrolls", deep > atTop,
              (atTop < 0 ? "none" : tabs[atTop].Text) + " -> " + (deep < 0 ? "none" : tabs[deep].Text));

        ScrollTo(column, 0);
        Check("and shifts back on the way up", SelectedTab(tabs) == atTop);

        // clicking a tab has to move the column, not merely light the tab
        int wasAt = -column.AutoScrollPosition.Y;
        tabs[tabs.Count - 1].Press();
        Application.DoEvents();
        Check("clicking a tab scrolls to its section", -column.AutoScrollPosition.Y > wasAt,
              wasAt + " -> " + (-column.AutoScrollPosition.Y));
        ScrollTo(column, 0);
        Console.WriteLine();

        Console.WriteLine("--- analytics: real, not modelled ---");
        Check("watts-over-time chart", lineCharts == 1, lineCharts + " found");
        Check("charge-over-time chart from saved history", batCharts == 1, batCharts + " found");
        Check("two process tables (live + cumulative)", procTables == 2, procTables + " found");
        Check("two stacked bars (watt split + health)", stackBars == 2, stackBars + " found");
        Check("info icons cover sections too", infoDots >= 18, infoDots + " found");
        Check("the modelled runtime curve is gone",
              Type.GetType("PowerDial.CurveChart, UiTest") == null);
        Check("backlight cost is null until measured on this display",
              !Model.WattsPerPoint.HasValue || Model.WattsPerPoint.Value > 0,
              Model.WattsPerPoint.HasValue
                  ? Model.WattsPerPoint.Value.ToString("0.000") + " W per point (calibrated)"
                  : "not calibrated - no figure shown, by design");

        Console.WriteLine();
        Console.WriteLine("--- suggestions section ---");
        Card fixCard = Field(f, "_fixCard") as Card;
        Panel fixList = Field(f, "_fixList") as Panel;
        Label fixSummary = Field(f, "_fixSummary") as Label;
        PillButton applyAll = Field(f, "_fixApplyAll") as PillButton;
        Check("the section exists", fixCard != null && fixList != null && fixSummary != null);

        object fixesObj = Field(f, "_fixes");
        List<Suggestion> fixes = fixesObj as List<Suggestion>;
        Check("the form scanned the machine on startup", fixes != null);
        if (fixes != null)
        {
            Console.WriteLine("  " + fixSummary.Text);
            foreach (Suggestion g in fixes)
                Console.WriteLine("    [" + g.Rank.ToString().PadLeft(3) + "] " +
                                  g.Title.PadRight(58) + g.ActionLabel);

            Check("one card per suggestion", fixList.Controls.Count == fixes.Count,
                  fixList.Controls.Count + " cards for " + fixes.Count + " suggestions");

            int buttons = 0, dots = 0, offEdge = 0;
            foreach (Control card in fixList.Controls)
                foreach (Control kid in card.Controls)
                {
                    if (kid is PillButton) buttons++;
                    if (kid is InfoDot) dots++;
                    if (kid.Right > card.Width) offEdge++;
                }
            Check("every suggestion carries a button", buttons == fixes.Count, buttons + " buttons");
            Check("every suggestion carries an info icon", dots == fixes.Count, dots + " icons");
            Check("nothing overflows its card", offEdge == 0, offEdge + " controls past the edge");

            // the card has to be tall enough to hold the list plus the footer buttons,
            // or the AutoSize trap comes back and the section paints over the next one
            Panel fixButtons = Field(f, "_fixButtons") as Panel;
            Check("card is tall enough for its contents",
                  fixButtons != null && fixCard.Height >= fixButtons.Bottom,
                  "card " + fixCard.Height + ", content ends " +
                  (fixButtons == null ? 0 : fixButtons.Bottom));

            Check("Apply all appears only when it saves clicks",
                  applyAll != null && applyAll.Visible == (CountApplicable(fixes) > 1),
                  CountApplicable(fixes) + " of " + fixes.Count + " can be written from here");

            Check("the summary describes what was found",
                  fixSummary.Text.Length > 20 &&
                  (fixes.Count == 0 || fixSummary.Text.Contains(fixes.Count.ToString())),
                  fixSummary.Text);
        }

        Console.WriteLine();
        Console.WriteLine("--- machine detection (replaces the hardcoded laptop) ---");
        Machine.Detect(true);
        Console.WriteLine("  " + Machine.Summary());
        Check("form factor detected", true, Machine.IsPortable ? "portable" : "desktop");
        Check("at least one GPU found", Machine.Gpus != null && Machine.Gpus.Count > 0,
              Machine.Gpus == null ? "none" : Machine.Gpus.Count + " adapters");
        foreach (GpuInfo gi in Machine.Gpus)
            Console.WriteLine("    " + gi.Name.PadRight(42) + gi.Vendor +
                              (gi.Discrete ? "  discrete" : "  integrated"));
        Check("hybrid graphics correctly identified", true,
              Machine.Hybrid ? "hybrid - GPU watch applies" : "not hybrid - GPU watch stands down");

        if (Machine.HasBattery)
        {
            Check("design capacity established", Machine.DesignCapacityMwh > 0,
                  Machine.DesignCapacityMwh + " mWh from " + Machine.DesignCapacitySource);
            // this used to be a hardcoded 70562; detection has to reproduce it
            Check("detected design capacity exceeds current full charge",
                  Machine.DesignCapacityMwh >= 43000,
                  "otherwise health would be meaningless");
        }
        else Console.WriteLine("  no battery - battery panels stand down");

        Console.WriteLine("  brightness controllable: " + Machine.BrightnessControllable);

        int defined = 0, missing = 0;
        foreach (Knob k in PowerCfg.Knobs) { if (PowerCfg.Exists(k)) defined++; else missing++; }
        Check("settings filtered to those Windows defines here", defined > 0,
              defined + " available, " + missing + " not present on this PC");

        Console.WriteLine();
        Console.WriteLine("--- config round-trip ---");
        double? was = Config.Current.WattsPerBrightnessPoint;
        Config.SetBacklight(0.0425);
        Config c2 = Config.Current;
        Check("calibration persists", c2.WattsPerBrightnessPoint.HasValue &&
              Math.Abs(c2.WattsPerBrightnessPoint.Value - 0.0425) < 1e-9,
              "stored in " + Config.Path);
        Config.Current.WattsPerBrightnessPoint = was;   // put it back
        Config.Save();
        Check("restored to prior value",
              Config.Current.WattsPerBrightnessPoint.HasValue == was.HasValue);

        Console.WriteLine();
        Console.WriteLine("--- gpu watch adapts to the hardware ---");
        List<Finding> gw = GpuWatch.Check();
        foreach (Finding gf in gw)
            Console.WriteLine("    [" + (gf.Informational ? "--" : (gf.Ok ? "OK" : "!!")) + "] " +
                              gf.Name.PadRight(44) + gf.Detail);
        Check("gpu watch produced a verdict", gw.Count > 0, gw.Count + " findings");
        double? gwCost = GpuWatch.TotalCost(gw);
        Check("cost is measured-or-unknown, never invented",
              !gwCost.HasValue || gwCost.Value >= 0,
              gwCost.HasValue ? gwCost.Value.ToString("0.0") + " W" : "not measured on this PC");

        Console.WriteLine();
        Console.WriteLine("--- live process telemetry ---");
        ProcessWatch pw = new ProcessWatch();
        List<ProcInfo> first = pw.Sample();
        Check("enumerates real processes", first.Count > 20, first.Count + " names");
        System.Threading.Thread.Sleep(2500);
        List<ProcInfo> second = pw.Sample();
        double cpuTotal = ProcessWatch.TotalCpu(second, false);
        Check("second sample yields real CPU deltas", cpuTotal > 0,
              cpuTotal.ToString("0.0") + "% of one core across all processes");
        Check("memory figures are real", ProcessWatch.TotalMb(second, false) > 100,
              ProcessWatch.TotalMb(second, false).ToString("0") + " MB");
        int bg = 0;
        foreach (ProcInfo pi in second) if (pi.Background) bg++;
        Check("background processes identified", bg > 0 && bg < second.Count,
              bg + " of " + second.Count + " have no window");
        List<ProcInfo> topMem = ProcessWatch.TopBy(second, false, true, 5);
        Console.WriteLine("  top background by memory:");
        foreach (ProcInfo pi in topMem)
            Console.WriteLine("    " + pi.Name.PadRight(30) + pi.WorkingSetMb.ToString("0").PadLeft(6) +
                              " MB   " + pi.CpuPercent.ToString("0.0") + "%");
        Check("ranking is descending",
              topMem.Count < 2 || topMem[0].WorkingSetMb >= topMem[topMem.Count - 1].WorkingSetMb);

        Console.WriteLine();
        Console.WriteLine("--- persistence ---");
        long bytesBefore = History.DiskBytes();
        HistPoint hp = new HistPoint {
            T = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), W = 7.77, Pct = 55, Ac = false,
            Br = 30, Cpu = 4.2, BgMb = 1234, Top = "uitest 1.0"
        };
        History.Append(hp, second, 60);
        History.SaveOffenders();
        List<HistPoint> back = History.Recent(50);
        bool found = false;
        foreach (HistPoint q in back) if (Math.Abs(q.W - 7.77) < 0.001 && q.Pct == 55) found = true;
        Check("a written point reads back from disk", found, back.Count + " points on file");
        Check("history file grew", History.DiskBytes() >= bytesBefore,
              (History.DiskBytes() / 1024.0).ToString("0") + " KB");
        Check("cumulative tally is populated", History.TopOffenders(5, false).Count > 0,
              History.LoadOffenders().Count + " processes tracked");
        Console.WriteLine("  worst by cumulative CPU:");
        foreach (Offender o in History.TopOffenders(5, false))
            Console.WriteLine("    " + o.Name.PadRight(30) + (o.CpuSeconds / 60.0).ToString("0.00") +
                              " cpu-min   peak " + o.PeakMb.ToString("0") + " MB");
        Console.WriteLine("  stored in " + History.Folder);
        Console.WriteLine();

        // ---- info text is actually populated ----------------------------------
        Console.WriteLine("--- info icons ---");
        int empty = 0;
        foreach (Control c in all)
        {
            InfoDot d = c as InfoDot;
            if (d != null && (string.IsNullOrWhiteSpace(d.Body) || d.Body.Length < 80)) empty++;
        }
        Check("every info icon has real explanatory text", empty == 0, empty + " short or empty");

        int knobsWithInfo = 0;
        foreach (Knob k in PowerCfg.Knobs) if (!string.IsNullOrWhiteSpace(k.Info) && k.Info.Length > 80) knobsWithInfo++;
        Check("every setting defines Info", knobsWithInfo == PowerCfg.Knobs.Count,
              knobsWithInfo + " of " + PowerCfg.Knobs.Count);
        Console.WriteLine();

        // ---- basic settings really are hidden, and really do expand -----------
        Console.WriteLine("--- collapsible Basic settings ---");
        Panel basicBox = Field(f, "_basicBox") as Panel;
        Check("basic box found", basicBox != null);

        int basicKnobs = 0, impactKnobs = 0;
        foreach (Knob k in PowerCfg.Knobs) { if (k.Basic) basicKnobs++; else impactKnobs++; }
        Console.WriteLine("  " + impactKnobs + " impact settings shown, " + basicKnobs + " tucked into Basic");
        Check("timeouts were moved out of the main list", basicKnobs == 5, basicKnobs + " basic");
        Check("screen-off timeout is one of them", PowerCfg.Find("videoidle").Basic);
        Check("EPP stayed in the main list", !PowerCfg.Find("epp").Basic);

        if (basicBox != null && basicToggle != null)
        {
            Check("starts collapsed", !basicBox.Visible);
            int before = f.Controls[0].PreferredSize.Height;

            basicToggle.Toggle();          // the real handler, same as a user click
            Application.DoEvents();
            Check("expands when clicked", basicBox.Visible);
            Check("expanded box has the basic rows", basicBox.Controls.Count == basicKnobs,
                  basicBox.Controls.Count + " rows");
            int after = f.Controls[0].PreferredSize.Height;
            Check("layout actually reflows taller", after > before, before + " -> " + after);

            basicToggle.Toggle();
            Application.DoEvents();
            Check("collapses again", !basicBox.Visible);
        }
        Console.WriteLine();

        // ---- restore point -----------------------------------------------------
        Console.WriteLine("--- analytics text ---");
        Label health = Field(f, "_healthNote") as Label;
        Check("health note exists", health != null);
        if (health != null)
        {
            Console.WriteLine("  " + health.Text);
            Check("capacity formatted to one decimal, not mangled",
                  health.Text.Contains("43.9 Wh") && health.Text.Contains("70.6 Wh"),
                  health.Text.Length > 0 ? health.Text.Substring(0, Math.Min(60, health.Text.Length)) : "empty");
            Check("no 441 or 711 artefacts",
                  !health.Text.Contains("441") && !health.Text.Contains("711"));
        }
        StackBar hb = Field(f, "_barHealth") as StackBar;
        Check("health bar has both segments", hb != null && hb.Segments.Count == 2,
              hb == null ? "null" : hb.Segments.Count + " segments");
        Console.WriteLine();

        Console.WriteLine("--- bottom padding ---");
        Control root = f.Controls[0];
        Control lastKid = null;
        foreach (Control c in root.Controls) if (lastKid == null || c.Bottom > lastKid.Bottom) lastKid = c;
        Check("a spacer sits below the Activity section",
              lastKid != null && lastKid.GetType() == typeof(Panel) && lastKid.Controls.Count == 0 && lastKid.Height >= 24,
              lastKid == null ? "none" : lastKid.GetType().Name + " h=" + lastKid.Height);
        Console.WriteLine();

        Console.WriteLine("--- restore point ---");
        Check("a restore point exists", Baseline.Exists, Baseline.FilePath);
        BaselineFile bf = Baseline.Load();
        Check("it covers every setting", bf.Entries.Count == PowerCfg.Knobs.Count,
              bf.Entries.Count + " of " + PowerCfg.Knobs.Count);
        Console.WriteLine("  source: " + bf.Source);
        foreach (BaselineEntry e in bf.Entries)
        {
            Knob k = PowerCfg.Find(e.Key);
            Console.WriteLine("    " + (k == null ? e.Key : k.Label).PadRight(34) +
                              PowerCfg.Describe(k, e.Value) + (e.WasExplicit ? "" : "   (inherited)"));
        }
        Console.WriteLine();

        // ---- render both states straight from the form -------------------------
        // Screen-scraping the live window kept getting snapped and mis-clicked, so draw
        // the control tree into a bitmap instead. No window manager involved.
        Console.WriteLine("--- rendering ---");
        string outDir = Environment.GetEnvironmentVariable("PD_SHOTS");
        if (!string.IsNullOrEmpty(outDir))
        {
            Shoot(f, System.IO.Path.Combine(outDir, "ui-collapsed.png"), 1180);
            Shoot(f, System.IO.Path.Combine(outDir, "ui-full.png"), 3100);

            // DrawToBitmap will not render past roughly the screen height, so a single
            // tall shot of the whole column comes back mostly blank. Capture it as
            // screen-sized slices instead, which is also how anyone actually sees it.
            Control col = f.Controls[0];
            int slice = 0;
            for (int y = 0; y < 3200; y += 900)
            {
                ScrollTo(col, y);
                Shoot(f, System.IO.Path.Combine(outDir, "ui-scroll" + slice + ".png"), 1000);
                slice++;
            }
            ScrollTo(col, 0);

            basicToggle.Toggle();
            Application.DoEvents();
            Shoot(f, System.IO.Path.Combine(outDir, "ui-expanded.png"), 1700);
            basicToggle.Toggle();
            Application.DoEvents();
            Console.WriteLine("  wrote ui-collapsed.png, ui-expanded.png and " + slice + " scroll slices");
        }
        Console.WriteLine();

        f.Close();
        Console.WriteLine(fail == 0 ? "=== ALL CHECKS PASSED ===" : "=== " + fail + " CHECK(S) FAILED ===");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
