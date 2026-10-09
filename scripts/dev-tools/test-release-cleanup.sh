#!/usr/bin/env bash
# 本地验证：CI 里"清理同标签 Release"那一步的命令逻辑。
#
# 为什么单独验：GitHub Actions 日志对外是 403，拿不到失败原文，
# 只能把那段脚本在本机跑一遍，确认 404 / 200 两条分支都不会让脚本非零退出。
#
# 用法（Git Bash）：bash scripts/dev-tools/test-release-cleanup.sh <owner/repo> <tag> [token]
# 不传 token 时只跑"查不到"分支（匿名也能验证状态码判断逻辑）。

set -euo pipefail

REPO="${1:?需要 owner/repo}"
TAG="${2:?需要 tag}"
TOKEN="${3:-}"

if [ -n "$TOKEN" ]; then
  AUTH=(-H "Authorization: Bearer $TOKEN")
else
  AUTH=()
fi

echo "仓库=$REPO 标签=$TAG 带凭据=$([ -n "$TOKEN" ] && echo 是 || echo 否)"
echo

# ↓↓↓ 与 workflow 里保持一致的核心逻辑 ↓↓↓
STATUS="$(curl -sS --ssl-no-revoke -o /tmp/rel.json -w '%{http_code}' \
  "${AUTH[@]}" \
  -H 'Accept: application/vnd.github+json' \
  -H 'User-Agent: pen-release-cleanup' \
  "https://api.github.com/repos/$REPO/releases/tags/$TAG")"

echo "查询状态码：$STATUS"

if [ "$STATUS" = "200" ]; then
  ID="$(grep -o '"id":[0-9]*' /tmp/rel.json | head -n1 | cut -d: -f2)"
  echo "已有 Release id=$ID，删除它"
  DEL_STATUS="$(curl -sS --ssl-no-revoke -o /dev/null -w '%{http_code}' -X DELETE \
    "${AUTH[@]}" \
    -H 'Accept: application/vnd.github+json' \
    -H 'User-Agent: pen-release-cleanup' \
    "https://api.github.com/repos/$REPO/releases/$ID")"
  echo "删除状态码：$DEL_STATUS（204 为正常）"
elif [ "$STATUS" = "404" ]; then
  echo "该标签下没有 Release，跳过删除 —— 这一步应当正常继续"
else
  echo "意外状态码，报错退出"
  exit 1
fi
# ↑↑↑ 与 workflow 里保持一致的核心逻辑 ↑↑↑

echo
echo "脚本正常结束（退出码 0）。若上面 404 分支也走到这里，说明 CI 那一步不会再失败。"
