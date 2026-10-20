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
        DateTime lastCheck = DateTime.MinValue;
        bool repairing;
        // while the launcher sits open it looks for updates every few minutes (and installs them when
        // automatic updates are on), so PLAY never starts an old version
        readonly System.Windows.Forms.Timer autoTimer = new System.Windows.Forms.Timer { Interval = 4 * 60 * 1000 };
        static string NewsCache => Path.Combine(Config.DataDir, "news.json");

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
        [StructLayout(LayoutKind.Sequential)] struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        public LauncherForm(bool justUpdated = false)
        {
            this.justUpdated = justUpdated;
            Text = "Son Durak Launcher";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(7, 9, 13);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ClientSize = Program.ScaledSize(1200, 700);
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(7, 9, 13) };
            Controls.Add(web);
            Load += async (o, e) => await InitAsync();
            Shown += (o, e) => DiscordPresence.LauncherOpened(); // Discord: "Playing Son Durak · In the launcher"
            autoTimer.Tick += async (o, e) => { if (!busy && !selfUpdating && game == null) { await CheckAsync(); await AutoInstallAsync(); } };
            autoTimer.Start();
            // automated tests run the launcher off-screen
            if (Environment.GetEnvironmentVariable("SONDURAK_TEST_OFFSCREEN") == "1") { StartPosition = FormStartPosition.Manual; Location = new Point(-5000, -5000); ShowInTaskbar = false; }
            // while the game runs the launcher stays open (friends, chat, invites): closing it only minimises it
            FormClosing += (o, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing && game != null && !game.IsDisposed) { e.Cancel = true; WindowState = FormWindowState.Minimized; }
            };
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
            Ui.Prepare();
            cw.SetVirtualHostNameToFolderMapping(Ui.Host, Ui.Folder, CoreWebView2HostResourceAccessKind.Allow);
            cw.Navigate(Ui.Url);
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
            else if (busy) status = repairing ? "repair" : "busy";
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
                store = Config.Store,
                playSeconds = PlayStats.Seconds,
                lastPlayed = PlayStats.LastPlayed,
                // every anomaly of the installed game, for the collection tab
                anomalies = installed ? ReadJson(Path.Combine(Config.GameDir, "assets", "anomalies.json")) : null,
                settings = new { autoUpdate = Options.AutoUpdate, fullscreen = Options.Fullscreen, chatFilter = Options.ChatFilter, map = Options.Map, discord = Options.Discord },
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
            lastCheck = DateTime.UtcNow;
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

        async Task InstallAsync(bool verify = false)
        {
            if (busy || remote == null) return;
            busy = true; repairing = verify;
            error = null;
            SendState();
            var progress = new Progress<Progress>(p => Send(new { type = "progress", done = p.Done, total = p.Total, files = p.Files, filesTotal = p.FilesTotal, current = p.Current }));
            try { await Task.Run(() => Updater.InstallAsync(remote, progress, CancellationToken.None, verify)); }
            catch (Exception ex) { error = ex.Message; }
            busy = false; repairing = false;
            plan = null;
            SendState();
        }

        bool UpdateWaiting => remote != null && Updater.IsInstalled(Updater.LoadState()) && remote.Sha != Updater.LoadState().sha;

        // automatic updates: an update that turned up is installed right away (never while the game runs)
        async Task AutoInstallAsync()
        {
            if (Options.AutoUpdate && game == null && !busy && UpdateWaiting) await InstallAsync();
        }

        // "Repair": check every game file on disk against the current version, fetch what is wrong
        async Task RepairAsync()
        {
            if (busy || game != null) return;
            if (remote == null) await CheckAsync();
            if (remote != null) await InstallAsync(true);
        }

        // PLAY: look for an update first unless that was just done (the launcher may have been open for
        // hours); with automatic updates it is installed before the game starts. "anyway" = the
        // player chose to play the installed version.
        async Task PlayAsync(string join, bool host, bool anyway)
        {
            if (busy) return;
            if (game == null && !anyway && !offline && (DateTime.UtcNow - lastCheck).TotalSeconds > 45)
            {
                await CheckAsync();
                if (UpdateWaiting)
                {
                    if (!Options.AutoUpdate) return; // the page now shows UPDATE (and "play without updating")
                    await InstallAsync();
                    if (UpdateWaiting) return; // the download failed: the page shows why
                }
            }
            Play(join, host);
        }

        // join: a room code from an accepted invite or the party leader; host: the party leader's game
        // hosts a room right away. The launcher stays open next to the game.
        void Play(string join = null, bool host = false)
        {
            if (busy || !Updater.IsInstalled(Updater.LoadState())) return;
            if (game != null && !game.IsDisposed)
            {
                if (!string.IsNullOrEmpty(join)) game.PostJson(json.Serialize(new { type = "join", room = join }));
                else if (host) game.PostJson(json.Serialize(new { type = "host" }));
                game.Activate();
                return;
            }
            game = new GameForm(join, host);
            playStart = DateTime.UtcNow;
            DiscordPresence.GameOpened();
            game.HostMessage += (m) =>
            {
                if (m.StartsWith("lang:") || m.StartsWith("map:") || m.StartsWith("filter:")) SendState();
                // Discord status: where the player is, the line, the language
                if (m.StartsWith("stat:")) DiscordPresence.FromGame(m.Substring(5));
                else if (m.StartsWith("map:")) DiscordPresence.SetMap(m.Substring(4));
                else if (m.StartsWith("lang:")) DiscordPresence.Kick();
                Send(new { type = "from-game", msg = m });
            };
            game.FormClosed += (o, e) =>
            {
                game = null;
                DiscordPresence.GameClosed();
                PlayStats.Add(DateTime.UtcNow - playStart);
                Send(new { type = "game-state", open = false });
                SendState(); // the language may have been changed in the game
                _ = CheckAsync();
            };
            game.Show();
            Send(new { type = "game-state", open = true });
        }

        void Flash()
        {
            if (Form.ActiveForm == this) return;
            var fi = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)), hwnd = Handle, dwFlags = 3 | 12 /* all, until foreground */, uCount = 3, dwTimeout = 0 };
            FlashWindowEx(ref fi);
        }

        async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string cmd;
            Dictionary<string, object> msg;
            try { msg = json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); cmd = (string)msg["cmd"]; }
            catch { return; }
            switch (cmd)
            {
                case "lang": Lang.Set(msg.TryGetValue("lang", out var l) ? l as string : null); SendState(); DiscordPresence.Kick(); break;
                case "ready": await CheckAsync(); await AutoInstallAsync(); break;
                case "check": if (!busy) { await CheckAsync(); await AutoInstallAsync(); } break;
                case "install":
                case "update": await InstallAsync(); break;
                case "repair": await RepairAsync(); break;
                case "pref":
                    if (msg.TryGetValue("key", out var pk) && pk is string pks && msg.TryGetValue("value", out var pv)) { Options.Set(pks, pv); SendState(); DiscordPresence.Kick(); }
                    break;
                case "play":
                    await PlayAsync(msg.TryGetValue("join", out var j) ? j as string : null, msg.TryGetValue("host", out var h) && h is bool hb && hb,
                        msg.TryGetValue("anyway", out var an) && an is bool ab && ab);
                    break;
                case "to-game": if (game != null && !game.IsDisposed && msg.TryGetValue("payload", out var pl)) game.PostJson(json.Serialize(pl)); break;
                case "profile": if (msg.TryGetValue("name", out var n) && n is string ns) Prefs.Set("name", ns); break;
                case "flash": Flash(); break;
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
