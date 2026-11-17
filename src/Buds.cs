// CMF Buds Pro 2 (Nothing) - sag / sol / kutu pil seviyesi.
// Windows sadece tek bir yuzde veriyor; kulakligin kendi protokolu (Nothing X uygulamasinin kullandigi,
// BudsLink / Gadgetbridge projelerinde belgelenen) RFCOMM uzerinden ayri ayri veriyor:
//   servis UUID aeac4a03-dff5-498f-843a-34487cf133eb
//   paket: 55 60 01 | tip(LE16) | uzunluk(LE16) | sira | veri | CRC16/MODBUS(LE)
//   pil istegi tip 0xC007, cevap/bildirim 0x4007 veya 0xE001: [adet] ([cihaz] [seviye|0x80=sarj])...
//   cihaz: 02 sol, 03 sag, 04 kutu
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

class Part
{
    public string Name;
    public int Percent = -1;
    public bool Charging;
}

class CmfBuds : IDisposable
{
    public event Action Changed;
    static readonly Guid Service = new Guid("aeac4a03-dff5-498f-843a-34487cf133eb");
    const string NameMatch = "CMF Buds";

    public string DeviceName = "CMF Buds";
    readonly object lk = new object();
    IntPtr sock = new IntPtr(-1);
    ulong addr;
    byte seq;
    DateTime nextTry = DateTime.MinValue, lastAsk = DateTime.MinValue, lastBat = DateTime.MinValue;
    int left = -1, right = -1, cas = -1;
    bool lc, rc, cc;
    readonly AutoResetEvent got = new AutoResetEvent(false);

    public Reading Read()
    {
        ulong a; string name;
        if (!FindConnected(out a, out name)) { Close(); lock (lk) { left = right = cas = -1; lastBat = DateTime.MinValue; } return new Reading { Present = false }; }
        DeviceName = name;
        if (Valid && a != addr) Close();
        if (!Valid && DateTime.Now >= nextTry) Open(a);
        if (Valid && (DateTime.Now - lastAsk).TotalSeconds >= 15)
        {
            lastAsk = DateTime.Now;
            got.Reset();
            if (Send(0xC007)) got.WaitOne(1500); else Close();
        }

        var r = new Reading { Present = true };
        lock (lk)
        {
            if ((DateTime.Now - lastBat).TotalSeconds > 90) return r; // Percent -1: bilinmiyor
            r.Parts = new List<Part> {
                new Part { Name = "Sol", Percent = left, Charging = lc },
                new Part { Name = "Sağ", Percent = right, Charging = rc },
                new Part { Name = "Kutu", Percent = cas, Charging = cc } };
            int min = 101; bool chg = false;
            foreach (var p in new[] { r.Parts[0], r.Parts[1] })
                if (p.Percent >= 0) { min = Math.Min(min, p.Percent); chg |= p.Charging; }
            r.Percent = min <= 100 ? min : -1;
            r.Charging = chg;
        }
        return r;
    }

    bool Valid { get { return sock != new IntPtr(-1); } }

    void Open(ulong a)
    {
        nextTry = DateTime.Now.AddSeconds(20);
        WSAStartup(0x0202, new byte[512]);
        IntPtr s = socket(32, 1, 3); // AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM
        if (s == new IntPtr(-1)) return;
        var sa = new byte[30];
        sa[0] = 32;
        BitConverter.GetBytes(a).CopyTo(sa, 2);
        Service.ToByteArray().CopyTo(sa, 10);
        if (connect(s, sa, 30) != 0) { closesocket(s); return; }
        sock = s; addr = a; lastAsk = DateTime.MinValue;
        var t = new Thread(() => RxLoop(s)) { IsBackground = true };
        t.Start();
    }

    void Close()
    {
        IntPtr s = sock;
        sock = new IntPtr(-1);
        if (s != new IntPtr(-1)) closesocket(s);
    }

    bool Send(ushort type)
    {
        seq = (byte)(seq >= 250 ? 1 : seq + 1);
        var p = new byte[] { 0x55, 0x60, 0x01, (byte)type, (byte)(type >> 8), 0, 0, seq, 0, 0 };
        ushort c = Crc(p, 8); p[8] = (byte)c; p[9] = (byte)(c >> 8);
        return send(sock, p, p.Length, 0) == p.Length;
    }

    static ushort Crc(byte[] b, int n)
    {
        int crc = 0xFFFF;
        for (int i = 0; i < n; i++)
        {
            crc ^= b[i];
            for (int j = 0; j < 8; j++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return (ushort)crc;
    }

    void RxLoop(IntPtr s)
    {
        var buf = new List<byte>();
        var tmp = new byte[1024];
        while (true)
        {
            int n = recv(s, tmp, tmp.Length, 0);
            if (n <= 0) break;
            for (int i = 0; i < n; i++) buf.Add(tmp[i]);
            while (true)
            {
                int st = buf.IndexOf(0x55);
                if (st < 0) { buf.Clear(); break; }
                if (st > 0) buf.RemoveRange(0, st);
                if (buf.Count < 8) break;
                bool crc = (buf[1] & 0x20) != 0;
                int len = buf[5] | buf[6] << 8;
                int total = 8 + len + (crc ? 2 : 0);
                if (len > 1000) { buf.RemoveAt(0); continue; }
                if (buf.Count < total) break;
                int type = buf[3] | buf[4] << 8;
                byte[] pay = buf.GetRange(8, len).ToArray();
                buf.RemoveRange(0, total);
                if (type == 0x4007 || type == 0xE001) Battery(pay);
            }
        }
        lock (lk) { if (sock == s) sock = new IntPtr(-1); }
        closesocket(s);
    }

    void Battery(byte[] p)
    {
        if (p.Length < 1) return;
        int l = -1, r = -1, c = -1; bool lcx = false, rcx = false, ccx = false;
        for (int i = 0; i < p[0] && 2 + i * 2 < p.Length; i++)
        {
            int id = p[1 + i * 2], v = p[2 + i * 2];
            int lvl = v & 0x7F; bool ch = (v & 0x80) != 0;
            if (id == 2) { l = lvl; lcx = ch; }
            else if (id == 3) { r = lvl; rcx = ch; }
            else if (id == 4) { c = lvl; ccx = ch; }
            else if (id == 6) { l = r = lvl; lcx = rcx = ch; }
        }
        bool diff;
        lock (lk)
        {
            diff = l != left || r != right || c != cas || lcx != lc || rcx != rc || ccx != cc;
            left = l; right = r; cas = c; lc = lcx; rc = rcx; cc = ccx;
            lastBat = DateTime.Now;
        }
        got.Set();
        var h = Changed;
        if (diff && h != null) h();
    }

    // Eslesmis ve su an bagli bir CMF Buds var mi?
    static bool FindConnected(out ulong a, out string name)
    {
        a = 0; name = null;
        var sp = new SEARCH { dwSize = Marshal.SizeOf(typeof(SEARCH)), fReturnAuthenticated = true, fReturnRemembered = true, fReturnConnected = true };
        var di = new DEVINFO { dwSize = Marshal.SizeOf(typeof(DEVINFO)) };
        IntPtr h = BluetoothFindFirstDevice(ref sp, ref di);
        if (h == IntPtr.Zero) return false;
        try
        {
            do
            {
                if (di.fConnected && di.szName != null && di.szName.IndexOf(NameMatch, StringComparison.OrdinalIgnoreCase) >= 0)
                { a = di.Address; name = di.szName; return true; }
                di.dwSize = Marshal.SizeOf(typeof(DEVINFO));
            } while (BluetoothFindNextDevice(h, ref di));
        }
        finally { BluetoothFindDeviceClose(h); }
        return false;
    }

    public void Dispose() { Close(); }

    [StructLayout(LayoutKind.Sequential)]
    struct SEARCH
    {
        public int dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated, fReturnRemembered, fReturnUnknown, fReturnConnected, fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVINFO
    {
        public int dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected, fRemembered, fAuthenticated;
        public SYSTEMTIME stLastSeen, stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEMTIME { public short y, mo, dow, d, h, mi, s, ms; }

    [DllImport("bthprops.cpl")] static extern IntPtr BluetoothFindFirstDevice(ref SEARCH p, ref DEVINFO d);
    [DllImport("bthprops.cpl")] static extern bool BluetoothFindNextDevice(IntPtr h, ref DEVINFO d);
    [DllImport("bthprops.cpl")] static extern bool BluetoothFindDeviceClose(IntPtr h);
    [DllImport("ws2_32.dll")] static extern int WSAStartup(ushort v, byte[] data);
    [DllImport("ws2_32.dll")] static extern IntPtr socket(int af, int type, int proto);
    [DllImport("ws2_32.dll")] static extern int connect(IntPtr s, byte[] addr, int len);
    [DllImport("ws2_32.dll")] static extern int send(IntPtr s, byte[] buf, int len, int flags);
    [DllImport("ws2_32.dll")] static extern int recv(IntPtr s, byte[] buf, int len, int flags);
    [DllImport("ws2_32.dll")] static extern int closesocket(IntPtr s);
}
