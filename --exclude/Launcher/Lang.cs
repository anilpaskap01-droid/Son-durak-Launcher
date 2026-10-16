// Launcher language (tr / en): saved in %LOCALAPPDATA%\SonDurak\launcher.json, defaults to the
// Windows display language. Shared with the game: passed as index.html?lang=.. and reported
// back by the game when it is changed there.
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SonDurakLauncher
{
    static class Lang
    {
        static string SettingsFile => Path.Combine(Config.DataDir, "launcher.json");
        static string current;

        public static string Current => current ?? (current = Load());
        public static bool En => Current == "en";
        public static string T(string tr, string en) => En ? en : tr;

        static string Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsFile, Encoding.UTF8));
                    if (d != null && d.TryGetValue("lang", out var l) && (l as string == "tr" || l as string == "en")) return (string)l;
                }
            }
            catch { /* unreadable: use the default */ }
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
        }

        public static void Set(string lang)
        {
            if (lang != "tr" && lang != "en") return;
            current = lang;
            try
            {
                Directory.CreateDirectory(Config.DataDir);
                File.WriteAllText(SettingsFile, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["lang"] = lang }), Encoding.UTF8);
            }
            catch { /* not fatal */ }
        }
    }
}
