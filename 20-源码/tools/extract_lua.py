import sys, os
import UnityPy

path = sys.argv[1]
out = sys.argv[2] if len(sys.argv) > 2 else None
env = UnityPy.load(path)
for obj in env.objects:
    if obj.type.name == "TextAsset":
        d = obj.read()
        sc = d.m_Script if isinstance(d.m_Script, str) else bytes(d.m_Script).decode("utf-8", "replace")
        name = getattr(d, "m_Name", "?")
        print("=== TextAsset %r  %d 字符 ===" % (name, len(sc)))
        if out:
            with open(out, "w", encoding="utf-8") as f:
                f.write(sc)
            print("已写出:", out)
        else:
            print(sc)
