// "SonDurakLauncher.exe --uninstall" (registered by the setup in Apps & features).
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SonDurakLauncher
{
    static class Uninstaller
    {
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SonDurak";

        public static void Run()
        {
            if (MessageBox.Show(Lang.T("Son Durak Launcher bilgisayarından kaldırılsın mı?", "Remove Son Durak Launcher from this computer?"), "Son Durak", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var removeData = MessageBox.Show(Lang.T("İndirilen oyun dosyaları ve oyun ayarları da silinsin mi?", "Also delete the downloaded game files and game settings?"), "Son Durak", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

            string installDir = null;
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey)) installDir = k?.GetValue("InstallLocation") as string;

            foreach (var lnk in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Son Durak.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Son Durak.lnk"),
            })
                try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }

            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            if (removeData) try { Directory.Delete(Config.DataDir, true); } catch { }

            // Remove the install folder after this process exits — only the folder the setup created.
            var exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (!string.IsNullOrEmpty(installDir) && string.Equals(Path.GetFullPath(installDir).TrimEnd('\\'), exeDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(exeDir, "SonDurakLauncher.exe")))
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{exeDir}\"")
                { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false });
            }
            MessageBox.Show(Lang.T("Son Durak kaldırıldı.", "Son Durak has been removed."), "Son Durak", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
