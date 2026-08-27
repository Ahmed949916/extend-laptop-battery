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
            RefreshProcesses();
            RefreshHistory(true);
            if (pruned > 0) Log("Cleared " + pruned + " history file(s) older than " + History.KeepMonths + " months.");

            Log("Watching " + PowerCfg.ActiveSchemeName() +
                (PowerCfg.IsElevated() ? ", running as admin." : ", not running as admin."));
            if (cap != null) Log(cap);
            Log("Watts appear after " + _bat.WindowSeconds + " seconds. This laptop reports no instant reading, " +
                "so draw is timed from the battery's own energy counter.");
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

            // ---------------------------------------------------------- profiles
            _root.Controls.Add(Heading("Profiles", "each one writes the battery side only",
                "Three tested combinations of the settings below, plus a way back.\n\n" +
                "Balanced is not a guess - it is the exact configuration that measured 6.92 W, " +
                "about 6h20m of reading, on this laptop. Endurance trades responsiveness for the " +
                "last watt or so. Full speed lets the CPU off the leash while still on battery, " +
                "and roughly halves your runtime.\n\n" +
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
            _root.Controls.Add(Heading("Screen brightness", "0.04 W per point, measured",
                "The single biggest thing you control directly, and the one people leave alone.\n\n" +
                "The backlight costs 0.04 W per point on this panel - about 4 W across the full " +
                "range. That is measured, not estimated: 12.00 W at 99% against 9.24 W at 30% with " +
                "everything else held still.\n\n" +
                "It bites hardest when the machine is otherwise quiet, because then it is a large " +
                "slice of a small total. Going 30% to 80% while reading costs nearly an hour and a " +
                "half. The same change during video costs about half an hour."));

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
                Body = "The backlight costs 0.04 W per point, about 4 W across the whole range. That was " +
                       "measured on this panel, not estimated.\n\n" +
                       "It matters most when the machine is otherwise idle, because then it is a large share " +
                       "of a small total. Going from 30% to 80% while reading costs nearly an hour and a half. " +
                       "The same change during video playback costs about half an hour.\n\n" +
                       "Around 30% indoors is the sweet spot. Below 20% you are chasing minutes at real cost " +
                       "to your eyes."
            };
            bc.Controls.Add(bdot);
            _root.Controls.Add(bc);

            // ---------------------------------------------------------- impact knobs
            _root.Controls.Add(Heading("What changes your watts", "the settings worth tuning",
                "The five settings that change how much power this laptop draws while you are " +
                "using it. Everything here is applied to the battery side only.\n\n" +
                "Changes commit about half a second after you stop moving a control, then get read " +
                "back from the registry to confirm they stuck. The Activity section shows what " +
                "actually happened.\n\n" +
                "Timeouts and lid behaviour live under Basic settings instead, because they change " +
                "nothing while you are actually at the keyboard."));
            foreach (Knob k in PowerCfg.Knobs)
                if (!k.Basic) _root.Controls.Add(BuildRow(k));

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
                if (k.Basic) _basicBox.Controls.Add(BuildRow(k));
            _root.Controls.Add(_basicBox);

            // ---------------------------------------------------------- analytics
            _root.Controls.Add(Heading("Analytics", "recorded here, kept on disk",
                "Everything in this section is measured on this machine and written to " +
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
            _root.Controls.Add(Heading("GPU watch", "an awake NVIDIA card costs about 17 W here",
                "The most valuable panel in this app, and the least obvious.\n\n" +
                "An awake but idle RTX 3050 Ti draws about 17 W on this laptop while reporting 0% " +
                "utilisation and 0 MiB of memory in use - more than the entire rest of the system " +
                "at idle. Three separate things were caught doing it, and not one of them looked " +
                "expensive in Task Manager: NVIDIA Instant Replay, OMEN Command Center, and OMEN " +
                "Light Studio.\n\n" +
                "The two OMEN ones come back at every boot as packaged background tasks, so " +
                "disabling their scheduled tasks achieves nothing. The fix that holds is the " +
                "per-app Background apps permission set to Never - and an OMEN update can quietly " +
                "switch it back on. That is what this panel is watching for."));

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
            _brightCost.Text = "~" + (_brightSlider.Value * Brightness.WattsPerPoint).ToString("0.0") + " W";
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
            if (!_bat.OnAc && _bat.Watts.HasValue && _bat.Watts.Value > 0.1)
            {
                double back = Math.Min(_bat.Watts.Value, _brightSlider.Value * Model.WattsPerPoint);
                double rest = Math.Max(0, _bat.Watts.Value - back);
                _barWatts.Segments.Add(new Segment { Name = "backlight", Value = back, Color = Theme.Spend });
                _barWatts.Segments.Add(new Segment { Name = "everything else", Value = rest, Color = Theme.Save });
            }
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
                    Color c = local.Ok ? Theme.Save : Theme.Alert;
                    Theme.Str(g, local.Ok ? Theme.GlyphCheck : Theme.GlyphWarn, Theme.IconSmall, c, 0, 3);
                    Theme.Str(g, local.Name, Theme.Body, local.Ok ? Theme.Text : Theme.Alert, 22, 2);
                    string d = local.Detail;
                    if (!local.Ok && local.CostWatts > 0) d += "   ~" + local.CostWatts.ToString("0.0") + " W";
                    Theme.StrRight(g, d, Theme.Small, local.Ok ? Theme.Dim : Theme.Alert, line.Width, 3);
                };
                _watchList.Controls.Add(line);
                y += 22;
            }

            _watchList.Height = Math.Max(22, y);
            _watchButtons.Top = _watchList.Bottom + 8;
            _watchCard.Height = _watchButtons.Bottom + 12;

            double total = GpuWatch.TotalCost(f);
            if (total > 0)
            {
                _watchSummary.Text = "About " + total.ToString("0.0") + " W is being wasted";
                _watchSummary.ForeColor = Theme.Alert;
            }
            else
            {
                _watchSummary.Text = "Nothing is waking the NVIDIA card";
                _watchSummary.ForeColor = Theme.Save;
            }
            _root.PerformLayout();
        }

        // ================================================================= actions

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
        }

        void CommitBrightness(int v)
        {
            string err = Brightness.Set(v);
            if (err != null) { Log("Could not change brightness: " + err); ShowLog(); }
            else { Log("Brightness set to " + v + "%"); _bat.ResetWindow(); _spark.Clear(); }
            UpdateBrightLabels();
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
            LoadValues(); RefreshWatch();
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
            LoadValues(); RefreshWatch(); ShowLog();
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
        public double? Watts, Remaining, Health;
        public int ChargePct;
        public int? Mwh, FullMwh;
        public int WindowSeconds = 60;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Quality(g);

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
