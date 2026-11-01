using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PowerDial
{
    internal sealed class AppEndingDialog : Form
    {
        AppEnding _review;
        readonly Func<List<ProcInfo>> _sample;
        readonly Picker _apps = new Picker();
        readonly DataGridView _processes = new DataGridView();
        readonly Label _appName = new Label(), _appInfo = new Label(), _count = new Label();
        readonly Label _noticeTitle = new Label(), _notice = new Label(), _status = new Label();
        readonly PillButton _end = new PillButton(), _done = new PillButton(), _refresh = new PillButton();
        bool _busy, _used, _loading, _confirming;
        internal string Result;
        internal event Action<string> Completed;

        internal AppEndingDialog(Func<List<ProcInfo>> sample)
        {
            _sample = sample;
            Text = "Manage background apps";
            BackColor = Theme.Ink; ForeColor = Theme.Text; Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(820, 710); MinimumSize = new Size(560, 680);
            MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            TextAt(this, "Manage background apps", 24, 22, 772, 38, Theme.Page, Theme.Lead);
            TextAt(this, "Review processes before ending an app.", 24, 68, 772, 26, Theme.Body, Theme.Dim);
            TextAt(this, "Choose a background app", 24, 106, 772, 22, Theme.Small, Theme.Dim);
            _apps.SetBounds(24, 132, 772, 34); _apps.Anchor |= AnchorStyles.Right;
            _apps.AccessibleName = "Background app to review"; Controls.Add(_apps);
            Card summary = new Card { Location = new Point(24, 184), Size = new Size(772, 78),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Selected background app" };
            Controls.Add(summary);
            TextAt(summary, "APP", 18, 16, 42, 22, Theme.Small, Theme.Save).Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Configure(_appName, summary, 78, 12, 484, 30, Theme.Head, Theme.Text);
            Configure(_appInfo, summary, 78, 43, 484, 23, Theme.Small, Theme.Dim);
            Configure(_count, summary, 570, 26, 184, 26, Theme.Body, Theme.Text);
            _count.TextAlign = ContentAlignment.MiddleRight; _count.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            TextAt(this, "Processes included", 24, 278, 772, 24, Theme.Section, Theme.Text);
            _processes.SetBounds(24, 312, 772, 168);
            _processes.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _processes.BackgroundColor = Theme.Panel; _processes.BorderStyle = BorderStyle.None;
            _processes.ReadOnly = true; _processes.AllowUserToAddRows = false; _processes.AllowUserToDeleteRows = false;
            _processes.AllowUserToResizeRows = false; _processes.RowHeadersVisible = false;
            _processes.AutoGenerateColumns = false; _processes.MultiSelect = false;
            _processes.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _processes.EnableHeadersVisualStyles = false; _processes.GridColor = Theme.Edge;
            _processes.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _processes.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            _processes.ColumnHeadersHeight = 34; _processes.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _processes.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle {
                BackColor = Theme.Inset, ForeColor = Theme.Dim, Font = Theme.Small, Padding = new Padding(8, 0, 0, 0),
                SelectionBackColor = Theme.Inset, SelectionForeColor = Theme.Dim };
            _processes.DefaultCellStyle = new DataGridViewCellStyle {
                BackColor = Theme.Panel, ForeColor = Theme.Text, Font = Theme.Small, Padding = new Padding(8, 0, 0, 0),
                SelectionBackColor = Theme.Raise, SelectionForeColor = Theme.Text };
            _processes.RowTemplate.Height = 34;
            Column("App name", 170); Column("PID", 80); Column("Executable path", 480);
            _processes.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _processes.Columns[2].MinimumWidth = 180;
            _processes.AccessibleName = "Reviewed processes: app name, PID and executable path. Ending affects all listed processes.";
            Controls.Add(_processes);
            Card notice = new Card { Location = new Point(24, 496), Size = new Size(772, 84),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Lift = false,
                AccessibleName = "Review the effect of ending an app" };
            Controls.Add(notice);
            Configure(_noticeTitle, notice, 16, 10, 740, 26, Theme.Section, Theme.Spend);
            Configure(_notice, notice, 16, 39, 740, 38, Theme.Small, Theme.Dim);
            Configure(_status, this, 24, 592, 772, 48, Theme.Small, Theme.Dim);
            _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _refresh.Text = "Refresh apps"; _refresh.Icon = ButtonIcon.Refresh;
            _done.Text = "Close"; _end.Text = "End app…"; _end.Icon = ButtonIcon.Stop;
            AddAction(_refresh); AddAction(_done); AddAction(_end);
            _refresh.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _apps.SelectedIndexChanged += (s, e) => SelectionChanged();
            _refresh.Click += async (s, e) => await ReloadAsync();
            _done.Click += (s, e) => { if (_confirming) CancelConfirmation(); else Close(); };
            _end.Click += async (s, e) => await ReviewOrEndAsync();
            _apps.Enabled = false; _end.Enabled = false;
            _appName.Text = "Finding background apps…";
            Shown += async (s, e) => await ReloadAsync();
        }

        void AddAction(PillButton button)
        { button.BackColor = Theme.Ink; button.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; Controls.Add(button); }

        void Column(string name, int width)
        {
            _processes.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        static Label TextAt(Control parent, string text, int x, int y, int width, int height, Font font, Color color)
        { Label label = new Label(); Configure(label, parent, x, y, width, height, font, color); label.Text = text; return label; }

        static void Configure(Label label, Control parent, int x, int y, int width, int height, Font font, Color color)
        {
            label.SetBounds(x, y, width, height); label.Font = font; label.ForeColor = color;
            label.BackColor = Color.Transparent; label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            parent.Controls.Add(label);
        }

        async Task ReloadAsync()
        {
            if (_busy || _loading) return;
            string selected = _review != null && _apps.SelectedIndex >= 0 ? _review.Groups[_apps.SelectedIndex].Name : null;
            _loading = true;
            _apps.Enabled = false; _end.Enabled = false; _refresh.Enabled = false; _refresh.Busy = true;
            _status.Text = "Checking app ownership and protected processes…";
            try
            {
                AppEnding next = await Task.Run(() => new AppEnding(_sample()));
                if (IsDisposed) { next.Dispose(); return; }
                _apps.SelectedIndex = -1; _apps.Items.Clear(); _processes.Rows.Clear();
                if (_review != null) _review.Dispose();
                _review = next;
                foreach (AppEndGroup group in _review.Groups)
                    _apps.Items.Add(group.DisplayName + " · " + group.Targets.Count + " processes");
                _used = false; _confirming = false; _end.Destructive = false;
                _apps.Enabled = _review.Groups.Count > 0; _end.Enabled = _apps.Enabled; _refresh.Enabled = true;
                _done.Text = "Close"; _refresh.Text = "Refresh apps";
                _status.Text = _review.Problem ?? "Only recognised apps from your session. System and protected processes are excluded.";
                if (_apps.Enabled) _apps.SelectedIndex = Math.Max(0, _review.Groups.FindIndex(g => g.Name == selected));
                else
                {
                    _appName.Text = "No eligible background apps"; _appInfo.Text = "Refresh to check again, or close this review.";
                    _count.Text = "0 processes";
                }
            }
            catch (Exception ex)
            {
                _status.Text = "Could not refresh apps: " + ex.Message;
                _apps.Enabled = false; _end.Enabled = false;
                _appName.Text = "App review unavailable"; _appInfo.Text = "Refresh to try again."; _count.Text = "";
            }
            finally
            {
                _loading = false;
                if (!IsDisposed) { _refresh.Busy = false; _refresh.Enabled = true; SelectionChanged(); }
            }
        }

        void SelectionChanged()
        {
            if (_loading) return;
            int index = _apps.SelectedIndex;
            if (index >= 0 && _review != null && index < _review.Groups.Count)
            {
                AppEndGroup group = _review.Groups[index];
                _appName.Text = group.DisplayName; _appInfo.Text = group.Name + ".exe";
                _count.Text = group.Targets.Count + (group.Targets.Count == 1 ? " process" : " processes");
                _processes.Rows.Clear();
                foreach (AppEndTarget target in group.Targets)
                    _processes.Rows.Add(AppEnding.DisplayName(target.Name), target.Id, target.Path);
                _processes.ClearSelection();
            }
            _noticeTitle.Text = "Save your work first";
            _notice.Text = "Ending stops all listed processes. Unsaved work may be lost; downloads, calls or syncing may stop.";
            _end.Enabled = !_used && !_busy && _apps.Enabled && index >= 0;
            LayoutActions();
        }

        void LayoutActions()
        {
            int y = ClientSize.Height - 56;
            _refresh.Visible = !_confirming; _refresh.SetBounds(24, y, 180, 36);
            _done.SetBounds(ClientSize.Width - (_confirming ? 174 : 310), y, _confirming ? 150 : 90, 36);
            // Move the final action away from the initial button to prevent double-click confirmation.
            _end.SetBounds(_confirming ? 24 : ClientSize.Width - 204, y, 180, 36);
        }

        void CancelConfirmation()
        {
            _confirming = false; _end.Destructive = false; _end.Text = "End app…";
            _apps.Enabled = _review != null && _review.Groups.Count > 0;
            _refresh.Enabled = true; _done.Text = "Close"; SelectionChanged(); _end.Invalidate();
        }

        async Task ReviewOrEndAsync()
        {
            if (_busy || _loading || _used || _apps.SelectedIndex < 0) return;
            AppEndGroup group = _review.Groups[_apps.SelectedIndex];
            if (!_confirming)
            {
                _confirming = true; _apps.Enabled = false; _refresh.Enabled = false;
                _noticeTitle.Text = "End " + group.DisplayName + "?";
                _notice.Text = "Unsaved work may be lost. This forcibly stops all " + group.Targets.Count +
                    " listed processes and cannot be undone. Choose Keep running to cancel.";
                _end.Text = "End app now"; _end.Destructive = true; _end.Invalidate();
                _done.Text = "Keep running"; LayoutActions(); _done.Focus(); return;
            }
            _busy = true; _end.Enabled = false; _done.Enabled = false; _end.Busy = true;
            try
            {
                Result = await Task.Run(() => _review.End(group));
                _status.Text = Result; if (Completed != null) Completed(Result);
            }
            catch (Exception ex)
            {
                Result = "App ending failed: " + ex.Message; _status.Text = Result;
                if (Completed != null) Completed(Result);
            }
            finally
            {
                _busy = false; _used = true; _confirming = false; _end.Busy = false; _end.Destructive = false;
                _end.Text = "End app…"; _done.Text = "Close"; _done.Enabled = true; _refresh.Enabled = true;
                _noticeTitle.Text = "Review complete";
                _apps.Enabled = false; _refresh.Text = "Review another";
                _notice.Text = "Choose Review another to check the remaining apps. Apps may restart themselves.";
                LayoutActions();
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (_end != null && _done != null && _refresh != null) LayoutActions();
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        { if (_busy) e.Cancel = true; base.OnFormClosing(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && !_busy)
            { e.Handled = true; if (_confirming) CancelConfirmation(); else Close(); }
            base.OnKeyDown(e);
        }
        protected override void Dispose(bool disposing)
        { if (disposing && _review != null) _review.Dispose(); base.Dispose(disposing); }
    }
}
