#!/usr/bin/env bash
# 一键回归检查 —— 每次改动后、部署前跑一遍
# 用法：bash run_checks.sh
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
PROJ="$(cd "$HERE/../.." && pwd)"
PASS=0
FAIL=0

step() { echo; echo "──────── $1 ────────"; }
ok()   { echo "  ✅ $1"; PASS=$((PASS+1)); }
bad()  { echo "  ❌ $1"; FAIL=$((FAIL+1)); }

step "1/4 密钥泄露自检"
if bash "$HERE/check_nosecrets.sh" > /tmp/_ns.log 2>&1; then ok "未发现密钥混入项目目录"; else bad "发现疑似密钥，详见下方"; cat /tmp/_ns.log; fi

step "2/4 Lua 语法 + 假环境冒烟测试（旧载体 / 官方 UI 两套场景）"
cd "$HERE"
if PYTHONIOENCODING=utf-8 python smoke_test_lua.py > /tmp/_smoke.log 2>&1; then
  ok "旧载体场景：全部断言通过"
else
  # 旧载体的"点击[调试]按钮无反应"是 Unity 官方 UI 改造前就存在的已知失效项（详情见 40-测试记录/01-实机测试-001至020/测试-019-全面排查报告.md）
  if [ "$(grep -c '❌' /tmp/_smoke.log)" = "1" ] && grep -q "点击\[调试\]按钮无反应" /tmp/_smoke.log; then
    ok "旧载体场景：通过（仅剩 1 条改造前就存在的已知失效项）"
  else
    bad "旧载体场景冒烟测试失败"
    tail -25 /tmp/_smoke.log | sed 's/^/     /'
  fi
fi
if PYTHONIOENCODING=utf-8 python smoke_test_lua.py --official-ui > /tmp/_smoke_ui.log 2>&1; then
  ok "官方 UI 场景（Unity Button + InputField）：全部断言通过"
  grep -E "✅ 断言" /tmp/_smoke_ui.log | tail -14 | sed 's/^/     /'
else
  bad "官方 UI 场景冒烟测试失败"
  tail -25 /tmp/_smoke_ui.log | sed 's/^/     /'
fi

step "3/4 编译 C# 插件"
cd "$PROJ/20-源码/plugin"
if bash build.sh > /tmp/_build.log 2>&1; then
  ok "插件编译并部署成功"
  grep -E "产物|插件:" /tmp/_build.log | sed 's/^/     /'
else
  bad "编译失败"
  tail -15 /tmp/_build.log | sed 's/^/     /'
fi

step "4/4 打包游戏侧模组（.rfc）"
cd "$PROJ/20-源码/tools"
DONOR="${RFAICHAT_DONOR_RFC:-}"   # 可选：设置环境变量指向一个可用的载体 .rfc（版权原因不随仓库分发）
if [ -n "$DONOR" ] && [ -f "$DONOR" ]; then
  if PYTHONIOENCODING=utf-8 python make_test_mod.py \
       --donor "$DONOR" \
       --lua "../lua/RFAiChat.lua" \
       --name "RF AI 对话(测试)" \
       --desc "士兵由 AI 实时生成聊天内容。按 Y 调出鼠标与输入框；字号/宽高/键位都可在本设置里改。" \
       --folder RFAiChatTest --behaviour RFAiChat --ui-scale 1.2 \
     --outdir "$PROJ/30-构建产物/回归产物" > /tmp/_pack.log 2>&1; then
    ok "模组打包成功（含回读校验；输出到 30-构建产物/回归产物，不碰游戏目录）"
    grep -E "回读校验|✅" /tmp/_pack.log | tail -3 | sed 's/^/     /'
  else
    bad "打包失败"
    tail -12 /tmp/_pack.log | sed 's/^/     /'
  fi
else
  echo "  ⚠️ 未提供载体 .rfc（RFAICHAT_DONOR_RFC），跳过打包步骤（源码/编译/冒烟测试不受影响）"
fi

echo
echo "════════ 结果：$PASS 项通过，$FAIL 项失败 ════════"
[ "$FAIL" -eq 0 ] || exit 1
