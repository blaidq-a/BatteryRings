// AirPods and Beats: left / right / case battery from Apple's "proximity pairing" Bluetooth LE advertisement
// (manufacturer data, company 0x004C, packet type 0x07), as decoded by AirPodsDesktop and OpenPods.
// Battery is in steps of 10%. Every nearby AirPods broadcasts this, so we only use it while an
// AirPods/Beats device is connected to this PC, and take the strongest (closest) signal.
// Not tested on real hardware here.
using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

static class AppleBuds
{
    class Adv { public DateTime At; public short Rssi; public byte[] Data; }

    static readonly object lk = new object();
    static readonly List<Adv> recent = new List<Adv>();
    static BluetoothLEAdvertisementWatcher watcher;
    static DateTime lastWanted = DateTime.MinValue;

    static bool IsApple(string name)
    {
        string n = name.ToLowerInvariant();
        return n.Contains("airpods") || n.Contains("beats") || n.Contains("powerbeats");
    }

    public static void Read(List<Found> list, Dictionary<string, string> connected)
    {
        string name = null;
        foreach (var kv in connected) if (IsApple(kv.Value)) { name = kv.Value; break; }
        if (name == null)
        {
            // nothing to look for: stop scanning a while after the AirPods went away
            if (watcher != null && (DateTime.Now - lastWanted).TotalMinutes > 2) Stop();
            return;
        }
        lastWanted = DateTime.Now;
        Start();

        Adv best = null;
        lock (lk)
        {
            recent.RemoveAll(a => (DateTime.Now - a.At).TotalSeconds > 30);
            foreach (var a in recent) if (a.Rssi >= -75 && (best == null || a.Rssi > best.Rssi)) best = a;
        }
        if (best == null) return;

        var d = best.Data;
        int model = d[3] | d[4] << 8;
        bool leftIsCurr = (d[5] & 0x20) != 0;            // which bud sent this advertisement
        int curr = d[6] & 0x0F, anot = d[6] >> 4, cas = d[7] & 0x0F;
        bool currChg = (d[7] & 0x10) != 0, anotChg = (d[7] & 0x20) != 0, caseChg = (d[7] & 0x40) != 0;
        Func<int, int> pct = v => v <= 10 ? Math.Min(100, v * 10) : -1;

        var r = new Reading { Present = true };
        if (IsHeadphones(model))
        {
            r.Percent = pct(curr); r.Charging = currChg;
        }
        else
        {
            int l = pct(leftIsCurr ? curr : anot), rt = pct(leftIsCurr ? anot : curr);
            bool lc = leftIsCurr ? currChg : anotChg, rc = leftIsCurr ? anotChg : currChg;
            r.Parts = new List<Part> {
                new Part { Name = "Left", Percent = l, Charging = lc },
                new Part { Name = "Right", Percent = rt, Charging = rc },
                new Part { Name = "Case", Percent = pct(cas), Charging = caseChg } };
            int min = 101;
            if (l >= 0) min = Math.Min(min, l);
            if (rt >= 0) min = Math.Min(min, rt);
            r.Percent = min <= 100 ? min : -1;
            r.Charging = (l >= 0 && lc) || (rt >= 0 && rc);
        }
        if (r.Percent < 0) return;
        list.Add(new Found { Id = "apple:" + name, Name = name, Kind = IsHeadphones(model) ? Kind.Headset : Kind.Buds, Reading = r });
    }

    // AirPods Max, Beats Solo / Studio: one battery
    static bool IsHeadphones(int m) { return m == 0x200A || m == 0x201F || m == 0x200C || m == 0x2006 || m == 0x2009 || m == 0x2017; }

    static void Start()
    {
        if (watcher != null) return;
        try
        {
            var w = new BluetoothLEAdvertisementWatcher();
            w.ScanningMode = BluetoothLEScanningMode.Passive;
            w.Received += OnReceived;
            w.Start();
            watcher = w;
        }
        catch { watcher = null; } // no Bluetooth LE / older Windows
    }

    static void Stop()
    {
        try { if (watcher != null) watcher.Stop(); } catch { }
        watcher = null;
        lock (lk) recent.Clear();
    }

    static void OnReceived(BluetoothLEAdvertisementWatcher s, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        try
        {
            foreach (var m in e.Advertisement.ManufacturerData)
            {
                if (m.CompanyId != 0x004C || m.Data.Length < 10) continue;
                var b = new byte[m.Data.Length];
                DataReader.FromBuffer(m.Data).ReadBytes(b);
                if (b[0] != 0x07 || b[1] != b.Length - 2) continue; // proximity pairing packet
                lock (lk)
                {
                    recent.Add(new Adv { At = DateTime.Now, Rssi = e.RawSignalStrengthInDBm, Data = b });
                    if (recent.Count > 200) recent.RemoveRange(0, 100);
                }
            }
        }
        catch { }
    }
}
