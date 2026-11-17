// BatteryRings: kablosuz cihazlarin pil seviyesini gorev cubugunda tek bir halka simgesiyle gosterir.
// Tek exe: ilk calistirmada kendini %LOCALAPPDATA%\Programs\BatteryRings altina kurar,
// Windows ile baslatir ve Baslat menusune kisayol ekler. Yeni surumu calistirmak eskisinin uzerine kurar.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("BatteryRings")]
[assembly: AssemblyDescription("Shows the battery levels of wireless devices in the Windows taskbar")]
[assembly: AssemblyProduct("BatteryRings")]
[assembly: AssemblyCompany("BatteryRings")]
[assembly: AssemblyCopyright("GPL-3.0")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]
// Guvenlik: P/Invoke DLL'leri yalnizca System32'den yuklenir (exe'nin yanina konan sahte DLL calismaz)
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

static class Program
{
    public const string AppName = "BatteryRings";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    static readonly string InstalledExe = Path.Combine(InstallDir, AppName + ".exe");
    static readonly string Shortcut = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    public static bool IsInstalled
    {
        get { return string.Equals(Path.GetFullPath(Application.ExecutablePath), InstalledExe, StringComparison.OrdinalIgnoreCase); }
    }

    [STAThread]
    static void Main(string[] args)
    {
        // Guvenlik: sonradan yuklenen DLL'ler icin de uygulama klasoru / calisma klasoru aranmasin
        try { SetDefaultDllDirectories(0x800); SetDllDirectory(""); } catch { }
        Native.SetProcessDPIAware();
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (mode == "/test") { SelfTest(); return; }
        if (mode == "/render")
        {
            LayeredWindow.DumpDir = args.Length > 1 ? args[1] : ".";
            Application.Run(new BatteryUI());
            return;
        }
        Application.EnableVisualStyles();
        if (mode == "/install") { Install(true); return; }
        if (mode == "/uninstall") { Uninstall(true); return; }

        if (!IsInstalled && mode != "/portable")
        {
            var ans = MessageBox.Show(
                "Install BatteryRings on this PC?\n\n" +
                "It will start with Windows and appear in the Start menu.\n" +
                "Choose No to just run it this time without installing.",
                AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (ans == DialogResult.Cancel) return;
            if (ans == DialogResult.Yes) { Install(false); return; }
        }

        CleanupLegacy();
        bool created;
        using (new Mutex(true, "BarracudaPil_tek", out created))
        {
            if (!created) return;
            Application.Run(new BatteryUI());
        }
    }

    static void SelfTest()
    {
        var h = new Barracuda().Read();
        var m = new AtkMouse().Read();
        var bd = new CmfBuds(); var b = bd.Read(); bd.Dispose();
        string bp = ""; if (b.Parts != null) foreach (var p in b.Parts) bp += " " + p.Name + "=" + p.Percent + (p.Charging ? "+" : "");
        File.WriteAllText("test_sonuc.txt",
            "kulaklik present=" + h.Present + " pil=" + h.Percent + " sarj=" + h.Charging + Environment.NewLine +
            "mouse present=" + m.Present + " pil=" + m.Percent + " sarj=" + m.Charging + " mv=" + m.MilliVolt + Environment.NewLine +
            "buds present=" + b.Present + " pil=" + b.Percent + bp + Environment.NewLine +
            GenericLines());
    }

    static string GenericLines()
    {
        string t = "";
        var v = new System.Collections.Generic.List<Found>();
        var sw = Stopwatch.StartNew();
        try { Logitech.Scan(v); } catch (Exception e) { t += "logitech hata: " + e.Message + Environment.NewLine; }
        try { RazerMice.Scan(v); } catch (Exception e) { t += "razer hata: " + e.Message + Environment.NewLine; }
        try { Headsets.Scan(v); } catch (Exception e) { t += "headset hata: " + e.Message + Environment.NewLine; }
        try { HidBattery.Scan(v); } catch (Exception e) { t += "hidbat hata: " + e.Message + Environment.NewLine; }
        t += "vendor tarama " + sw.ElapsedMilliseconds + " ms, " + v.Count + " cihaz" + Environment.NewLine;
        foreach (var f in v) t += "vendor " + f.Kind + " pil=" + f.Reading.Percent + Environment.NewLine;
        foreach (var f in GenericDevices.Read())
            t += "genel " + f.Kind + " pil=" + f.Reading.Percent + (f.Reading.Text != null ? " (" + f.Reading.Text + ")" : "") + Environment.NewLine;
        return t;
    }

    // Calisan diger kopyalari kapat (guncelleme / kaldirma icin)
    static void StopOthers()
    {
        int me = Process.GetCurrentProcess().Id;
        foreach (var name in new[] { AppName, "PilGostergesi", "BarracudaPil" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id == me) continue;
                try
                {
                    // ayni adli baska bir programi kapatmamak icin urun adini kontrol et
                    string prod = FileVersionInfo.GetVersionInfo(p.MainModule.FileName).ProductName ?? "";
                    if (prod != AppName && prod != "Pil Gostergesi") continue;
                    p.Kill(); p.WaitForExit(3000);
                }
                catch { }
            }
    }

    static void Install(bool quiet)
    {
        try
        {
            StopOthers();
            Directory.CreateDirectory(InstallDir);
            if (!IsInstalled) File.Copy(Application.ExecutablePath, InstalledExe, true);
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.SetValue(AppName, "\"" + InstalledExe + "\"");
            CleanupLegacy();
            MakeShortcut();
            RegisterUninstall();
            Process.Start(InstalledExe);
            if (!quiet)
                MessageBox.Show("Installed. The icon is in the taskbar, next to the clock.\n\nTo uninstall, use Windows Settings > Apps > Installed apps.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e)
        {
            MessageBox.Show("Could not install: " + e.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public static void Uninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show("Uninstall BatteryRings?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        StopOthers();
        try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue(AppName, false); } catch { }
        try { if (File.Exists(Shortcut)) File.Delete(Shortcut); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\BatteryRings", false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\PilGostergesi", false); } catch { }
        // calisan exe kendini silemez: kapandiktan sonra klasoru sil
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + InstallDir + "\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { }
        Application.Exit();
    }

    // Entry in Windows Settings > Apps > Installed apps (per-user, no admin rights needed)
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName;
    static void RegisterUninstall()
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Application.ProductVersion);
                k.SetValue("Publisher", "blaidq");
                k.SetValue("DisplayIcon", InstalledExe);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + InstalledExe + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + InstalledExe + "\" /uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    // Eski adla (BarracudaPil) kalmis otomatik baslatma kaydini temizle
    static void CleanupLegacy()
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue("BarracudaPil", false); } catch { }
    }

    [DllImport("kernel32.dll")] static extern bool SetDefaultDllDirectories(int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string path);

    static void MakeShortcut()
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { Shortcut });
            var lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { InstalledExe });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { InstallDir });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Battery levels of your wireless devices" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }
        catch { }
    }
}
