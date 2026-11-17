// Kablolu/2.4 GHz cihazlar: Razer Barracuda X (2022) alicisi ve ATK (Compx) mouse - HID uzerinden pil okuma
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Reflection;

static class Native
{
    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pd);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pd);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pd, out HIDP_CAPS caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] public static extern bool CancelIoEx(SafeFileHandle h, IntPtr o);

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr srcDc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr reserved; }
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort a, b, c, d, e, f, g, h, i, j;
    }

    public static List<string> HidPaths(string contains)
    {
        var list = new List<string>();
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        try
        {
            for (int i = 0; ; i++)
            {
                var d = new SP_DEVICE_INTERFACE_DATA(); d.cbSize = Marshal.SizeOf(d);
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                int req; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero)) continue;
                    string p = Marshal.PtrToStringAuto(new IntPtr(buf.ToInt64() + 4));
                    if (p.ToLowerInvariant().Contains(contains)) list.Add(p);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    public static bool GetCaps(string path, out HIDP_CAPS caps)
    {
        caps = new HIDP_CAPS();
        using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) return false;
            IntPtr pd;
            if (!HidD_GetPreparsedData(h, out pd)) return false;
            try { return HidP_GetCaps(pd, out caps) == 0x110000; }
            finally { HidD_FreePreparsedData(pd); }
        }
    }
}

// Genel HID baglantisi: okuma icin overlapped tutamac + okuma is parcacigi, yazma icin ayri tutamac
class HidLink : IDisposable
{
    FileStream rd, wr;
    SafeFileHandle rdh;
    BlockingCollection<byte[]> q = new BlockingCollection<byte[]>();
    int outLen;
    public volatile bool Dead;
    public Action<byte[]> Tap; // her gelen cerceveyi (istenmeyen olaylar dahil) gorur

    public static HidLink Open(string path, int outLen)
    {
        var hr = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        var hw = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (hr.IsInvalid || hw.IsInvalid) { hr.Dispose(); hw.Dispose(); return null; }
        var l = new HidLink();
        l.outLen = outLen; l.rdh = hr;
        l.rd = new FileStream(hr, FileAccess.Read, 1, true);
        l.wr = new FileStream(hw, FileAccess.Write, 1, false);
        var t = new Thread(l.ReadLoop); t.IsBackground = true; t.Start();
        return l;
    }

    void ReadLoop()
    {
        try
        {
            while (true)
            {
                var b = new byte[128];
                int n = rd.Read(b, 0, b.Length);
                if (n <= 0) break;
                var tap = Tap;
                if (tap != null) { try { tap(b); } catch { } }
                q.Add(b);
            }
        }
        catch { }
        Dead = true;
    }

    public void Drain() { byte[] x; while (q.TryTake(out x)) { } }

    public void Send(params byte[] data)
    {
        var b = new byte[outLen];
        Array.Copy(data, b, Math.Min(data.Length, outLen));
        wr.Write(b, 0, b.Length); wr.Flush();
    }

    public byte[] Wait(Func<byte[], bool> ok, int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (true)
        {
            int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
            if (left <= 0) return null;
            byte[] b;
            if (q.TryTake(out b, left) && ok(b)) return b;
        }
    }

    public void Dispose()
    {
        try { Native.CancelIoEx(rdh, IntPtr.Zero); } catch { }
        try { rd.Dispose(); } catch { }
        try { wr.Dispose(); } catch { }
    }
}

class Reading
{
    public bool Present;     // alici takili mi
    public int Percent = -1; // -1: cihaz kapali / uykuda
    public bool Charging;
    public int MilliVolt;
    public System.Collections.Generic.List<Part> Parts; // cok pilli cihazlar (sol/sag/kutu)
    public string Text;      // yuzde yerine gosterilecek metin (orn. kademeli seviye: "Orta")
}

abstract class Device : IDisposable
{
    protected HidLink link;
    protected abstract HidLink Connect();
    protected abstract void Query(Reading r);

    public virtual Reading Read()
    {
        var r = new Reading();
        try
        {
            if (link != null && link.Dead) { link.Dispose(); link = null; }
            if (link == null) link = Connect();
            if (link == null) return r;
            r.Present = true;
            Query(r);
        }
        catch { if (link != null) { link.Dispose(); link = null; } }
        return r;
    }

    public void Dispose() { if (link != null) link.Dispose(); }
}

// Razer Barracuda X (2022) - alici 1532:0552, MI_03 COL01, 64 bayt rapor, Airoha RACE komutlari
class Barracuda : Device
{
    byte seq;
    public event Action Changed;

    // Alicinin kendiliginden gonderdigi olaylar (op 0x02): kablo takildi/cikti (0x2A), pil degisti (0x21)
    void OnFrame(byte[] b)
    {
        if (b[1] == 0x80 && b[3] == 0x50 && b[4] == 0x49 && b[5] == 0x08 && b[14] == 0x02 && (b[13] == 0x2A || b[13] == 0x21))
        {
            var h = Changed;
            if (h != null) h();
        }
    }

    byte Next() { seq = (byte)(seq % 127 + 1); return seq; }

    protected override HidLink Connect()
    {
        foreach (var p in Native.HidPaths("vid_1532&pid_0552&mi_03&col01"))
        {
            var l = HidLink.Open(p, 64);
            if (l != null) { l.Tap = OnFrame; return l; }
        }
        return null;
    }

    bool Remote(bool on)
    {
        link.Drain();
        byte s = Next();
        link.Send(1, 0x80, 7, 0x50, 0x41, 0x0E, s, 2, 0xE1, (byte)(on ? 1 : 0));
        var r = link.Wait(b => b[1] == 0x80 && b[3] == 0x50 && b[4] == 0x49 && b[13] == 0x0E && b[14] == (0x80 | s), 1000);
        return r != null && r[15] == 0;
    }

    int Get(byte cmd)
    {
        link.Drain();
        link.Send(1, 0x80, 8, 0x50, 0x41, 0x08, Next(), 3, cmd, 0, 0);
        var r = link.Wait(b => b[1] == 0x80 && b[3] == 0x50 && b[4] == 0x49 && b[5] == 0x08 && b[13] == cmd && b[14] == 0x01, 1500);
        return r == null ? -1 : r[16];
    }

    protected override void Query(Reading r)
    {
        link.Drain();
        link.Send(1, 0x40);
        link.Wait(b => b[1] == 0x40, 600);
        if (!Remote(true)) { Remote(false); return; }
        try
        {
            int p = Get(0x21);
            if (p >= 0 && p <= 100) r.Percent = p;
            r.Charging = Get(0x2A) == 1;
        }
        finally { Remote(false); }
    }
}

// ATK / Compx 2.4G mouse - VID 373B, kullanim sayfasi 0xFF02, 17 bayt rapor, komut 0x04
class AtkMouse : Device
{
    // Firmware'in verdigi yuzde ayni voltajda %25-%45 arasinda ziplayabiliyor; voltaj ise kararli.
    // Bu yuzden yuzdeyi voltajdan (Li-Po bosalma egrisi) hesapliyor ve filtreliyoruz.
    // Kayittan: tam dolu pil dinlenmede ~4350 mV (LiHV hucre, 4.35 V), sarj oncesi %40-45 civari 3845 mV.
    static readonly int[] Curve = { 3450, 3700, 3750, 3790, 3830, 3880, 3950, 4030, 4120, 4220, 4330 }; // %0,10,...,100

    public static int PercentFromMv(int mv)
    {
        if (mv <= Curve[0]) return 0;
        if (mv >= Curve[10]) return 100;
        for (int i = 1; i <= 10; i++)
            if (mv <= Curve[i])
                return (int)Math.Round(10.0 * (i - 1) + 10.0 * (mv - Curve[i - 1]) / (Curve[i] - Curve[i - 1]));
        return 100;
    }

    readonly List<KeyValuePair<DateTime, int>> samples = new List<KeyValuePair<DateTime, int>>();
    int shown = -1;               // gosterilen deger
    DateTime riseSince = DateTime.MinValue;
    bool wasCharging;
    DateTime settleUntil = DateTime.MinValue; // bu ana kadar deger serbestce oturabilir
    int chargeBase; DateTime chargeStart;     // sarj basladiginda gosterilen deger ve zaman
    // Kayittan: %45 -> ~%90 yaklasik 52 dk (sabit akim), son %10 yaklasik 45 dk (sabit voltaj).
    static double ChargeMinutes(int from, int to)
    {
        double m = 0;
        for (int p = from; p < to; p++) m += p < 90 ? 1 / 0.85 : 1 / 0.22;
        return m;
    }
    static int ChargeAfter(int from, double minutes)
    {
        int p = from;
        while (p < 100 && ChargeMinutes(from, p + 1) <= minutes) p++;
        return p;
    }
    const string StateKey = @"Software\BatteryRings";

    public AtkMouse() { LoadState(); }

    // Gosterilen degeri sakla: program yeniden acilinca firmware'in ziplayan degeriyle baslamasin
    void LoadState()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(StateKey))
            {
                if (k == null) return;
                long at = Convert.ToInt64(k.GetValue("MouseAt", 0L));
                int v = (int)k.GetValue("MouseShown", -1);
                if (v >= 0 && v <= 100 && (DateTime.Now - new DateTime(at)).TotalHours < 12) shown = v;
            }
        }
        catch { }
    }

    int saved = -2;
    void SaveState()
    {
        if (shown == saved) return;
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(StateKey))
            {
                k.SetValue("MouseShown", shown, RegistryValueKind.DWord);
                k.SetValue("MouseAt", DateTime.Now.Ticks, RegistryValueKind.QWord);
            }
            saved = shown;
        }
        catch { }
    }

    int Smooth(int fwPercent, int mv, bool charging)
    {
        int v = SmoothCore(fwPercent, mv, charging);
        SaveState();
        if (LogEnabled) Log(fwPercent, mv, charging, v);
        return v;
    }

    // Kalibrasyon icin ham veri kaydi: %LOCALAPPDATA%\PilGostergesi\mouse_log.csv
    // Her 30 sn'de bir (ya da sarj durumu/baglanti degisince) bir satir.
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PilGostergesi", "mouse_log.csv");
    const bool LogEnabled = false; // kalibrasyon bitti, kayit kapali
    DateTime lastLog = DateTime.MinValue;
    string lastKey = "";

    void Log(int fw, int mv, bool charging, int shownVal)
    {
        try
        {
            var now = DateTime.Now;
            string conn = linkPath != null && linkPath.ToLowerInvariant().Contains("pid_11d9") ? "alici" : "kablo";
            string key = conn + charging + "|" + PollCode + "|" + SensorMode;
            if (key == lastKey && (now - lastLog).TotalSeconds < 30) return;
            lastKey = key; lastLog = now;
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            bool header = !File.Exists(LogPath);
            File.AppendAllText(LogPath,
                (header ? "zaman,baglanti,sarj,firmware_yuzde,mV,gosterilen_yuzde,polling_hz,sensor_modu" + Environment.NewLine : "") +
                now.ToString("yyyy-MM-dd HH:mm:ss") + "," + conn + "," + (charging ? 1 : 0) + "," + fw + "," + mv + "," + shownVal + "," + PollText(PollCode) + "," + (SensorMode < 0 ? "" : SensorMode.ToString()) + Environment.NewLine);
        }
        catch { }
    }

    int SmoothCore(int fwPercent, int mv, bool charging)
    {
        var now = DateTime.Now;
        if (charging)
        {
            // Sarjda voltaj sarj akimi yuzunden sisik, firmware yuzdesi de ziplayabiliyor.
            // Sarj oncesindeki degerden basla, hedefe en fazla dakikada %1 hizla yuksel, asla dusme.
            samples.Clear(); riseSince = DateTime.MinValue; settleUntil = DateTime.MinValue;
            int target = Math.Max(0, Math.Min(100, fwPercent));
            if (shown < 0) { shown = target; chargeBase = shown; chargeStart = now; }
            else if (!wasCharging) { chargeBase = shown; chargeStart = now; }
            wasCharging = true;
            int allowed = ChargeAfter(chargeBase, (now - chargeStart).TotalMinutes);
            if (target >= 100) shown = 100;   // sarj devresi "doldu" dedi
            else shown = Math.Max(shown, Math.Min(target, allowed));
            return shown;
        }
        if (mv < 2500 || mv > 4500) return shown >= 0 ? shown : fwPercent;

        // Son 3 dakikanin voltajlari: hareket sirasindaki anlik dususleri yok saymak icin ust %75'lik dilimi al
        samples.Add(new KeyValuePair<DateTime, int>(now, mv));
        samples.RemoveAll(x => (now - x.Key).TotalMinutes > 3);
        var sorted = samples.ConvertAll(x => x.Value); sorted.Sort();
        int v = sorted[(int)Math.Floor((sorted.Count - 1) * 0.75)];
        int pct = PercentFromMv(v);

        if (shown < 0 || wasCharging)
        {
            // ilk okuma ya da sarjdan yeni cikildi: sarj sonrasi voltaj birkac dakika kendiliginden dusecek
            bool fromCharge = wasCharging && shown >= 0;
            if (fromCharge && pct > shown) pct = shown;   // sarj sonrasi voltaj sisik: yukselme
            shown = pct; wasCharging = false; riseSince = DateTime.MinValue;
            if (!fromCharge) settleUntil = now.AddMinutes(1);
            return shown;
        }
        if (now < settleUntil) { shown = pct; return shown; }
        if (pct <= shown) { shown = pct; riseSince = DateTime.MinValue; }
        else if (pct >= shown + 3)
        {
            // sarjda degilken pil dolmaz: yukselisi ancak 10 dk boyunca surerse kabul et (ilk hatali okuma icin)
            if (riseSince == DateTime.MinValue) riseSince = now;
            else if ((now - riseSince).TotalMinutes >= 10) { shown = pct; riseSince = DateTime.MinValue; }
        }
        return shown;
    }

    // Mouse iki sekilde baglanabilir: kablosuz alici (or. 373B:11D9) ya da sarj kablosuyla
    // dogrudan USB (or. 373B:11B6 "ATK A9 Ultimate"). Hangisi cevap veriyorsa onu kullan.
    string linkPath;

    // Many brands build on the same Compx chip (VID 373B), so take the name from the device itself.
    // The calibrated voltage curve is only used for the ATK A9 Ultimate; others show the firmware percentage.
    public string DeviceName = "Wireless mouse";
    public bool IsAtk;
    readonly Dictionary<string, string> names = new Dictionary<string, string>();

    // Over the cable the mouse reports its real name (e.g. "ATK A9 Ultimate"); the 2.4 GHz dongle only says
    // "Wireless mouse ... dongle" / "Compx". Remember the cable name and reuse it on the dongle.
    void UpdateName(string path)
    {
        string n;
        if (!names.TryGetValue(path, out n))
        {
            n = VendorDevices.Product(path) ?? "";
            names[path] = n;
        }
        bool generic = n.Length == 0 || n.IndexOf("dongle", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("receiver", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("2.4G", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!generic)
        {
            if (DeviceName != n)
                try { using (var k = Registry.CurrentUser.CreateSubKey(StateKey)) k.SetValue("MouseName", n); } catch { }
            DeviceName = n;
        }
        else
        {
            string saved = null;
            try { using (var k = Registry.CurrentUser.OpenSubKey(StateKey)) if (k != null) saved = k.GetValue("MouseName") as string; } catch { }
            DeviceName = string.IsNullOrEmpty(saved) ? "Wireless mouse" : saved;
        }
        // The voltage curve and charge model were measured on an ATK A9 Ultimate (4.35 V LiHV cell).
        // Other models may use a different battery, so they keep the firmware's own percentage.
        IsAtk = DeviceName.IndexOf("A9 Ultimate", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Compx command interface: vendor page 0xFF02. Most use 17-byte reports; some firmwares (e.g. on cable)
    // use longer ones, so accept those too and pad the 17-byte command.
    readonly Dictionary<string, int> outLens = new Dictionary<string, int>();
    List<string> Candidates()
    {
        var list = new List<string>();
        foreach (var p in Native.HidPaths("vid_373b&"))
        {
            Native.HIDP_CAPS c;
            if (!Native.GetCaps(p, out c)) continue;
            if (c.UsagePage == 0xFF02 && c.InputReportByteLength >= 17 && c.OutputReportByteLength >= 17)
            {
                list.Add(p);
                outLens[p] = c.OutputReportByteLength;
            }
        }
        // 17-byte interfaces first: those are the ones known to answer
        list.Sort((a, b) => outLens[a].CompareTo(outLens[b]));
        return list;
    }

    // true if a Compx device that is not a dongle (= the mouse on its cable) is plugged in
    bool CablePresent(List<string> paths)
    {
        foreach (var p in Native.HidPaths("vid_373b&"))
        {
            string n;
            if (!names.TryGetValue(p, out n)) { n = VendorDevices.Product(p) ?? ""; names[p] = n; }
            if (n.Length > 0 && n.IndexOf("dongle", StringComparison.OrdinalIgnoreCase) < 0
                && n.IndexOf("receiver", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("2.4G", StringComparison.OrdinalIgnoreCase) < 0)
                return true;
        }
        return false;
    }
    int lastPercent = -1;

    protected override HidLink Connect() { return null; } // handled inside Read

    public override Reading Read()
    {
        var r = new Reading();
        try
        {
            var paths = Candidates();
            if (paths.Count == 0) { Drop(); return r; }
            r.Present = true;

            // 1) mevcut baglanti hala listede ve cevap veriyorsa onu kullan
            if (link != null && !link.Dead && paths.Contains(linkPath))
            {
                UpdateName(linkPath);
                Query(r);
                if (r.Percent >= 0) { lastPercent = r.Percent; return r; }
            }
            // 2) digerlerini sirayla dene (kablo takildiysa kablolu baglanti burada bulunur)
            foreach (var p in paths)
            {
                if (p == linkPath && link != null && !link.Dead) continue;
                var l = HidLink.Open(p, outLens.ContainsKey(p) ? outLens[p] : 17);
                if (l == null) continue;
                var old = link; link = l;
                UpdateName(p);
                Query(r);
                if (r.Percent >= 0) { if (old != null) old.Dispose(); linkPath = p; lastPercent = r.Percent; return r; }
                link = old; l.Dispose();
            }
            // On the cable some mice stop answering battery queries. Keep showing it as charging
            // with the last known level instead of making it disappear.
            if (lastPercent >= 0 && CablePresent(paths))
            {
                r.Percent = lastPercent; r.Charging = true;
                return r;
            }
        }
        catch { Drop(); }
        return r;
    }

    void Drop() { if (link != null) { link.Dispose(); link = null; } linkPath = null; }

    // Mouse ayarlarini oku (yalnizca OKUMA komutu 0x08; hicbir ayar degistirilmez)
    // 0x0000: polling kodu (+ tamamlayicisi 0x55-kod), 0x00B5: sensor/performans modu
    public int PollCode = -1, SensorMode = -1;
    DateTime cfgAt = DateTime.MinValue;

    byte[] ReadEeprom(int addr, int len)
    {
        var f = new byte[17];
        f[0] = 8; f[1] = 8; f[3] = (byte)(addr >> 8); f[4] = (byte)addr; f[5] = (byte)len;
        int sum = 0; for (int i = 0; i < 16; i++) sum += f[i];
        f[16] = (byte)((0x55 - sum) & 0xFF);
        link.Drain();
        link.Send(f);
        return link.Wait(x =>
        {
            if (x[0] != 8 || x[1] != 8 || x[3] != f[3] || x[4] != f[4]) return false;
            int sm = 0; for (int i = 0; i < 17; i++) sm += x[i];
            if ((sm & 0xFF) != 0x55) return false;
            for (int i = 6; i < 16; i++) if (x[i] != 0) return true;
            return false; // yanki
        }, 800);
    }

    void ReadConfig()
    {
        if ((DateTime.Now - cfgAt).TotalSeconds < 60) return;
        cfgAt = DateTime.Now;
        try
        {
            var a = ReadEeprom(0x0000, 10);
            if (a != null && ((a[6] + a[7]) & 0xFF) == 0x55) PollCode = a[6];
            var b = ReadEeprom(0x00B5, 6);
            if (b != null && b[8] == 0x06 && b[9] == 0x4F && ((b[10] + b[11]) & 0xFF) == 0x55) SensorMode = b[10];
        }
        catch { }
    }

    public static string PollText(int code)
    {
        switch (code)
        {
            case 0x08: return "125"; case 0x04: return "250"; case 0x02: return "500"; case 0x01: return "1000";
            case 0x10: return "2000"; case 0x20: return "4000"; case 0x40: return "8000";
            default: return code < 0 ? "" : "kod" + code;
        }
    }

    protected override void Query(Reading r)
    {
        var q = new byte[] { 8, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x49 };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            link.Drain();
            link.Send(q);
            var b = link.Wait(x =>
            {
                if (x[0] != 8 || x[1] != 4 || x[2] != 0) return false;
                int sum = 0; for (int i = 0; i < 17; i++) sum += x[i];
                if ((sum & 0xFF) != 0x55) return false;
                for (int i = 2; i < 16; i++) if (x[i] != 0) return true;
                return false; // sorgunun yankisi
            }, 800);
            if (b == null) continue;
            int mv = (b[8] << 8) | b[9];
            if (b[6] == 0 && mv == 0) continue;
            r.Charging = b[7] == 1;
            r.MilliVolt = mv;
            if (LogEnabled) ReadConfig(); // ATK-only EEPROM read, needed only for calibration logs
            r.Percent = IsAtk ? Smooth(Math.Min(100, (int)b[6]), mv, r.Charging) : Math.Min(100, (int)b[6]);
            return;
        }
    }
}
