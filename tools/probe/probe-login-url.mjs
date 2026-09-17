// Verify which login URL actually serves the QR login page.
import fs from 'node:fs';
import path from 'node:path';
import { repoRoot } from './session-cookies.mjs';

const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36';
const BASE = 'https://changjiang.yuketang.cn';
const targets = ['/login/weixinapp/?next=%2Fweb', '/web?ykt_ai_login', '/api/open/is_login/', '/v2/api/web/userinfo'];

const out = [];
const say = (s) => { console.log(s); out.push(s); };

for (const u of targets) {
  try {
    const r = await fetch(BASE + u, {
      headers: { xtbz: 'ykt', 'User-Agent': UA },
      redirect: 'follow',
      signal: AbortSignal.timeout(25000),
    });
    const t = await r.text();
    say(`--- ${u}`);
    say(`    status=${r.status} bytes=${t.length} ctype=${r.headers.get('content-type') ?? '-'}`);
    say(`    final=${r.url}`);

    const scripts = [...t.matchAll(/<script[^>]+src=["']([^"']+)["']/g)].map((m) => m[1]);
    say(`    script srcs (${scripts.length}): ${scripts.slice(0, 8).join(' | ') || '(none)'}`);
    say(`    head: ${t.replace(/\s+/g, ' ').slice(0, 260)}`);
    say('');
  } catch (e) {
    say(`${u} FAIL ${e.message}`);
  }
}

fs.writeFileSync(path.join(repoRoot, 'docs', 'research', 'login-url-probe.txt'), out.join('\n'), 'utf8');
