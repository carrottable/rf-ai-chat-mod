# -*- coding: utf-8 -*-
# 第92轮：构建一致性校验（源码 / DLL / rfc 内嵌 Lua / 部署位置 / Key 缓存）
import os, hashlib, io, sys

P = r'F:\RAVENFIELDMOD\mod分类\.工作指导\AI日志\RF对话脚本AI接入MOD'
DLL = os.path.join(P, '30-构建产物', 'RF.AiChat.dll')
DEPLOY_DLL = r'F:\SteamLibrary\steamapps\common\Ravenfield\BepInEx\plugins\RF.AiChat\RF.AiChat.dll'
RFC = r'F:\SteamLibrary\steamapps\common\Ravenfield\ravenfield_Data\mods\RFAiChatUI\RFAiChatUI.rfc'
LUA_SRC = os.path.join(P, '20-源码', 'lua', 'RFAiChat.lua')
CFG = r'F:\SteamLibrary\steamapps\common\Ravenfield\BepInEx\config\rf.aichat.bridge.cfg'

def sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()

def embedded_lua(path):
    import UnityPy
    env = UnityPy.load(path)
    for o in env.objects:
        if o.type.name != 'TextAsset':
            continue
        d = o.read_typetree()
        s = d.get('m_Script', '')
        if 'behaviour("RFAiChat")' in s:
            return s
    return None

print('=== 构建一致性校验 ===')
print('DLL(产物)   :', os.path.getsize(DLL), sha(DLL)[:16])
if os.path.exists(DEPLOY_DLL):
    print('DLL(已部署) :', os.path.getsize(DEPLOY_DLL), sha(DEPLOY_DLL)[:16],
          '→', '一致' if sha(DLL) == sha(DEPLOY_DLL) else '❌不一致')
else:
    print('DLL(已部署) : 不存在')
print('RFC         :', os.path.getsize(RFC), sha(RFC)[:16])
src = io.open(LUA_SRC, encoding='utf-8').read()
emb = embedded_lua(RFC)
if emb is None:
    print('内嵌 Lua    : ❌ 未找到')
else:
    print('内嵌 Lua    :', len(emb), '字符 / 源码:', len(src), '字符')
    norm_a = src.replace('\r\n', '\n').rstrip('\n')
    norm_b = emb.replace('\r\n', '\n').rstrip('\n')
    print('内容比对    :', '✅ 一字不差' if norm_a == norm_b else '❌ 有差异')
    if norm_a != norm_b:
        import difflib
        diff = list(difflib.unified_diff(norm_a.split('\n'), norm_b.split('\n'), 'source', 'embedded', lineterm=''))
        print('\n'.join(diff[:40]))
# Key 缓存
try:
    cfg = io.open(CFG, encoding='utf-8', errors='ignore').read()
    cache = ''
    for line in cfg.split('\n'):
        if line.strip().startswith('ApiKeyCache'):
            cache = line.split('=', 1)[1].strip() if '=' in line else ''
    print('ApiKeyCache :', '空 ✅' if cache == '' else ('非空 ⚠️ 长度=' + str(len(cache))))
    print('配置文件含 sk- 明文:', '有 ⚠️' if 'sk-' in cfg else '无 ✅')
except Exception as e:
    print('配置读取失败', e)
