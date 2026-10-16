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
        FormWindowState prevState;
        Rectangle prevBounds;

        public GameForm()
        {
            Text = "Son Durak";
            BackColor = Color.Black;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var s = DeviceDpi / 96f;
            ClientSize = new Size((int)(1280 * s), (int)(740 * s));
            MinimumSize = new Size((int)(800 * s), (int)(500 * s));
            WindowState = FormWindowState.Maximized;
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(5, 7, 11) };
            Controls.Add(web);
            KeyPreview = true;
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
                    else if (m != null && m.StartsWith("lang:")) Lang.Set(m.Substring(5));
                };
                cw.ContainsFullScreenElementChanged += (s2, a) => SetFull(cw.ContainsFullScreenElement);
                cw.NewWindowRequested += (s2, a) =>
                {
                    a.Handled = true;
                    if (a.Uri.StartsWith("https://") || a.Uri.StartsWith("http://")) Process.Start(new ProcessStartInfo(a.Uri) { UseShellExecute = true });
                };
                cw.Navigate("https://sondurak.game/index.html?lang=" + Lang.Current);
            };
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
