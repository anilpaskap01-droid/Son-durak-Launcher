// The game library: every game this launcher installs, updates and runs. Each one is a folder of the
// Gokhan-Abi repository (same read-only token), downloaded into its own folder under the data dir and
// served in the game window from its own virtual host.
//   sondurak  "Son Durak/"        -> DataDir\game            state.json          https://sondurak.game/
//   firtina   "Firtina Teknesi/"  -> DataDir\games\firtina   state-firtina.json  https://firtina.game/
// (Son Durak keeps the paths launchers before 1.9 used, so existing installs need no new download.)
// A game whose folder has no commits yet shows as "coming soon" and is picked up on a later check.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SonDurakLauncher
{
    sealed class GameDef
    {
        public string Id;        // used by the page, launcher.json keys and self-test arguments
        public string Name;      // window title, Discord
        public string Folder;    // folder of the game repository
        public string Dir;       // local install
        public string StateFile; // installed commit + file hashes (Updater.LocalState)
        public string NewsCache; // the latest news.json downloaded for the game page
        public string Host;      // virtual host the game window serves Dir from
        // only the files the game needs at runtime (the repo folder also has tools, sources …)
        public string[] Runtime = { "index.html", "CREDITS.md", "news.json", "css/", "js/", "vendor/", "assets/" };
        // friends, invites, parties, the line picker, stats / quests / collection: Son Durak only for now
        public bool Social;

        // launcher.json keys of per-game values (play time …); Son Durak keeps its pre-1.9 keys
        public string PrefKey(string key) => this == Games.SonDurak ? key : key + "." + Id;
        public override string ToString() => Id;
    }

    static class Games
    {
        public static readonly GameDef SonDurak = new GameDef
        {
            Id = "sondurak", Name = "Son Durak", Folder = "Son Durak", Host = "sondurak.game", Social = true,
            Dir = Path.Combine(Config.DataDir, "game"),
            StateFile = Path.Combine(Config.DataDir, "state.json"),
            NewsCache = Path.Combine(Config.DataDir, "news.json"),
        };

        public static readonly GameDef Firtina = new GameDef
        {
            Id = "firtina", Name = "Fırtına Teknesi", Host = "firtina.game",
            // TEST HOOK: SONDURAK_FIRTINA_FOLDER=<another folder of the repo, e.g. "Son Durak"> installs that
            // folder as Fırtına Teknesi, to test a real install before "Firtina Teknesi/" is published.
            Folder = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SONDURAK_FIRTINA_FOLDER"))
                ? "Firtina Teknesi" : Environment.GetEnvironmentVariable("SONDURAK_FIRTINA_FOLDER"),
            Dir = Path.Combine(Config.DataDir, "games", "firtina"),
            StateFile = Path.Combine(Config.DataDir, "state-firtina.json"),
            NewsCache = Path.Combine(Config.DataDir, "news-firtina.json"),
        };

        public static readonly GameDef[] All = { SonDurak, Firtina };

        public static GameDef Get(string id) => All.FirstOrDefault(g => g.Id == id);
    }

    // What the launcher knows about one game right now: the latest version on GitHub, what an update
    // would download, whether the game is published at all, the last error.
    sealed class GameRun
    {
        public readonly GameDef Def;
        public RemoteInfo Remote;    // null: not checked yet, offline, or not published
        public Updater.Plan Plan;
        public bool ComingSoon;      // the game's folder has no commits (or no index.html) on GitHub yet
        public string Error;

        public GameRun(GameDef def) { Def = def; }

        public LocalState Local => Updater.LoadState(Def);
        public bool Installed => Updater.IsInstalled(Def, Local);
        public bool UpdateWaiting { get { var s = Local; return Remote != null && Updater.IsInstalled(Def, s) && Remote.Sha != s.sha; } }

        // The latest commit of the game's folder, its news.json and what an update would download.
        // Network errors are thrown (the caller decides between "offline" and an error).
        public async Task CheckAsync(CancellationToken ct)
        {
            Remote = null; Plan = null;
            var remote = await Updater.CheckAsync(Def, ct).ConfigureAwait(false);
            var st = Updater.LoadState(Def);
            var installed = Updater.IsInstalled(Def, st);
            if (remote == null) { NotPublished(installed); return; }
            if (remote.Sha != st.sha)
            {
                Updater.Plan plan;
                try { plan = await Updater.PlanAsync(Def, remote, ct).ConfigureAwait(false); }
                // (the folder exists but has no game in it yet: still coming soon)
                catch (Exception ex) when (ex.Message == "bad-remote" && !installed) { NotPublished(false); return; }
                // only launcher / tool files changed: nothing to download, the game is current
                if (installed && plan.Files == 0 && !plan.Removals) { Updater.MarkCurrent(Def, remote); plan = null; }
                Plan = plan;
            }
            ComingSoon = false;
            Remote = remote;
            try
            {
                var news = await Updater.NewsJsonAsync(Def, remote.Sha, ct).ConfigureAwait(false);
                if (news != null) { Directory.CreateDirectory(Path.GetDirectoryName(Def.NewsCache)); File.WriteAllText(Def.NewsCache, news, System.Text.Encoding.UTF8); }
            }
            catch { /* the notes are nice to have; keep the last ones */ }
        }

        void NotPublished(bool installed)
        {
            ComingSoon = true;
            // notes of a game that is not out are stale (e.g. from a test install)
            if (!installed) try { if (File.Exists(Def.NewsCache)) File.Delete(Def.NewsCache); } catch { }
        }

        // What the launcher page shows for this game (LauncherForm adds busy / self-update / offline).
        public string Status(bool selfUpdating, bool busyHere, bool repairing, bool offline)
        {
            var st = Local;
            var installed = Updater.IsInstalled(Def, st);
            if (selfUpdating) return "selfUpdate";
            if (busyHere) return repairing ? "repair" : "busy";
            if (ComingSoon && !installed) return "comingSoon";
            if (Remote == null && !offline && Error == null && !ComingSoon) return "checking";
            if (!installed) return Remote == null ? "offlineNoGame" : "notInstalled";
            if (Remote != null && Remote.Sha != st.sha) return "update";
            return "ready";
        }
    }
}
