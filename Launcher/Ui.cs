// The launcher page is served from a folder (written from the exe's resources at every start) as
// https://launcher.sondurak/ — a secure origin, so WebRTC (the friends system) and the page's
// storage (friends, chats) work and persist in the WebView2 profile.
using System.IO;
using System.Reflection;

namespace SonDurakLauncher
{
    static class Ui
    {
        public const string Host = "launcher.sondurak";
        public static string Url => $"https://{Host}/launcher.html";
        public static string Folder => Path.Combine(Config.DataDir, "launcher-ui");
        static readonly string[] Files = { "launcher.html", "bg.jpg", "peerjs.min.js", "LICENSE-peerjs.txt" };

        public static void Prepare()
        {
            Directory.CreateDirectory(Folder);
            foreach (var f in Files) File.WriteAllBytes(Path.Combine(Folder, f), Resource(f));
        }

        static byte[] Resource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var m = new MemoryStream()) { s.CopyTo(m); return m.ToArray(); }
        }
    }
}
