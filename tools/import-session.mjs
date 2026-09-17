// One-off import: convert the Playwright cookie export from the earlier
// investigation into the app's session.json, so the app can be launched
// straight into the authenticated view without a manual QR scan.
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
// 解析优先级见 tools/probe/session-cookies.mjs。
import { resolveCookiePath } from './probe/session-cookies.mjs';

const src = resolveCookiePath(process.argv[2]);
const explicitOut = process.argv[3];
const cookies = JSON.parse(fs.readFileSync(src, 'utf8'));

const pick = (name) => cookies.find((c) => c.name === name);
const sessionId = pick('sessionid')?.value;
if (!sessionId) throw new Error('sessionid not found in ' + src);

const csrf = pick('csrftoken')?.value ?? null;
const loginType = pick('login_type')?.value ?? null;
const rawExpires = pick('sessionid')?.expires;
const expires = typeof rawExpires === 'number' && rawExpires > 0
  ? new Date(rawExpires * 1000).toISOString()
  : null;

const out = {
  SessionId: sessionId,
  CsrfToken: csrf,
  LoginType: loginType,
  ExpiresAt: expires,
  UniversityId: 3214,
  Term: 202601,
};

const dir = explicitOut
  ? path.dirname(explicitOut)
  : path.join(os.homedir(), 'AppData', 'Local', 'HomeworkReminder');
fs.mkdirSync(dir, { recursive: true });
const file = explicitOut ?? path.join(dir, 'session.json');
fs.writeFileSync(file, JSON.stringify(out, null, 2), 'utf8');

console.log('wrote', file);
console.log('  sessionid …' + sessionId.slice(-6));
console.log('  csrftoken ' + (csrf ? 'yes' : 'MISSING'));
console.log('  expires   ' + (expires ?? '(none)'));
