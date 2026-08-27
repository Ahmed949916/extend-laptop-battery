using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PowerDial
{
    public class MainForm : Form
    {
        const int W = 648;          // content column width

        class Row
        {
            public Knob Knob;
            public Card Host;
            public Slider Slider;
            public Picker Combo;
            public Label Value;
            public Label Status;
        }

        readonly BatteryMonitor _bat = new BatteryMonitor();
        readonly List<Row> _rows = new List<Row>();
        readonly Timer _poll = new Timer();
        readonly Timer _commit = new Timer();

        Row _pendingRow;
        int _pendingValue;
        int? _pendingBrightness;

        SteadyPanel _root;
        Readout _readout;
        Sparkline _spark;
        Slider _brightSlider;
        Label _brightValue, _brightCost;
        Panel _basicBox;
        SectionToggle _basicToggle, _logToggle;
        Panel _logBox;
        TextBox _log;
        Panel _watchList, _watchButtons;
        Card _watchCard;
        Label _watchSummary;
        Panel _fixList, _fixButtons;
        Card _fixCard;
        Label _fixSummary;
        PillButton _fixApplyAll;
        List<Suggestion> _fixes = new List<Suggestion>();
        List<Finding> _findings;
        bool _fixesSeeded;
        LineChart _chartWatts;
        BatteryChart _batChart;
        ProcessTable _procTable, _offTable;
        StackBar _barWatts, _barHealth;
        Label _healthNote, _histLabel, _procLabel, _procTotals;
        PillButton _btnMem, _btnCpu, _btnBg;

        readonly ProcessWatch _procWatch = new ProcessWatch();
        List<ProcInfo> _procs = new List<ProcInfo>();
        bool _sortCpu;
        bool _bgOnly = true;
        DateTime _lastHistory = DateTime.MinValue;
        PillButton _adminBtn;
        readonly List<PillButton> _presetBtns = new List<PillButton>();

        NotifyIcon _tray;
        bool _loading;

        public MainForm()
        {
            Text = "PowerDial";
            BackColor = Theme.Ink;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(W + 34, 860);
            MinimumSize = new Size(W + 34, 520);
            Icon = MakeIcon();
            DoubleBuffered = true;

            Machine.Detect(false);

            BuildUi();
            BuildTray();

            _poll.Interval = 15000;
            _poll.Tick += (s, e) => {
                _bat.Poll();
                _procs = _procWatch.Sample();
                PushSample();
                RefreshReadout();
                RefreshProcesses();
                MaybeRecord();

                // CPU is a delta, so the sample taken during startup carried memory
                // figures and no CPU at all - anything judging a process by what it burns
                // was looking at zeroes. Rescan once here, where the first real numbers
                // land, then leave it alone: re-reading every setting on a fifteen-second
                // timer is not free.
                if (!_fixesSeeded) { _fixesSeeded = true; RefreshFixes(); }
            };
            _poll.Start();

            _commit.Interval = 600;
            _commit.Tick += (s, e) => {
                _commit.Stop();
                if (_pendingRow != null)
                {
                    Row r = _pendingRow; _pendingRow = null;
                    ApplyKnob(r, _pendingValue);
                }
                if (_pendingBrightness.HasValue)
                {
                    int v = _pendingBrightness.Value; _pendingBrightness = null;
                    CommitBrightness(v);
                }
            };

            string cap = Baseline.CaptureIfMissing();

            int pruned = History.Prune();
            _bat.Poll();
            _procs = _procWatch.Sample();
            LoadValues();
            RefreshReadout();
            RefreshWatch();
            RefreshFixes();
            RefreshProcesses();
            RefreshHistory(true);
            if (pruned > 0) Log("Cleared " + pruned + " history file(s) older than " + History.KeepMonths + " months.");

            Log("Watching " + PowerCfg.ActiveSchemeName() +
                (PowerCfg.IsElevated() ? ", running as admin." : ", not running as admin."));
            if (cap != null) Log(cap);
            Log(Machine.Summary());
            if (Machine.HasBattery)
            {
                Log("Design capacity " + (Machine.DesignCapacityMwh > 0
                    ? Machine.DesignCapacityMwh + " mWh, " + Machine.DesignCapacitySource
                    : "could not be determined - battery health will read as unknown"));
                Log("Watts appear after " + _bat.WindowSeconds + " seconds. Windows reports no instant " +
                    "figure, so draw is timed from the battery energy counter.");
            }
            else Log("No battery detected, so the draw and charge panels are inactive.");
            int skipped = 0;
            foreach (Knob k in PowerCfg.Knobs) if (!PowerCfg.Exists(k)) skipped++;
            if (skipped > 0) Log(skipped + " setting(s) are not defined on this PC and were left out.");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // Pin the window to the content column. Something in the flow layout was
            // letting it open full-screen, which left a huge empty gutter to the right.
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            int maxH = Screen.FromControl(this).WorkingArea.Height - 90;
            ClientSize = new Size(W + 34, Math.Min(880, Math.Max(520, maxH)));
            CenterToScreen();

            // AutoScroll parks itself on whichever control takes focus, which pushed the
            // header card above the fold. Resetting inline is too early - the flow panel
            // is still laying out - so do it on the next idle tick.
            Timer once = new Timer { Interval = 60 };
            once.Tick += (s, ev) => {
                once.Stop(); once.Dispose();
                _root.AutoScrollPosition = new Point(0, 0);
            };
            once.Start();
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            if (_root == null) return;
            // keep the column centred if the window is dragged or snapped wider
            int extra = Math.Max(0, ClientSize.Width - (W + 34));
            _root.Padding = new Padding(14 + extra / 2, 14, 6, 20);
        }

        // ================================================================= layout

        void BuildUi()
        {
            _root = new SteadyPanel {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, BackColor = Theme.Ink, Padding = new Padding(14, 14, 6, 20)
            };
            Controls.Add(_root);

            // ---------------------------------------------------------- header
            _readout = new Readout { Width = W, Height = 122, Margin = new Padding(0, 0, 0, 12) };
            _spark = new Sparkline { Location = new Point(16, 74), Size = new Size(W - 32, 34), Ceiling = 16 };
            _readout.Controls.Add(_spark);
            _root.Controls.Add(_readout);

            // ---------------------------------------------------------- suggestions
            _root.Controls.Add(Heading("Make it last longer", "what is worth changing on this PC right now",
                "Everything below is something this app found wrong with how this PC is set up at " +
                "the moment, sorted by what matters most. It is not a checklist of things you " +
                "could do - a setting that is already sensible does not appear here at all, so an " +
                "empty list genuinely means there is nothing left to fix.\n\n" +
                "Each suggestion has a button that does exactly what its title says, writing the " +
                "battery side only and reading the value straight back to confirm it stuck. " +
                "Nothing is applied until you press one.\n\n" +
                "Most of them carry no watt figure, on purpose. What a setting is worth depends on " +
                "hardware that has not been measured here, and a number copied from another machine " +
                "would be wrong by several times. Where a figure has been measured on this PC it is " +
                "shown; where it has not, the order of the list carries the priority instead.\n\n" +
                "A few things cannot be fixed by writing a setting - background apps and whatever is " +
                "holding a discrete GPU awake. Those open the Windows page where you do it yourself " +
                "rather than pretending to be fixed."));

            _fixCard = new Card { Width = W, Height = 120, Margin = new Padding(0, 0, 0, 12) };

            _fixSummary = new Label {
                Location = new Point(14, 12), Size = new Size(W - 30, 22),
                Font = Theme.Title, ForeColor = Theme.Save, BackColor = Theme.Panel
            };
            _fixCard.Controls.Add(_fixSummary);

            _fixList = new Panel {
                Location = new Point(14, 40), Size = new Size(W - 30, 10), BackColor = Theme.Panel
            };
            _fixCard.Controls.Add(_fixList);

            _fixButtons = new Panel {
                Location = new Point(14, 58), Size = new Size(W - 30, 38), BackColor = Theme.Panel
            };
            _fixApplyAll = new PillButton {
                Text = "Apply every fix", Glyph = Theme.GlyphCheck, Location = new Point(0, 3),
                Size = new Size(160, 32), Primary = true, BackColor = Theme.Panel
            };
            _fixApplyAll.Click += (s, e) => ApplyAllFixes();
            _fixButtons.Controls.Add(_fixApplyAll);
            PillButton refix = new PillButton {
                Text = "Check again", Glyph = Theme.GlyphRefresh, Location = new Point(168, 3),
                Size = new Size(126, 32), BackColor = Theme.Panel
            };
            refix.Click += (s, e) => { LoadValues(); RefreshWatch(); RefreshFixes(); Log("Re-checked what is worth changing."); };
            _fixButtons.Controls.Add(refix);
            _fixCard.Controls.Add(_fixButtons);

            _root.Controls.Add(_fixCard);

            // ---------------------------------------------------------- profiles
            _root.Controls.Add(Heading("Profiles", "each one writes the battery side only",
                "Three tested combinations of the settings below, plus a way back.\n\n" +
                "Balanced is the sensible default on any machine. Endurance trades responsiveness for " +
                "the last watt or so. Full speed lets the CPU off the leash while still on battery.\n\n" +
                "Apply one, then watch the power draw chart for a minute - what each is worth " +
                "depends entirely on the hardware.\n\n" +
                "Every profile writes the on-battery side only. What happens when you are plugged " +
                "in is never touched, which is what made all of this safe to experiment with."));

            Panel profiles = new Panel { Width = W, Height = 40, BackColor = Theme.Ink, Margin = new Padding(0, 0, 0, 8) };
            int px = 0, bw = (W - 2 * 8) / 3;
            foreach (Preset p in Presets.All)
            {
                Preset local = p;
                PillButton b = new PillButton {
                    Text = p.Name, Location = new Point(px, 0), Size = new Size(bw, 36), Primary = false
                };
                b.Click += (s, e) => ApplyPreset(local);
                profiles.Controls.Add(b);
                _presetBtns.Add(b);
                px += bw + 8;
            }
            _root.Controls.Add(profiles);

            Panel restoreRow = new Panel { Width = W, Height = 38, BackColor = Theme.Ink, Margin = new Padding(0, 0, 0, 2) };
            PillButton restore = new PillButton {
                Text = "Restore original settings", Glyph = Theme.GlyphUndo,
                Location = new Point(0, 0), Size = new Size(232, 36), Primary = true
            };
            restore.Click += (s, e) => RestoreBaseline();
            restoreRow.Controls.Add(restore);
            _root.Controls.Add(restoreRow);

            Label profileNote = new Label {
                Text = "Puts every battery setting back to how it was before PowerDial existed.",
                AutoSize = false, Width = W, Height = 20, ForeColor = Theme.Dim, Font = Theme.Small,
                Margin = new Padding(2, 2, 0, 10), BackColor = Theme.Ink
            };
            _root.Controls.Add(profileNote);

            _adminBtn = new PillButton {
                Text = "Run as admin", Glyph = Theme.GlyphShield, Width = 150, Height = 32,
                Margin = new Padding(0, 0, 0, 10), Visible = false
            };
            _adminBtn.Click += (s, e) => RelaunchElevated();
            _root.Controls.Add(_adminBtn);
            if (!PowerCfg.IsElevated()) _adminBtn.Visible = true;

            // ---------------------------------------------------------- brightness
            _root.Controls.Add(Heading("Screen brightness", "the biggest lever you hold directly",
                "Often the single biggest thing you control directly, and the one most people leave " +
                "alone.\n\n" +
                "How much a backlight costs varies enormously - a small dim panel and a large bright " +
                "one differ by several times - so this app will not quote you a number it has not " +
                "measured on this display. Once it has, the watt figure appears beside the slider " +
                "and in Where your watts go.\n\n" +
                "It bites hardest when the machine is otherwise quiet, because then it is a large " +
                "slice of a small total."));

            Card bc = new Card { Width = W, Height = 56, Margin = new Padding(0, 0, 0, 12) };
            _brightSlider = new Slider {
                Location = new Point(14, 14), Size = new Size(W - 210, 28),
                Minimum = 0, Maximum = 100, Step = 5, Accent = Theme.Spend
            };
            _brightSlider.ValueChanged += (s, e) => {
                UpdateBrightLabels();
                if (_loading) return;
                _pendingBrightness = _brightSlider.Value;
                _commit.Stop(); _commit.Start();
            };
            bc.Controls.Add(_brightSlider);
            _brightValue = new Label {
                Text = "--", Location = new Point(W - 186, 16), Size = new Size(56, 24),
                Font = Theme.Value, ForeColor = Theme.Text, BackColor = Theme.Panel, TextAlign = ContentAlignment.MiddleRight
            };
            bc.Controls.Add(_brightValue);
            _brightCost = new Label {
                Text = "", Location = new Point(W - 124, 20), Size = new Size(74, 18),
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel
            };
            bc.Controls.Add(_brightCost);
            InfoDot bdot = new InfoDot {
                Location = new Point(W - 34, 19), Heading = "Screen brightness",
                Body = "Backlight cost varies by several times between displays, so no figure is shown " +
                       "here until it has been measured on this one.\n\n" +
                       "It matters most when the machine is otherwise idle, because then it is a large " +
                       "share of a small total. On a quiet laptop, going from 30% to 80% can cost more " +
                       "runtime than every CPU setting on this page put together.\n\n" +
                       "Around 30% indoors is usually the sweet spot. Below 20% you are chasing minutes " +
                       "at real cost to your eyes."
            };
            bc.Controls.Add(bdot);
            _root.Controls.Add(bc);

            // ---------------------------------------------------------- impact knobs
            _root.Controls.Add(Heading("What changes your watts", "the settings worth tuning",
                "The settings that change how much power this PC draws while you are using it. " +
                "Everything here is applied to the battery side only, and anything Windows does " +
                "not define on this hardware is left out rather than shown dead.\n\n" +
                "Changes commit about half a second after you stop moving a control, then get read " +
                "back from the registry to confirm they stuck. The Activity section shows what " +
                "actually happened.\n\n" +
                "Timeouts and lid behaviour live under Basic settings instead, because they change " +
                "nothing while you are actually at the keyboard."));
            foreach (Knob k in PowerCfg.Knobs)
                if (!k.Basic && PowerCfg.Exists(k)) _root.Controls.Add(BuildRow(k));

            // ---------------------------------------------------------- basic knobs
            _basicToggle = new SectionToggle {
                Width = W, Caption = "Basic settings",
                Sub = "timeouts and lid behaviour, none of which change your draw while you work",
                Margin = new Padding(0, 10, 0, 0)
            };
            _basicToggle.Toggled += (s, e) => {
                _basicBox.Visible = _basicToggle.Expanded;
                _root.PerformLayout();
            };
            _root.Controls.Add(WithInfo(_basicToggle, "Basic settings",
                "Timeouts and lid behaviour. Collapsed because none of them change how much " +
                "power you draw while you are actually using the laptop.\n\n" +
                "They still matter once you walk away: the screen-off timeout saves up to 4 W, " +
                "and sleep and hibernate are what stop a closed laptop draining flat in a bag. " +
                "The lid action was set to Do nothing when this machine was first examined, " +
                "which is exactly that failure.\n\n" +
                "Nothing is hidden from you here. It is sorted by whether it affects runtime " +
                "while you work."));

            _basicBox = new FlowLayoutPanel {
                Width = W, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                BackColor = Theme.Ink, Margin = new Padding(0), Visible = false
            };
            foreach (Knob k in PowerCfg.Knobs)
                if (k.Basic && PowerCfg.Exists(k)) _basicBox.Controls.Add(BuildRow(k));
            _root.Controls.Add(_basicBox);

            // ---------------------------------------------------------- analytics
            _root.Controls.Add(Heading("Analytics", "recorded here, kept on disk",
                "Everything in this section is measured on this PC and written to " +
                "%LOCALAPPDATA%\\PowerDial\\history, one line a minute, so it survives restarts " +
                "instead of starting from nothing every launch.\n\n" +
                "Power draw is the timed battery counter. Charge over time spans previous runs, " +
                "amber where you were on battery.\n\n" +
                "Running now is a live sample of every process, grouped by name. Since recording " +
                "began is the cumulative tally - the process that has actually burned the most CPU " +
                "across every session, which is the one costing you runtime.\n\n" +
                "Background means no instance of it owns a visible window. Those are the ones worth " +
                "questioning, because you are not the one using them."));

            Card ac = new Card { Width = W, Height = 318, Margin = new Padding(0, 0, 0, 10) };
            ac.Controls.Add(new Label {
                Text = "Power draw", Location = new Point(14, 10), AutoSize = true,
                Font = Theme.Title, ForeColor = Theme.Text, BackColor = Theme.Panel });
            _chartWatts = new LineChart {
                Location = new Point(14, 32), Size = new Size(W - 30, 118),
                Empty = "no samples yet - readings begin after 60 seconds on battery" };
            ac.Controls.Add(_chartWatts);
            ac.Controls.Add(new Label {
                Text = "Charge over time", Location = new Point(14, 158), AutoSize = true,
                Font = Theme.Title, ForeColor = Theme.Text, BackColor = Theme.Panel });
            _batChart = new BatteryChart { Location = new Point(14, 180), Size = new Size(W - 30, 104) };
            ac.Controls.Add(_batChart);
            _histLabel = new Label {
                Location = new Point(14, 292), Size = new Size(W - 30, 18),
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel };
            ac.Controls.Add(_histLabel);
            _root.Controls.Add(ac);

            Card pc = new Card { Width = W, Height = 292, Margin = new Padding(0, 0, 0, 10) };
            _procLabel = new Label {
                Text = "Running now", Location = new Point(14, 10), Size = new Size(300, 18),
                Font = Theme.Title, ForeColor = Theme.Text, BackColor = Theme.Panel };
            pc.Controls.Add(_procLabel);
            _btnMem = new PillButton { Text = "By memory", Location = new Point(W - 322, 6), Size = new Size(98, 26), Selected = true };
            _btnCpu = new PillButton { Text = "By CPU", Location = new Point(W - 218, 6), Size = new Size(80, 26) };
            _btnBg = new PillButton { Text = "Background", Location = new Point(W - 132, 6), Size = new Size(118, 26), Selected = true };
            _btnMem.Click += (s, e) => { _sortCpu = false; RefreshProcesses(); };
            _btnCpu.Click += (s, e) => { _sortCpu = true; RefreshProcesses(); };
            _btnBg.Click += (s, e) => { _bgOnly = !_bgOnly; RefreshProcesses(); };
            pc.Controls.Add(_btnMem);
            pc.Controls.Add(_btnCpu);
            pc.Controls.Add(_btnBg);
            _procTable = new ProcessTable {
                Location = new Point(14, 40), Size = new Size(W - 30, ProcessTable.RowH * 9 + 22) };
            pc.Controls.Add(_procTable);
            _procTotals = new Label {
                Location = new Point(14, 262), Size = new Size(W - 30, 18),
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel };
            pc.Controls.Add(_procTotals);
            _root.Controls.Add(pc);

            Card oc = new Card { Width = W, Height = 216, Margin = new Padding(0, 0, 0, 10) };
            oc.Controls.Add(new Label {
                Text = "Since recording began", Location = new Point(14, 10), AutoSize = true,
                Font = Theme.Title, ForeColor = Theme.Text, BackColor = Theme.Panel });
            oc.Controls.Add(new Label {
                Text = "cumulative CPU time across every session", Location = new Point(176, 13),
                AutoSize = true, Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel });
            _offTable = new ProcessTable {
                Location = new Point(14, 34), Size = new Size(W - 30, ProcessTable.RowH * 7 + 22),
                SortByCpu = true, CpuHeader = "cpu minutes", CpuSuffix = " min", CpuFormat = "0.0",
                MemHeader = "peak memory", Empty = "nothing recorded yet" };
            oc.Controls.Add(_offTable);
            _root.Controls.Add(oc);

            Card wc2 = new Card { Width = W, Height = 104, Margin = new Padding(0, 0, 0, 10) };
            wc2.Controls.Add(new Label {
                Text = "Where your watts go", Location = new Point(14, 10), AutoSize = true,
                Font = Theme.Title, ForeColor = Theme.Text, BackColor = Theme.Panel });
            _barWatts = new StackBar {
                Location = new Point(14, 36), Size = new Size(W - 30, 44),
                Empty = "waiting for a reading on battery" };
            wc2.Controls.Add(_barWatts);
            _root.Controls.Add(wc2);

            Card hc = new Card { Width = W, Height = 104, Margin = new Padding(0, 0, 0, 10) };
            hc.Controls.Add(new Label {
                Text = "Battery health", Location = new Point(14, 10), AutoSize = true,
                Font = Theme.Title, ForeColor = Theme.Text, BackColor = Theme.Panel });
            _barHealth = new StackBar {
                Location = new Point(14, 34), Size = new Size(W - 30, 44), Unit = "Wh",
                Empty = "capacity not readable" };
            hc.Controls.Add(_barHealth);
            _healthNote = new Label {
                Location = new Point(14, 80), Size = new Size(W - 30, 18),
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel };
            hc.Controls.Add(_healthNote);
            _root.Controls.Add(hc);

            // ---------------------------------------------------------- gpu watch
            _root.Controls.Add(Heading("GPU watch", "only matters on a switchable-graphics laptop",
                "On a laptop with both integrated and discrete graphics, the discrete GPU is meant to " +
                "power down when nothing needs it. An awake but idle one can draw anywhere from " +
                "about 5 W to over 20 W depending on the part, while reporting 0% utilisation - " +
                "which is why it hides so well in Task Manager.\n\n" +
                "This panel lists the software most likely to be holding it awake: graphics vendor " +
                "overlays and capture tools, and the laptop makers gaming and lighting suites. Any " +
                "single one of them is enough, so the saving from clearing them is the GPU wake " +
                "cost once, not the sum of the entries.\n\n" +
                "Several of these relaunch at every boot as packaged background tasks, so disabling " +
                "their scheduled tasks achieves nothing - the per-app Background apps permission is " +
                "the setting that holds, and a vendor update can quietly switch it back on.\n\n" +
                "On a desktop, or a single-GPU laptop, there is nothing here to keep asleep and the " +
                "panel says so."));

            _watchCard = new Card { Width = W, Height = 278, Margin = new Padding(0, 0, 0, 10) };

            _watchSummary = new Label {
                Location = new Point(14, 12), Size = new Size(W - 30, 22),
                Font = Theme.Title, ForeColor = Theme.Save, BackColor = Theme.Panel
            };
            _watchCard.Controls.Add(_watchSummary);

            _watchList = new Panel {
                Location = new Point(14, 40), Size = new Size(W - 30, 180), BackColor = Theme.Panel
            };
            _watchCard.Controls.Add(_watchList);

            _watchButtons = new Panel {
                Location = new Point(14, 228), Size = new Size(W - 30, 38), BackColor = Theme.Panel
            };
            PillButton recheck = new PillButton { Text = "Check again", Glyph = Theme.GlyphRefresh, Location = new Point(0, 3), Size = new Size(126, 32) };
            recheck.Click += (s, e) => { RefreshWatch(); Log("Re-checked what could be waking the GPU."); };
            _watchButtons.Controls.Add(recheck);
            PillButton opensettings = new PillButton { Text = "Open Windows settings", Location = new Point(134, 3), Size = new Size(184, 32) };
            opensettings.Click += (s, e) => GpuWatch.OpenBackgroundAppsSettings();
            _watchButtons.Controls.Add(opensettings);
            PillButton reread = new PillButton { Text = "Re-read everything", Location = new Point(326, 3), Size = new Size(160, 32) };
            reread.Click += (s, e) => { LoadValues(); RefreshWatch(); Log("Re-read every setting from the registry."); };
            _watchButtons.Controls.Add(reread);
            _watchCard.Controls.Add(_watchButtons);

            _root.Controls.Add(_watchCard);

            // ---------------------------------------------------------- log
            _logToggle = new SectionToggle { Width = W, Caption = "Activity", Sub = "what this app changed", Margin = new Padding(0, 4, 0, 0) };
            _logToggle.Toggled += (s, e) => { _logBox.Visible = _logToggle.Expanded; _root.PerformLayout(); };
            _root.Controls.Add(WithInfo(_logToggle, "Activity",
                "Everything this app changed, in order, with timestamps.\n\n" +
                "Each write is read straight back from the registry afterwards. If a value did " +
                "not stick, this is where it says so and shows what it actually reads, rather " +
                "than claiming success. It opens by itself when something fails.\n\n" +
                "Useful when a setting needs administrator rights: the message will say so, and " +
                "Run as admin restarts the app elevated."));

            _logBox = new Panel { Width = W, Height = 150, BackColor = Theme.Ink, Visible = false, Margin = new Padding(0, 0, 0, 8) };
            _log = new TextBox {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.Inset, ForeColor = Theme.Text, Font = Theme.Mono, BorderStyle = BorderStyle.None
            };
            _logBox.Controls.Add(_log);
            _root.Controls.Add(_logBox);

            // breathing room so the last section is not flush against the window edge
            _root.Controls.Add(new Panel {
                Width = W, Height = 30, BackColor = Theme.Ink, Margin = new Padding(0)
            });
        }

        /// <summary>Section heading, with an info icon explaining the section itself.</summary>
        Panel Heading(string text, string sub, string info)
        {
            Panel host = new Panel {
                Width = W, Height = 30, BackColor = Theme.Ink, Margin = new Padding(2, 6, 0, 2)
            };

            float tw;
            using (Graphics g = CreateGraphics()) tw = Theme.TextW(g, text, Theme.Section);

            Label l = new Label {
                Text = text, AutoSize = true, Location = new Point(0, 5),
                Font = Theme.Section, ForeColor = Theme.Text, BackColor = Theme.Ink
            };
            host.Controls.Add(l);

            float subX = tw + 10;
            if (!string.IsNullOrEmpty(info))
            {
                InfoDot d = new InfoDot {
                    Location = new Point((int)(tw + 8), 5), Heading = text, Body = info, BackColor = Theme.Ink
                };
                host.Controls.Add(d);
                subX = tw + 32;
            }

            if (!string.IsNullOrEmpty(sub))
            {
                float sx = subX;
                host.Paint += (s, e) => {
                    Theme.Quality(e.Graphics);
                    Theme.Str(e.Graphics, sub, Theme.Small, Theme.Dim, sx, 9);
                };
            }
            return host;
        }

        /// <summary>Wraps a collapsible header so it can carry an info icon too.</summary>
        Panel WithInfo(SectionToggle t, string heading, string info)
        {
            Panel host = new Panel {
                Width = W, Height = t.Height, BackColor = Theme.Ink, Margin = t.Margin
            };
            t.Location = new Point(0, 0);
            t.Width = W - 28;
            host.Controls.Add(t);
            InfoDot d = new InfoDot {
                Location = new Point(W - 24, (t.Height - 18) / 2),
                Heading = heading, Body = info, BackColor = Theme.Ink
            };
            host.Controls.Add(d);
            d.BringToFront();
            return host;
        }

        Card BuildRow(Knob k)
        {
            Card c = new Card { Width = W, Height = 92, Margin = new Padding(0, 0, 0, 8) };
            Row row = new Row { Knob = k, Host = c };

            Label title = new Label {
                Text = k.Label, Location = new Point(14, 11), Size = new Size(W - 200, 20),
                Font = Theme.Title, ForeColor = Theme.Text, BackColor = Theme.Panel, AutoEllipsis = true
            };
            c.Controls.Add(title);

            if (k.Hidden)
            {
                Label badge = new Label {
                    Text = "hidden by Windows", AutoSize = true, Font = Theme.Small, ForeColor = Theme.Dim,
                    BackColor = Theme.Panel, Location = new Point(W - 176, 13)
                };
                c.Controls.Add(badge);
            }

            InfoDot dot = new InfoDot { Location = new Point(W - 34, 12), Heading = k.Label, Body = k.Info };
            c.Controls.Add(dot);

            Label note = new Label {
                Text = k.Note, Location = new Point(14, 31), Size = new Size(W - 40, 18),
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel, AutoEllipsis = true
            };
            c.Controls.Add(note);

            if (k.Choices != null)
            {
                Picker d = new Picker { Location = new Point(14, 54), Size = new Size(300, 28) };
                List<int> keys = new List<int>(k.Choices.Keys);
                keys.Sort();
                foreach (int kv in keys) d.Items.Add(k.Choices[kv]);
                d.Tag2 = keys;
                d.SelectedIndexChanged += (s, e) => {
                    if (_loading) return;
                    List<int> ks = (List<int>)d.Tag2;
                    if (d.SelectedIndex >= 0 && d.SelectedIndex < ks.Count)
                    {
                        _pendingRow = row; _pendingValue = ks[d.SelectedIndex];
                        _commit.Stop(); _commit.Start();
                    }
                };
                c.Controls.Add(d);
                row.Combo = d;
            }
            else
            {
                Slider sl = new Slider {
                    Location = new Point(14, 54), Size = new Size(W - 250, 28),
                    Minimum = k.Min, Maximum = k.Max, Step = (k.Unit == "s" ? 60 : 1),
                    Accent = Theme.Save
                };
                sl.ValueChanged += (s, e) => {
                    if (row.Value != null) row.Value.Text = PowerCfg.Describe(k, sl.Value);
                    if (_loading) return;
                    _pendingRow = row; _pendingValue = sl.Value;
                    _commit.Stop(); _commit.Start();
                };
                c.Controls.Add(sl);
                row.Slider = sl;

                row.Value = new Label {
                    Text = "--", Location = new Point(W - 226, 55), Size = new Size(70, 24),
                    Font = Theme.Value, ForeColor = Theme.Text, BackColor = Theme.Panel,
                    TextAlign = ContentAlignment.MiddleRight
                };
                c.Controls.Add(row.Value);
            }

            row.Status = new Label {
                Text = "", Location = new Point(W - 236, 58), Size = new Size(222, 20),
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel,
                TextAlign = ContentAlignment.MiddleRight
            };
            c.Controls.Add(row.Status);

            _rows.Add(row);
            return c;
        }

        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip { BackColor = Theme.Inset, ForeColor = Theme.Text, ShowImageMargin = false };
            foreach (Preset p in Presets.All)
            {
                Preset local = p;
                menu.Items.Add(p.Name, null, (s, e) => ApplyPreset(local));
            }
            menu.Items.Add("Restore my settings", null, (s, e) => RestoreBaseline());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open PowerDial", null, (s, e) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
            menu.Items.Add("Quit", null, (s, e) => { _tray.Visible = false; Application.Exit(); });

            _tray = new NotifyIcon { Icon = Icon, Visible = true, Text = "PowerDial", ContextMenuStrip = menu };
            _tray.DoubleClick += (s, e) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            History.SaveOffenders();
            if (_tray != null) _tray.Visible = false;
            base.OnFormClosing(e);
        }

        // ================================================================= state

        void LoadValues()
        {
            _loading = true;
            try
            {
                foreach (Row r in _rows)
                {
                    int? v = PowerCfg.Read(r.Knob, true);
                    int? ac = PowerCfg.Read(r.Knob, false);
                    bool expl = PowerCfg.IsExplicit(r.Knob, true);

                    r.Status.Text = (expl ? "" : "inherited  ") + "plugged in: " + PowerCfg.Describe(r.Knob, ac);

                    if (!v.HasValue) continue;
                    if (r.Combo != null)
                    {
                        List<int> keys = (List<int>)r.Combo.Tag2;
                        int idx = keys.IndexOf(v.Value);
                        if (idx < 0)
                        {
                            r.Combo.Items.Add(PowerCfg.Describe(r.Knob, v));
                            keys.Add(v.Value);
                            idx = keys.Count - 1;
                        }
                        r.Combo.SelectedIndex = idx;
                    }
                    else if (r.Slider != null)
                    {
                        r.Slider.Value = v.Value;
                        r.Value.Text = PowerCfg.Describe(r.Knob, v);
                    }
                }

                int? b = Brightness.Get();
                if (b.HasValue) { _brightSlider.Value = b.Value; UpdateBrightLabels(); }
                else { _brightValue.Text = "n/a"; _brightCost.Text = ""; }
            }
            finally { _loading = false; }
        }

        void UpdateBrightLabels()
        {
            _brightValue.Text = _brightSlider.Value + "%";
            // no figure until the backlight has been measured on this display - a borrowed
            // number would be wrong by several times between a small dim panel and a big one
            double? w = Model.Backlight(_brightSlider.Value);
            _brightCost.Text = w.HasValue ? "~" + w.Value.ToString("0.0") + " W" : "";
        }

        void RefreshProcesses()
        {
            _btnMem.Selected = !_sortCpu; _btnMem.Invalidate();
            _btnCpu.Selected = _sortCpu; _btnCpu.Invalidate();
            _btnBg.Selected = _bgOnly; _btnBg.Invalidate();

            _procTable.SortByCpu = _sortCpu;
            _procTable.Rows = ProcessWatch.TopBy(_procs, _sortCpu, _bgOnly, 9);
            _procTable.Invalidate();

            int n = 0;
            foreach (ProcInfo p in _procs) if (!_bgOnly || p.Background) n++;
            _procTotals.Text = n + (_bgOnly ? " background" : " total") + " processes  ·  " +
                               (ProcessWatch.TotalMb(_procs, _bgOnly) / 1024.0).ToString("0.00") + " GB  ·  " +
                               ProcessWatch.TotalCpu(_procs, _bgOnly).ToString("0.0") + "% of one core";

            // the cumulative table follows the same background filter
            RefreshOffenders();
        }

        void RefreshOffenders()
        {
            List<ProcInfo> rows = new List<ProcInfo>();
            foreach (Offender o in History.TopOffenders(7, _bgOnly))
                rows.Add(new ProcInfo {
                    Name = o.Name, Instances = 1, Background = o.Background,
                    WorkingSetMb = o.PeakMb, CpuPercent = o.CpuSeconds / 60.0
                });
            _offTable.Rows = rows;
            _offTable.Invalidate();
        }

        /// <summary>Reload the saved history and redraw everything that comes from it.</summary>
        void RefreshHistory(bool seedChart)
        {
            List<HistPoint> pts = History.Recent(1440);      // about a day at one a minute
            _batChart.SetData(pts);

            if (seedChart)
            {
                List<double> w = new List<double>();
                foreach (HistPoint p in pts) if (!p.Ac && p.W > 0.05) w.Add(p.W);
                _chartWatts.Seed(w);
            }

            History.Stats st = History.Summarise(pts);
            string line = st.Points + " minutes recorded";
            if (st.BatteryPoints > 0)
                line += "   ·   " + FmtHours(st.BatteryMinutes / 60.0) + " of it on battery, averaging " +
                        st.AvgWatts.ToString("0.00") + " W (" + st.MinWatts.ToString("0.00") + " to " +
                        st.MaxWatts.ToString("0.00") + ")";
            long bytes = History.DiskBytes();
            line += "   ·   " + (bytes / 1024.0).ToString("0") + " KB on disk";
            _histLabel.Text = line;

            RefreshOffenders();
        }

        /// <summary>Write one history point a minute. Anything faster is noise and disk churn.</summary>
        void MaybeRecord()
        {
            DateTime now = DateTime.UtcNow;
            if (_lastHistory != DateTime.MinValue && (now - _lastHistory).TotalSeconds < 60) return;
            double interval = _lastHistory == DateTime.MinValue ? 60 : (now - _lastHistory).TotalSeconds;
            _lastHistory = now;

            bool draining = !_bat.OnAc && _bat.Watts.HasValue && _bat.Watts.Value > 0.05;
            HistPoint p = new HistPoint {
                T = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                W = draining ? Math.Round(_bat.Watts.Value, 3) : 0,
                Pct = _bat.PercentOfFull,
                Ac = _bat.OnAc,
                Br = _brightSlider.Value,
                Cpu = Math.Round(ProcessWatch.TotalCpu(_procs, false), 1),
                BgMb = Math.Round(ProcessWatch.TotalMb(_procs, true), 0),
                Top = TopSummary()
            };
            History.Append(p, _procs, interval);
            if (draining) _chartWatts.Push(p.W);
            RefreshHistory(false);
        }

        string TopSummary()
        {
            List<ProcInfo> top = ProcessWatch.TopBy(_procs, true, false, 3);
            string s = "";
            foreach (ProcInfo p in top)
            {
                if (s.Length > 0) s += "|";
                s += p.Name + " " + p.CpuPercent.ToString("0.0");
            }
            return s;
        }

        void PushSample()
        {
            if (!_bat.OnAc && _bat.Watts.HasValue && _bat.Watts.Value > 0)
            {
                _spark.Push(_bat.Watts.Value);
                _chartWatts.Push(_bat.Watts.Value);
            }
        }

        static string FmtHours(double h)
        {
            int t = (int)Math.Round(Math.Max(0, h) * 60);
            return (t / 60) + "h " + (t % 60).ToString("00") + "m";
        }

        void RefreshAnalytics()
        {
            _barWatts.Segments.Clear();
            double? perPoint = Model.WattsPerPoint;
            if (!_bat.OnAc && _bat.Watts.HasValue && _bat.Watts.Value > 0.1 && perPoint.HasValue)
            {
                double back = Math.Min(_bat.Watts.Value, _brightSlider.Value * perPoint.Value);
                double rest = Math.Max(0, _bat.Watts.Value - back);
                _barWatts.Segments.Add(new Segment { Name = "backlight", Value = back, Color = Theme.Spend });
                _barWatts.Segments.Add(new Segment { Name = "everything else", Value = rest, Color = Theme.Save });
            }
            _barWatts.Empty = perPoint.HasValue
                ? "waiting for a reading on battery"
                : "backlight cost has not been measured on this display yet";
            _barWatts.Invalidate();

            _barHealth.Segments.Clear();
            if (_bat.FullChargeMwh.HasValue)
            {
                double usable = _bat.FullChargeMwh.Value / 1000.0;
                double design = BatteryMonitor.OriginalDesignMwh / 1000.0;
                double lost = Math.Max(0, design - usable);
                _barHealth.Segments.Add(new Segment { Name = "still holds", Value = usable, Color = Theme.Save });
                _barHealth.Segments.Add(new Segment { Name = "lost to ageing", Value = lost, Color = Theme.Spend });

                string note = usable.ToString("0.0") + " Wh of the " + design.ToString("0.0") +
                              " Wh it shipped with.";
                if (_bat.Watts.HasValue && _bat.Watts.Value > 0.1)
                    note += "  At " + _bat.Watts.Value.ToString("0.00") + " W that missing capacity is " +
                            FmtHours(lost / _bat.Watts.Value) + " you no longer have.";
                else
                    note += "  No software setting can recover it.";
                _healthNote.Text = note;
            }
            _barHealth.Invalidate();
        }

        void RefreshReadout()
        {
            _readout.HasBattery = Machine.HasBattery;
            _readout.OnAc = _bat.OnAc;
            _readout.Charging = _bat.Charging;
            _readout.Watts = _bat.Watts;
            _readout.WindowSeconds = _bat.WindowSeconds;
            _readout.Remaining = _bat.HoursRemaining;
            _readout.ChargePct = _bat.PercentOfFull;
            _readout.Health = _bat.HealthPercent;
            _readout.Mwh = _bat.RemainingMwh;
            _readout.FullMwh = _bat.FullChargeMwh;
            _readout.Invalidate();
            RefreshAnalytics();

            string tip = "PowerDial  " + (_bat.OnAc ? "on AC" :
                (_bat.Watts.HasValue ? _bat.Watts.Value.ToString("0.0") + " W" : "measuring")) +
                "  " + _bat.PercentOfFull + "%";
            if (_tray != null) _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        void RefreshWatch()
        {
            _watchList.Controls.Clear();
            List<Finding> f = GpuWatch.Check();
            _findings = f;          // the advisor reuses these rather than re-enumerating
            int y = 0;
            foreach (Finding item in f)
            {
                Finding local = item;
                Panel line = new Panel {
                    Location = new Point(0, y), Size = new Size(_watchList.Width, 22), BackColor = Theme.Panel
                };
                line.Paint += (s, e) => {
                    Graphics g = e.Graphics;
                    Theme.Quality(g);
                    g.Clear(Theme.Panel);
                    Color c = local.Informational ? Theme.Dim : (local.Ok ? Theme.Save : Theme.Alert);
                    string glyph = local.Informational ? "-" : (local.Ok ? Theme.GlyphCheck : Theme.GlyphWarn);
                    Theme.Str(g, glyph, local.Informational ? Theme.Small : Theme.IconSmall, c, 0, 3);
                    Theme.Str(g, local.Name, Theme.Body, local.Ok ? Theme.Text : Theme.Alert, 22, 2);
                    Color dc = local.Ok ? Theme.Dim : Theme.Alert;
                    if (local.Informational) dc = Theme.Dim;
                    Theme.StrRight(g, local.Detail, Theme.Small, dc, line.Width, 3);
                };
                _watchList.Controls.Add(line);
                y += 22;
            }

            _watchList.Height = Math.Max(22, y);
            _watchButtons.Top = _watchList.Bottom + 8;
            _watchCard.Height = _watchButtons.Bottom + 12;

            int flagged = 0;
            foreach (Finding fi in f) if (!fi.Ok) flagged++;
            double? total = GpuWatch.TotalCost(f);

            if (flagged == 0)
            {
                GpuInfo d = Machine.Discrete;
                _watchSummary.Text = d == null || !Machine.Hybrid || !Machine.IsPortable
                    ? "No switchable GPU on this PC - nothing to keep asleep"
                    : "Nothing is holding the discrete GPU awake";
                _watchSummary.ForeColor = d == null ? Theme.Dim : Theme.Save;
            }
            else if (total.HasValue && total.Value > 0)
            {
                _watchSummary.Text = flagged + " item(s) can hold the GPU awake, costing about " +
                                     total.Value.ToString("0.0") + " W";
                _watchSummary.ForeColor = Theme.Alert;
            }
            else
            {
                _watchSummary.Text = flagged + " item(s) can hold the discrete GPU awake " +
                                     "(cost not yet measured on this PC)";
                _watchSummary.ForeColor = Theme.Alert;
            }
            _root.PerformLayout();
        }

        /// <summary>
        /// Rebuild the suggestion list from what the machine currently looks like. Called
        /// on startup and after anything changes, not on the poll timer: it re-reads every
        /// setting and would otherwise rebuild a dozen controls every fifteen seconds.
        /// </summary>
        void RefreshFixes()
        {
            _fixList.Controls.Clear();
            _fixes = Advisor.Scan(_bat, _procs, _findings);

            int y = 0;
            foreach (Suggestion s in _fixes)
            {
                _fixList.Controls.Add(BuildFix(s, y));
                y += 108;
            }

            _fixList.Height = Math.Max(1, y - 8);
            _fixList.Visible = _fixes.Count > 0;
            _fixButtons.Top = _fixes.Count > 0 ? _fixList.Bottom + 10 : _fixList.Top;

            int applicable = 0;
            foreach (Suggestion s in _fixes) if (s.CanApply) applicable++;
            _fixApplyAll.Visible = applicable > 1;
            _fixApplyAll.Text = applicable == 2 ? "Apply both" : "Apply all " + applicable;

            if (_fixes.Count == 0)
            {
                _fixSummary.ForeColor = Theme.Save;
                _fixSummary.Text = Machine.HasBattery
                    ? "Nothing left to suggest - everything this app checks is already set for endurance"
                    : "No battery on this PC, so there is no runtime to extend";
            }
            else
            {
                _fixSummary.ForeColor = Theme.Spend;
                string head = _fixes.Count + " thing" + (_fixes.Count == 1 ? "" : "s") + " worth changing";
                if (applicable == 0) head += ", none of which this app can write for you";
                else if (applicable < _fixes.Count)
                    head += "  ·  " + applicable + " can be applied from here";
                _fixSummary.Text = head;
            }

            _fixCard.Height = _fixButtons.Bottom + 12;
            _root.PerformLayout();
        }

        /// <summary>One suggestion, as a card inside the suggestions panel.</summary>
        Card BuildFix(Suggestion s, int y)
        {
            Suggestion local = s;
            int w = _fixList.Width;
            Card c = new Card {
                Location = new Point(0, y), Size = new Size(w, 100),
                Fill = Theme.Inset, Border = Theme.Edge, Radius = 8, BackColor = Theme.Panel
            };

            Label title = new Label {
                Text = s.Title, Location = new Point(12, 9), Size = new Size(w - 190, 20),
                Font = Theme.Title, ForeColor = Theme.Text, BackColor = Theme.Inset, AutoEllipsis = true
            };
            c.Controls.Add(title);

            InfoDot dot = new InfoDot {
                Location = new Point(w - 28, 10), Heading = s.Title, Body = s.Info, BackColor = Theme.Inset
            };
            c.Controls.Add(dot);

            Label detail = new Label {
                Text = s.Detail, Location = new Point(12, 30), Size = new Size(w - 34, 32),
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Inset
            };
            c.Controls.Add(detail);

            PillButton act = new PillButton {
                Text = s.ActionLabel, Location = new Point(12, 62), Size = new Size(ButtonWidth(s.ActionLabel), 30),
                Primary = s.CanApply, BackColor = Theme.Inset
            };
            act.Click += (o, e) => ApplyFix(local);
            c.Controls.Add(act);

            // the saving, and the before/after, painted rather than laid out so they can
            // sit hard against the right edge whatever length they are
            c.Paint += (o, e) => {
                Graphics g = e.Graphics;
                Theme.Quality(g);
                if (local.Gain.HasValue && local.Gain.Value > 0.05)
                    Theme.StrRight(g, "~" + local.Gain.Value.ToString("0.0") + " W", Theme.Value, Theme.Save, w - 34, 6);
                string change = local.Change;
                if (change.Length > 0)
                    Theme.StrRight(g, change, Theme.Small, Theme.Dim, w - 14, 70);
            };

            return c;
        }

        static int ButtonWidth(string text)
        {
            // PillButton centres its own text, so it only needs to be wide enough not to clip
            return Math.Max(120, Math.Min(240, 22 + text.Length * 8));
        }

        // ================================================================= actions

        /// <summary>
        /// Carry out one suggestion. Advisory ones open a Windows page instead of writing
        /// anything, and say so in the log rather than claiming a fix.
        /// </summary>
        void ApplyFix(Suggestion s)
        {
            string err = Advisor.Apply(s);

            if (s.Kind == FixKind.Advisory)
            {
                if (err != null) { Log("Could not open Windows settings: " + err); ShowLog(); }
                else Log("Opened Windows settings for: " + s.Title);
                return;     // nothing changed here, so nothing to re-read
            }

            if (err != null) { Log(s.Title + " - failed: " + err); ShowLog(); }
            else Log(s.Title + " - done (" + s.NowText + " to " + s.ThenText + ")");

            _bat.ResetWindow(); _spark.Clear();
            LoadValues(); RefreshWatch(); RefreshFixes();
        }

        /// <summary>
        /// Everything on the list that this app can actually write, in one go. Confirmed
        /// first, because several settings at once is a bigger step than nudging a slider
        /// and the whole point of the restore point is that surprises are recoverable.
        /// </summary>
        void ApplyAllFixes()
        {
            List<Suggestion> doable = new List<Suggestion>();
            foreach (Suggestion s in _fixes) if (s.CanApply) doable.Add(s);
            if (doable.Count == 0) return;

            string what = "";
            foreach (Suggestion s in doable) what += "   ·  " + s.Title + "\n";

            int advisory = _fixes.Count - doable.Count;
            string tail = advisory > 0
                ? "\n" + advisory + " other suggestion(s) need doing in Windows and are not included.\n"
                : "";

            DialogResult r = MessageBox.Show(
                "Apply " + doable.Count + " change(s)?\n\n" + what + tail +
                "\nThe battery side only. Your plugged-in settings are not touched, and " +
                "Restore original settings puts all of this back.",
                "Make it last longer", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;

            int ok = 0, fail = 0;
            foreach (Suggestion s in doable)
            {
                string err = Advisor.Apply(s);
                if (err != null) { Log("   " + s.Title + ": " + err); fail++; }
                else { Log("   " + s.Title + " -> " + s.ThenText); ok++; }
            }
            Log("Applied " + ok + " suggestion(s)" + (fail > 0 ? ", " + fail + " failed" : "") + ".");
            if (fail > 0) ShowLog();

            foreach (PillButton b in _presetBtns) b.Selected = false;
            _bat.ResetWindow(); _spark.Clear();
            LoadValues(); RefreshWatch(); RefreshFixes();
        }

        void ApplyKnob(Row row, int value)
        {
            string err = PowerCfg.WriteDc(row.Knob, value);
            if (err != null)
            {
                Log("Could not change " + row.Knob.Label + ": " + err);
                ShowLog();
            }
            else
            {
                int? back = PowerCfg.Read(row.Knob, true);
                if (back.HasValue && back.Value == value)
                    Log(row.Knob.Label + " set to " + PowerCfg.Describe(row.Knob, value));
                else
                    Log(row.Knob.Label + " did not stick - it reads back as " + PowerCfg.Describe(row.Knob, back));
                _bat.ResetWindow(); _spark.Clear();
            }
            LoadValues();
            RefreshFixes();
        }

        void CommitBrightness(int v)
        {
            string err = Brightness.Set(v);
            if (err != null) { Log("Could not change brightness: " + err); ShowLog(); }
            else { Log("Brightness set to " + v + "%"); _bat.ResetWindow(); _spark.Clear(); }
            UpdateBrightLabels();
            RefreshFixes();
        }

        void ApplyPreset(Preset p)
        {
            Log("Applying " + p.Name + "...");
            int ok = 0, fail = 0;
            foreach (KeyValuePair<string, int> kv in p.Values)
            {
                Knob k = PowerCfg.Find(kv.Key);
                if (k == null) continue;
                string err = PowerCfg.WriteDc(k, kv.Value);
                if (err != null) { Log("   could not set " + k.Label + ": " + err); fail++; continue; }
                int? back = PowerCfg.Read(k, true);
                if (back.HasValue && back.Value == kv.Value) { Log("   " + k.Label + " -> " + PowerCfg.Describe(k, kv.Value)); ok++; }
                else { Log("   " + k.Label + " did not stick"); fail++; }
            }
            if (p.Brightness.HasValue)
            {
                string berr = Brightness.Set(p.Brightness.Value);
                if (berr != null) { Log("   could not set brightness: " + berr); fail++; }
                else { Log("   brightness -> " + p.Brightness.Value + "%"); ok++; }
            }
            Log(p.Name + ": " + ok + " changed" + (fail > 0 ? ", " + fail + " failed" : "") + ".");
            if (fail > 0) ShowLog();

            foreach (PillButton b in _presetBtns) b.Selected = (b.Text == p.Name);
            _bat.ResetWindow(); _spark.Clear();
            LoadValues(); RefreshWatch(); RefreshFixes();
        }

        void RestoreBaseline()
        {
            BaselineFile bf = Baseline.Load();
            string when = bf.CapturedUtc == "(none)" ? "the tuning session" : bf.CapturedUtc + " UTC";
            DialogResult r = MessageBox.Show(
                "Put back the battery settings as they were before PowerDial?\n\n" +
                "Restore point: " + when + "\n" +
                bf.Entries.Count + " settings will be rewritten.\n\n" +
                "Your plugged-in settings and screen brightness are not touched.",
                "Restore my settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;

            foreach (string line in Baseline.Restore()) Log(line);
            foreach (PillButton b in _presetBtns) b.Selected = false;
            _bat.ResetWindow(); _spark.Clear();
            LoadValues(); RefreshWatch(); RefreshFixes(); ShowLog();
        }

        void RelaunchElevated()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                if (_tray != null) _tray.Visible = false;
                Application.Exit();
            }
            catch (Exception ex) { Log("Did not restart as admin: " + ex.Message); ShowLog(); }
        }

        void ShowLog()
        {
            if (_logToggle != null && !_logToggle.Expanded)
            {
                _logToggle.Expanded = true;
                _logBox.Visible = true;
                _logToggle.Invalidate();
                _root.PerformLayout();
            }
        }

        void Log(string msg)
        {
            if (_log == null) return;
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine);
        }

        static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (Pen pen = new Pen(Theme.Save, 1.4f)) g.DrawRectangle(pen, 2, 4, 10, 8);
                    using (SolidBrush br = new SolidBrush(Theme.Save))
                    {
                        g.FillRectangle(br, 4, 6, 5, 5);
                        g.FillRectangle(br, 12, 6, 2, 4);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    /// <summary>The header instrument: watts, what is left, charge, health, and the trace.</summary>
    public class Readout : Card
    {
        public bool OnAc, Charging;
        public bool HasBattery = true;
        public double? Watts, Remaining, Health;
        public int ChargePct;
        public int? Mwh, FullMwh;
        public int WindowSeconds = 60;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Quality(g);

            // A desktop has no battery, so there is no draw to time and no charge to
            // report. Say what the machine is instead of showing a row of dashes.
            if (!HasBattery)
            {
                Theme.Str(g, "Mains power", Theme.Stat, Theme.Text, 14, 14);
                Theme.Str(g, "no battery, so there is nothing to measure a draw against",
                          Theme.Small, Theme.Dim, 16, 44);
                Theme.Str(g, Machine.Summary(), Theme.Small, Theme.Dim, 16, 62);
                Theme.Str(g, "The settings, process telemetry and GPU watch below still apply.",
                          Theme.Small, Theme.Dim, 16, 96);
                return;
            }

            string big, unit;
            Color c;
            if (OnAc)
            {
                if (Watts.HasValue && Watts.Value < -0.1) { big = (-Watts.Value).ToString("0.0"); unit = "W charging"; c = Theme.Save; }
                else { big = "--"; unit = "on AC, unplug to measure"; c = Theme.Dim; }
            }
            else if (Watts.HasValue)
            {
                big = Watts.Value.ToString("0.00");
                unit = "watts";
                c = Watts.Value > 12 ? Theme.Spend : Theme.Save;
            }
            else { big = "--"; unit = "measuring, " + WindowSeconds + "s"; c = Theme.Dim; }

            Theme.Str(g, big, Theme.Readout, c, 14, 8);
            float bw = Theme.TextW(g, big, Theme.Readout);
            Theme.Str(g, unit, Theme.Small, Theme.Dim, 18, 56);

            float x = Math.Max(180, 14 + bw + 26);
            Stat(g, x, Remaining.HasValue ? Fmt(Remaining.Value) : "--", "left", Theme.Text);
            Stat(g, x + 118, ChargePct + "%", "charge", Theme.Text);
            Stat(g, x + 222, Health.HasValue ? Health.Value.ToString("0") + "%" : "--", "health",
                 Health.HasValue && Health.Value < 70 ? Theme.Spend : Theme.Text);

            string state = OnAc ? (Charging ? "on AC, charging" : "on AC") : "on battery";
            if (Mwh.HasValue && FullMwh.HasValue) state += "   ·   " + Mwh.Value.ToString("N0") + " of " + FullMwh.Value.ToString("N0") + " mWh";
            Theme.StrRight(g, state, Theme.Small, Theme.Dim, Width - 16, 56);
        }

        void Stat(Graphics g, float x, string v, string label, Color c)
        {
            Theme.Str(g, v, Theme.Stat, c, x, 14);
            Theme.Str(g, label, Theme.Small, Theme.Dim, x + 2, 40);
        }

        static string Fmt(double h)
        {
            if (h < 0 || h > 99) return "--";
            int t = (int)Math.Round(h * 60);
            return (t / 60) + ":" + (t % 60).ToString("00");
        }
    }
}
