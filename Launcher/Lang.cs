// Launcher preferences and play statistics, saved in %LOCALAPPDATA%\SonDurak\launcher.json:
//   lang         tr / en (default: the Windows display language); shared with the game, which is
//                opened as index.html?lang=.. and reports back when it is changed there
//   playSeconds  total time the game window was open
//   lastPlayed   when the game window was last closed (UTC, ISO 8601)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SonDurakLauncher
{
    static class Prefs
    {
        static Dictionary<string, object> data;
        static string FilePath => Path.Combine(Config.DataDir, "launcher.json");

        static Dictionary<string, object> Data => data ?? (data = Load());

        static Dictionary<string, object> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new Dictionary<string, object>();
            }
            catch { /* unreadable: start again */ }
            return new Dictionary<string, object>();
        }

        public static object Get(string key) => Data.TryGetValue(key, out var v) ? v : null;

        public static void Set(string key, object value)
        {
            Data[key] = value;
            try
            {
                Directory.CreateDirectory(Config.DataDir);
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(Data), Encoding.UTF8);
            }
            catch { /* not fatal */ }
        }
    }

    static class Lang
    {
        public static string Current
        {
            get
            {
                var l = Prefs.Get("lang") as string;
                if (l == "tr" || l == "en") return l;
                return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
            }
        }
        public static bool En => Current == "en";
        public static string T(string tr, string en) => En ? en : tr;
        public static void Set(string lang) { if (lang == "tr" || lang == "en") Prefs.Set("lang", lang); }
    }

    // Launcher settings the page changes (cmd "pref"); the game gets the line and the chat filter in its URL.
    static class Options
    {
        static bool Flag(string key, bool def) => Prefs.Get(key) is bool b ? b : def;
        public static bool AutoUpdate => Flag("autoUpdate", true);
        public static bool Fullscreen => Flag("fullscreen", true);
        public static bool ChatFilter => Flag("chatFilter", true);
        public static string Map => Prefs.Get("map") as string == "metro" ? "metro" : "tram";

        public static void Set(string key, object value)
        {
            switch (key)
            {
                case "autoUpdate": case "fullscreen": case "chatFilter": if (value is bool) Prefs.Set(key, value); break;
                case "map": if (value as string == "metro" || value as string == "tram") Prefs.Set(key, value); break;
            }
        }
    }

    static class PlayStats
    {
        public static double Seconds { get { try { return Convert.ToDouble(Prefs.Get("playSeconds") ?? 0, CultureInfo.InvariantCulture); } catch { return 0; } } }
        public static string LastPlayed => Prefs.Get("lastPlayed") as string;

        public static void Add(TimeSpan played)
        {
            if (played.TotalSeconds < 1) return;
            Prefs.Set("playSeconds", Math.Round(Seconds + played.TotalSeconds));
            Prefs.Set("lastPlayed", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
    }
}
