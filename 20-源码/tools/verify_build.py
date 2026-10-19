# -*- coding: utf-8 -*-
"""verify_build.py —— 校验源码 / DLL / 模组包内嵌 Lua 是否一致

用法（在仓库根目录）：
  python 20-源码/tools/verify_build.py
依赖：UnityPy（pip install UnityPy）
需要环境变量：RAVENFIELD_DIR（游戏根目录）
"""
import os, sys, hashlib, io
try:
    import UnityPy
except ImportError:
    print("请先安装 UnityPy：pip install UnityPy"); sys.exit(1)

PROJ = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DLL = os.path.join(PROJ, "30-构建产物", "RF.AiChat.dll")
GAME = os.environ.get("RAVENFIELD_DIR", "")
RFC = os.path.join(GAME, "ravenfield_Data", "mods", "RFAiChatUI", "RFAiChatUI.rfc")
LUA = os.path.join(PROJ, "20-源码", "lua", "RFAiChat.lua")

def sha(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()

def embedded(path):
    env = UnityPy.load(path)
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        s = o.read_typetree().get("m_Script", "")
        if 'behaviour("RFAiChat")' in s:
            return s
    return None

if not os.path.exists(DLL):
    print("找不到构建产物：", DLL, "（先运行 20-源码/plugin/build.sh）")
if os.path.exists(DLL):
    print("DLL:", os.path.getsize(DLL), sha(DLL)[:16])
if not os.path.exists(RFC):
    print("找不到模组包：设置 RAVENFIELD_DIR 后重试")
    sys.exit(0)
print("RFC:", os.path.getsize(RFC), sha(RFC)[:16])
s = io.open(LUA, encoding="utf-8").read()
e = embedded(RFC)
if e is None:
    print("❌ 包内未找到 RFAiChat 脚本")
else:
    print("内嵌 Lua:", len(e), "字符 / 源码:", len(s), "字符 →", "✅ 一致" if e.replace("\r\n", "\n").strip() == s.replace("\r\n", "\n").strip() else "❌ 有差异")
