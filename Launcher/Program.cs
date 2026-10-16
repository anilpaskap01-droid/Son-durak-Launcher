using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace SonDurakLauncher
{
    static class Program
    {
        public static CoreWebView2Environment Env;

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern uint GetDpiForSystem();

        // Window size for a 96-dpi design size. (A per-monitor-aware form reports 96 dpi until its
        // window exists, which made the launcher open too small on 125 % / 150 % displays.)
        // Never larger than 94 % of the screen's work area.
        public static System.Drawing.Size ScaledSize(int w, int h)
        {
            float s = 1f;
            try { s = GetDpiForSystem() / 96f; } catch { /* Windows 8.1 and older */ }
            var area = Screen.PrimaryScreen.WorkingArea;
            var fit = Math.Min(1f, Math.Min(area.Width * 0.94f / (w * s), area.Height * 0.94f / (h * s)));
            return new System.Drawing.Size((int)(w * s * fit), (int)(h * s * fit));
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (Environment.OSVersion.Version.Major >= 6) SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (Array.IndexOf(args, "--uninstall") >= 0) { Uninstaller.Run(); return; }
            int a;
            if ((a = Array.IndexOf(args, "--selftest-install")) >= 0) { Environment.Exit(SelfTest.Install(args[a + 1])); return; }
            if ((a = Array.IndexOf(args, "--shot-launcher")) >= 0) { SelfTest.Shot(args[a + 1], false); return; }
            if ((a = Array.IndexOf(args, "--shot-game")) >= 0) { SelfTest.Shot(args[a + 1], true); return; }

            if ((a = Array.IndexOf(args, "--selftest-selfupdate")) >= 0) { Environment.Exit(SelfTest.SelfUpdate(args[a + 1])); return; }

            // "--updated": started by the previous launcher version right after it replaced itself;
            // wait until that process has exited and released the single-instance mutex.
            var updated = Array.IndexOf(args, "--updated") >= 0;
            // (SONDURAK_INSTANCE lets an automated test run next to a normally opened launcher)
            using (var mutex = new Mutex(false, "SonDurakLauncher.Single" + Environment.GetEnvironmentVariable("SONDURAK_INSTANCE")))
            {
                bool owned;
                try { owned = mutex.WaitOne(updated ? 20000 : 0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) { MessageBox.Show(Lang.T("Son Durak Launcher zaten açık.", "Son Durak Launcher is already running."), "Son Durak", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                try
                {
                    string runtime = null;
                    try { runtime = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
                    if (string.IsNullOrEmpty(runtime))
                    {
                        if (MessageBox.Show(Lang.T("Microsoft Edge WebView2 Runtime bulunamadı. Son Durak çalışmak için buna ihtiyaç duyar.\n\nİndirme sayfası açılsın mı?",
                            "Microsoft Edge WebView2 Runtime was not found. Son Durak needs it to run.\n\nOpen the download page?"),
                            "Son Durak", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            Process.Start("https://developer.microsoft.com/microsoft-edge/webview2/");
                        return;
                    }
                    Directory.CreateDirectory(Config.DataDir);
                    Updater.CleanupOldLauncherFiles();
                    Application.Run(new LauncherForm(updated));
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        // Start the (just replaced) launcher exe again.
        public static void Restart()
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Config.AppDir, Config.LauncherExe), "--updated") { WorkingDirectory = Config.AppDir, UseShellExecute = false });
        }

        // One WebView2 environment for the launcher UI and the game (same profile = same saved settings).
        public static async System.Threading.Tasks.Task<CoreWebView2Environment> GetEnv()
        {
            if (Env != null) return Env;
            // the launcher page stays connected to friends while the game covers it or it is minimised,
            // so its timers / page must not be throttled as "hidden"
            var opts = new CoreWebView2EnvironmentOptions("--force_high_performance_gpu --autoplay-policy=no-user-gesture-required"
                + " --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows --disable-features=CalculateNativeWinOcclusion");
            Env = await CoreWebView2Environment.CreateAsync(null, Config.WebViewData, opts);
            return Env;
        }
    }
}
