// Keeps a local copy of the game in sync with the "Son Durak" folder of the GitHub repo.
// - CheckAsync: latest commits that touched the folder (version = commit SHA)
// - InstallAsync: downloads only files whose git blob SHA changed, verifies every file,
//   removes files that disappeared from the repo, then records the new version.
// - LauncherPlanAsync / ApplyLauncherUpdateAsync: the launcher updates itself from the built
//   copy in the "release/" folder of its own repository, Son-durak-Launcher (same blob-SHA
//   comparison, swap by renaming).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SonDurakLauncher
{
    static class Config
    {
        public const string Owner = "anilpaskap01-droid";
        public const string Repo = "Gokhan-Abi";
        public const string Branch = "main";
        public const string Folder = "Son Durak";
        // only the files the game needs at runtime (the repo folder also has tools, launcher sources …)
        public static readonly string[] Runtime = { "index.html", "CREDITS.md", "news.json", "css/", "js/", "vendor/", "assets/" };

        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonDurak");
        public static string GameDir => Path.Combine(DataDir, "game");
        public static string StateFile => Path.Combine(DataDir, "state.json");
        public static string WebViewData => Path.Combine(DataDir, "webview");
        public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";
        // the launcher's own repository: sources + the built launcher in release/ (exe + its dlls)
        public const string LauncherRepo = "Son-durak-Launcher";
        public const string ReleaseFolder = "release/";
        public static string LauncherRepoUrl => $"https://github.com/{Owner}/{LauncherRepo}";
        public const string LauncherExe = "SonDurakLauncher.exe";
        public static string AppDir => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

        // Read-only token for the private repo: token.bin next to the exe (written by the setup, never
        // committed and never part of a launcher update), or SONDURAK_GITHUB_TOKEN on a developer PC.
        // Anyone holding the file can extract it, so it must only ever have Contents: Read on this repo.
        public static string Token
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("SONDURAK_GITHUB_TOKEN");
                return string.IsNullOrEmpty(env) ? fileToken : env;
            }
        }
        // (TokenKey must be initialised before fileToken: static initialisers run in order)
        internal static readonly byte[] TokenKey = Encoding.ASCII.GetBytes("SonDurak-T9-Gece-Hatti");
        static readonly string fileToken = LoadToken();

        static string LoadToken()
        {
            try
            {
                byte[] b = null;
                var path = Path.Combine(AppDir, "token.bin");
                if (File.Exists(path)) b = File.ReadAllBytes(path);
                else
                    // launchers before 1.1 carried it inside the exe
                    using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("token.bin"))
                        if (s != null) { b = new byte[s.Length]; s.Read(b, 0, b.Length); }
                if (b == null || b.Length == 0) return null;
                for (int i = 0; i < b.Length; i++) b[i] ^= TokenKey[i % TokenKey.Length];
                return Encoding.ASCII.GetString(b).Trim();
            }
            catch { return null; }
        }
    }

    class LauncherFile
    {
        public string Name;
        public string Sha;
        public long Size;
    }

    class CommitInfo
    {
        public string Sha;
        public string Message;
        public string Date;
    }

    class RemoteInfo
    {
        public string Sha;
        public string Message;
        public string Date;
        public List<CommitInfo> Log = new List<CommitInfo>();
    }

    class LocalState
    {
        public string sha;
        public string message;
        public string date;
        public Dictionary<string, string> files = new Dictionary<string, string>();
    }

    class Progress
    {
        public long Done;
        public long Total;
        public int Files;
        public int FilesTotal;
        public string Current;
    }

    static class Updater
    {
        static readonly HttpClient http;
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static Updater()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SonDurakLauncher/1.0");
            if (!string.IsNullOrEmpty(Config.Token)) http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Config.Token);
        }

        static string Api(string path, string repo = null) => $"https://api.github.com/repos/{Config.Owner}/{repo ?? Config.Repo}{path}";

        static async Task<T> GetJson<T>(string url, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using (var res = await http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (res.StatusCode == HttpStatusCode.Unauthorized) throw new Exception("auth");
                    if (res.StatusCode == HttpStatusCode.NotFound) throw new Exception("repo-not-found");
                    if ((int)res.StatusCode == 403 || (int)res.StatusCode == 429) throw new Exception("rate-limit");
                    if (!res.IsSuccessStatusCode) throw new Exception("HTTP " + (int)res.StatusCode);
                    return json.Deserialize<T>(body);
                }
            }
        }

        public static LocalState LoadState()
        {
            try
            {
                if (File.Exists(Config.StateFile)) return json.Deserialize<LocalState>(File.ReadAllText(Config.StateFile, Encoding.UTF8)) ?? new LocalState();
            }
            catch { /* corrupt state: treat as not installed */ }
            return new LocalState();
        }

        static void SaveState(LocalState s)
        {
            Directory.CreateDirectory(Config.DataDir);
            var tmp = Config.StateFile + ".tmp";
            File.WriteAllText(tmp, json.Serialize(s), Encoding.UTF8);
            if (File.Exists(Config.StateFile)) File.Delete(Config.StateFile);
            File.Move(tmp, Config.StateFile);
        }

        public static bool IsInstalled(LocalState s) => !string.IsNullOrEmpty(s.sha) && File.Exists(Path.Combine(Config.GameDir, "index.html"));

        public static async Task<RemoteInfo> CheckAsync(CancellationToken ct)
        {
            var url = Api($"/commits?sha={Config.Branch}&path={Uri.EscapeDataString(Config.Folder)}&per_page=6");
            var list = await GetJson<List<Dictionary<string, object>>>(url, ct).ConfigureAwait(false);
            if (list == null || list.Count == 0) throw new Exception("no-commits");
            var info = new RemoteInfo();
            foreach (var c in list)
            {
                var commit = (Dictionary<string, object>)c["commit"];
                var committer = (Dictionary<string, object>)commit["committer"];
                var msg = (string)commit["message"];
                info.Log.Add(new CommitInfo { Sha = (string)c["sha"], Message = msg.Split('\n')[0].Trim(), Date = (string)committer["date"] });
            }
            info.Sha = info.Log[0].Sha;
            info.Message = info.Log[0].Message;
            info.Date = info.Log[0].Date;
            return info;
        }

        static bool IsRuntime(string rel) => Config.Runtime.Any(p => p.EndsWith("/") ? rel.StartsWith(p, StringComparison.Ordinal) : rel == p);

        // git's blob id: sha1("blob <len>\0" + content)
        public static string GitBlobSha(byte[] data)
        {
            using (var sha = SHA1.Create())
            {
                var header = Encoding.ASCII.GetBytes("blob " + data.Length + "\0");
                sha.TransformBlock(header, 0, header.Length, null, 0);
                sha.TransformFinalBlock(data, 0, data.Length);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        // Every file of the repo folder at a commit: path relative to the folder -> (blob sha, size)
        static async Task<Dictionary<string, (string sha, long size)>> GetFolderAsync(string commitSha, CancellationToken ct)
        {
            var tree = await GetJson<Dictionary<string, object>>(Api($"/git/trees/{commitSha}?recursive=1"), ct).ConfigureAwait(false);
            if (tree.ContainsKey("truncated") && (bool)tree["truncated"]) throw new Exception("tree-truncated");
            var prefix = Config.Folder + "/";
            var files = new Dictionary<string, (string sha, long size)>();
            foreach (var o in (System.Collections.IEnumerable)tree["tree"])
            {
                var e = (Dictionary<string, object>)o;
                if ((string)e["type"] != "blob") continue;
                var path = (string)e["path"];
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                files[path.Substring(prefix.Length)] = ((string)e["sha"], Convert.ToInt64(e["size"]));
            }
            return files;
        }

        // Runtime files of the game at a commit: relative path -> (blob sha, size)
        static async Task<Dictionary<string, (string sha, long size)>> GetWantedAsync(string commitSha, CancellationToken ct)
        {
            var wanted = (await GetFolderAsync(commitSha, ct).ConfigureAwait(false)).Where(kv => IsRuntime(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (!wanted.ContainsKey("index.html")) throw new Exception("bad-remote");
            return wanted;
        }

        // Player-facing patch notes and game info ("Son Durak/news.json") at a commit; null if missing.
        public static async Task<string> NewsJsonAsync(string commitSha, CancellationToken ct)
        {
            var path = string.Join("/", (Config.Folder + "/news.json").Split('/').Select(Uri.EscapeDataString));
            var url = !string.IsNullOrEmpty(Config.Token) ? Api($"/contents/{path}?ref={commitSha}") : $"https://raw.githubusercontent.com/{Config.Owner}/{Config.Repo}/{commitSha}/{path}";
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Accept.ParseAdd("application/vnd.github.raw");
                using (var res = await http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if (res.StatusCode == HttpStatusCode.NotFound) return null;
                    res.EnsureSuccessStatusCode();
                    return Encoding.UTF8.GetString(await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
                }
            }
        }

        // ---------------------------------------------------------------- launcher self-update
        // Never on a developer build (bin\Debug|Release) or when SONDURAK_NO_SELFUPDATE=1.
        public static bool SelfUpdateAllowed =>
            Environment.GetEnvironmentVariable("SONDURAK_NO_SELFUPDATE") != "1"
            && Config.AppDir.IndexOf(@"\bin\", StringComparison.OrdinalIgnoreCase) < 0;

        // Launcher files in release/ of the launcher repository (latest commit on the branch) that
        // differ from the ones next to this exe. Returns that commit and the files to replace.
        public static async Task<(string commit, List<LauncherFile> files)> LauncherPlanAsync(CancellationToken ct)
        {
            var commits = await GetJson<List<Dictionary<string, object>>>(Api($"/commits?sha={Config.Branch}&path=release&per_page=1", Config.LauncherRepo), ct).ConfigureAwait(false);
            if (commits == null || commits.Count == 0) return (null, new List<LauncherFile>());
            var commit = (string)commits[0]["sha"];
            var tree = await GetJson<Dictionary<string, object>>(Api($"/git/trees/{commit}?recursive=1", Config.LauncherRepo), ct).ConfigureAwait(false);
            var release = new List<LauncherFile>();
            foreach (var o in (System.Collections.IEnumerable)tree["tree"])
            {
                var e = (Dictionary<string, object>)o;
                var path = (string)e["path"];
                if ((string)e["type"] != "blob" || !path.StartsWith(Config.ReleaseFolder, StringComparison.Ordinal) || path.IndexOf('/', Config.ReleaseFolder.Length) >= 0) continue;
                release.Add(new LauncherFile { Name = path.Substring(Config.ReleaseFolder.Length), Sha = (string)e["sha"], Size = Convert.ToInt64(e["size"]) });
            }
            // nothing published (or a broken upload without the exe): leave the launcher alone
            if (!release.Any(f => string.Equals(f.Name, Config.LauncherExe, StringComparison.OrdinalIgnoreCase))) return (commit, new List<LauncherFile>());
            return (commit, release.Where(f =>
            {
                var local = Path.Combine(Config.AppDir, f.Name);
                return !File.Exists(local) || GitBlobSha(File.ReadAllBytes(local)) != f.Sha;
            }).ToList());
        }

        // Download + verify into a staging folder, then swap: a running exe or loaded dll cannot be
        // overwritten or deleted, but it can be renamed, so the old files become *.old (deleted on
        // the next start). Any failure puts the old files back.
        public static async Task ApplyLauncherUpdateAsync(string commitSha, List<LauncherFile> files, IProgress<Progress> progress, CancellationToken ct)
        {
            var stage = Path.Combine(Config.DataDir, "launcher-update");
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);
            var p = new Progress { Total = files.Sum(f => f.Size), FilesTotal = files.Count };
            progress?.Report(p);
            foreach (var f in files)
            {
                var data = await DownloadAsync(commitSha, Config.ReleaseFolder + f.Name, f.Sha, ct, Config.LauncherRepo).ConfigureAwait(false);
                if (GitBlobSha(data) != f.Sha) throw new Exception("checksum " + f.Name);
                File.WriteAllBytes(Path.Combine(stage, f.Name), data);
                p.Done += f.Size; p.Files++; p.Current = f.Name;
                progress?.Report(p);
            }

            var swapped = new List<(string target, string old)>();
            try
            {
                foreach (var f in files)
                {
                    var target = Path.Combine(Config.AppDir, f.Name);
                    string old = null;
                    if (File.Exists(target))
                    {
                        old = target + ".old";
                        if (File.Exists(old)) { try { File.Delete(old); } catch { old = target + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".old"; } }
                        File.Move(target, old);
                    }
                    swapped.Add((target, old));
                    File.Move(Path.Combine(stage, f.Name), target);
                }
            }
            catch
            {
                for (var i = swapped.Count - 1; i >= 0; i--)
                {
                    var (target, old) = swapped[i];
                    try
                    {
                        if (old == null) { if (File.Exists(target)) File.Delete(target); continue; }
                        if (File.Exists(old)) { if (File.Exists(target)) File.Delete(target); File.Move(old, target); }
                    }
                    catch { /* best effort */ }
                }
                throw;
            }
            try { Directory.Delete(stage, true); } catch { }
        }

        // Leftovers of the previous launcher version (they were still running when replaced).
        public static void CleanupOldLauncherFiles()
        {
            try { foreach (var f in Directory.GetFiles(Config.AppDir, "*.old")) try { File.Delete(f); } catch { } }
            catch { }
        }

        // Which runtime files differ from what is installed.
        static List<string> ComputeTodo(Dictionary<string, (string sha, long size)> wanted, LocalState state)
        {
            var todo = new List<string>();
            foreach (var kv in wanted)
            {
                var local = LocalPath(kv.Key);
                string have = null;
                if (File.Exists(local))
                {
                    if (state.files != null && state.files.TryGetValue(kv.Key, out var recorded) && new FileInfo(local).Length == kv.Value.size) have = recorded;
                    else have = GitBlobSha(File.ReadAllBytes(local));
                }
                if (have != kv.Value.sha) todo.Add(kv.Key);
            }
            return todo;
        }

        public class Plan { public int Files; public long Bytes; public bool Removals; }

        // What an update to `info` would download (0 files = only non-game files changed).
        public static async Task<Plan> PlanAsync(RemoteInfo info, CancellationToken ct)
        {
            var wanted = await GetWantedAsync(info.Sha, ct).ConfigureAwait(false);
            var state = LoadState();
            var todo = ComputeTodo(wanted, state);
            return new Plan { Files = todo.Count, Bytes = todo.Sum(k => wanted[k].size), Removals = state.files != null && state.files.Keys.Any(k => !wanted.ContainsKey(k)) };
        }

        // The commit changed nothing the game uses (e.g. launcher sources): just record it.
        public static void MarkCurrent(RemoteInfo info)
        {
            var s = LoadState();
            s.sha = info.Sha; s.message = info.Message; s.date = info.Date;
            SaveState(s);
        }

        public static async Task InstallAsync(RemoteInfo info, IProgress<Progress> progress, CancellationToken ct)
        {
            var wanted = await GetWantedAsync(info.Sha, ct).ConfigureAwait(false);
            var state = LoadState();
            Directory.CreateDirectory(Config.GameDir);
            var todo = ComputeTodo(wanted, state);

            var p = new Progress { Total = todo.Sum(k => wanted[k].size), FilesTotal = todo.Count };
            progress?.Report(p);
            var gate = new SemaphoreSlim(4);
            var tasks = todo.Select(async rel =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var (sha, size) = wanted[rel];
                    var data = await DownloadAsync(info.Sha, rel, sha, ct).ConfigureAwait(false);
                    if (GitBlobSha(data) != sha) throw new Exception("checksum " + rel);
                    var local = LocalPath(rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(local));
                    var tmp = local + ".part";
                    File.WriteAllBytes(tmp, data);
                    if (File.Exists(local)) File.Delete(local);
                    File.Move(tmp, local);
                    lock (p) { p.Done += size; p.Files++; p.Current = rel; }
                    progress?.Report(p);
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);

            // remove files that are no longer part of the game
            foreach (var file in Directory.GetFiles(Config.GameDir, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(Config.GameDir.Length + 1).Replace('\\', '/');
                if (!wanted.ContainsKey(rel)) { try { File.Delete(file); } catch { } }
            }
            foreach (var dir in Directory.GetDirectories(Config.GameDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) { try { Directory.Delete(dir); } catch { } }

            SaveState(new LocalState { sha = info.Sha, message = info.Message, date = info.Date, files = wanted.ToDictionary(k => k.Key, k => k.Value.sha) });
        }

        static string LocalPath(string rel) => Path.Combine(Config.GameDir, rel.Replace('/', Path.DirectorySeparatorChar));

        // repo = null: the game repository (paths are relative to its "Son Durak" folder);
        // otherwise paths are relative to that repository's root.
        static async Task<byte[]> DownloadAsync(string commitSha, string rel, string blobSha, CancellationToken ct, string repo = null)
        {
            string url;
            var req = new HttpRequestMessage(HttpMethod.Get, "https://x");
            if (!string.IsNullOrEmpty(Config.Token))
            {
                // private repo: the blob API returns raw bytes with this media type
                url = Api($"/git/blobs/{blobSha}", repo);
                req.Headers.Accept.ParseAdd("application/vnd.github.raw");
            }
            else
            {
                var path = string.Join("/", (repo == null ? Config.Folder + "/" + rel : rel).Split('/').Select(Uri.EscapeDataString));
                url = $"https://raw.githubusercontent.com/{Config.Owner}/{repo ?? Config.Repo}/{commitSha}/{path}";
            }
            req.RequestUri = new Uri(url);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using (var clone = new HttpRequestMessage(HttpMethod.Get, req.RequestUri))
                    {
                        foreach (var h in req.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
                        using (var res = await http.SendAsync(clone, ct).ConfigureAwait(false))
                        {
                            res.EnsureSuccessStatusCode();
                            return await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        }
                    }
                }
                catch when (attempt < 3 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(800 * attempt, ct).ConfigureAwait(false);
                }
            }
        }
    }
}
