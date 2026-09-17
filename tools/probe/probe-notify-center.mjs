// Probe 5: live-test the notification-centre API and map filter_type values.
import fs from 'node:fs';
import { resolveCookiePath } from './session-cookies.mjs';

const cookies = JSON.parse(fs.readFileSync(resolveCookiePath(process.argv[2]), 'utf8'));
const BASE = 'https://changjiang.yuketang.cn';
const cookieHeader = cookies.map((c) => `${c.name}=${c.value}`).join('; ');
const H = { xtbz: 'ykt', Cookie: cookieHeader, Accept: 'application/json, text/plain, */*', Referer: `${BASE}/` };
const Q = 'term=latest&uv_id=3214&classroom_id=26109238';

async function get(u) {
  try {
    const r = await fetch(BASE + u, { headers: H, signal: AbortSignal.timeout(25000) });
    const t = await r.text();
    let j = null; try { j = JSON.parse(t); } catch {}
    return { status: r.status, j, t };
  } catch (e) { return { status: 0, j: null, t: e.cause?.code ?? e.message }; }
}

console.log('=== user_summary ===');
const s = await get(`/smart_education/notification/message_assistant/user_summary/?${Q}`);
console.log(s.status, s.t.replace(/\s+/g, ' ').slice(0, 400));

console.log('\n=== user_messages 默认 ===');
const m = await get(`/smart_education/notification/message_assistant/user_messages/?${Q}&page=1&page_size=8`);
const d = m.j?.data;
console.log(`status=${m.status} count=${d?.count} max_page=${d?.max_page} items=${d?.data?.length}`);
const items = d?.data ?? [];
console.log('item keys:', Object.keys(items[0] ?? {}).join(', '));
console.log('\nfilter_type 分布（前 8 条）:');
for (const it of items) {
  console.log(`  ft=${it.filter_type} read=${it.is_read} time=${it.msg_time} | ${it.course_name} | ${it.title}`);
}

console.log('\n=== 尝试按 filter_type 过滤（找公告）===');
for (const ft of [1, 2, 3, 4, 5]) {
  const r = await get(`/smart_education/notification/message_assistant/user_messages/?${Q}&page=1&page_size=3&filter_type=${ft}`);
  const dd = r.j?.data;
  const first = dd?.data?.[0];
  console.log(`  filter_type=${ft}: count=${dd?.count ?? '-'} first=${first ? `ft=${first.filter_type} "${String(first.title).slice(0, 30)}"` : '-'}`);
}

console.log('\n=== 全量读一页，统计 filter_type 分布 ===');
const big = await get(`/smart_education/notification/message_assistant/user_messages/?${Q}&page=1&page_size=200`);
const all = big.j?.data?.data ?? [];
const dist = {};
for (const it of all) dist[it.filter_type] = (dist[it.filter_type] ?? 0) + 1;
console.log('count=', big.j?.data?.count, 'fetched=', all.length, 'distribution=', JSON.stringify(dist));
const ur = all.filter((x) => x.is_read === false);
console.log('unread in this page:', ur.length);
console.log('\n未读样例（前 6 条）:');
ur.slice(0, 6).forEach((x) => console.log(`  ft=${x.filter_type} ${x.msg_time} | ${x.course_name} | ${x.title}`));
