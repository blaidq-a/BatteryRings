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
    public Color Hue;
    public Reading Last = new Reading();
    public DateTime LastOk = DateTime.MinValue;
    public int LastWarn = 101;
    public bool Active;
}

static class Look
{
    public static float K = 1f;
    public static bool Light;
    public static Color Fg { get { return Light ? Color.FromArgb(255, 25, 25, 25) : Color.White; } }
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
    readonly Slot head = new Slot { Id = "razer-barracuda-x", Name = "Razer Barracuda X", Kind = Kind.Headset, Hue = Color.FromArgb(255, 40, 200, 255) };
    readonly Slot mouse = new Slot { Id = "atk-mouse", Name = "ATK A9 Ultimate", Kind = Kind.Mouse, Hue = Color.FromArgb(255, 175, 120, 255) };
    readonly Slot buds = new Slot { Id = "cmf-buds", Name = "CMF Buds Pro 2", Kind = Kind.Buds, Hue = Color.FromArgb(255, 255, 140, 50) };
    readonly List<Slot> slots;
    readonly HashSet<string> hidden = LoadHidden();
    static readonly Color[] Palette = {
        Color.FromArgb(255, 70, 215, 130), Color.FromArgb(255, 255, 95, 160),
        Color.FromArgb(255, 90, 140, 255), Color.FromArgb(255, 200, 220, 70) };
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
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    ToolStripMenuItem autoStart;
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

        var devMenu = new ToolStripMenuItem("Cihazlar");
        devMenu.DropDownItems.Add("(cihaz yok)");
        menu.Items.Add(devMenu);
        menu.Opening += delegate { BuildDeviceMenu(devMenu); };
        menu.Items.Add("Şimdi yenile", null, delegate { genericAt = DateTime.MinValue; Refresh(); });
        autoStart = new ToolStripMenuItem("Windows ile başlat");
        autoStart.Checked = IsAutoStart();
        autoStart.Click += delegate { SetAutoStart(!autoStart.Checked); autoStart.Checked = IsAutoStart(); };
        menu.Items.Add(autoStart);
        if (Program.IsInstalled) menu.Items.Add("Kaldır", null, delegate { icon.Visible = false; Program.Uninstall(false); icon.Visible = ActiveSlots().Count > 0; });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, delegate { icon.Visible = false; ExitThread(); });
        icon.ContextMenuStrip = menu;
        icon.MouseMove += delegate
        {
            lastIconMove = DateTime.Now;
            if (panel == null || !panel.Visible) { if (!hoverDelay.Enabled) hoverDelay.Start(); }
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
        hoverDelay.Interval = 250;
        hoverDelay.Tick += delegate
        {
            hoverDelay.Stop();
            if ((DateTime.Now - lastIconMove).TotalMilliseconds < 400) ShowPanel(false);
        };

        SystemEvents.UserPreferenceChanged += OnPref;
        Refresh();
        if (LayeredWindow.DumpDir != null)
        {
            var t = new System.Windows.Forms.Timer { Interval = 7000 };
            t.Tick += delegate
            {
                t.Stop(); phase = 0.5f;
                ShowPanel(true);
                using (var b = RingIcon(64)) b.Save(Path.Combine(LayeredWindow.DumpDir, "icon64.png"));
                using (var b = RingIcon(16)) b.Save(Path.Combine(LayeredWindow.DumpDir, "icon16.png"));
                icon.Visible = false; ExitThread();
            };
            t.Start();
        }
    }

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
                buds.Name = dBuds.DeviceName;
                Apply(head, h); Apply(mouse, m); Apply(buds, b);
                if (gen != null) generic = gen;
                ApplyGeneric();
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
        foreach (int lvl in new[] { 20, 10, 5 })
            if (d.Last.Percent <= lvl && d.LastWarn > lvl)
            {
                d.LastWarn = lvl;
                if (icon.Visible) icon.ShowBalloonTip(5000, d.Name + " pili azaldı", "Kalan pil: %" + d.Last.Percent, ToolTipIcon.Warning);
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
                s = new Slot { Id = f.Id, Kind = f.Kind, Hue = Palette[paletteNext++ % Palette.Length] };
                slots.Add(s);
            }
            s.Name = f.Name;
            Apply(s, f.Reading);
        }
        foreach (var s in slots.ToArray())
            if (s.Id.Contains(":") && !ids.Contains(s.Id))
            {
                Apply(s, new Reading { Present = false });
                if (!s.Active) slots.Remove(s);
            }
    }

    // Gorunen (gizlenmemis) aktif cihazlar
    List<Slot> ActiveSlots() { return slots.FindAll(s => s.Active && !hidden.Contains(s.Id)); }

    void BuildDeviceMenu(ToolStripMenuItem m)
    {
        m.DropDownItems.Clear();
        var act = slots.FindAll(s => s.Active);
        if (act.Count == 0) { m.DropDownItems.Add(new ToolStripMenuItem("(bağlı cihaz yok)") { Enabled = false }); return; }
        foreach (var s in act)
        {
            var sl = s;
            var it = new ToolStripMenuItem(s.Name) { Checked = !hidden.Contains(s.Id), CheckOnClick = true };
            it.Click += delegate
            {
                if (it.Checked) hidden.Remove(sl.Id); else hidden.Add(sl.Id);
                SaveHidden(hidden);
                DrawIcon();
                if (panel != null && panel.Visible) DrawPanel();
            };
            m.DropDownItems.Add(it);
        }
        m.DropDownItems.Add(new ToolStripSeparator());
        m.DropDownItems.Add(new ToolStripMenuItem("Simgede ilk 3 cihaz halka olarak görünür") { Enabled = false });
    }

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
        bool any = act.Count > 0;
        if (any)
        {
            int S = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = RingIcon(S))
            {
                IntPtr h = bmp.GetHicon();
                icon.Icon = Icon.FromHandle(h);
                if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
                iconHandle = h;
            }
            string tip = "";
            foreach (var s in act) tip += (tip.Length > 0 ? "\n" : "") + Short(s);
            icon.Text = tip.Length > 127 ? tip.Substring(0, 127) : tip;
        }
        else HidePanel();
        if (icon.Visible != any) icon.Visible = any;

        bool a = false;
        foreach (var s in act)
        {
            if (s.Last.Charging && s.Last.Percent < 100) a = true;
            if (s.Last.Parts != null) foreach (var p in s.Last.Parts) if (p.Charging && p.Percent >= 0 && p.Percent < 100) a = true;
        }
        if (a && !anim.Enabled) anim.Start();
        if (!a && anim.Enabled) anim.Stop();
    }

    static string Short(Slot s)
    {
        if (s.Last.Parts != null && s.Last.Parts.Count >= 2)
            return "Buds  S " + Look.Pct(s.Last.Parts[0].Percent) + "  Ğ " + Look.Pct(s.Last.Parts[1].Percent);
        string n = s.Name.Length > 24 ? s.Name.Substring(0, 24) : s.Name;
        return n + " " + (s.Last.Text ?? Look.Pct(s.Last.Percent)) + (s.Last.Charging ? " şarjda" : "");
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
            if (act.Count == 1)
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

    void ShowPanel(bool pin)
    {
        if (ActiveSlots().Count == 0) return;
        pinned = pin || pinned;
        if (panel == null || panel.IsDisposed) panel = new LayeredWindow(false);
        lastInside = DateTime.Now;
        DrawPanel();
        if (!panel.Visible) panel.Visible = true;
        SetWindowPos(panel.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        watch.Start();
    }

    void HidePanel()
    {
        pinned = false;
        watch.Stop();
        if (panel != null && panel.Visible) panel.Visible = false;
    }

    void WatchPanel()
    {
        if (panel == null || !panel.Visible) { watch.Stop(); return; }
        var c = Cursor.Position;
        RECT b, ir;
        bool overIcon = (DateTime.Now - lastIconMove).TotalMilliseconds < 150 || (IconRect(out ir) && ir.Contains(c));
        bool inside = overIcon || (GetWindowRect(panel.Handle, out b) && b.Contains(c));
        if (inside) lastInside = DateTime.Now;
        if (pinned) { if (!inside && Control.MouseButtons != MouseButtons.None) HidePanel(); }
        else if ((DateTime.Now - lastInside).TotalMilliseconds > 400) HidePanel();
    }

    // Simgenin ekrandaki yeri (Shell_NotifyIconGetRect)
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

    const float PW = 272, PPad = 14, RowH = 46, BudsRowH = 74;

    void DrawPanel()
    {
        var act = ActiveSlots();
        if (act.Count == 0) { HidePanel(); return; }
        float k = Look.K;
        float hh = PPad * 2 - 4;
        foreach (var s in act) hh += (Multi(s) ? BudsRowH : RowH);
        int W = (int)(PW * k), H = (int)(hh * k);
        bool light = Look.Light;
        Color fg = Look.Fg, sub = Look.A(fg, light ? 140 : 150);
        using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            using (var fName = new Font("Segoe UI", 13f * k, GraphicsUnit.Pixel))
            using (var fPct = new Font("Segoe UI Semibold", 13f * k, GraphicsUnit.Pixel))
            using (var fSmall = new Font("Segoe UI", 11.5f * k, GraphicsUnit.Pixel))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var sf = StringFormat.GenericTypographic;

                using (var p = Look.Round(new RectangleF(0.5f, 0.5f, W - 1, H - 1), 8 * k))
                {
                    using (var br = new SolidBrush(light ? Color.FromArgb(252, 243, 243, 243) : Color.FromArgb(252, 39, 39, 39))) g.FillPath(br, p);
                    using (var pen = new Pen(light ? Color.FromArgb(28, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255), 1f)) g.DrawPath(pen, p);
                }

                float L = PPad * k, R = W - PPad * k, y = (PPad - 2) * k;
                float gs = 16 * k, tx = L + gs + 12 * k;
                foreach (var s in act)
                {
                    var r = s.Last;
                    float ty = y + 6 * k;
                    Look.Glyph(g, s.Kind, new RectangleF(L, ty + 1 * k, gs, gs), Look.A(fg, 210));
                    using (var br = new SolidBrush(fg)) g.DrawString(s.Name, fName, br, tx, ty, sf);

                    if (!Multi(s))
                    {
                        PctRight(g, fPct, fSmall, R, ty, r.Percent, r.Charging, fg, sub, sf, r.Text);
                        Look.Bar(g, new RectangleF(tx, ty + 23 * k, R - tx, 3 * k), r.Percent, Look.BarColor(s, r.Percent, r.Charging), r.Charging, phase);
                    }
                    else
                    {
                        var parts = r.Parts ?? new List<Part>();
                        float colW = (R - tx - 2 * 14 * k) / 3f;
                        for (int i = 0; i < 3; i++)
                        {
                            Part p = i < parts.Count ? parts[i] : new Part { Name = i == 0 ? "Sol" : i == 1 ? "Sağ" : "Kutu" };
                            float cx = tx + i * (colW + 14 * k), cy = ty + 24 * k;
                            using (var br = new SolidBrush(sub)) g.DrawString(p.Name, fSmall, br, cx, cy, sf);
                            PctRight(g, fPct, fSmall, cx + colW, cy - 1 * k, p.Percent, p.Charging, fg, sub, sf);
                            Look.Bar(g, new RectangleF(cx, cy + 21 * k, colW, 3 * k), p.Percent, Look.BarColor(s, p.Percent, p.Charging), p.Charging, phase);
                        }
                    }
                    y += (Multi(s) ? BudsRowH : RowH) * k;
                }
            }

            // konum: simgenin ustu, ortalanmis; simge yeri bilinmiyorsa imlecin ustu
            RECT ir;
            Point anchor;
            int top;
            if (IconRect(out ir)) { anchor = new Point((ir.l + ir.r) / 2, ir.t); top = ir.t; }
            else { anchor = Cursor.Position; top = Cursor.Position.Y - 12; }
            var scr = Screen.FromPoint(anchor).Bounds;
            int px = Math.Max(scr.Left + 8, Math.Min(anchor.X - W / 2, scr.Right - W - 8));
            int py = Math.Max(scr.Top + 8, top - H - (int)(12 * k));
            panel.Location = new Point(px, py);
            panel.Show(bmp);
        }
    }

    // Sagdan hizali yuzde; sarjda yaninda kucuk "sarj" isareti
    static void PctRight(Graphics g, Font f, Font fs, float right, float y, int pct, bool chg, Color fg, Color sub, StringFormat sf, string text = null)
    {
        float k = Look.K;
        string t = text ?? Look.Pct(pct);
        float w = g.MeasureString(t, f, PointF.Empty, sf).Width;
        bool low = !chg && pct >= 0 && pct <= 15;
        using (var br = new SolidBrush(pct < 0 ? sub : low ? Look.LowC : fg)) g.DrawString(t, f, br, right - w, y, sf);
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
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, int f);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}
