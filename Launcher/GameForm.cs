// The game window: WebView2 serving the downloaded game folder as https://sondurak.game/
// (a secure origin, so ES modules, pointer lock and WebRTC multiplayer all work).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SonDurakLauncher
{
    public class GameForm : Form
    {
        readonly WebView2 web;
        bool full;
        // messages from the game page the launcher page handles (friends list, invites, name, room)
        public event Action<string> HostMessage;
        FormWindowState prevState;
        Rectangle prevBounds;

        // join: a room code to join right away (an accepted invite)
        public GameForm(string join = null)
        {
            Text = "Son Durak";
            BackColor = Color.Black;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ClientSize = Program.ScaledSize(1280, 740);
            MinimumSize = Program.ScaledSize(800, 500);
            WindowState = FormWindowState.Maximized;
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(5, 7, 11) };
            Controls.Add(web);
            KeyPreview = true;
            // the game starts fullscreen; F11 in the game switches to a (maximised) window and back
            Shown += (o, e) => SetFull(true);
            Load += async (o, e) =>
            {
                var env = await Program.GetEnv();
                await web.EnsureCoreWebView2Async(env);
                var cw = web.CoreWebView2;
                cw.Settings.AreDefaultContextMenusEnabled = false;
                cw.Settings.IsZoomControlEnabled = false;
                cw.Settings.IsStatusBarEnabled = false;
                cw.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("SONDURAK_DEVTOOLS") == "1";
                cw.SetVirtualHostNameToFolderMapping("sondurak.game", Config.GameDir, CoreWebView2HostResourceAccessKind.Allow);
                cw.WebMessageReceived += (s2, a) =>
                {
                    string m;
                    try { m = a.TryGetWebMessageAsString(); } catch { return; }
                    if (m == "quit") Close();
                    else if (m == "fullscreen") SetFull(!full);
                    else if (m == null) return;
                    else
                    {
                        if (m.StartsWith("lang:")) Lang.Set(m.Substring(5));
                        if (m.StartsWith("name:")) Prefs.Set("name", m.Substring(5));
                        HostMessage?.Invoke(m);
                    }
                };
                cw.ContainsFullScreenElementChanged += (s2, a) => SetFull(cw.ContainsFullScreenElement);
                cw.NewWindowRequested += (s2, a) =>
                {
                    a.Handled = true;
                    if (a.Uri.StartsWith("https://") || a.Uri.StartsWith("http://")) Process.Start(new ProcessStartInfo(a.Uri) { UseShellExecute = true });
                };
                var url = "https://sondurak.game/index.html?lang=" + Lang.Current;
                if (Prefs.Get("name") is string name && name.Length > 0) url += "&name=" + Uri.EscapeDataString(name);
                if (!string.IsNullOrEmpty(join)) url += "&join=" + Uri.EscapeDataString(join);
                cw.Navigate(url);
            };
        }

        public void PostJson(string json)
        {
            if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json);
        }

        void SetFull(bool on)
        {
            if (on == full) return;
            full = on;
            if (on)
            {
                prevState = WindowState;
                prevBounds = Bounds;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                Bounds = prevBounds;
                WindowState = prevState;
            }
        }
    }
}
