// Samsung Galaxy Buds (all models): left / right / case battery over Bluetooth RFCOMM,
// following GalaxyBudsClient and Gadgetbridge. The buds push a status message after the link opens
// and whenever something changes, so we only listen.
//   Galaxy Buds (2019):  service 00001102-0000-1000-8000-00805f9b34fd, frame FE | type | size | id | payload | crc16 | EE
//   Buds+ and later:     service 2e73a4ad-332d-41fc-90e2-16bef06523f2, frame FD | size (LE16, low 10 bits) | id | payload | crc16 | DD
//   id 0x60 status: legacy L=p[1] R=p[2]; newer L=p[1] R=p[2] case=p[6]
//   id 0x61 extended status: L=p[2] R=p[3]; newer case=p[7]
// Not tested on real hardware here.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

static class GalaxyBuds
{
    static readonly Guid SppLegacy = new Guid("00001102-0000-1000-8000-00805f9b34fd");
    static readonly Guid SppNew = new Guid("2e73a4ad-332d-41fc-90e2-16bef06523f2");

    static readonly object lk = new object();
    static IntPtr sock = new IntPtr(-1);
    static ulong addr;
    static string devName;
    static bool legacy;
    static DateTime nextTry = DateTime.MinValue, lastData = DateTime.MinValue;
    static int left = -1, right = -1, cas = -1;

    public static bool IsGalaxyBuds(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        return n.Contains("galaxy buds") || n.Contains("buds2") || n.Contains("buds3") || n.Contains("buds pro")
            || n.Contains("buds live") || n.Contains("buds fe") || n.Contains("buds+");
    }

    public static void Read(List<Found> list)
    {
        ulong a = 0; string name = null;
        foreach (var kv in CmfBuds.ConnectedClassic())
            if (IsGalaxyBuds(kv.Value)) { a = Convert.ToUInt64(kv.Key, 16); name = kv.Value; break; }
        if (name == null) { Close(); lock (lk) { left = right = cas = -1; } return; }

        if (Valid && a != addr) Close();
        if (!Valid && DateTime.Now >= nextTry)
        {
            legacy = name.StartsWith("Galaxy Buds (", StringComparison.OrdinalIgnoreCase); // the 2019 model
            Open(a, legacy ? SppLegacy : SppNew);
        }
        devName = name;

        var r = new Reading { Present = true };
        lock (lk)
        {
            if (left < 0 && right < 0) return;
            r.Parts = new List<Part> {
                new Part { Name = "Left", Percent = left },
                new Part { Name = "Right", Percent = right },
                new Part { Name = "Case", Percent = cas } };
            int min = 101;
            if (left >= 0) min = Math.Min(min, left);
            if (right >= 0) min = Math.Min(min, right);
            r.Percent = min <= 100 ? min : -1;
        }
        if (r.Percent >= 0) list.Add(new Found { Id = "galaxy:" + name, Name = name, Kind = Kind.Buds, Reading = r });
    }

    static bool Valid { get { return sock != new IntPtr(-1); } }

    static void Open(ulong a, Guid service)
    {
        nextTry = DateTime.Now.AddSeconds(30);
        WSAStartup(0x0202, new byte[512]);
        IntPtr s = socket(32, 1, 3); // AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM
        if (s == new IntPtr(-1)) return;
        var sa = new byte[30];
        sa[0] = 32;
        BitConverter.GetBytes(a).CopyTo(sa, 2);
        service.ToByteArray().CopyTo(sa, 10);
        if (connect(s, sa, 30) != 0) { closesocket(s); return; }
        sock = s; addr = a;
        new Thread(() => RxLoop(s, legacy)) { IsBackground = true }.Start();
    }

    static void Close()
    {
        IntPtr s = sock;
        sock = new IntPtr(-1);
        if (s != new IntPtr(-1)) closesocket(s);
    }

    static void RxLoop(IntPtr s, bool old)
    {
        var buf = new List<byte>();
        var tmp = new byte[2048];
        byte som = (byte)(old ? 0xFE : 0xFD), eom = (byte)(old ? 0xEE : 0xDD);
        while (true)
        {
            int n = recv(s, tmp, tmp.Length, 0);
            if (n <= 0) break;
            for (int i = 0; i < n; i++) buf.Add(tmp[i]);
            while (true)
            {
                int st = buf.IndexOf(som);
                if (st < 0) { buf.Clear(); break; }
                if (st > 0) buf.RemoveRange(0, st);
                if (buf.Count < 4) break;
                int size = old ? buf[2] : (buf[1] | buf[2] << 8) & 0x3FF;  // id + payload + crc
                int total = 3 + size + 1;
                if (size < 3 || size > 1000) { buf.RemoveAt(0); continue; }
                if (buf.Count < total) break;
                if (buf[total - 1] != eom) { buf.RemoveAt(0); continue; }
                int id = buf[3];
                byte[] p = buf.GetRange(4, size - 3).ToArray();
                buf.RemoveRange(0, total);
                Parse(id, p, old);
            }
        }
        lock (lk) { if (sock == s) sock = new IntPtr(-1); }
        closesocket(s);
    }

    static void Parse(int id, byte[] p, bool old)
    {
        int l = -1, r = -1, c = -1;
        if (id == 0x60 && p.Length >= 3) { l = p[1]; r = p[2]; if (!old && p.Length > 6) c = p[6]; }
        else if (id == 0x61 && p.Length >= 4) { l = p[2]; r = p[3]; if (!old && p.Length > 7) c = p[7]; }
        else return;
        Func<int, int> ok = v => v >= 0 && v <= 100 ? v : -1;
        lock (lk)
        {
            left = ok(l); right = ok(r);
            if (c >= 0) cas = ok(c);
            lastData = DateTime.Now;
        }
    }

    [DllImport("ws2_32.dll")] static extern int WSAStartup(ushort v, byte[] data);
    [DllImport("ws2_32.dll")] static extern IntPtr socket(int af, int type, int proto);
    [DllImport("ws2_32.dll")] static extern int connect(IntPtr s, byte[] addr, int len);
    [DllImport("ws2_32.dll")] static extern int recv(IntPtr s, byte[] buf, int len, int flags);
    [DllImport("ws2_32.dll")] static extern int closesocket(IntPtr s);
}
