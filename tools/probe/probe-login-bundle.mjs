// Download the login-page bundle and look for the QR login endpoint chain,
// which the earlier research proved is NOT in the main student bundle.
import fs from 'node:fs';
import path from 'node:path';
import { repoRoot } from './session-cookies.mjs';

const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36';
const urls = [
  'https://fe-static-yuketang.yuketang.cn/fe/static/vue/2.2.680/login.js',
  'https://changjiang.yuketang.cn/static/vue/login.js',
];

const dir = path.join(repoRoot, 'docs', 'research');
fs.mkdirSync(dir, { recursive: true });

const terms = [
  'qr', 'Qr', 'QR', 'wechat', 'weixin', 'wxcode', 'auth-param', 'gen_token', 'login_status',
  'check_login', 'poll', 'scan', 'ticket', 'sessionid', 'is_login', 'open.weixin', 'qrcode',
];

for (const u of urls) {
  try {
    const r = await fetch(u, {
      headers: { 'User-Agent': UA, Referer: 'https://changjiang.yuketang.cn/web' },
      signal: AbortSignal.timeout(30000),
    });
    const t = await r.text();
    const name = u.split('/').pop().split('?')[0];
    const path = `${dir}\\${name}`;
    fs.writeFileSync(path, t, 'utf8');
    console.log(`${r.status}  ${t.length} bytes  -> ${path}`);

    for (const term of terms) {
      const n = t.split(term).length - 1;
      if (n > 0) console.log(`     ${term.padEnd(14)} ${n}`);
    }
  } catch (e) {
    console.log(`${u} FAIL ${e.message}`);
  }
  console.log('');
}
