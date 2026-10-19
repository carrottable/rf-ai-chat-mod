#!/usr/bin/env bash
# 密钥泄露自检 —— 发布前必跑
# 用途：扫描项目目录，确保 API Key 没有混进任何将要发布的文件（源码/文档/构建产物/工坊包）
# 用法：bash check_nosecrets.sh
set -u

PROJ="$(cd "$(dirname "$0")/../.." && pwd)"
echo "扫描目录：$PROJ"
echo "--------------------------------------------------"

# 常见 Key 形态：sk- 开头；以及通用的长随机串赋值
HITS=0

check() {
  local desc="$1"; shift
  local out
  out=$(grep -rInE "$@" "$PROJ" \
        --include="*.cs" --include="*.lua" --include="*.md" --include="*.sh" \
        --include="*.cfg" --include="*.ini" --include="*.json" --include="*.txt" \
        2>/dev/null | grep -v "check_nosecrets.sh" || true)
  if [ -n "$out" ]; then
    echo "❌ 命中【$desc】："
    echo "$out" | sed 's/\(sk-[A-Za-z0-9]\{4\}\)[A-Za-z0-9]*/\1***(已隐藏)/g' | head -20
    HITS=$((HITS+1))
  else
    echo "✅ 未命中【$desc】"
  fi
}

# 1) 明显的 API Key
check "sk- 开头的密钥" 'sk-[A-Za-z0-9]{16,}'
# 2) 明文赋值（防手滑把 key 写进配置模板）
check "ApiKey 赋了非空值" 'ApiKey[[:space:]]*=[[:space:]]*["'"'"']?[A-Za-z0-9_-]{16,}'
# 3) 常见云端密钥关键字
check "含 api_key/secret 长串" '(api[_-]?key|secret|token)[^A-Za-z0-9]{0,4}["'"'"'][A-Za-z0-9_-]{20,}'

echo "--------------------------------------------------"
if [ "$HITS" -eq 0 ]; then
  echo "结论：通过，可以打包发布。"
  exit 0
else
  echo "结论：发现 $HITS 类问题，请先清理再发布。"
  echo "注意：游戏目录里的 BepInEx\\config\\rf.aichat.bridge.cfg 与 .工作指导\\RFAPI.txt 属本机私密文件，"
  echo "      它们不在本目录内，正常不会被发布；此脚本只检查本目录。"
  exit 1
fi
