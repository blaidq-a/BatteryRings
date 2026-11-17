// Vendor protocols over HID that Windows itself does not report:
//   - Logitech HID++ 2.0 (Unifying / Bolt / Lightspeed receivers and direct USB/Bluetooth): mice, keyboards, G headsets
//   - Razer mice (90-byte feature reports, as documented by OpenRazer)
//   - Headsets from SteelSeries, HyperX and Corsair (commands as documented by HeadsetControl)
//   - ASUS ROG / TUF wireless mice (command 0x12 0x07, as used by G-Helper)
//   - Sony DualShock 4 and DualSense controllers (battery in the input report, as read by the Linux hid-playstation driver)
// None of these were tested on real hardware here; they follow the published protocol notes.
// A slow scan runs on its own thread so device timeouts never hold up the UI.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

static class VendorDevices
{
    static readonly object lk = new object();
    static List<Found> cache = new List<Found>();
    static Thread worker;

    // Latest results; starts the background scanner on first use
    public static List<Found> Snapshot()
    {
        if (worker == null)
        {
            worker = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }
        lock (lk) return new List<Found>(cache);
    }

    static void Loop()
    {
        while (true)
        {
            var list = new List<Found>();
            try { Logitech.Scan(list); } catch { }
            try { RazerMice.Scan(list); } catch { }
            try { Headsets.Scan(list); } catch { }
            try { HyperXCloud2.Scan(list); } catch { }
            try { HyperXCloud3.Scan(list); } catch { }
            try { AsusMice.Scan(list); } catch { }
            try { PlayStation.Scan(list); } catch { }
            try { HidBattery.Scan(list); } catch { }
            lock (lk) cache = list;
            Thread.Sleep(20000);
        }
    }

    // ---------------- shared helpers ----------------

    public static List<KeyValuePair<string, Native.HIDP_CAPS>> Collections(int vid)
    {
        var r = new List<KeyValuePair<string, Native.HIDP_CAPS>>();
        foreach (var p in Native.HidPaths("vid_" + vid.ToString("x4")))
        {
            Native.HIDP_CAPS c;
            if (Native.GetCaps(p, out c)) r.Add(new KeyValuePair<string, Native.HIDP_CAPS>(p, c));
        }
        return r;
    }

    public static int Pid(string path)
    {
        int i = path.ToLowerInvariant().IndexOf("pid_");
        int v;
        return i >= 0 && int.TryParse(path.Substring(i + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out v) ? v : -1;
    }

    public static int Interface(string path)
    {
        int i = path.ToLowerInvariant().IndexOf("&mi_");
        int v;
        return i >= 0 && int.TryParse(path.Substring(i + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out v) ? v : -1;
    }

    public static string Product(string path)
    {
        using (var h = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) return null;
            var b = new byte[256];
            if (!HidD_GetProductString(h, b, b.Length)) return null;
            string s = Encoding.Unicode.GetString(b);
            int z = s.IndexOf('\0');
            return (z >= 0 ? s.Substring(0, z) : s).Trim();
        }
    }

    // Li-ion voltage -> percent (used where a device only reports millivolts)
    public static int FromMv(int mv)
    {
        int[] c = { 3500, 3680, 3740, 3770, 3790, 3820, 3870, 3920, 3980, 4060, 4150 };
        if (mv <= c[0]) return 0;
        for (int i = 1; i < c.Length; i++)
            if (mv <= c[i]) return (int)Math.Round(10.0 * (i - 1) + 10.0 * (mv - c[i - 1]) / (c[i] - c[i - 1]));
        return 100;
    }

    [DllImport("hid.dll")] public static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] public static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buf, int len);
}

// ---------------- Logitech HID++ 2.0 ----------------
static class Logitech
{
    const byte SW = 0x0B; // our software id, echoed back in replies

    public static void Scan(List<Found> list)
    {
        foreach (var c in VendorDevices.Collections(0x046D))
        {
            // the HID++ long-report collection: vendor page 0xFF00, usage 2, 20-byte reports
            if (c.Value.UsagePage != 0xFF00 || c.Value.Usage != 0x0002 || c.Value.OutputReportByteLength != 20) continue;
            var link = HidLink.Open(c.Key, 20);
            if (link == null) continue;
            try
            {
                int pid = VendorDevices.Pid(c.Key);
                // direct device (USB cable / Bluetooth) answers on index 0xFF; receivers on 1..6
                if (!TryDevice(link, 0xFF, pid, c.Key, list))
                    for (byte idx = 1; idx <= 6; idx++) TryDevice(link, idx, pid, c.Key, list);
            }
            finally { link.Dispose(); }
        }
    }

    static bool TryDevice(HidLink l, byte idx, int pid, string path, List<Found> list)
    {
        // Root.GetFeature(0x0000) doubles as a ping: offline devices do not answer
        if (Feature(l, idx, 0x0001) < 0) return false;
        Reading r = null;
        int f;
        if ((f = Feature(l, idx, 0x1004)) > 0) r = Unified(l, idx, (byte)f);
        if (r == null && (f = Feature(l, idx, 0x1000)) > 0) r = Status(l, idx, (byte)f);
        if (r == null && (f = Feature(l, idx, 0x1001)) > 0) r = Voltage(l, idx, (byte)f, false);
        if (r == null && (f = Feature(l, idx, 0x1F20)) > 0) r = Voltage(l, idx, (byte)f, true); // G headsets
        if (r == null) return true; // reachable but no battery feature

        Kind kind = Kind.Other;
        string name = Name(l, idx, ref kind) ?? (idx == 0xFF ? VendorDevices.Product(path) : null) ?? "Logitech device";
        list.Add(new Found { Id = "logi:" + pid.ToString("x4") + ":" + idx + ":" + name, Name = name, Kind = kind, Reading = r });
        return true;
    }

    // Returns the feature index, 0 if unsupported, -1 if the device did not answer
    static int Feature(HidLink l, byte idx, int id)
    {
        var a = Call(l, idx, 0x00, 0, (byte)(id >> 8), (byte)id);
        if (a == null) return -1;
        return a[4];
    }

    static byte[] Call(HidLink l, byte idx, byte feat, int func, params byte[] args)
    {
        var req = new byte[20];
        req[0] = 0x11; req[1] = idx; req[2] = feat; req[3] = (byte)((func << 4) | SW);
        Array.Copy(args, 0, req, 4, Math.Min(args.Length, 16));
        l.Drain();
        try { l.Send(req); } catch { return null; }
        var r = l.Wait(b => b[0] == 0x11 && b[1] == idx && (b[2] == feat || b[2] == 0xFF) && (b[3] & 0x0F) == SW
                          || (b[0] == 0x10 && b[1] == idx && b[2] == 0x8F), 350);
        if (r == null || r[0] == 0x10 || r[2] == 0xFF) return null; // timeout or error
        return r;
    }

    static Reading Unified(HidLink l, byte idx, byte f)
    {
        var a = Call(l, idx, f, 1);
        if (a == null) return null;
        int soc = a[4], levels = a[5], chg = a[6];
        if (soc == 0 || soc > 100)
            soc = (levels & 8) != 0 ? 100 : (levels & 4) != 0 ? 60 : (levels & 2) != 0 ? 20 : (levels & 1) != 0 ? 5 : -1;
        if (soc < 0) return null;
        return new Reading { Present = true, Percent = soc, Charging = chg == 1 || chg == 2 };
    }

    static Reading Status(HidLink l, byte idx, byte f)
    {
        var a = Call(l, idx, f, 0);
        if (a == null || a[4] == 0 || a[4] > 100) return null;
        int st = a[6];
        return new Reading { Present = true, Percent = a[4], Charging = st == 1 || st == 2 || st == 4 };
    }

    static Reading Voltage(HidLink l, byte idx, byte f, bool adc)
    {
        var a = Call(l, idx, f, 0);
        if (a == null) return null;
        int mv = a[4] << 8 | a[5];
        if (mv < 2500 || mv > 4500) return null;
        int flags = a[6];
        bool chg = adc ? (flags & 0x02) != 0 : (flags & 0x80) != 0;
        return new Reading { Present = true, Percent = VendorDevices.FromMv(mv), Charging = chg, MilliVolt = mv };
    }

    // DEVICE_NAME (0x0005): length, then 16-character chunks; function 2 gives the device type
    static string Name(HidLink l, byte idx, ref Kind kind)
    {
        int f = Feature(l, idx, 0x0005);
        if (f <= 0) return null;
        var t = Call(l, idx, (byte)f, 2);
        if (t != null)
        {
            int ty = t[4];
            kind = ty == 0 || ty == 2 ? Kind.Keyboard : ty == 3 || ty == 4 || ty == 5 ? Kind.Mouse : ty == 8 ? Kind.Headset : Kind.Other;
        }
        var n = Call(l, idx, (byte)f, 0);
        if (n == null || n[4] == 0) return null;
        int len = Math.Min((int)n[4], 64);
        var sb = new StringBuilder();
        while (sb.Length < len)
        {
            var c = Call(l, idx, (byte)f, 1, (byte)sb.Length);
            if (c == null) break;
            for (int i = 4; i < 20 && sb.Length < len; i++) sb.Append((char)c[i]);
        }
        string s = sb.ToString().Trim('\0', ' ');
        return s.Length == 0 ? null : (s.StartsWith("Logitech", StringComparison.OrdinalIgnoreCase) || s.StartsWith("G") || s.StartsWith("MX") ? s : "Logitech " + s);
    }
}

// ---------------- Razer mice ----------------
static class RazerMice
{
    static readonly byte[] Transactions = { 0x1F, 0x3F, 0xFF, 0x08 };
    static readonly Dictionary<string, byte> known = new Dictionary<string, byte>();

    public static void Scan(List<Found> list)
    {
        var done = new HashSet<int>();
        foreach (var c in VendorDevices.Collections(0x1532))
        {
            int pid = VendorDevices.Pid(c.Key);
            if (pid == 0x0552 || done.Contains(pid)) continue;           // Barracuda X has its own reader
            if (c.Value.FeatureReportByteLength != 91) continue;         // Razer control interface
            using (var h = Native.CreateFile(c.Key, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) continue;
                byte[] lvl = null, chg = null;
                byte tid;
                if (known.TryGetValue(c.Key, out tid)) lvl = Query(h, tid, 0x80);
                else
                    foreach (var t in Transactions)
                        if ((lvl = Query(h, t, 0x80)) != null) { known[c.Key] = tid = t; break; }
                if (lvl == null) continue;
                chg = Query(h, tid, 0x84);
                int raw = lvl[10];
                if (raw == 0) continue; // dongle without a mouse awake
                done.Add(pid);
                string name = VendorDevices.Product(c.Key) ?? "Razer mouse";
                list.Add(new Found
                {
                    Id = "razer:" + pid.ToString("x4"), Name = name,
                    Kind = name.IndexOf("keyboard", StringComparison.OrdinalIgnoreCase) >= 0 ? Kind.Keyboard : Kind.Mouse,
                    Reading = new Reading { Present = true, Percent = (int)Math.Round(raw * 100 / 255.0), Charging = chg != null && chg[10] == 1 }
                });
            }
        }
    }

    // Power class 0x07: 0x80 battery level, 0x84 charging state. Returns the reply or null.
    static byte[] Query(SafeFileHandle h, byte tid, byte cmd)
    {
        var b = new byte[91];
        b[2] = tid; b[6] = 0x02; b[7] = 0x07; b[8] = cmd;
        byte crc = 0;
        for (int i = 3; i < 89; i++) crc ^= b[i];
        b[89] = crc;
        if (!VendorDevices.HidD_SetFeature(h, b, b.Length)) return null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            Thread.Sleep(60);
            var r = new byte[91];
            if (!VendorDevices.HidD_GetFeature(h, r, r.Length)) return null;
            if (r[1] == 0x01) continue;                       // busy
            if (r[1] != 0x02 || r[7] != 0x07 || r[8] != cmd) return null; // failure / not supported
            return r;
        }
        return null;
    }
}

// ---------------- Headsets (SteelSeries, HyperX, Corsair) ----------------
static class Headsets
{
    // How to read one model family. Response indexes are after the report id byte is dropped
    // when the device uses no report ids (the way hidapi returns it).
    class Def
    {
        public string Name;
        public int Vid;
        public int[] Pids;
        public int Iface = -1;
        public byte[] Req;
        public Func<byte[], Reading> Parse;
    }

    static int Map(int v, int lo, int hi) { return Math.Max(0, Math.Min(100, (int)Math.Round((v - lo) * 100.0 / (hi - lo)))); }
    static Reading R(int pct, bool chg) { return pct < 0 ? null : new Reading { Present = true, Percent = Math.Min(100, pct), Charging = chg }; }

    static readonly int[] Nova7Discrete = { 0x2202, 0x2206, 0x220a, 0x223a, 0x227a, 0x22a4 };

    static readonly Def[] Defs = {
        new Def { Name = "SteelSeries Arctis 1 Wireless", Vid = 0x1038, Pids = new[] { 0x12b3, 0x12b6, 0x12d7, 0x12d5 }, Iface = 3,
            Req = new byte[] { 0x06, 0x12 }, Parse = d => d[2] == 0x01 ? null : R(d[3], false) },
        new Def { Name = "SteelSeries Arctis 7", Vid = 0x1038, Pids = new[] { 0x1260, 0x12ad, 0x1252, 0x1280 }, Iface = 5,
            Req = new byte[] { 0x06, 0x18 }, Parse = d => R(d[2], false) },
        new Def { Name = "SteelSeries Arctis 9", Vid = 0x1038, Pids = new[] { 0x12c2, 0x12c4 }, Iface = 0,
            Req = new byte[] { 0x00, 0x20 }, Parse = d => R(Map(d[3], 0x64, 0x9A), d[4] == 0x01) },
        new Def { Name = "SteelSeries Arctis 7+", Vid = 0x1038, Pids = new[] { 0x220e, 0x2212, 0x2216, 0x2236 }, Iface = 3,
            Req = new byte[] { 0x00, 0xb0 }, Parse = d => d[1] == 0x01 ? null : R(Map(d[2], 0, 4), d[3] == 0x01) },
        new Def { Name = "SteelSeries Arctis Nova 7", Vid = 0x1038,
            Pids = new[] { 0x2202, 0x22A1, 0x227e, 0x2206, 0x2258, 0x229e, 0x22ad, 0x223a, 0x22a9, 0x227a, 0x22a4, 0x22a5 }, Iface = 3,
            Req = new byte[] { 0x00, 0xb0 }, Parse = null /* depends on pid, see Read */ },
        new Def { Name = "SteelSeries Arctis Nova 5", Vid = 0x1038, Pids = new[] { 0x2232, 0x2253 }, Iface = 3,
            Req = new byte[] { 0x00, 0xb0 }, Parse = d => d[1] == 0x02 ? null : R(d[3], d[4] == 0x01) },
        new Def { Name = "SteelSeries Arctis Nova 3P", Vid = 0x1038, Pids = new[] { 0x2269, 0x226d }, Iface = 3,
            Req = new byte[] { 0x00, 0xb0 }, Parse = d => d[1] == 0x02 ? null : R(d[3], d[4] == 0x01) },
        new Def { Name = "SteelSeries Arctis Nova Pro Wireless", Vid = 0x1038, Pids = new[] { 0x12e0, 0x12e5 }, Iface = 4,
            Req = new byte[] { 0x06, 0xb0 }, Parse = d => d[15] == 0x01 ? null : R(Map(d[6], 0, 8), d[15] == 0x02) },
        new Def { Name = "HyperX Cloud Alpha Wireless", Vid = 0x03F0, Pids = new[] { 0x098d },
            Req = new byte[] { 0x21, 0xbb, 0x0b }, Parse = d => d[0] == 0x21 && d[2] == 0x0b ? R(d[3], false) : null },
        new Def { Name = "HyperX Cloud Flight", Vid = 0x0951, Pids = new[] { 0x16C4, 0x1723 },
            Req = new byte[] { 0x21, 0xff, 0x05 }, Parse = d =>
            {
                int mv = d[3] << 8 | d[4];
                if (mv > 0x100B) return R(100, true);
                return R(mv <= 3648 ? 5 : mv > 3975 ? 100 : Map(mv, 3648, 3975), false);
            } },
        new Def { Name = "Corsair Void", Vid = 0x1B1C,
            Pids = new[] { 0x1b27, 0x0a14, 0x0a16, 0x0a17, 0x0a1d, 0x0a1a, 0x1b2a, 0x1b23, 0x1b29, 0x0a55, 0x0a51, 0x0a52, 0x0a38, 0x0a4f, 0x0a2b, 0x0a75, 0x0a56 },
            Req = new byte[] { 0xC9, 0x64 }, Parse = d => d[4] == 0 || d[4] > 5 ? null : R(d[2] & 0x7F, d[4] == 4 || d[4] == 5) },
    };

    public static void Scan(List<Found> list)
    {
        foreach (var vid in new[] { 0x1038, 0x03F0, 0x0951, 0x1B1C })
        {
            var cols = VendorDevices.Collections(vid);
            var done = new HashSet<int>();
            foreach (var c in cols)
            {
                int pid = VendorDevices.Pid(c.Key);
                if (done.Contains(pid)) continue;
                Def def = Array.Find(Defs, x => x.Vid == vid && Array.IndexOf(x.Pids, pid) >= 0);
                if (def == null) continue;
                if (def.Iface >= 0 && VendorDevices.Interface(c.Key) != def.Iface) continue;
                if (c.Value.UsagePage < 0xFF00 || c.Value.OutputReportByteLength == 0) continue;
                var r = Read(def, pid, c.Key, c.Value);
                if (r == null) continue;
                done.Add(pid);
                list.Add(new Found { Id = "hs:" + vid.ToString("x4") + pid.ToString("x4"), Name = VendorDevices.Product(c.Key) ?? def.Name, Kind = Kind.Headset, Reading = r });
            }
        }
    }

    static Reading Read(Def def, int pid, string path, Native.HIDP_CAPS caps)
    {
        var l = HidLink.Open(path, caps.OutputReportByteLength);
        if (l == null) return null;
        try
        {
            l.Drain();
            l.Send(def.Req);
            var raw = l.Wait(b => true, 800);
            if (raw == null) return null;
            // drop the leading report-id 0 like hidapi does
            byte[] d = raw;
            if (raw[0] == 0) { d = new byte[raw.Length - 1]; Array.Copy(raw, 1, d, 0, d.Length); }
            if (def.Parse != null) return def.Parse(d);
            // Arctis Nova 7 family
            if (d[3] == 0x00) return null; // headset offline
            bool discrete = Array.IndexOf(Nova7Discrete, pid) >= 0;
            return R(discrete ? Map(d[2], 0, 4) : d[2], d[3] == 0x01 || d[3] == 0x02);
        }
        catch { return null; }
        finally { l.Dispose(); }
    }
}

// ---------------- Standard HID battery (any brand) ----------------
// Many 2.4 GHz keyboards and mice report their charge through the standard HID usages
// "Battery Strength" (page 0x06, usage 0x20) or "Absolute/Relative State Of Charge" (page 0x85).
// Windows does not display these, so we read them ourselves.
static class HidBattery
{
    struct Cap { public int Type, Page, Usage, Link, ReportId, Min, Max; }

    public static void Scan(List<Found> list)
    {
        var done = new HashSet<string>();
        foreach (var f in list) done.Add(f.Id);
        var seen = new HashSet<string>();
        foreach (var p in Native.HidPaths("hid#"))
        {
            string lp = p.ToLowerInvariant();
            int vi = lp.IndexOf("vid_");
            if (vi < 0) continue;
            string vid = lp.Substring(vi + 4, 4);
            int pid = VendorDevices.Pid(p);
            if (vid == "373b" || vid == "3554" || (vid == "1532" && pid == 0x0552)) continue; // have dedicated readers
            string key = vid + ":" + pid.ToString("x4");
            if (seen.Contains(key)) continue;
            Reading r; Kind kind;
            if (!TryRead(p, out r, out kind)) continue;
            seen.Add(key);
            string name = VendorDevices.Product(p) ?? "Wireless device";
            if (kind == Kind.Other) kind = GenericDevices.GuessKind(name);
            list.Add(new Found { Id = "hidbat:" + key, Name = name, Kind = kind, Reading = r });
        }
    }

    static bool TryRead(string path, out Reading r, out Kind kind)
    {
        r = null; kind = Kind.Other;
        using (var h = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) return false;
            IntPtr pd;
            if (!Native.HidD_GetPreparsedData(h, out pd)) return false;
            try
            {
                Native.HIDP_CAPS caps;
                if (Native.HidP_GetCaps(pd, out caps) != 0x110000) return false;
                if (caps.UsagePage == 1 && caps.Usage == 6) kind = Kind.Keyboard;
                else if (caps.UsagePage == 1 && caps.Usage == 2) kind = Kind.Mouse;
                else if (caps.UsagePage == 1 && (caps.Usage == 4 || caps.Usage == 5)) kind = Kind.Gamepad;

                foreach (int type in new[] { 2, 0 }) // feature reports first, then input reports
                {
                    int len = type == 2 ? caps.FeatureReportByteLength : caps.InputReportByteLength;
                    if (len == 0) continue;
                    foreach (var c in Caps(pd, type))
                    {
                        if (!((c.Page == 0x06 && c.Usage == 0x20) || (c.Page == 0x85 && (c.Usage == 0x65 || c.Usage == 0x64 || c.Usage == 0x66)))) continue;
                        var buf = new byte[len];
                        buf[0] = (byte)c.ReportId;
                        bool ok = type == 2 ? VendorDevices.HidD_GetFeature(h, buf, len) : HidD_GetInputReport(h, buf, len);
                        if (!ok) continue;
                        uint v;
                        if (HidP_GetUsageValue(type, (ushort)c.Page, (ushort)c.Link, (ushort)c.Usage, out v, pd, buf, (uint)len) != 0x110000) continue;
                        int lo = c.Min, hi = c.Max > c.Min ? c.Max : 100;
                        int pct = (int)Math.Round(((int)v - lo) * 100.0 / (hi - lo));
                        if (pct <= 0 || pct > 100) continue; // 0 is usually "unknown / not connected"
                        r = new Reading { Present = true, Percent = pct };
                        return true;
                    }
                }
            }
            catch { }
            finally { Native.HidD_FreePreparsedData(pd); }
        }
        return false;
    }

    // HIDP_VALUE_CAPS is 72 bytes; read the few fields we need from raw memory
    static List<Cap> Caps(IntPtr pd, int type)
    {
        var res = new List<Cap>();
        ushort n = 64;
        var raw = new byte[72 * n];
        if (HidP_GetValueCaps(type, raw, ref n, pd) != 0x110000) return res;
        for (int i = 0; i < n; i++)
        {
            int o = i * 72;
            res.Add(new Cap
            {
                Type = type,
                Page = BitConverter.ToUInt16(raw, o + 0),
                ReportId = raw[o + 2],
                Link = BitConverter.ToUInt16(raw, o + 6),
                Min = BitConverter.ToInt32(raw, o + 40),
                Max = BitConverter.ToInt32(raw, o + 44),
                Usage = BitConverter.ToUInt16(raw, o + 56), // NotRange.Usage / Range.UsageMin
            });
        }
        return res;
    }

    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort len, IntPtr pd);
    [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr pd, byte[] report, uint len);
    [DllImport("hid.dll")] static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buf, int len);
}

// ---------------- ASUS ROG / TUF wireless mice ----------------
static class AsusMice
{
    // pid, interface filter, report id, how the level is encoded (0: byte 5, 1: byte 5 x25, 2: byte 7 x25)
    struct Model { public int Pid; public string Filter; public byte Rid; public int Mode; }
    static Model M(int pid, string f, byte rid, int mode) { return new Model { Pid = pid, Filter = f, Rid = rid, Mode = mode }; }

    static readonly Model[] Models = {
        M(0x18A0, "mi_02", 0, 1), // Gladius II Wireless
        M(0x18B4, "mi_01", 0, 2), // Strix Carry
        M(0x18E5, "mi_00", 0, 1), // Chakram
        M(0x1908, "mi_00", 0, 1), // Pugio II
        M(0x1949, "mi_00", 0, 1), // Strix Impact II Wireless
        M(0x1960, "mi_00", 0, 1), // Keris Wireless
        M(0x1979, "mi_00", 0, 0), // Spatha X
        M(0x197F, "mi_00", 0, 0), // Gladius III Wireless
        M(0x19F4, "mi_00", 0, 0), // TUF M4 Wireless
        M(0x1A1A, "mi_00", 0, 0), // Chakram X
        M(0x1A24, "mi_02", 0, 0), // MD200
        M(0x1A68, "mi_00", 0, 0), // Keris Wireless Aimpoint
        M(0x1A72, "mi_00", 0, 0), // Gladius III Aimpoint
        M(0x1A94, "mi_00", 0, 0), // Harpe Ace Aim Lab
        M(0x1ACE, "mi_02&col03", 3, 0), // Strix Impact III Wireless
        M(0x1B16, "mi_00", 0, 0), // Keris II Ace
        M(0x1C0C, "mi_00", 0, 0), // Keris II Origin
        M(0x1CD3, "mi_03", 0, 0), // MD301
    };

    public static void Scan(List<Found> list)
    {
        var done = new HashSet<int>();
        foreach (var c in VendorDevices.Collections(0x0B05))
        {
            int pid = VendorDevices.Pid(c.Key);
            if (done.Contains(pid)) continue;
            int mi = Array.FindIndex(Models, x => x.Pid == pid);
            if (mi < 0) continue;
            var m = Models[mi];
            if (c.Key.ToLowerInvariant().IndexOf(m.Filter) < 0 || c.Value.OutputReportByteLength < 3) continue;
            var l = HidLink.Open(c.Key, c.Value.OutputReportByteLength);
            if (l == null) continue;
            try
            {
                l.Drain();
                l.Send(m.Rid, 0x12, 0x07);
                var r = l.Wait(b => b[0] == m.Rid && b[1] == 0x12 && b[2] == 0x07, 600);
                if (r == null) continue;
                int lvl = m.Mode == 0 ? r[5] : m.Mode == 1 ? r[5] * 25 : r[7] * 25;
                if (lvl <= 0 || lvl > 100) continue; // asleep / not connected
                done.Add(pid);
                list.Add(new Found
                {
                    Id = "asus:" + pid.ToString("x4"), Name = VendorDevices.Product(c.Key) ?? "ASUS mouse", Kind = Kind.Mouse,
                    Reading = new Reading { Present = true, Percent = lvl, Charging = r[10] > 0 }
                });
            }
            catch { }
            finally { l.Dispose(); }
        }
    }
}

// ---------------- Device report (Settings > Copy device report) ----------------
// A plain-text list of HID interfaces to help add support for a device that does not show up.
// Contains only vendor/product ids, interface numbers, report sizes and product names: no serial numbers or paths.
static class Diagnostics
{
    public static Func<List<string>> Shown; // devices the UI currently shows (set by the UI)

    public static string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine("BatteryRings " + System.Windows.Forms.Application.ProductVersion + " device report");
        sb.AppendLine("Windows " + Environment.OSVersion.Version);
        sb.AppendLine();
        sb.AppendLine("HID interfaces (vid:pid  mi  col  usage page:usage  in/out/feature  product):");
        var seen = new HashSet<string>();
        foreach (var p in Native.HidPaths("hid#"))
        {
            string lp = p.ToLowerInvariant();
            int vi = lp.IndexOf("vid_"); if (vi < 0) continue;
            string vid = lp.Substring(vi + 4, 4);
            int pid = VendorDevices.Pid(p);
            int mi = VendorDevices.Interface(p);
            int ci = lp.IndexOf("&col");
            string col = ci >= 0 ? lp.Substring(ci + 4, 2) : "--";
            Native.HIDP_CAPS c;
            if (!Native.GetCaps(p, out c)) continue;
            string line = string.Format("{0}:{1:x4}  {2}  {3}  {4:X4}:{5:X4}  {6}/{7}/{8}  {9}",
                vid, pid, mi < 0 ? "--" : mi.ToString("x2"), col, c.UsagePage, c.Usage,
                c.InputReportByteLength, c.OutputReportByteLength, c.FeatureReportByteLength, VendorDevices.Product(p) ?? "");
            if (seen.Add(line)) sb.AppendLine("  " + line);
        }
        sb.AppendLine();
        sb.AppendLine("Compx (373b / 3554) battery query:");
        foreach (var p in AtkMouse.CompxPaths())
        {
            Native.HIDP_CAPS c;
            if (!Native.GetCaps(p, out c) || c.UsagePage != 0xFF02 || c.OutputReportByteLength < 17) continue;
            var l = HidLink.Open(p, c.OutputReportByteLength);
            if (l == null) { sb.AppendLine("  pid " + VendorDevices.Pid(p).ToString("x4") + ": cannot open"); continue; }
            try
            {
                l.Drain();
                l.Send(8, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x49);
                var r = l.Wait(b => b[0] == 8 && b[1] == 4, 800);
                string vv = p.ToLowerInvariant().Contains("vid_3554") ? "3554:" : "373b:";
                sb.AppendLine("  " + vv + VendorDevices.Pid(p).ToString("x4") + " (" + c.OutputReportByteLength + " bytes): " +
                    (r == null ? "no answer" : BitConverter.ToString(r, 0, Math.Min(20, r.Length))));
            }
            catch (Exception e) { sb.AppendLine("  error: " + e.Message); }
            finally { l.Dispose(); }
        }
        sb.AppendLine();
        sb.AppendLine("Shown by BatteryRings:");
        try
        {
            var shown = Shown != null ? Shown() : null;
            if (shown != null) foreach (var x in shown) sb.AppendLine("  " + x);
            else foreach (var f in GenericDevices.Read()) sb.AppendLine("  " + f.Kind + "  " + f.Name + "  " + f.Reading.Percent + "%");
        }
        catch { }
        return sb.ToString();
    }

    // New-issue URL on GitHub with title and body filled in. Browsers and GitHub cap URL length,
    // so a long report is cut; the full report is then put on the clipboard to paste.
    public static string IssueUrl(string description, string report, out bool copiedFull)
    {
        copiedFull = false;
        string first = description.Split('\n')[0].Trim();
        if (first.Length > 70) first = first.Substring(0, 70) + "…";
        string body = "### What happened\n" + description + "\n";
        if (report.Length > 0) body += "\n### Device report\n```\n" + report + "```\n";
        const int max = 6000; // characters before encoding; keeps the URL well under browser limits
        if (body.Length > max && report.Length > 0)
        {
            try { System.Windows.Forms.Clipboard.SetText(report); copiedFull = true; } catch { }
            int keep = Math.Max(0, max - description.Length - 200);
            body = "### What happened\n" + description + "\n\n### Device report (shortened, full report is on the clipboard: paste it below)\n```\n"
                + report.Substring(0, Math.Min(keep, report.Length)) + "\n```\n";
        }
        return "https://github.com/blaidq-a/BatteryRings/issues/new?labels=bug&title=" + Uri.EscapeDataString(first)
            + "&body=" + Uri.EscapeDataString(body);
    }
}

// ---------------- PlayStation controllers ----------------
static class PlayStation
{
    public static void Scan(List<Found> list)
    {
        var done = new HashSet<int>();
        foreach (var p in Native.HidPaths("054c"))
        {
            string lp = p.ToLowerInvariant();
            if (!(lp.Contains("vid_054c") || lp.Contains("vid&0002054c"))) continue;
            bool bt = lp.Contains("vid&0002054c");
            int pi = lp.IndexOf(bt ? "pid&" : "pid_");
            int pid;
            if (pi < 0 || !int.TryParse(lp.Substring(pi + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out pid)) continue;
            bool sense = pid == 0x0CE6 || pid == 0x0DF2;                    // DualSense, DualSense Edge
            bool ds4 = pid == 0x05C4 || pid == 0x09CC || pid == 0x0BA0;    // DualShock 4 v1 / v2 / USB wireless adapter
            if ((!sense && !ds4) || done.Contains(pid)) continue;
            Native.HIDP_CAPS c;
            if (!Native.GetCaps(p, out c) || c.UsagePage != 1 || c.Usage != 5) continue;
            var r = Read(p, c, sense, bt);
            if (r == null) continue;
            done.Add(pid);
            string name = sense ? (pid == 0x0DF2 ? "DualSense Edge" : "DualSense") : "DualShock 4";
            list.Add(new Found { Id = "ps:" + pid.ToString("x4"), Name = name, Kind = Kind.Gamepad, Reading = r });
        }
    }

    static Reading Read(string path, Native.HIDP_CAPS c, bool sense, bool bt)
    {
        var l = HidLink.Open(path, Math.Max(1, (int)c.OutputReportByteLength));
        if (l == null) return null;
        try
        {
            if (bt && c.FeatureReportByteLength > 0)
            {
                // over Bluetooth the full report (with battery) is only sent after the calibration feature report is read
                using (var h = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    var f = new byte[c.FeatureReportByteLength];
                    f[0] = (byte)(sense ? 0x05 : 0x02);
                    VendorDevices.HidD_GetFeature(h, f, f.Length);
                }
            }
            byte want = (byte)(bt ? (sense ? 0x31 : 0x11) : 0x01);
            int at = sense ? (bt ? 54 : 53) : (bt ? 32 : 30);
            l.Drain();
            var d = l.Wait(b => b[0] == want, 1500);
            if (d == null || d.Length <= at) return null;
            int st = d[at];
            if (sense)
            {
                int lvl = st & 0x0F, chg = st >> 4;           // 0 discharging, 1 charging, 2 full
                return new Reading { Present = true, Percent = chg == 2 ? 100 : Math.Min(100, lvl * 10 + 5), Charging = chg == 1 };
            }
            else
            {
                int lvl = st & 0x0F; bool cable = (st & 0x10) != 0;
                if (!cable) return new Reading { Present = true, Percent = Math.Min(100, lvl * 10 + 5) };
                return new Reading { Present = true, Percent = lvl >= 10 ? 100 : lvl * 10 + 5, Charging = lvl < 11 };
            }
        }
        catch { return null; }
        finally { l.Dispose(); }
    }
}

// ---------------- HyperX Cloud II Wireless ----------------
// Two dongle generations with different framing (from HeadsetControl):
//   HP-branded    03F0:0696 / 03F0:018B   request 06 FF BB <cmd>            reply 06 FF BB <cmd> ...
//   Kingston      0951:1718               request 06 00 02 00 9A 00 00 68 4A 8E 0A 00 00 00 BB <cmd>   reply 0B ?? BB <cmd> ...
//   cmd 0x02: battery level at byte 7;  cmd 0x03: charging (1) at byte 4.   Not tested on real hardware here.
static class HyperXCloud2
{
    public static void Scan(List<Found> list)
    {
        foreach (var vid in new[] { 0x03F0, 0x0951 })
        {
            bool kingston = vid == 0x0951;
            foreach (var c in VendorDevices.Collections(vid))
            {
                int pid = VendorDevices.Pid(c.Key);
                if (kingston ? pid != 0x1718 : (pid != 0x0696 && pid != 0x018B)) continue;
                if (c.Value.UsagePage < 0xFF00 || c.Value.OutputReportByteLength < 17) continue;
                var r = Read(c.Key, c.Value, kingston);
                if (r == null) continue;
                list.Add(new Found { Id = "hyperx-c2:" + pid.ToString("x4"), Name = VendorDevices.Product(c.Key) ?? "HyperX Cloud II Wireless", Kind = Kind.Headset, Reading = r });
                break;
            }
        }
    }

    static Reading Read(string path, Native.HIDP_CAPS caps, bool kingston)
    {
        var l = HidLink.Open(path, caps.OutputReportByteLength);
        if (l == null) return null;
        try
        {
            var lvl = Ask(l, path, caps, kingston, 0x02);
            if (lvl == null || lvl.Length < 8 || lvl[7] > 100) return null;
            var chg = Ask(l, path, caps, kingston, 0x03);
            if (lvl[7] == 0 && (chg == null || chg[4] != 1)) return null; // headset off
            return new Reading { Present = true, Percent = lvl[7], Charging = chg != null && chg.Length > 4 && chg[4] == 1 };
        }
        catch { return null; }
        finally { l.Dispose(); }
    }

    static byte[] Ask(HidLink l, string path, Native.HIDP_CAPS caps, bool kingston, byte cmd)
    {
        l.Drain();
        if (kingston)
        {
            // the Kingston dongle wants an input report read before each command
            try
            {
                using (var h = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    var ir = new byte[Math.Max(64, (int)caps.InputReportByteLength)];
                    ir[0] = 0x06;
                    VendorDevices.HidD_GetInputReport(h, ir, caps.InputReportByteLength);
                }
            }
            catch { }
            l.Send(0x06, 0x00, 0x02, 0x00, 0x9A, 0x00, 0x00, 0x68, 0x4A, 0x8E, 0x0A, 0x00, 0x00, 0x00, 0xBB, cmd, 0x00);
            return l.Wait(b => b[0] == 0x0B && b[2] == 0xBB && b[3] == cmd, 1000);
        }
        l.Send(0x06, 0xFF, 0xBB, cmd, 0x00);
        return l.Wait(b => b[0] == 0x06 && b[1] == 0xFF && b[2] == 0xBB && b[3] == cmd, 1000);
    }
}

// ---------------- HyperX Cloud III Wireless / Cloud III S Wireless ----------------
// Protocols from HyperHeadset (LennardKittner/HyperHeadset), which captured them from HyperX's own software.
//   Cloud III Wireless    03F0:05B7 / 03F0:0C9D   62-byte report 0x66:  66 <cmd>
//       cmd 0x89 battery -> 66 89 s1 s2 level   (s1 = s2 = 0: headset off)
//       cmd 0x8A charging -> 66 8A state        (0 no, 1 charging, 2 full)
//   Cloud III S Wireless  03F0:06BE / 03F0:02CC   64-byte report 0x0C:  0C 02 03 01 00 <cmd>
//       reply 0C .. .. .. .. <cmd> value        (value 0xFF: no answer / headset off)
//       cmd 0x06 battery, cmd 0x48 charging state
// Not tested on real hardware here.
static class HyperXCloud3
{
    public static void Scan(List<Found> list)
    {
        foreach (var c in VendorDevices.Collections(0x03F0))
        {
            int pid = VendorDevices.Pid(c.Key);
            bool s = pid == 0x06BE || pid == 0x02CC;
            if (!s && pid != 0x05B7 && pid != 0x0C9D) continue;
            if (c.Value.UsagePage < 0xFF00 || c.Value.OutputReportByteLength < (s ? 64 : 62)) continue;
            var r = Read(c.Key, c.Value, s);
            if (r == null) continue;
            string def = s ? "HyperX Cloud III S Wireless" : "HyperX Cloud III Wireless";
            list.Add(new Found { Id = "hyperx-c3:" + pid.ToString("x4"), Name = VendorDevices.Product(c.Key) ?? def, Kind = Kind.Headset, Reading = r });
            break;
        }
    }

    static Reading Read(string path, Native.HIDP_CAPS caps, bool s)
    {
        var l = HidLink.Open(path, caps.OutputReportByteLength);
        if (l == null) return null;
        try
        {
            int level, chg;
            if (s)
            {
                var b = Ask(l, new byte[] { 0x0C, 0x02, 0x03, 0x01, 0x00, 0x06 }, x => x[0] == 0x0C && x[5] == 0x06);
                if (b == null || b[6] > 100) return null;
                level = b[6];
                var c = Ask(l, new byte[] { 0x0C, 0x02, 0x03, 0x01, 0x00, 0x48 }, x => x[0] == 0x0C && x[5] == 0x48);
                chg = c != null && c[6] != 0xFF ? c[6] : 0;
            }
            else
            {
                var b = Ask(l, new byte[] { 0x66, 0x89 }, x => x[0] == 0x66 && x[1] == 0x89);
                if (b == null || (b[2] == 0 && b[3] == 0) || b[4] > 100) return null; // headset off
                level = b[4];
                var c = Ask(l, new byte[] { 0x66, 0x8A }, x => x[0] == 0x66 && x[1] == 0x8A);
                chg = c != null ? c[2] : 0;
            }
            return new Reading { Present = true, Percent = level, Charging = chg == 1 };
        }
        catch { return null; }
        finally { l.Dispose(); }
    }

    static byte[] Ask(HidLink l, byte[] req, Func<byte[], bool> ok)
    {
        l.Drain();
        l.Send(req);
        return l.Wait(x => x.Length > 6 && ok(x), 1000);
    }
}
