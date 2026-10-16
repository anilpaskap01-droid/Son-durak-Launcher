// The launcher window: an HTML UI (ui/launcher.html) in WebView2 talking to this host
// with JSON messages. Handles launcher self-update / check / install / update / play.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SonDurakLauncher
{
    public class LauncherForm : Form
    {
        readonly WebView2 web;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        RemoteInfo remote;
        LocalState local;
        bool busy, offline, selfUpdating;
        readonly bool justUpdated;
        Updater.Plan plan;
        string error;
        GameForm game;
        DateTime playStart;
        static string NewsCache => Path.Combine(Config.DataDir, "news.json");

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public LauncherForm(bool justUpdated = false)
        {
            this.justUpdated = justUpdated;
            Text = "Son Durak Launcher";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(7, 9, 13);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ClientSize = Program.ScaledSize(1120, 680);
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(7, 9, 13) };
            Controls.Add(web);
            Load += async (o, e) => await InitAsync();
        }

        async Task InitAsync()
        {
            try
            {
                var env = await Program.GetEnv();
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Lang.T("WebView2 başlatılamadı: ", "Could not start WebView2: ") + ex.Message, "Son Durak", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            var cw = web.CoreWebView2;
            cw.Settings.AreDefaultContextMenusEnabled = false;
            cw.Settings.IsZoomControlEnabled = false;
            cw.Settings.IsStatusBarEnabled = false;
            cw.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("SONDURAK_DEVTOOLS") == "1";
            cw.WebMessageReceived += OnMessage;
            cw.NewWindowRequested += (o, e) => { e.Handled = true; OpenUrl(e.Uri); };
            cw.NavigateToString(Ui.LauncherHtml());
        }

        void Send(object msg)
        {
            if (web.CoreWebView2 == null) return;
            web.CoreWebView2.PostWebMessageAsJson(json.Serialize(msg));
        }

        void SendState()
        {
            local = Updater.LoadState();
            var installed = Updater.IsInstalled(local);
            string status;
            if (selfUpdating) status = "selfUpdate";
            else if (busy) status = "busy";
            else if (remote == null && !offline && error == null) status = "checking";
            else if (!installed) status = remote == null ? "offlineNoGame" : "notInstalled";
            else if (remote != null && remote.Sha != local.sha) status = "update";
            else status = "ready";
            // what the page shows: patch notes written for players (news.json), versions, play stats.
            // No commit messages / ids and nothing about the update mechanism.
            var installedNews = Path.Combine(Config.GameDir, "news.json");
            Send(new
            {
                type = "state",
                status,
                installed,
                offline,
                error,
                plan = plan == null ? null : new { files = plan.Files, bytes = plan.Bytes },
                version = Application.ProductVersion,
                launcherUpdated = justUpdated,
                lang = Lang.Current,
                news = ReadJson(File.Exists(NewsCache) ? NewsCache : installedNews),
                installedVersion = installed ? NewsVersion(installedNews) : null,
                latestVersion = NewsVersion(NewsCache),
                playSeconds = PlayStats.Seconds,
                lastPlayed = PlayStats.LastPlayed,
            });
        }

        object ReadJson(string file)
        {
            try { return File.Exists(file) ? json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) : null; }
            catch { return null; }
        }

        // newest version in a news.json ("1.4.1"), or null
        string NewsVersion(string file)
        {
            try
            {
                var notes = ((Dictionary<string, object>)ReadJson(file))["notes"] as System.Collections.IList;
                return notes != null && notes.Count > 0 ? ((Dictionary<string, object>)notes[0])["version"] as string : null;
            }
            catch { return null; }
        }

        async Task CheckAsync()
        {
            error = null;
            offline = false;
            remote = null;
            SendState();
            plan = null;
            try
            {
                // the launcher itself first (from its own repo): a newer one is installed and started right away
                if (Updater.SelfUpdateAllowed)
                {
                    (string commit, List<LauncherFile> files) lp = (null, null);
                    try { lp = await Task.Run(() => Updater.LauncherPlanAsync(CancellationToken.None)); } catch { }
                    if (lp.files != null && lp.files.Count > 0 && await SelfUpdateAsync(lp.commit, lp.files)) return;
                }
                remote = await Task.Run(() => Updater.CheckAsync(CancellationToken.None));
                try
                {
                    var news = await Task.Run(() => Updater.NewsJsonAsync(remote.Sha, CancellationToken.None));
                    if (news != null) File.WriteAllText(NewsCache, news, Encoding.UTF8);
                }
                catch { /* the notes are nice to have; keep the last ones */ }
                var st = Updater.LoadState();
                if (remote.Sha != st.sha)
                {
                    plan = await Task.Run(() => Updater.PlanAsync(remote, CancellationToken.None));
                    // only launcher / tool files changed: nothing to download, the game is current
                    if (Updater.IsInstalled(st) && plan.Files == 0 && !plan.Removals) { Updater.MarkCurrent(remote); plan = null; }
                }
            }
            catch (Exception ex)
            {
                // no network → "offline" (an installed game can still be played); anything else is shown as an error
                if (ex is System.Net.Http.HttpRequestException || ex.InnerException is System.Net.WebException || ex is TaskCanceledException) offline = true;
                else error = ex.Message;
            }
            SendState();
        }

        // Replace the launcher files and restart. Returns true when the new launcher is starting.
        async Task<bool> SelfUpdateAsync(string commit, List<LauncherFile> files)
        {
            selfUpdating = true;
            SendState();
            var progress = new Progress<Progress>(p => Send(new { type = "progress", done = p.Done, total = p.Total, files = p.Files, filesTotal = p.FilesTotal, current = p.Current }));
            try
            {
                await Task.Run(() => Updater.ApplyLauncherUpdateAsync(commit, files, progress, CancellationToken.None));
                Program.Restart();
                Close();
                return true;
            }
            catch { error = "launcher-update"; }
            selfUpdating = false;
            return false;
        }

        async Task InstallAsync()
        {
            if (busy || remote == null) return;
            busy = true;
            error = null;
            SendState();
            var progress = new Progress<Progress>(p => Send(new { type = "progress", done = p.Done, total = p.Total, files = p.Files, filesTotal = p.FilesTotal, current = p.Current }));
            try { await Task.Run(() => Updater.InstallAsync(remote, progress, CancellationToken.None)); }
            catch (Exception ex) { error = ex.Message; }
            busy = false;
            SendState();
        }

        void Play()
        {
            if (busy || !Updater.IsInstalled(Updater.LoadState())) return;
            if (game != null && !game.IsDisposed) { game.Activate(); return; }
            game = new GameForm();
            playStart = DateTime.UtcNow;
            game.FormClosed += (o, e) =>
            {
                game = null;
                PlayStats.Add(DateTime.UtcNow - playStart);
                SendState(); // the language may have been changed in the game
                Show();
                Activate();
                _ = CheckAsync();
            };
            game.Show();
            Hide();
        }

        async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string cmd;
            Dictionary<string, object> msg;
            try { msg = json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); cmd = (string)msg["cmd"]; }
            catch { return; }
            switch (cmd)
            {
                case "lang": Lang.Set(msg.TryGetValue("lang", out var l) ? l as string : null); SendState(); break;
                case "ready": await CheckAsync(); break;
                case "check": if (!busy) await CheckAsync(); break;
                case "install":
                case "update": await InstallAsync(); break;
                case "play": Play(); break;
                case "folder": Directory.CreateDirectory(Config.GameDir); Process.Start("explorer.exe", Config.GameDir); break;
                case "min": WindowState = FormWindowState.Minimized; break;
                case "close": Close(); break;
                case "drag":
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
                    break;
            }
        }

        static void OpenUrl(string url)
        {
            if (url != null && (url.StartsWith("https://") || url.StartsWith("http://"))) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

    }
}
