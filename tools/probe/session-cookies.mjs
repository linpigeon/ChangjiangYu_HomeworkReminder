// Shared cookie-file resolution for the probe scripts.
//
// 优先级：显式参数 > HR_PROBE_COOKIES 环境变量 > 仓库内 .appdata/session-cookies.json
// > 旧探针目录（仅在本机存在时兜底，保持原默认行为）。
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

export function resolveCookiePath(explicit) {
  const candidates = [
    explicit,
    process.env.HR_PROBE_COOKIES,
    path.join(repoRoot, '.appdata', 'session-cookies.json'),
    'D:\\DS_Workpalce\\ykt-probe\\session-cookies.json',
  ].filter(Boolean);
  const found = candidates.find((p) => fs.existsSync(p));
  if (!found) {
    throw new Error('no session-cookies.json found; pass a path as an argument or set HR_PROBE_COOKIES');
  }
  return found;
}
