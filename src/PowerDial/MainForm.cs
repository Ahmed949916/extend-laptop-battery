using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PowerDial
{
    public sealed class MainForm : Form
    {
        // Everything is laid out once at this width, then stretched to the window by
        // Relayout. Building at a fixed width keeps the arithmetic in one place; the
        // stretching is carried by Anchor on the children that should grow.
        const int W = 648;          // design width of the content column
        const int WMax = 1240;      // past this a chart is wider, not clearer
        const int Chrome = 14;      // gutter either side of the column
        const int Rail = 240;       // the sidebar, which every other measurement sits beside
        const int SideCol = 340;    // x of the plugged-in column in a settings row

        sealed class Row
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

        SteadyPanel _root;
        Panel _chrome;              // header + watts strip + section bar, pinned above the scroll
        Card _wattsStrip;           // "Where your watts go", pinned so it never scrolls away
        SavingCard _cardSaving;     // Insights: what the last change was actually worth
        ModeCostCard _cardModeCost; // Insights: what each mode has cost, measured
        Card _cardHealth;           // Battery health, its own page
        Card _cardHealthAdvice;     // and the one honest thing to say about wear
        Label _healthPct;           // the headline percentage
        StatusCard _cardStatus;     // Overview: the battery, stated once
        AdviceCard _cardAdvice;     // Overview: the one thing worth doing
        ModeSummaryCard _cardMode;  // Overview: which mode is in effect
        TopAppsCard _cardApps;      // Overview: what is keeping the processor busy
        Panel _overviewPair;        // holds the mode and apps cards side by side
        PageTitle _pageTitle;       // the name of the section on screen
        readonly List<ModeCard> _modeCards = new List<ModeCard>();
        Panel _profileRow;          // the old row of four identical buttons, now retired
        Panel _bottomPad;           // the floor under the last card, on every page
        Suggestion _adviceFix;      // what the Overview primary button acts on, if anything

        NavRail _nav;               // the sidebar - the app's one navigation model
        StatusBlock _status;        // battery summary pinned to the foot of the sidebar
        string _page = "overview";  // which section is on screen

        /// <summary>Preset.Code of the profile the machine currently matches, 0 for custom.
        /// Read from the machine after any change, and written into every history point.</summary>
        int _modeCode;
        Label _wattsTotal;          // the measured figure the list is describing
        readonly Dictionary<string, Control> _sections = new Dictionary<string, Control>();
        Readout _readout;
        Sparkline _spark;
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
        PillButton _btnRestore;
        List<Suggestion> _fixes = new List<Suggestion>();
        List<Finding> _findings;
        bool _fixesSeeded;
        LineChart _chartWatts;

        // How far back both charts look. History is one point a minute, so these are
        // minutes; 0 means everything kept on disk.
        int _rangeMin = 1440;
        readonly List<PillButton> _rangeBtns = new List<PillButton>();
        BatteryChart _batChart;
        ProcessTable _procTable, _offTable;
        StackBar _barHealth;
        Label _healthNote, _histLabel, _procLabel, _procTotals, _contribNote;
        PillButton _btnMem, _btnCpu, _btnBg;
        Panel _contribList;
        PillButton _fixRecheck;

        PillButton _busyBtn;            // the button that was clicked, spinning until done
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
        sealed class PollCpu { public DateTime At; public Dictionary<string, double> Core; }
        readonly List<PollCpu> _window = new List<PollCpu>();
        DateTime _lastPoll = DateTime.MinValue;
        DateTime _windowStart = DateTime.MinValue;   // when the current draw window began
        bool _sortCpu;
        bool _bgOnly = true;
        DateTime _lastHistory = DateTime.MinValue;
        PillButton _adminBtn;
        Panel _adminBox;            // the Run as admin button and the line explaining it
        bool _elevated;             // read once: it cannot change without a restart
        readonly List<PillButton> _presetBtns = new List<PillButton>();

        NotifyIcon _tray;
        bool _loading;

        /// <summary>
        /// Named event a second launch sets to ask this copy to show its window. Lives here
        /// rather than on Program because the test projects compile MainForm but not Program.
        /// </summary>
        public const string WakeEvent = "PowerDial.ShowWindow";

        System.Threading.EventWaitHandle _wake;
        // Written on the UI thread by Quit/OnFormClosing, read by the relaunch-wait thread.
        // Without volatile the JIT is free to hoist that read out of the wait loop.
        volatile bool _quitting;
        bool _saidWhereItWent;

        public MainForm()
        {
            Text = "PowerDial";
            BackColor = Theme.Ink;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            StartPosition = FormStartPosition.CenterScreen;

            // The manifest declares this app PerMonitorV2 aware, which tells Windows not to
            // bitmap-scale it because the app scales itself. It did not, so on any display
            // above 100% the whole interface rendered too small. Point-sized fonts in
            // Theme.cs handle the text; this is what scales the control bounds around it.
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(980, 900);
            MinimumSize = new Size(W + Rail + 34, 560);
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
            // BuildUi applies the saved mode before any of this has run, so the Basic card
            // is first drawn with no battery reading and no history. Fill it in now rather
            // than leaving it saying "not measured yet" until the first poll fifteen
            // seconds later - the data is already here.
            RefreshBasic();
            if (pruned > 0) Log("Cleared " + pruned + " history file(s) older than " + History.KeepMonths + " months.");

            Log("Watching " + PowerCfg.ActiveSchemeName() +
                (_elevated ? ", running as admin." : ", not running as admin."));
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
            int wantW = Math.Min(WMax + Rail + 34, Math.Max(W + Rail + 34, (int)(work.Width * 0.68)));
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

        /// <summary>
        /// Light the focus rings, because the keyboard is being used to move around.
        ///
        /// Control.Focused is just as true after a mouse click, and a ring drawn on every
        /// click is one people learn to ignore - CSS solves this with :focus-visible and
        /// WinForms has no equivalent. Tab and the arrows are handled here rather than in
        /// each widget because they never reach a control's OnKeyDown: the form consumes
        /// them as navigation before the control ever sees them.
        /// </summary>
        protected override bool ProcessDialogKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Tab || k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down)
                Theme.KeyboardNav = true;
            return base.ProcessDialogKey(keyData);
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
                Location = new Point(Chrome, 12), Width = W, Height = 180,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _spark = new Sparkline {
                Location = new Point(16, 72), Size = new Size(W - 32, 28), Ceiling = 16,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _readout.Controls.Add(_spark);
            _readout.Top = 38;                 // under the Basic / Advanced switch
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
                Size = new Size(176, 36), Primary = true, BackColor = Theme.Panel
            };
            _fixApplyAll.Click += (s, e) => ApplyAllFixes();
            _fixButtons.Controls.Add(_fixApplyAll);
            _fixRecheck = new PillButton {
                Text = "Check again", Glyph = Theme.GlyphRefresh, Location = new Point(186, 3),
                Size = new Size(140, 36), BackColor = Theme.Panel
            };
            _fixRecheck.Click += (s, e) => RunBusy(_fixRecheck, delegate {
                LoadValues(); RefreshWatch(); RefreshFixes(); Log("Re-checked what is worth changing."); });
            _fixButtons.Controls.Add(_fixRecheck);
            _fixCard.Controls.Add(_fixButtons);

            _root.Controls.Add(_fixCard);

            // ---------------------------------------------------------- profiles
            _root.Controls.Add(Heading("Profiles", "each one writes the battery side only",
                "Four tested combinations of the settings below, plus a way back. The same four " +
                "Basic offers as its named modes - this is what each one actually sets.\n\n" +
                "Balanced is the sensible default on any machine. Battery saver trades responsiveness " +
                "for the last watt or so; Max battery goes further still and is the only one that " +
                "turns boost off outright. Performance lets the CPU off the leash while still on " +
                "battery.\n\n" +
                "Apply one, then watch the power draw chart for a minute - what each is worth " +
                "depends entirely on the hardware.\n\n" +
                "Every profile writes the on-battery side only. What happens when you are plugged " +
                "in is never touched, which is what made all of this safe to experiment with."));

            _profileRow = new Panel { Width = W, Height = 40, BackColor = Theme.Ink, Margin = new Padding(0, 0, 0, 8) };
            _presetBtns.AddRange(BuildProfileRow(_profileRow, Presets.All, 36, (p, b) => WriteProfile(p, b)));
            _root.Controls.Add(_profileRow);

            Panel restoreRow = new Panel { Width = W, Height = 38, BackColor = Theme.Ink, Margin = new Padding(0, 0, 0, 2) };
            _btnRestore = new PillButton {
                Text = "Restore original settings", Glyph = Theme.GlyphUndo,
                // Not Primary: putting the filled accent on Restore made undo look like
                // the main thing to do here, ahead of the profiles the section is for.
                Location = new Point(0, 0), Size = new Size(256, 38)
            };
            _btnRestore.Click += (s, e) => RestoreBaseline();
            restoreRow.Controls.Add(_btnRestore);
            _root.Controls.Add(restoreRow);

            Label profileNote = new Label {
                Text = "Puts every battery setting back to how it was before PowerDial existed.",
                AutoSize = false, Width = W, Height = 20, ForeColor = Theme.Dim, Font = Theme.Small,
                Margin = new Padding(2, 2, 0, 10), BackColor = Theme.Ink
            };
            _root.Controls.Add(profileNote);


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
            _root.Controls.Add(Accent(Heading("Analytics", "recorded here, kept on disk",
                "Everything in this section is measured on this PC and written to " +
                "%LOCALAPPDATA%\\PowerDial\\history, one line a minute, so it survives restarts " +
                "instead of starting from nothing every launch.\n\n" +
                "Power draw is the timed battery counter. Charge over time spans previous runs, " +
                "amber where you were on battery.\n\n" +
                "Running now is a live sample of every process, grouped by name. Since recording " +
                "began is the cumulative tally - the process that has actually burned the most CPU " +
                "across every session, which is the one costing you runtime.\n\n" +
                "Background means no instance of it owns a visible window. Those are the ones worth " +
                "questioning, because you are not the one using them.")));

            // Built here rather than appended later: the column is a flow panel, so child
            // order is layout order, and these two are the answer to "did it work" and
            // "which mode should I use" - the questions Insights exists for.
            BuildInsightsHeadline();

            Card ac = new Card { Width = W, Height = 352, Margin = new Padding(0, 0, 0, 10) };
            ac.Controls.Add(new Label {
                Text = "Power draw", Location = new Point(14, 10), AutoSize = true,
                Font = Theme.Section, ForeColor = Theme.Save, BackColor = Theme.Panel });

            // How far back to look. Both charts move together - they are stacked and read
            // against each other, so showing six hours of watts beside a day of charge
            // would invite exactly the wrong comparison.
            int[] mins = { 60, 360, 1440, 10080, 0 };
            string[] rlabels = { "1h", "6h", "24h", "7d", "All" };
            int rx = W - 30;
            for (int i = rlabels.Length - 1; i >= 0; i--)
            {
                int localMin = mins[i];
                PillButton rb = new PillButton {
                    Text = rlabels[i], Size = new Size(46, 24), BackColor = Theme.Panel,
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    Selected = mins[i] == _rangeMin
                };
                rx -= rb.Width + 6;
                rb.Location = new Point(rx, 8);
                rb.Click += (s, e) => SetRange(localMin);
                _rangeBtns.Add(rb);
                ac.Controls.Add(rb);
            }

            _chartWatts = new LineChart {
                Location = new Point(14, 42), Size = new Size(W - 30, 118), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Empty = "no samples yet - readings begin after 60 seconds on battery" };
            ac.Controls.Add(_chartWatts);
            ac.Controls.Add(new Label {
                Text = "Charge over time", Location = new Point(14, 168), AutoSize = true,
                Font = Theme.Section, ForeColor = Theme.Save, BackColor = Theme.Panel });
            _batChart = new BatteryChart {
                Location = new Point(14, 190), Size = new Size(W - 30, 104), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            ac.Controls.Add(_batChart);
            _histLabel = new Label {
                Location = new Point(14, 302), Size = new Size(W - 30, 34), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel };
            ac.Controls.Add(_histLabel);
            _root.Controls.Add(ac);

            Card pc = new Card { Width = W, Height = 292, Margin = new Padding(0, 0, 0, 10) };
            _procLabel = new Label {
                Text = "Apps using the most power", Location = new Point(14, 10), Size = new Size(300, 18),
                Font = Theme.Section, ForeColor = Theme.Save, BackColor = Theme.Panel };
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
                Font = Theme.Section, ForeColor = Theme.Save, BackColor = Theme.Panel });
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

            // Battery health is not an insight about your usage - it is a fact about the
            // hardware, it changes over months rather than minutes, and nothing in this app
            // can act on it. It gets its own page rather than a footnote under the charts.
            Card hc = new Card { Width = W, Height = 188, Margin = new Padding(0, 0, 0, 10) };
            _cardHealth = hc;
            hc.Controls.Add(new Label {
                Text = "How much charge it still holds", Location = new Point(14, 12), AutoSize = true,
                Font = Theme.Section, ForeColor = Theme.Save, BackColor = Theme.Panel });

            // The figure first, in the size the figure deserves. It was a clause in the
            // middle of a 12px grey sentence, which is not how you state the one number on
            // the page.
            _healthPct = new Label {
                Location = new Point(14, 42), Size = new Size(W - 30, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Stat, ForeColor = Theme.Text, BackColor = Theme.Panel };
            hc.Controls.Add(_healthPct);

            _barHealth = new StackBar {
                Location = new Point(14, 88), Size = new Size(W - 30, 44), Unit = "Wh",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Empty = "Windows does not report this battery's capacity on this PC." };
            hc.Controls.Add(_barHealth);
            _healthNote = new Label {
                Location = new Point(14, 136), Size = new Size(W - 30, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel };
            hc.Controls.Add(_healthNote);
            _root.Controls.Add(hc);

            // Wear is permanent, so the page says so once rather than implying a setting
            // somewhere could undo it.
            Card hw = new Card { Width = W, Height = 162, Margin = new Padding(0, 0, 0, 10) };
            _cardHealthAdvice = hw;
            hw.Controls.Add(new Label {
                Text = "What you can do about it", Location = new Point(14, 12), AutoSize = true,
                Font = Theme.Section, ForeColor = Theme.Save, BackColor = Theme.Panel });
            hw.Controls.Add(new Label {
                Text = "Nothing in this app, or in Windows, can give back capacity a battery has " +
                       "already lost - so nothing here pretends to. What slows further wear is " +
                       "ordinary care: keep it off the charger at 100% for days at a time, and keep " +
                       "it out of the heat. Everything else on these pages is about drawing less " +
                       "from the capacity you still have.",
                Location = new Point(14, 42), Size = new Size(W - 30, 108),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Body, ForeColor = Theme.Dim, BackColor = Theme.Panel });
            _root.Controls.Add(hw);

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
            PillButton recheck = new PillButton { Text = "Check again", Glyph = Theme.GlyphRefresh, Location = new Point(0, 3), Size = new Size(140, 36) };
            recheck.Click += (s, e) => RunBusy(recheck, delegate {
                RefreshWatch(); Log("Re-checked what could be waking the GPU."); });
            _watchButtons.Controls.Add(recheck);
            PillButton opensettings = new PillButton { Text = "Open Windows settings", Location = new Point(134, 3), Size = new Size(184, 32) };
            opensettings.Click += (s, e) => GpuWatch.OpenBackgroundAppsSettings();
            _watchButtons.Controls.Add(opensettings);
            PillButton reread = new PillButton { Text = "Re-read everything", Location = new Point(326, 3), Size = new Size(160, 32) };
            reread.Click += (s, e) => RunBusy(reread, delegate {
                LoadValues(); RefreshWatch(); Log("Re-read every setting from the registry."); });
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

            // ---------------------------------------------------------- run as admin
            // It used to sit under Restore on Power modes, which is nowhere near anything
            // that mentions it. When a write needs rights the app does not have, PowerCfg
            // returns "needs administrator - use Run as admin", and that sentence is read
            // in the log immediately above this button. The instruction and the thing it
            // instructs you to press are now on the same page.
            //
            // Tagged by TagSections as part of the Activity section, so it follows the log
            // onto Diagnostics; it does not follow the log's collapse, because a button you
            // are being told to press should not be hidden behind a chevron.
            _elevated = PowerCfg.IsElevated();
            _adminBox = new Panel {
                Width = W, Height = 78, BackColor = Theme.Ink,
                Margin = new Padding(0, 12, 0, 0), Visible = false
            };
            _adminBox.Controls.Add(new Label {
                Text = "A few Windows power settings can only be written by an administrator.",
                Location = new Point(2, 0), Size = new Size(W - 24, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Ink
            });

            // No glyph: the icon font is not on every install, and a button whose label is
            // preceded by an empty square is a button people do not press.
            _adminBtn = new PillButton {
                Text = "Run as admin", Location = new Point(0, 28), Width = 168, Height = 36,
                BackColor = Theme.Ink
            };
            _adminBtn.Click += (s, e) => RelaunchElevated();
            _adminBox.Controls.Add(_adminBtn);

            // Restarting an app is not a small thing to ask, and "admin" is the vaguest
            // word in the interface. The one line above says what it needs; the dot says
            // why, when, and what it costs - the same bargain every other section makes.
            _adminBox.Controls.Add(new InfoDot {
                Location = new Point(176, 36), BackColor = Theme.Ink,
                Heading = "Run as admin",
                Body = "Most of what this app changes is written to your own user settings, which " +
                       "needs no special rights. A few of them belong to the power scheme itself, " +
                       "and Windows refuses those unless the app is running as an administrator.\n\n" +
                       "When that happens the Activity log above says \"needs administrator - use " +
                       "Run as admin\" rather than claiming the change worked. This button is the " +
                       "answer to that message, and if nothing in the log has asked for it, you do " +
                       "not need it.\n\n" +
                       "It starts a second copy of PowerDial with those rights - which is what makes " +
                       "Windows show you the confirmation prompt - and closes this one. Settings, " +
                       "history and the measured record are all on disk, so the restart loses " +
                       "nothing but the current measuring window.\n\n" +
                       "One consequence worth knowing: once it is running elevated, a normal " +
                       "command prompt can no longer close it. Use its own window or the tray icon."
            });
            _root.Controls.Add(_adminBox);

            // Breathing room under the last card. It used to be tagged with whichever
            // section happened to be last in the column, so only that one page had a floor
            // and every other ended flush against the window edge.
            _bottomPad = new Panel {
                Width = W, Height = 36, BackColor = Theme.Ink, Margin = new Padding(0)
            };
            _root.Controls.Add(_bottomPad);

            // built last: every section has to exist before the sidebar can show one
            BuildOverview();
            BuildPowerModes();
            TagSections();

            // TagSections gives everything between the Analytics heading and the next one
            // to Insights, which is right for all of it except the health card - that is a
            // fact about the hardware rather than a record of your usage, and it has its
            // own page. Retagging it here keeps the build order (and so the layout order)
            // of the column alone.
            if (_cardHealth != null) _cardHealth.Tag = "Battery health";
            if (_cardHealthAdvice != null) _cardHealthAdvice.Tag = "Battery health";
            foreach (ModeCard mc in _modeCards) mc.Tag = "Profiles";

            // The cards go where the button row was, above Restore - a flow panel lays its
            // children out in child order, and they were appended at the end of the column.
            Control profHead;
            if (_sections.TryGetValue("Profiles", out profHead) && profHead != null)
            {
                int at = _root.Controls.GetChildIndex(profHead) + 1;
                for (int i = 0; i < _modeCards.Count; i++)
                    _root.Controls.SetChildIndex(_modeCards[i], at + i);
            }

            // Four identical buttons said nothing about what any of them would do. The
            // cards replace them; the buttons stay built because RefreshModeCode lights
            // whichever one the machine matches, and nothing else needs changing for that.
            if (_profileRow != null) { _profileRow.Visible = false; _profileRow.Height = 0; }

            // Each page now carries its own title, so the heading that used to introduce
            // the section inside the column says the same thing twice. They stay in the
            // tree because TagSections uses them as the section boundaries.
            // Only the ones that now repeat the page title, or that said nothing to begin
            // with. Advanced holds two distinct groups - what is worth changing, and the
            // settings themselves - and without their headings it was one long stream of
            // cards with nothing separating them.
            string[] replaced = { "Profiles", "Analytics", "More" };
            foreach (string name in replaced)
            {
                Control head;
                if (_sections.TryGetValue(name, out head) && head != null)
                { head.Visible = false; head.Height = 0; }
            }
            BuildWattsStrip();
            BuildRail();

            // Overview every time, not the section you happened to close on. Overview is
            // the page that answers "is anything wrong and what should I do" in one screen;
            // reopening on Diagnostics because that is where you were last Tuesday starts
            // you three clicks from the answer. The section is still recorded as you move,
            // so the setting is there if this is ever worth making a preference.
            ShowPage("overview");
        }

        /// <summary>
        /// Label every control in the column with the section it belongs to.
        ///
        /// The column is one flat flow of cards - nothing groups a card with the heading
        /// above it. Rather than change every construction site to say which section it is
        /// in, this walks the column once after it is built: a heading starts a section, and
        /// everything after it belongs to that section until the next one. Basic mode then
        /// shows or hides whole sections by tag.
        /// </summary>
        void TagSections()
        {
            string current = null;
            foreach (Control c in _root.Controls)
            {
                foreach (KeyValuePair<string, Control> kv in _sections)
                    if (kv.Value == c) { current = kv.Key; break; }
                c.Tag = current;
            }
        }

        /// <summary>
        /// Which sidebar section a heading belongs to.
        ///
        /// The column is still one flow of cards tagged by TagSections; this is the only
        /// thing that decides which of them a section shows. Adding a section means adding
        /// its heading here - a heading with no entry falls to Overview rather than
        /// vanishing, because a card nobody can reach is worse than one in the wrong place.
        /// </summary>
        static string PageOf(string section)
        {
            switch (section)
            {
                case "Profiles":                return "modes";
                case "Analytics":               return "insights";
                case "Battery health":          return "health";
                case "What changes your watts": return "advanced";
                case "More":                    return "diagnostics";

                // WithInfo registers a section for every collapsible toggle as well as for
                // the headings, so these three are section names too. Without them they hit
                // the default and everything after each one landed on Overview - which is
                // why Diagnostics came up empty with only its heading on it.
                case "Basic settings":          return "advanced";
                case "GPU watch":               return "diagnostics";
                case "Activity":                return "diagnostics";

                // The suggestion list is the detailed version of what the Overview card
                // recommends. One recommendation up front, the full list with the rest of
                // the settings it is talking about.
                case "Make it last longer":     return "advanced";

                default:                        return "overview";
            }
        }

        /// <summary>
        /// Show one section of the app.
        ///
        /// This replaced ApplyMode, which chose between a Basic card and the whole
        /// instrument. The mechanism is the same one, and deliberately so: sections are
        /// hidden rather than unbuilt, so switching is instant and nothing is reconstructed.
        /// What changed is what decides - the sidebar, rather than a mode that hid features.
        /// </summary>
        void ShowPage(string key)
        {
            if (string.IsNullOrEmpty(key)) key = "overview";
            _page = key;
            if (_nav != null) _nav.Select(key);

            SetTitle(key);

            foreach (Control c in _root.Controls)
            {
                if (c == null) continue;

                // The title names whichever page is open, so it is the one thing in the
                // column that is never hidden.
                if (c == _pageTitle || c == _bottomPad) { c.Visible = true; continue; }

                // The Overview cards are inserted above the first heading, so TagSections
                // leaves them untagged - and untagged falls to Overview, which is where
                // they belong.
                bool on = PageOf(c.Tag as string) == key;

                // Already running as admin? Then the offer is not merely redundant, it is
                // wrong - pressing it would restart the app to grant rights it already has.
                // It was hidden once at build time before, which ShowPage then undid on the
                // next page change by setting Visible from the tag alone.
                if (c == _adminBox) on = on && !_elevated;

                // The collapsible bodies follow their own toggle. Opening a section must
                // not fling them open: doing that left the chevron reading "collapsed" over
                // an expanded section, and dumped the settings, the GPU watch and the log
                // on someone who had never asked for them.
                else if (c == _basicBox) on = on && _basicToggle.Expanded;
                else if (c == _watchBox) on = on && _watchToggle.Expanded;
                else if (c == _logBox) on = on && _logToggle.Expanded;

                c.Visible = on;
            }

            // The header instrument and the watts strip each describe a measurement, and
            // each belongs to the section that is about it - pinned over every page they
            // cost ~300px of permanent chrome on sections with nothing to do with either.
            // The header instrument is gone: StatusCard says all of this on Overview, and
            // the two together stated the charge, the draw and the time left twice on the
            // same screen. It stays built because the measuring countdown and the sparkline
            // still live on it; it is simply never shown.
            // Both are retired rather than removed. The header instrument is said better
            // by StatusCard on Overview; the watts strip ranked processes over the measured
            // window, which the Overview card now summarises and the Insights table states
            // in full - pinned over Insights it was the third telling of one measurement.
            // They stay built because the measuring countdown, the sparkline and the window
            // ranking still feed other cards.
            _readout.Visible = false;
            _wattsStrip.Visible = false;
            _chrome.Height = 0;      // nothing is pinned above the column any more

            _root.Location = new Point(Rail, _chrome.Height);
            _root.Size = new Size(Math.Max(80, ClientSize.Width - Rail),
                                  Math.Max(80, ClientSize.Height - _chrome.Height));

            if (key == "overview") RefreshOverview();
            else if (key == "modes") RefreshModes();
            else if (key == "insights") RefreshBasic();
            else if (key == "health") RefreshAnalytics();
            Relayout();
        }

        /// <summary>The page title, and the sentence under it.</summary>
        void SetTitle(string key)
        {
            if (_pageTitle == null) return;
            switch (key)
            {
                case "modes":
                    _pageTitle.Title = "Power modes";
                    _pageTitle.Subtitle = "Each one changes how hard the processor works and how " +
                                          "quickly the screen sleeps. These apply on battery only.";
                    break;
                case "insights":
                    _pageTitle.Title = "Insights";
                    _pageTitle.Subtitle = "What your changes were worth, and what this laptop has " +
                                          "actually drawn. Recorded once a minute, kept on disk.";
                    break;
                case "health":
                    _pageTitle.Title = "Battery health";
                    _pageTitle.Subtitle = "How much charge this battery still holds compared with " +
                                          "when it was new. Read from the hardware.";
                    break;
                case "advanced":
                    _pageTitle.Title = "Advanced";
                    _pageTitle.Subtitle = "Individual Windows power settings, and everything this app " +
                                          "found worth changing.";
                    break;
                case "diagnostics":
                    _pageTitle.Title = "Diagnostics";
                    _pageTitle.Subtitle = "Detail for troubleshooting. Nothing here needs your " +
                                          "attention day to day.";
                    break;
                default:
                    _pageTitle.Title = "Overview";
                    _pageTitle.Subtitle = "";
                    break;
            }
            _pageTitle.AccessibleName = _pageTitle.Title;
            _pageTitle.Height = _pageTitle.Subtitle.Length > 0 ? 82 : 58;
            _pageTitle.Invalidate();
        }

        void SetPage(string key)
        {
            if (key == _page) return;
            Config.SetSection(key);
            ShowPage(key);
        }

        /// <summary>
        /// The bar under the header: one button per section, which scrolls the column to
        /// it. The whole app is one list about three screens long, so finding the charts
        /// or the GPU watch used to mean scrolling past everything else to get there.
        /// Nothing is hidden - this only moves you, so every section stays on one page.
        /// </summary>
        /// <summary>
        /// "Where your watts go", pinned between the header instrument and the section bar.
        ///
        /// It used to sit a screen and a half down the column, which put it out of sight
        /// exactly when it was useful - the whole point is to read it against the draw
        /// figure directly above it, and the two describe the same window. Pinned it costs
        /// ~124px of permanent chrome, so it is deliberately denser than it was as a card:
        /// the measured total moved up onto the title row, the explanation is one line
        /// rather than two, and it lists four processes rather than five.
        /// </summary>
        /// <summary>
        /// Basic mode, entire. One button that does the sensible thing, a plain-language
        /// account of what the machine is doing, and - once it has been measured - what the
        /// last optimise was actually worth.
        ///
        /// Nothing here is a prediction. The saving is the average draw recorded before the
        /// change against the average recorded since, both measured on this PC; until enough
        /// minutes have accumulated afterwards it says so rather than showing a number.
        /// </summary>
        /// <summary>
        /// The sidebar, and the battery summary under it.
        ///
        /// It replaces two strips that looked identical and did different things: a
        /// Basic/Advanced switch that decided what existed, and a section bar three hundred
        /// pixels below it that only scrolled. One list now, and nothing is hidden behind a
        /// mode - every section is one click away from every other.
        /// </summary>
        void BuildRail()
        {
            _nav = new NavRail {
                Location = new Point(0, 0),
                Size = new Size(Rail, Math.Max(240, ClientSize.Height)),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left
            };
            _nav.Add("overview",    "Overview");
            _nav.Add("modes",       "Power modes");
            _nav.Add("insights",    "Insights");
            _nav.Add("advanced",    "Advanced");
            _nav.Add("health",      "Battery health");
            _nav.Add("diagnostics", "Diagnostics");
            _nav.SelectionChanged += (s, e) => SetPage(_nav.Selected);

            _status = new StatusBlock {
                Width = Rail,
                Location = new Point(0, Math.Max(240, ClientSize.Height) - 96)
            };
            _nav.Controls.Add(_status);

            Controls.Add(_nav);
            _nav.BringToFront();
        }

        /// <summary>
        /// The two cards that open Insights: what the last change was worth, and what each
        /// mode has cost.
        ///
        /// Both used to live in one 440px card that also held four mode buttons and a
        /// machine summary, with several of its children overlapping each other and three
        /// of them never added to it at all - including the only Undo the interface had.
        /// They are two cards now, each about one question.
        /// </summary>
        void BuildInsightsHeadline()
        {
            _cardSaving = new SavingCard { Width = W, Margin = new Padding(0, 0, 0, 10) };
            _cardSaving.Undo.Click += (s, e) => RestoreBaseline();
            _root.Controls.Add(_cardSaving);

            _cardModeCost = new ModeCostCard { Width = W, Margin = new Padding(0, 0, 0, 10) };
            _root.Controls.Add(_cardModeCost);
        }

        /// <summary>
        /// Overview, as designed: the battery stated once, the one thing worth doing, the
        /// mode in effect and what is keeping the processor busy.
        ///
        /// These four go in above every heading, so TagSections leaves them untagged and
        /// they fall to Overview. Each is a painted card that names itself to a screen
        /// reader through its own Describe(), because a painted card is otherwise silent.
        /// </summary>
        void BuildOverview()
        {
            _pageTitle = new PageTitle { Width = W, Margin = new Padding(0, 0, 0, 4) };
            _cardStatus = new StatusCard { Width = W, Margin = new Padding(0, 0, 0, 12) };
            _cardAdvice = new AdviceCard { Width = W, Margin = new Padding(0, 0, 0, 12) };

            _cardMode = new ModeSummaryCard { Location = new Point(0, 0), Width = W / 2 };
            _cardApps = new TopAppsCard { Location = new Point(0, 0), Width = W / 2 };
            _overviewPair = new Panel {
                Width = W, Height = 168, BackColor = Theme.Ink, Margin = new Padding(0, 0, 0, 12)
            };
            _overviewPair.Controls.Add(_cardMode);
            _overviewPair.Controls.Add(_cardApps);

            _cardAdvice.Act.Click += (s, e) => OverviewAct();
            _cardAdvice.Recheck.Click += (s, e) => RunBusy(_cardAdvice.Recheck, delegate {
                LoadValues(); RefreshWatch(); RefreshFixes(); });
            _cardMode.Change.Click += (s, e) => SetPage("modes");
            // Diagnostics has the GPU watch and the log; neither is a list of apps. The
            // full ranking is "Running now" on Insights, so that is where See all goes -
            // and it arrives sorted the way the card was, by processor time.
            _cardApps.SeeAll.Click += (s, e) => {
                // The card ranks every app by processor time, so the table has to arrive
                // ranked the same way and unfiltered - "Running now" defaults to background
                // processes only, which would have dropped the very apps you just clicked
                // through from.
                _sortCpu = true;
                _bgOnly = false;
                RefreshProcesses();
                SetPage("insights");
            };

            _root.Controls.Add(_pageTitle);
            _root.Controls.Add(_cardStatus);
            _root.Controls.Add(_cardAdvice);
            _root.Controls.Add(_overviewPair);
            _root.Controls.SetChildIndex(_pageTitle, 0);
            _root.Controls.SetChildIndex(_cardStatus, 1);
            _root.Controls.SetChildIndex(_cardAdvice, 2);
            _root.Controls.SetChildIndex(_overviewPair, 3);
        }

        /// <summary>
        /// Power modes: one card per profile, in place of a row of four identical buttons.
        /// </summary>
        void BuildPowerModes()
        {
            foreach (Preset p in Presets.All)
            {
                Preset local = p;
                ModeCard card = new ModeCard {
                    Width = W,
                    Code = p.Code,
                    ModeName = p.Friendly ?? p.Name,
                    Blurb = p.Blurb,

                    // The only mode that deliberately raises draw. Saying so on the card is
                    // the difference between choosing it and discovering it.
                    WarnText = p.Code == 4 ? "SHORTENS RUNTIME" : "",
                    Margin = new Padding(0, 0, 0, 10)
                };
                card.Chips.AddRange(ChipsFor(p));
                card.Use.Click += (s, e) => WriteProfile(local, card.Use);
                _modeCards.Add(card);
                _root.Controls.Add(card);
            }
        }

        /// <summary>
        /// What a profile actually changes, in words rather than registry values.
        ///
        /// The settings themselves are on the Advanced page for anyone who wants them; what
        /// belongs on a mode card is the consequence - whether the processor may boost, how
        /// soon the screen goes dark, which graphics chip it will use.
        /// </summary>
        static List<string> ChipsFor(Preset p)
        {
            List<string> chips = new List<string>();
            int v;

            if (p.Values.TryGetValue("boost", out v))
            {
                if (v == 0) chips.Add("Processor boost off");
                else if (v == 1) chips.Add("Boost limited");
                else if (v == 2) chips.Add("Boost unrestricted");
                else chips.Add("Boost when it is needed");
            }
            if (p.Values.TryGetValue("videoidle", out v)) chips.Add("Screen off after " + ChipTime(v));
            if (p.Values.TryGetValue("sleepidle", out v)) chips.Add("Sleeps after " + ChipTime(v));
            if (p.Values.TryGetValue("switchable", out v))
                chips.Add(v == 0 ? "Always the efficient graphics chip" : "May use the fast graphics chip");
            return chips;
        }

        static string ChipTime(int seconds)
        {
            if (seconds <= 0) return "never";
            if (seconds < 3600) return (seconds / 60) + " min";
            int h = seconds / 3600;
            return h + (h == 1 ? " hour" : " hours");
        }

        /// <summary>
        /// Fill the mode cards: which one the machine matches, and what each has measured.
        ///
        /// Under five recorded minutes is not a measurement, so the card says it has not
        /// been measured rather than printing an average of three readings.
        /// </summary>
        void RefreshModes()
        {
            if (_modeCards.Count == 0) return;

            List<HistPoint> all = History.All();
            foreach (ModeCard card in _modeCards)
            {
                List<HistPoint> mine = new List<HistPoint>();
                foreach (HistPoint h in all) if (h.M == card.Code) mine.Add(h);

                RuntimeAverage r = RuntimeAverage.From(mine);
                card.Minutes = r.Minutes;
                card.Watts = (r.Known && r.Minutes >= 5) ? (double?)r.Watts : null;
                card.Current = card.Code == _modeCode;
                card.Describe();
                card.Invalidate();
            }
            ReflowModeCards();
        }

        /// <summary>Measured off-screen, because this runs during BuildUi before the form
        /// has a window handle and CreateGraphics needs one.</summary>
        void ReflowModeCards()
        {
            if (_modeCards.Count == 0) return;
            using (Bitmap bmp = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(bmp))
                foreach (ModeCard card in _modeCards) card.Reflow(g);
        }

        /// <summary>Whichever button started an optimise, so the arc spins where the
        /// person pressed rather than on a control that is no longer on screen.</summary>
        PillButton OptimiseButton()
        {
            return _cardAdvice != null && _cardAdvice.Act.Visible ? _cardAdvice.Act : null;
        }

        /// <summary>What the Overview button acts on: a whole optimise, or the one thing
        /// Windows has to do itself.</summary>
        void OverviewAct()
        {
            if (_adviceFix != null) { ApplyFix(_adviceFix, _cardAdvice.Act); return; }
            OptimiseBasic();
        }

        /// <summary>
        /// Fill the Overview cards from the same measurements everything else uses.
        ///
        /// Nothing here computes a figure of its own: the charge and the draw come from the
        /// battery poll, the time left from the recorded average, the mode from the machine
        /// read-back, and the processes from the same window the contributors list uses. One
        /// set of numbers, so two cards cannot disagree about the time left the way the old
        /// header and Basic card did.
        /// </summary>
        void RefreshOverview()
        {
            if (_cardStatus == null) return;

            _cardStatus.HasBattery = Machine.HasBattery;
            _cardStatus.OnAc = _bat.OnAc;
            _cardStatus.Charging = _bat.Charging;
            _cardStatus.ChargePct = _bat.PercentOfFull;
            _cardStatus.Mwh = _bat.RemainingMwh;
            _cardStatus.FullMwh = _bat.FullChargeMwh;
            _cardStatus.Watts = _bat.Watts;
            _cardStatus.MeasuringLeft = _readout == null ? 0 : _readout.MeasuringLeft;
            _cardStatus.HoursLeft = _bat.OnAc ? null : _avg.Left(_bat.RemainingMwh);
            _cardStatus.Health = _bat.HealthPercent;
            _cardStatus.Describe();
            _cardStatus.Invalidate();

            int fixable = 0;
            Suggestion advisory = null;
            foreach (Suggestion sg in _fixes)
            {
                if (sg.Kind == FixKind.Advisory) { if (advisory == null) advisory = sg; }
                else fixable++;
            }

            _adviceFix = null;
            _cardAdvice.FooterText = "Every power setting PowerDial can change is already set for long battery life.";

            if (fixable > 0)
            {
                _cardAdvice.Warn = true;
                _cardAdvice.Title = fixable + (fixable == 1
                    ? " setting is costing you battery life" : " settings are costing you battery life");
                _cardAdvice.Body = "One click changes them all, on the battery side only. Your " +
                                   "plugged-in settings are not touched, and Undo puts everything back.";
                _cardAdvice.Note = "";
                _cardAdvice.AllSet = false;
                _cardAdvice.Act.Text = "Optimise my battery";
                _cardAdvice.Act.Glyph = Theme.GlyphCheck;
                _cardAdvice.Act.Visible = true;
            }
            else if (advisory != null)
            {
                // Nothing left that this app can write, but something is still costing
                // runtime. Say who has to change it rather than offering a button that
                // quietly opens a Windows page and looks like it did the work.
                _adviceFix = advisory;
                _cardAdvice.Warn = true;
                _cardAdvice.Title = advisory.Title;
                _cardAdvice.Body = advisory.Detail;
                _cardAdvice.Note = "PowerDial cannot change this for you - Windows decides which " +
                                   "apps may run in the background.";
                _cardAdvice.AllSet = true;
                _cardAdvice.Act.Text = advisory.ActionLabel;
                _cardAdvice.Act.Glyph = null;
                _cardAdvice.Act.Visible = true;
            }
            else
            {
                _cardAdvice.Warn = false;
                _cardAdvice.Title = "Nothing left to change";
                _cardAdvice.Body = "Every power setting this app can write is already set for long " +
                                   "battery life, and nothing is holding the graphics chip awake.";
                _cardAdvice.Note = "";
                _cardAdvice.AllSet = false;
                _cardAdvice.Act.Visible = false;
            }
            _cardAdvice.Describe();

            // Measured off-screen: this runs during BuildUi, before the form has a window
            // handle, and CreateGraphics needs one.
            using (Bitmap bmp = new Bitmap(1, 1))
            using (Graphics mg = Graphics.FromImage(bmp))
                _cardAdvice.Reflow(mg);
            _cardAdvice.Invalidate();

            Preset pr = Presets.ByCode(_modeCode);
            _cardMode.Known = pr != null;
            _cardMode.Mode = pr == null ? "" : (pr.Friendly ?? pr.Name);

            // The card had an empty lower half; what this mode has actually cost is the
            // most useful thing that can go in it, and it is already measured.
            _cardMode.Watts = null;
            _cardMode.Minutes = 0;
            if (pr != null)
            {
                List<HistPoint> mine = new List<HistPoint>();
                foreach (HistPoint h in History.All()) if (h.M == pr.Code) mine.Add(h);
                RuntimeAverage ra = RuntimeAverage.From(mine);
                _cardMode.Minutes = ra.Minutes;
                if (ra.Known && ra.Minutes >= 5) _cardMode.Watts = ra.Watts;
            }
            _cardMode.Blurb = pr == null
                ? "These settings do not match any mode. Something changed them in Windows or another app."
                : pr.Blurb;
            _cardMode.Describe();
            _cardMode.Invalidate();

            _cardApps.Rows.Clear();
            List<KeyValuePair<string, double>> busiest = WindowTop(4);
            foreach (KeyValuePair<string, double> kv in busiest)
            {
                TopAppsCard.Row r = new TopAppsCard.Row();
                r.Name = kv.Key;
                r.Seconds = kv.Value;
                r.Background = IsBackground(kv.Key);
                _cardApps.Rows.Add(r);
            }
            _cardApps.WindowSeconds = _bat.WindowSeconds;
            _cardApps.Empty = _bat.OnAc
                ? "Nothing timed yet - processor time is only recorded on battery."
                : "Collecting: the first window takes " + _bat.WindowSeconds + " seconds.";
            _cardApps.Describe();
            _cardApps.Invalidate();
        }

        /// <summary>
        /// A row of profile buttons, evenly sharing the container's width - one button per
        /// preset, labelled the same way everywhere (`Friendly ?? Name`). Basic and Advanced
        /// both build their profile row through here: one definition of what a profile button
        /// looks like, how wide it is, and what happens when it is clicked. Only the presets
        /// offered and the button height differ between the two callers.
        ///
        /// Selection is never set from the click - <see cref="RefreshModeCode"/> sets it
        /// afterwards from what the machine actually reads back, the same way for every row
        /// this builds, so a row this method built is never the thing deciding it is "on".
        /// </summary>
        static List<PillButton> BuildProfileRow(Panel container, List<Preset> presets, int buttonHeight, Action<Preset, PillButton> onPick)
        {
            List<PillButton> outp = new List<PillButton>();
            int count = Math.Max(1, presets.Count);
            int bw = (container.Width - (count - 1) * 8) / count;
            int px = 0;
            foreach (Preset p in presets)
            {
                Preset local = p;
                PillButton b = new PillButton {
                    Text = p.Friendly ?? p.Name, Location = new Point(px, 0), Size = new Size(bw, buttonHeight),
                    BackColor = container.BackColor, Tag = local
                };
                b.Click += (s, e) => onPick(local, b);
                container.Controls.Add(b);
                outp.Add(b);
                px += bw + 8;
            }
            return outp;
        }

        /// <summary>
        /// Re-space a row BuildProfileRow already built, to share a new width - called from
        /// Relayout, on window resize. Anchoring would overlap the buttons instead of
        /// resizing each one, so this is run by hand.
        /// </summary>
        static void RespaceRow(List<PillButton> row, int width)
        {
            if (row.Count == 0) return;
            int bw = (width - (row.Count - 1) * 8) / row.Count;
            int px = 0;
            foreach (PillButton b in row)
            {
                b.Location = new Point(px, 0);
                b.Width = bw;
                px += bw + 8;
            }
        }

        /// <summary>
        /// Read back which profile the machine now matches, and mark it selected everywhere
        /// a profile can be picked - the Basic mode row and the Advanced profile row are two
        /// separate rows of buttons (one WinForms control can only live in one place), but
        /// both were built by <see cref="BuildProfileRow"/> and both get their "you are here"
        /// from this one read, matched by which preset a button's Tag actually is rather than
        /// by comparing text. Called after anything that writes a setting, never on the poll
        /// - it is a registry read per knob per profile.
        /// </summary>
        void RefreshModeCode()
        {
            Preset m = Presets.Match();
            _modeCode = m == null ? 0 : m.Code;
            foreach (PillButton b in _presetBtns)
            {
                bool on = m != null && (Preset)b.Tag == m;
                if (b.Selected != on) { b.Selected = on; b.Invalidate(); }
            }

        }

        /// <summary>One mode's measured record: how long it ran, and what it drew.</summary>
        sealed class ModeStat
        {
            public string Name;
            public int Minutes;
            public double Watts;
            public double? Hours;
        }

        /// <summary>
        /// What each mode actually cost, measured. Points are grouped by the profile that
        /// was in effect when they were recorded, and only points taken on battery while
        /// actually drawing count - the same filter the header average uses.
        ///
        /// This is the honest version of "which mode is better": it compares what this
        /// laptop really drew in each, over stated amounts of time, rather than predicting
        /// anything. A mode with too little recorded says so.
        /// </summary>
        List<ModeStat> ModeStats(List<HistPoint> all)
        {
            List<ModeStat> outp = new List<ModeStat>();

            foreach (Preset p in Presets.Basic)
            {
                List<HistPoint> mine = new List<HistPoint>();
                foreach (HistPoint h in all) if (h.M == p.Code) mine.Add(h);

                RuntimeAverage r = RuntimeAverage.From(mine);
                ModeStat st = new ModeStat();
                st.Name = p.Friendly ?? p.Name;
                st.Minutes = r.Minutes;
                st.Watts = r.Watts;
                st.Hours = r.Known ? r.FromFull(_bat.FullChargeMwh) : null;
                outp.Add(st);
            }
            return outp;
        }

        /// <summary>
        /// Feed the two Insights headline cards.
        ///
        /// One pass over the history serves both: what each mode cost, and what the last
        /// change was worth. Neither card reads a file or parses anything in its paint
        /// handler - a paint runs on every expose, resize and invalidate.
        /// </summary>
        void RefreshBasic()
        {
            if (_cardSaving == null || _cardModeCost == null) return;

            // A desktop is a supported target. It still has power settings worth changing,
            // but "what your last change was worth" is measured from the battery counter,
            // and without a battery there is nothing to measure it with.
            if (!Machine.HasBattery)
            {
                _cardSaving.Idle = true;
                _cardSaving.Waiting = false;
                _cardSaving.Message = "This PC has no battery, so there is nothing here to measure. " +
                                      "The power settings and the process list still work.";
                _cardSaving.Fit();
                _cardSaving.Invalidate();

                _cardModeCost.Rows.Clear();
                _cardModeCost.Empty = Machine.Summary();
                _cardModeCost.Fit();
                _cardModeCost.Invalidate();
                _root.PerformLayout();
                return;
            }

            List<HistPoint> hist = History.All();

            // ------------------------------------------------ what each mode has cost
            _cardModeCost.Rows.Clear();
            foreach (ModeStat st in ModeStats(hist))
            {
                ModeCostCard.Row r = new ModeCostCard.Row();
                r.Name = st.Name;
                r.Minutes = st.Minutes;
                r.Watts = st.Watts;
                r.Hours = st.Hours;
                r.Current = NameOfMode(_modeCode) == st.Name;
                _cardModeCost.Rows.Add(r);
            }
            _cardModeCost.Empty = "Nothing measured yet. Pick a mode and use the laptop on battery " +
                                  "for a few minutes - this fills in as it goes.";
            _cardModeCost.Fit();
            _cardModeCost.Invalidate();

            // ------------------------------------------------ what the last change was worth
            FillSaving(hist);
            _cardSaving.Fit();
            _cardSaving.Invalidate();

            _root.PerformLayout();
        }

        /// <summary>The friendly name of a profile code, or null for a custom mix.</summary>
        static string NameOfMode(int code)
        {
            foreach (Preset p in Presets.Basic) if (p.Code == code) return p.Friendly ?? p.Name;
            return null;
        }

        /// <summary>
        /// The before/after comparison, as figures rather than a sentence.
        ///
        /// Same arithmetic as before - an average measured before the change against an
        /// average measured since - but the card wants the numbers, not prose, so the three
        /// outcomes (nothing to compare, not enough measured yet, a result) are states on
        /// the card rather than three different paragraphs.
        /// </summary>
        void FillSaving(List<HistPoint> hist)
        {
            Config c = Config.Current;
            _cardSaving.Detail = "";

            if (!c.BeforeWatts.HasValue || c.OptimisedAtUnix <= 0)
            {
                _cardSaving.Idle = true;
                _cardSaving.Waiting = false;
                _cardSaving.Message = "Nothing to compare yet. Once you optimise from Overview, or pick " +
                                      "a mode, this shows what it actually saved - measured on this " +
                                      "laptop, never estimated.";
                return;
            }

            List<HistPoint> since = new List<HistPoint>();
            foreach (HistPoint p in hist)
                if (p.T >= c.OptimisedAtUnix) since.Add(p);

            RuntimeAverage after = RuntimeAverage.From(since);
            if (!after.Known || after.Minutes < 10)
            {
                _cardSaving.Idle = false;
                _cardSaving.Waiting = true;
                _cardSaving.Message = "Measuring what that change was worth. It needs about " +
                                      Math.Max(1, 10 - after.Minutes) +
                                      " more minutes on battery before it can say - and only minutes " +
                                      "spent on battery count.";
                return;
            }

            _cardSaving.Idle = false;
            _cardSaving.Waiting = false;
            _cardSaving.Before = c.BeforeWatts.Value;
            _cardSaving.After = after.Watts;
            _cardSaving.Minutes = after.Minutes;

            double delta = _cardSaving.Before - _cardSaving.After;
            if (Math.Abs(delta) < 0.15) { _cardSaving.Detail = "no measurable difference yet"; return; }

            RuntimeAverage was = new RuntimeAverage();
            was.Watts = c.BeforeWatts.Value;
            was.Minutes = c.BeforeMinutes;
            double? fullBefore = was.FromFull(_bat.FullChargeMwh);
            double? fullAfter = after.FromFull(_bat.FullChargeMwh);

            if (fullBefore.HasValue && fullAfter.HasValue)
                _cardSaving.Detail = delta > 0
                    ? "about " + FmtHours(fullAfter.Value - fullBefore.Value) + " more from a full charge"
                    : "about " + FmtHours(fullBefore.Value - fullAfter.Value) + " less from a full charge - " +
                      "you have probably been working the machine harder since";
            else
                _cardSaving.Detail = delta > 0 ? "drawing less than before" : "drawing more than before";
        }

        /// <summary>
        /// The one button. Applies everything the advisor can write, after noting what the
        /// machine was drawing so the result can be compared against it.
        /// </summary>
        void OptimiseBasic()
        {
            List<Suggestion> doable = new List<Suggestion>();
            foreach (Suggestion g in _fixes) if (g.Kind != FixKind.Advisory) doable.Add(g);

            if (doable.Count == 0)
            {
                RunBusy(OptimiseButton(), delegate { LoadValues(); RefreshWatch(); RefreshFixes(); });
                Log("Re-checked: nothing left that this app can set.");
                RefreshBasic();
                return;
            }

            if (MessageBox.Show(
                    "Change " + doable.Count + " setting(s) so this laptop lasts longer on battery?\n\n" +
                    "Your plugged-in settings are not touched, and Undo puts everything back.",
                    "Optimise my battery", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            WriteFixes(doable, OptimiseButton());
        }

        void BuildWattsStrip()
        {
            _wattsStrip = new Card {
                Location = new Point(Chrome, _readout.Bottom + 10), Width = W, Height = 164,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _wattsStrip.Controls.Add(new Label {
                Text = "Apps using the most power", Location = new Point(14, 9), AutoSize = true,
                Font = Theme.Section, ForeColor = Theme.Save, BackColor = Theme.Panel });

            // The figure the list is describing. Measured, never derived - see the info text.
            _wattsTotal = new Label {
                Location = new Point(W - 250, 11), Size = new Size(212, 18),
                Anchor = AnchorStyles.Top | AnchorStyles.Right, TextAlign = ContentAlignment.MiddleRight,
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel };
            _wattsStrip.Controls.Add(_wattsTotal);

            _contribNote = new Label {
                Location = new Point(14, 30), Size = new Size(W - 60, 16),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel };
            _wattsStrip.Controls.Add(_contribNote);

            // Four rows at 22px need 88px. It was 68, so the fourth row was sliced in
            // half by the panel edge - the card underneath had room to spare, which is
            // why it read as a rendering glitch rather than as an overflow.
            _contribList = new Panel {
                Location = new Point(14, 50), Size = new Size(W - 30, 100), BackColor = Theme.Panel,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _contribList.Paint += (s, e) => PaintContributors(e.Graphics);
            _wattsStrip.Controls.Add(_contribList);

            InfoDot cdot = new InfoDot {
                Location = new Point(W - 32, 11), Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Heading = "Apps using the most power",
                Body = "The processes that burned the most CPU during the same window the watts figure " +
                       "was timed over, so the two describe the same slice of time rather than the draw " +
                       "from a minute ago and whatever happens to be busy this instant.\n\n" +
                       "These are core-seconds, not watts, and that is deliberate. Splitting the measured " +
                       "total across processes by CPU share would be a fabricated number, and it would " +
                       "point at the wrong culprit: anything holding a discrete GPU awake can cost more than " +
                       "everything on this list put together while reporting almost no CPU at all. Use this " +
                       "to see what was working, then " +
                       "A/B the draw itself to find out what a change is really worth.\n\n" +
                       "bg marks a process with no window of its own - the ones that are easy to miss.\n\n" +
                       "The list empties when a setting changes, because the measurement window restarts."
            };
            _wattsStrip.Controls.Add(cdot);
            _chrome.Controls.Add(_wattsStrip);
        }


        /// <summary>
        /// Show the work on the button that was clicked. It used to be a progress bar that
        /// covered the section strip, which put the feedback nowhere near the thing you had
        /// just pressed. The arc spins on the button itself; where the work is a countable
        /// list of writes the count rides beside it, so a profile still says how far along
        /// it is rather than becoming an indefinite spinner.
        ///
        /// The column is disabled either way - queuing clicks onto a half-written scheme is
        /// how you end up with settings nobody asked for. A disabled parent does not stop
        /// the button painting itself, and the timer keeps ticking through DoEvents.
        /// </summary>
        void BeginBusy(PillButton on, int steps)
        {
            _busyTotal = steps; _busyDone = 0;
            _busyBtn = on;
            if (_busyBtn != null)
            {
                _busyBtn.Busy = true;
                _busyBtn.BusyText = steps > 0 ? "0/" + steps : null;
            }
            _root.Enabled = false;          // no queuing clicks onto a half-written scheme
            Application.DoEvents();
        }

        void BusyStep()
        {
            _busyDone++;
            if (_busyBtn != null && !_busyBtn.IsDisposed && _busyTotal > 0)
                _busyBtn.BusyText = Math.Min(_busyDone, _busyTotal) + "/" + _busyTotal;
            Application.DoEvents();
        }

        /// <summary>Spin <paramref name="b"/> for the length of one blocking call. Every
        /// button that shells out to powercfg or re-probes the GPU goes through here, so
        /// none of them can look ignored while the window is frozen.</summary>
        void RunBusy(PillButton b, MethodInvoker work)
        {
            BeginBusy(b, 0);
            try { work(); }
            finally { EndBusy(); }
        }

        void EndBusy()
        {
            // RefreshFixes rebuilds the suggestion cards, so the button that started this
            // may already have been disposed by the time the work finishes.
            if (_busyBtn != null && !_busyBtn.IsDisposed) { _busyBtn.Busy = false; _busyBtn.Invalidate(); }
            _busyBtn = null;
            _root.Enabled = true;
            _busyTotal = 0; _busyDone = 0;
        }

        /// <summary>
        /// Release what the form owns that the form does not already own.
        ///
        /// WinForms disposes child <see cref="Control"/>s through the Controls collection,
        /// so every card, label and button is already handled. These are not controls -
        /// they are components and kernel objects that nothing else releases: the WMI
        /// searchers inside <see cref="BatteryMonitor"/>, the tray icon, the named event the
        /// relaunch thread waits on, and three timers.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_poll != null) { _poll.Stop(); _poll.Dispose(); }
                if (_commit != null) { _commit.Stop(); _commit.Dispose(); }
                if (_tick != null) { _tick.Stop(); _tick.Dispose(); }

                // Hide before disposing or the icon can outlive the process in the tray
                // until something makes the shell re-enumerate it.
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }

                if (_bat != null) _bat.Dispose();

                // The relaunch thread is a background thread waiting on this. Quit() has
                // already set it and set _quitting, so it has been released; it swallows
                // the exception if it has not yet noticed.
                if (_wake != null) _wake.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Stretch what was built at the design width out to the window. Cards get set
        /// explicitly because a FlowLayoutPanel ignores Anchor on its own children; the
        /// controls inside each card are anchored and follow on their own.
        /// </summary>
        void Relayout()
        {
            if (_root == null) return;

            // Everything to the right of the sidebar is the page, so the column measures
            // against what is left of the window rather than the whole of it.
            int avail = ClientSize.Width - Rail - Chrome - 6 - SystemInformation.VerticalScrollBarWidth;
            int w = Math.Max(W, Math.Min(WMax, avail));

            if (_nav != null)
            {
                _nav.Height = ClientSize.Height;
                if (_status != null)
                    _status.Location = new Point(0, Math.Max(200, _nav.Height - _status.Height));
            }

            _chrome.Location = new Point(Rail, 0);
            _chrome.Width = Math.Max(80, ClientSize.Width - Rail);
            _root.Location = new Point(Rail, _chrome.Height);
            _root.Size = new Size(Math.Max(80, ClientSize.Width - Rail),
                                  Math.Max(80, ClientSize.Height - _chrome.Height));

            // centre the column when the window is wider than the column is allowed to be
            int extra = Math.Max(0, avail - w);
            _root.Padding = new Padding(Chrome + extra / 2, 12, 6, 28);
            _readout.Width = w;
            _wattsStrip.Width = w;
            _readout.Left = Chrome + extra / 2;
            if (_cardSaving != null) _cardSaving.Width = w;
            if (_cardModeCost != null) _cardModeCost.Width = w;
            if (_pageTitle != null) _pageTitle.Width = w;
            foreach (ModeCard mc in _modeCards) mc.Width = w;
            ReflowModeCards();
            if (_cardStatus != null) _cardStatus.Width = w;
            if (_cardAdvice != null) _cardAdvice.Width = w;

            // Mode and apps share one row, so they are sized against each other rather
            // than stretched: the apps list carries four names and four figures and needs
            // the greater share of it.
            if (_overviewPair != null)
            {
                _overviewPair.Width = w;
                int gap = 14;
                int modeW = (int)Math.Round((w - gap) * 0.42);
                _cardMode.Location = new Point(0, 0);
                _cardMode.Width = modeW;
                _cardApps.Location = new Point(modeW + gap, 0);
                _cardApps.Width = Math.Max(160, w - modeW - gap);
                _overviewPair.Height = Math.Max(_cardMode.Height, _cardApps.Height);
            }
            _wattsStrip.Left = _readout.Left;

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

            // The profile buttons share their row, so they have to be re-spaced rather
            // than anchored - anchoring all of them would overlap them. Only the Advanced
            // row is left; Power modes uses cards now, and the Basic row it mirrored is
            // gone with the card that held it.
            RespaceRow(_presetBtns, w);

            if (_fixList != null)
                foreach (Control card in _fixList.Controls) card.Width = _fixList.Width;

            if (_watchList != null)
                foreach (Control line in _watchList.Controls) line.Width = _watchList.Width;

            _root.PerformLayout();
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

        /// <summary>
        /// Recolour a heading's title. Insights is the one page that is entirely measured
        /// rather than set - nothing on it writes anything - and the green that means
        /// "this is what you saved" everywhere else marks it as read-only throughout.
        /// </summary>
        static Panel Accent(Panel head)
        {
            foreach (Control c in head.Controls)
            {
                Label l = c as Label;
                if (l != null) l.ForeColor = Theme.Save;
            }
            return head;
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
            // 110 was measured against 11.5px labels and 28px controls. Body type is 14px
            // now and the pickers and sliders are 32, so the control sat 4px from the floor
            // of the card with the value label clipped against it.
            // Title, note, the control, then what it currently reads on each side. The
            // last of those used to start at y=84, ten pixels inside a control that now
            // stands 32px tall - so the slider was drawn straight over the top half of it
            // and the values looked sliced in two.
            Card c = new Card { Width = W, Height = 142, Margin = new Padding(0, 0, 0, 10) };
            Row row = new Row { Knob = k, Host = c };

            Label title = new Label {
                Text = k.Label, Location = new Point(18, 14), Size = new Size(W - 200, 22),
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
                Text = k.Note, Location = new Point(18, 38), Size = new Size(W - 48, 20),
                Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Panel,
                AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c.Controls.Add(note);

            if (k.Choices != null)
            {
                Picker d = new Picker { Location = new Point(18, 62), Size = new Size(320, 32) };
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
                    Location = new Point(18, 62), Size = new Size(W - 258, 32),
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
                    Text = "--", Location = new Point(W - 226, 64), Size = new Size(70, 26),
                    Font = Theme.Value, ForeColor = Theme.Text, BackColor = Theme.Panel,
                    TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                c.Controls.Add(row.Value);
            }

            // Two fixed columns rather than one run-on line, so the battery and plugged-in
            // values line up down the whole page and can be compared by eye. Inline they
            // started at a different x on every card, which made them unscannable.
            row.Status = new Label {
                Text = "", Location = new Point(18, 102), Size = new Size(SideCol - 24, 20),
                Font = Theme.Small, ForeColor = Theme.Text, BackColor = Theme.Panel,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            c.Controls.Add(row.Status);

            row.Status2 = new Label {
                Text = "", Location = new Point(SideCol, 102), Size = new Size(W - SideCol - 20, 20),
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
                menu.Items.Add(p.Friendly ?? p.Name, null, (s, e) => ApplyPreset(local));
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

            // ours to release: three timers, the WMI searchers and the named event
            _poll.Dispose(); _commit.Dispose(); _tick.Dispose();
            _bat.Dispose();
            if (_wake != null) { _wake.Dispose(); _wake = null; }
            if (_tray != null) { _tray.Dispose(); _tray = null; }

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

            }
            finally { _loading = false; }

            // Which profile the machine now matches, read back from the machine itself.
            // Every history point written from here on carries it.
            RefreshModeCode();

            // The sidebar and the mode cards name that profile too, so they follow the same
            // read rather than waiting for the next poll to catch up.
            RefreshStatus();
            RefreshModes();
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
        /// <summary>
        /// Change how far back the charts look. Reseeds rather than pushing, because the
        /// watts chart is a rolling buffer - it has to be refilled from history, not nudged.
        /// </summary>
        void SetRange(int minutes)
        {
            _rangeMin = minutes;
            foreach (PillButton b in _rangeBtns) b.Selected = RangeOf(b.Text) == minutes;
            foreach (PillButton b in _rangeBtns) b.Invalidate();
            RefreshHistory(true);
        }

        static int RangeOf(string label)
        {
            if (label == "1h") return 60;
            if (label == "6h") return 360;
            if (label == "24h") return 1440;
            if (label == "7d") return 10080;
            return 0;
        }

        string RangeWord()
        {
            if (_rangeMin == 60) return "the last hour";
            if (_rangeMin == 360) return "the last 6 hours";
            if (_rangeMin == 1440) return "the last 24 hours";
            if (_rangeMin == 10080) return "the last 7 days";
            return "everything recorded";
        }

        // What RefreshHistory last did its arithmetic over. The averages and the summary
        // line only change when a point is appended or the range button moves, but the poll
        // runs four times a minute - and over the All range that arithmetic sorts every
        // recorded point to find its percentiles.
        int _histVersion = -1, _histRange = -1;

        void RefreshHistory(bool seedChart)
        {
            // 0 means everything kept on disk - three months at one point a minute.
            List<HistPoint> pts = History.Recent(_rangeMin > 0 ? _rangeMin : 200000);
            _batChart.SetData(pts);

            // The arithmetic below only changes when a point is appended or the range moves,
            // but the poll runs four times a minute - and over the All range RuntimeAverage
            // sorts every recorded point to find its percentiles. Skip it when the inputs
            // are the same as last time.
            bool changed = seedChart || History.Version != _histVersion || _rangeMin != _histRange;
            _histVersion = History.Version;
            _histRange = _rangeMin;

            if (changed)
            {
                if (seedChart)
                {
                    List<double> w = new List<double>();
                    foreach (HistPoint p in pts) if (!p.Ac && p.W > 0.05) w.Add(p.W);
                    _chartWatts.Seed(w);
                }

                // the average runtime in the header comes from these same recorded points, so
                // it is recomputed here rather than anywhere it could drift out of step
                _avg = RuntimeAverage.From(pts);
                RefreshHistoryLabel(pts);
            }

            // Not inside the guard: this reads what is left in the pack right now, which
            // moves on every poll even when no new point has been recorded. Skipping it
            // froze the header time-left figure between minutes.
            PushAverage();

            RefreshOffenders();
        }

        /// <summary>The one-line summary under the charts.</summary>
        void RefreshHistoryLabel(List<HistPoint> pts)
        {
            History.Stats st = History.Summarise(pts);
            string line = "Showing " + RangeWord() + "   ·   " + st.Points + " minutes recorded";
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
                Cpu = Math.Round(ProcessWatch.TotalCpu(_procs, false), 1),
                BgMb = Math.Round(ProcessWatch.TotalMb(_procs, true), 0),
                M = _modeCode,
                Top = TopSummary()
            };
            History.Append(p, _procs, interval);

            // Saving is best-effort by design, but a failure used to be invisible. Anything
            // that could not be written says so here, once per distinct reason.
            List<string> problems = Diag.Drain();
            if (problems != null)
            {
                foreach (string line in problems) Log(line);
                ShowLog();
            }

            if (draining) _chartWatts.Push(p.W);
            RefreshHistory(false);
            RefreshOverview();
            RefreshBasic();
            if (_page == "modes") RefreshModes();
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
            _barHealth.Segments.Clear();

            if (!_bat.FullChargeMwh.HasValue)
            {
                // Plenty of machines simply do not report this. Saying so is better than a
                // blank card, and much better than a made-up percentage.
                _healthPct.Text = "Not reported by this PC";
                _healthNote.Text = "Windows exposes a full-charge capacity for most laptop batteries, " +
                                   "but not all of them. Everything else in this app still works - " +
                                   "it just cannot say how much this one has aged.";
                _barHealth.Invalidate();
                return;
            }

            double usable = _bat.FullChargeMwh.Value / 1000.0;
            double design = BatteryMonitor.OriginalDesignMwh / 1000.0;
            double lost = Math.Max(0, design - usable);
            _barHealth.Segments.Add(new Segment { Name = "still holds", Value = usable, Color = Theme.Save });
            _barHealth.Segments.Add(new Segment { Name = "lost to ageing", Value = lost, Color = Theme.Spend });

            if (design > 0.01)
            {
                double pct = 100.0 * usable / design;
                _healthPct.Text = pct.ToString("0") + "% of its original capacity";
                _healthPct.ForeColor = pct >= 80 ? Theme.Text : (pct >= 60 ? Theme.Spend : Theme.Alert);
            }
            else
            {
                _healthPct.Text = usable.ToString("0.0") + " Wh";
                _healthPct.ForeColor = Theme.Text;
            }

            string note = usable.ToString("0.0") + " Wh of the " + design.ToString("0.0") +
                          " Wh it shipped with.";
            if (_bat.Watts.HasValue && _bat.Watts.Value > 0.1)
                note += "  At the " + _bat.Watts.Value.ToString("0.00") + " W measured just now, that " +
                        "missing capacity is " + FmtHours(lost / _bat.Watts.Value) + " you no longer have.";
            else
                note += "  Use the laptop on battery for a minute and this will also say how much " +
                        "running time that works out to.";
            _healthNote.Text = note;

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
            RefreshStatus();
            RefreshAnalytics();
            RefreshContributors();

            string tip = "PowerDial  " + (_bat.OnAc ? "on AC" :
                (_bat.Watts.HasValue ? _bat.Watts.Value.ToString("0.0") + " W" : "measuring")) +
                "  " + _bat.PercentOfFull + "%";
            if (_tray != null) _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        /// <summary>
        /// The battery summary in the sidebar, which is on screen whichever section is.
        ///
        /// Charge, what is left and the mode in effect used to be spread across a pinned
        /// header, a card and a status line, and the header and the card could state the
        /// time left a minute apart. One block, one statement, visible everywhere.
        /// </summary>
        void RefreshStatus()
        {
            if (_status == null) return;
            _status.HasBattery = Machine.HasBattery;
            _status.OnAc = _bat.OnAc;
            _status.ChargePct = _bat.PercentOfFull;

            // Only the measured average earns a figure here. The live 60-second reading
            // answers "what am I drawing this minute", which swings by several watts.
            _status.HoursLeft = _bat.OnAc ? null : _avg.Left(_bat.RemainingMwh);

            Preset p = Presets.ByCode(_modeCode);
            _status.Mode = p == null ? "Custom settings" : (p.Friendly ?? p.Name);

            _status.Describe();
            _status.Invalidate();
        }

        /// <summary>
        /// The contributors list: core-seconds per process across the measurement window,
        /// drawn as a share bar. No watt figures, on purpose - see the info text.
        /// </summary>
        void PaintContributors(Graphics g)
        {
            Theme.Quality(g);
            g.Clear(Theme.Panel);

            List<KeyValuePair<string, double>> top = WindowTop(4);
            if (top.Count == 0)
            {
                Theme.Str(g, _bat.OnAc
                    ? "nothing timed yet - readings only happen on battery"
                    : "collecting - the first window takes " + _bat.WindowSeconds + " seconds",
                    Theme.Small, Theme.Dim, 0, 2);
                return;
            }

            // Bars are scaled against the busiest process, not against the total: this ranks
            // what was working, and a share-of-total bar would read as a share of the watts.
            double max = top[0].Value;
            if (max <= 0) max = 1;

            const int NameW = 150;                 // name column, then the bar, then the figure
            const int FigW = 58;
            int barMax = Math.Max(20, _contribList.Width - NameW - FigW - 12);

            int y = 0;
            int rank = 0;
            foreach (KeyValuePair<string, double> kv in top)
            {
                Theme.Str(g, kv.Key, Theme.Body, Theme.Text, 0, y);

                // A process with no window of its own is the easy one to miss, so it is
                // marked - quietly, because it is a label and not a verdict.
                if (IsBackground(kv.Key))
                {
                    float nx = g.MeasureString(kv.Key, Theme.Body).Width;
                    if (nx < NameW - 26)
                        Theme.Str(g, "bg", Theme.Small, Theme.Mute, nx + 4, y + 3);
                }

                // An empty track behind the bar, so a short bar still reads as a short bar
                // rather than as a rendering that has not finished.
                Theme.FillRound(g, new Rectangle(NameW, y + 6, barMax, 10), 3, Theme.Inset);
                int barW = (int)Math.Round(kv.Value / max * barMax);
                if (barW < 2) barW = 2;

                // Amber is energy leaving, and it only means that if it is spent on the one
                // actually spending it. Every row in this list used to be amber, which made
                // four ordinary processes look like four problems and drained the accent of
                // the meaning it exists to carry. The busiest gets it; the rest are neutral.
                Theme.FillRound(g, new Rectangle(NameW, y + 6, barW, 10), 3,
                                rank == 0 ? Theme.Spend : Theme.Data);

                Theme.StrRight(g, kv.Value.ToString("0.0") + "s", Theme.Small, Theme.Dim,
                               _contribList.Width, y + 2);
                y += 22;
                rank++;
            }
        }

        /// <summary>Did any instance of this executable own a window at the last sample?</summary>
        bool IsBackground(string name)
        {
            if (_procs == null) return false;
            foreach (ProcInfo pi in _procs)
                if (pi != null && pi.Name == name) return pi.Background;
            return false;
        }

        void RefreshContributors()
        {
            if (_contribList == null) return;
            int secs = 0;
            if (_window.Count > 0)
                secs = (int)Math.Round((DateTime.UtcNow - _window[0].At).TotalSeconds);
            // Plain language, and the same sentence the Overview card uses: this measures
            // processor time, and the app refuses to split measured watts across processes.
            _contribNote.Text = secs > 0
                ? "Processor time used over the last " + secs + " seconds. More time means more drain."
                : "Processor time used over the measured window. More time means more drain.";

            // The measured figure the list is describing. Only ever the reading itself: the
            // section refuses to split it across processes, so it must not look split.
            if (_wattsTotal != null)
            {
                if (_bat.OnAc) _wattsTotal.Text = "on power - nothing to measure";
                else if (!_bat.Watts.HasValue) _wattsTotal.Text = "measuring the first window";
                else _wattsTotal.Text = _bat.Watts.Value.ToString("0.0") + " W measured over " + secs + "s";
            }
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
            PillButton actLocal = act;
            act.Click += (o, e) => ApplyFix(local, actLocal);
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
        void ApplyFix(Suggestion s, PillButton btn)
        {
            // Advisory just opens a Windows page - that returns at once, and a spinner on
            // something instantaneous reads as a glitch rather than as progress.
            if (s.Kind == FixKind.Advisory)
            {
                string aerr = Advisor.Apply(s);
                if (aerr != null) { Log("Could not open Windows settings: " + aerr); ShowLog(); }
                else Log("Opened Windows settings for: " + s.Title);
                return;     // nothing changed here, so nothing to re-read
            }

            RunBusy(btn, delegate
            {
                string err = Advisor.Apply(s);
                if (err != null) { Log(s.Title + " - failed: " + err); ShowLog(); }
                else Log(s.Title + " - done (" + s.NowText + " to " + s.ThenText + ")");

                ResetDrawWindow();
                LoadValues(); RefreshWatch(); RefreshFixes();
            });
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

            WriteFixes(doable, _fixApplyAll);
        }

        /// <summary>
        /// Carry out a list of suggestions. The single place that happens for more than one
        /// at a time: the one button in Basic and Apply-every-fix in Advanced are the same
        /// work, and were the same twenty lines written twice.
        /// </summary>
        void WriteFixes(List<Suggestion> doable, PillButton on)
        {
            // The only "before" a saving can ever be measured against, taken before anything
            // changes.
            if (_avg.Known)
                Config.MarkOptimised(_avg.Watts, _avg.Minutes, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            BeginBusy(on, doable.Count + 1);
            int ok = 0, fail = 0;
            foreach (Suggestion s in doable)
            {
                BusyStep();
                string err = Advisor.Apply(s);
                if (err != null) { Log("   " + s.Title + ": " + err); fail++; }
                else { Log("   " + s.Title + " -> " + s.ThenText); ok++; }
            }
            Log("Applied " + ok + " suggestion(s)" + (fail > 0 ? ", " + fail + " failed" : "") + ".");
            if (fail > 0) ShowLog();

            // Whether this still matches a whole profile is for RefreshModeCode to say, once
            // LoadValues below has it read the machine back - not for this method to assume.
            ResetDrawWindow();
            BusyStep();
            LoadValues(); RefreshWatch(); RefreshFixes();
            EndBusy();
            RefreshBasic();
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


        /// <summary>
        /// Write one profile to the battery side, verifying every value as it goes.
        ///
        /// The single place a profile is applied. Basic mode and Advanced both come through
        /// here - the same click handler BuildProfileRow wires up for both rows, plus the
        /// tray menu via ApplyPreset - so the read-back that invariant 3 requires cannot be
        /// forgotten in one of them, and "how a profile is applied" has one definition.
        /// </summary>
        /// <param name="p">the profile</param>
        /// <param name="on">the button to spin while it runs, or null from the tray menu</param>
        void WriteProfile(Preset p, PillButton on)
        {
            string label = p.Friendly ?? p.Name;
            Log("Applying " + label + "...");

            // Note what it was drawing first: the only "before" a saving can be measured
            // against, and it has to be taken before anything changes.
            if (_avg.Known)
                Config.MarkOptimised(_avg.Watts, _avg.Minutes, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            BeginBusy(on, p.Values.Count + 1);
            int ok = 0, fail = 0;
            foreach (KeyValuePair<string, int> kv in p.Values)
            {
                Knob k = PowerCfg.Find(kv.Key);
                if (k == null) continue;                 // Windows does not define it here
                BusyStep();
                string err = PowerCfg.WriteDc(k, kv.Value);
                if (err != null) { Log("   could not set " + k.Label + ": " + err); fail++; continue; }
                int? back = PowerCfg.Read(k, true);
                if (back.HasValue && back.Value == kv.Value) { Log("   " + k.Label + " -> " + PowerCfg.Describe(k, kv.Value)); ok++; }
                else { Log("   " + k.Label + " did not stick"); fail++; }
            }
            Log(label + ": " + ok + " changed" + (fail > 0 ? ", " + fail + " failed" : "") + ".");
            if (fail > 0) ShowLog();

            // Selected is set by RefreshModeCode below (via LoadValues), from what the
            // machine reads back - not assumed here just because this is the profile that
            // was requested. A partly-failed write should not show as if it fully landed.
            ResetDrawWindow();
            BusyStep();
            LoadValues(); RefreshWatch(); RefreshFixes();
            EndBusy();
            RefreshBasic();
        }

        /// <summary>Applied from the tray menu, which has no button of its own to spin.</summary>
        void ApplyPreset(Preset p)
        {
            WriteProfile(p, null);
        }

        void RestoreBaseline()
        {
            BaselineFile bf = Baseline.Load();
            string when = bf.CapturedUtc == "(none)" ? "the tuning session" : bf.CapturedUtc + " UTC";
            int withAc = 0;
            foreach (BaselineEntry e in bf.Entries) if (e.AcValue.HasValue) withAc++;
            string acLine = withAc > 0
                ? "Both sides are restored: " + bf.Entries.Count + " on battery, " + withAc + " plugged in.\n"
                : bf.Entries.Count + " battery settings will be rewritten. Plugged-in values are left\n" +
                  "alone - this restore point predates the app recording them.\n";
            DialogResult r = MessageBox.Show(
                "Put the settings back as they were before PowerDial?\n\n" +
                "Restore point: " + when + "\n" +
                acLine,
                "Restore my settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;

            // Restore writes both sides of every setting in one call, so there is no
            // per-step hook to hang progress on - the arc spins with no count.
            BeginBusy(_btnRestore, 0);
            List<string> lines = Baseline.Restore();
            foreach (string line in lines) Log(line);
            // Selection follows from LoadValues -> RefreshModeCode below, the same honest
            // machine read every other write goes through.
            ResetDrawWindow();
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
    public sealed class Readout : Card
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
            // Mute, not Dim: this is where the figures above came from, which is worth
            // being able to read and is never the thing to read first.
            Theme.StrRight(g, prov, Theme.Small, Theme.Mute, Width - 14, top + 47);
        }

        /// <summary>One figure in the average band. Returns how far to step right.</summary>
        static float Band(Graphics g, float x, int top, string v, string label, Color c)
        {
            Theme.Str(g, v, Theme.Stat, c, x, top + 22);
            Theme.Str(g, label, Theme.Small, Theme.Dim, x + 2, top + 47);
            float w = Math.Max(Theme.TextW(g, v, Theme.Stat), Theme.TextW(g, label, Theme.Small));
            return w + 28;
        }

        static void Stat(Graphics g, float x, string v, string label, Color c)
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
