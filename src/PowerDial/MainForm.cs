using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PowerDial
{
    public class MainForm : Form
    {
        // Everything is laid out once at this width, then stretched to the window by
        // Relayout. Building at a fixed width keeps the arithmetic in one place; the
        // stretching is carried by Anchor on the children that should grow.
        const int W = 648;          // design width of the content column
        const int WMax = 1240;      // past this a chart is wider, not clearer
        const int Chrome = 14;      // gutter either side of the column
        const int SideCol = 340;    // x of the plugged-in column in a settings row

        class Row
        {
            public Knob Knob;
            public Card Host;
            public Slider Slider;
            public Picker Combo;
            public Label Value;
            public Label Status;     // the battery side
            public Label Status2;    // the plugged-in side
        }

        readonly BatteryMonitor _bat = new BatteryMonitor();
        readonly List<Row> _rows = new List<Row>();
        readonly Timer _poll = new Timer();
        readonly Timer _commit = new Timer();
        readonly Timer _tick = new Timer();   // 1 s, only to move the measuring countdown

        Row _pendingRow;
        int _pendingValue;
        int? _pendingBrightness;

        SteadyPanel _root;
        Panel _chrome;              // header + section bar, pinned above the scroll area
        Panel _navBar;
        readonly List<PillButton> _navBtns = new List<PillButton>();
        readonly List<Control> _navTargets = new List<Control>();
        readonly Dictionary<string, Control> _sections = new Dictionary<string, Control>();
        Readout _readout;
        Sparkline _spark;
        Slider _brightSlider;
        Label _brightValue, _brightCost;
        Panel _basicBox;
        SectionToggle _basicToggle, _logToggle;
        Panel _logBox;
        TextBox _log;
        Panel _watchList, _watchButtons, _watchBox;
        SectionToggle _watchToggle;
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
        Label _healthNote, _histLabel, _procLabel, _procTotals, _contribNote;
        PillButton _btnMem, _btnCpu, _btnBg;
        Panel _contribList;
        PillButton _fixRecheck;

        Panel _busyBar;
        string _busyText = "";
        int _busyTotal, _busyDone;

        // which side the settings section writes. Battery unless explicitly switched.
        bool _acMode;
        PillButton _sideDc, _sideAc;
        Label _sideNote;

        readonly ProcessWatch _procWatch = new ProcessWatch();
        List<ProcInfo> _procs = new List<ProcInfo>();
        RuntimeAverage _avg = new RuntimeAverage();

        /// <summary>
        /// Per-poll CPU, kept only for as long as the draw measurement window, so the
        /// contributors list answers "what was busy while that reading was taken" rather
        /// than "what is busy this instant". Deliberately core-seconds, never watts: a
        /// background app holding the discrete GPU awake costs ~17 W at near-zero CPU, so
        /// splitting the measured total by CPU share would point at the wrong process.
        /// </summary>
        class PollCpu { public DateTime At; public Dictionary<string, double> Core; }
        readonly List<PollCpu> _window = new List<PollCpu>();
        DateTime _lastPoll = DateTime.MinValue;
        DateTime _windowStart = DateTime.MinValue;   // when the current draw window began
        bool _sortCpu;
        bool _bgOnly = true;
        DateTime _lastHistory = DateTime.MinValue;
        PillButton _adminBtn;
        readonly List<PillButton> _presetBtns = new List<PillButton>();

        NotifyIcon _tray;
        bool _loading;

        /// <summary>
        /// Named event a second launch sets to ask this copy to show its window. Lives here
        /// rather than on Program because the test projects compile MainForm but not Program.
        /// </summary>
        public const string WakeEvent = "PowerDial.ShowWindow";

        System.Threading.EventWaitHandle _wake;
        bool _quitting, _saidWhereItWent;

        public MainForm()
        {
            Text = "PowerDial";
            BackColor = Theme.Ink;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(980, 900);
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
                TrackWindowCpu();
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

            // one second, and only while the first reading is still pending - a countdown
            // that moves is the difference between "working" and "hung"
            _tick.Interval = 1000;
            _tick.Tick += (s, e) => {
                if (_readout == null || _bat.Watts.HasValue) return;
                if (_windowStart == DateTime.MinValue) return;
                int left = Math.Max(0, _bat.WindowSeconds - (int)(DateTime.UtcNow - _windowStart).TotalSeconds);
                if (left == _readout.MeasuringLeft) return;
                _readout.MeasuringLeft = left;
                _readout.Invalidate();
            };
            _tick.Start();

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
            // an older snapshot has no plugged-in side; fill it in before the user can
            // reach the switch that would make those values no longer original
            string acCap = Baseline.BackfillAcIfMissing();

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
            if (acCap != null) Log(acCap);
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

            // Open at a size that suits the screen rather than a fixed column. The column
            // itself is still capped by Relayout - a chart three thousand pixels wide is
            // no clearer - but the window no longer opens as a strip on a large display.
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            Rectangle work = Screen.FromControl(this).WorkingArea;
            int maxH = work.Height - 90;
            int wantW = Math.Min(WMax + 34, Math.Max(W + 34, (int)(work.Width * 0.62)));
            ClientSize = new Size(wantW, Math.Min(940, Math.Max(520, maxH)));
            CenterToScreen();
            Relayout();
            ListenForRelaunch();

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
            Relayout();
        }

        // ================================================================= layout

        void BuildUi()
        {
            // _root is added first on purpose: the UI test reads Controls[0] as the
            // scrolling column, and checks the spacer is its bottom-most child. Neither
                // panel is docked - explicit bounds plus Anchor means dock ordering, which
            // is z-order dependent and easy to get backwards, never enters into it.
            _root = new SteadyPanel {
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, BackColor = Theme.Ink, Padding = new Padding(Chrome, 12, 6, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(_root);

            _chrome = new Panel {
                Location = new Point(0, 0), BackColor = Theme.Ink,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(_chrome);

            // ---------------------------------------------------------- header
            // Pinned rather than scrolled. The draw, what is left and the measured
            // average are the reason the app is open; they used to scroll away within
            // one flick of the wheel and you had to come back up to read the effect of
            // whatever you had just changed.
            _readout = new Readout {
                Location = new Point(Chrome, 12), Width = W, Height = 170,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _spark = new Sparkline {
                Location = new Point(16, 72), Size = new Size(W - 32, 28), Ceiling = 16,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _readout.Controls.Add(_spark);
            _chrome.Controls.Add(_readout);

            // ---------------------------------------------------------- suggestions
            Panel fixHead = Heading("Make it last longer", "what is worth changing on this PC right now",
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
                "rather than pretending to be fixed.");
            _root.Controls.Add(fixHead);

            _fixCard = new Card { Width = W, Height = 120, Margin = new Padding(0, 0, 0, 12) };

            _fixSummary = new Label {
                Location = new Point(14, 12), Size = new Size(W - 30, 22),
                Font = Theme.Title, ForeColor = Theme.Save, BackColor = Theme.Panel,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _fixCard.Controls.Add(_fixSummary);

            _fixList = new Panel {
                Location = new Point(14, 40), Size = new Size(W - 30, 10), BackColor = Theme.Panel,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _fixCard.Controls.Add(_fixList);

            _fixButtons = new Panel {
                Location = new Point(14, 58), Size = new Size(W - 30, 38), BackColor = Theme.Panel,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _fixApplyAll = new PillButton {
                Text = "Apply every fix", Glyph = Theme.GlyphCheck, Location = new Point(0, 3),
                Size = new Size(160, 32), Primary = true, BackColor = Theme.Panel
            };
            _fixApplyAll.Click += (s, e) => ApplyAllFixes();
            _fixButtons.Controls.Add(_fixApplyAll);
            _fixRecheck = new PillButton {
                Text = "Check again", Glyph = Theme.GlyphRefresh, Location = new Point(168, 3),
                Size = new Size(126, 32), BackColor = Theme.Panel
            };
            _fixRecheck.Click += (s, e) => { LoadValues(); RefreshWatch(); RefreshFixes(); Log("Re-checked what is worth changing."); };
            _fixButtons.Controls.Add(_fixRecheck);
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
            int count = Math.Max(1, Presets.All.Count);
            int px = 0, bw = (W - (count - 1) * 8) / count;
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
                Minimum = 0, Maximum = 100, Step = 5, Accent = Theme.Spend,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
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
                Font = Theme.Value, ForeColor = Theme.Text, BackColor = Theme.Panel,
                TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            bc.Controls.Add(_brightValue);
            _brightCost = new Label {
                Text = "", Location = new Point(W - 124, 20), Size = new Size(74, 18),
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel, Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            bc.Controls.Add(_brightCost);
            InfoDot bdot = new InfoDot {
                Location = new Point(W - 34, 19), Heading = "Screen brightness", Anchor = AnchorStyles.Top | AnchorStyles.Right,
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
                "nothing while you are actually at the keyboard.\n\n" +
                "The switch chooses which side you are editing. Battery is the default and the only " +
                "side the profiles, the suggestions and the restore point ever write."));

            _root.Controls.Add(BuildSideSwitch());

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
                Font = Theme.Section, ForeColor = Theme.Text, BackColor = Theme.Panel });
            _chartWatts = new LineChart {
                Location = new Point(14, 32), Size = new Size(W - 30, 118), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Empty = "no samples yet - readings begin after 60 seconds on battery" };
            ac.Controls.Add(_chartWatts);
            ac.Controls.Add(new Label {
                Text = "Charge over time", Location = new Point(14, 158), AutoSize = true,
                Font = Theme.Section, ForeColor = Theme.Text, BackColor = Theme.Panel });
            _batChart = new BatteryChart {
                Location = new Point(14, 180), Size = new Size(W - 30, 104), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            ac.Controls.Add(_batChart);
            _histLabel = new Label {
                Location = new Point(14, 292), Size = new Size(W - 30, 18), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel };
            ac.Controls.Add(_histLabel);
            _root.Controls.Add(ac);

            Card pc = new Card { Width = W, Height = 292, Margin = new Padding(0, 0, 0, 10) };
            _procLabel = new Label {
                Text = "Running now", Location = new Point(14, 10), Size = new Size(300, 18),
                Font = Theme.Section, ForeColor = Theme.Text, BackColor = Theme.Panel };
            pc.Controls.Add(_procLabel);
            _btnMem = new PillButton { Text = "By memory", Location = new Point(W - 400, 6), Size = new Size(98, 26), Selected = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _btnCpu = new PillButton { Text = "By CPU", Location = new Point(W - 296, 6), Size = new Size(80, 26), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _btnBg = new PillButton { Text = "Background", Location = new Point(W - 210, 6), Size = new Size(118, 26), Selected = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            PillButton resetLive = new PillButton {
                Text = "Reset", Glyph = Theme.GlyphRefresh, Location = new Point(W - 86, 6),
                Size = new Size(72, 26), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _btnMem.Click += (s, e) => { _sortCpu = false; RefreshProcesses(); };
            _btnCpu.Click += (s, e) => { _sortCpu = true; RefreshProcesses(); };
            _btnBg.Click += (s, e) => { _bgOnly = !_bgOnly; RefreshProcesses(); };
            // CPU here is a delta against the previous sample, so "reset" means forget that
            // baseline: the next poll reads zero and everything after it is fresh.
            resetLive.Click += (s, e) => {
                _procWatch.ResetBaseline();
                _window.Clear();
                _lastPoll = DateTime.MinValue;
                _procs = _procWatch.Sample();
                RefreshProcesses();
                RefreshContributors();
                Log("Reset the live CPU baseline. The next reading starts from zero.");
            };
            pc.Controls.Add(_btnMem);
            pc.Controls.Add(_btnCpu);
            pc.Controls.Add(_btnBg);
            pc.Controls.Add(resetLive);
            _procTable = new ProcessTable {
                Location = new Point(14, 40), Size = new Size(W - 30, ProcessTable.RowH * 9 + 22), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            pc.Controls.Add(_procTable);
            _procTotals = new Label {
                Location = new Point(14, 262), Size = new Size(W - 30, 18), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel };
            pc.Controls.Add(_procTotals);
            _root.Controls.Add(pc);

            Card oc = new Card { Width = W, Height = 216, Margin = new Padding(0, 0, 0, 10) };
            oc.Controls.Add(new Label {
                Text = "Since recording began", Location = new Point(14, 10), AutoSize = true,
                Font = Theme.Section, ForeColor = Theme.Text, BackColor = Theme.Panel });
            oc.Controls.Add(new Label {
                Text = "cumulative CPU time across every session", Location = new Point(176, 13),
                AutoSize = true, Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel });
            PillButton resetTally = new PillButton {
                Text = "Reset tally", Glyph = Theme.GlyphUndo, Location = new Point(W - 132, 6),
                Size = new Size(118, 26), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            // this one destroys data that took weeks to accumulate, so it asks first
            resetTally.Click += (s, e) => {
                if (MessageBox.Show(
                        "Clear the cumulative per-process tally?\n\n" +
                        "This is the record of what has actually burned CPU across every session, " +
                        "and it is what makes \"what has been eating the battery\" survive reboots. " +
                        "Deleting it cannot be undone.\n\n" +
                        "The per-minute history and its charts are not affected.",
                        "Reset tally", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                History.ClearOffenders();
                RefreshOffenders();
                RefreshHistory(false);
                Log("Cleared the cumulative per-process tally. Counting starts again from now.");
            };
            oc.Controls.Add(resetTally);
            _offTable = new ProcessTable {
                Location = new Point(14, 34), Size = new Size(W - 30, ProcessTable.RowH * 7 + 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, SortByCpu = true, CpuHeader = "cpu minutes", CpuSuffix = " min", CpuFormat = "0.0",
                MemHeader = "peak memory", Empty = "nothing recorded yet" };
            oc.Controls.Add(_offTable);
            _root.Controls.Add(oc);

            Card wc2 = new Card { Width = W, Height = 268, Margin = new Padding(0, 0, 0, 10) };
            wc2.Controls.Add(new Label {
                Text = "Where your watts go", Location = new Point(14, 10), AutoSize = true,
                Font = Theme.Section, ForeColor = Theme.Text, BackColor = Theme.Panel });
            _barWatts = new StackBar {
                Location = new Point(14, 36), Size = new Size(W - 30, 44), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Empty = "waiting for a reading on battery" };
            wc2.Controls.Add(_barWatts);

            wc2.Controls.Add(new Label {
                Text = "Busiest while that reading was taken", Location = new Point(14, 92), AutoSize = true,
                Font = Theme.Section, ForeColor = Theme.Text, BackColor = Theme.Panel });
            _contribNote = new Label {
                Location = new Point(14, 114), Size = new Size(W - 30, 32),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel };
            wc2.Controls.Add(_contribNote);
            _contribList = new Panel {
                Location = new Point(14, 148), Size = new Size(W - 30, 108), BackColor = Theme.Panel,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _contribList.Paint += (s, e) => PaintContributors(e.Graphics);
            wc2.Controls.Add(_contribList);
            InfoDot cdot = new InfoDot {
                Location = new Point(W - 34, 94), Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Heading = "Busiest while that reading was taken",
                Body = "The processes that burned the most CPU during the same window the watts figure " +
                       "was timed over, so the two describe the same slice of time rather than the draw " +
                       "from a minute ago and whatever happens to be busy this instant.\n\n" +
                       "These are core-seconds, not watts, and that is deliberate. Splitting the measured " +
                       "total across processes by CPU share would be a fabricated number, and it would " +
                       "point at the wrong culprit: anything holding a discrete GPU awake costs upwards of " +
                       "17 W while reporting almost no CPU at all. Use this to see what was working, then " +
                       "A/B the draw itself to find out what a change is really worth.\n\n" +
                       "The list empties when a setting changes, because the measurement window restarts."
            };
            wc2.Controls.Add(cdot);
            _root.Controls.Add(wc2);

            Card hc = new Card { Width = W, Height = 104, Margin = new Padding(0, 0, 0, 10) };
            hc.Controls.Add(new Label {
                Text = "Battery health", Location = new Point(14, 10), AutoSize = true,
                Font = Theme.Section, ForeColor = Theme.Text, BackColor = Theme.Panel });
            _barHealth = new StackBar {
                Location = new Point(14, 34), Size = new Size(W - 30, 44), Unit = "Wh", Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Empty = "capacity not readable" };
            hc.Controls.Add(_barHealth);
            _healthNote = new Label {
                Location = new Point(14, 80), Size = new Size(W - 30, 18), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel };
            hc.Controls.Add(_healthNote);
            _root.Controls.Add(hc);

            // ---------------------------------------------------------- more
            // Both of these are collapsed and neither is needed on a normal day, so they
            // share one heading and one tab rather than spending two slots in a bar that
            // has to stay scannable.
            _root.Controls.Add(Heading("More", "the two sections most people never open",
                "Two things that matter occasionally rather than daily, kept collapsed so they " +
                "cost nothing until you want them.\n\n" +
                "GPU watch is the one that catches a discrete GPU being held awake, which on a " +
                "switchable-graphics laptop is worth more than every other setting in this app put " +
                "together. It is quiet because the answer is usually 'nothing is', and on a desktop " +
                "or single-GPU laptop there is nothing for it to find at all.\n\n" +
                "Activity is the log of everything this app has changed, with each write read back " +
                "from the registry to confirm it stuck. It opens by itself when something fails, so " +
                "if you have never seen it, nothing has gone wrong."));

            // ---------------------------------------------------------- gpu watch
            // Collapsed like Activity: on most machines it reports nothing to do, and on a
            // desktop or single-GPU laptop there is nothing here at all. It stays one click
            // away rather than taking a screen of space to say "all clear".
            _watchToggle = new SectionToggle {
                Width = W, Caption = "GPU watch",
                Sub = "only matters on a switchable-graphics laptop",
                Margin = new Padding(0, 10, 0, 0)
            };
            _watchToggle.Toggled += (s, e) => {
                _watchBox.Visible = _watchToggle.Expanded;
                _root.PerformLayout();
                MarkNav();
            };
            _root.Controls.Add(WithInfo(_watchToggle, "GPU watch",
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

            _watchBox = new FlowLayoutPanel {
                Width = W, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                BackColor = Theme.Ink, Margin = new Padding(0), Visible = false
            };

            _watchCard = new Card { Width = W, Height = 278, Margin = new Padding(0, 0, 0, 10) };

            _watchSummary = new Label {
                Location = new Point(14, 12), Size = new Size(W - 30, 22),
                Font = Theme.Title, ForeColor = Theme.Save, BackColor = Theme.Panel, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _watchCard.Controls.Add(_watchSummary);

            _watchList = new Panel {
                Location = new Point(14, 40), Size = new Size(W - 30, 180), BackColor = Theme.Panel, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _watchCard.Controls.Add(_watchList);

            _watchButtons = new Panel {
                Location = new Point(14, 228), Size = new Size(W - 30, 38), BackColor = Theme.Panel, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
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

            _watchBox.Controls.Add(_watchCard);
            _root.Controls.Add(_watchBox);

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

            // built last: every section has to exist before the bar can point at them
            BuildNav();
            // Scrolled covers the wheel as well as the scrollbar - the stock Scroll event
            // does not, so the strip used to go stale the moment anyone spun the wheel
            _root.Scrolled += (s, e) => MarkNav();
        }

        /// <summary>
        /// The bar under the header: one button per section, which scrolls the column to
        /// it. The whole app is one list about three screens long, so finding the charts
        /// or the GPU watch used to mean scrolling past everything else to get there.
        /// Nothing is hidden - this only moves you, so every section stays on one page.
        /// </summary>
        void BuildNav()
        {
            _navBar = new Panel {
                Location = new Point(Chrome, _readout.Bottom + 10), Width = W, Height = 36,
                BackColor = Theme.Ink,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            // The strip's baseline. Children paint over their parent, so the tabs are made
            // two pixels shorter than the bar and this rule runs underneath them - drawn at
            // the same height as the tabs it was hidden behind them, showing through only in
            // the gaps as a row of dashes.
            _navBar.Paint += (s, e) => {
                Theme.Quality(e.Graphics);
                using (Pen p = new Pen(Theme.Edge, 1f))
                    e.Graphics.DrawLine(p, 0, _navBar.Height - 1, _navBar.Width - 4, _navBar.Height - 1);
            };

            // GPU watch and Activity share the More entry - both are collapsed, both sit
            // under that heading, and two tabs for two things nobody opens daily crowded
            // out the ones people do use.
            string[] names = {
                "Make it last longer", "Profiles", "What changes your watts", "Analytics", "More"
            };
            string[] labels = { "Fixes", "Profiles", "Settings", "Analytics", "More" };

            int x = 0;
            for (int i = 0; i < names.Length; i++)
            {
                Control target;
                if (!_sections.TryGetValue(names[i], out target)) continue;

                Control local = target;
                int tw;
                using (Graphics g = CreateGraphics())
                    tw = (int)Math.Ceiling(Theme.TextW(g, labels[i], Theme.Tab));

                PillButton b = new PillButton {
                    Text = labels[i], Tab = true, Location = new Point(x, 0),
                    Size = new Size(Math.Max(62, tw + 26), _navBar.Height - 2), BackColor = Theme.Ink
                };
                b.Click += (s, e) => JumpTo(local);
                _navBar.Controls.Add(b);
                _navBtns.Add(b);
                _navTargets.Add(local);
                x += b.Width + 2;
            }

            _chrome.Controls.Add(_navBar);

            // Sits exactly on top of the section bar and takes its place while the app is
            // working. Same bounds, so nothing reflows mid-operation, and the bar you cannot
            // use during a write is the one being covered.
            _busyBar = new Panel {
                Location = _navBar.Location, Size = _navBar.Size, BackColor = Theme.Ink,
                Visible = false, Anchor = _navBar.Anchor
            };
            _busyBar.Paint += (s, e) => PaintBusy(e.Graphics);
            _chrome.Controls.Add(_busyBar);
            _busyBar.BringToFront();

            _chrome.Height = _navBar.Bottom + 10;
        }

        /// <summary>
        /// Progress while settings are being written. Determinate rather than a spinner
        /// because the work is a known list of writes - powercfg is shelled out to twice per
        /// setting, so a profile is eighteen processes and takes seconds. Before this the
        /// window simply froze.
        /// </summary>
        void PaintBusy(Graphics g)
        {
            Theme.Quality(g);
            g.Clear(Theme.Ink);
            int h = _busyBar.Height - 8;
            Rectangle track = new Rectangle(0, (_busyBar.Height - h) / 2, _busyBar.Width - 4, h);
            Theme.FillRound(g, track, 4, Theme.Inset, Theme.Edge);

            if (_busyTotal > 0)
            {
                int w = (int)Math.Round((double)_busyDone / _busyTotal * (track.Width - 2));
                if (w > 0) Theme.FillRound(g, new Rectangle(track.X + 1, track.Y + 1, w, track.Height - 2), 3, Theme.Save);
            }
            Theme.Str(g, _busyText, Theme.Body, Theme.Text, 10, track.Y + (h - 16) / 2f);
            if (_busyTotal > 0)
                Theme.StrRight(g, _busyDone + " of " + _busyTotal, Theme.Small, Theme.Dim,
                               track.Right - 10, track.Y + (h - 14) / 2f);
        }

        /// <summary>Show the progress bar and lock the column. Always paired with EndBusy.</summary>
        void BeginBusy(string text, int steps)
        {
            _busyText = text; _busyTotal = steps; _busyDone = 0;
            _navBar.Visible = false;
            _busyBar.Visible = true;
            _root.Enabled = false;          // no queuing clicks onto a half-written scheme
            _busyBar.Invalidate();
            _busyBar.Update();
            Application.DoEvents();
        }

        void BusyStep(string text)
        {
            _busyDone++;
            if (text != null) _busyText = text;
            _busyBar.Invalidate();
            _busyBar.Update();
            Application.DoEvents();
        }

        void EndBusy()
        {
            _busyBar.Visible = false;
            _navBar.Visible = true;
            _root.Enabled = true;
            _busyTotal = 0; _busyDone = 0;
        }

        /// <summary>Scroll the column so a section sits just under the pinned header.</summary>
        void JumpTo(Control target)
        {
            if (target == null || _root == null) return;
            // Top is in scrolled coordinates; AutoScrollPosition.Y is zero or negative,
            // so subtracting it recovers where the control sits in the content itself.
            int y = target.Top - _root.AutoScrollPosition.Y - 8;
            if (y < 0) y = 0;
            _root.AutoScrollPosition = new Point(0, y);
            MarkNav();
        }

        /// <summary>Light the button for whichever section the column is showing.</summary>
        void MarkNav()
        {
            if (_root == null || _navBtns.Count == 0) return;
            int view = -_root.AutoScrollPosition.Y;
            int best = 0;
            for (int i = 0; i < _navTargets.Count; i++)
            {
                int top = _navTargets[i].Top - _root.AutoScrollPosition.Y;
                if (top <= view + 24) best = i;
            }

            // At the very bottom the last section can never win that test - Activity sits
            // a few dozen pixels from the end, so its top only clears the viewport top on
            // a window shorter than the section itself. Once the column cannot scroll any
            // further, whatever is last is what you are looking at.
            int maxScroll = _root.DisplayRectangle.Height - _root.ClientSize.Height;
            if (maxScroll > 0 && view >= maxScroll - 4) best = _navTargets.Count - 1;
            for (int i = 0; i < _navBtns.Count; i++)
            {
                bool on = i == best;
                if (_navBtns[i].Selected != on) { _navBtns[i].Selected = on; _navBtns[i].Invalidate(); }
            }
        }

        /// <summary>
        /// Stretch what was built at the design width out to the window. Cards get set
        /// explicitly because a FlowLayoutPanel ignores Anchor on its own children; the
        /// controls inside each card are anchored and follow on their own.
        /// </summary>
        void Relayout()
        {
            if (_root == null) return;

            int avail = ClientSize.Width - Chrome - 6 - SystemInformation.VerticalScrollBarWidth;
            int w = Math.Max(W, Math.Min(WMax, avail));

            _chrome.Width = ClientSize.Width;
            _root.Location = new Point(0, _chrome.Height);
            _root.Size = new Size(ClientSize.Width, Math.Max(80, ClientSize.Height - _chrome.Height));

            // centre the column when the window is wider than the column is allowed to be
            int extra = Math.Max(0, avail - w);
            _root.Padding = new Padding(Chrome + extra / 2, 12, 6, 20);
            _readout.Width = w;
            _navBar.Width = w;
            _readout.Left = Chrome + extra / 2;
            _navBar.Left = _readout.Left;

            foreach (Control c in _root.Controls)
            {
                if (c == null) continue;
                // a button keeps the width its label needs - stretching Run as admin
                // across the whole column made it look like the primary action
                if (c is PillButton) continue;
                // the AutoSize collapsible boxes size themselves from their children
                if (c == _basicBox || c == _watchBox)
                { foreach (Control k in c.Controls) k.Width = w; continue; }
                c.Width = w;
            }

            // the three profile buttons share the row, so they have to be re-spaced
            // rather than anchored - anchoring all three would overlap them
            if (_presetBtns.Count > 0)
            {
                int bw = (w - (_presetBtns.Count - 1) * 8) / _presetBtns.Count;
                int px = 0;
                foreach (PillButton b in _presetBtns)
                {
                    b.Location = new Point(px, 0);
                    b.Width = bw;
                    px += bw + 8;
                }
            }

            if (_fixList != null)
                foreach (Control card in _fixList.Controls) card.Width = _fixList.Width;

            if (_watchList != null)
                foreach (Control line in _watchList.Controls) line.Width = _watchList.Width;

            _root.PerformLayout();
            MarkNav();
        }

        /// <summary>
        /// Section heading, with an info icon explaining the section itself.
        ///
        /// A rule across the column, then a generous gap, then the title - so a heading
        /// reads as the start of something rather than as one more line of text. The gap
        /// above is deliberately much larger than the gap below: that is what attaches a
        /// heading to the cards under it instead of leaving it floating between two
        /// sections, which is how the old 13px inline label read.
        /// </summary>
        Panel Heading(string text, string sub, string info)
        {
            Panel host = new Panel {
                Width = W, Height = 46, BackColor = Theme.Ink, Margin = new Padding(2, 20, 0, 4)
            };
            _sections[text] = host;

            float tw;
            using (Graphics g = CreateGraphics()) tw = Theme.TextW(g, text, Theme.Head);

            Label l = new Label {
                Text = text, AutoSize = true, Location = new Point(0, 18),
                Font = Theme.Head, ForeColor = Theme.Text, BackColor = Theme.Ink
            };
            host.Controls.Add(l);

            float subX = tw + 10;
            if (!string.IsNullOrEmpty(info))
            {
                InfoDot d = new InfoDot {
                    Location = new Point((int)(tw + 10), 22), Heading = text, Body = info, BackColor = Theme.Ink
                };
                host.Controls.Add(d);
                subX = tw + 34;
            }

            float sx = subX;
            host.Paint += (s, e) => {
                Graphics g = e.Graphics;
                Theme.Quality(g);
                using (Pen p = new Pen(Theme.Edge, 1f))
                    g.DrawLine(p, 0, 0, host.Width - 4, 0);
                if (!string.IsNullOrEmpty(sub))
                    Theme.Str(g, sub, Theme.Small, Theme.Dim, sx, 24);
            };
            return host;
        }

        /// <summary>Wraps a collapsible header so it can carry an info icon too.</summary>
        Panel WithInfo(SectionToggle t, string heading, string info)
        {
            Panel host = new Panel {
                Width = W, Height = t.Height, BackColor = Theme.Ink, Margin = t.Margin
            };
            _sections[heading] = host;
            t.Location = new Point(0, 0);
            t.Width = W - 28;
            t.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            host.Controls.Add(t);
            InfoDot d = new InfoDot {
                Location = new Point(W - 24, (t.Height - 18) / 2),
                Heading = heading, Body = info, BackColor = Theme.Ink,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            host.Controls.Add(d);
            d.BringToFront();
            return host;
        }

        /// <summary>
        /// Which side the settings below write to. Battery is the default; plugged in is
        /// opt-in and says so plainly while it is on, because on that side a change costs
        /// you nothing in runtime and there is no measurement to check it against.
        /// </summary>
        Panel BuildSideSwitch()
        {
            Panel host = new Panel {
                Width = W, Height = 62, BackColor = Theme.Ink, Margin = new Padding(0, 0, 0, 10)
            };

            _sideDc = new PillButton {
                Text = "On battery", Tab = true, Location = new Point(0, 0),
                Size = new Size(128, 30), Selected = true, BackColor = Theme.Ink
            };
            _sideAc = new PillButton {
                Text = "Plugged in", Tab = true, Location = new Point(130, 0),
                Size = new Size(128, 30), BackColor = Theme.Ink
            };
            _sideDc.Click += (s, e) => SetSide(false);
            _sideAc.Click += (s, e) => SetSide(true);
            host.Controls.Add(_sideDc);
            host.Controls.Add(_sideAc);

            _sideNote = new Label {
                Location = new Point(2, 36), Size = new Size(W - 4, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Ink,
                Text = "Editing the battery side. This is what the profiles, the suggestions and the "
                     + "restore point write."
            };
            host.Controls.Add(_sideNote);
            return host;
        }

        void SetSide(bool ac)
        {
            _acMode = ac;
            _sideDc.Selected = !ac; _sideDc.Invalidate();
            _sideAc.Selected = ac;  _sideAc.Invalidate();
            _sideNote.Text = ac
                ? "Editing the plugged-in side. Battery values are untouched, and nothing here changes "
                  + "your runtime - on mains there is no drain to measure against."
                : "Editing the battery side. This is what the profiles, the suggestions and the "
                  + "restore point write.";
            _sideNote.ForeColor = ac ? Theme.Spend : Theme.Dim;
            LoadValues();
            Log(ac ? "Switched to editing the plugged-in side."
                   : "Switched back to editing the battery side.");
        }

        Card BuildRow(Knob k)
        {
            Card c = new Card { Width = W, Height = 110, Margin = new Padding(0, 0, 0, 8) };
            Row row = new Row { Knob = k, Host = c };

            Label title = new Label {
                Text = k.Label, Location = new Point(14, 11), Size = new Size(W - 200, 20),
                Font = Theme.Section, ForeColor = Theme.Text, BackColor = Theme.Panel,
                AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c.Controls.Add(title);

            if (k.Hidden)
            {
                Label badge = new Label {
                    Text = "hidden by Windows", AutoSize = true, Font = Theme.Small, ForeColor = Theme.Dim,
                    BackColor = Theme.Panel, Location = new Point(W - 176, 13), Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                c.Controls.Add(badge);
            }

            InfoDot dot = new InfoDot {
                Location = new Point(W - 34, 12), Heading = k.Label, Body = k.Info, Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            c.Controls.Add(dot);

            Label note = new Label {
                Text = k.Note, Location = new Point(14, 31), Size = new Size(W - 40, 18),
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel,
                AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
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
                    Accent = Theme.Save, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
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
                    TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                c.Controls.Add(row.Value);
            }

            // Two fixed columns rather than one run-on line, so the battery and plugged-in
            // values line up down the whole page and can be compared by eye. Inline they
            // started at a different x on every card, which made them unscannable.
            row.Status = new Label {
                Text = "", Location = new Point(14, 84), Size = new Size(SideCol - 20, 18),
                Font = Theme.Small, ForeColor = Theme.Text, BackColor = Theme.Panel,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            c.Controls.Add(row.Status);

            row.Status2 = new Label {
                Text = "", Location = new Point(SideCol, 84), Size = new Size(W - SideCol - 16, 18),
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c.Controls.Add(row.Status2);

            _rows.Add(row);
            return c;
        }

        /// <summary>
        /// Bring the window back, from the tray or from a second launch of the shortcut.
        /// Restore before Activate: activating a minimised window leaves it minimised.
        /// </summary>
        void ShowFromTray()
        {
            Show();
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

        /// <summary>
        /// Wait for a second launch to signal us. On its own thread because WaitOne blocks,
        /// and marshalled back with BeginInvoke because touching the window from a worker
        /// thread is not allowed. Started from OnShown - in the constructor the handle does
        /// not exist yet and BeginInvoke throws.
        /// </summary>
        void ListenForRelaunch()
        {
            bool mine;
            try
            {
                _wake = new System.Threading.EventWaitHandle(
                    false, System.Threading.EventResetMode.AutoReset, WakeEvent, out mine);
            }
            catch (Exception) { return; }

            System.Threading.Thread t = new System.Threading.Thread(delegate ()
            {
                while (true)
                {
                    try { _wake.WaitOne(); } catch (Exception) { return; }
                    if (_quitting) return;
                    try { BeginInvoke(new Action(ShowFromTray)); }
                    catch (Exception) { return; }
                }
            });
            t.IsBackground = true;      // must never hold the process open
            t.Start();
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
            menu.Items.Add("Open PowerDial", null, (s, e) => ShowFromTray());
            menu.Items.Add("Quit PowerDial", null, (s, e) => Quit());

            _tray = new NotifyIcon { Icon = Icon, Visible = true, Text = "PowerDial", ContextMenuStrip = menu };
            _tray.DoubleClick += (s, e) => ShowFromTray();
        }

        /// <summary>Actually exit, rather than hide. The tray menu and nothing else.</summary>
        void Quit()
        {
            _quitting = true;
            if (_wake != null) { try { _wake.Set(); } catch (Exception) { } }
            if (_tray != null) _tray.Visible = false;
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !_quitting)
            {
                e.Cancel = true;
                Hide();

                // Say where it went, once. Otherwise closing the window looks like quitting,
                // and the app is left running with no obvious way back to it.
                if (!_saidWhereItWent && _tray != null)
                {
                    _saidWhereItWent = true;
                    try
                    {
                        _tray.ShowBalloonTip(4000, "PowerDial is still running",
                            "It keeps recording here in the notification area. Open it again from the " +
                            "shortcut or this icon, and use Quit PowerDial to close it for good.",
                            ToolTipIcon.Info);
                    }
                    catch (Exception) { }
                }
                return;
            }
            _quitting = true;
            if (_wake != null) { try { _wake.Set(); } catch (Exception) { } }
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
                    // the controls always show the side being edited; the footer shows both
                    int? dc = PowerCfg.Read(r.Knob, true);
                    int? ac = PowerCfg.Read(r.Knob, false);
                    int? v = _acMode ? ac : dc;
                    bool expl = PowerCfg.IsExplicit(r.Knob, !_acMode);

                    // the side being edited is the readable one; the other stays quiet
                    r.Status.Text = "on battery   " + PowerCfg.Describe(r.Knob, dc) +
                                    (!_acMode && !expl ? "   (inherited)" : "");
                    r.Status2.Text = "plugged in   " + PowerCfg.Describe(r.Knob, ac) +
                                     (_acMode && !expl ? "   (inherited)" : "");
                    r.Status.ForeColor = _acMode ? Theme.Dim : Theme.Text;
                    r.Status2.ForeColor = _acMode ? Theme.Text : Theme.Dim;

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

            // the average runtime in the header comes from these same recorded points, so
            // it is recomputed here rather than anywhere it could drift out of step
            _avg = RuntimeAverage.From(pts);
            PushAverage();

            History.Stats st = History.Summarise(pts);
            string line = st.Points + " minutes recorded";
            if (st.BatteryPoints > 0)
                line += "   ·   " + FmtHours(st.BatteryMinutes / 60.0) + " of it on battery, averaging " +
                        st.AvgWatts.ToString("0.00") + " W (" + st.MinWatts.ToString("0.00") + " to " +
                        st.MaxWatts.ToString("0.00") + ")";
            double? full = _avg.FromFull(_bat.FullChargeMwh);
            if (full.HasValue)
                line += "   ·   " + FmtHours(full.Value) + " from a full charge at that average";
            long bytes = History.DiskBytes();
            line += "   ·   " + (bytes / 1024.0).ToString("0") + " KB on disk";
            _histLabel.Text = line;

            RefreshOffenders();
        }

        /// <summary>
        /// Turn the measured average draw into the figures the header shows. Kept apart
        /// from RefreshHistory because the capacity it divides into comes from the battery
        /// poll, which runs four times a minute against history's once.
        /// </summary>
        void PushAverage()
        {
            if (_readout == null) return;
            _readout.Avg = _avg;
            _readout.AvgFromFull = _avg.FromFull(_bat.FullChargeMwh);
            _readout.AvgLeft = _avg.Left(_bat.RemainingMwh);
            _readout.AvgBest = _avg.Best(_bat.FullChargeMwh);
            _readout.AvgWorst = _avg.Worst(_bat.FullChargeMwh);
            _readout.Invalidate();
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

        /// <summary>Record this poll's CPU and drop anything older than the draw window.</summary>
        void TrackWindowCpu()
        {
            DateTime now = DateTime.UtcNow;
            double secs = _lastPoll == DateTime.MinValue ? 0 : (now - _lastPoll).TotalSeconds;
            _lastPoll = now;
            if (secs <= 0.5 || secs > 300) return;      // first poll, or we were asleep

            Dictionary<string, double> core = new Dictionary<string, double>();
            foreach (ProcInfo p in _procs)
            {
                if (p.CpuPercent <= 0.05) continue;
                core[p.Name] = p.CpuPercent / 100.0 * secs;
            }
            _window.Add(new PollCpu { At = now, Core = core });

            while (_window.Count > 0 &&
                   (now - _window[0].At).TotalSeconds > _bat.WindowSeconds + 5)
                _window.RemoveAt(0);
        }

        /// <summary>What burned the most CPU across the retained window, core-seconds.</summary>
        List<KeyValuePair<string, double>> WindowTop(int take)
        {
            Dictionary<string, double> sum = new Dictionary<string, double>();
            foreach (PollCpu s in _window)
                foreach (KeyValuePair<string, double> kv in s.Core)
                {
                    double had;
                    sum.TryGetValue(kv.Key, out had);
                    sum[kv.Key] = had + kv.Value;
                }

            List<KeyValuePair<string, double>> list = new List<KeyValuePair<string, double>>(sum);
            list.Sort(delegate (KeyValuePair<string, double> a, KeyValuePair<string, double> b)
            { return b.Value.CompareTo(a.Value); });
            if (list.Count > take) list.RemoveRange(take, list.Count - take);
            return list;
        }

        /// <summary>
        /// Start the draw measurement over. Every setting change has to do this or the next
        /// reading averages across the change, and the countdown in the header has to
        /// restart with it or it counts down to a reading that is not coming.
        /// </summary>
        void ResetDrawWindow()
        {
            _bat.ResetWindow();
            _spark.Clear();
            _window.Clear();
            _windowStart = DateTime.UtcNow;
            if (_readout != null) _readout.MeasuringLeft = _bat.WindowSeconds;
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
            // the window restarts on every setting change, so count from that moment
            if (_bat.Watts.HasValue) { _windowStart = DateTime.MinValue; _readout.MeasuringLeft = 0; }
            else
            {
                if (_windowStart == DateTime.MinValue) _windowStart = DateTime.UtcNow;
                int left = _bat.WindowSeconds - (int)(DateTime.UtcNow - _windowStart).TotalSeconds;
                _readout.MeasuringLeft = Math.Max(0, left);
            }
            _readout.Remaining = _bat.HoursRemaining;
            _readout.ChargePct = _bat.PercentOfFull;
            _readout.Health = _bat.HealthPercent;
            _readout.Mwh = _bat.RemainingMwh;
            _readout.FullMwh = _bat.FullChargeMwh;
            PushAverage();
            _readout.Invalidate();
            RefreshAnalytics();
            RefreshContributors();

            string tip = "PowerDial  " + (_bat.OnAc ? "on AC" :
                (_bat.Watts.HasValue ? _bat.Watts.Value.ToString("0.0") + " W" : "measuring")) +
                "  " + _bat.PercentOfFull + "%";
            if (_tray != null) _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        /// <summary>
        /// The contributors list: core-seconds per process across the measurement window,
        /// drawn as a share bar. No watt figures, on purpose - see the info text.
        /// </summary>
        void PaintContributors(Graphics g)
        {
            Theme.Quality(g);
            g.Clear(Theme.Panel);

            List<KeyValuePair<string, double>> top = WindowTop(5);
            if (top.Count == 0)
            {
                Theme.Str(g, _bat.OnAc
                    ? "nothing timed yet - readings only happen on battery"
                    : "collecting - the first window takes " + _bat.WindowSeconds + " seconds",
                    Theme.Small, Theme.Dim, 0, 4);
                return;
            }

            double max = top[0].Value;
            if (max <= 0) max = 1;
            int y = 0;
            foreach (KeyValuePair<string, double> kv in top)
            {
                int barW = (int)Math.Round(kv.Value / max * (_contribList.Width - 210));
                if (barW < 2) barW = 2;
                Theme.FillRound(g, new Rectangle(150, y + 5, barW, 10), 3, Theme.Spend);
                Theme.Str(g, kv.Key, Theme.Body, Theme.Text, 0, y);
                Theme.StrRight(g, kv.Value.ToString("0.0") + " cpu-s", Theme.Small, Theme.Dim,
                               _contribList.Width, y + 2);
                y += 21;
            }
        }

        void RefreshContributors()
        {
            if (_contribList == null) return;
            int secs = 0;
            if (_window.Count > 0)
                secs = (int)Math.Round((DateTime.UtcNow - _window[0].At).TotalSeconds);
            _contribNote.Text = _window.Count == 0
                ? "Ranked by CPU over the same window the watts figure was timed across. Not watts - " +
                  "an idle-but-awake GPU costs far more than anything here while using no CPU."
                : "Over the last " + secs + "s, the window the watts figure was timed across. " +
                  "Core-seconds, not watts - what was working, not what it cost.";
            _contribList.Invalidate();
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
                    Location = new Point(0, y), Size = new Size(_watchList.Width, 22),
                    BackColor = Theme.Panel, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
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

            // Check again sits after Apply all, or takes its place when there is nothing to
            // apply. Left at a fixed offset it floated in from the edge with a gap beside
            // it, which read as a mistake every time the list came back empty.
            _fixRecheck.Left = _fixApplyAll.Visible ? _fixApplyAll.Right + 8 : 0;

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
                Fill = Theme.Inset, Border = Theme.Edge, Radius = 8, BackColor = Theme.Panel,
                Lift = false          // nested inside a card already; a second highlight is noise
            };

            Label title = new Label {
                Text = s.Title, Location = new Point(12, 9), Size = new Size(w - 190, 20),
                Font = Theme.Title, ForeColor = Theme.Text, BackColor = Theme.Inset,
                AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c.Controls.Add(title);

            InfoDot dot = new InfoDot {
                Location = new Point(w - 28, 10), Heading = s.Title, Body = s.Info,
                BackColor = Theme.Inset, Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            c.Controls.Add(dot);

            Label detail = new Label {
                Text = s.Detail, Location = new Point(12, 30), Size = new Size(w - 34, 32),
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Inset, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
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
                    Theme.StrRight(g, "~" + local.Gain.Value.ToString("0.0") + " W", Theme.Value, Theme.Save, c.Width - 34, 6);
                string change = local.Change;
                if (change.Length > 0)
                    Theme.StrRight(g, change, Theme.Small, Theme.Dim, c.Width - 14, 70);
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

            ResetDrawWindow();
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

            BeginBusy("Applying " + doable.Count + " change(s)", doable.Count + 1);
            int ok = 0, fail = 0;
            foreach (Suggestion s in doable)
            {
                BusyStep(s.Title);
                string err = Advisor.Apply(s);
                if (err != null) { Log("   " + s.Title + ": " + err); fail++; }
                else { Log("   " + s.Title + " -> " + s.ThenText); ok++; }
            }
            Log("Applied " + ok + " suggestion(s)" + (fail > 0 ? ", " + fail + " failed" : "") + ".");
            if (fail > 0) ShowLog();

            foreach (PillButton b in _presetBtns) b.Selected = false;
            ResetDrawWindow();
            BusyStep("Re-checking what is left");
            LoadValues(); RefreshWatch(); RefreshFixes();
            EndBusy();
        }

        void ApplyKnob(Row row, int value)
        {
            bool dc = !_acMode;
            string side = dc ? "on battery" : "plugged in";
            string err = PowerCfg.Write(row.Knob, value, dc);
            if (err != null)
            {
                Log("Could not change " + row.Knob.Label + " " + side + ": " + err);
                ShowLog();
            }
            else
            {
                // read the same side straight back - never claim a write we have not confirmed
                int? back = PowerCfg.Read(row.Knob, dc);
                if (back.HasValue && back.Value == value)
                    Log(row.Knob.Label + " " + side + " set to " + PowerCfg.Describe(row.Knob, value));
                else
                    Log(row.Knob.Label + " " + side + " did not stick - it reads back as " +
                        PowerCfg.Describe(row.Knob, back));

                // only a battery-side change invalidates the draw measurement
                if (dc) ResetDrawWindow();
            }
            LoadValues();
            RefreshFixes();
        }

        void CommitBrightness(int v)
        {
            string err = Brightness.Set(v);
            if (err != null) { Log("Could not change brightness: " + err); ShowLog(); }
            else { Log("Brightness set to " + v + "%"); ResetDrawWindow(); }
            UpdateBrightLabels();
            RefreshFixes();
        }

        void ApplyPreset(Preset p)
        {
            Log("Applying " + p.Name + "...");
            BeginBusy("Applying " + p.Name, p.Values.Count + (p.Brightness.HasValue ? 1 : 0));
            int ok = 0, fail = 0;
            foreach (KeyValuePair<string, int> kv in p.Values)
            {
                Knob k = PowerCfg.Find(kv.Key);
                if (k == null) continue;
                BusyStep(p.Name + " - " + k.Label);
                string err = PowerCfg.WriteDc(k, kv.Value);
                if (err != null) { Log("   could not set " + k.Label + ": " + err); fail++; continue; }
                int? back = PowerCfg.Read(k, true);
                if (back.HasValue && back.Value == kv.Value) { Log("   " + k.Label + " -> " + PowerCfg.Describe(k, kv.Value)); ok++; }
                else { Log("   " + k.Label + " did not stick"); fail++; }
            }
            if (p.Brightness.HasValue)
            {
                BusyStep(p.Name + " - screen brightness");
                string berr = Brightness.Set(p.Brightness.Value);
                if (berr != null) { Log("   could not set brightness: " + berr); fail++; }
                else { Log("   brightness -> " + p.Brightness.Value + "%"); ok++; }
            }
            Log(p.Name + ": " + ok + " changed" + (fail > 0 ? ", " + fail + " failed" : "") + ".");
            if (fail > 0) ShowLog();

            foreach (PillButton b in _presetBtns) b.Selected = (b.Text == p.Name);
            ResetDrawWindow();
            BusyStep("Re-reading what changed");
            LoadValues(); RefreshWatch(); RefreshFixes();
            EndBusy();
        }

        void RestoreBaseline()
        {
            BaselineFile bf = Baseline.Load();
            string when = bf.CapturedUtc == "(none)" ? "the tuning session" : bf.CapturedUtc + " UTC";
            string bright = bf.Brightness.HasValue
                ? "Screen brightness goes back to " + bf.Brightness.Value + "%.\n"
                : "Screen brightness is left alone - this restore point predates the app\n" +
                  "recording it, so there is no level to go back to.\n";
            int withAc = 0;
            foreach (BaselineEntry e in bf.Entries) if (e.AcValue.HasValue) withAc++;
            string acLine = withAc > 0
                ? "Both sides are restored: " + bf.Entries.Count + " on battery, " + withAc + " plugged in.\n"
                : bf.Entries.Count + " battery settings will be rewritten. Plugged-in values are left\n" +
                  "alone - this restore point predates the app recording them.\n";
            DialogResult r = MessageBox.Show(
                "Put the settings back as they were before PowerDial?\n\n" +
                "Restore point: " + when + "\n" +
                acLine + bright,
                "Restore my settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;

            // Restore writes both sides of every setting in one call, so there is no
            // per-step hook to hang progress on. Say what is happening rather than freeze.
            BeginBusy("Putting your settings back", 0);
            List<string> lines = Baseline.Restore();
            foreach (string line in lines) Log(line);
            foreach (PillButton b in _presetBtns) b.Selected = false;
            ResetDrawWindow();
            _busyText = "Re-reading every setting";
            _busyBar.Invalidate(); _busyBar.Update();
            LoadValues(); RefreshWatch(); RefreshFixes();
            EndBusy();
            ShowLog();
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

        /// <summary>Seconds until the first draw reading lands. 0 once it has.</summary>
        public int MeasuringLeft;

        /// <summary>The measured average, and what it implies. Null figures print as
        /// "not measured yet" - never as a borrowed number.</summary>
        public RuntimeAverage Avg = new RuntimeAverage();
        public double? AvgFromFull, AvgLeft, AvgBest, AvgWorst;

        /// <summary>Y of the hairline under the live row - the sparkline sits above it.</summary>
        public const int AvgBandTop = 106;

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
            else
            {
                // a live countdown rather than a fixed "60s": the first reading genuinely
                // takes a minute, and a number that never moves reads as broken
                big = "--";
                unit = MeasuringLeft > 0
                    ? "measuring, " + MeasuringLeft + "s left"
                    : "measuring";
                c = Theme.Dim;
            }

            Theme.Str(g, big, Theme.Readout, c, 14, 8);
            float bw = Theme.TextW(g, big, Theme.Readout);
            Theme.Str(g, unit, Theme.Small, Theme.Dim, 18, 56);

            // Four readings on an even grid across the header, the way an instrument
            // cluster is laid out. Clustered left they left a dead gap across the middle
            // once the window could be resized; clustered right they left it on the left
            // and crowded the state line. A grid fills the row at any width.
            string[] vals = {
                Remaining.HasValue ? Fmt(Remaining.Value) : "--",
                ChargePct + "%",
                Health.HasValue ? Health.Value.ToString("0") + "%" : "--"
            };
            string[] labs = { "left", "charge", "health" };
            Color[] cols = {
                Theme.Text, Theme.Text,
                Health.HasValue && Health.Value < 70 ? Theme.Spend : Theme.Text
            };

            float col = (Width - 28) / 4f;
            for (int i = 0; i < 3; i++)
            {
                // never let a long watts figure run into the first column
                float sx = Math.Max(14 + bw + 30, 14 + col * (i + 1));
                Stat(g, sx, vals[i], labs[i], cols[i]);
            }

            string state = OnAc ? (Charging ? "on AC, charging" : "on AC") : "on battery";
            if (Mwh.HasValue && FullMwh.HasValue) state += "   ·   " + Mwh.Value.ToString("N0") + " of " + FullMwh.Value.ToString("N0") + " mWh";
            Theme.StrRight(g, state, Theme.Small, Theme.Dim, Width - 16, 56);

            PaintAverage(g);
        }

        /// <summary>
        /// The band under the trace: how long the battery lasts on the draw this PC has
        /// actually recorded, rather than on whatever this one minute happens to be doing.
        /// The live figure above swings by several watts as the CPU breathes; this is the
        /// one that answers "how long does it last".
        /// </summary>
        void PaintAverage(Graphics g)
        {
            int top = AvgBandTop;
            using (Pen p = new Pen(Theme.Edge, 1f))
                g.DrawLine(p, 14, top, Width - 14, top);

            Theme.Str(g, "TYPICAL, MEASURED HERE", Theme.Small, Theme.Dim, 14, top + 7);

            if (!Avg.Known)
            {
                Theme.Str(g, "not measured yet", Theme.Value, Theme.Dim, 14, top + 26);
                Theme.StrRight(g,
                    "time on battery is recorded a minute at a time - this fills in as you use it",
                    Theme.Small, Theme.Dim, Width - 14, top + 31);
                return;
            }

            // three figures, all of them measured arithmetic: mean draw, and what that
            // draw does to a full pack and to what is left in it right now
            float x = 14;
            x += Band(g, x, top, Avg.Watts.ToString("0.00") + " W", "average draw",
                      Avg.Watts > 12 ? Theme.Spend : Theme.Save);
            x += Band(g, x, top, HM(AvgFromFull), "from a full charge", Theme.Text);
            x += Band(g, x, top, HM(AvgLeft), "left at that rate", Theme.Text);

            string prov = "over " + HM(Avg.Minutes / 60.0) + " on battery";
            if (Avg.High > Avg.Low + 0.05)
                prov += "  ·  " + Avg.Low.ToString("0.0") + " to " + Avg.High.ToString("0.0") + " W";
            if (AvgBest.HasValue && AvgWorst.HasValue)
                prov += "  ·  " + HM(AvgWorst) + " to " + HM(AvgBest) + " from full";
            Theme.StrRight(g, prov, Theme.Small, Theme.Dim, Width - 14, top + 47);
        }

        /// <summary>One figure in the average band. Returns how far to step right.</summary>
        float Band(Graphics g, float x, int top, string v, string label, Color c)
        {
            Theme.Str(g, v, Theme.Stat, c, x, top + 22);
            Theme.Str(g, label, Theme.Small, Theme.Dim, x + 2, top + 47);
            float w = Math.Max(Theme.TextW(g, v, Theme.Stat), Theme.TextW(g, label, Theme.Small));
            return w + 28;
        }

        void Stat(Graphics g, float x, string v, string label, Color c)
        {
            // 11/37 rather than 14/40: it lifts the caption clear of the state line, which
            // shares its row with the unit at the far left
            Theme.Str(g, v, Theme.Stat, c, x, 11);
            Theme.Str(g, label, Theme.Small, Theme.Dim, x + 2, 37);
        }

        static string Fmt(double h)
        {
            if (h < 0 || h > 99) return "--";
            int t = (int)Math.Round(h * 60);
            return (t / 60) + ":" + (t % 60).ToString("00");
        }

        /// <summary>"4h 55m" - the form people read a runtime in.</summary>
        static string HM(double? h)
        {
            if (!h.HasValue || h.Value < 0 || h.Value > 99) return "--";
            int t = (int)Math.Round(h.Value * 60);
            return (t / 60) + "h " + (t % 60).ToString("00") + "m";
        }
    }
}
