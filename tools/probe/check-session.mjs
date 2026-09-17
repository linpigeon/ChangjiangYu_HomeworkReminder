// Probe: can Node's own TLS stack (OpenSSL) reach 长江雨课堂 with the saved cookie?
// If yes, the C# client can use plain HttpClient and Playwright is only needed for login.
//
// Cookie 来源见 ./session-cookies.mjs：命令行参数 > HR_PROBE_COOKIES 环境变量 >
// 仓库内 .appdata/session-cookies.json > 旧探针目录（仅在本机存在时兜底）。
import fs from 'node:fs';
import { resolveCookiePath } from './session-cookies.mjs';

const COOKIE_PATH = resolveCookiePath(process.argv[2]);
const BASE = 'https://changjiang.yuketang.cn';

const cookies = JSON.parse(fs.readFileSync(COOKIE_PATH, 'utf8'));
const cookieHeader = cookies.map((c) => `${c.name}=${c.value}`).join('; ');
const csrf = cookies.find((c) => c.name === 'csrftoken')?.value;

function show(label, v) {
  console.log(`${label}: ${v}`);
}

// 1. raw TLS reachability
try {
  const r = await fetch(`${BASE}/`, { method: 'HEAD' });
  show('HEAD / (node fetch)', `status=${r.status}`);
} catch (e) {
  show('HEAD / (node fetch)', `FAIL ${e.message} / ${e.cause?.code ?? ''}`);
}

// 2. authenticated call
try {
  const r = await fetch(`${BASE}/v2/api/web/courses/list?identity=2`, {
    headers: {
      xtbz: 'ykt',
      Cookie: cookieHeader,
      Accept: 'application/json, text/plain, */*',
      Referer: `${BASE}/`,
    },
  });
  const t = await r.text();
  let j = null;
  try { j = JSON.parse(t); } catch {}
  show('GET courses/list', `status=${r.status} errcode=${j?.errcode ?? '-'} courses=${j?.data?.list?.length ?? '-'}`);
  if (r.status !== 200) show('  body', t.slice(0, 300));
} catch (e) {
  show('GET courses/list', `FAIL ${e.message} / ${e.cause?.code ?? ''}`);
}

// 3. POST with CSRF (pub_new_pro shape)
try {
  const r = await fetch(`${BASE}/mooc-api/v1/lms/learn/course/pub_new_pro?cid=26109213&term=latest&uv_id=3214&classroom_id=26109213`, {
    method: 'POST',
    headers: {
      xtbz: 'ykt',
      Cookie: cookieHeader,
      'X-CSRFToken': csrf,
      'Content-Type': 'application/json',
      Accept: 'application/json, text/plain, */*',
      Referer: `${BASE}/`,
    },
    body: JSON.stringify({ classroom_id: 26109213, cid: 26109213, term: 'latest', uv_id: 3214 }),
  });
  const t = await r.text();
  let j = null;
  try { j = JSON.parse(t); } catch {}
  const ls = j?.data?.leaf_schedules;
  show('POST pub_new_pro', `status=${r.status} leaf_schedules=${ls ? Object.keys(ls).length : '-'}`);
  if (r.status !== 200) show('  body', t.slice(0, 300));
} catch (e) {
  show('POST pub_new_pro', `FAIL ${e.message} / ${e.cause?.code ?? ''}`);
}
