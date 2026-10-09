#!/bin/sh
# 一次性工具：把所有历史提交的作者/提交者身份改写成对外身份。
#
# ⚠️ 会重写全部提交的哈希。仓库一旦推送到 GitHub（别人已经 clone），
#    再执行会让所有人的本地历史与远程冲突，那时不要用这个脚本。
#    它的适用场景就是「本地还没发布，先把私人邮箱从历史里抹掉」。
#
# 用法（两个参数：显示名、对外邮箱）：
#   scripts\rewrite-author.sh "Huan Nian" "12345678+username@users.noreply.github.com"
# 在 Windows 上更省事的做法是让 Git Bash 执行：
#   & "$env:ProgramFiles\Git\bin\bash.exe" scripts/rewrite-author.sh "名字" "邮箱"
#
# 执行后必须按提示清理备份引用与 reflog，否则旧提交仍然可达、旧邮箱依然在对象库里：
#   git for-each-ref --format='%(refname)' refs/original/ | xargs -n1 git update-ref -d
#   git reflog expire --expire=now --all
#   git gc --prune=now

set -e

NAME="$1"
EMAIL="$2"

if [ -z "$NAME" ] || [ -z "$EMAIL" ]; then
    echo "用法: $0 <显示名> <对外邮箱>" >&2
    exit 1
fi

# 把过滤脚本写到临时文件：--env-filter 的参数里只要含空格，
# 就会被 PowerShell 与 git 的参数再解析拆散（实测报 bad revision）。
FILTER_FILE="$(mktemp)"
trap 'rm -f "$FILTER_FILE"' EXIT

cat > "$FILTER_FILE" <<EOF
GIT_AUTHOR_NAME="$NAME"
GIT_AUTHOR_EMAIL="$EMAIL"
GIT_COMMITTER_NAME="$NAME"
GIT_COMMITTER_EMAIL="$EMAIL"
export GIT_AUTHOR_NAME GIT_AUTHOR_EMAIL GIT_COMMITTER_NAME GIT_COMMITTER_EMAIL
EOF

# 用 sh 的源文件方式调用，参数里不含空格
export FILTER_BRANCH_SQUELCH_WARNING=1
git filter-branch -f --env-filter ". $FILTER_FILE" -- --all

# 清掉备份引用，否则 refs/original/ 会让旧提交继续可达
git for-each-ref --format='%(refname)' refs/original/ | while read -r ref; do
    git update-ref -d "$ref"
done

git reflog expire --expire=now --all
git gc --prune=now

echo
echo "完成。用下面两条确认旧身份已经不存在："
echo "  git log --all --format='%an <%ae>' | sort -u"
echo "  git log --all --format='%ae%n%ce' | sort -u"
