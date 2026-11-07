// Anti-cheat, the launcher's side. A cheat gets into the game window through the WebView2 browser's remote
// debugging (the DevTools protocol: WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port, as the
// test cheat does) or by changing the downloaded game files; made-up stats it sends the launcher page are
// checked by that page itself (launcher.html). So, in release builds:
// - at start, before any WebView2: the WEBVIEW2_* variables (a debugging port, another browser, another
//   profile) are dropped from this process, and a machine policy giving WebView2 debugging arguments stops
//   the launcher;
// - before every ride (LauncherForm.PlayAsync): the browser this launcher started must not listen for a
//   debugger, and every game file must hash to the repository's list (Updater.TamperedFilesAsync; changed
//   or added files are repaired first);
// - DevTools stay off.
// Debug builds skip all of this: the automated tests drive them over DevTools.
using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Win32;

namespace SonDurakLauncher
{
    static class AntiCheat
    {
#if DEBUG
        public static readonly bool Enforced = false;
#else
        public static readonly bool Enforced = true;
#endif
        static readonly string[] Vars = { "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", "WEBVIEW2_USER_DATA_FOLDER",
            "WEBVIEW2_RELEASE_CHANNEL_PREFERENCE", "WEBVIEW2_CHANNEL_SEARCH_KIND", "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER" };

        // At start. Returns why the launcher must not run, or null.
        public static string Harden()
        {
            if (!Enforced) return null;
            foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null); // (this process only)
            return DebugPolicy()
                ? Lang.T("Bu bilgisayarda WebView2 için hata ayıklama ayarı var (yönetici ilkesi). Son Durak bu ayarla çalışmaz.",
                    "This computer has a debugging setting for WebView2 (an administrator policy). Son Durak does not run with it.")
                : null;
        }

        // WebView2 policies (HKLM / HKCU\Software\Policies\Microsoft\Edge\WebView2\<setting>, the value named after
        // the exe or "*") that would hand the browser a debugger
        static bool DebugPolicy()
        {
            string exe;
            try { exe = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName); } catch { exe = Config.LauncherExe; }
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using (var root = hive.OpenSubKey(@"Software\Policies\Microsoft\Edge\WebView2"))
                    {
                        if (root == null) continue;
                        foreach (var name in root.GetSubKeyNames())
                            using (var k = root.OpenSubKey(name))
                            {
                                if (k == null) continue;
                                foreach (var v in k.GetValueNames())
                                    if ((v == "*" || v.Equals(exe, StringComparison.OrdinalIgnoreCase)) && Debugging(k.GetValue(v) as string)) return true;
                            }
                    }
                }
                catch { /* no access: nothing set there for us */ }
            }
            return false;
        }

        static bool Debugging(string args) => !string.IsNullOrEmpty(args)
            && (args.IndexOf("remote-debugging", StringComparison.OrdinalIgnoreCase) >= 0 || args.IndexOf("remote-allow-origins", StringComparison.OrdinalIgnoreCase) >= 0);

        // The WebView2 browser this launcher started listens for a debugger (its command line has a port or a pipe).
        public static bool BrowserDebuggable()
        {
            if (!Enforced) return false;
            try
            {
                var me = Process.GetCurrentProcess().Id;
                using (var q = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe' AND ParentProcessId = {me}"))
                    foreach (ManagementObject o in q.Get())
                        using (o) if (Debugging(o["CommandLine"] as string)) return true;
            }
            catch { /* WMI unavailable: the variables were dropped at start anyway */ }
            return false;
        }

        public static string DebuggerMessage => Lang.T("Oyuna bir hata ayıklayıcı bağlanmaya çalışıyor (hile). Launcher'ı kapatıp normal şekilde yeniden aç.",
            "A debugger is trying to attach to the game (a cheat). Close the launcher and open it again normally.");
        public static string FilesMessage => Lang.T("Oyun dosyaları değiştirilmiş. Onarmak için internete bağlan.",
            "The game files were changed. Connect to the internet to repair them.");
        public static string RepairFailedMessage => Lang.T("Oyun dosyaları onarılamadı. Ayarlar > Oyunu onar'ı dene.",
            "The game files could not be repaired. Try Settings > Repair the game.");
    }
}
