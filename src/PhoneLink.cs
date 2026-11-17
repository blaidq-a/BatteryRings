// Phone battery from Microsoft Phone Link (opt-in, Settings > "Phone battery (Phone Link)").
// Windows keeps no battery value for a phone, but Phone Link writes the Start menu phone panel as an
// Adaptive Card to LocalState\StartMenu\StartMenuCompanion.json. That card holds the phone's name (the bold
// TextBlock) and its battery ("%34" / "34%"). Only these two values are read; the rest of the file
// (recent messages) is ignored and never stored, logged or put into problem reports.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

static class PhoneLink
{
    static readonly string CardPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Packages\Microsoft.YourPhone_8wekyb3d8bbwe\LocalState\StartMenu\StartMenuCompanion.json");

    // Phone Link is installed for this user (the settings show the phone switch only then)
    public static bool Installed
    {
        get { return Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(CardPath)))); }
    }

    static DateTime cachedAt = DateTime.MinValue;
    static string cachedName;
    static int cachedPct = -1;
    static bool cachedCharging;

    public static void Read(List<Found> list)
    {
        var fi = new FileInfo(CardPath);
        if (!fi.Exists || fi.Length > 4 * 1024 * 1024) return;
        if ((DateTime.Now - fi.LastWriteTime).TotalHours > 3) return; // phone not seen for a while

        if (fi.LastWriteTime != cachedAt)
        {
            string json;
            using (var fs = new FileStream(CardPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs)) json = sr.ReadToEnd();
            // battery: the first TextBlock whose text is just a percentage
            var m = Regex.Match(json, @"""type""\s*:\s*""TextBlock""\s*,\s*""text""\s*:\s*""\s*(%\s*(\d{1,3})|(\d{1,3})\s*%)\s*""");
            if (!m.Success) return; // file may be half written: try again next time
            int pct = int.Parse(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
            if (pct > 100) return;
            string head = json.Substring(0, m.Index);

            // name: the bold TextBlock before it (keep the last good one if this copy has none)
            string name = null;
            foreach (Match n in Regex.Matches(head, @"""type""\s*:\s*""TextBlock""\s*,\s*""text""\s*:\s*""((?:[^""\\]|\\.){1,60})""\s*,\s*""weight""\s*:\s*""Bolder"""))
                name = n.Groups[1].Value;
            try { name = name != null ? Regex.Unescape(name).Trim() : null; } catch { name = null; }
            if (!string.IsNullOrEmpty(name)) cachedName = name;

            // charging: the battery tooltip gets an extra clause, e.g. "Kalan pil %33, şarj oluyor"
            string tip = null;
            foreach (Match t in Regex.Matches(head, @"""tooltip""\s*:\s*""((?:[^""\\]|\\.){0,200})"""))
                tip = t.Groups[1].Value;
            try { tip = tip != null ? Regex.Unescape(tip).ToLowerInvariant() : ""; } catch { tip = ""; }
            cachedCharging = tip.Contains(",") || tip.Contains("charg") || tip.Contains("şarj") || tip.Contains("lädt") || tip.Contains("carg");

            cachedPct = pct;
            cachedAt = fi.LastWriteTime;
        }
        if (cachedPct < 0) return;
        list.Add(new Found { Id = "phonelink", Name = cachedName ?? "Phone", Kind = Kind.Phone,
            Reading = new Reading { Present = true, Percent = cachedPct, Charging = cachedCharging } });
    }
}
