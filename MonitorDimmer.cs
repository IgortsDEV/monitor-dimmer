// Monitor Dimmer: covers chosen monitors with a black full-screen window.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Native
{
    public const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_HOTKEY = 0x312;
    public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

    public static float ScaleAt(Point p)
    {
        try
        {
            POINT pt; pt.X = p.X; pt.Y = p.Y;
            uint x, y;
            if (GetDpiForMonitor(MonitorFromPoint(pt, 2), 0, out x, out y) == 0) return x / 96f;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1f;
    }
}

class Display
{
    public int Number;
    public string Id;
    public Rectangle Bounds;

    // Numbered left to right, so the numbers match how the monitors sit on the desk.
    public static List<Display> ReadAll()
    {
        var list = new List<Display>();
        int n = 1;
        foreach (var s in Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top))
            list.Add(new Display { Number = n++, Id = s.DeviceName, Bounds = s.Bounds });
        return list;
    }
}

// Topmost borderless window that never takes keyboard focus from the app being used.
class FloatingWindow : Form
{
    public FloatingWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var c = base.CreateParams;
            c.ExStyle |= Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return c;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    public void Place(Rectangle r)
    {
        if (!Visible) { Bounds = r; Show(); }
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }
}

class BlackoutWindow : FloatingWindow
{
    public readonly Display Display;
    public bool Dark;

    public BlackoutWindow(Display display)
    {
        Display = display;
        BackColor = Color.Black;
        Text = "Monitor Dimmer blackout";
    }

    public void SetDark(bool dark)
    {
        if (dark == Dark) return;
        Dark = dark;
        if (dark) Place(Display.Bounds); else Hide();
    }
}

// Big number shown on each real monitor while choosing, so it is obvious which is which.
class BadgeWindow : FloatingWindow
{
    readonly Display display;
    readonly Func<bool> isSelected;
    readonly Font numberFont, labelFont;

    public BadgeWindow(Display display, Func<bool> isSelected, Action clicked)
    {
        this.display = display;
        this.isSelected = isSelected;
        float s = Native.ScaleAt(new Point(display.Bounds.Left + 1, display.Bounds.Top + 1));
        numberFont = new Font("Segoe UI", 96 * s, FontStyle.Bold, GraphicsUnit.Pixel);
        labelFont = new Font("Segoe UI", 20 * s, FontStyle.Bold, GraphicsUnit.Pixel);
        int size = (int)(190 * s), margin = (int)(40 * s);
        BackColor = Color.Black;
        Text = "Monitor Dimmer badge";
        Cursor = Cursors.Hand;
        Click += delegate { clicked(); };
        Place(new Rectangle(display.Bounds.Left + margin, display.Bounds.Top + margin, size, size));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PickerForm.PaintMonitor(e.Graphics, ClientRectangle, display.Number, isSelected(), numberFont, labelFont);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { numberFont.Dispose(); labelFont.Dispose(); }
        base.Dispose(disposing);
    }
}

// Clickable miniature of the real monitor arrangement.
class MonitorMap : Control
{
    readonly List<Display> displays;
    readonly Func<Display, bool> isSelected;
    readonly Action<Display> toggle;
    Font numberFont, labelFont;

    public MonitorMap(List<Display> displays, Func<Display, bool> isSelected, Action<Display> toggle)
    {
        this.displays = displays;
        this.isSelected = isSelected;
        this.toggle = toggle;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
    }

    Rectangle MapRect(Display d)
    {
        Rectangle all = displays[0].Bounds;
        foreach (var o in displays) all = Rectangle.Union(all, o.Bounds);
        float k = Math.Min((float)Width / all.Width, (float)Height / all.Height);
        float ox = (Width - all.Width * k) / 2, oy = (Height - all.Height * k) / 2;
        int gap = 3;
        return Rectangle.FromLTRB(
            (int)(ox + (d.Bounds.Left - all.Left) * k) + gap,
            (int)(oy + (d.Bounds.Top - all.Top) * k) + gap,
            (int)(ox + (d.Bounds.Right - all.Left) * k) - gap,
            (int)(oy + (d.Bounds.Bottom - all.Top) * k) - gap);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        foreach (var d in displays)
            if (MapRect(d).Contains(e.Location)) { toggle(d); return; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (numberFont == null)
        {
            float h = Height;
            numberFont = new Font("Segoe UI", h * 0.17f, FontStyle.Bold, GraphicsUnit.Pixel);
            labelFont = new Font("Segoe UI", h * 0.06f, FontStyle.Bold, GraphicsUnit.Pixel);
        }
        foreach (var d in displays)
            PickerForm.PaintMonitor(e.Graphics, MapRect(d), d.Number, isSelected(d), numberFont, labelFont);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && numberFont != null) { numberFont.Dispose(); labelFont.Dispose(); }
        base.Dispose(disposing);
    }
}

class PickerForm : Form
{
    static readonly Color Surface = Color.FromArgb(32, 32, 32);
    static readonly Color Muted = Color.FromArgb(190, 190, 190);
    static readonly Color Accent = Color.FromArgb(0, 120, 212);

    readonly List<Display> displays;
    readonly HashSet<string> selected;
    readonly MonitorMap map;
    readonly CheckBox follow;
    readonly Button goDark;
    readonly float scale;
    bool followBeforeAll;

    public event Action Changed;
    public event Action Confirmed;
    public bool Follow { get { return follow.Checked; } }

    public static void PaintMonitor(Graphics g, Rectangle r, int number, bool dark, Font numberFont, Font labelFont)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        Color fill = dark ? Color.Black : Color.FromArgb(235, 235, 235);
        Color ink = dark ? Color.White : Color.Black;
        using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
        using (var p = new Pen(dark ? Color.FromArgb(120, 120, 120) : Accent, 3) { Alignment = PenAlignment.Inset })
            g.DrawRectangle(p, r);
        using (var b = new SolidBrush(ink))
        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            int labelHeight = (int)(labelFont.Size * 2.2f);
            var top = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom - labelHeight);
            var bottom = Rectangle.FromLTRB(r.Left, r.Bottom - labelHeight - 4, r.Right, r.Bottom - 4);
            g.DrawString(number.ToString(), numberFont, b, top, center);
            g.DrawString(dark ? "DARK" : "ON", labelFont, b, bottom, center);
        }
    }

    public PickerForm(List<Display> displays, HashSet<string> selected, bool followMouse, bool hotkeyOk, Point cursor)
    {
        this.displays = displays;
        this.selected = selected;
        scale = Native.ScaleAt(cursor);

        Text = "Monitor Dimmer";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        TopMost = true;
        KeyPreview = true;
        BackColor = Surface;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", S(15), GraphicsUnit.Pixel);
        Icon = App.AppIcon;
        ClientSize = new Size(S(540), S(470));

        var screen = Screen.FromPoint(cursor).WorkingArea;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);

        Controls.Add(new Label
        {
            Text = "Which monitors should go dark?",
            Font = new Font("Segoe UI Semibold", S(22), GraphicsUnit.Pixel),
            Bounds = new Rectangle(S(24), S(16), S(492), S(34)),
        });
        Controls.Add(new Label
        {
            Text = "Press 1, 2 or 3, or click a monitor.",
            ForeColor = Muted,
            Bounds = new Rectangle(S(24), S(52), S(492), S(24)),
        });

        map = new MonitorMap(displays, d => selected.Contains(d.Id), Toggle)
        {
            Bounds = new Rectangle(S(24), S(84), S(492), S(220)),
        };
        Controls.Add(map);

        follow = new CheckBox
        {
            Text = "Light up a dark monitor while the mouse is on it",
            Checked = followMouse,
            Bounds = new Rectangle(S(24), S(314), S(492), S(28)),
        };
        Controls.Add(follow);

        Controls.Add(new Label
        {
            Text = (hotkeyOk ? "To get this window back: press Ctrl+Alt+D, double-click a\r\nblack screen, or use the tray icon."
                             : "To get this window back: double-click a black screen\r\nor use the tray icon."),
            ForeColor = Muted,
            Bounds = new Rectangle(S(24), S(346), S(492), S(48)),
        });

        goDark = MakeButton("Go dark", new Rectangle(S(286), S(410), S(110), S(38)));
        goDark.BackColor = Accent;
        goDark.Click += delegate { if (Confirmed != null) Confirmed(); };
        AcceptButton = goDark;

        var quit = MakeButton("Quit", new Rectangle(S(406), S(410), S(110), S(38)));
        quit.BackColor = Color.FromArgb(64, 64, 64);
        quit.Click += delegate { Close(); };
        CancelButton = quit;

        UpdateState();
    }

    int S(int v) { return (int)Math.Round(v * scale); }

    Button MakeButton(string text, Rectangle bounds)
    {
        var b = new Button { Text = text, Bounds = bounds, FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
        b.FlatAppearance.BorderSize = 0;
        Controls.Add(b);
        return b;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        try { Native.DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); }
        catch (DllNotFoundException) { }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int n = 0;
        if (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9) n = e.KeyCode - Keys.D0;
        else if (e.KeyCode >= Keys.NumPad1 && e.KeyCode <= Keys.NumPad9) n = e.KeyCode - Keys.NumPad0;
        var d = displays.FirstOrDefault(x => x.Number == n);
        if (d != null) { Toggle(d); e.Handled = true; }
        base.OnKeyDown(e);
    }

    public void Toggle(Display d)
    {
        if (!selected.Remove(d.Id)) selected.Add(d.Id);
        UpdateState();
        if (Changed != null) Changed();
    }

    void UpdateState()
    {
        goDark.Enabled = selected.Count > 0;
        goDark.BackColor = goDark.Enabled ? Accent : Color.FromArgb(64, 64, 64);

        // With every monitor dark, the one under the mouse must stay lit or nothing is usable.
        bool all = selected.Count == displays.Count;
        if (all && follow.Enabled)
        {
            followBeforeAll = follow.Checked;
            follow.Checked = true;
            follow.Enabled = false;
        }
        else if (!all && !follow.Enabled)
        {
            follow.Checked = followBeforeAll;
            follow.Enabled = true;
        }
        map.Invalidate();
    }
}

class HotkeyWindow : NativeWindow, IDisposable
{
    readonly Action pressed;
    public readonly bool Registered;

    public HotkeyWindow(Action pressed)
    {
        this.pressed = pressed;
        CreateHandle(new CreateParams());
        Registered = Native.RegisterHotKey(Handle, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.D);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) pressed();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Registered) Native.UnregisterHotKey(Handle, 1);
        DestroyHandle();
    }
}

class App : ApplicationContext
{
    public static readonly Icon AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

    readonly EventWaitHandle showSignal;
    readonly HotkeyWindow hotkey;
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer;
    readonly EventHandler displaysChangedHandler;
    readonly HashSet<string> selected = new HashSet<string>();
    readonly List<BadgeWindow> badges = new List<BadgeWindow>();

    public readonly List<BlackoutWindow> Overlays = new List<BlackoutWindow>();
    public PickerForm Picker;
    public bool HotkeyRegistered { get { return hotkey.Registered; } }
    public HashSet<string> Selected { get { return selected; } }
    public bool Follow;

    List<Display> displays;
    bool everChosen, dimming, closingPickerOnPurpose, quitting;
    volatile bool displaysDirty;
    int ticksSinceRefresh;

    public App(EventWaitHandle showSignal)
    {
        this.showSignal = showSignal;
        hotkey = new HotkeyWindow(ShowPicker);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Choose monitors...", null, delegate { ShowPicker(); });
        menu.Items.Add("Quit", null, delegate { Quit(); });
        tray = new NotifyIcon { Icon = AppIcon, Text = "Monitor Dimmer", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += delegate { ShowPicker(); };

        displaysChangedHandler = delegate { displaysDirty = true; };
        SystemEvents.DisplaySettingsChanged += displaysChangedHandler;

        timer = new System.Windows.Forms.Timer { Interval = 30 };
        timer.Tick += OnTick;
        timer.Start();
    }

    static Point CursorNow()
    {
        Native.POINT p;
        return Native.GetCursorPos(out p) ? new Point(p.X, p.Y) : Cursor.Position;
    }

    public void ShowPicker()
    {
        if (quitting) return;
        if (Picker != null) { BringPickerForward(); return; }

        dimming = false;
        ClearOverlays();
        displays = Display.ReadAll();
        Point cursor = CursorNow();

        selected.RemoveWhere(id => !displays.Any(d => d.Id == id));
        if (!everChosen)
        {
            // First run: suggest focusing on the monitor the mouse is on.
            foreach (var d in displays)
                if (!d.Bounds.Contains(cursor)) selected.Add(d.Id);
        }

        Picker = new PickerForm(displays, selected, Follow, hotkey.Registered, cursor);
        Picker.Changed += delegate { foreach (var b in badges) b.Invalidate(); };
        Picker.Confirmed += GoDark;
        Picker.FormClosed += delegate
        {
            if (closingPickerOnPurpose) return;
            Picker = null;
            ClearBadges();
            Quit();
        };
        foreach (var d in displays)
        {
            var display = d;
            badges.Add(new BadgeWindow(display, () => selected.Contains(display.Id), delegate { Picker.Toggle(display); }));
        }
        Picker.Show();
        BringPickerForward();
    }

    void BringPickerForward()
    {
        Picker.Activate();
        Native.SetForegroundWindow(Picker.Handle);
    }

    void ClosePickerQuietly()
    {
        if (Picker == null) return;
        closingPickerOnPurpose = true;
        Picker.Close();
        closingPickerOnPurpose = false;
        Picker.Dispose();
        Picker = null;
        ClearBadges();
    }

    public void GoDark()
    {
        everChosen = true;
        Follow = Picker.Follow;
        ClosePickerQuietly();
        StartDimming();
    }

    public void StartDimming()
    {
        displays = Display.ReadAll();
        selected.RemoveWhere(id => !displays.Any(d => d.Id == id));
        ClearOverlays();
        foreach (var d in displays)
        {
            var o = new BlackoutWindow(d);
            o.DoubleClick += delegate { ShowPicker(); };
            Overlays.Add(o);
        }
        dimming = true;
        UpdateOverlays(CursorNow());
    }

    public void UpdateOverlays(Point cursor)
    {
        foreach (var o in Overlays)
            o.SetDark(selected.Contains(o.Display.Id) && !(Follow && o.Display.Bounds.Contains(cursor)));
    }

    void OnTick(object sender, EventArgs e)
    {
        if (showSignal.WaitOne(0)) ShowPicker();

        if (displaysDirty)
        {
            displaysDirty = false;
            if (Picker != null) { ClosePickerQuietly(); ShowPicker(); }
            else if (dimming) StartDimming();
        }

        if (!dimming) return;
        Native.POINT p;
        if (Native.GetCursorPos(out p)) UpdateOverlays(new Point(p.X, p.Y));

        // Stay above anything else that went topmost, such as the taskbar.
        if (++ticksSinceRefresh >= 33)
        {
            ticksSinceRefresh = 0;
            foreach (var o in Overlays)
                if (o.Dark) o.Place(o.Display.Bounds);
        }
    }

    void ClearOverlays()
    {
        foreach (var o in Overlays) { o.Close(); o.Dispose(); }
        Overlays.Clear();
    }

    void ClearBadges()
    {
        foreach (var b in badges) { b.Close(); b.Dispose(); }
        badges.Clear();
    }

    public void Quit()
    {
        if (quitting) return;
        quitting = true;
        timer.Stop();
        SystemEvents.DisplaySettingsChanged -= displaysChangedHandler;
        ClosePickerQuietly();
        ClearBadges();
        ClearOverlays();
        hotkey.Dispose();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch (EntryPointNotFoundException) { }

        bool first;
        using (var mutex = new Mutex(true, "MonitorDimmer.SingleInstance.v1", out first))
        using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "MonitorDimmer.ShowPicker.v1"))
        {
            // Opening the program again while it runs brings back the chooser.
            if (!first) { signal.Set(); return; }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var app = new App(signal);
#if SELFTEST
            SelfTest.Run(app, args[0]);
            return;
#else
            app.ShowPicker();
            Application.Run(app);
#endif
        }
    }
}

#if SELFTEST
static class SelfTest
{
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);

    static StringBuilder log = new StringBuilder();
    static int failures;

    static void Check(string name, bool ok)
    {
        log.AppendLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    static void Pump(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    static Point Center(Rectangle r) { return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2); }

    static bool RectMatches(BlackoutWindow o)
    {
        Native.RECT r;
        Native.GetWindowRect(o.Handle, out r);
        var b = o.Display.Bounds;
        return r.Left == b.Left && r.Top == b.Top && r.Right == b.Right && r.Bottom == b.Bottom;
    }

    public static void Run(App app, string outDir)
    {
        try
        {
            var displays = Display.ReadAll();
            foreach (var d in displays) log.AppendLine("monitor " + d.Number + " = " + d.Id + " " + d.Bounds);
            Check("hotkey registered", app.HotkeyRegistered);

            // Chooser, drawn to a file.
            app.ShowPicker();
            Pump(300);
            using (var bmp = new Bitmap(app.Picker.Width, app.Picker.Height))
            {
                app.Picker.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
                bmp.Save(Path.Combine(outDir, "picker.png"));
            }
            bool before = app.Selected.Contains(displays[0].Id);
            app.Picker.Toggle(displays[0]);
            Check("toggle flips a monitor", app.Selected.Contains(displays[0].Id) != before);
            app.Picker.Toggle(displays[0]);

            // Fixed mode for real: the monitors the mouse is not on go solid black.
            Native.POINT p; Native.GetCursorPos(out p);
            var cursor = new Point(p.X, p.Y);
            app.Selected.Clear();
            foreach (var d in displays)
                if (!d.Bounds.Contains(cursor)) app.Selected.Add(d.Id);
            app.GoDark();
            Pump(300);
            foreach (var o in app.Overlays)
            {
                bool want = app.Selected.Contains(o.Display.Id);
                string n = "fixed mode monitor " + o.Display.Number;
                Check(n + (want ? " dark" : " left on"), Native.IsWindowVisible(o.Handle) == want);
                if (!want) continue;
                Check(n + " covers exact bounds", RectMatches(o));
                using (var px = new Bitmap(8, 8))
                using (var g = Graphics.FromImage(px))
                {
                    g.CopyFromScreen(Center(o.Display.Bounds), Point.Empty, px.Size);
                    Check(n + " pixels are black", px.GetPixel(4, 4).ToArgb() == Color.Black.ToArgb());
                }
            }

            // Hotkey brings the chooser back and restores the screens.
            keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x44, 0, 0, UIntPtr.Zero); keybd_event(0x44, 0, 2, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
            Pump(400);
            Check("Ctrl+Alt+D reopens chooser", app.Picker != null);
            Check("Ctrl+Alt+D clears overlays", app.Overlays.Count == 0);

            // All monitors chosen in the window, checkbox untouched: the mouse's monitor stays lit.
            Check("checkbox off before choosing all", !app.Picker.Follow);
            foreach (var d in displays)
                if (!app.Selected.Contains(d.Id)) app.Picker.Toggle(d);
            Check("choosing all turns on follow", app.Picker.Follow);
            var last = displays[displays.Count - 1];
            app.Picker.Toggle(last);
            Check("unchoosing one restores checkbox", !app.Picker.Follow);
            app.Picker.Toggle(last);
            app.GoDark();
            Pump(300);
            foreach (var o in app.Overlays)
            {
                bool want = !o.Display.Bounds.Contains(cursor);
                Check("all dark: monitor " + o.Display.Number + (want ? " dark" : " lit under mouse"),
                    Native.IsWindowVisible(o.Handle) == want);
            }
            app.ShowPicker();
            Pump(100);

            // Follow mode on all monitors, nearly transparent because the mouse position is simulated.
            foreach (var d in displays) app.Selected.Add(d.Id);
            app.GoDark();
            app.Follow = true;
            app.StartDimming();
            foreach (var o in app.Overlays) o.Opacity = 0.02;
            foreach (var mouseOn in displays)
            {
                Application.DoEvents();
                app.UpdateOverlays(Center(mouseOn.Bounds));
                foreach (var o in app.Overlays)
                {
                    bool want = o.Display.Id != mouseOn.Id;
                    Check("follow: mouse on " + mouseOn.Number + ", monitor " + o.Display.Number + (want ? " dark" : " lit"),
                        Native.IsWindowVisible(o.Handle) == want && o.Dark == want);
                }
            }

            // Double-clicking a black screen brings the chooser back.
            app.Follow = false;
            app.UpdateOverlays(Point.Empty);
            Pump(100);
            var h = app.Overlays[0].Handle;
            var mid = (IntPtr)((app.Overlays[0].Height / 2 << 16) | (app.Overlays[0].Width / 2));
            SendMessage(h, 0x201, (IntPtr)1, mid); SendMessage(h, 0x202, IntPtr.Zero, mid);
            SendMessage(h, 0x203, (IntPtr)1, mid); SendMessage(h, 0x202, IntPtr.Zero, mid);
            Pump(100);
            Check("double-click reopens chooser", app.Picker != null);
        }
        catch (Exception ex)
        {
            Check("no exception: " + ex, false);
        }
        app.Quit();
        log.AppendLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        File.WriteAllText(Path.Combine(outDir, "selftest.txt"), log.ToString());
    }
}
#endif
