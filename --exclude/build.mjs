// Builds the Son Durak Launcher and its setup (run from anywhere: node build.mjs):
//   Launcher/ -> release/ (pushed to this repository: installed launchers update themselves from it)
//             -> Setup/payload.zip (+ token.bin) -> Setup/ -> dist/SonDurak-Setup.exe
// Fails if the setup is 10 MB or larger, or if the token ended up inside the published exe.
import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const L = dirname(fileURLToPath(import.meta.url));
const run = (cmd, args, cwd) => execFileSync(cmd, args, { cwd, stdio: 'inherit' });

// ---- read-only GitHub token for the private repos (SONDURAK_READ_TOKEN, user or process env)
const OWNER = 'anilpaskap01-droid', REPOS = ['Gokhan-Abi', 'Son-durak-Launcher'];
const KEY = Buffer.from('SonDurak-T9-Gece-Hatti', 'ascii'); // same key as Updater.cs (obfuscation only)
let token = process.env.SONDURAK_READ_TOKEN;
if (!token) {
  try { token = execFileSync('powershell.exe', ['-NoProfile', '-Command', "[Environment]::GetEnvironmentVariable('SONDURAK_READ_TOKEN','User')"]).toString().trim(); } catch { token = ''; }
}
const tokenFile = join(L, 'Launcher', 'token.bin');
if (token) {
  const gh = (repo, method, path, body) => fetch(`https://api.github.com/repos/${OWNER}/${repo}${path}`, {
    method, body: body && JSON.stringify(body),
    headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json', 'User-Agent': 'sondurak-build', 'Content-Type': 'application/json' },
  });
  for (const repo of REPOS) {
    const read = await gh(repo, 'GET', '/commits?per_page=1');
    if (!read.ok) throw new Error(`SONDURAK_READ_TOKEN cannot read ${OWNER}/${repo} (HTTP ${read.status})`);
    // A harmless write probe (an unreferenced blob): it must be refused, otherwise the token is not read-only.
    const write = await gh(repo, 'POST', '/git/blobs', { content: 'son-durak read-only check', encoding: 'utf-8' });
    if (write.ok) throw new Error(`SONDURAK_READ_TOKEN can WRITE to ${repo} - create a token with Contents: Read-only and try again.`);
  }
  const b = Buffer.from(token, 'ascii');
  for (let i = 0; i < b.length; i++) b[i] ^= KEY[i % KEY.length];
  writeFileSync(tokenFile, b);
  console.log(`read-only token ready for the setup payload (verified on ${REPOS.join(', ')}: can read, cannot write)`);
} else {
  if (existsSync(tokenFile)) rmSync(tokenFile);
  console.log('WARNING: no SONDURAK_READ_TOKEN - the launcher will only work if the repositories are public');
}

run('dotnet', ['build', '-c', 'Release', '-nologo', '-v', 'q'], join(L, 'Launcher'));
const out = join(L, 'Launcher', 'bin', 'Release', 'net48');
const files = ['SonDurakLauncher.exe', 'SonDurakLauncher.exe.config', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll'];
for (const f of files) if (!existsSync(join(out, f))) throw new Error('missing ' + f);

// the published launcher (no token inside: the repo copy is what installed launchers update to)
const release = join(L, 'release');
rmSync(release, { recursive: true, force: true });
mkdirSync(release, { recursive: true });
for (const f of files) copyFileSync(join(out, f), join(release, f));
if (token) {
  const exe = readFileSync(join(release, 'SonDurakLauncher.exe'));
  if (exe.includes(Buffer.from(token, 'ascii')) || exe.includes(readFileSync(tokenFile))) throw new Error('the token is inside SonDurakLauncher.exe - it must not be published');
}
console.log('release/ ' + files.join(', '));

const payload = join(L, 'Setup', 'payload.zip');
if (existsSync(payload)) rmSync(payload);
const list = [...files.map((f) => join(release, f)), ...(token ? [tokenFile] : [])].map((f) => `'${f}'`).join(',');
run('powershell.exe', ['-NoProfile', '-Command', `Compress-Archive -Path ${list} -DestinationPath '${payload}' -CompressionLevel Optimal`], L);

run('dotnet', ['build', '-c', 'Release', '-nologo', '-v', 'q'], join(L, 'Setup'));
mkdirSync(join(L, 'dist'), { recursive: true });
const setup = join(L, 'dist', 'SonDurak-Setup.exe');
copyFileSync(join(L, 'Setup', 'bin', 'Release', 'net48', 'SonDurakSetup.exe'), setup);
const size = statSync(setup).size;
console.log(`payload.zip ${(statSync(payload).size / 1024).toFixed(0)} KB`);
console.log(`dist/SonDurak-Setup.exe ${(size / 1024 / 1024).toFixed(2)} MB`);
if (size >= 10 * 1024 * 1024) throw new Error('setup is not under 10 MB');
