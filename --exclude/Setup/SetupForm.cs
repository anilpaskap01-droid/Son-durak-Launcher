// Son Durak Launcher setup: per-user install (no admin), shortcuts, Apps & features entry.
// The launcher files are embedded as payload.zip. Command line (for tests / silent installs):
//   --silent            install with defaults, no UI, do not start the launcher
//   --dir <path>        install folder (default %LOCALAPPDATA%\Programs\Son Durak)
//   --test              skip shortcuts and registry (used by the build's install test)
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SonDurakSetup
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var dir = Installer.DefaultDir;
            var i = Array.IndexOf(args, "--dir");
            if (i >= 0 && i + 1 < args.Length) dir = args[i + 1];
            var test = args.Contains("--test");
            if (args.Contains("--silent"))
            {
                try { Installer.Install(dir, !test, !test, null); return 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            }
            var form = new SetupForm(dir);
            var shot = Array.IndexOf(args, "--shot");
            if (shot >= 0 && shot + 1 < args.Length)
            {
                // test helper: render the dialog to a PNG without showing it
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-4000, -4000); form.ShowInTaskbar = false;
                form.Shown += (o, e) => { using (var bmp = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height)); bmp.Save(args[shot + 1]); } form.Close(); };
            }
            Application.Run(form);
            return 0;
        }
    }

    // Setup language: the launcher's saved choice (%LOCALAPPDATA%\SonDurak\launcher.json) or Windows'.
    static class Tx
    {
        public static readonly bool En = Detect();
        public static string T(string tr, string en) => En ? en : tr;
        static bool Detect()
        {
            try
            {
                var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonDurak", "launcher.json");
                if (File.Exists(f)) { var j = File.ReadAllText(f); if (j.Contains("\"en\"")) return true; if (j.Contains("\"tr\"")) return false; }
            }
            catch { }
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "tr";
        }
    }

    static class Installer
    {
        public const string AppName = "Son Durak";
        public const string Exe = "SonDurakLauncher.exe";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SonDurak";
        public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public static bool WebView2Installed()
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var path in new[] { @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" })
                    using (var k = root.OpenSubKey(path))
                    {
                        var pv = k?.GetValue("pv") as string;
                        if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0") return true;
                    }
            return false;
        }

        public static void Install(string dir, bool shortcuts, bool register, Action<int> progress)
        {
            foreach (var p in Process.GetProcessesByName("SonDurakLauncher"))
            {
                try { p.CloseMainWindow(); if (!p.WaitForExit(3000)) p.Kill(); } catch { }
            }
            Directory.CreateDirectory(dir);
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
            {
                var n = 0;
                foreach (var e in zip.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(dir, e.FullName));
                    if (!target.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
                    if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    e.ExtractToFile(target, true);
                    progress?.Invoke(++n * 90 / zip.Entries.Count);
                }
            }
            var exe = Path.Combine(dir, Exe);
            if (shortcuts)
            {
                MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"), exe, dir);
                MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"), exe, dir);
            }
            if (register)
            {
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", AppName);
                    k.SetValue("DisplayVersion", Version);
                    k.SetValue("Publisher", "Gökhan Abi ile Projeler");
                    k.SetValue("DisplayIcon", exe + ",0");
                    k.SetValue("InstallLocation", dir);
                    k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", 20000, RegistryValueKind.DWord); // KB, launcher + downloaded game
                }
            }
            progress?.Invoke(100);
        }

        static void MakeShortcut(string lnk, string target, string workDir)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(t);
            var s = shell.CreateShortcut(lnk);
            s.TargetPath = target;
            s.WorkingDirectory = workDir;
            s.IconLocation = target + ",0";
            s.Description = "Son Durak Launcher";
            s.Save();
        }

        public static async Task<bool> InstallWebView2()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var tmp = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
            using (var wc = new WebClient()) await wc.DownloadFileTaskAsync("https://go.microsoft.com/fwlink/p/?LinkId=2124703", tmp);
            var p = Process.Start(new ProcessStartInfo(tmp, "/silent /install") { UseShellExecute = true });
            await Task.Run(() => p.WaitForExit());
            return WebView2Installed();
        }
    }

    class SetupForm : Form
    {
        readonly Label info, pathLabel;
        readonly CheckBox desktop, launch;
        readonly Button install, cancel, change;
        readonly ProgressBar bar;
        string dir;
        bool finished;

        static readonly Color Bg = Color.FromArgb(14, 17, 23), Fg = Color.FromArgb(233, 236, 239), Muted = Color.FromArgb(154, 163, 173), Amber = Color.FromArgb(255, 176, 32);

        public SetupForm(string dir)
        {
            this.dir = dir;
            Text = Tx.T("Son Durak Launcher Kurulumu", "Son Durak Launcher Setup");
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg; ForeColor = Fg;
            Font = new Font("Segoe UI", 10f);
            // layout below is in 96-dpi units; WinForms scales it on load for the real DPI
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 336);

            var title = new Label { Text = "SON DURAK", Font = new Font("Consolas", 26f, FontStyle.Bold), ForeColor = Amber, AutoSize = true, Location = new Point(28, 20) };
            var sub = new Label { Text = Tx.T("Launcher kurulumu · sürüm ", "Launcher setup · version ") + Installer.Version, ForeColor = Muted, AutoSize = true, Location = new Point(32, 70) };
            info = new Label { Text = Tx.T("Launcher kurulacak. Oyunu ilk açılışta launcher indirir (~19 MB); oyun ve launcher güncellemelerini GitHub'dan kendisi alır.", "The launcher will be installed. It downloads the game on first start (~19 MB) and keeps both the game and itself up to date from GitHub."), ForeColor = Fg, Location = new Point(32, 100), Size = new Size(510, 52) };
            pathLabel = new Label { ForeColor = Muted, Location = new Point(32, 156), Size = new Size(400, 48) };
            change = Btn(Tx.T("Değiştir…", "Change…"), new Point(440, 160), 90);
            change.Click += (o, e) =>
            {
                using (var f = new FolderBrowserDialog { SelectedPath = Path.GetDirectoryName(this.dir), Description = Tx.T("Kurulum klasörünü seçin", "Choose the install folder") })
                    if (f.ShowDialog(this) == DialogResult.OK) { this.dir = Path.Combine(f.SelectedPath, Installer.AppName); UpdatePath(); }
            };
            desktop = new CheckBox { Text = Tx.T("Masaüstüne kısayol oluştur", "Create a desktop shortcut"), Checked = true, AutoSize = true, Location = new Point(32, 212), ForeColor = Fg };
            launch = new CheckBox { Text = Tx.T("Kurulumdan sonra launcher'ı başlat", "Start the launcher after setup"), Checked = true, AutoSize = true, Location = new Point(32, 238), ForeColor = Fg };
            bar = new ProgressBar { Location = new Point(32, 290), Size = new Size(300, 14), Visible = false };
            install = Btn(Tx.T("Kur", "Install"), new Point(350, 282), 90, true);
            cancel = Btn(Tx.T("İptal", "Cancel"), new Point(450, 282), 80);
            cancel.Click += (o, e) => Close();
            install.Click += async (o, e) => await DoInstall();
            Controls.AddRange(new Control[] { title, sub, info, pathLabel, change, desktop, launch, bar, install, cancel });
            UpdatePath();
            AcceptButton = install;
        }

        Button Btn(string text, Point at, int w, bool primary = false)
        {
            var b = new Button { Text = text, Location = at, Size = new Size(w, 34), FlatStyle = FlatStyle.Flat, BackColor = primary ? Amber : Color.FromArgb(32, 37, 46), ForeColor = primary ? Color.FromArgb(26, 18, 4) : Fg, Font = new Font("Segoe UI", 10f, primary ? FontStyle.Bold : FontStyle.Regular) };
            b.FlatAppearance.BorderColor = primary ? Amber : Color.FromArgb(60, 66, 76);
            return b;
        }

        void UpdatePath() => pathLabel.Text = Tx.T("Kurulum klasörü:\n", "Install folder:\n") + dir;

        async Task DoInstall()
        {
            if (finished) { Finish(); return; }
            install.Enabled = cancel.Enabled = change.Enabled = desktop.Enabled = false;
            if (!Installer.WebView2Installed())
            {
                if (MessageBox.Show(this, Tx.T("Son Durak için Microsoft Edge WebView2 Runtime gerekiyor ve bu bilgisayarda bulunamadı.\n\nMicrosoft'un resmi yükleyicisi indirilip kurulsun mu? (~2 MB)", "Son Durak needs the Microsoft Edge WebView2 Runtime, which was not found on this computer.\n\nDownload and run Microsoft's official installer? (~2 MB)"), "WebView2", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    info.Text = Tx.T("WebView2 Runtime indiriliyor ve kuruluyor…", "Downloading and installing the WebView2 Runtime…");
                    try { await Installer.InstallWebView2(); } catch (Exception ex) { MessageBox.Show(this, Tx.T("WebView2 kurulamadı: ", "WebView2 could not be installed: ") + ex.Message, "Son Durak"); }
                }
            }
            info.Text = Tx.T("Launcher kuruluyor…", "Installing the launcher…");
            bar.Visible = true;
            try
            {
                var d = dir; var desk = desktop.Checked;
                await Task.Run(() => Installer.Install(d, true, true, p => BeginInvoke((Action)(() => bar.Value = Math.Min(100, p)))));
                if (!desk) try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Installer.AppName + ".lnk")); } catch { }
                info.Text = Tx.T("Kurulum tamamlandı! Başlat menüsünde ve " + (desk ? "masaüstünde " : "") + "\"Son Durak\" kısayolu oluşturuldu.",
                    "Setup complete! A \"Son Durak\" shortcut was added to the Start menu" + (desk ? " and the desktop." : "."));
                install.Text = Tx.T("Bitir", "Finish");
                finished = true;
                install.Enabled = true;
            }
            catch (Exception ex)
            {
                info.Text = Tx.T("Kurulum başarısız: ", "Setup failed: ") + ex.Message;
                install.Enabled = cancel.Enabled = change.Enabled = desktop.Enabled = true;
            }
        }

        void Finish()
        {
            if (launch.Checked) try { Process.Start(new ProcessStartInfo(Path.Combine(dir, Installer.Exe)) { WorkingDirectory = dir, UseShellExecute = true }); } catch { }
            Close();
        }
    }
}
