// 一次性排查工具：用本机 git 里缓存的 GitHub 凭据，实测删除 Release 的接口。
//
// 为什么单独写个脚本：GitHub Actions 的日志对外是 403、拿不到原文，
// 只能靠本地复现同一个 API 调用，看它到底返回什么状态码。
//
// 用法：node scripts/dev-tools/check-release-api.mjs [owner/repo] [tag]
// 凭据来自 `git credential fill`，脚本本身不保存也不打印 token。

import { execFileSync } from 'node:child_process';

const repo = process.argv[2] ?? 'huan-nian/pen-wireless-tablet';
const tag = process.argv[3] ?? 'v0.1.0';

function readToken() {
  // git credential fill 从 stdin 读协议/主机，输出含 password= 的那行
  const input = 'protocol=https\nhost=github.com\n\n';
  const out = execFileSync(
    'C:\\Program Files\\Git\\cmd\\git.exe',
    ['credential', 'fill'],
    { input, encoding: 'utf8' },
  );
  const line = out.split('\n').find((l) => l.startsWith('password='));
  if (!line) throw new Error('没有从 git 拿到凭据');
  return line.slice('password='.length).trim();
}

const token = readToken();
const headers = {
  Authorization: `Bearer ${token}`,
  Accept: 'application/vnd.github+json',
  'User-Agent': 'pen-release-check',
  'X-GitHub-Api-Version': '2022-11-28',
};

async function call(method, path, body) {
  const res = await fetch(`https://api.github.com${path}`, {
    method,
    headers: body ? { ...headers, 'Content-Type': 'application/json' } : headers,
    body: body ? JSON.stringify(body) : undefined,
  });
  let text = '';
  try { text = await res.text(); } catch { /* 忽略 */ }
  return { status: res.status, text: text.slice(0, 400) };
}

console.log(`仓库: ${repo}   标签: ${tag}\n`);

console.log('1) 按标签查 Release（模拟 CI 里的 gh api .../releases/tags/<tag>）');
const lookup = await call('GET', `/repos/${repo}/releases/tags/${tag}`);
console.log(`   状态码: ${lookup.status}`);
console.log(`   响应: ${lookup.text}\n`);

console.log('2) 期望的码：200=存在可删；404=不存在，此时应跳过删除');
console.log('   注：CI 那一步用 `gh api ... || true` 取 id，', 
  lookup.status === 404 ? '当前会拿到空串，跳过删除' : '当前会拿到 id，执行删除');
console.log();

console.log('3) 查询该 token 的权限（看 actions 里 GITHUB_TOKEN 需要哪些）');
const repoInfo = await call('GET', `/repos/${repo}`);
console.log(`   状态码: ${repoInfo.status}（403 说明 token 无此仓库权限）`);
console.log();
console.log('结论要点：');
console.log('  - 若第 1 步在 CI 里返回 404，`gh api` 会以非零码退出；');
console.log('    脚本里的 `|| true` 只作用于命令替换，实测会让 -e 下的步骤失败。');
console.log('  - 因此删除逻辑不能依赖 `gh`，应改用 curl + 显式判断状态码。');
