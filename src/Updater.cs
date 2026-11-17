// Updates from this project's GitHub releases.
//   - Asks api.github.com for the latest release of blaidq-a/BatteryRings (no personal data is sent).
//   - Downloads BatteryRings.exe and BatteryRings.exe.sha256 only from that repository's release URLs.
//   - Installs only if the SHA-256 matches and the file is a BatteryRings build of the advertised version.
//   - The new exe installs itself over the old one ("/update") and restarts.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class Updater
{
    const string Repo = "blaidq-a/BatteryRings";
    const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";
    const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";

    public static string Status = "";      // shown in Settings
    static int running;

    public static Version Current
    {
        get { return new Version(Application.ProductVersion); }
    }

    // manual: started by the button (also works when not installed: then it only reports)
    public static void Check(bool manual, Action changed)
    {
        if (Interlocked.Exchange(ref running, 1) == 1) return;
        Status = "Checking…";
        if (changed != null) changed();
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { Status = Run(manual); }
            catch { Status = "Update check failed"; }
            finally { Interlocked.Exchange(ref running, 0); }
            if (changed != null) changed();
        });
    }

    static string Run(bool manual)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
        string json;
        using (var wc = Client()) json = wc.DownloadString(Api);

        var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+(\\.[0-9]+){1,3})\"");
        if (!tag.Success) return "Update check failed";
        var latest = new Version(tag.Groups[1].Value);
        var cur = Current;
        if (Norm(latest) <= Norm(cur)) return "Up to date (v" + Short(cur) + ")";

        if (!Program.IsInstalled) return "v" + Short(latest) + " is available on GitHub";

        string exeUrl = Asset(json, "BatteryRings.exe"), shaUrl = Asset(json, "BatteryRings.exe.sha256");
        if (exeUrl == null || shaUrl == null) return "v" + Short(latest) + " is available on GitHub";

        Status = "Downloading v" + Short(latest) + "…";
        string dir = Path.Combine(Path.GetTempPath(), "BatteryRings-update");
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "BatteryRings.exe");
        string expected;
        using (var wc = Client())
        {
            expected = wc.DownloadString(shaUrl).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
            wc.DownloadFile(exeUrl, exe);
        }

        // integrity and identity checks before running anything
        string actual;
        using (var sha = SHA256.Create())
        using (var f = File.OpenRead(exe))
            actual = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        if (expected.Length != 64 || actual != expected) { TryDelete(exe); return "Update failed: checksum mismatch"; }
        var info = FileVersionInfo.GetVersionInfo(exe);
        if (info.ProductName != Program.AppName || Norm(new Version(info.FileVersion)) != Norm(latest))
        { TryDelete(exe); return "Update failed: unexpected file"; }

        Process.Start(new ProcessStartInfo(exe, "/update") { UseShellExecute = false });
        return "Installing v" + Short(latest) + "…";
    }

    static WebClient Client()
    {
        var wc = new WebClient();
        wc.Headers[HttpRequestHeader.UserAgent] = "BatteryRings-Updater";
        return wc;
    }

    // browser_download_url of an asset, accepted only if it points at this repository's releases
    static string Asset(string json, string name)
    {
        foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
        {
            string u = m.Groups[1].Value;
            if (u.StartsWith(DownloadPrefix, StringComparison.Ordinal) && u.EndsWith("/" + name, StringComparison.Ordinal)) return u;
        }
        return null;
    }

    static Version Norm(Version v) { return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision)); }
    public static string Short(Version v) { return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); }
    static void TryDelete(string f) { try { File.Delete(f); } catch { } }
}
