// Pil gostergesi arayuzu: gorev cubugunda tek halka simgesi + uzerine gelince acilan ayrinti paneli.
//   Yeni cihaz eklemek: Slot olustur, slots listesine ekle, Refresh icinde oku.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

enum Kind { Headset, Mouse, Buds, Gamepad, Keyboard, Other }

class Slot
{
    public string Id, Name;
    public Kind Kind;
    public string RealName;  // as the device reports it, before the user's own name/icon
    public Kind RealKind;
    public int ColorIndex;   // which theme colour this device uses
    public Color Hue { get { return Themes.Current.Hue(ColorIndex); } }
    public Reading Last = new Reading();
    public DateTime LastOk = DateTime.MinValue;
    public int LastWarn = 101;
    public bool Active;
}

// Preset colour themes for the panel and the rings
class Theme
{
    public string Name;
    public Color Swatch;
    public bool System;          // follow Windows light/dark and accent colour
    public bool IsLight;
    public Color Bg, Fg, Accent;
    public Color[] Hues;
    public Color Hue(int i) { return Hues[((i % Hues.Length) + Hues.Length) % Hues.Length]; }
}

static class Themes
{
    static Color C(int r, int g, int b) { return Color.FromArgb(255, r, g, b); }
    public static readonly Theme[] All = {
        new Theme { Name = "Default", System = true, Swatch = C(128, 128, 128),
            Hues = new[] { C(40, 200, 255), C(175, 120, 255), C(255, 140, 50), C(70, 215, 130), C(255, 95, 160), C(90, 140, 255), C(200, 220, 70) } },
        new Theme { Name = "Sakura", IsLight = true, Swatch = C(244, 143, 177),
            Bg = C(255, 241, 246), Fg = C(74, 36, 54), Accent = C(232, 93, 141),
            Hues = new[] { C(236, 98, 150), C(178, 110, 210), C(255, 150, 120), C(120, 190, 150), C(110, 150, 230), C(240, 180, 60) } },
        new Theme { Name = "Aqua", Swatch = C(0, 200, 210),
            Bg = C(12, 34, 42), Fg = C(222, 248, 250), Accent = C(0, 200, 205),
            Hues = new[] { C(0, 220, 230), C(90, 160, 255), C(120, 240, 190), C(255, 200, 90), C(190, 140, 255), C(255, 120, 150) } },
        new Theme { Name = "Midnight", Swatch = C(120, 100, 255),
            Bg = C(17, 17, 32), Fg = C(232, 232, 255), Accent = C(126, 106, 255),
            Hues = new[] { C(140, 120, 255), C(90, 175, 255), C(225, 120, 255), C(110, 230, 200), C(255, 150, 110), C(240, 220, 120) } },
        new Theme { Name = "Forest", Swatch = C(96, 180, 110),
            Bg = C(20, 31, 23), Fg = C(226, 242, 226), Accent = C(92, 186, 110),
            Hues = new[] { C(110, 210, 120), C(200, 220, 90), C(90, 190, 170), C(240, 170, 80), C(160, 140, 230), C(240, 120, 110) } },
        new Theme { Name = "Sunset", Swatch = C(255, 120, 70),
            Bg = C(38, 22, 22), Fg = C(255, 236, 226), Accent = C(255, 118, 72),
            Hues = new[] { C(255, 140, 60), C(255, 90, 110), C(255, 205, 90), C(190, 120, 255), C(90, 200, 220), C(150, 220, 120) } },
    };
    public static Theme Current
    {
        get { string n = Settings.Theme; foreach (var t in All) if (t.Name == n) return t; return All[0]; }
    }
}

static class Look
{
    public static float K = 1f;
    public static bool TaskbarLight;
    public static bool Panel;    // true while the panel is being drawn: colours come from the theme
    public static bool Light
    {
        get { var t = Themes.Current; return Panel && !t.System ? t.IsLight : TaskbarLight; }
        set { TaskbarLight = value; }
    }
    public static Color Fg
    {
        get
        {
            var t = Themes.Current;
            if (Panel && !t.System) return t.Fg;
            return Light ? Color.FromArgb(255, 25, 25, 25) : Color.White;
        }
    }
    public static Color PanelBg
    {
        get
        {
            var t = Themes.Current;
            if (!t.System) return t.Bg;
            return Light ? Color.FromArgb(255, 243, 243, 243) : Color.FromArgb(255, 36, 36, 36);
        }
    }
    public static readonly Color ChargeC = Color.FromArgb(255, 255, 204, 0);
    public static readonly Color LowC = Color.FromArgb(255, 255, 80, 70);
    public static Color A(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B); }

    static readonly string IconFace =
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "SegoeIcons.ttf"))
        ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static void Refresh()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                Light = k != null && (int)k.GetValue("SystemUsesLightTheme", 0) == 1;
        }
        catch { Light = false; }
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) K = g.DpiX / 96f;
    }

    public static Color BarColor(Slot s, int pct, bool chg)
    {
        if (chg) return ChargeC;
        if (pct >= 0 && pct <= 15) return LowC;
        return s.Hue;
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.1f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Bar(Graphics g, RectangleF r, int pct, Color c, bool chg, float phase)
    {
        using (var path = Round(r, r.Height / 2))
        using (var br = new SolidBrush(A(Fg, Light ? 45 : 55)))
            g.FillPath(br, path);
        if (pct < 0) return;
        float w = Math.Max(r.Height, r.Width * Math.Min(100, pct) / 100f);
        var fr = new RectangleF(r.X, r.Y, w, r.Height);
        using (var path = Round(fr, r.Height / 2))
        {
            using (var br = new SolidBrush(c)) g.FillPath(br, path);
            if (chg && pct < 100)
            {
                // sarjda cubugun uzerinde kayan parlak isik
                float sw = Math.Max(8 * K, r.Width * 0.35f);
                float x = r.X - sw + (r.Width + sw) * (phase % 1f);
                var sr = new RectangleF(x, r.Y, sw, r.Height);
                var old = g.Clip;
                g.SetClip(path, CombineMode.Intersect);
                using (var lg = new LinearGradientBrush(new RectangleF(sr.X - 1, sr.Y, sr.Width + 2, sr.Height), Color.Transparent, Color.Transparent, 0f))
                {
                    var cb = new ColorBlend(3);
                    cb.Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(200, 255, 255, 240), Color.FromArgb(0, 255, 255, 255) };
                    cb.Positions = new[] { 0f, 0.5f, 1f };
                    lg.InterpolationColors = cb;
                    g.FillRectangle(lg, sr);
                }
                g.Clip = old;
            }
        }
    }

    // Cihaz simgesi: Fluent ikon fontu; kablosuz kulaklik (font'ta yok) elle cizilir
    public static void Glyph(Graphics g, Kind kind, RectangleF box, Color c)
    {
        if (kind == Kind.Buds) { Buds(g, box, c); return; }
        string ch = kind == Kind.Headset ? "\uE7F6" : kind == Kind.Mouse ? "\uE962" : kind == Kind.Gamepad ? "\uE7FC"
                  : kind == Kind.Keyboard ? "\uE765" : "\uE702";
        using (var fam = new FontFamily(IconFace))
        using (var path = new GraphicsPath())
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            path.AddString(ch, fam, (int)FontStyle.Regular, box.Height, box, sf);
            using (var br = new SolidBrush(c)) g.FillPath(br, path);
            using (var pen = new Pen(c, 0.35f * K) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
        }
    }

    static void Buds(Graphics g, RectangleF b, Color c)
    {
        float s = Math.Min(b.Width, b.Height) * 0.86f;
        float x0 = b.X + (b.Width - s) / 2, y0 = b.Y + (b.Height - s) / 2;
        float lw = Math.Max(1.2f * K, s * 0.085f);
        using (var pen = new Pen(c, lw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            foreach (float cx in new[] { 0.28f, 0.72f })
            {
                float r = s * 0.19f;
                float hx = x0 + s * cx, hy = y0 + s * 0.30f;
                g.DrawEllipse(pen, hx - r, hy - r, r * 2, r * 2);
                float sx = hx + (cx < 0.5f ? r * 0.55f : -r * 0.55f);
                using (var stem = new Pen(c, lw * 1.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(stem, sx, hy + r * 0.7f, sx, y0 + s * 0.92f);
            }
        }
    }

    // Kucuk sarj simsegi (simge koseye)
    public static void Bolt(Graphics g, PointF at, float h)
    {
        float w = h * 0.62f;
        var pts = new[] {
            new PointF(at.X + w * 0.62f, at.Y), new PointF(at.X + w * 0.05f, at.Y + h * 0.58f),
            new PointF(at.X + w * 0.45f, at.Y + h * 0.58f), new PointF(at.X + w * 0.32f, at.Y + h),
            new PointF(at.X + w * 0.98f, at.Y + h * 0.38f), new PointF(at.X + w * 0.55f, at.Y + h * 0.38f) };
        using (var pen = new Pen(Light ? Color.White : Color.FromArgb(255, 20, 20, 20), 1.6f * K) { LineJoin = LineJoin.Round })
            g.DrawPolygon(pen, pts);
        using (var br = new SolidBrush(ChargeC)) g.FillPolygon(br, pts);
    }

    public static Color Accent
    {
        get
        {
            if (!Themes.Current.System) return Themes.Current.Accent;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = k == null ? null : k.GetValue("AccentColor");
                    if (v is int) { int c = (int)v; return Color.FromArgb(255, c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF); }
                }
            }
            catch { }
            return Color.FromArgb(255, 76, 160, 255);
        }
    }

    // A single Fluent icon glyph centred in a box
    public static void Icon(Graphics g, string glyph, RectangleF box, float size, Color c)
    {
        using (var fam = new FontFamily(IconFace))
        using (var path = new GraphicsPath())
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            path.AddString(glyph, fam, (int)FontStyle.Regular, size, box, sf);
            using (var br = new SolidBrush(c)) g.FillPath(br, path);
        }
    }

    public static string Pct(int p) { return p < 0 ? "–" : p + "%"; }
}

// Per-pixel alfa ile cizilen pencere (UpdateLayeredWindow)
class LayeredWindow : Form
{
    readonly bool asChild;
    public LayeredWindow(bool child)
    {
        asChild = child;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; // LAYERED | TOOLWINDOW | NOACTIVATE
            if (!asChild) cp.ExStyle |= 0x8;             // TOPMOST
            cp.Style = unchecked((int)0x80000000);        // POPUP (serit sonra cocuk pencereye cevrilir)
            return cp;
        }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE -> MA_NOACTIVATE
        base.WndProc(ref m);
    }

    public static string DumpDir; // /render: cizimleri dosyaya da yaz
    public void Show(Bitmap bmp)
    {
        if (DumpDir != null) try { bmp.Save(Path.Combine(DumpDir, (asChild ? "strip" : "panel") + ".png")); } catch { }
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        IntPtr hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var bl = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
            UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref bl, 2);
        }
        finally
        {
            SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, IntPtr pptDst, ref SIZE size, IntPtr src, ref POINT ptSrc, int key, ref BLEND bl, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
}

// Gorev cubugunda tek simge: ic ice halkalar (dis: kulaklik, orta: mouse, ic: kablosuz kulaklik).
// Simgenin uzerine gelince ya da tiklayinca ayrinti paneli acilir.
class BatteryUI : ApplicationContext
{
    readonly Barracuda dHead = new Barracuda();
    readonly AtkMouse dMouse = new AtkMouse();
    readonly CmfBuds dBuds = new CmfBuds();
    readonly Slot head = new Slot { Id = "razer-barracuda-x", Name = "Razer Barracuda X", Kind = Kind.Headset, ColorIndex = 0 };
    readonly Slot mouse = new Slot { Id = "atk-mouse", Name = "Wireless mouse", Kind = Kind.Mouse, ColorIndex = 1 };
    readonly Slot buds = new Slot { Id = "cmf-buds", Name = "CMF Buds Pro 2", Kind = Kind.Buds, ColorIndex = 2 };
    readonly List<Slot> slots;
    readonly HashSet<string> hidden = LoadHidden();
    int paletteNext;
    List<Found> generic = new List<Found>();
    DateTime genericAt = DateTime.MinValue;

    readonly NotifyIcon icon = new NotifyIcon();
    IntPtr iconHandle;
    LayeredWindow panel;
    readonly Control invoker = new Control();
    readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer hoverDelay = new System.Windows.Forms.Timer();
    DateTime hoverStart;
    const int HoverOpenMs = 550;
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    int busy;
    float phase;
    bool pinned;
    DateTime lastInside, lastIconMove;
    const int GraceSeconds = 20;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = Program.AppName;

    public BatteryUI()
    {
        slots = new List<Slot> { head, mouse, buds };
        Look.Refresh();
        invoker.CreateControl();

        menu.Items.Add("Settings", null, delegate { ShowSettings(); });
        menu.Items.Add("Refresh", null, delegate { genericAt = DateTime.MinValue; Refresh(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, delegate { icon.Visible = false; ExitThread(); });
        icon.ContextMenuStrip = menu;
        icon.MouseMove += delegate
        {
            lastIconMove = DateTime.Now;
            if (panel == null || !panel.Visible)
            {
                if (Settings.HoverOpen && !hoverDelay.Enabled) { hoverStart = DateTime.Now; hoverDelay.Start(); }
            }
            else lastInside = DateTime.Now;
        };
        icon.MouseClick += (o, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            hoverDelay.Stop();
            if (panel != null && panel.Visible && pinned) HidePanel(); else ShowPanel(true);
        };

        dHead.Changed += Kick;
        dBuds.Changed += Kick;

        poll.Interval = 3000;
        poll.Tick += delegate { Refresh(); };
        poll.Start();
        anim.Interval = 60;
        anim.Tick += delegate { phase += 0.02f; if (phase > 1000) phase = 0; DrawIcon(); if (panel != null && panel.Visible) DrawPanel(); };
        watch.Interval = 100;
        watch.Tick += delegate { WatchPanel(); };
        // Open on hover only when the mouse rests on the icon (~0.55 s), not when it just passes over it
        hoverDelay.Interval = 100;
        hoverDelay.Tick += delegate
        {
            RECT ir;
            bool over = IconRect(out ir) ? ir.Contains(Cursor.Position) : (DateTime.Now - lastIconMove).TotalMilliseconds < 200;
            if (!over || (panel != null && panel.Visible)) { hoverDelay.Stop(); return; }
            if ((DateTime.Now - hoverStart).TotalMilliseconds >= HoverOpenMs) { hoverDelay.Stop(); ShowPanel(false); }
        };

        SystemEvents.UserPreferenceChanged += OnPref;
        Diagnostics.Shown = delegate
        {
            var l = new List<string>();
            foreach (var x in slots) if (x.Active) l.Add(x.RealKind + "  " + (x.RealName ?? x.Name) + "  " + x.Last.Percent + "%" + (x.Last.Charging ? " charging" : ""));
            return l;
        };
        Refresh();

        // memory: once a minute while the panel is closed, give unused memory back to Windows
        trim.Interval = 60000;
        trim.Tick += delegate { if (panel == null || !panel.Visible) TrimMemory(); };
        trim.Start();
        var first = new System.Windows.Forms.Timer { Interval = 15000 };
        first.Tick += delegate { first.Stop(); first.Dispose(); TrimMemory(); };
        first.Start();

        // updates: first check shortly after start, then every 6 hours
        updates.Interval = 30000;
        updates.Tick += delegate
        {
            updates.Interval = 6 * 3600 * 1000;
            if (Settings.AutoUpdate && Program.IsInstalled) Updater.Check(false, PanelChanged);
        };
        if (LayeredWindow.DumpDir == null) updates.Start();
        if (Program.JustUpdated)
        {
            var t1 = new System.Windows.Forms.Timer { Interval = 3000 };
            t1.Tick += delegate
            {
                t1.Stop(); t1.Dispose();
                if (icon.Visible) icon.ShowBalloonTip(5000, "BatteryRings updated", "Now running v" + Updater.Short(Updater.Current), ToolTipIcon.Info);
            };
            t1.Start();
        }
        if (LayeredWindow.DumpDir != null)
        {
            var t = new System.Windows.Forms.Timer { Interval = 7000 };
            t.Tick += delegate
            {
                t.Stop(); phase = 0.5f;
                string dd = LayeredWindow.DumpDir;
                ShowPanel(true);
                try { File.Copy(Path.Combine(dd, "panel.png"), Path.Combine(dd, "panel_main.png"), true); } catch { }
                ShowSettings();
                try { File.Copy(Path.Combine(dd, "panel.png"), Path.Combine(dd, "panel_settings.png"), true); } catch { }
                try
                {
                    using (var dlg = new DeviceEditor(head))
                    {
                        dlg.StartPosition = FormStartPosition.Manual; dlg.Location = new Point(-2000, -2000);
                        dlg.Show(); dlg.Refresh();
                        using (var bm = new Bitmap(dlg.Width, dlg.Height))
                        {
                            dlg.DrawToBitmap(bm, new Rectangle(0, 0, dlg.Width, dlg.Height));
                            bm.Save(Path.Combine(dd, "editor.png"));
                        }
                    }
                }
                catch { }
                try
                {
                    using (var dlg = new ReportDialog())
                    {
                        dlg.StartPosition = FormStartPosition.Manual; dlg.Location = new Point(-2000, -2000);
                        dlg.Show(); dlg.Refresh();
                        using (var bm = new Bitmap(dlg.Width, dlg.Height))
                        {
                            dlg.DrawToBitmap(bm, new Rectangle(0, 0, dlg.Width, dlg.Height));
                            bm.Save(Path.Combine(dd, "report.png"));
                        }
                    }
                }
                catch { }
                using (var b = RingIcon(64)) b.Save(Path.Combine(LayeredWindow.DumpDir, "icon64.png"));
                using (var b = RingIcon(16)) b.Save(Path.Combine(LayeredWindow.DumpDir, "icon16.png"));
                icon.Visible = false; ExitThread();
            };
            t.Start();
        }
    }

    readonly System.Windows.Forms.Timer updates = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer trim = new System.Windows.Forms.Timer();

    static void TrimMemory()
    {
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
    }
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);
    string reportStatus = "";

    // Settings > Report a problem: user describes it, we attach the device report and open a pre-filled GitHub issue.
    // Nothing is sent by the app itself; the user sees the text on GitHub and submits it.
    void ReportProblem()
    {
        HidePanel();
        using (var dlg = new ReportDialog())
        {
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                string text = dlg.Description;
                bool attach = dlg.IncludeReport;
                reportStatus = "Preparing…";
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string rep = "";
                    if (attach) { try { rep = Diagnostics.Report(); } catch (Exception e) { rep = "report failed: " + e.Message; } }
                    invoker.BeginInvoke((Action)delegate
                    {
                        bool copied;
                        string url = Diagnostics.IssueUrl(text, rep, out copied);
                        try { System.Diagnostics.Process.Start(url); reportStatus = copied ? "Opened GitHub, full report copied" : "Opened GitHub"; }
                        catch { reportStatus = "Could not open the browser"; }
                    });
                });
            }
        }
        ShowSettings();
    }
    void CopyReport()
    {
        reportStatus = "Collecting…";
        DrawPanel();
        ThreadPool.QueueUserWorkItem(delegate
        {
            string text;
            try { text = Diagnostics.Report(); } catch (Exception e) { text = "Report failed: " + e.Message; }
            invoker.BeginInvoke((Action)delegate
            {
                try { Clipboard.SetText(text); reportStatus = "Copied to clipboard"; }
                catch { reportStatus = "Could not copy"; }
                if (panel != null && panel.Visible) DrawPanel();
            });
        });
    }

    void PanelChanged() { try { invoker.BeginInvoke((Action)delegate { if (panel != null && panel.Visible) DrawPanel(); }); } catch { } }

    void Kick() { try { invoker.BeginInvoke((Action)Refresh); } catch { } }
    void OnPref(object s, UserPreferenceChangedEventArgs e) { try { invoker.BeginInvoke((Action)delegate { Look.Refresh(); DrawIcon(); }); } catch { } }

    void Refresh()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            Reading h = Safe(dHead.Read), m = Safe(dMouse.Read), b = Safe(dBuds.Read);
            List<Found> gen = null;
            if ((DateTime.Now - genericAt).TotalSeconds >= 9)
            {
                try { gen = GenericDevices.Read(); } catch { gen = new List<Found>(); }
                genericAt = DateTime.Now;
            }
            invoker.BeginInvoke((Action)delegate
            {
                Interlocked.Exchange(ref busy, 0);
                head.Name = "Razer Barracuda X"; head.Kind = Kind.Headset;
                buds.Name = dBuds.DeviceName; buds.Kind = b.Present && b.Percent >= 0 && b.Parts == null ? Kind.Headset : Kind.Buds;
                mouse.Name = dMouse.DeviceName; mouse.Kind = Kind.Mouse;
                Apply(head, h); Apply(mouse, m); Apply(buds, b);
                if (gen != null) generic = gen;
                ApplyGeneric();
                ApplyOverrides();
                DrawIcon();
                if (panel != null && panel.Visible) DrawPanel();
            });
        });
    }

    static Reading Safe(Func<Reading> f) { try { return f(); } catch { return new Reading(); } }

    void Apply(Slot d, Reading r)
    {
        if (r.Present && r.Percent >= 0) { d.Last = r; d.LastOk = DateTime.Now; }
        else if (!r.Present) d.LastOk = DateTime.MinValue;
        d.Active = (DateTime.Now - d.LastOk).TotalSeconds <= GraceSeconds;
        if (!d.Active) return;
        if (d.Last.Charging) { d.LastWarn = 101; return; }
        // 25 / 10 / 5: some devices (e.g. ASUS Pugio II) only report 0-25-50-75-100, so 20 would never trigger.
        // Lowest threshold first: starting at 8% gives one alert (10%), not two in a row.
        foreach (int lvl in new[] { 5, 10, 25 })
            if (d.Last.Percent <= lvl && d.LastWarn > lvl)
            {
                d.LastWarn = lvl;
                if (icon.Visible && Settings.Alerts) icon.ShowBalloonTip(5000, d.Name + " battery low", d.Last.Percent + "% remaining", ToolTipIcon.Warning);
                return;
            }
    }

    // Ozel kodu olmayan (Bluetooth / XInput) cihazlar: gelenlere yuva ac, gidenleri kaldir
    void ApplyGeneric()
    {
        var ids = new HashSet<string>();
        foreach (var f in generic)
        {
            ids.Add(f.Id);
            var s = slots.Find(x => x.Id == f.Id);
            if (s == null)
            {
                s = new Slot { Id = f.Id, Kind = f.Kind, ColorIndex = 3 + paletteNext++ };
                slots.Add(s);
            }
            s.Name = f.Name; s.Kind = f.Kind;
            Apply(s, f.Reading);
        }
        foreach (var s in slots.ToArray())
            if (s.Id.Contains(":") && !ids.Contains(s.Id))
            {
                Apply(s, new Reading { Present = false });
                if (!s.Active) slots.Remove(s);
            }
    }

    // User's own name / icon for a device (Settings > pencil)
    void ApplyOverrides()
    {
        foreach (var s in slots)
        {
            s.RealName = s.Name; s.RealKind = s.Kind;
            string n; Kind k;
            if (DeviceOverrides.Get(s.Id, out n, out k)) { if (!string.IsNullOrEmpty(n)) s.Name = n; s.Kind = k; }
        }
    }

    void EditDevice(Slot s)
    {
        HidePanel();
        using (var dlg = new DeviceEditor(s))
        {
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                if (dlg.Reset) DeviceOverrides.Clear(s.Id);
                else DeviceOverrides.Set(s.Id, dlg.NameText == s.RealName ? "" : dlg.NameText, dlg.Chosen);
                s.Name = s.RealName; s.Kind = s.RealKind;
                ApplyOverrides();
                DrawIcon();
            }
        }
        ShowSettings();
    }

    // Gorunen (gizlenmemis) aktif cihazlar
    List<Slot> ActiveSlots() { return Sorted(slots.FindAll(s => s.Active && !hidden.Contains(s.Id))); }

    // ---- user order (Settings > drag): first device = outer ring, top of the panel ----
    List<string> order = LoadOrder();

    List<Slot> Sorted(List<Slot> list)
    {
        var res = new List<Slot>(list);
        var pos = new Dictionary<Slot, int>();
        for (int i = 0; i < list.Count; i++)
        {
            int o = order.IndexOf(list[i].Id);
            pos[list[i]] = o >= 0 ? o : 10000 + slots.IndexOf(list[i]);
        }
        res.Sort((a, b) => pos[a].CompareTo(pos[b]));
        return res;
    }

    void MoveDevice(Slot s, int target, List<Slot> shown)
    {
        var ids = new List<string>();
        foreach (var x in shown) if (x != s) ids.Add(x.Id);
        ids.Insert(Math.Max(0, Math.Min(target, ids.Count)), s.Id);
        foreach (var id in order) if (!ids.Contains(id)) ids.Add(id); // keep devices that are not connected now
        order = ids;
        try { using (var k = Registry.CurrentUser.CreateSubKey(StateKey)) k.SetValue("Order", order.ToArray(), RegistryValueKind.MultiString); } catch { }
        DrawIcon();
    }

    static List<string> LoadOrder()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\BatteryRings"))
                if (k != null) { var v = k.GetValue("Order") as string[]; if (v != null) return new List<string>(v); }
        }
        catch { }
        return new List<string>();
    }

    // drag state
    Slot dragSlot;
    float dragStartY, dragY;
    bool dragging, suppressClick;
    readonly List<KeyValuePair<Slot, RectangleF>> devRows = new List<KeyValuePair<Slot, RectangleF>>();

    const string StateKey = @"Software\BatteryRings";
    static HashSet<string> LoadHidden()
    {
        var h = new HashSet<string>();
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(StateKey))
                if (k != null) foreach (var x in (k.GetValue("Hidden") as string[]) ?? new string[0]) h.Add(x);
        }
        catch { }
        return h;
    }
    static void SaveHidden(HashSet<string> h)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(StateKey))
                k.SetValue("Hidden", new List<string>(h).ToArray(), RegistryValueKind.MultiString);
        }
        catch { }
    }

    // ---------------- Simge ----------------

    void DrawIcon()
    {
        var act = ActiveSlots();
        // the icon always stays in the taskbar; with no devices it is an empty ring and the panel says so
        {
            int S = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = RingIcon(S))
            {
                IntPtr h = bmp.GetHicon();
                icon.Icon = Icon.FromHandle(h);
                if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
                iconHandle = h;
            }
            icon.Text = ""; // no tooltip: it would cover the panel
        }
        if (!icon.Visible) icon.Visible = true;

        bool a = false;
        foreach (var s in act)
        {
            if (s.Last.Charging && s.Last.Percent < 100) a = true;
            if (s.Last.Parts != null) foreach (var p in s.Last.Parts) if (p.Charging && p.Percent >= 0 && p.Percent < 100) a = true;
        }
        if (a && !anim.Enabled) anim.Start();
        if (!a && anim.Enabled) anim.Stop();
    }

    // Ic ice halkalar; tek cihazda halka + ortada cihaz simgesi
    Bitmap RingIcon(int S)
    {
        var act = ActiveSlots();
        if (act.Count > 3) act = act.GetRange(0, 3);
        float k = S / 16f;
        var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float c = S / 2f;
            if (act.Count == 0)
            {
                // empty ring: no device found
                float sw = 1.9f * k, r1 = c - sw / 2f - 0.15f * k;
                using (var pen = new Pen(Look.A(Look.Fg, 110), sw)) g.DrawEllipse(pen, c - r1, c - r1, r1 * 2, r1 * 2);
                using (var br = new SolidBrush(Look.A(Look.Fg, 150))) g.FillEllipse(br, c - 1.3f * k, c - 1.3f * k, 2.6f * k, 2.6f * k);
            }
            else if (act.Count == 1)
            {
                float sw = 1.9f * k, r1 = c - sw / 2f - 0.15f * k;
                Ring(g, new RectangleF(c - r1, c - r1, r1 * 2, r1 * 2), sw, act[0], k);
                float gs = 8.4f * k;
                Look.Glyph(g, act[0].Kind, new RectangleF(c - gs / 2, c - gs / 2, gs, gs), Look.Fg);
            }
            else
            {
                float sw = act.Count == 2 ? 2.1f * k : 1.75f * k;
                float gap = act.Count == 2 ? 1.1f * k : 0.7f * k;
                float r = c - sw / 2f - 0.1f * k;
                foreach (var s in act)
                {
                    Ring(g, new RectangleF(c - r, c - r, r * 2, r * 2), sw, s, k);
                    r -= sw + gap;
                }
                if (act.Count == 2)
                    using (var br = new SolidBrush(Look.A(Look.Fg, 230)))
                        g.FillEllipse(br, c - 1.1f * k, c - 1.1f * k, 2.2f * k, 2.2f * k);
            }
        }
        return bmp;
    }

    void Ring(Graphics g, RectangleF rc, float sw, Slot s, float k)
    {
        var r = s.Last;
        int p = Math.Max(0, Math.Min(100, r.Percent));
        bool chg = r.Charging, low = !chg && p <= 15;
        Color c = chg ? Look.ChargeC : low ? Look.LowC : s.Hue;
        int alpha = low ? (int)(150 + 105 * (0.5 + 0.5 * Math.Sin(phase * 20))) : 255;
        using (var pen = new Pen(Look.A(c, 60), sw)) g.DrawEllipse(pen, rc);
        float sweep = 360f * p / 100f;
        using (var pen = new Pen(Look.A(c, alpha), sw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            if (sweep >= 359.5f) g.DrawEllipse(pen, rc);
            else if (sweep > 0.5f) g.DrawArc(pen, rc, -90, Math.Max(sweep, 10));
        }
        if (chg && p < 100)
        {
            float headAng = (phase * 360f * 2.5f) % 360f - 90f;
            for (int i = 0; i < 8; i++)
                using (var pen = new Pen(Color.FromArgb((int)(230 * (1 - i / 8f)), 255, 255, 235), sw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, rc, headAng - i * 9f, 8f);
        }
    }

    // ---------------- Panel ----------------
    // Two views: device list, and settings (gear button). Clickable areas are collected while drawing.

    class Hit { public RectangleF R; public Action A; }
    readonly List<Hit> hits = new List<Hit>();
    int hot = -1;
    bool settingsView;

    void ShowPanel(bool pin)
    {
        pinned = pin || pinned;
        if (panel == null || panel.IsDisposed)
        {
            panel = new LayeredWindow(false);
            panel.MouseDown += (o, e) =>
            {
                if (e.Button != MouseButtons.Left || !settingsView) return;
                float k = Look.K;
                foreach (var kv in devRows)
                    if (kv.Value.Contains(e.Location) && e.X < kv.Value.Right - 86 * k) { dragSlot = kv.Key; dragStartY = e.Y; }
            };
            panel.MouseUp += (o, e) =>
            {
                if (dragging)
                {
                    var shown = Sorted(slots.FindAll(x => x.Active));
                    MoveDevice(dragSlot, DropIndex(shown), shown);
                    suppressClick = true;
                }
                dragging = false; dragSlot = null;
                panel.Cursor = Cursors.Default;
                if (panel.Visible) DrawPanel();
            };
            panel.MouseMove += (o, e) =>
            {
                lastInside = DateTime.Now;
                if (dragSlot != null && (Control.MouseButtons & MouseButtons.Left) != 0)
                {
                    if (!dragging && Math.Abs(e.Y - dragStartY) > 4 * Look.K) dragging = true;
                    if (dragging) { dragY = e.Y; hot = -1; panel.Cursor = Cursors.SizeNS; DrawPanel(); return; }
                }
                int h = hits.FindIndex(x => x.R.Contains(e.Location));
                panel.Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
                if (h != hot) { hot = h; DrawPanel(); }
            };
            panel.MouseLeave += delegate { if (hot != -1) { hot = -1; DrawPanel(); } };
            panel.MouseClick += (o, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                pinned = true;
                if (suppressClick) { suppressClick = false; return; } // end of a drag, not a click
                var h = hits.Find(x => x.R.Contains(e.Location));
                if (h != null) { h.A(); hot = -1; if (panel.Visible) DrawPanel(); }
            };
        }
        lastInside = DateTime.Now;
        DrawPanel();
        if (!panel.Visible) panel.Visible = true;
        SetWindowPos(panel.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        watch.Start();
    }

    void ShowSettings() { settingsView = true; ShowPanel(true); }

    void HidePanel()
    {
        pinned = false;
        settingsView = false;
        hot = -1;
        watch.Stop();
        if (panel != null && panel.Visible)
        {
            panel.Visible = false;
            var t = new System.Windows.Forms.Timer { Interval = 2000 };
            t.Tick += delegate { t.Stop(); t.Dispose(); if (panel == null || !panel.Visible) TrimMemory(); };
            t.Start();
        }
    }

    void WatchPanel()
    {
        if (panel == null || !panel.Visible) { watch.Stop(); return; }
        if (dragSlot != null) { lastInside = DateTime.Now; return; } // a drag may leave the panel; never close mid-drag
        var c = Cursor.Position;
        RECT b, ir;
        bool overPanel = GetWindowRect(panel.Handle, out b) && b.Contains(c);
        bool overIcon = (DateTime.Now - lastIconMove).TotalMilliseconds < 150 || (IconRect(out ir) && ir.Contains(c));
        bool inside = overIcon || overPanel;
        if (inside) lastInside = DateTime.Now;
        // auto-hiding taskbar slid away: the panel would float on its own, so close it once the mouse is not on it
        if (TaskbarHidden() && !overPanel && (DateTime.Now - lastInside).TotalMilliseconds > 250) { HidePanel(); return; }
        if (pinned) { if (!inside && Control.MouseButtons != MouseButtons.None) HidePanel(); }
        else if ((DateTime.Now - lastInside).TotalMilliseconds > 400) HidePanel();
    }

    // Is the (auto-hiding) taskbar currently slid off the screen?
    static bool TaskbarHidden()
    {
        RECT t;
        IntPtr h = FindWindow("Shell_TrayWnd", null);
        if (h == IntPtr.Zero || !GetWindowRect(h, out t)) return false;
        var scr = Screen.FromPoint(new Point((t.l + t.r) / 2, (t.t + t.b) / 2)).Bounds;
        int w = Math.Min(t.r, scr.Right) - Math.Max(t.l, scr.Left);
        int hgt = Math.Min(t.b, scr.Bottom) - Math.Max(t.t, scr.Top);
        return w < 8 || hgt < 8;
    }

    // Icon position for placing the panel. While the taskbar is hidden the icon is off screen,
    // so use where it was last seen; the panel then stays put instead of jumping.
    RECT lastIcon; bool haveLastIcon;
    bool AnchorRect(out RECT r)
    {
        if (IconRect(out r) && !TaskbarHidden()) { lastIcon = r; haveLastIcon = true; return true; }
        r = lastIcon;
        return haveLastIcon;
    }

    // Where the tray icon is on screen (Shell_NotifyIconGetRect)
    bool IconRect(out RECT r)
    {
        r = new RECT();
        try
        {
            var t = typeof(NotifyIcon);
            var win = (NativeWindow)t.GetField("window", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(icon);
            int id = (int)t.GetField("id", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(icon);
            var nid = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)), hWnd = win.Handle, uID = id };
            return Shell_NotifyIconGetRect(ref nid, out r) == 0 && r.r > r.l;
        }
        catch { return false; }
    }

    static bool Multi(Slot s) { return s.Last.Parts != null && s.Last.Parts.Count > 0; }

    const float PW = 300, PPad = 16, HeadH = 46, RowH = 52, MultiRowH = 80, SetRowH = 40, ThemeRowH = 40;

    void DrawPanel()
    {
        if (panel == null) return;
        float k = Look.K;
        var devs = settingsView ? Sorted(slots.FindAll(s => s.Active)) : ActiveSlots();
        bool empty = !settingsView && devs.Count == 0;

        float hh = HeadH;
        if (settingsView) hh += SetRowH * 4 + 30 + ThemeRowH + 30 + Math.Max(1, devs.Count) * SetRowH + 56;
        else if (empty) hh += 64;
        else foreach (var s in devs) hh += Multi(s) ? MultiRowH : RowH;
        hh += 8;
        int W = (int)(PW * k), H = (int)(hh * k);
        hits.Clear();

        using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
        {
            Look.Panel = true;
            try
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // opaque background + ClearType: much crisper text than greyscale on a translucent layer
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    bool light = Look.Light;
                    using (var p = Look.Round(new RectangleF(0.5f, 0.5f, W - 1, H - 1), 8 * k))
                    {
                        using (var br = new SolidBrush(Look.PanelBg)) g.FillPath(br, p);
                        using (var pen = new Pen(light ? Color.FromArgb(34, 0, 0, 0) : Color.FromArgb(36, 255, 255, 255), 1f)) g.DrawPath(pen, p);
                    }
                    if (settingsView) DrawSettings(g, W, devs); else DrawDevices(g, W, devs);
                }
            }
            finally { Look.Panel = false; }

            // above the tray icon, centred; fall back to the cursor
            RECT ir;
            Point anchor;
            int top;
            bool below = false;
            if (AnchorRect(out ir))
            {
                anchor = new Point((ir.l + ir.r) / 2, ir.t); top = ir.t;
                // taskbar at the top of the screen: open the panel below the icon
                var sb = Screen.FromPoint(anchor).Bounds;
                if (ir.t < sb.Top + sb.Height / 2) { below = true; top = ir.b; }
            }
            else if (panel.Visible) { anchor = new Point(panel.Left + panel.Width / 2, panel.Bottom + 12); top = panel.Bottom + 12; }
            else { anchor = Cursor.Position; top = Cursor.Position.Y - 12; }
            var scr = Screen.FromPoint(anchor).Bounds;
            int px = Math.Max(scr.Left + 8, Math.Min(anchor.X - W / 2, scr.Right - W - 8));
            int py = below ? Math.Min(scr.Bottom - H - 8, top + (int)(12 * k)) : Math.Max(scr.Top + 8, top - H - (int)(12 * k));
            panel.Location = new Point(px, py);
            panel.Show(bmp);
        }
    }

    // Header: title on the left, one round icon button on the right (or left for "back")
    void Header(Graphics g, float W, string title, string glyph, bool left, Action a)
    {
        float k = Look.K, L = PPad * k;
        var btn = left ? new RectangleF(L - 6 * k, 8 * k, 30 * k, 30 * k) : new RectangleF(W - L - 24 * k, 8 * k, 30 * k, 30 * k);
        int idx = hits.Count;
        hits.Add(new Hit { R = btn, A = a });
        if (hot == idx)
            using (var p = Look.Round(btn, 5 * k))
            using (var br = new SolidBrush(Look.A(Look.Fg, Look.Light ? 18 : 22))) g.FillPath(br, p);
        Look.Icon(g, glyph, btn, 14 * k, Look.A(Look.Fg, 220));
        float tx = left ? btn.Right + 4 * k : L;
        using (var f = new Font("Segoe UI Semibold", 14f * k, GraphicsUnit.Pixel))
        using (var br = new SolidBrush(Look.Fg))
            g.DrawString(title, f, br, tx, 13 * k, StringFormat.GenericTypographic);
    }

    void DrawDevices(Graphics g, float W, List<Slot> devs)
    {
        float k = Look.K;
        Color fg = Look.Fg, sub = Look.A(fg, Look.Light ? 175 : 180);
        var sf = StringFormat.GenericTypographic;
        Header(g, W, "Battery", "", false, ShowSettings);

        using (var fName = new Font("Segoe UI", 14f * k, GraphicsUnit.Pixel))
        using (var fPct = new Font("Segoe UI Semibold", 14f * k, GraphicsUnit.Pixel))
        using (var fSmall = new Font("Segoe UI", 12.5f * k, GraphicsUnit.Pixel))
        {
            float L = PPad * k, R = W - PPad * k, y = HeadH * k;
            float gs = 16 * k, tx = L + gs + 12 * k;
            if (devs.Count == 0)
            {
                using (var br = new SolidBrush(fg)) g.DrawString("No devices found", fName, br, L, y + 6 * k, sf);
                using (var br = new SolidBrush(sub)) g.DrawString("Turn on or connect a wireless device", fSmall, br, L, y + 30 * k, sf);
            }
            foreach (var s in devs)
            {
                var r = s.Last;
                float ty = y + 4 * k;
                Look.Glyph(g, s.Kind, new RectangleF(L, ty + 1 * k, gs, gs), Look.A(fg, 235));
                string name = s.Name;
                float maxName = R - tx - 70 * k;
                while (name.Length > 4 && g.MeasureString(name, fName, PointF.Empty, sf).Width > maxName) name = name.Substring(0, name.Length - 2);
                if (name != s.Name) name = name.TrimEnd() + "…";
                using (var br = new SolidBrush(fg)) g.DrawString(name, fName, br, tx, ty, sf);

                if (!Multi(s))
                {
                    PctRight(g, fPct, R, ty, r.Percent, r.Charging, fg, sub, sf, r.Text);
                    Look.Bar(g, new RectangleF(tx, ty + 24 * k, R - tx, 4 * k), r.Percent, Look.BarColor(s, r.Percent, r.Charging), r.Charging, phase);
                    y += RowH * k;
                }
                else
                {
                    var parts = r.Parts;
                    float colW = (R - tx - 2 * 14 * k) / 3f;
                    for (int i = 0; i < 3; i++)
                    {
                        Part p = i < parts.Count ? parts[i] : new Part { Name = i == 0 ? "Left" : i == 1 ? "Right" : "Case" };
                        float cx = tx + i * (colW + 14 * k), cy = ty + 25 * k;
                        using (var br = new SolidBrush(sub)) g.DrawString(p.Name, fSmall, br, cx, cy, sf);
                        PctRight(g, fPct, cx + colW, cy - 1 * k, p.Percent, p.Charging, fg, sub, sf, null);
                        Look.Bar(g, new RectangleF(cx, cy + 21 * k, colW, 4 * k), p.Percent, Look.BarColor(s, p.Percent, p.Charging), p.Charging, phase);
                    }
                    y += MultiRowH * k;
                }
            }
        }
    }

    void DrawSettings(Graphics g, float W, List<Slot> devs)
    {
        float k = Look.K;
        Color fg = Look.Fg, sub = Look.A(fg, Look.Light ? 175 : 180);
        var sf = StringFormat.GenericTypographic;
        Header(g, W, "Settings", "", true, delegate { settingsView = false; });

        using (var f = new Font("Segoe UI", 14f * k, GraphicsUnit.Pixel))
        using (var fSmall = new Font("Segoe UI Semibold", 11.5f * k, GraphicsUnit.Pixel))
        {
            float L = PPad * k, R = W - PPad * k, y = HeadH * k;
            Toggle(g, f, L, R, ref y, null, "Start with Windows", IsAutoStart(), delegate { SetAutoStart(!IsAutoStart()); });
            Toggle(g, f, L, R, ref y, null, "Open when hovering the icon", Settings.HoverOpen, delegate { Settings.HoverOpen = !Settings.HoverOpen; });
            Toggle(g, f, L, R, ref y, null, "Low battery alerts", Settings.Alerts, delegate { Settings.Alerts = !Settings.Alerts; });
            Toggle(g, f, L, R, ref y, null, "Install updates automatically", Settings.AutoUpdate, delegate { Settings.AutoUpdate = !Settings.AutoUpdate; });

            y += 10 * k;
            using (var br = new SolidBrush(sub))
            {
                g.DrawString("THEME", fSmall, br, L, y, sf);
                string tn = Themes.Current.Name;
                g.DrawString(tn, fSmall, br, R - MeasureW(g, fSmall, tn), y, sf);
            }
            y += 20 * k;
            ThemePicker(g, f, L, R, ref y);

            y += 10 * k;
            using (var br = new SolidBrush(sub))
            {
                g.DrawString("DEVICES", fSmall, br, L, y, sf);
                string hint = "drag to reorder";
                using (var fh = new Font("Segoe UI", 11.5f * k, GraphicsUnit.Pixel))
                using (var bh = new SolidBrush(Look.A(fg, Look.Light ? 120 : 110)))
                    g.DrawString(hint, fh, bh, R - MeasureW(g, fh, hint), y - 1 * k, sf);
            }
            y += 20 * k;
            if (devs.Count == 0)
            {
                using (var br = new SolidBrush(sub)) g.DrawString("No devices connected", f, br, L, y + 10 * k, sf);
                y += SetRowH * k;
            }
            devRows.Clear();
            foreach (var s in devs)
            {
                var sl = s;
                devRows.Add(new KeyValuePair<Slot, RectangleF>(s, new RectangleF(L - 8 * k, y, R - L + 16 * k, SetRowH * k)));
                if (dragging && s == dragSlot)
                    using (var p = Look.Round(new RectangleF(L - 8 * k, y, R - L + 16 * k, SetRowH * k), 5 * k))
                    {
                        using (var br = new SolidBrush(Look.A(Look.Accent, 40))) g.FillPath(br, p);
                        using (var pen = new Pen(Look.Accent, 1.2f * k)) g.DrawPath(pen, p);
                    }
                Toggle(g, f, L, R, ref y, s, s.Name, !hidden.Contains(s.Id), delegate
                {
                    if (hidden.Contains(sl.Id)) hidden.Remove(sl.Id); else hidden.Add(sl.Id);
                    SaveHidden(hidden);
                    DrawIcon();
                });
            }
            if (dragging && devRows.Count > 1)
            {
                // where the dragged device will land
                var others = devRows.FindAll(dr => dr.Key != dragSlot);
                int ti = DropIndex(devs);
                float ly = ti < others.Count ? others[ti].Value.Top : others[others.Count - 1].Value.Bottom;
                using (var pen = new Pen(Look.Accent, 2.5f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, L - 4 * k, ly, R + 4 * k, ly);
            }

            // footer: credit / status on the left, small icon buttons on the right
            y += 6 * k;
            using (var pen = new Pen(Look.A(fg, Look.Light ? 22 : 24), 1f)) g.DrawLine(pen, L, y, R, y);
            y += 10 * k;
            float bx = R + 4 * k;
            footerLabel = null;
            bx = IconButton(g, bx, y, "", "Quit", delegate { icon.Visible = false; ExitThread(); });
            bx = IconButton(g, bx, y, "", "Report a problem", ReportProblem);
            bx = IconButton(g, bx, y, "", "Check for updates", delegate { Updater.Check(true, PanelChanged); });
            string ver = "v" + Application.ProductVersion.Substring(0, Application.ProductVersion.LastIndexOf('.'));
            // status messages stay for ~10 s (longer while something is in progress), then the credit returns
            if (Updater.Status != lastUpd) { lastUpd = Updater.Status; lastUpdAt = DateTime.Now; }
            if (reportStatus != lastRep) { lastRep = reportStatus; lastRepAt = DateTime.Now; }
            Func<string, DateTime, bool> fresh = (st, at) => st.Length > 0 && (st.EndsWith("…") || (DateTime.Now - at).TotalSeconds < 10);
            string left = footerLabel                                   // name of the hovered button
                ?? (fresh(reportStatus, lastRepAt) ? reportStatus : null)
                ?? (fresh(Updater.Status, lastUpdAt) ? Updater.Status : null)
                ?? "by blaidq  ·  " + ver;
            using (var fs = new Font("Segoe UI", 12.5f * k, GraphicsUnit.Pixel))
            {
                float maxW = bx - L - 6 * k;
                string t = left;
                while (t.Length > 4 && MeasureW(g, fs, t) > maxW) t = t.Substring(0, t.Length - 2);
                if (t != left) t = t.TrimEnd() + "…";
                using (var br = new SolidBrush(footerLabel != null ? fg : sub)) g.DrawString(t, fs, br, L, y + 7 * k, sf);
            }
        }
    }

    string footerLabel;
    string lastUpd = "", lastRep = "";
    DateTime lastUpdAt, lastRepAt;

    // Small square icon button, laid out right to left; returns the left edge for the next one
    float IconButton(Graphics g, float right, float y, string glyph, string label, Action a)
    {
        float k = Look.K;
        var r = new RectangleF(right - 30 * k, y, 30 * k, 30 * k);
        int idx = hits.Count;
        hits.Add(new Hit { R = r, A = a });
        bool h = hot == idx;
        if (h)
        {
            footerLabel = label;
            using (var p = Look.Round(r, 5 * k))
            using (var br = new SolidBrush(Look.A(Look.Fg, Look.Light ? 18 : 24))) g.FillPath(br, p);
        }
        Look.Icon(g, glyph, r, 14 * k, Look.A(Look.Fg, h ? 235 : 160));
        return r.X - 2 * k;
    }

    // Row of colour swatches; the chosen one gets a ring, its name sits underneath
    void ThemePicker(Graphics g, Font f, float L, float R, ref float y)
    {
        float k = Look.K, d = 26 * k;
        int n = Themes.All.Length;
        float gap = (R - L - n * d) / (n - 1);
        var cur = Themes.Current;
        for (int i = 0; i < n; i++)
        {
            var t = Themes.All[i];
            var cell = new RectangleF(L + i * (d + gap), y + 2 * k, d, d);
            int idx = hits.Count;
            hits.Add(new Hit { R = RectangleF.Inflate(cell, 4 * k, 4 * k), A = delegate { Settings.Theme = t.Name; DrawIcon(); } });
            var inner = RectangleF.Inflate(cell, -4 * k, -4 * k);
            if (t.System)
            {
                // same look as the others: dark disc with a dot; the dot is half light / half dark = "follow Windows"
                using (var br = new SolidBrush(Color.FromArgb(255, 36, 36, 36))) g.FillEllipse(br, inner);
                var dot = RectangleF.Inflate(inner, -inner.Width * 0.22f, -inner.Height * 0.22f);
                using (var br = new SolidBrush(Color.FromArgb(255, 245, 245, 245))) g.FillPie(br, dot.X, dot.Y, dot.Width, dot.Height, 90, 180);
                using (var br = new SolidBrush(Color.FromArgb(255, 90, 90, 90))) g.FillPie(br, dot.X, dot.Y, dot.Width, dot.Height, 270, 180);
            }
            else
            {
                using (var br = new SolidBrush(t.Bg)) g.FillEllipse(br, inner);
                var dot = RectangleF.Inflate(inner, -inner.Width * 0.22f, -inner.Height * 0.22f);
                using (var br = new SolidBrush(t.Swatch)) g.FillEllipse(br, dot);
            }
            using (var pen = new Pen(Look.A(Look.Fg, 60), 1f)) g.DrawEllipse(pen, inner);
            if (t == cur)
                using (var pen = new Pen(Look.Accent, 2f * k)) g.DrawEllipse(pen, RectangleF.Inflate(cell, -1 * k, -1 * k));
            else if (hot == idx)
                using (var pen = new Pen(Look.A(Look.Fg, 90), 1.5f * k)) g.DrawEllipse(pen, RectangleF.Inflate(cell, -1 * k, -1 * k));
        }
        y += ThemeRowH * k;
    }

    // One settings row: [optional device glyph] label ........ [switch]
    // position among the other devices where the dragged one would be dropped
    int DropIndex(List<Slot> shown)
    {
        int i = 0;
        foreach (var kv in devRows)
            if (kv.Key != dragSlot && kv.Value.Top + kv.Value.Height / 2 < dragY) i++;
        return i;
    }

    void Toggle(Graphics g, Font f, float L, float R, ref float y, Slot dev, string label, bool on, Action a)
    {
        float k = Look.K;
        var row = new RectangleF(L - 8 * k, y, R - L + 16 * k, SetRowH * k);
        int penHit = -1;
        RectangleF pr = RectangleF.Empty;
        if (dev != null)
        {
            // pencil: rename / change icon (added first so it wins over the row's click area)
            pr = new RectangleF(R - 40 * k - 36 * k, y + 7 * k, 26 * k, 26 * k);
            penHit = hits.Count;
            var dv = dev;
            hits.Add(new Hit { R = pr, A = delegate { EditDevice(dv); } });
        }
        // only the switch itself toggles; clicking the name does nothing (and starts a drag for devices)
        var swHit = new RectangleF(R - 40 * k - 6 * k, y + 4 * k, 52 * k, SetRowH * k - 8 * k);
        int idx = hits.Count;
        hits.Add(new Hit { R = swHit, A = a });
        if (hot == idx || (penHit >= 0 && hot == penHit))
            using (var p = Look.Round(row, 5 * k))
            using (var br = new SolidBrush(Look.A(Look.Fg, Look.Light ? 12 : 14))) g.FillPath(br, p);
        if (penHit >= 0)
        {
            if (hot == penHit)
                using (var p = Look.Round(pr, 5 * k))
                using (var br = new SolidBrush(Look.A(Look.Fg, Look.Light ? 20 : 26))) g.FillPath(br, p);
            Look.Icon(g, "\uE70F", pr, 12 * k, Look.A(Look.Fg, hot == penHit || hot == idx ? 230 : 120));
        }

        float tx = L;
        if (dev != null)
        {
            // grip: the row can be dragged up or down
            Look.Icon(g, "\uE700", new RectangleF(L - 4 * k, y + 13 * k, 14 * k, 14 * k), 10 * k, Look.A(Look.Fg, hot == idx ? 170 : 90));
            Look.Glyph(g, dev.Kind, new RectangleF(L + 16 * k, y + 12 * k, 16 * k, 16 * k), Look.A(Look.Fg, 200));
            tx += 44 * k;
        }
        string t = label;
        float maxW = R - tx - (dev != null ? 86 : 52) * k;
        while (t.Length > 4 && g.MeasureString(t, f, PointF.Empty, StringFormat.GenericTypographic).Width > maxW) t = t.Substring(0, t.Length - 2);
        if (t != label) t = t.TrimEnd() + "…";
        using (var br = new SolidBrush(Look.Fg)) g.DrawString(t, f, br, tx, y + 11 * k, StringFormat.GenericTypographic);

        // Windows 11 style switch
        var sw = new RectangleF(R - 40 * k, y + 10 * k, 40 * k, 20 * k);
        using (var p = Look.Round(sw, 10 * k))
        {
            if (on) using (var br = new SolidBrush(Look.Accent)) g.FillPath(br, p);
            else using (var pen = new Pen(Look.A(Look.Fg, 170), 1.2f * k)) g.DrawPath(pen, p);
        }
        float d = (on ? 12 : 10) * k;
        float kx = on ? sw.Right - 4 * k - d : sw.X + 5 * k;
        using (var br = new SolidBrush(on ? (Look.Light ? Color.White : Color.FromArgb(255, 20, 20, 20)) : Look.A(Look.Fg, 200)))
            g.FillEllipse(br, kx, sw.Y + (sw.Height - d) / 2, d, d);
        y += SetRowH * k;
    }

    static float MeasureW(Graphics g, Font f, string t) { return g.MeasureString(t, f, PointF.Empty, StringFormat.GenericTypographic).Width; }

    // Text button aligned to the right edge; returns the left edge for the next one
    float Link(Graphics g, Font f, float right, float y, string text, Color c, Action a)
    {
        float k = Look.K;
        float w = g.MeasureString(text, f, PointF.Empty, StringFormat.GenericTypographic).Width;
        var r = new RectangleF(right - w - 16 * k, y - 2 * k, w + 16 * k, 26 * k);
        int idx = hits.Count;
        hits.Add(new Hit { R = r, A = a });
        if (hot == idx)
            using (var p = Look.Round(r, 5 * k))
            using (var br = new SolidBrush(Look.A(Look.Fg, Look.Light ? 14 : 18))) g.FillPath(br, p);
        using (var br = new SolidBrush(c)) g.DrawString(text, f, br, r.X + 8 * k, y + 4 * k, StringFormat.GenericTypographic);
        return r.X;
    }

    // Right-aligned percentage; small bolt when charging
    static void PctRight(Graphics g, Font f, float right, float y, int pct, bool chg, Color fg, Color sub, StringFormat sf, string text)
    {
        float k = Look.K;
        string t = text ?? Look.Pct(pct);
        float w = g.MeasureString(t, f, PointF.Empty, sf).Width;
        bool low = !chg && pct >= 0 && pct <= 15;
        using (var br = new SolidBrush(pct < 0 ? sub : chg ? Look.ChargeC : low ? Look.LowC : fg)) g.DrawString(t, f, br, right - w, y, sf);
        if (chg && pct >= 0) Look.Bolt(g, new PointF(right - w - 10 * k, y + 2 * k), 11 * k);
    }

    // ---------------- Yardimcilar ----------------

    static bool IsAutoStart()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(RunName) != null;
    }

    static void SetAutoStart(bool on)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(RunName, false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnPref;
            icon.Dispose();
            if (panel != null) panel.Dispose();
            dHead.Dispose(); dMouse.Dispose(); dBuds.Dispose();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int l, t, r, b;
        public bool Contains(Point p) { return p.X >= l && p.X < r && p.Y >= t && p.Y < b; }
    }
    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONIDENTIFIER { public int cbSize; public IntPtr hWnd; public int uID; public Guid guidItem; }
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT r);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, int f);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}

// Small per-user settings in HKCU\Software\BatteryRings
static class Settings
{
    const string Key = @"Software\BatteryRings";
    static bool Get(string name, bool def)
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) { object v = k == null ? null : k.GetValue(name); return v is int ? (int)v != 0 : def; } }
        catch { return def; }
    }
    static void Set(string name, bool v)
    {
        try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, v ? 1 : 0, RegistryValueKind.DWord); } catch { }
    }
    public static bool HoverOpen { get { return Get("HoverOpen", true); } set { Set("HoverOpen", value); } }
    public static bool Alerts { get { return Get("Alerts", true); } set { Set("Alerts", value); } }
    public static bool AutoUpdate { get { return Get("AutoUpdate", true); } set { Set("AutoUpdate", value); } }
    static string theme; // cached: read many times per frame
    public static string Theme
    {
        get
        {
            if (theme == null)
                try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) theme = (k == null ? null : k.GetValue("Theme") as string) ?? "Default"; }
                catch { theme = "Default"; }
            return theme;
        }
        set { theme = value; try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("Theme", value); } catch { } }
    }
}

// Saved per-device name / icon chosen by the user: HKCU\Software\BatteryRings\Devices\<id>
static class DeviceOverrides
{
    const string Root = @"Software\BatteryRings\Devices";
    static string Key(string id) { return Root + "\\" + id.Replace("\\", "_"); }

    public static bool Get(string id, out string name, out Kind kind)
    {
        name = null; kind = Kind.Other;
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key(id)))
            {
                if (k == null) return false;
                name = k.GetValue("Name") as string;
                object v = k.GetValue("Kind");
                if (!(v is int) || !Enum.IsDefined(typeof(Kind), (int)v)) return false;
                kind = (Kind)(int)v;
                return true;
            }
        }
        catch { return false; }
    }

    public static void Set(string id, string name, Kind kind)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key(id)))
            {
                k.SetValue("Name", name ?? "");
                k.SetValue("Kind", (int)kind, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    public static void Clear(string id) { try { Registry.CurrentUser.DeleteSubKeyTree(Key(id), false); } catch { } }
}

// Small dialog: rename a device and pick its icon
class DeviceEditor : Form
{
    public string NameText { get { return box.Text.Trim(); } }
    public Kind Chosen;
    public bool Reset;
    readonly TextBox box = new TextBox();
    static readonly Kind[] Kinds = { Kind.Headset, Kind.Buds, Kind.Mouse, Kind.Keyboard, Kind.Gamepad, Kind.Other };
    static readonly string[] KindNames = { "Headset", "Earbuds", "Mouse", "Keyboard", "Controller", "Other" };
    readonly Slot slot;
    Color bg, fg;
    int hover = -1;

    public DeviceEditor(Slot s)
    {
        slot = s;
        Chosen = s.Kind;
        Look.Panel = true;
        try { bg = Look.PanelBg; fg = Look.Fg; } finally { Look.Panel = false; }
        float k = Look.K;
        Text = "Edit device";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = bg; ForeColor = fg;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size((int)(360 * k), (int)(232 * k));
        DoubleBuffered = true;

        var lbl = new Label { Text = "Name", AutoSize = true, Location = new Point((int)(16 * k), (int)(14 * k)), ForeColor = Look.A(fg, 190) };
        box.Text = s.Name;
        box.Location = new Point((int)(16 * k), (int)(36 * k));
        box.Width = (int)(328 * k);
        box.MaxLength = 40;
        box.BackColor = Blend(bg, fg, 0.08f); box.ForeColor = fg; box.BorderStyle = BorderStyle.FixedSingle;
        var lbl2 = new Label { Text = "Icon", AutoSize = true, Location = new Point((int)(16 * k), (int)(74 * k)), ForeColor = Look.A(fg, 190) };

        var save = Btn("Save", (int)(256 * k), true);
        save.Click += delegate { DialogResult = DialogResult.OK; };
        var cancel = Btn("Cancel", (int)(164 * k), false);
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
        var reset = Btn("Reset", (int)(16 * k), false);
        reset.Click += delegate { Reset = true; DialogResult = DialogResult.OK; };
        AcceptButton = save; CancelButton = cancel;
        Controls.AddRange(new Control[] { lbl, box, lbl2, save, cancel, reset });

        MouseMove += (o, e) => { int h = HitIcon(e.Location); if (h != hover) { hover = h; Invalidate(); } Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; };
        MouseClick += (o, e) => { int h = HitIcon(e.Location); if (h >= 0) { Chosen = Kinds[h]; Invalidate(); } };
    }

    // dark title bar when the panel theme is dark (Windows 10 2004+ / 11)
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = (bg.R + bg.G + bg.B) / 3 < 128 ? 1 : 0;
        try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    Button Btn(string t, int x, bool primary)
    {
        float k = Look.K;
        var b = new Button
        {
            Text = t, FlatStyle = FlatStyle.Flat, Location = new Point(x, (int)(186 * k)), Size = new Size((int)(88 * k), (int)(32 * k)),
            BackColor = primary ? Look.Accent : Blend(bg, fg, 0.10f), ForeColor = primary ? Color.White : fg, UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    static Color Blend(Color a, Color b, float t)
    {
        return Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    RectangleF IconRect(int i)
    {
        float k = Look.K, w = 52 * k, gap = (328 * k - 6 * w) / 5;
        return new RectangleF(16 * k + i * (w + gap), 96 * k, w, 72 * k);
    }

    int HitIcon(Point p) { for (int i = 0; i < Kinds.Length; i++) if (IconRect(i).Contains(p)) return i; return -1; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float k = Look.K;
        using (var f = new Font("Segoe UI", 11f * k, GraphicsUnit.Pixel))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center })
            for (int i = 0; i < Kinds.Length; i++)
            {
                var r = IconRect(i);
                bool sel = Kinds[i] == Chosen;
                if (sel || hover == i)
                    using (var p = Look.Round(r, 6 * k))
                    using (var br = new SolidBrush(sel ? Look.A(Look.Accent, 60) : Look.A(fg, 18))) g.FillPath(br, p);
                if (sel)
                    using (var p = Look.Round(r, 6 * k))
                    using (var pen = new Pen(Look.Accent, 1.5f * k)) g.DrawPath(pen, p);
                Look.Glyph(g, Kinds[i], new RectangleF(r.X + r.Width / 2 - 11 * k, r.Y + 12 * k, 22 * k, 22 * k), fg);
                using (var br = new SolidBrush(Look.A(fg, 200)))
                    g.DrawString(KindNames[i], f, br, new RectangleF(r.X - 4 * k, r.Y + 44 * k, r.Width + 8 * k, 20 * k), sf);
            }
    }
}

// "Report a problem" dialog
class ReportDialog : Form
{
    public string Description { get { return box.Text.Trim(); } }
    public bool IncludeReport { get { return chk.Checked; } }
    readonly TextBox box = new TextBox();
    readonly CheckBox chk = new CheckBox();
    Color bg, fg;

    public ReportDialog()
    {
        Look.Panel = true;
        try { bg = Look.PanelBg; fg = Look.Fg; } finally { Look.Panel = false; }
        float k = Look.K;
        Text = "Report a problem";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = bg; ForeColor = fg;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size((int)(420 * k), (int)(338 * k));

        var lbl = new Label { Text = "What is wrong? (device, what you expected, what happened)", AutoSize = true, Location = new Point((int)(16 * k), (int)(14 * k)), ForeColor = Look.A(fg, 200) };
        box.Multiline = true; box.ScrollBars = ScrollBars.Vertical; box.MaxLength = 2000;
        box.Location = new Point((int)(16 * k), (int)(38 * k)); box.Size = new Size((int)(388 * k), (int)(140 * k));
        box.BackColor = Blend(bg, fg, 0.08f); box.ForeColor = fg; box.BorderStyle = BorderStyle.FixedSingle;
        chk.Text = "Attach device report (device ids, interfaces and product names; no personal data)";
        chk.Checked = true; chk.AutoSize = false; chk.Size = new Size((int)(388 * k), (int)(40 * k));
        chk.Location = new Point((int)(16 * k), (int)(186 * k)); chk.ForeColor = fg;
        var note = new Label
        {
            Text = "This opens a new issue on GitHub with the text filled in. You can read and edit it there before sending (needs a free GitHub account).",
            Location = new Point((int)(16 * k), (int)(228 * k)), Size = new Size((int)(388 * k), (int)(58 * k)), ForeColor = Look.A(fg, 160)
        };
        var ok = Btn("Open on GitHub", (int)(264 * k), (int)(140 * k), true);
        ok.Click += delegate
        {
            if (Description.Length < 3) { box.Focus(); return; }
            DialogResult = DialogResult.OK;
        };
        var cancel = Btn("Cancel", (int)(160 * k), (int)(96 * k), false);
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
        CancelButton = cancel;
        Controls.AddRange(new Control[] { lbl, box, chk, note, ok, cancel });
    }

    Button Btn(string t, int x, int w, bool primary)
    {
        float k = Look.K;
        var b = new Button
        {
            Text = t, FlatStyle = FlatStyle.Flat, Location = new Point(x, (int)(292 * k)), Size = new Size(w, (int)(32 * k)),
            BackColor = primary ? Look.Accent : Blend(bg, fg, 0.10f), ForeColor = primary ? Color.White : fg, UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    static Color Blend(Color a, Color b, float t)
    {
        return Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = (bg.R + bg.G + bg.B) / 3 < 128 ? 1 : 0;
        try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
}
