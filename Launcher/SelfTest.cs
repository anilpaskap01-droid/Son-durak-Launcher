// Automated checks (used by tools/launcher-test, no clicking needed):
//   --selftest-install <log>   check + install/update headless, write a JSON report
//   --selftest-selfupdate <log> check the launcher itself against the repo and replace it (no restart)
//   --shot-launcher <png>      render the launcher UI off-screen and save a PNG
//   --shot-game <png>          start the installed game off-screen, begin a round, save a PNG
//   --selftest-discord <json> [s] show a sample Discord status for s seconds (Discord.cs)
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SonDurakLauncher
{
    static class SelfTest
    {
        public static int Install(string log)
        {
            var report = new System.Collections.Generic.Dictionary<string, object>();
            try
            {
                var before = Updater.LoadState();
                report["before"] = before.sha;
                var remote = Updater.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                report["remote"] = remote.Sha;
                report["message"] = remote.Message;
                var plan = Updater.PlanAsync(remote, CancellationToken.None).GetAwaiter().GetResult();
                report["planFiles"] = plan.Files;
                report["planBytes"] = plan.Bytes;
                long bytes = 0; int files = 0, reports = 0, partial = 0;
                Updater.InstallAsync(remote, new SyncProgress(p =>
                {
                    reports++;
                    // a report between whole files = bytes streamed live (the download screen's speed)
                    if (p.Done != bytes && p.Files == files) partial++;
                    bytes = p.Done; files = p.Files;
                }), CancellationToken.None).GetAwaiter().GetResult();
                var after = Updater.LoadState();
                report["after"] = after.sha;
                report["downloadedFiles"] = files;
                report["downloadedBytes"] = bytes;
                report["progressReports"] = reports;
                report["liveByteReports"] = partial;
                report["installedFiles"] = after.files.Count;
                report["ok"] = after.sha == remote.Sha && Updater.IsInstalled(after) && bytes == plan.Bytes && files == plan.Files;
            }
            catch (Exception ex) { report["ok"] = false; report["error"] = ex.ToString(); }
            File.WriteAllText(log, new JavaScriptSerializer().Serialize(report));
            return (bool)report["ok"] ? 0 : 1;
        }

        public static int SelfUpdate(string log)
        {
            var report = new System.Collections.Generic.Dictionary<string, object>();
            try
            {
                report["appDir"] = Config.AppDir;
                report["version"] = System.Windows.Forms.Application.ProductVersion;
                report["allowed"] = Updater.SelfUpdateAllowed;
                report["hasToken"] = !string.IsNullOrEmpty(Config.Token);
                var (commit, files) = Updater.LauncherPlanAsync(CancellationToken.None).GetAwaiter().GetResult();
                report["launcherRepoCommit"] = commit;
                report["planned"] = files.Select(f => f.Name).ToArray();
                if (files.Count > 0 && Updater.SelfUpdateAllowed)
                    Updater.ApplyLauncherUpdateAsync(commit, files, null, CancellationToken.None).GetAwaiter().GetResult();
                var after = Updater.LauncherPlanAsync(CancellationToken.None).GetAwaiter().GetResult().files;
                report["remainingAfter"] = after.Select(f => f.Name).ToArray();
                report["ok"] = commit != null && after.Count == 0;
            }
            catch (Exception ex) { report["ok"] = false; report["error"] = ex.ToString(); }
            File.WriteAllText(log, new JavaScriptSerializer().Serialize(report));
            return (bool)report["ok"] ? 0 : 1;
        }

        class SyncProgress : IProgress<Progress>
        {
            readonly Action<Progress> a;
            public SyncProgress(Action<Progress> a) { this.a = a; }
            public void Report(Progress p) => a(p);
        }

        // Off-screen window that hosts one WebView2, runs `script` after load and saves the result.
        public static void Shot(string png, bool game)
        {
            var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ClientSize = new Size(1200, 700), ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None };
            var web = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(web);
            form.Load += async (o, e) =>
            {
                try
                {
                    var env = await Program.GetEnv();
                    await web.EnsureCoreWebView2Async(env);
                    var cw = web.CoreWebView2;
                    var loaded = new TaskCompletionSource<bool>();
                    cw.NavigationCompleted += (s, a) => loaded.TrySetResult(true);
                    if (game)
                    {
                        cw.SetVirtualHostNameToFolderMapping("sondurak.game", Config.GameDir, CoreWebView2HostResourceAccessKind.Allow);
                        cw.Navigate("https://sondurak.game/index.html?lang=" + Lang.Current);
                    }
                    else
                    {
                        cw.WebMessageReceived += (s, a) => { };
                        Ui.Prepare();
                        cw.SetVirtualHostNameToFolderMapping(Ui.Host, Ui.Folder, CoreWebView2HostResourceAccessKind.Allow);
                        cw.Navigate(Ui.Url);
                    }
                    await loaded.Task;
                    if (game)
                    {
                        string res = "null";
                        for (int i = 0; i < 60 && res == "null"; i++) { await Task.Delay(500); res = await cw.ExecuteScriptAsync("window.__game ? 'ready' : null"); }
                        var url = await cw.ExecuteScriptAsync("(() => { const g = window.__game; g.manual = true; g.resize(1280, 720); g.startSingle(); for (let i = 0; i < 90; i++) g.update(1/30); g.render(); g.render(); return document.getElementById('c').toDataURL('image/png'); })()");
                        url = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<string>(url);
                        File.WriteAllBytes(png, Convert.FromBase64String(url.Substring(url.IndexOf(',') + 1)));
                        var info = await cw.ExecuteScriptAsync("JSON.stringify({ mode: __game.mode, passengers: __game.world.passengers.list.length, quitVisible: getComputedStyle(document.getElementById('quitBtn')).display !== 'none', gpu: (() => { const gl = __game.renderer.getContext(); const d = gl.getExtension('WEBGL_debug_renderer_info'); return d ? gl.getParameter(d.UNMASKED_RENDERER_WEBGL) : '?'; })() })");
                        File.WriteAllText(png + ".json", new JavaScriptSerializer().Deserialize<string>(info));
                    }
                    else
                    {
                        // fake a state so the page renders like a real launcher (status from SONDURAK_SHOT_STATUS,
                        // patch notes from SONDURAK_NEWS_FILE or the last downloaded ones)
                        await Task.Delay(800);
                        var newsFile = Environment.GetEnvironmentVariable("SONDURAK_NEWS_FILE");
                        if (string.IsNullOrEmpty(newsFile)) newsFile = Path.Combine(Config.DataDir, "news.json");
                        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                        object news = File.Exists(newsFile) ? js.DeserializeObject(File.ReadAllText(newsFile, System.Text.Encoding.UTF8)) : null;
                        var st = Environment.GetEnvironmentVariable("SONDURAK_SHOT_STATUS") ?? "update";
                        cw.PostWebMessageAsJson(js.Serialize(new
                        {
                            type = "state", status = st, installed = true, offline = false, error = (string)null,
                            plan = st == "update" ? new { files = 7, bytes = 131072 } : null,
                            version = Application.ProductVersion, launcherUpdated = false, lang = Lang.Current, news,
                            installedVersion = st == "update" ? "1.4.0" : "1.4.1", latestVersion = "1.4.1",
                            playSeconds = 7560, lastPlayed = DateTime.UtcNow.AddDays(-1).ToString("o"),
                        }));
                        await Task.Delay(800);
                        using (var fs = File.Create(png)) await cw.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                    }
                }
                catch (Exception ex) { File.WriteAllText(png + ".err.txt", ex.ToString()); }
                form.Close();
            };
            Application.Run(form);
        }
    }
}
