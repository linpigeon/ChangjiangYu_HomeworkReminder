// Determine which id leaf_info and leaf_schedules actually use.
// Discovery so far: leaf_schedules keys match the chapter tree's `id`, not `leafinfo_id`.
import fs from 'node:fs';
import { resolveCookiePath } from './session-cookies.mjs';

const cookies = JSON.parse(fs.readFileSync(resolveCookiePath(process.argv[2]), 'utf8'));
const BASE = 'https://changjiang.yuketang.cn';
const cookieHeader = cookies.map((c) => `${c.name}=${c.value}`).join('; ');
const H = { xtbz: 'ykt', Cookie: cookieHeader, Accept: 'application/json, text/plain, */*', Referer: `${BASE}/` };

async function get(u, h = {}) {
  const r = await fetch(BASE + u, { headers: { ...H, ...h }, signal: AbortSignal.timeout(30000) });
  const t = await r.text();
  let j = null; try { j = JSON.parse(t); } catch {}
  return { status: r.status, j, t };
}

const ROOM = 26109213;
// from the tree: id=48064815 leafinfo_id=48067036 name="概率论与数理统计B作业 第1次"
for (const id of [48064815, 48067036]) {
  const r = await get(`/mooc-api/v1/lms/learn/leaf_info/${ROOM}/${id}/`, { 'classroom-id': String(ROOM) });
  const d = r.j?.data;
  console.log(`leaf_info/${id}: status=${r.status} error_code=${r.j?.error_code ?? '-'} msg=${r.j?.msg ?? '-'}`);
  if (d) {
    console.log(`   keys=${Object.keys(d).slice(0, 14).join(',')}`);
    console.log(`   name=${JSON.stringify(d.name)} leaf_type=${d.leaf_type} publish_time=${JSON.stringify(d.publish_time)}`);
    console.log(`   score_deadline=${JSON.stringify(d.score_deadline)}`);
  }
  console.log('');
}

// Now the same question for a type-19 (章节作业) item in the 模电 course,
// which DID resolve correctly in the C# run — so we can see what id form it used.
const ROOM2 = 26111701;
const logs = await get(`/v2/api/web/logs/learn/${ROOM2}?actype=-1&page=0&offset=500&sort=-1&term=latest&uv_id=3214`);
const type19 = (logs.j?.data?.activities ?? []).filter((a) => a.type === 19);
console.log(`=== 模电 type=19 日志 ${type19.length} 条 ===`);
for (const a of type19.slice(0, 3)) {
  console.log(`  id=${a.id} title="${a.title}" content=${JSON.stringify(a.content)}`);
}
const tree2 = await get(`/mooc-api/v1/lms/learn/course/chapter?cid=${ROOM2}&term=latest&uv_id=3214&classroom_id=${ROOM2}`);
const leaves2 = (tree2.j?.data?.course_chapter ?? []).flatMap((c) => c.section_leaf_list ?? []);
console.log(`  章节树 ${leaves2.length} 个叶子，前 6 个：`);
for (const l of leaves2.slice(0, 6)) {
  console.log(`     id=${l.id} leafinfo_id=${JSON.stringify(l.leafinfo_id)} name="${l.name}"`);
}
