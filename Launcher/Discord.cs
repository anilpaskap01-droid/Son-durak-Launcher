// Discord Rich Presence: the player's Discord profile shows "Playing Son Durak" while the launcher is
// open ("In the launcher"), and while the game window is open where they are: main menu, a co-op
// lobby, the line + round, single player or co-op (party size), the end; plus the time and a link to
// the game's site. The launcher talks to the Discord desktop app over its local IPC pipe
// (\\.\pipe\discord-ipc-N; each frame = opcode + length + JSON), no library and nothing sent anywhere
// else. Off when the "discord" option is off or Discord is not running (then retried every 15 s).
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace SonDurakLauncher
{
    static class DiscordPresence
    {
        // the "Son Durak" application in the Discord Developer Portal (public; its name is what
        // "Playing ..." shows)
        const string AppId = "1561390026669953126";
        const string Site = "https://son-durak.pages.dev";
        static readonly JavaScriptSerializer json = new JavaScriptSerializer();
        static readonly object gate = new object();
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static Thread worker;
        static NamedPipeClientStream pipe;
        static bool launcher; // the launcher window is up
        static bool running; // the game window is open
        static long started; // since when the current status counts (unix seconds)
        static Dictionary<string, object> where; // the game's last "where" report
        static string map = "tram";
        static string sent; // the activity JSON Discord has now

        // (automated launcher tests set SONDURAK_NO_DISCORD=1 so they do not change the tester's status)
        public static bool Available => AppId.Length > 0 && Environment.GetEnvironmentVariable("SONDURAK_NO_DISCORD") != "1";
        public static string LastError { get; private set; }
        public static string LastReply { get; private set; }

        static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static void LauncherOpened() { lock (gate) { launcher = true; if (!running) started = Now; } Kick(); }
        public static void GameOpened()
        {
            lock (gate) { running = true; started = Now; where = null; map = Options.Map; }
            Kick();
        }
        public static void GameClosed() { lock (gate) { running = false; started = Now; } Kick(); }
        public static void SetMap(string m) { lock (gate) map = m == "metro" ? "metro" : "tram"; Kick(); }

        // a "stat:" report from the game: "where" (menu / lobby / round n, players, single or co-op)
        // and "start" carry the line
        public static void FromGame(string statJson)
        {
            Dictionary<string, object> ev;
            try { ev = json.Deserialize<Dictionary<string, object>>(statJson); } catch { return; }
            if (ev == null || !(ev.TryGetValue("e", out var e) && e is string kind)) return;
            lock (gate)
            {
                if (ev.TryGetValue("map", out var m) && m is string ms) map = ms == "metro" ? "metro" : "tram";
                if (kind == "where") where = ev;
            }
            if (kind == "where" || kind == "start") Kick();
        }

        // something changed (the game, settings, the language): the worker brings Discord up to date
        public static void Kick()
        {
            if (!Available) return;
            lock (gate)
            {
                if (worker == null) { worker = new Thread(Run) { IsBackground = true, Name = "discord" }; worker.Start(); }
            }
            wake.Set();
        }

        static void Run()
        {
            while (true)
            {
                bool on; string activity;
                lock (gate) { on = (launcher || running) && Options.Discord; activity = on ? json.Serialize(Build()) : null; }
                try
                {
                    // closing the connection clears the status in Discord
                    if (!on) Close();
                    else if (pipe != null || Connect())
                    {
                        if (activity != sent) { SetActivity(activity); sent = activity; }
                    }
                }
                catch (Exception ex) { LastError = ex.Message; Close(); }
                // Discord not running (or restarted): try again a little later
                wake.WaitOne(on && pipe == null ? 15000 : Timeout.Infinite);
            }
        }

        static int Num(object o) { try { return Convert.ToInt32(o); } catch { return 0; } }

        static Dictionary<string, object> Build()
        {
            var tr = Lang.Current != "en";
            var line = map == "metro" ? (tr ? "M2 Gece Metrosu" : "M2 Night Metro") : (tr ? "T9 Gece Tramvayı" : "T9 Night Tram");
            string details, state = null;
            int party = 0;
            if (!running) details = tr ? "Launcher'da" : "In the launcher";
            else
            {
                var w = where ?? new Dictionary<string, object>();
                var mode = w.TryGetValue("mode", out var mo) ? mo as string : "menu";
                var coop = w.TryGetValue("role", out var ro) && (ro as string == "host" || ro as string == "guest");
                int round = w.TryGetValue("round", out var r) ? Num(r) : 0;
                int total = w.TryGetValue("total", out var t) ? Num(t) : 10;
                int players = Math.Max(1, w.TryGetValue("players", out var p) ? Num(p) : 1);
                if (total <= 0) total = 10;
                var who = coop ? (tr ? "Co-op · " + players + " kişi" : "Co-op · " + players + (players == 1 ? " player" : " players")) : (tr ? "Tek oyunculu" : "Single player");
                switch (mode)
                {
                    case "play":
                        details = line + " · " + (tr ? "Tur " : "Round ") + Math.Max(1, round) + "/" + total;
                        state = who;
                        break;
                    case "lobby":
                        details = tr ? "Co-op lobisinde" : "In a co-op lobby";
                        state = line;
                        break;
                    case "end":
                        details = tr ? "Son Durak'a ulaştı!" : "Reached the Last Stop!";
                        state = who;
                        break;
                    default:
                        details = tr ? "Ana menüde" : "In the main menu";
                        state = line;
                        break;
                }
                if (coop && mode != "menu") party = Math.Min(players, 6);
            }
            var a = new Dictionary<string, object>
            {
                ["details"] = details,
                ["timestamps"] = new Dictionary<string, object> { ["start"] = started },
                ["assets"] = new Dictionary<string, object> { ["large_image"] = Site + "/img/icon.png", ["large_text"] = "Son Durak" },
                ["buttons"] = new object[] { new Dictionary<string, object> { ["label"] = tr ? "Son Durak'ı indir" : "Get Son Durak", ["url"] = Site } },
            };
            if (state != null) a["state"] = state;
            if (party > 0) a["party"] = new Dictionary<string, object> { ["id"] = "sondurak-" + started, ["size"] = new[] { party, 6 } };
            return a;
        }

        // ---- IPC
        static bool Connect()
        {
            for (int i = 0; i < 10; i++)
            {
                var p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { p.Connect(300); } catch { p.Dispose(); continue; }
                pipe = p;
                try
                {
                    Write(0, json.Serialize(new { v = 1, client_id = AppId }));
                    var (op, body) = Read();
                    if (op == 1 && body.Contains("\"READY\"")) { sent = null; LastError = null; return true; }
                    LastError = body; // e.g. an unknown application id
                }
                catch (Exception ex) { LastError = ex.Message; }
                Close();
            }
            return false;
        }

        static void SetActivity(string activityJson)
        {
            var payload = "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id
                + ",\"activity\":" + activityJson + "},\"nonce\":\"" + Guid.NewGuid().ToString("N") + "\"}";
            Write(1, payload);
            var (op, body) = Read();
            LastReply = body;
            // tests: SONDURAK_DISCORD_LOG=<file> gets every status that was set (one JSON per line)
            var log = Environment.GetEnvironmentVariable("SONDURAK_DISCORD_LOG");
            if (!string.IsNullOrEmpty(log)) try { System.IO.File.AppendAllText(log, activityJson + "\n"); } catch { }
            if (op == 2) throw new Exception(body); // Discord closed the connection
            LastError = body.Contains("\"ERROR\"") ? body : null;
        }

        static void Write(int op, string body)
        {
            var data = Encoding.UTF8.GetBytes(body);
            var frame = new byte[8 + data.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);
            BitConverter.GetBytes(data.Length).CopyTo(frame, 4);
            data.CopyTo(frame, 8);
            if (!pipe.WriteAsync(frame, 0, frame.Length).Wait(3000)) throw new TimeoutException("discord write");
        }

        static (int, string) Read()
        {
            var head = ReadExact(8);
            int op = BitConverter.ToInt32(head, 0), len = BitConverter.ToInt32(head, 4);
            if (len < 0 || len > 1 << 20) throw new Exception("bad frame");
            return (op, Encoding.UTF8.GetString(ReadExact(len)));
        }

        static byte[] ReadExact(int n)
        {
            var buf = new byte[n];
            int got = 0;
            while (got < n)
            {
                var t = pipe.ReadAsync(buf, got, n - got);
                if (!t.Wait(3000)) throw new TimeoutException("discord read");
                if (t.Result <= 0) throw new Exception("discord closed the pipe");
                got += t.Result;
            }
            return buf;
        }

        static void Close()
        {
            if (pipe == null) return;
            try { pipe.Dispose(); } catch { }
            pipe = null; sent = null;
        }

        // --selftest-discord <json> [seconds]: shows every kind of status for `seconds` each (launcher,
        // menu, co-op lobby, single player round, co-op round, the end) and reports Discord's answers
        public static Dictionary<string, object> Test(int seconds)
        {
            var report = new Dictionary<string, object> { ["available"] = Available };
            if (!Available) return report;
            var steps = new List<object>();
            var cases = new[]
            {
                new { name = "launcher", game = false, w = (Dictionary<string, object>)null, m = "tram" },
                new { name = "menu", game = true, w = new Dictionary<string, object> { ["mode"] = "menu", ["role"] = "single", ["players"] = 1 }, m = "tram" },
                new { name = "lobby", game = true, w = new Dictionary<string, object> { ["mode"] = "lobby", ["role"] = "host", ["players"] = 3 }, m = "metro" },
                new { name = "single", game = true, w = new Dictionary<string, object> { ["mode"] = "play", ["role"] = "single", ["round"] = 4, ["total"] = 10, ["players"] = 1 }, m = "tram" },
                new { name = "coop", game = true, w = new Dictionary<string, object> { ["mode"] = "play", ["role"] = "guest", ["round"] = 7, ["total"] = 10, ["players"] = 3 }, m = "metro" },
                new { name = "end", game = true, w = new Dictionary<string, object> { ["mode"] = "end", ["role"] = "host", ["round"] = 10, ["total"] = 10, ["players"] = 2 }, m = "metro" },
            };
            try
            {
                report["connected"] = Connect();
                bool all = pipe != null;
                foreach (var c in cases)
                {
                    if (pipe == null) break;
                    string activity;
                    lock (gate) { launcher = true; running = c.game; where = c.w; map = c.m; started = Now; activity = json.Serialize(Build()); }
                    SetActivity(activity);
                    steps.Add(new { c.name, activity, ok = LastError == null, reply = LastReply });
                    all &= LastError == null;
                    Thread.Sleep(seconds * 1000);
                }
                report["set"] = all && steps.Count == cases.Length;
            }
            catch (Exception ex) { LastError = ex.Message; }
            report["steps"] = steps;
            report["error"] = LastError;
            Close();
            return report;
        }
    }
}
