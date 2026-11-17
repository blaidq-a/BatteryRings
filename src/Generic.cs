// Ozel kod gerektirmeyen cihazlar:
//   - Bluetooth: Windows'un Ayarlar'da gosterdigi pil yuzdesi (aygit ozelligi {104EA319-...} 2).
//   - Xbox / XInput oyun kollari: XInputGetBatteryInformation (4 kademeli seviye).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

class Found
{
    public string Id, Name;
    public Kind Kind;
    public Reading Reading;
}

static class GenericDevices
{
    static readonly DEVPROPKEY BatteryKey = new DEVPROPKEY { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
    static readonly DEVPROPKEY NameKey = new DEVPROPKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 }; // FriendlyName


    public static List<Found> Read()
    {
        var list = new List<Found>();
        try { list.AddRange(VendorDevices.Snapshot()); } catch { }
        var vendorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in list) vendorNames.Add(f.Name);
        try { AppleBuds.Read(list, CmfBuds.ConnectedClassic()); } catch { } // AirPods / Beats: left, right, case
        try { GalaxyBuds.Read(list); } catch { }                            // Samsung Galaxy Buds: left, right, case
        foreach (var f in list) vendorNames.Add(f.Name);
        var bt = new List<Found>();
        try { Bluetooth(bt); } catch { }
        foreach (var f in bt) if (!vendorNames.Contains(f.Name)) list.Add(f); // same device seen twice: keep the detailed one
        try { XInput(list); } catch { }
        return list;
    }

    // ---------------- Bluetooth ----------------

    static void Bluetooth(List<Found> list)
    {
        var connected = CmfBuds.ConnectedClassic(); // adres -> ad
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 0x2 | 0x4); // PRESENT | ALLCLASSES
        if (set == new IntPtr(-1)) return;
        try
        {
            var seen = new HashSet<string>();
            var di = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
            for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref di); i++)
            {
                string inst = InstanceId(set, ref di);
                if (inst == null || !inst.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] v = Prop(set, ref di, BatteryKey);
                if (v == null || v.Length < 1 || v[0] > 100) continue;

                string addr = Address(inst);
                bool ble = inst.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase);
                string name;
                if (!ble)
                {
                    // klasik Bluetooth: yalnizca su an bagli olanlar
                    if (addr == null || !connected.TryGetValue(addr, out name)) continue;
                }
                else
                {
                    byte[] n = Prop(set, ref di, NameKey);
                    name = n != null ? Encoding.Unicode.GetString(n).TrimEnd('\0') : "Bluetooth device";
                }
                if (addr == null || !seen.Add(addr)) continue;
                bool skip = false;
                if (CmfBuds.IsNothing(name)) skip = true;
                if (skip) continue;

                list.Add(new Found
                {
                    Id = "bt:" + addr,
                    Name = name,
                    Kind = GuessKind(name),
                    Reading = new Reading { Present = true, Percent = v[0] }
                });
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public static Kind GuessKind(string n)
    {
        n = n.ToLowerInvariant();
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("joy")) return Kind.Gamepad;
        if (n.Contains("mouse") || n.Contains("mx ")) return Kind.Mouse;
        if (n.Contains("keyboard") || n.Contains("klavye") || n.Contains("keys")) return Kind.Keyboard;
        if (n.Contains("buds") || n.Contains("pods") || n.Contains("ear") || n.Contains("tws")) return Kind.Buds;
        if (n.Contains("head") || n.Contains("phone") || n.Contains("wh-") || n.Contains("kulak")) return Kind.Headset;
        return Kind.Other;
    }

    // Aygit kimligindeki 12 haneli Bluetooth adresi (BTHENUM\..._001A7DDA7113_... gibi)
    static string Address(string inst)
    {
        string u = inst.ToUpperInvariant();
        for (int i = 0; i + 12 <= u.Length; i++)
        {
            if (i > 0 && IsHex(u[i - 1])) continue;
            int j = i;
            while (j < u.Length && j - i < 12 && IsHex(u[j])) j++;
            if (j - i == 12 && (j == u.Length || !IsHex(u[j])) && u.Substring(i, 12) != "000000000000") return u.Substring(i, 12);
        }
        return null;
    }
    static bool IsHex(char c) { return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'); }

    static string InstanceId(IntPtr set, ref SP_DEVINFO_DATA di)
    {
        var sb = new StringBuilder(512);
        int req;
        return SetupDiGetDeviceInstanceId(set, ref di, sb, sb.Capacity, out req) ? sb.ToString() : null;
    }

    static byte[] Prop(IntPtr set, ref SP_DEVINFO_DATA di, DEVPROPKEY key)
    {
        var buf = new byte[512];
        int type, req;
        if (!SetupDiGetDeviceProperty(set, ref di, ref key, out type, buf, buf.Length, out req, 0)) return null;
        var r = new byte[Math.Min(req, buf.Length)];
        Array.Copy(buf, r, r.Length);
        return r;
    }

    // ---------------- XInput (Xbox ve uyumlu kollar) ----------------

    static void XInput(List<Found> list)
    {
        for (int i = 0; i < 4; i++)
        {
            var caps = new byte[64];
            if (XInputGetCapabilities(i, 1, caps) != 0) continue; // bagli degil
            XINPUT_BATTERY b;
            if (XInputGetBatteryInformation(i, 0, out b) != 0) continue;
            // 0: bagli degil, 1: kablolu (pil yok ya da kablodan besleniyor), 0xFF: bilinmiyor -> gosterme
            if (b.Type != 2 && b.Type != 3) continue;
            var r = new Reading { Present = true };
            int[] pct = { 5, 30, 65, 100 };
            string[] txt = { "Empty", "Low", "Medium", "Full" };
            int lv = Math.Min(3, (int)b.Level);
            r.Percent = pct[lv]; r.Text = txt[lv];
            list.Add(new Found { Id = "xinput:" + i, Name = "Controller " + (i + 1), Kind = Kind.Gamepad, Reading = r });
        }
    }

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_BATTERY { public byte Type, Level; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(IntPtr guid, string enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, int idx, ref SP_DEVINFO_DATA di);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA di, StringBuilder id, int size, out int req);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA di, ref DEVPROPKEY key, out int type, byte[] buf, int size, out int req, int flags);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("xinput1_4.dll")]
    static extern int XInputGetCapabilities(int user, int flags, byte[] caps);
    [DllImport("xinput1_4.dll")]
    static extern int XInputGetBatteryInformation(int user, byte devType, out XINPUT_BATTERY b);
}
