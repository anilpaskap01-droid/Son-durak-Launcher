// Builds the Microsoft Store package of the launcher: dist/SonDurak-Store-<version>.msix
// (run `node build.mjs` first: it builds release/ and Launcher/token.bin).
// The Store edition is this same launcher: it downloads and updates the game, keeps friends, stats and
// the Discord status; it only never replaces its own files (Config.Store, Updater.cs): the Store updates
// it. The identity is the Partner Center product's (Product management > Product identity). The package
// is not signed: the Store signs it. Needs the Windows SDK (makeappx.exe).
// usage: node store-msix.mjs [package version, e.g. 1.9.5.0 — must grow with every Store submission]
import { execFileSync } from 'node:child_process';
import { cpSync, existsSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const L = dirname(fileURLToPath(import.meta.url));
const IDENTITY = { name: 'SonVagonStudios.SonDurak', publisher: 'CN=B582E6A7-A11E-42D4-BF5B-FE0C9488C439', publisherName: 'Son Vagon Studios' };
const version = process.argv[2] || '1.9.4.0';
if (!/^\d+\.\d+\.\d+\.0$/.test(version)) throw new Error('version must look like 1.9.4.0 (the Store needs the last number to be 0)');

const release = join(L, 'release'), token = join(L, 'Launcher', 'token.bin');
if (!existsSync(join(release, 'SonDurakLauncher.exe'))) throw new Error('no release/: run node build.mjs first');
if (!existsSync(token)) throw new Error('no Launcher/token.bin: run node build.mjs with SONDURAK_READ_TOKEN set');
const kits = 'C:/Program Files (x86)/Windows Kits/10/bin';
const sdk = readdirSync(kits).filter((d) => /^10\./.test(d) && existsSync(join(kits, d, 'x64', 'makeappx.exe'))).sort().pop();
if (!sdk) throw new Error('makeappx.exe not found (install the Windows SDK)');

const layout = join(L, 'dist', 'store-layout');
const out = join(L, 'dist', `SonDurak-Store-${version}.msix`);
rmSync(layout, { recursive: true, force: true });
cpSync(release, layout, { recursive: true });
cpSync(token, join(layout, 'token.bin')); // the read-only game download token, as the setup ships it
cpSync(join(L, 'store', 'Assets'), join(layout, 'Assets'), { recursive: true });

const desc = 'Son Durak: a co-op anomaly-spotting game on a night tram. The launcher downloads and updates the game. / Gece tramvayında co-op anomali avı; launcher oyunu indirir ve günceller.';
writeFileSync(join(layout, 'AppxManifest.xml'), `<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap rescap">
  <Identity Name="${IDENTITY.name}" Publisher="${IDENTITY.publisher}" Version="${version}" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>Son Durak</DisplayName>
    <PublisherDisplayName>${IDENTITY.publisherName}</PublisherDisplayName>
    <Logo>Assets\\StoreLogo.png</Logo>
    <Description>${desc}</Description>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Resources>
    <Resource Language="tr-TR" />
    <Resource Language="en-US" />
  </Resources>
  <Applications>
    <Application Id="SonDurak" Executable="SonDurakLauncher.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="Son Durak" Description="${desc}" BackgroundColor="transparent"
        Square150x150Logo="Assets\\Square150x150Logo.png" Square44x44Logo="Assets\\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\\Wide310x150Logo.png" />
        <uap:SplashScreen Image="Assets\\SplashScreen.png" BackgroundColor="#0a0d14" />
      </uap:VisualElements>
    </Application>
  </Applications>
  <Capabilities>
    <Capability Name="internetClient" />
    <rescap:Capability Name="runFullTrust" />
    <DeviceCapability Name="microphone" />
  </Capabilities>
</Package>
`);
rmSync(out, { force: true });
execFileSync(join(kits, sdk, 'x64', 'makeappx.exe'), ['pack', '/d', layout, '/p', out, '/o'], { stdio: 'ignore' });
console.log(`msix -> ${out} (${IDENTITY.name} ${version})`);
