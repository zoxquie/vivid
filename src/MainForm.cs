using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Vivid
{
    public enum PageId { Filters, Crosshair, Settings }

    /// <summary>Borderless, fully custom-drawn main window.</summary>
    public sealed class MainForm : Form
    {
        public AppController Ctl { get; }
        public PageId Current { get; private set; } = PageId.Filters;

        public const float TopBarH = 64;
        readonly TopBar topBar;
        readonly Dictionary<PageId, Page> pages = new Dictionary<PageId, Page>();
        Dialog dialog;

        readonly TextBox edit;
        Action<string> editCommit;
        RectangleF editRect;
        bool editPersistent;

        string toastText;
        DateTime toastAt;
        readonly Timer uiTimer;

        DateTime hoverSince;
        Point mouse;
        Bitmap bgCache;
        bool animRequested;
        DateTime lastPulse;

        readonly NotifyIcon tray;
        bool exiting;
        bool allowShow;
        readonly uint showMsg, quitMsg;

        public MainForm(AppController ctl, bool startHidden)
        {
            Ctl = ctl;
            Ui.Form = this;
            allowShow = !startHidden;

            Text = "Vivid";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            KeyPreview = true;
            try { Icon = Program.AppIcon; } catch { }

            var wa = Screen.PrimaryScreen.WorkingArea;
            float S = Theme.Scale;
            MinimumSize = new Size((int)Math.Min(1060 * S, wa.Width), (int)Math.Min(600 * S, wa.Height));
            Size = new Size((int)Math.Min(1280 * S, wa.Width * 0.92), (int)Math.Min(830 * S, wa.Height * 0.92));

            edit = new TextBox { Visible = false, BorderStyle = BorderStyle.None, BackColor = Theme.Surface3, ForeColor = Theme.Text };
            BackColor = Theme.Bg;
            edit.KeyDown += Edit_KeyDown;
            edit.Leave += (s, e) => { if (!editPersistent) EndTextEdit(true); };
            Controls.Add(edit);

            topBar = new TopBar();
            pages[PageId.Filters] = new FiltersPage();
            pages[PageId.Crosshair] = new CrosshairPage();
            pages[PageId.Settings] = new SettingsPage();
            foreach (var p in pages.Values) p.Build();
            topBar.Build();

            uiTimer = new Timer { Interval = 15 };
            uiTimer.Tick += UiTick;
            uiTimer.Start();

            Ctl.Changed += () => { if (!IsDisposed) { topBar.Sync(); Invalidate(); } };
            Updater.Changed += () =>
            {
                if (IsDisposed) return;
                DoLayout(); Invalidate();
                if (Updater.Status == Updater.State.Ready) ShowToast("Vivid " + Updater.ReadyVersion + " is ready. Restart to use it.");
            };
            Ctl.StructureChanged += () => { if (!IsDisposed) { pages[PageId.Filters].Build(); DoLayout(); Invalidate(); } };
            Ctl.Toast += ShowToast;
            Ctl.ShowWindowRequested = () => { if (Visible && WindowState != FormWindowState.Minimized && ContainsFocus) HideToTray(); else ShowFromTray(); };

            tray = new NotifyIcon { Text = "Vivid", Icon = Icon ?? SystemIcons.Application, Visible = Native.IsWindows };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
            tray.ContextMenuStrip = BuildTrayMenu();

            try { showMsg = Native.IsWindows ? Native.RegisterWindowMessage("Vivid.ShowWindow") : 0; } catch { }
            try { quitMsg = Native.IsWindows ? Native.RegisterWindowMessage("Vivid.Quit") : 0; } catch { }
            DoLayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            DoLayout();
            Invalidate();
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            UpdateMaximizedBounds();
        }

        void UpdateMaximizedBounds()
        {
            if (!IsHandleCreated || WindowState == FormWindowState.Maximized) return;
            // Keep a maximized borderless window off the taskbar (also when maximized by double-clicking the title area).
            var scr = Screen.FromHandle(Handle);
            MaximizedBounds = new Rectangle(scr.WorkingArea.X - scr.Bounds.X, scr.WorkingArea.Y - scr.Bounds.Y, scr.WorkingArea.Width, scr.WorkingArea.Height);
        }

        // ---------------- window chrome ----------------

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX | Native.WS_SYSMENU;
                cp.ClassStyle |= Native.CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!Native.IsWindows) return;
            try
            {
                int round = Native.DWMWCP_ROUND;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
                int dark = 1;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
            }
            catch { }
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!allowShow)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        public void ShowFromTray()
        {
            allowShow = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            if (Native.IsWindows) Native.SetForegroundWindow(Handle);
        }

        void HideToTray()
        {
            Hide();
            if (!Ctl.Settings.TrayHintShown && Native.IsWindows)
            {
                Ctl.Settings.TrayHintShown = true;
                Ctl.SaveSoon();
                tray.ShowBalloonTip(3000, "Vivid is still running", "Filters and hotkeys stay active. Open Vivid from the tray icon.", ToolTipIcon.Info);
            }
        }

        public void ExitApp()
        {
            exiting = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing && Ctl.Settings.CloseToTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            EndTextEdit(true);
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (showMsg != 0 && m.Msg == showMsg) { ShowFromTray(); return; }
            // another copy of Vivid (usually a newer one you just opened) is taking over
            if (quitMsg != 0 && m.Msg == quitMsg) { BeginInvoke(new Action(ExitApp)); return; }
            if (m.Msg == Native.WM_NCHITTEST)
            {
                base.WndProc(ref m);
                long lp = m.LParam.ToInt64();
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int ht = ChromeHitTest(p);
                if (ht != Native.HTCLIENT) m.Result = new IntPtr(ht);
                return;
            }
            base.WndProc(ref m);
        }

        int ChromeHitTest(Point p)
        {
            float S = Theme.Scale;
            if (WindowState == FormWindowState.Normal)
            {
                int b = (int)(6 * S);
                bool l = p.X < b, r = p.X >= ClientSize.Width - b, t = p.Y < b, bo = p.Y >= ClientSize.Height - b;
                if (t && l) return Native.HTTOPLEFT;
                if (t && r) return Native.HTTOPRIGHT;
                if (bo && l) return Native.HTBOTTOMLEFT;
                if (bo && r) return Native.HTBOTTOMRIGHT;
                if (l) return Native.HTLEFT;
                if (r) return Native.HTRIGHT;
                if (t) return Native.HTTOP;
                if (bo) return Native.HTBOTTOM;
            }
            if (dialog == null && p.Y < TopBarH * S && HitTest(p) == null) return Native.HTCAPTION;
            return Native.HTCLIENT;
        }

        public void ToggleMaximize()
        {
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            else
            {
                UpdateMaximizedBounds();
                WindowState = FormWindowState.Maximized;
            }
        }

        // ---------------- pages & layout ----------------

        public void ShowPage(PageId id)
        {
            EndTextEdit(true);
            CancelKeyCapture();
            Current = Current == id && id == PageId.Settings ? PageId.Filters : id;
            if (Current == PageId.Settings) Ctl.Engine.ProbeNv(); // fills in the vibrance status line
            DoLayout();
            Invalidate();
        }

        public Page CurrentPage => pages[Current];

        public void RebuildAll()
        {
            foreach (var p in pages.Values) p.Build();
            topBar.Build();
            DoLayout();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateMaximizedBounds();
            if (pages.Count > 0) { topBar.Sync(); DoLayout(); }
        }

        public void DoLayout()
        {
            RenderCache.FitWindow(ClientSize);
            float S = Theme.Scale;
            var cs = ClientSize;
            topBar.Layout(new RectangleF(0, 0, cs.Width, TopBarH * S));
            var area = new RectangleF(24 * S, TopBarH * S + 22 * S, cs.Width - 48 * S, cs.Height - TopBarH * S - 44 * S);
            foreach (var p in pages.Values) p.Visible = false;
            var cur = pages[Current];
            cur.Visible = true;
            cur.Layout(area);
            dialog?.Layout(new RectangleF(0, 0, cs.Width, cs.Height));
            if (edit.Visible && !editPersistent) EndTextEdit(true);
        }

        public Widget HitTest(PointF p)
        {
            if (dialog != null) return dialog.HitTest(p);
            return topBar.HitTest(p) ?? pages[Current].HitTest(p);
        }

        // ---------------- painting ----------------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Anim.BeginFrame();
            Theme.Prepare(g);
            // Only the status pill is pulsing: repaint just the top bar for those frames.
            bool topOnly = e.ClipRectangle.Bottom <= TopBarH * Theme.Scale && dialog == null && toastText == null;
            PaintBackground(g, ClientSize);
            if (topOnly) { topBar.Paint(g); return; }
            PaintAll(g, ClientSize, background: false);
        }

        public void RequestAnimationFrame() => animRequested = true;

        /// <summary>Deep background with soft coloured light and a faint dot grid. Cached per window size.</summary>
        void PaintBackground(Graphics g, Size size)
        {
            if (bgCache == null || bgCache.Size != size)
            {
                bgCache?.Dispose();
                bgCache = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var bg = Graphics.FromImage(bgCache))
                {
                    Theme.Prepare(bg);
                    bg.Clear(Theme.Bg);
                    float w = size.Width, h = size.Height, S = Theme.Scale;
                    Theme.RadialGlow(bg, new PointF(w * 0.82f, h * 0.02f), w * 0.55f, h * 0.70f, Theme.Cyan, 85);
                    Theme.RadialGlow(bg, new PointF(w * 0.06f, h * 0.28f), w * 0.40f, h * 0.55f, Theme.Accent, 26);
                    Theme.RadialGlow(bg, new PointF(w * 0.95f, h * 1.05f), w * 0.40f, h * 0.45f, Theme.AccentEnd, 30);
                    float step = 34 * S;
                    using (var line = new Pen(Color.FromArgb(34, 38, 62, 75), Math.Max(1, S)))
                    {
                        for (float y = step; y < h; y += step) bg.DrawLine(line, 0, y, w, y);
                        for (float x = step; x < w; x += step) bg.DrawLine(line, x, 0, x, h);
                    }
                    // fade the grid toward the bottom so it doesn't fight the content
                    using (var fade = Theme.Gradient(new RectangleF(0, 0, w, h), Color.FromArgb(0, 7, 15, 23), Color.FromArgb(200, 7, 15, 23), 90f)) bg.FillRectangle(fade, 0, 0, w, h);
                }
            }
            g.DrawImageUnscaled(bgCache, 0, 0);
        }

        public void PaintAll(Graphics g, Size size, bool background = true)
        {
            float S = Theme.Scale;
            if (background) PaintBackground(g, size);
            topBar.Paint(g);
            pages[Current].Paint(g);
            dialog?.Paint(g);
            PaintToast(g, size);
            PaintTooltip(g, size);
            if (WindowState == FormWindowState.Normal)
            {
                var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
                using (var p = new Pen(Theme.White(30), 1)) g.DrawRectangle(p, 0, 0, size.Width - 1, size.Height - 1);
                g.SmoothingMode = sm;
            }
        }

        void PaintToast(Graphics g, Size size)
        {
            if (toastText == null) return;
            double ms = (DateTime.UtcNow - toastAt).TotalMilliseconds;
            if (ms > 2600) { toastText = null; return; }
            float S = Theme.Scale;
            float alpha = (float)Math.Min(1, Math.Min(ms / 120, (2600 - ms) / 300));
            var f = Theme.F(13, semibold: true);
            var ts = Theme.Measure(toastText, f);
            float w = ts.Width + 40 * S, h = 40 * S;
            var r = new RectangleF((size.Width - w) / 2, size.Height - h - 30 * S, w, h);
            r.Y += (1 - alpha) * 10 * S;
            Theme.Glow(g, r, h / 2, Theme.AccentMid, 12 * S, (int)(60 * alpha));
            Theme.FillRound(g, r, h / 2, Theme.WithAlpha(Theme.Hex("#0D1B27"), (int)(250 * alpha)));
            using (var b = Theme.AccentBrush(r, (int)(220 * alpha))) Theme.StrokeRound(g, r, h / 2, b, Math.Max(1, S));
            Theme.Txt(g, toastText, f, Theme.WithAlpha(Theme.Text, (int)(255 * alpha)), r, Theme.H.Center);
        }

        void PaintTooltip(Graphics g, Size size)
        {
            var h = Ui.Hover;
            if (h == null || Ui.Pressed != null || string.IsNullOrEmpty(h.Tooltip)) return;
            if ((DateTime.UtcNow - hoverSince).TotalMilliseconds < 550) return;
            float S = Theme.Scale;
            var f = Theme.F(12);
            var ts = Theme.Measure(h.Tooltip, f, 280 * S);
            var r = new RectangleF(mouse.X + 2 * S, mouse.Y + 22 * S, ts.Width + 20 * S, ts.Height + 12 * S);
            if (r.Right > size.Width - 8 * S) r.X = size.Width - 8 * S - r.Width;
            if (r.Bottom > size.Height - 8 * S) r.Y = mouse.Y - r.Height - 8 * S;
            Theme.Glow(g, r, 8 * S, Theme.Black(255), 8 * S, 60);
            Theme.FillRound(g, r, 8 * S, Theme.Hex("#12222F"));
            Theme.StrokeRound(g, r, 8 * S, Theme.White(40), Math.Max(1, S));
            Theme.Txt(g, h.Tooltip, f, Theme.Text, RectangleF.Inflate(r, -10 * S, -6 * S), Theme.H.Left, false, true);
        }

        void UiTick(object s, EventArgs e)
        {
            if (!Visible || WindowState == FormWindowState.Minimized) return;
            if (animRequested || toastText != null) { animRequested = false; Invalidate(); return; }
            if (Anim.Busy)
            {
                // repaint just the widgets that are still animating
                if (Anim.DirtyArea(out var area)) Invalidate(area); else Invalidate();
                return;
            }
            if (Ui.Hover != null && !string.IsNullOrEmpty(Ui.Hover.Tooltip))
            {
                double ms = (DateTime.UtcNow - hoverSince).TotalMilliseconds;
                if (ms > 550 && ms < 700) { Invalidate(); return; }
            }
            // gentle pulse on the status pill (~30 fps, top bar only)
            if (Ctl.Settings.FiltersOn && (DateTime.UtcNow - lastPulse).TotalMilliseconds > 33)
            {
                lastPulse = DateTime.UtcNow;
                var pr = topBar.PillRect;
                if (!pr.IsEmpty) Invalidate(Rectangle.Round(new RectangleF(pr.X, pr.Y, 50 * Theme.Scale, pr.Height)));
            }
        }

        public void ShowToast(string text)
        {
            toastText = text;
            toastAt = DateTime.UtcNow;
            Invalidate();
        }

        // ---------------- mouse ----------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            if (Ui.Pressed != null) { Ui.Pressed.MouseDrag(e.Location); Invalidate(); return; }
            UpdateHover(e.Location);
        }

        void UpdateHover(Point p)
        {
            var h = HitTest(p);
            if (h is Segmented seg) seg.TrackHover(p);
            if (h != Ui.Hover)
            {
                Ui.Hover = h;
                hoverSince = DateTime.UtcNow;
                Cursor = h?.Cursor ?? Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (Ui.Pressed == null && Ui.Hover != null) { Ui.Hover = null; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var h = HitTest(e.Location);
            if (Ui.Capturing != null && h != Ui.Capturing) CancelKeyCapture();
            if (edit.Visible && !editRect.Contains(e.Location) && !editPersistent) EndTextEdit(true);
            if (!edit.Visible || !editPersistent) Focus();
            if (e.Button == MouseButtons.Right) { h?.RightClick(e.Location); Invalidate(); return; }
            if (e.Button != MouseButtons.Left) return;
            Ui.Pressed = h;
            h?.MouseDown(e.Location, e.Button);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var pr = Ui.Pressed;
            Ui.Pressed = null;
            if (pr != null)
            {
                bool inside = HitTest(e.Location) == pr;
                pr.MouseUp(e.Location, inside, e.Button);
            }
            if (!IsDisposed) { UpdateHover(e.Location); Invalidate(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            HitTest(e.Location)?.DoubleClick(e.Location);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var h = HitTest(e.Location);
            // Scroll the nearest scrollable panel first, so the wheel never changes a slider by accident.
            for (var w = h; w != null; w = w.Parent)
                if (w is ScrollPanel sp && sp.ContentHeight > sp.Bounds.Height + 1) { sp.Wheel(e.Delta); UpdateHover(e.Location); return; }
            for (var w = h; w != null; w = w.Parent)
                if (w.Wheel(e.Delta)) { Invalidate(); return; }
        }

        // ---------------- keyboard capture ----------------

        public void BeginKeyCapture(KeyBindButton b)
        {
            EndTextEdit(true);
            Ui.Capturing = b;
            Ctl.SuspendHotkeys(true);
            Focus();
            Invalidate();
        }

        public void CancelKeyCapture()
        {
            if (Ui.Capturing == null) return;
            Ui.Capturing = null;
            Ctl.SuspendHotkeys(false);
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (Ui.Capturing is KeyBindButton kb)
            {
                if (kb.HandleKey(keyData)) CancelKeyCapture();
                return true;
            }
            if (!edit.Visible && keyData == Keys.Escape && dialog != null) { CloseDialog(); return true; }
            if (!edit.Visible && keyData == Keys.Enter && dialog != null) { dialog.Accept(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            CancelKeyCapture();
        }

        // ---------------- text editing ----------------

        public bool IsEditing(RectangleF r) => edit.Visible && editRect == r;

        public bool IsEditingAny => edit.Visible;

        public void MoveEditor(RectangleF r)
        {
            float S = Theme.Scale;
            editRect = r;
            int h = edit.PreferredHeight;
            edit.Bounds = new Rectangle((int)(r.X + 10 * S), (int)(r.Y + (r.Height - h) / 2), (int)(r.Width - 20 * S), h);
        }

        public string EditText => edit.Text;

        public void BeginTextEdit(RectangleF r, string text, Action<string> commit, bool center = false, bool persistent = false)
        {
            EndTextEdit(true);
            float S = Theme.Scale;
            editRect = r;
            editCommit = commit;
            editPersistent = persistent;
            edit.Font = Theme.F(center ? 13.5f : 14, semibold: center);
            edit.TextAlign = center ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            edit.BackColor = center ? Theme.Surface3 : Theme.Surface2;
            int h = edit.PreferredHeight;
            edit.Bounds = new Rectangle((int)(r.X + 10 * S), (int)(r.Y + (r.Height - h) / 2), (int)(r.Width - 20 * S), h);
            edit.Text = text ?? "";
            edit.Visible = true;
            edit.Focus();
            edit.SelectAll();
            Invalidate();
        }

        public void EndTextEdit(bool commit)
        {
            if (!edit.Visible) return;
            var cb = editCommit;
            string t = edit.Text;
            editCommit = null;
            edit.Visible = false;
            editPersistent = false;
            if (commit) cb?.Invoke(t);
            if (!IsDisposed) { Focus(); Invalidate(); }
        }

        void Edit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (editPersistent && dialog != null) dialog.Accept();
                else EndTextEdit(true);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                if (editPersistent && dialog != null) CloseDialog();
                else EndTextEdit(false);
            }
        }

        // ---------------- dialogs ----------------

        public void OpenDialog(Dialog d)
        {
            EndTextEdit(true);
            CancelKeyCapture();
            dialog = d;
            d.Build();
            d.Layout(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height));
            d.Opened();
            Ui.Hover = null;
            Invalidate();
        }

        public void CloseDialog()
        {
            if (dialog == null) return;
            editPersistent = false;
            EndTextEdit(false);
            CancelKeyCapture();
            dialog = null;
            Invalidate();
        }

        public void Prompt(string title, string message, string initial, string okText, Action<string> ok) =>
            OpenDialog(new PromptDialog(title, message, initial, okText, ok));

        public void Confirm(string title, string message, string okText, Action ok, bool danger = false) =>
            OpenDialog(new ConfirmDialog(title, message, okText, ok, danger));

        public void BindKeyDialog(string title, Func<Hotkey> get, Action<Hotkey> set) =>
            OpenDialog(new KeyDialog(title, get, set));

        // ---------------- menus ----------------

        public ContextMenuStrip NewMenu()
        {
            var m = new ContextMenuStrip
            {
                Renderer = new DarkMenuRenderer(),
                BackColor = Theme.Surface2,
                ForeColor = Theme.Text,
                ShowImageMargin = false,
                Font = new Font(Theme.RegularFamily, 9.5f),
            };
            return m;
        }

        public void ShowMenu(ContextMenuStrip m, PointF at)
        {
            m.Show(this, Point.Round(at));
        }

        public void ProfileMenu(Profile p, PointF at)
        {
            var m = NewMenu();
            if (!p.BuiltIn)
                m.Items.Add("Rename…", null, (s, e) => Prompt("Rename profile", "Give this profile a new name.", p.Name, "Rename", n => Ctl.Rename(p, n)));
            m.Items.Add(p.BuiltIn ? "Copy to my profiles" : "Duplicate", null, (s, e) => { Ctl.Duplicate(p); ShowToast("Copied to My profiles"); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Bind key…", null, (s, e) => BindKeyDialog("Key for " + p.Name, () => Ctl.KeyFor(p), k => Ctl.SetProfileKey(p, k)));
            if (Ctl.KeyFor(p) != null) m.Items.Add("Clear key", null, (s, e) => Ctl.SetProfileKey(p, null));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Set card image…", null, (s, e) => PickCardImage(p));
            if (CardArt.HasCustom(p)) m.Items.Add("Remove card image", null, (s, e) => { CardArt.Clear(p); Invalidate(); ShowToast("Card image removed"); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Copy share code", null, (s, e) => CopyShareCode(p));
            m.Items.Add("Export to file…", null, (s, e) => ExportProfiles(new[] { p }, p.Name));
            if (!p.BuiltIn)
            {
                m.Items.Add(new ToolStripSeparator());
                var del = m.Items.Add("Delete", null, (s, e) => Confirm("Delete profile", $"Delete \"{p.Name}\"? This can't be undone.", "Delete", () => Ctl.Delete(p), danger: true));
                del.ForeColor = Theme.Red;
            }
            ShowMenu(m, at);
        }

        const string ImageFilter = "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All files (*.*)|*.*";

        string PickImage(string title)
        {
            using (var dlg = new OpenFileDialog { Filter = ImageFilter, Title = title })
            {
                try
                {
                    // Start in Pictures; Steam screenshots can also be found from Steam > View > Screenshots.
                    dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                }
                catch { }
                return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
            }
        }

        public void PickCardImage(Profile p)
        {
            string f = PickImage("Pick a screenshot for " + p.Name);
            if (f == null) return;
            try { CardArt.Set(p, f); ShowToast("Card image set for " + p.Name); }
            catch { ShowToast("That file isn't an image Windows can open"); }
            Invalidate();
        }

        public void PickPreviewImage()
        {
            string f = PickImage("Pick a screenshot for the live preview");
            if (f == null) return;
            try { CardArt.SetPreview(f); ShowToast("Preview now uses your screenshot"); }
            catch { ShowToast("That file isn't an image Windows can open"); }
            DoLayout();
            Invalidate();
        }

        public void PreviewMenu(PointF at)
        {
            var m = NewMenu();
            m.Items.Add("Use my own screenshot…", null, (s, e) => PickPreviewImage());
            if (CardArt.HasCustomPreview)
                m.Items.Add("Use the preset pictures", null, (s, e) => { CardArt.ClearPreview(); DoLayout(); Invalidate(); ShowToast("Preview reset"); });
            ShowMenu(m, at);
        }

        public void ImportMenu(PointF at)
        {
            var m = NewMenu();
            m.Items.Add("Import from file…", null, (s, e) => ImportFromFile());
            m.Items.Add("Paste share code", null, (s, e) => ImportFromClipboard());
            ShowMenu(m, at);
        }

        public void ExportMenu(PointF at)
        {
            var m = NewMenu();
            var sel = Ctl.Selected;
            m.Items.Add("Export \"" + sel.Name + "\"…", null, (s, e) => ExportProfiles(new[] { sel }, sel.Name));
            var all = m.Items.Add("Export all my profiles…", null, (s, e) => ExportProfiles(Ctl.UserProfiles, "Vivid profiles"));
            all.Enabled = Ctl.UserProfiles.Count > 0;
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Copy share code for \"" + sel.Name + "\"", null, (s, e) => CopyShareCode(sel));
            ShowMenu(m, at);
        }

        void CopyShareCode(Profile p)
        {
            var copy = new Profile { Name = p.Id == Presets.CustomId ? "Custom" : p.Name, Values = (p.Id == Presets.CustomId ? Ctl.Settings.Custom : p.Values).Clone() };
            try { Clipboard.SetText(Storage.ToShareCode(copy)); ShowToast("Share code copied"); }
            catch { ShowToast("Could not use the clipboard"); }
        }

        void ExportProfiles(IEnumerable<Profile> list, string suggested)
        {
            var items = list.Select(p => new Profile { Name = p.Name, Values = (p.Id == Presets.CustomId ? Ctl.Settings.Custom : p.Values).Clone() }).ToList();
            using (var dlg = new SaveFileDialog { Filter = "Vivid profiles (*.json)|*.json", FileName = MakeFileName(suggested) + ".json", Title = "Export profiles" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dlg.FileName, Storage.ExportJson(items)); ShowToast(items.Count == 1 ? "Profile exported" : $"{items.Count} profiles exported"); }
                catch { ShowToast("Could not write that file"); }
            }
        }

        static string MakeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim().Length == 0 ? "profile" : s.Trim();
        }

        void ImportFromFile()
        {
            using (var dlg = new OpenFileDialog { Filter = "Vivid profiles (*.json)|*.json|All files (*.*)|*.*", Multiselect = true, Title = "Import profiles" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                int n = 0;
                foreach (var f in dlg.FileNames)
                {
                    try { n += Ctl.Import(Storage.ImportJson(File.ReadAllText(f))); }
                    catch { }
                }
                ShowToast(n == 0 ? "No profiles found in that file" : n == 1 ? "1 profile imported" : $"{n} profiles imported");
            }
        }

        void ImportFromClipboard()
        {
            try
            {
                string t = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                int n = Ctl.Import(Storage.FromShareCodeOrJson(t));
                ShowToast(n == 0 ? "No share code on the clipboard" : n == 1 ? "1 profile imported" : $"{n} profiles imported");
            }
            catch { ShowToast("That share code isn't valid"); }
        }

        ContextMenuStrip BuildTrayMenu()
        {
            var m = NewMenu();
            m.Opening += (s, e) =>
            {
                m.Items.Clear();
                var open = m.Items.Add("Open Vivid", null, (a, b) => ShowFromTray());
                open.Font = new Font(m.Font, FontStyle.Bold);
                m.Items.Add(new ToolStripSeparator());
                var f = new ToolStripMenuItem("Filters on", null, (a, b) => Ctl.SetFiltersOn(!Ctl.Settings.FiltersOn)) { Checked = Ctl.Settings.FiltersOn };
                var c = new ToolStripMenuItem("Crosshair", null, (a, b) => Ctl.SetCrosshairEnabled(!Ctl.Settings.Crosshair.Enabled)) { Checked = Ctl.Settings.Crosshair.Enabled };
                m.Items.Add(f);
                m.Items.Add(c);
                var prof = new ToolStripMenuItem("Profile");
                foreach (var p in Ctl.AllProfiles())
                {
                    var pp = p;
                    prof.DropDownItems.Add(new ToolStripMenuItem(p.Name, null, (a, b) => { Ctl.Select(pp, fromHotkey: true); }) { Checked = Ctl.Settings.SelectedProfileId == p.Id });
                }
                prof.DropDown.BackColor = Theme.Surface2;
                prof.DropDown.ForeColor = Theme.Text;
                ((ToolStripDropDownMenu)prof.DropDown).ShowImageMargin = false;
                ((ToolStripDropDownMenu)prof.DropDown).ShowCheckMargin = true;
                m.Items.Add(prof);
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("Quit", null, (a, b) => ExitApp());
                e.Cancel = false;
            };
            m.Items.Add("…");
            return m;
        }

        sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (e.Item.ForeColor != Theme.Red) e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextDim;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Theme.TextMuted;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                var r = e.ImageRectangle;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Icons.Check(e.Graphics, new RectangleF(r.X, r.Y, r.Width, r.Height), Theme.Green);
            }
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Theme.Surface2;
            public override Color MenuBorder => Theme.BorderStrong;
            public override Color MenuItemBorder => Theme.Surface3;
            public override Color MenuItemSelected => Theme.Surface3;
            public override Color MenuItemSelectedGradientBegin => Theme.Surface3;
            public override Color MenuItemSelectedGradientEnd => Theme.Surface3;
            public override Color MenuItemPressedGradientBegin => Theme.Surface3;
            public override Color MenuItemPressedGradientEnd => Theme.Surface3;
            public override Color ImageMarginGradientBegin => Theme.Surface2;
            public override Color ImageMarginGradientMiddle => Theme.Surface2;
            public override Color ImageMarginGradientEnd => Theme.Surface2;
            public override Color SeparatorDark => Theme.Border;
            public override Color SeparatorLight => Theme.Surface2;
            public override Color CheckBackground => Theme.Surface2;
            public override Color CheckSelectedBackground => Theme.Surface3;
            public override Color CheckPressedBackground => Theme.Surface3;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { uiTimer.Dispose(); edit.Dispose(); bgCache?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
