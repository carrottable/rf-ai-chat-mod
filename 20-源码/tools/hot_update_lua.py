#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
hot_update_lua.py —— 把 Lua 脚本热更新进已打包的模组（.rfc），不必重跑 Unity

原理：.rfc 里有一个 TextAsset（名字 RFAiChat），内容就是我们的 Ravenscript 源码。
      用 UnityPy 读包 → 找到它 → 换掉 m_Script → 原样存回（lz4 压缩）→ 覆盖部署文件。

为什么要有这个工具：Unity 批处理构建一次要几分钟，而改脚本（不涉及 UI 布局）只要几秒。
      ※ 但要注意：UnityPy 的 read_typetree() 读的永远是"文件原始字节"，
        所以【同一个对象只允许改一次】。本脚本只改 TextAsset，安全。

用法：
  python hot_update_lua.py                        # 用默认路径：源码 → 部署包
  python hot_update_lua.py --in 包.rfc --lua 源码.lua --out 包.rfc
  python hot_update_lua.py --dry-run              # 只看会改什么，不写文件
"""
import argparse
import os
import shutil
import sys
import time

import UnityPy

PROJ = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_LUA = os.path.join(PROJ, "20-源码", "lua", "RFAiChat.lua")
DEFAULT_RFC = r"os.environ.get("RFAICHAT_RFC", "")"
MARK = 'behaviour("RFAiChat")'


def find_script_asset(env):
    """找到承载我们脚本的 TextAsset（用 behaviour 标记认，避免认错别人的包）"""
    hits = []
    for o in env.objects:
        if o.type.name != "TextAsset":
            continue
        try:
            d = o.read_typetree()
        except Exception:
            continue
        s = d.get("m_Script") or ""
        if MARK in s:
            hits.append((o, d, s))
    return hits


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lua", default=DEFAULT_LUA, help="新的 Lua 源码")
    ap.add_argument("--in", dest="inp", default=DEFAULT_RFC, help="要改的 .rfc（默认=游戏目录里的部署包）")
    ap.add_argument("--out", dest="out", default=None, help="输出 .rfc（默认=覆盖 --in，并自动备份）")
    ap.add_argument("--dry-run", action="store_true", help="只检查，不写文件")
    args = ap.parse_args()

    if not os.path.isfile(args.lua):
        print("❌ 找不到 Lua 源码：%s" % args.lua)
        return 2
    if not os.path.isfile(args.inp):
        print("❌ 找不到模组包：%s" % args.inp)
        return 2
    out = args.out or args.inp

    with open(args.lua, encoding="utf-8") as f:
        new_lua = f.read()

    print("──────── 热更新 Lua ────────")
    print("  源码    : %s（%d 字节）" % (args.lua, len(new_lua.encode("utf-8"))))
    print("  模组包  : %s" % args.inp)

    env = UnityPy.load(args.inp)
    hits = find_script_asset(env)
    if not hits:
        print("❌ 这个包里没有找到含 %s 的 TextAsset（不是我们的包？）" % MARK)
        return 1
    if len(hits) > 1:
        print("⚠️  找到 %d 个候选 TextAsset，只改第一个" % len(hits))

    obj, tt, old = hits[0]
    print("  旧脚本  : %d 字节" % len(old.encode("utf-8")))
    if old.strip() == new_lua.strip():
        print("  ✅ 包里的脚本与源码已一致，无需更新")
        return 0

    if args.dry_run:
        print("  （dry-run：未写文件）")
        return 0

    # 备份（同一个目录下带时间戳，绝不覆盖旧备份）
    if os.path.abspath(out) == os.path.abspath(args.inp):
        bak = args.inp + ".bak_" + time.strftime("%Y%m%d_%H%M%S")
        shutil.copy2(args.inp, bak)
        print("  已备份  : %s" % bak)

    # ★ 同一个对象只改一次：直接改 typetree 再写回
    tt["m_Script"] = new_lua
    obj.save_typetree(tt)

    data = env.file.save(packer="lz4")
    with open(out, "wb") as f:
        f.write(data)
    print("  已写出  : %s（%d 字节）" % (out, len(data)))

    # ---- 回读校验：确认写进去的就是新脚本 ----
    env2 = UnityPy.load(out)
    hits2 = find_script_asset(env2)
    if not hits2:
        print("❌ 回读失败：写完后找不到脚本了")
        return 1
    got = hits2[0][2]
    ok = got.strip() == new_lua.strip()
    print("  回读校验: %s（%d 字节）" % ("✅ 一致" if ok else "❌ 不一致", len(got.encode("utf-8"))))
    if not ok:
        # 打印第一处不同，便于排查
        for i in range(min(len(got), len(new_lua))):
            if got[i] != new_lua[i]:
                print("    首个差异在第 %d 字节：包内=%r 源码=%r" % (i, got[i - 30:i + 30], new_lua[i - 30:i + 30]))
                break
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
