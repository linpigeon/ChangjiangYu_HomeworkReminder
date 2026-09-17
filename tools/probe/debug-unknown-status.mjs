// Debug why N homework items resolve to Unknown status: dump the chapter tree,
// the raw activity content, and the leaf_schedules keys for one course.
import fs from 'node:fs';
import { resolveCookiePath } from './session-cookies.mjs';

const cookies = JSON.parse(fs.readFileSync(resolveCookiePath(process.argv[2]), 'utf8'));
const BASE = 'https://changjiang.yuketang.cn';
const cookieHeader = cookies.map((c) => `${c.name}=${c.value}`).join('; ');
const csrf = cookies.find((c) => c.name === 'csrftoken')?.value;
const H = { xtbz: 'ykt', Cookie: cookieHeader, Accept: 'application/json, text/plain, */*', Referer: `${BASE}/` };

async function get(u, h = {}) {
  const r = await fetch(BASE + u, { headers: { ...H, ...h }, signal: AbortSignal.timeout(30000) });
  const t = await r.text();
  let j = null; try { j = JSON.parse(t); } catch {}
  return j;
}
async function post(u, body) {
  const r = await fetch(BASE + u, {
    method: 'POST',
    headers: { ...H, 'Content-Type': 'application/json', 'X-CSRFToken': csrf },
    body: JSON.stringify(body),
    signal: AbortSignal.timeout(30000),
  });
  const t = await r.text();
  let j = null; try { j = JSON.parse(t); } catch {}
  return j;
}

// 概率论 course — it has the unresolved 课堂小测1—前测 / 第1-2周作业 etc.
const ROOM = 26109213;

console.log('===== 1. leaf_schedules keys =====');
const prog = await post(
  `/mooc-api/v1/lms/learn/course/pub_new_pro?cid=${ROOM}&term=latest&uv_id=3214&classroom_id=${ROOM}`,
  { classroom_id: ROOM, cid: ROOM, term: 'latest', uv_id: 3214 });
const sched = prog?.data?.leaf_schedules ?? {};
console.log('keys:', Object.keys(sched).join(', '));
console.log('entries:', JSON.stringify(sched));

console.log('\n===== 2. chapter tree leaves =====');
const tree = await get(`/mooc-api/v1/lms/learn/course/chapter?cid=${ROOM}&term=latest&uv_id=3214&classroom_id=${ROOM}`);
const leaves = (tree?.data?.course_chapter ?? []).flatMap((c) => c.section_leaf_list ?? []);
console.log('leaf count:', leaves.length);
for (const l of leaves) {
  console.log(`  id=${l.id} leafinfo_id=${JSON.stringify(l.leafinfo_id)} score_deadline=${JSON.stringify(l.score_deadline)} name="${l.name}"`);
}

console.log('\n===== 3. learn logs (homework only) =====');
const logs = await get(`/v2/api/web/logs/learn/${ROOM}?actype=-1&page=0&offset=500&sort=-1&term=latest&uv_id=3214`);
for (const a of (logs?.data?.activities ?? []).filter((x) => x.type === 5 || x.type === 19)) {
  console.log(`  type=${a.type} id=${a.id} title="${a.title}"`);
  console.log(`     content=${JSON.stringify(a.content)}`);
}

console.log('\n===== 4. 用标题匹配章节树？ =====');
for (const a of (logs?.data?.activities ?? []).filter((x) => x.type === 5 || x.type === 19)) {
  const hit = leaves.find((l) => l.name === a.title);
  console.log(`  "${a.title}" -> ${hit ? `MATCH id=${hit.id} leafinfo_id=${JSON.stringify(hit.leafinfo_id)}` : 'NO TITLE MATCH'}`);
}
