// Download the WebView package into the repo-local offline feed.
//
// Why: in this sandbox .NET cannot reach nuget.org (Windows schannel credential
// store is blocked), while Node's OpenSSL stack works. So packages that the
// machine-wide cache lacks are fetched here and served from `packages/`, which
// NuGet.Config registers as a local source. On an unrestricted machine you can
// delete NuGet.Config and restore from nuget.org normally.
import fs from 'node:fs';
import path from 'node:path';

const PACKAGES = [
  ['avalonia.controls.webview', '12.1.0'],
];

const outDir = path.resolve('packages');
fs.mkdirSync(outDir, { recursive: true });

for (const [id, version] of PACKAGES) {
  const file = path.join(outDir, `${id}.${version}.nupkg`);
  if (fs.existsSync(file)) {
    console.log(`skip   ${id} ${version} (already present)`);
    continue;
  }

  const url = `https://api.nuget.org/v3-flatcontainer/${id}/${version}/${id}.${version}.nupkg`;
  const r = await fetch(url, { signal: AbortSignal.timeout(120000) });
  if (!r.ok) {
    console.error(`FAIL   ${id} ${version} -> HTTP ${r.status}`);
    process.exitCode = 1;
    continue;
  }

  const buf = Buffer.from(await r.arrayBuffer());
  fs.writeFileSync(file, buf);
  console.log(`saved  ${file} (${buf.length} bytes)`);
}
