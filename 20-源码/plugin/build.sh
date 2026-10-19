#!/usr/bin/env bash
# RF AiChat Bridge —— 构建脚本
# 用法：bash build.sh
# 说明：使用 .NET Framework 自带的 csc（C# 5），无需安装 .NET SDK。
set -e

SRC_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJ_DIR="$(cd "$SRC_DIR/../.." && pwd)"          # RF对话脚本AI接入MOD
OUT_DIR="$PROJ_DIR/30-构建产物"
GAME="${RAVENFIELD_DIR:-/c/Program Files (x86)/Steam/steamapps/common/Ravenfield}"   # 改成你的游戏根目录，或设环境变量 RAVENFIELD_DIR
MANAGED="$GAME/ravenfield_Data/Managed"
BEPCORE="$GAME/BepInEx/core"
CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"

mkdir -p "$OUT_DIR"
DLL="$OUT_DIR/RF.AiChat.dll"

echo "[1/3] 编译插件源码 ..."
# 注意：用 "-" 前缀而非 "/"，否则 Git Bash(MSYS) 会把 /nologo 当成路径转换掉
export MSYS2_ARG_CONV_EXCL="*"
"$CSC" -nologo -target:library -langversion:5 -optimize+ \
  -out:"$(cygpath -w "$DLL")" \
  -reference:"$(cygpath -w "$BEPCORE/BepInEx.dll")" \
  -reference:"$(cygpath -w "$BEPCORE/0Harmony.dll")" \
  -reference:"$(cygpath -w "$MANAGED/UnityEngine.dll")" \
  -reference:"$(cygpath -w "$MANAGED/UnityEngine.CoreModule.dll")" \
  -reference:"$(cygpath -w "$MANAGED/UnityEngine.InputLegacyModule.dll")" \
  -reference:"$(cygpath -w "$MANAGED/UnityEngine.TextRenderingModule.dll")" \
  -reference:"$(cygpath -w "$MANAGED/Unity.TextMeshPro.dll")" \
  -reference:"$(cygpath -w "$MANAGED/UnityEngine.UIModule.dll")" \
  -reference:"$(cygpath -w "$MANAGED/UnityEngine.IMGUIModule.dll")" \
  -reference:"$(cygpath -w "$MANAGED/UnityEngine.UI.dll")" \
  -reference:"$(cygpath -w "$MANAGED/Assembly-CSharp.dll")" \
  -reference:"$(cygpath -w "$MANAGED/netstandard.dll")" \
  "$(cygpath -w "$SRC_DIR/AiChatPlugin.cs")" \
  "$(cygpath -w "$SRC_DIR/AiChatBridgeApi.cs")" \
  "$(cygpath -w "$SRC_DIR/BattleReport.cs")" \
  "$(cygpath -w "$SRC_DIR/DebugWindow.cs")" \
  "$(cygpath -w "$SRC_DIR/PromptWindow.cs")" \
  "$(cygpath -w "$SRC_DIR/PersonaWindow.cs")" \
  "$(cygpath -w "$SRC_DIR/KnowledgeBase.cs")" \
  "$(cygpath -w "$SRC_DIR/KBWindow.cs")" \
  "$(cygpath -w "$SRC_DIR/NameColorRows.cs")" \
  "$(cygpath -w "$SRC_DIR/ChatEngine.cs")" \
  "$(cygpath -w "$SRC_DIR/SettingsProbe.cs")" \
  "$(cygpath -w "$SRC_DIR/OverlayChat.cs")"

echo "[2/3] 部署到游戏目录 ..."
PLUGIN_DIR="$GAME/BepInEx/plugins/RF.AiChat"
mkdir -p "$PLUGIN_DIR"
cp "$DLL" "$PLUGIN_DIR/"

echo "[3/3] 确保配置文件存在（不覆盖已填写的 Key） ..."
CFG="$GAME/BepInEx/config/rf.aichat.bridge.cfg"
# 说明：本脚本假定 BepInEx 5.4.23.5 已安装；部署步骤可用 DEPLOY=0 跳过。
if [ ! -f "$CFG" ]; then
  cat > "$CFG" <<'EOF'
[AI]
ApiUrl = https://api.deepseek.com/v1
# ApiKey 不在此处填写：Key 只在 MOD 设置界面里由玩家填写（工坊安全要求）
Model = deepseek-chat
TimeoutSeconds = 30

[Debug]
TestOnStartup = true
EOF
  echo "    已创建新配置：$CFG"
else
  echo "    已存在，跳过：$CFG"
fi

echo
echo "完成。"
echo "  产物: $DLL"
echo "  插件: $PLUGIN_DIR/RF.AiChat.dll"
echo "  配置: $CFG"
