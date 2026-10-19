#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
make_test_mod.py —— 免 Unity 打包测试模组

原理：拿一个现成的 Mutator 模组做「载体」(donor)，替换其中的 Ravenscript 文本与显示名，
      重打包成新的 .rfc，放进游戏的本地模组目录，即可在游戏内测试。

为什么可以这样做：
  · .rfc 是 UnityFS AssetBundle，Lua 以 TextAsset 明文存在其中（见 00-调研归档/05）
  · 脚本挂在 ScriptedBehaviour 上，behaviour 名留空时按 TextAsset 的 m_Name 推断
  · Mutator 在模组列表里的显示名存在 MutatorContentMod 里

⚠️ 仅用于自测：donor 的美术/预制件版权属于原作者，不可用于发布。正式版必须用 Unity + RFTools 重做。

用法：
  python make_test_mod.py --donor <donor.rfc> --lua <our.lua> --name "RF AI 对话(测试)" \
                          --folder RFAiChatTest --behaviour RFAiChat
"""
import argparse
import os
import shutil
import sys

import UnityPy

GAME_MODS = r"os.environ.get("RAVENFIELD_MODS_DIR", "")"


def load_env(path):
    return UnityPy.load(path)


def build_config():
    """模组设置界面：按玩家要求分组（基础设置 / API / 字号 / 聊天框设置）。

    排序依据：游戏是按 orderPriority 【全局】排序的（不是按类型分组），
    所以用 10/20/30… 的间隔把每一项排到该在的位置，分组标题用 labels。
    """
    return {
        "labels": [
            {"id": "sec_basic", "displayName": "　　　　———— 基础设置 ————", "orderPriority": 5},
            {"id": "sec_api",   "displayName": "　　　　　———— API ————",    "orderPriority": 35},
            {"id": "sec_font",  "displayName": "　　　　　———— 字号 ————",     "orderPriority": 65},
            {"id": "sec_box",   "displayName": "　　　　———— 聊天框设置 ————", "orderPriority": 75},
        ],
        "integers": [
            {"id": "fontSize",  "displayName": "聊天字号", "orderPriority": 70, "value": 16},
        ],
        "floats": [
            {"id": "boxWidth",    "displayName": "聊天框宽度（倍数，1.0=原宽）", "orderPriority": 80,  "value": 0.90},
            {"id": "boxHeight",   "displayName": "聊天框高度倍数", "orderPriority": 90,  "value": 2.00},
            {"id": "leftMargin",  "displayName": "左边距（像素）", "orderPriority": 100, "value": 0.0},
        ],
        "ranges": [],
        "strings": [
            {"id": "chatkey", "displayName": "输入键", "orderPriority": 20, "value": "Y"},
            {"id": "ApiUrl",  "displayName": "API 接口", "orderPriority": 40,
             "value": "https://api.deepseek.com/v1/chat/completions"},
            {"id": "ApiKey",  "displayName": "API 密钥", "orderPriority": 50, "value": ""},
            {"id": "Model",   "displayName": "模型名称", "orderPriority": 60, "value": "deepseek-chat"},
        ],
        "bools": [
            {"id": "enable", "displayName": "开启 AI 对话", "orderPriority": 30, "value": 1},
            {"id": "testconn", "displayName": "连接测试（勾上后看聊天框结果）", "orderPriority": 55, "value": 0},
        ],
        "dropdowns": [
            {"id": "lang", "displayName": "语言", "orderPriority": 10,
             "value": {"index": 0, "labels": ["中文", "English"]}},
            {"id": "freq", "displayName": "说话频率", "orderPriority": 25,
             "value": {"index": 1, "labels": ["少", "正常", "多"]}},
        ],
    }


def apply_ui_layout(env, scale=1.2, layout=None):
    """
    调整聊天框 UI 的位置与大小（不依赖 Unity，直接改 RectTransform 的 typetree）。
    默认：整体移到「左侧垂直居中」，并放大约 20%。

    层级（载体 Chat Feed 原版）：
        ChatFeedScript → Canvas ├─ Panel        (半透明底)
                               └─ Scroll View → Viewport → Content → Text
    """
    if layout is None:
        # 原值：Panel pos(103.85, -112.8) / Scroll View pos(129.8, -186.2)
        # 目标：把整组上移 ~142 像素使视觉中心落在屏幕垂直中部；同时各放大 1.2 倍
        layout = {
            "Panel":       {"pos": (125.0, 29.0),  "scale": (scale, scale, 1.0)},
            "Scroll View": {"pos": (150.0, -44.0), "scale": (scale, scale, 1.0)},
        }

    by_id = {o.path_id: o for o in env.objects}

    def go_name_of(rt_tt):
        gpid = rt_tt.get("m_GameObject", {}).get("m_PathID")
        go = by_id.get(gpid)
        if go is None:
            return None
        try:
            return getattr(go.read(), "m_Name", None)
        except Exception:
            return None

    changed = []
    for obj in env.objects:
        if obj.type.name != "RectTransform":
            continue
        try:
            tt = obj.read_typetree()
        except Exception:
            continue
        name = go_name_of(tt)
        if name not in layout:
            continue
        rule = layout[name]
        tt["m_AnchoredPosition"] = {"x": rule["pos"][0], "y": rule["pos"][1]}
        tt["m_LocalPosition"] = {"x": rule["pos"][0], "y": rule["pos"][1], "z": 0.0}
        tt["m_LocalScale"] = {"x": rule["scale"][0], "y": rule["scale"][1], "z": rule["scale"][2]}
        obj.save_typetree(tt)
        changed.append(name)

    for name in layout:
        if name not in changed:
            print("  [UI] 警告：没找到名为 %r 的 UI 对象，未调整" % name)
    if changed:
        print("  [UI] 已调整布局：%s（位置移到左侧居中，放大 %.0f%%）"
              % ("、".join(changed), (scale - 1) * 100))
    return changed


def darken_backdrop(env, alpha=0.78):
    """把聊天框背板加深（Scroll View 上的 Image 默认只有 0.384 不透明度，字在战场背景上看不清）。
    零风险：只改已有组件的颜色字段。"""
    done = 0
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            tt = obj.read_typetree()
        except Exception:
            continue
        col = tt.get("m_Color")
        if not isinstance(col, dict) or "a" not in col:
            continue
        # 只动"黑色半透明"的那个背板（黑底、alpha 在 0.2~0.6 之间）
        r, g, b, a = col.get("r", 0), col.get("g", 0), col.get("b", 0), col.get("a", 0)
        if r < 0.05 and g < 0.05 and b < 0.05 and 0.2 < a < 0.6:
            col["a"] = alpha
            tt["m_Color"] = col
            try:
                obj.save_typetree(tt)
                done += 1
                print("  [UI] 背板加深：alpha %.2f → %.2f" % (a, alpha))
            except Exception as e:
                print("  [UI] 背板加深失败:", e)
    return done


def bold_text(env):
    """把聊天文字设成粗体（uGUI 的 FontStyle.Bold = 1）。
    没有 Outline 组件的情况下，粗体是提升可读性最有效的办法。"""
    done = 0
    for o in env.objects:
        if o.type.name != "MonoBehaviour":
            continue
        try:
            tt = o.read_typetree()
        except Exception:
            continue
        fd = tt.get("m_FontData")
        if isinstance(fd, dict) and "m_FontStyle" in fd:
            fd["m_FontStyle"] = 1
            tt["m_FontData"] = fd
            o.save_typetree(tt)
            done += 1
            print("  [UI] 文字已设为粗体（原字号 %s）" % fd.get("m_FontSize"))
    return done


def style_boxes(env, fill=(0, 0, 0, 0.30), border=(0, 0, 0, 0.52)):
    """给两个已有的 Image 上色：Panel 当聊天框外框（露出的一圈=描边），Scroll View 当黑色填充。
    注意：MonoBehaviour 的 m_GameObject.m_PathID 就是 GameObject 的 path_id，直接查名字即可。"""
    names = {}
    for o in env.objects:
        if o.type.name == "GameObject":
            names[o.path_id] = o.read_typetree().get("m_Name")
    done = []
    for o in env.objects:
        if o.type.name != "MonoBehaviour":
            continue
        try:
            tt = o.read_typetree()
        except Exception:
            continue
        if "m_Color" not in tt or "m_Sprite" not in tt:
            continue
        go = names.get(tt.get("m_GameObject", {}).get("m_PathID"))
        col = tt["m_Color"]
        if go == "Panel":
            col.update(r=border[0], g=border[1], b=border[2], a=border[3]); tt["m_Color"] = col
            tt["m_RaycastTarget"] = 0
            o.save_typetree(tt); done.append("Panel=输入框底板")
        elif go == "Scroll View":
            col.update(r=fill[0], g=fill[1], b=fill[2], a=fill[3]); tt["m_Color"] = col
            tt["m_RaycastTarget"] = 0
            o.save_typetree(tt); done.append("Scroll View=聊天区半透明底")
    print("  [UI] 配色:", "、".join(done) if done else "没有匹配到目标（检查 GO 名字）")
    return len(done)


_UI_REFS = []
_UI_PARENT = None


def link_ui_children(env):
    """统一把所有手工新增图层挂到父物体下——只写一次。

    ⚠️ 坑：read_typetree() 读的是"文件原始字节"，所以两个函数各自
    "读父物体→追加子物体→写回"时，后一次会把前一次追加的引用覆盖掉
    （实测：12 个白线图层的引用被 5 个文字图层的写入覆盖 → 白线全消失）。
    """
    global _UI_REFS, _UI_PARENT
    if _UI_PARENT is None or not _UI_REFS:
        print("  [UI+] 没有需要挂载的图层")
        return 0
    ptt = _UI_PARENT.read_typetree()
    ptt["m_Children"] = list(ptt.get("m_Children") or []) + _UI_REFS
    _UI_PARENT.save_typetree(ptt)
    print("  [UI+] 已统一挂载 %d 个新图层" % len(_UI_REFS))
    _UI_REFS = []
    return 0


def add_ui_texts(env, count=5, parent_name="Scroll View", prefix="AIChat_Txt_"):
    """手工新增 count 个【文字图层】（各带自己的 Text 组件），用作按钮与输入框，
    这样它们不再是"聊天文字的一部分"，永远不会被聊天内容挤走。
    做法：克隆现有 Text 组件的 typtree → 改 m_GameObject/m_RectTransform/m_Text/字号/对齐 → 插入。
    """
    from UnityPy.helpers import TypeTreeHelper
    from UnityPy.streams import EndianBinaryWriter
    from UnityPy.files import ObjectReader as _OR

    class _S:
        def __init__(self, af, pid, cid, tid, data, st):
            self.assets_file = af; self.path_id = pid; self.class_id = cid
            self.type_id = tid; self.data = data; self.serialized_type = st
            self.reader = None; self.byte_size = len(data)
            self.is_destroyed = None; self.is_stripped = None
        def write(self, header, writer, data_writer):
            return _OR.write(self, header, writer, data_writer)

    objs = [o for o in env.objects if getattr(o, "type", None) is not None]
    sf = None
    for k, v in env.file.files.items():
        if type(v).__name__ == 'SerializedFile':
            sf = v; break
    if sf is None:
        return 0

    def ser(tmpl, **patch):
        node = tmpl._get_typetree_node(None)
        tt = tmpl.read_typetree()
        tt.update(patch)
        w = EndianBinaryWriter(endian=sf.header.endian)
        TypeTreeHelper.write_typetree(tt, node, w, sf)
        return w.bytes

    gos = {}
    for o in objs:
        if o.type.name == 'GameObject':
            try: gos[o.read_typetree().get('m_Name')] = o
            except Exception: pass
    # 模板：现有聊天 Text 的 GameObject/RT/CR/Text 组件
    tmpl_text = tmpl_go = None
    for o in objs:
        if o.type.name == 'MonoBehaviour':
            try: tt = o.read_typetree()
            except Exception: continue
            if 'm_Text' in tt:
                tmpl_text = o
                tmpl_go = gos.get('Text')
    parent_go = gos.get(parent_name)
    if tmpl_text is None or tmpl_go is None or parent_go is None:
        print("  [UI+] 文字模板缺失，跳过"); return 0
    key = {c['component']['m_PathID']: c for c in tmpl_go.read_typetree()['m_Component']}
    rt = cr = None
    for o in objs:
        if o.path_id in key:
            if o.type.name == 'RectTransform': rt = o
            elif o.type.name == 'CanvasRenderer': cr = o
    parent_rt = None
    for o in objs:
        if o.type.name == 'RectTransform' and o.read_typetree().get('m_GameObject', {}).get('m_PathID') == parent_go.path_id:
            parent_rt = o; break
    if rt is None or cr is None or parent_rt is None:
        print("  [UI+] 文字模板组件不全，跳过"); return 0

    base = -7770000000000002000
    made = 0; refs = []
    for i in range(count):
        pg = base - i * 4; prt, pcr, ptxt = pg - 1, pg - 2, pg - 3
        try:
            tt_txt = tmpl_text.read_typetree()
            fd = dict(tt_txt.get('m_FontData') or {})
            fd['m_FontSize'] = max(10, int(fd.get('m_FontSize', 14)) - 2)
            fd['m_Alignment'] = 4          # MiddleCenter
            fd['m_HorizontalOverflow'] = 0
            fd['m_VerticalOverflow'] = 1
            tt_txt['m_FontData'] = fd
            tt_txt['m_GameObject'] = {'m_FileID': 0, 'm_PathID': pg}
            tt_txt['m_RectTransform'] = {'m_FileID': 0, 'm_PathID': prt}
            tt_txt['m_Text'] = ''
            tt_txt['m_Color'] = {'r': 1, 'g': 1, 'b': 1, 'a': 1}
            tt_txt['m_RaycastTarget'] = False
            node = tmpl_text._get_typetree_node(None)
            w = EndianBinaryWriter(endian=sf.header.endian)
            TypeTreeHelper.write_typetree(tt_txt, node, w, sf)
            sf.objects[ptxt] = _S(sf, ptxt, tmpl_text.class_id, tmpl_text.type_id, w.bytes, tmpl_text.serialized_type)

            d = ser(tmpl_go, m_Name="%s%d" % (prefix, i),
                       m_Component=[{'component': {'m_FileID': 0, 'm_PathID': prt}},
                                    {'component': {'m_FileID': 0, 'm_PathID': pcr}},
                                    {'component': {'m_FileID': 0, 'm_PathID': ptxt}}])
            sf.objects[pg] = _S(sf, pg, tmpl_go.class_id, tmpl_go.type_id, d, tmpl_go.serialized_type)
            d = ser(rt, m_GameObject={'m_FileID': 0, 'm_PathID': pg}, m_Children=[],
                       m_Father={'m_FileID': 0, 'm_PathID': parent_rt.path_id},
                       m_AnchorMin={'x': 0.5, 'y': 1}, m_AnchorMax={'x': 0.5, 'y': 1},
                       m_Pivot={'x': 0.5, 'y': 0.5},
                       m_AnchoredPosition={'x': 0, 'y': 0},
                       m_SizeDelta={'x': 60, 'y': 20}, m_LocalScale={'x': 1, 'y': 1, 'z': 1})
            sf.objects[prt] = _S(sf, prt, rt.class_id, rt.type_id, d, rt.serialized_type)
            d = ser(cr, m_GameObject={'m_FileID': 0, 'm_PathID': pg})
            sf.objects[pcr] = _S(sf, pcr, cr.class_id, cr.type_id, d, cr.serialized_type)
            refs.append({'m_FileID': 0, 'm_PathID': prt})
            made += 1
        except Exception as e:
            print("  [UI+] 文字第 %d 个失败：%s" % (i, e)); break
    global _UI_PARENT
    _UI_PARENT = parent_rt
    _UI_REFS.extend(refs)
    print("  [UI+] 已新增 %d 个【文字图层】（按钮/输入框专用）" % made)
    return made


def add_ui_images(env, count=12, parent_name="Scroll View", prefix="AIChat_UI_"):
    """手工往包里新增 count 个 UI 图层（RectTransform+CanvasRenderer+Image），
    挂在 parent_name 之下，命名 AIChat_UI_0..N-1，供脚本运行时摆位。

    为什么要手工拼字节：UnityPy 不能新建对象；Ravenscript 不能 AddComponent。
    做法：克隆现有图层(Panel)的 typtree → 改数据 → 用 TypeTreeHelper 序列化成字节 → 作为新对象插入。
    """
    from UnityPy.helpers import TypeTreeHelper
    from UnityPy.streams import EndianBinaryWriter
    from UnityPy.files import ObjectReader as _OR

    class _Shim:
        def __init__(self, af, pid, cid, tid, data, st):
            self.assets_file = af; self.path_id = pid; self.class_id = cid
            self.type_id = tid; self.data = data; self.serialized_type = st
            self.reader = None; self.byte_size = len(data)
            self.is_destroyed = None; self.is_stripped = None

        def write(self, header, writer, data_writer):
            return _OR.write(self, header, writer, data_writer)

    objs = [o for o in env.objects if getattr(o, "type", None) is not None]
    sf = None
    for k, v in env.file.files.items():
        if type(v).__name__ == 'SerializedFile':
            sf = v; break
    if sf is None:
        return 0

    def ser(tmpl, **patch):
        node = tmpl._get_typetree_node(None)
        tt = tmpl.read_typetree()
        tt.update(patch)
        w = EndianBinaryWriter(endian=sf.header.endian)
        TypeTreeHelper.write_typetree(tt, node, w, sf)
        return tt, w.bytes

    gos = {}
    for o in objs:
        if o.type.name == 'GameObject':
            try: gos[o.read_typetree().get('m_Name')] = o
            except Exception: pass
    tmpl_go = gos.get('Panel')
    parent_go = gos.get(parent_name)
    if tmpl_go is None or parent_go is None:
        print("  [UI+] 找不到模板/父物体，跳过"); return 0

    key = {c['component']['m_PathID']: c for c in tmpl_go.read_typetree()['m_Component']}
    rt = cr = img = None
    for o in objs:
        if o.path_id in key:
            if o.type.name == 'RectTransform': rt = o
            elif o.type.name == 'CanvasRenderer': cr = o
            elif o.type.name == 'MonoBehaviour': img = o
    if rt is None or cr is None or img is None:
        print("  [UI+] 模板组件不全，跳过"); return 0

    parent_rt = None
    for o in objs:
        if o.type.name == 'RectTransform' and o.read_typetree().get('m_GameObject', {}).get('m_PathID') == parent_go.path_id:
            parent_rt = o; break
    if parent_rt is None:
        print("  [UI+] 找不到父 RectTransform，跳过"); return 0

    base = -7770000000000001000
    made = 0
    child_refs = []
    for i in range(count):
        pg = base - i * 4
        prt, pcr, pimg = pg - 1, pg - 2, pg - 3
        try:
            _, d = ser(tmpl_go, m_Name="%s%d" % (prefix, i),
                       m_Component=[{'component': {'m_FileID': 0, 'm_PathID': prt}},
                                    {'component': {'m_FileID': 0, 'm_PathID': pcr}},
                                    {'component': {'m_FileID': 0, 'm_PathID': pimg}}])
            sf.objects[pg] = _Shim(sf, pg, tmpl_go.class_id, tmpl_go.type_id, d, tmpl_go.serialized_type)
            _, d = ser(rt, m_GameObject={'m_FileID': 0, 'm_PathID': pg}, m_Children=[],
                       m_Father={'m_FileID': 0, 'm_PathID': parent_rt.path_id},
                       m_AnchorMin={'x': 0.5, 'y': 1}, m_AnchorMax={'x': 0.5, 'y': 1},
                       m_Pivot={'x': 0.5, 'y': 1},
                       m_AnchoredPosition={'x': 0, 'y': 0},
                       m_SizeDelta={'x': 200, 'y': 2}, m_LocalScale={'x': 1, 'y': 1, 'z': 1})
            sf.objects[prt] = _Shim(sf, prt, rt.class_id, rt.type_id, d, rt.serialized_type)
            _, d = ser(cr, m_GameObject={'m_FileID': 0, 'm_PathID': pg})
            sf.objects[pcr] = _Shim(sf, pcr, cr.class_id, cr.type_id, d, cr.serialized_type)
            _, d = ser(img, m_GameObject={'m_FileID': 0, 'm_PathID': pg},
                       m_Color={'r': 1, 'g': 1, 'b': 1, 'a': 1}, m_RaycastTarget=False)
            sf.objects[pimg] = _Shim(sf, pimg, img.class_id, img.type_id, d, img.serialized_type)
            child_refs.append({'m_FileID': 0, 'm_PathID': prt})
            made += 1
        except Exception as e:
            print("  [UI+] 第 %d 个失败：%s" % (i, e)); break

    global _UI_PARENT
    _UI_PARENT = parent_rt
    _UI_REFS.extend(child_refs)
    print("  [UI+] 已新增 %d 个 UI 图层（供白线边框/按钮底板用）" % made)
    return made


def add_outline_component(env, color=(0.0, 0.0, 0.0, 1.0), distance=(1.0, -1.0)):
    """给聊天文字加一个 uGUI 的 Outline（细黑边）。

    为什么要手工拼字节：Ravenscript 不开放 AddComponent、预制体里也没有 Outline 组件、
    UnityPy 不能新建对象。但包里 MonoBehaviour 的脚本关联是
        types[type_id].script_type_index -> script_types[i] -> MonoScript(path_id)
    所以可以：新建 MonoScript("Outline","UnityEngine.UI") + 新建 MonoBehaviour 挂到 Text 上。
    游戏运行时按"运行时类的字段布局"读 MonoBehaviour（包里的类型树只有基类节点），
    因此手工字节是有效的；类型树不匹配只影响离线工具，不影响游戏。
    """
    import struct, copy
    from UnityPy.files import ObjectReader

    def ustr(x):
        b = x.encode('utf-8')
        out = struct.pack('<i', len(b)) + b
        while len(out) % 4 != 0:
            out += b'\x00'
        return out

    def pptr(pid):
        return struct.pack('<iq', 0, pid)

    class _Shim:
        def __init__(self, assets_file, path_id, class_id, type_id, data, serialized_type):
            self.assets_file = assets_file
            self.path_id = path_id
            self.class_id = class_id
            self.type_id = type_id
            self.data = data
            self.serialized_type = serialized_type
            self.reader = None
            self.byte_size = len(data)
            self.is_destroyed = None
            self.is_stripped = None

        def write(self, header, writer, data_writer):
            return ObjectReader.write(self, header, writer, data_writer)

    sf = None
    for k, v in env.file.files.items():
        if type(v).__name__ == 'SerializedFile':
            sf = v
            break
    if sf is None:
        print("  [描边] 找不到 SerializedFile，跳过")
        return 0

    text_ms = text_mb = None
    text_go_pid = None
    for o in env.objects:
        if getattr(o, "type", None) is None:
            continue                     # 跳过手工插入的对象
        if o.type.name == 'MonoScript':
            try:
                if o.read_typetree().get('m_ClassName') == 'Outline':
                    print("  [描边] 已存在 Outline，跳过")
                    return 0
            except Exception:
                pass
            try:
                if text_ms is None and o.read_typetree().get('m_ClassName') == 'Text':
                    text_ms = o
            except Exception:
                pass
        elif o.type.name == 'MonoBehaviour':
            try:
                tt = o.read_typetree()
            except Exception:
                continue
            if 'm_Text' in tt:
                text_mb = o
                text_go_pid = tt['m_GameObject']['m_PathID']
    if text_ms is None or text_mb is None or text_go_pid is None:
        print("  [描边] 没找到 Text 相关对象，跳过")
        return 0

    try:
        hash16 = text_ms.get_raw_data()[12:28]
        body = ustr('Outline') + struct.pack('<i', 0) + hash16 + ustr('Outline')             + ustr('UnityEngine.UI') + ustr('UnityEngine.UI.dll') + struct.pack('<b', 0)
        new_ms_pid = -7770000000000000001
        ms_type = sf.types[text_ms.type_id]
        sf.objects[new_ms_pid] = _Shim(sf, new_ms_pid, ms_type.class_id, text_ms.type_id, body, ms_type)

        base_entry = sf.script_types[text_mb.serialized_type.script_type_index]
        new_entry = copy.copy(base_entry)
        new_entry.local_serialized_file_index = 0
        new_entry.local_identifier_in_file = new_ms_pid
        sf.script_types.append(new_entry)

        new_type = copy.copy(sf.types[text_mb.type_id])
        new_type.class_id = 114
        new_type.script_type_index = len(sf.script_types) - 1
        sf.types.append(new_type)
        new_type_id = len(sf.types) - 1

        mb = pptr(text_go_pid) + struct.pack('<i', 1) + pptr(new_ms_pid) + ustr('')
        mb += struct.pack('<4f', color[0], color[1], color[2], color[3])
        mb += struct.pack('<2f', distance[0], distance[1])
        mb += struct.pack('<b', 1)
        new_mb_pid = -7770000000000000002
        sf.objects[new_mb_pid] = _Shim(sf, new_mb_pid, 114, new_type_id, mb, new_type)

        for o in env.objects:
            if o.path_id == text_go_pid and o.type.name == 'GameObject':
                tt = o.read_typetree()
                tt['m_Component'].append({'component': {'m_FileID': 0, 'm_PathID': new_mb_pid}})
                o.save_typetree(tt)
                print("  [描边] ✅ 已给聊天文字加上 Outline（黑边，距离 %s/%s）" % (distance[0], distance[1]))
                return 1
    except Exception as e:
        print("  [描边] 失败（本次不带描边，不影响其它功能）：%s" % e)
        return 0
    return 0


def apply_monobehaviour_edits(env, fill=(0, 0, 0, 0.30), border=(0, 0, 0, 0.52),
                              font_size=None, bold=True):
    """把所有 MonoBehaviour 上的改动**合并成一次读-改-写**。

    ⚠️ 踩坑记录：UnityPy 的 read_typetree() 永远读"文件里的原始字节"，
    所以同一个对象被"读-改-写"多次时，后一次会把前一次的修改覆盖掉
    （实测：文字组件的鼠标拦截被后面的步骤改回了 1，又挡住装备选择界面）。
    """
    names = {}
    for o in env.objects:
        if o.type.name == "GameObject":
            try:
                names[o.path_id] = o.read_typetree().get("m_Name")
            except Exception:
                pass
    stat = {"raycast": 0, "color": 0, "bold": 0}
    for o in env.objects:
        if o.type.name != "MonoBehaviour":
            continue
        try:
            tt = o.read_typetree()
        except Exception:
            continue
        changed = False
        go = names.get(tt.get("m_GameObject", {}).get("m_PathID"))
        # ① 鼠标拦截一律关掉（聊天框绝不能挡住装备选择界面）
        if "m_RaycastTarget" in tt and tt["m_RaycastTarget"] != 0:
            tt["m_RaycastTarget"] = 0
            stat["raycast"] += 1
            changed = True
        # ② 文字：字号 / 粗体 / 富文本
        fd = tt.get("m_FontData")
        if isinstance(fd, dict):
            if font_size:
                fd["m_FontSize"] = int(font_size)
            if bold:
                fd["m_FontStyle"] = 1
            fd["m_RichText"] = 1
            fd["m_Alignment"] = 0               # 左上对齐（关键：否则台词会被画在长条中下部、掉出屏幕）
            fd["m_HorizontalOverflow"] = 0      # Wrap：按宽度换行
            fd["m_VerticalOverflow"] = 2        # Truncate：超出部分直接截断，文字绝不越界
            tt["m_FontData"] = fd
            stat["bold"] += 1
            changed = True
        # ③ 两块底色：聊天区（Scroll View）/ 输入区（Panel）
        if "m_Color" in tt and "m_Sprite" in tt:
            col = tt["m_Color"]
            if go == "Scroll View":
                col.update(r=fill[0], g=fill[1], b=fill[2], a=fill[3]); tt["m_Color"] = col
                stat["color"] += 1; changed = True
            elif go == "Panel":
                col.update(r=border[0], g=border[1], b=border[2], a=border[3]); tt["m_Color"] = col
                stat["color"] += 1; changed = True
        if changed:
            try:
                o.save_typetree(tt)
            except Exception as e:
                print("  [UI] 写入失败:", e)
    print("  [UI] 合并改动：关鼠标拦截 %d 个、文字 %d 个、配色 %d 个（每个对象只改一次）"
          % (stat["raycast"], stat["bold"], stat["color"]))
    return stat


def disable_raycast(env):
    """把聊天框（Text / 半透明底板 Image）的鼠标拦截关掉。
    这是必须的：否则在装备选择等界面上，聊天框会挡住鼠标点击（实测严重缺陷）。"""
    done = 0
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            tt = obj.read_typetree()
        except Exception:
            continue
        if "m_RaycastTarget" in tt and tt["m_RaycastTarget"] != 0:
            try:
                tt["m_RaycastTarget"] = 0
                obj.save_typetree(tt)
                done += 1
            except Exception as e:
                print("  [UI] 关闭鼠标拦截失败:", e)
    print("  [UI] 已关闭 %d 个组件的鼠标拦截（不再挡住装备选择界面）" % done)
    return done


def raise_canvas_order(env, order=100):
    """把聊天框所在的 Canvas 排序抬高，使其在游戏暂停菜单之上仍然可见（方便暂停时翻阅/调试）"""
    done = 0
    for obj in env.objects:
        if obj.type.name != "Canvas":
            continue
        try:
            tt = obj.read_typetree()
            old = tt.get("m_SortingOrder")
            tt["m_SortingOrder"] = order
            obj.save_typetree(tt)
            done += 1
            print("  [UI] Canvas 排序 %s → %s（确保暂停菜单之上可见）" % (old, order))
        except Exception as e:
            print("  [UI] Canvas 排序设置失败:", e)
    return done


def replace_script(env, new_lua, new_asset_name, behaviour_name, mutator_title, mutator_desc, inject_config=True):
    stat = {"textasset": 0, "scriptedbehaviour": 0, "mutator": 0, "assetbundle": 0}
    for obj in env.objects:
        t = obj.type.name

        if t == "TextAsset":
            d = obj.read()
            old_name = getattr(d, "m_Name", "")
            old = d.m_Script
            if not isinstance(old, str):
                old = bytes(old).decode("utf-8", "replace")
            if "behaviour(" not in old:
                continue
            d.m_Script = new_lua
            try:
                d.m_Name = new_asset_name
            except Exception:
                pass
            d.save()
            stat["textasset"] += 1
            print("  [TextAsset] %s → %s（%d 字符 → %d 字符）" % (old_name, new_asset_name, len(old), len(new_lua)))

        elif t == "MonoBehaviour":
            try:
                tt = obj.read_typetree()
            except Exception as e:
                print("  [MonoBehaviour] 跳过（无法读 typetree）:", e)
                continue

            # ScriptedBehaviour：source + behaviour
            if "source" in tt and "behaviour" in tt and "targets" in tt:
                tt["behaviour"] = behaviour_name
                obj.save_typetree(tt)
                stat["scriptedbehaviour"] += 1
                print("  [ScriptedBehaviour] behaviour → %s" % behaviour_name)

            # MutatorContentMod：mutators[] 的显示名、描述与配置项
            elif "mutators" in tt:
                ms = tt["mutators"]
                if isinstance(ms, list) and ms:
                    for m in ms:
                        if isinstance(m, dict):
                            old = m.get("name", "")
                            m["name"] = mutator_title
                            m["description"] = mutator_desc
                            if inject_config:
                                m["configuration"] = build_config()
                                print("  [MutatorContentMod] 已写入配置项：语言 / 说话频率 / 启用开关")
                            print("  [MutatorContentMod] 名称 %r → %r" % (old, mutator_title))
                    obj.save_typetree(tt)
                    stat["mutator"] += 1

        elif t == "AssetBundle":
            try:
                d = obj.read()
                d.m_Name = os.path.basename(new_asset_name) + ".rfc"
                d.save()
                stat["assetbundle"] += 1
            except Exception as e:
                print("  [AssetBundle] 改名失败（不影响加载）:", e)

    return stat


def save_env(env, out_path):
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    data = None
    for packer in ("lz4", "original", "none"):
        try:
            data = env.file.save(packer=packer)
            print("  保存成功，压缩方式:", packer, "大小:", len(data))
            break
        except Exception as e:
            print("  保存失败(packer=%s): %s" % (packer, e))
    if data is None:
        raise RuntimeError("无法保存 AssetBundle")
    with open(out_path, "wb") as f:
        f.write(data)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--donor", required=True, help="载体 .rfc 路径")
    ap.add_argument("--lua", required=True, help="我们的 Ravenscript 源码路径")
    ap.add_argument("--name", required=True, help="模组在游戏内显示的名字（中文可）")
    ap.add_argument("--desc", default="AI 实时生成聊天内容（测试版）", help="模组描述")
    ap.add_argument("--font-size", type=int, default=16, help="聊天文字字号（默认 16）")
    ap.add_argument("--folder", required=True, help="本地模组文件夹名（建议英文）")
    ap.add_argument("--behaviour", required=True, help="脚本里的 behaviour 名")
    ap.add_argument("--outdir", default=GAME_MODS, help="输出根目录（默认游戏本地模组目录）")
    ap.add_argument("--ui-scale", type=float, default=1.2, help="聊天框放大倍数（默认 1.2 = 放大 20%%）")
    ap.add_argument("--no-ui", action="store_true", help="不调整聊天框布局")
    args = ap.parse_args()

    lua = open(args.lua, encoding="utf-8").read()
    if ('behaviour("%s")' % args.behaviour) not in lua:
        print("❌ 源码里没有 behaviour(\"%s\")，请检查 --behaviour 是否与脚本一致" % args.behaviour)
        sys.exit(2)

    print("载体:", args.donor)
    env = load_env(args.donor)

    print("替换内容：")
    stat = replace_script(env, lua, args.behaviour, args.behaviour, args.name, args.desc, inject_config=True)
    if not args.no_ui:
        apply_ui_layout(env, scale=args.ui_scale)
        raise_canvas_order(env, order=100)
        # 这三件事合并成一次读写（见 apply_monobehaviour_edits 的注释）
        apply_monobehaviour_edits(env, font_size=args.font_size)
        add_outline_component(env)
        add_ui_images(env, count=12)
        add_ui_texts(env, count=5)
        link_ui_children(env)          # 统一挂载（必须只写一次）：手工对象不支持 .type，避免被后续步骤遍历到

    if stat["textasset"] == 0:
        print("❌ 没有找到可替换的 TextAsset（该 .rfc 可能不是脚本模组）")
        sys.exit(3)

    mod_dir = os.path.join(args.outdir, args.folder)
    out_rfc = os.path.join(mod_dir, args.folder + ".rfc")
    save_env(env, out_rfc)

    # 顺手放一份图标（复用载体里的），便于在模组列表识别
    print("输出:", out_rfc)

    # 回读校验
    print("回读校验：")
    env2 = UnityPy.load(out_rfc)
    ok = False
    for obj in env2.objects:
        if obj.type.name == "TextAsset":
            d = obj.read()
            sc = d.m_Script if isinstance(d.m_Script, str) else bytes(d.m_Script).decode("utf-8", "replace")
            if 'behaviour("%s")' % args.behaviour in sc:
                ok = True
                print("  ✅ TextAsset=%s，含 behaviour(\"%s\")，%d 字符" % (getattr(d, "m_Name", "?"), args.behaviour, len(sc)))
    if not ok:
        print("  ❌ 回读校验失败")
        sys.exit(4)
    print("完成。游戏内：Instant Action → Mutators 里勾选「%s」" % args.name)


if __name__ == "__main__":
    main()
