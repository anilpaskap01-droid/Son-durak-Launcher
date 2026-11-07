// The launcher window: an HTML UI (ui/launcher.html) in WebView2 talking to this host
// with JSON messages. Handles launcher self-update and, for every game of the library (Games.cs):
// check / install / update / repair / play. One game window at a time.
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
        readonly GameRun[] runs = Games.All.Select(g => new GameRun(g)).ToArray();
        GameRun selected; // the game shown on the page (remembered in launcher.json)
        bool busy, offline, selfUpdating;
        GameRun busyRun; // the game being downloaded / repaired (one at a time)
        readonly bool justUpdated;
        string launcherError; // the launcher's own update failed
        GameForm game;
        GameRun running; // the game whose window is open
        DateTime playStart;
        DateTime lastCheck = DateTime.MinValue;
        bool repairing;
        // while the launcher sits open it looks for updates every few minutes (and installs them when
        // automatic updates are on), so PLAY never starts an old version
        readonly System.Windows.Forms.Timer autoTimer = new System.Windows.Forms.Timer { Interval = 4 * 60 * 1000 };

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
        [StructLayout(LayoutKind.Sequential)] struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        public LauncherForm(bool justUpdated = false)
        {
            this.justUpdated = justUpdated;
            selected = Run(Prefs.Get("game") as string) ?? runs[0];
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

        GameRun Run(string id) => runs.FirstOrDefault(r => r.Def.Id == id);
        // the game a page command is about: its "game" field, or the selected one
        GameRun RunFor(Dictionary<string, object> msg) => (msg.TryGetValue("game", out var g) ? Run(g as string) : null) ?? selected;

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
            cw.Settings.AreDevToolsEnabled = !AntiCheat.Enforced && Environment.GetEnvironmentVariable("SONDURAK_DEVTOOLS") == "1"; // (never in release builds: a cheat would run in DevTools)
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

        // One game as the page sees it: patch notes written for players (news.json), versions, play stats.
        // No commit messages / ids and nothing about the update mechanism.
        Dictionary<string, object> GameInfo(GameRun r)
        {
            var g = r.Def;
            var st = r.Local;
            var installed = Updater.IsInstalled(g, st);
            var installedNews = Path.Combine(g.Dir, "news.json");
            var info = new Dictionary<string, object>
            {
                ["id"] = g.Id,
                ["name"] = g.Name,
                ["status"] = r.Status(selfUpdating, busy && busyRun == r, repairing, offline),
                ["installed"] = installed,
                ["comingSoon"] = r.ComingSoon && !installed,
                ["error"] = r.Error ?? launcherError,
                ["plan"] = r.Plan == null ? null : new { files = r.Plan.Files, bytes = r.Plan.Bytes },
                ["installedVersion"] = installed ? NewsVersion(installedNews) : null,
                ["latestVersion"] = r.ComingSoon && !installed ? null : NewsVersion(g.NewsCache),
                ["playSeconds"] = PlayStats.Seconds(g),
                ["lastPlayed"] = PlayStats.LastPlayed(g),
                ["news"] = ReadJson(File.Exists(g.NewsCache) ? g.NewsCache : installedNews),
            };
            return info;
        }

        // Every game ("games": the library; the page shows the one it has selected, so switching needs no
        // round trip), plus the selected game's fields at the top level as launchers before 1.9 sent them.
        void SendState()
        {
            var games = runs.Select(GameInfo).ToArray();
            var state = new Dictionary<string, object>(games[Array.IndexOf(runs, selected)]);
            state["type"] = "state";
            state["game"] = selected.Def.Id;
            state["games"] = games;
            state["running"] = running?.Def.Id;
            state["busyGame"] = busy ? busyRun?.Def.Id : null;
            state["offline"] = offline;
            state["version"] = Application.ProductVersion;
            state["launcherUpdated"] = justUpdated;
            state["lang"] = Lang.Current;
            state["store"] = Config.Store;
            // every anomaly of the installed Son Durak, for its collection tab
            state["anomalies"] = Updater.IsInstalled(Games.SonDurak, Updater.LoadState(Games.SonDurak)) ? ReadJson(Path.Combine(Games.SonDurak.Dir, "assets", "anomalies.json")) : null;
            state["settings"] = new { autoUpdate = Options.AutoUpdate, fullscreen = Options.Fullscreen, chatFilter = Options.ChatFilter, map = Options.Map, discord = Options.Discord };
            Send(state);
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
            launcherError = null;
            offline = false;
            // (a game that is downloading right now keeps what it has: it is checked again afterwards)
            var check = runs.Where(r => !(busy && busyRun == r)).ToList();
            foreach (var r in check) { r.Error = null; r.Remote = null; r.Plan = null; }
            SendState();
            // the launcher itself first (from its own repo): a newer one is installed and started right away
            if (Updater.SelfUpdateAllowed)
            {
                (string commit, List<LauncherFile> files) lp = (null, null);
                try { lp = await Task.Run(() => Updater.LauncherPlanAsync(CancellationToken.None)); } catch { }
                if (lp.files != null && lp.files.Count > 0 && await SelfUpdateAsync(lp.commit, lp.files)) return;
            }
            // then every game (a game that is not published yet just stays "coming soon")
            foreach (var r in check)
            {
                if (busy && busyRun == r) continue; // (started downloading while the others were checked)
                try { await Task.Run(() => r.CheckAsync(CancellationToken.None)); }
                catch (Exception ex)
                {
                    // no network → "offline" (an installed game can still be played); anything else is shown as an error
                    if (ex is System.Net.Http.HttpRequestException || ex.InnerException is System.Net.WebException || ex is TaskCanceledException) offline = true;
                    else r.Error = ex.Message;
                }
                if (offline) break; // (no point asking for the next game)
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
            catch { launcherError = "launcher-update"; }
            selfUpdating = false;
            return false;
        }

        async Task InstallAsync(GameRun r, bool verify = false)
        {
            if (busy || r.Remote == null) return;
            busy = true; busyRun = r; repairing = verify;
            r.Error = null;
            SendState();
            var progress = new Progress<Progress>(p => Send(new { type = "progress", game = r.Def.Id, done = p.Done, total = p.Total, files = p.Files, filesTotal = p.FilesTotal, current = p.Current }));
            try { await Task.Run(() => Updater.InstallAsync(r.Def, r.Remote, progress, CancellationToken.None, verify)); }
            catch (Exception ex) { r.Error = ex.Message; }
            busy = false; busyRun = null; repairing = false;
            r.Plan = null;
            SendState();
        }

        // automatic updates: an update that turned up is installed right away, for every installed game
        // (never for the game that is running)
        async Task AutoInstallAsync()
        {
            if (!Options.AutoUpdate) return;
            foreach (var r in runs)
                if (!busy && r != running && r.UpdateWaiting) await InstallAsync(r);
        }

        // "Repair": check every game file on disk against the current version, fetch what is wrong
        async Task RepairAsync(GameRun r)
        {
            if (busy || game != null) return;
            if (r.Remote == null) await CheckAsync();
            if (r.Remote != null) await InstallAsync(r, true);
        }

        // PLAY: look for an update first unless that was just done (the launcher may have been open for
        // hours); with automatic updates it is installed before the game starts. "anyway" = the
        // player chose to play the installed version.
        async Task PlayAsync(GameRun r, string join, bool host, bool anyway)
        {
            if (busy) return;
            if (game != null && running != r) return; // one game at a time (the page says so)
            if (game == null && !anyway && !offline && (DateTime.UtcNow - lastCheck).TotalSeconds > 45)
            {
                await CheckAsync();
                if (r.UpdateWaiting)
                {
                    if (!Options.AutoUpdate) return; // the page now shows UPDATE (and "play without updating")
                    await InstallAsync(r);
                    if (r.UpdateWaiting) return; // the download failed: the page shows why
                }
            }
            if (!r.Installed) return;
            if (game == null && !await AntiCheatOkAsync(r)) return;
            Play(r, join, host);
        }

        // Anti-cheat before a ride (AntiCheat.cs): no debugger on the browser, the game files as the repository
        // has them (changed or added files are repaired first; offline, a changed game does not start).
        // (errors starting with "msg:" are already written for the player: the page shows them as they are)
        async Task<bool> AntiCheatOkAsync(GameRun r)
        {
            if (!AntiCheat.Enforced) return true;
            if (await Task.Run(() => AntiCheat.BrowserDebuggable())) { r.Error = "msg:" + AntiCheat.DebuggerMessage; SendState(); return false; }
            var bad = await Updater.TamperedFilesAsync(r.Def, !offline, CancellationToken.None);
            if (bad.Count == 0) return true;
            if (offline || r.Remote == null) { r.Error = "msg:" + AntiCheat.FilesMessage; SendState(); return false; }
            await InstallAsync(r, true); // "repair the game": re-downloads what differs, removes what does not belong
            bad = await Updater.TamperedFilesAsync(r.Def, true, CancellationToken.None);
            if (bad.Count == 0) return true;
            r.Error = "msg:" + AntiCheat.RepairFailedMessage; SendState();
            return false;
        }

        // join: a room code from an accepted invite or the party leader; host: the party leader's game
        // hosts a room right away (both Son Durak only). The launcher stays open next to the game.
        void Play(GameRun r, string join = null, bool host = false)
        {
            if (busy || !r.Installed) return;
            if (game != null && !game.IsDisposed)
            {
                if (running != r) return;
                if (!string.IsNullOrEmpty(join)) game.PostJson(json.Serialize(new { type = "join", room = join }));
                else if (host) game.PostJson(json.Serialize(new { type = "host" }));
                game.Activate();
                return;
            }
            var g = r.Def;
            game = new GameForm(g, join, host);
            running = r;
            playStart = DateTime.UtcNow;
            DiscordPresence.GameOpened(g);
            game.HostMessage += (m) =>
            {
                if (m.StartsWith("lang:") || m.StartsWith("map:") || m.StartsWith("filter:")) SendState();
                // Discord status: where the player is (Son Durak: the line too), the language
                if (m.StartsWith("stat:")) DiscordPresence.FromGame(m.Substring(5));
                else if (m.StartsWith("map:") && g == Games.SonDurak) DiscordPresence.SetMap(m.Substring(4));
                else if (m.StartsWith("lang:")) DiscordPresence.Kick();
                Send(new { type = "from-game", game = g.Id, msg = m });
            };
            game.FormClosed += (o, e) =>
            {
                game = null;
                running = null;
                DiscordPresence.GameClosed();
                PlayStats.Add(g, DateTime.UtcNow - playStart);
                Send(new { type = "game-state", game = g.Id, open = false });
                SendState(); // the language may have been changed in the game
                _ = CheckAsync();
            };
            game.Show();
            Send(new { type = "game-state", game = g.Id, open = true });
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
                case "select":
                    // the game library: which game the page shows (remembered for the next start)
                    if (msg.TryGetValue("game", out var sg) && Run(sg as string) is GameRun sr) { selected = sr; Prefs.Set("game", sr.Def.Id); }
                    SendState();
                    break;
                case "install":
                case "update": await InstallAsync(RunFor(msg)); break;
                case "repair": await RepairAsync(RunFor(msg)); break;
                case "pref":
                    if (msg.TryGetValue("key", out var pk) && pk is string pks && msg.TryGetValue("value", out var pv)) { Options.Set(pks, pv); SendState(); DiscordPresence.Kick(); }
                    break;
                case "play":
                    await PlayAsync(RunFor(msg), msg.TryGetValue("join", out var j) ? j as string : null, msg.TryGetValue("host", out var h) && h is bool hb && hb,
                        msg.TryGetValue("anyway", out var an) && an is bool ab && ab);
                    break;
                case "to-game": if (game != null && !game.IsDisposed && msg.TryGetValue("payload", out var pl)) game.PostJson(json.Serialize(pl)); break;
                case "profile": if (msg.TryGetValue("name", out var n) && n is string ns) Prefs.Set("name", ns); break;
                case "flash": Flash(); break;
                case "folder": { var dir = RunFor(msg).Def.Dir; Directory.CreateDirectory(dir); Process.Start("explorer.exe", dir); break; }
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
