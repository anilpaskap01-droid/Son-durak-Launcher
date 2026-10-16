using System;
using System.IO;
using System.Reflection;

namespace SonDurakLauncher
{
    static class Ui
    {
        static byte[] Resource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var m = new MemoryStream()) { s.CopyTo(m); return m.ToArray(); }
        }

        // The launcher page with its background image inlined (NavigateToString needs one string).
        public static string LauncherHtml()
        {
            var html = System.Text.Encoding.UTF8.GetString(Resource("launcher.html"));
            var bg = "data:image/jpeg;base64," + Convert.ToBase64String(Resource("bg.jpg"));
            return html.Replace("{{BG}}", bg);
        }
    }
}
