// Probe 2: firm up the POST / pub_new_pro path (completion status) with retries,
// and check the unread-notification endpoints.
import fs from 'node:fs';
import { resolveCookiePath } from './session-cookies.mjs';

const COOKIE_PATH = resolveCookiePath(process.argv[2]);
const BASE = 'https://changjiang.yuketang.cn';
const cookies = JSON.parse(fs.readFileSync(COOKIE_PATH, 'utf8'));
const cookieHeader = cookies.map((c) => `${c.name}=${c.value}`).join('; ');
const csrf = cookies.find((c) => c.name === 'csrftoken')?.value;
const H = { xtbz: 'ykt', Cookie: cookieHeader, Accept: 'application/json, text/plain, */*', Referer: `${BASE}/` };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function call(label, method, url, body) {
  for (let attempt = 1; attempt <= 3; attempt++) {
    const t0 = Date.now();
    try {
      const r = await fetch(BASE + url, {
        method,
        headers: { ...H, ...(body ? { 'Content-Type': 'application/json', 'X-CSRFToken': csrf } : {}) },
        body: body ? JSON.stringify(body) : undefined,
        signal: AbortSignal.timeout(45000),
      });
      const t = await r.text();
      let j = null; try { j = JSON.parse(t); } catch {}
      console.log(`${label}: status=${r.status} ms=${Date.now() - t0} keys=${j ? Object.keys(j).join(',') : '-'}`);
      if (r.status !== 200) console.log(`   body: ${t.slice(0, 240)}`);
      return j;
    } catch (e) {
      console.log(`${label}: attempt ${attempt} FAIL ${e.cause?.code ?? e.message} ms=${Date.now() - t0}`);
      await sleep(1500 * attempt);
    }
  }
  return null;
}

// A. POST pub_new_pro — completion status
for (const room of [26109213, 26109238, 26111701]) {
  const j = await call(`POST pub_new_pro room=${room}`, 'POST',
    `/mooc-api/v1/lms/learn/course/pub_new_pro?cid=${room}&term=latest&uv_id=3214&classroom_id=${room}`,
    { classroom_id: room, cid: room, term: 'latest', uv_id: 3214 });
  const ls = j?.data?.leaf_schedules;
  if (ls) {
    const arr = Object.entries(ls);
    console.log(`   leaf_schedules=${arr.length} sample=${JSON.stringify(arr.slice(0, 2))}`);
  }
  await sleep(400);
}

// B. notification / announcement candidates
const cands = [
  ['GET', '/v2/api/web/announcement/unread-list'],
  ['GET', '/v2/api/web/notices/list'],
  ['GET', '/v2/api/web/notification/unread'],
  ['GET', '/api/v3/user/basic-info'],
];
for (const [m, u] of cands) await call(`  ${m} ${u}`, m, u);

// C. unread announcements via learn logs for one real course (type 9 = 公告)
const logs = await call('GET logs/learn 26109238', 'GET',
  '/v2/api/web/logs/learn/26109238?actype=-1&page=0&offset=500&sort=-1&term=latest&uv_id=3214');
const acts = logs?.data?.activities ?? [];
console.log(`   activities=${acts.length} types=${JSON.stringify([...new Set(acts.map((a) => a.type))])}`);
const ann = acts.filter((a) => a.type === 9);
console.log(`   公告 sample: ${JSON.stringify(ann[0] ?? null).slice(0, 400)}`);
