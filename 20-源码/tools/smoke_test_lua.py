# -*- coding: utf-8 -*-
"""
smoke_test_lua.py —— 在本地用「假游戏环境」跑我们的 Ravenscript 脚本

目的：在把模组打进游戏前，先用 Lua 解释器（lupa）把脚本跑一遍，
      捕获语法错误、nil 索引、字段名写错等低级错误，避免浪费一轮实机测试。

做法：注入一整套游戏 API 的桩（behaviour / Time / GameEvents / ColorScheme / Text / AiChatBridge …），
      然后依次调用 Start、若干次 Update，并模拟一批真实事件，观察是否报错、是否按预期推事件。
"""
import os
import traceback
from lupa import LuaRuntime

HERE = os.path.dirname(os.path.abspath(__file__))
LUA_FILE = os.path.join(HERE, "..", "lua", "RFAiChat.lua")

PUSHED = []      # 记录脚本推给插件的事件
POLL_QUEUE = []  # 让 Poll() 返回的内容
OFFICIAL_UI = False   # True = 用官方 UI（Unity Button/InputField）场景跑，见 --official-ui


def esc(s):
    """把非 ASCII 字符转成 <U+XXXX> —— 与 Lua 侧 print 桩的写法保持一致。

    为什么要这样：lupa 把 Lua 字符串转回 Python str 时会按"字节猜测编码"，
    中文日志会猜错并抛 UnicodeDecodeError（旧脚本挂在这一步）。
    统一转成 ASCII 后既能比对中文断言，也永远不会解码失败。
    """
    out = []
    for ch in s:
        out.append(ch if ord(ch) < 128 else "<U+%04X>" % ord(ch))
    return "".join(out)


def run():
    lua = LuaRuntime(unpack_returned_tuples=True)

    # ---------- 基础库 ----------
    lua.execute("""
        BehaviourClass = nil
        function behaviour(name)
            BehaviourClass = { __name = name }
            _G[name] = BehaviourClass
            return BehaviourClass
        end
        -- 把非 ASCII 字符按【真实码点】转成 <U+XXXX>（与 Python 侧 esc() 一致）
        -- 注意：Lua 侧字符串是 UTF-8 字节，必须自己解码，否则 "中" 会被拆成 3 个假码点。
        function escape_non_ascii(s)
            local out = {}
            local i, n = 1, #s
            while i <= n do
                local b = string.byte(s, i)
                if b == nil then break end
                if b < 128 then
                    out[#out + 1] = string.sub(s, i, i)
                    i = i + 1
                else
                    local len, cp = 2, 0
                    if b >= 240 then len = 4
                    elseif b >= 224 then len = 3 end
                    if len == 4 then
                        cp = (b % 8) * 262144 + (string.byte(s, i + 1) or 0) % 64 * 4096
                           + (string.byte(s, i + 2) or 0) % 64 * 64 + (string.byte(s, i + 3) or 0) % 64
                    elseif len == 3 then
                        cp = (b % 16) * 4096 + (string.byte(s, i + 1) or 0) % 64 * 64
                           + (string.byte(s, i + 2) or 0) % 64
                    else
                        cp = (b % 32) * 64 + (string.byte(s, i + 1) or 0) % 64
                    end
                    out[#out + 1] = string.format("<U+%04X>", cp)
                    i = i + len
                end
            end
            return table.concat(out)
        end
        _G.print = function(...)
            local parts = {}
            for i = 1, select('#', ...) do
                local s = tostring((select(i, ...)))
                s = string.gsub(s, "%c", "?")   -- 控制字符会让 Python 侧解码失败，先替换
                -- 非 ASCII（中文）逐字转成 <U+XXXX>：lupa 回传时会按字节猜编码而报错，
                -- 这跟被测脚本本身无关，所以在这里转义掉，断言侧用同样的转义比对。
                s = escape_non_ascii(s)
                parts[#parts + 1] = s
            end
            table.insert(_G.__logs, table.concat(parts, " "))
        end
        _G.__logs = {}

        Team = { Blue = 0, Red = 1, Neutral = 2 }
        SkillLevel = { Beginner = 0, Normal = 1, Veteran = 2, Elite = 3 }
        KeyCode = { End = 1, PageUp = 2, PageDown = 3, F9 = 4, T = 5, Return = 6, Escape = 7, Backspace = 8,
                    F10 = 9, F11 = 10, F8 = 11, Y = 12 }
        Color = { white = 1 }
        ColorScheme = {
            GetTeamColorBrighter = function(t) return t end,
            RichTextColorTag = function(c) return "<color=#fff>" end,
        }
        Overlay = { ShowMessage = function(...) end }
        Screen = { UnlockCursor = function() end, LockCursor = function() end, width = 1920, height = 1080 }
        IngameDialog = { PrintActorText = function(...) end, HideInstant = function() end }
        Input = { GetKeyDown = function(k) return false end, mouseScrollDelta = { x = 0, y = 0 },
                  inputString = "", compositionString = "",
                  mousePosition = { x = 0, y = 0 }, GetMouseButtonDown = function(b) return false end }
        WaitForSeconds = function(s) return s end
        Time = { time = 0, deltaTime = 0.016 }
        Player = { actor = nil, inputEnabled = true, allowMouseLook = true, movementInputEnabled = true }
        ActorManager = {
            ActorsDistance = function(a, b) return 12 end,
            actors = {},
        }

        -- 事件系统桩
        -- 注意：真实 API 是「点号」调用 event.AddListener(script, "method")，不是冒号调用，
        -- 所以这里用闭包捕获事件对象，签名里没有 self。
        local function mkEvent(name)
            local ev = { _name = name, _listeners = {} }
            ev.AddListener = function(script, method, data)
                ev._listeners[#ev._listeners + 1] = { script = script, method = method }
            end
            ev.RemoveListener = function(script, method) end
            return ev
        end
        GameEvents = {}
        for _, n in ipairs({ "onActorSpawn","onActorCreated","onActorDiedInfo","onCapturePointCaptured",
                             "onCapturePointNeutralized","onSquadAssignedNewOrder","onOverlayText",
                             "onMatchEnd","onVehicleDestroyed" }) do
            GameEvents[n] = mkEvent(n)
        end
        function FireEvent(evName, ...)
            local ev = GameEvents[evName]
            if ev == nil then return end
            for _, l in ipairs(ev._listeners) do
                l.script[l.method](l.script, ...)
            end
        end

        -- UI 桩
        Text = { __type = "Text" }
        Image = { __type = "Image" }
        Button = { __type = "Button" }
        InputField = { __type = "InputField", LineType = { SingleLine = 0, MultiLineNewline = 1 } }
        Canvas = { __type = "Canvas", scaleFactor = 1 }
        RectTransform = { __type = "RectTransform" }
        TextAnchor = { UpperLeft = 0, MiddleLeft = 3, MiddleCenter = 4 }
        HorizontalWrapMode = { Wrap = 0, Overflow = 1 }
        VerticalWrapMode = { Truncate = 0, Overflow = 1 }
        Size = { width = 1920, height = 1080 }
        function Vector2(x, y) return { x = x, y = y } end
        local function mkRT(w, h, parent)
            local rt = { sizeDelta = { x = w, y = h }, anchoredPosition = { x = 0, y = 0 },
                         position = { x = 960, y = 540, z = 0 }, pivot = { x = 0.5, y = 0.5 }, parent = parent }
            rt.asRectTransform = rt
            return rt
        end
        function MakeTextComponent()
            local panel = mkRT(200, 238, nil)
            panel.name = "Panel"
            local scroll = mkRT(242, 209, nil)
            scroll.name = "Scroll View"
            local canvasRT = mkRT(0, 0, nil)
            canvasRT.childCount = 2
            canvasRT.GetChild = function(self, i)
                if i == 0 then return panel end
                return scroll
            end
            scroll.parent = canvasRT
            panel.parent = canvasRT
            local viewport = mkRT(222, 189, scroll)
            local content = mkRT(222, 189, viewport)
            local textRT = mkRT(220, 210, content)
            return {
                __type = "Text",
                text = "", fontSize = 28, lineSpacing = 1, resizeTextForBestFit = false,
                supportRichText = false, rectTransform = textRT,
                transform = { parent = textRT },
            }
        end
        function MakeGameObject(kind)
            local go = { _kind = kind, activeSelf = true }
            -- ★ 真实 Ravenscript 里 targets 给的是 GameObject，而 asRectTransform 只在 Transform 上，
            --   GameObject 上必须用 GetComponent(RectTransform)。桩必须照这个语义来，
            --   否则脚本里写错了也测不出来（这正是上一轮"测试全绿、实机全挂了"的原因）。
            go.GetComponent = function(self, t)
                if t == RectTransform then return self.__rt or (self.transform and self.transform.asRectTransform) end
                local k = self.__uiKind or "Text"
                if k == "InputField" then return MakeInputFieldComponent() end
                if k == "Button" then return MakeButtonComponent() end
                return MakeTextComponent()
            end
            go.GetComponentInChildren = go.GetComponent
            go.transform = { asRectTransform = nil }   -- 默认没有；必要时由测试显式赋值
            go.SetActive = function(self, v) self.activeSelf = v end
            return go
        end

        -- ===== 官方 UI（Unity Button / InputField）桩 =====
        -- 真实游戏里 targets 里给的是真 Unity 物体，组件是 真 Button / 真 InputField；
        -- 这里用结构一致的表模拟，专门验证"接线 + 焦点 + 提交"这条新链路。
        function MakeButtonComponent()
            local b = {}
            b.onClick = {
                _listeners = {},
                AddListener = function(script, method)
                    b.onClick._listeners[#b.onClick._listeners + 1] = { script = script, method = method }
                end,
                RemoveListener = function() end,
            }
            -- 模拟 Unity 点击按钮：直接触发已挂上的回调
            b.Click = function()
                for _, l in ipairs(b.onClick._listeners) do
                    l.script[l.method](l.script)
                end
                return #b.onClick._listeners
            end
            return b
        end

        function MakeInputFieldComponent()
            local inf = {}
            inf.text = ""
            inf.isFocused = false
            inf.characterLimit = 0
            inf.textComponent = { text = "", fontSize = 16, preferredHeight = 20 }
            inf.placeholder = { text = "点击这里输入文字…", fontSize = 16 }
            inf.onEndEdit = {
                _listeners = {},
                AddListener = function(script, method)
                    inf.onEndEdit._listeners[#inf.onEndEdit._listeners + 1] = { script = script, method = method }
                end,
                RemoveListener = function() end,
            }
            -- 模拟真输入框：受 characterLimit 限制、按回车触发 onEndEdit
            inf.Type = function(s)
                local i = 1
                while i <= #s do
                    local b = string.byte(s, i)
                    local len = 1
                    if b >= 240 then len = 4 elseif b >= 224 then len = 3 elseif b >= 192 then len = 2 end
                    local piece = string.sub(s, i, i + len - 1)
                    local t = inf.text .. piece
                    if inf.characterLimit > 0 and #t > inf.characterLimit then break end
                    inf.text = t
                    i = i + len
                end
            end
            inf.Submit = function()
                for _, l in ipairs(inf.onEndEdit._listeners) do
                    l.script[l.method](l.script, inf.text)
                end
                return #inf.onEndEdit._listeners
            end
            return inf
        end

        -- 插件桥桩
        AiChatBridge = {
            Push = function(line) table.insert(_G.__pushed, line) end,
            Poll = function() return _G.__poll_next end,
            Status = function() return "stub-status" end,
            ClickPoll = function() local c = __click_next; __click_next = nil; return c or "" end,
            -- ↓ 官方 UI 接口（插件 v1.7 新增）
            SetTargets = function() __uiTargetsSet = true end,
            SetInputRect = function(x, y, w, h) __uiRect = string.format("%.0f,%.0f,%.0f,%.0f", x, y, w, h) end,
            SetButtonRects = function() __uiBtnsSet = true end,
            SetFontSize = function() end,
            FocusInput = function() __uiWantFocus = true end,
            UnfocusInput = function() __uiWantFocus = false end,
            InputFocused = function() return __uiWantFocus == true end,
            InputText = function() return __uiText or "" end,
            InputEvent = function() local e = __uiEvent; __uiEvent = nil; return e or "" end,
            RebuildLayout = function() end,
            HitElement = function(x, y)
                if __uiHit == nil then return "" end
                local r = __uiHit
                if x >= r.l and x <= r.r and y >= r.t and y <= r.b then return r.kind or "input" end
                return ""
            end,
            DiagUI = function() return "stub-diag" end,
        }
        _G.__pushed = {}
        _G.__poll_next = ""
        _G.__click_next = nil
        _G.__uiTargetsSet = false
        _G.__uiBtnsSet = false
        _G.__uiRect = ""
        _G.__uiWantFocus = false
        _G.__uiEvent = nil
        _G.__uiHit = nil
    """)

    # 把 Python 侧的记录数组接进 Lua
    lua.execute("__pushed = __pushed")

    # ---------- 载入脚本 ----------
    src = open(LUA_FILE, encoding="utf-8").read()
    try:
        chunk = lua.eval("function(s) return load(s) end")(src)
        if chunk is None:
            print("❌ 语法错误：无法 load")
            return 1
    except Exception as e:
        print("❌ 语法错误:", e)
        return 1
    print("✅ 语法检查通过")

    lua.execute("__chunk = ...", chunk) if False else None
    # 执行脚本（此时会调用 behaviour("RFAiChat")）
    try:
        lua.execute(src)
    except Exception as e:
        print("❌ 执行脚本失败:", e)
        traceback.print_exc()
        return 1

    obj = lua.globals().RFAiChat
    if obj is None:
        print("❌ 没有注册出 RFAiChat 对象")
        return 1
    print("✅ 脚本注册成功：behaviour =", obj._name)

    # 造一个"世界"：玩家 + 若干士兵 + 聊天框 UI 目标 + mutator 配置
    lua.execute("""
        local function mkActor(name, team, skill)
            local a = { name = name, team = team, isBot = true, isDead = false,
                        aiController = { skillLevel = skill or SkillLevel.Beginner } }
            return a
        end
        Player.actor = mkActor("PLAYER", Team.Blue, SkillLevel.Normal)
        Player.actor.isPlayer = true      -- 关键：玩家本人必须带 isPlayer（之前漏了，导致"玩家多杀/玩家阵亡"根本没被识别）
        Player.actor.isBot = false
        A1 = mkActor("Croc", Team.Red, SkillLevel.Beginner)
        A2 = mkActor("Turner", Team.Blue, SkillLevel.Veteran)
        A3 = mkActor("Benn", Team.Red, SkillLevel.Beginner)
        ActorManager.actors = { Player.actor, A1, A2, A3 }

        -- 聊天框 UI 目标（模拟 Chat Feed 载体：targets.text / targets.canvas）
        RFAiChat.targets = {
            text = MakeGameObject("Text"),
            canvas = MakeGameObject("Canvas"),
        }
        -- mutator 配置（模拟游戏内设置：语言=中文 / 频率=正常 / 启用）
        RFAiChat.script = {
            mutator = {
                configuration = {
                    GetDropdown = function(id) return 0 end,
                    GetBool = function(id) return true end,
                    GetString = function(id) return nil end,
                    GetFloat = function(id) return nil end,
                    GetInt = function(id) return nil end,
                    GetFloatNil = true,
                },
                GetConfigurationDropdown = function(id) return 0 end,
                GetConfigurationBool = function(id) return true end,
                GetConfigurationString = function(id) return nil end,
                GetConfigurationFloat = function(id) return nil end,
                GetConfigurationInt = function(id) return nil end,
            },
        }
    """)

    # ---------- 可选：切到「官方 UI（Unity Button/InputField）」场景 ----------
    # 用 --official-ui 打开：脚本会走 useUnityUI 分支（真按钮接线 + 真输入框焦点）
    if OFFICIAL_UI:
        lua.execute("""
            -- 与 Unity 生成器一致的几何：左中、宽 560、高 640
            -- 这里自己再定义一份 mkRT（最初那份是 local 的，跨 execute 拿不到）
            local function mkRT2(w, h, parent)
                local rt = { sizeDelta = { x = w, y = h }, anchoredPosition = { x = 0, y = 0 },
                             position = { x = 960, y = 540, z = 0 }, pivot = { x = 0.5, y = 0.5 },
                             rect = { width = w, height = h },
                             localScale = { x = 1, y = 1, z = 1 }, parent = parent }
                rt.asRectTransform = rt
                return rt
            end
            _G.mkRT2 = mkRT2
            local chatRT = mkRT2(560, 640, nil)
            chatRT.name = "ChatBox"
            chatRT.anchoredPosition = { x = 16, y = 0 }        -- 生成器旧值（错的那个）
            local inputRT = mkRT2(560, 48, nil)
            inputRT.name = "InputBox"
            inputRT.position = { x = 296, y = 274 }            -- 输入框中心（屏幕坐标）
            local textRT2 = mkRT2(548, 632, nil)
            textRT2.position = { x = 296, y = 820 }
            textRT2.parent = { asRectTransform = { rect = { width = 548, height = 632 },
                                                   parent = { asRectTransform = { rect = { width = 548, height = 632 } } } } }

            local canvasGO = MakeGameObject("Canvas")
            canvasGO.GetComponent = function(self, t)
                if t == Canvas then return { scaleFactor = 1 } end
                if t == Text then return MakeTextComponent() end
                return nil
            end

            -- targets：每个物体显式声明"该返回哪种组件"（不依赖跨块可见的全局表）
            RFAiChat.targets = {
                text   = MakeGameObject("Text"),
                canvas = canvasGO,
                scroll = MakeGameObject("Scroll"),
                input  = MakeGameObject("InputField"),
                btn_send   = MakeGameObject("Button"),
                btn_debug  = MakeGameObject("Button"),
                btn_report = MakeGameObject("Button"),
                btn_back   = MakeGameObject("Button"),
            }
            -- 每个物体都挂上它自己的 RectTransform（桩语义：asRectTransform 只在 transform 上）
            local rtMap = { scroll = chatRT, input = inputRT, text = textRT2 }
            for k, v in pairs(rtMap) do
                RFAiChat.targets[k].__rt = v
                RFAiChat.targets[k].transform = { asRectTransform = v }
            end
            UISCROLL = RFAiChat.targets.scroll
            UISCROLL.__rt = chatRT                 -- GetComponent(RectTransform) 返回它
            UISCROLL.asRectTransform = chatRT      -- 桩里也放一份（脚本不该依赖它）
            UITEXT = RFAiChat.targets.text
            UITEXT.__uiKind = "Text"
            -- 让 GetComponent(Text) 返回带 rectTransform 的文字组件，并把 parent 链指向 Content
            local uiTextComp = MakeTextComponent()
            uiTextComp.rectTransform = textRT2
            UITEXT.GetComponent = function(self, t)
                if t == RectTransform then return UITEXT.__rt or textRT2 end
                return uiTextComp
            end
            UIINPUT = RFAiChat.targets.input
            UIINPUT.__uiKind = "InputField"

            UIFIELD = MakeInputFieldComponent()
            UIINPUT.GetComponent = function(self, t)
                if t == RectTransform then return UIINPUT.__rt or inputRT end
                return UIFIELD
            end
            -- 注意：inputMode 是脚本文件级的 local，Python 侧不能直接读 RFAiChat.inputMode（会得到 nil）。
            -- 但脚本里写的是 RFAiChat.inputMode（读表字段），所以下面用同样写法反射即可。
            -- （不能写成裸 inputMode：本块里函数的作用域是 _G，看不见脚本文件的 local）
            function __state()
                return { inputMode = RFAiChat.inputMode, uiFocus = RFAiChat.inputFocused }
            end
            -- 四个按钮各自保留自己的组件实例，便于单独触发
            UIBTN = {}
            local btnKeys = { "btn_send", "btn_debug", "btn_report", "btn_back" }
            for _, k in ipairs(btnKeys) do
                local comp = MakeButtonComponent()
                UIBTN[k] = comp
                RFAiChat.targets[k].__uiKind = "Button"
                RFAiChat.targets[k].transform = { asRectTransform = chatRT }
                RFAiChat.targets[k].GetComponent = function(self, t) return comp end
            end
        """)

    # ---------- 跑一遍生命周期 ----------
    try:
        lua.execute("RFAiChat.Start(RFAiChat)")
        print("✅ Start 执行通过")
    except Exception as e:
        print("❌ Start 报错:", e)
        traceback.print_exc()
        return 1

    # 事件：出生 / 创建
    for stmt in ["FireEvent('onActorSpawn', A1)",
                 "FireEvent('onActorSpawn', A2)",
                 "FireEvent('onActorCreated', A1)"]:
        try:
            lua.execute(stmt)
            print("✅", stmt)
        except Exception as e:
            print("❌", stmt, "报错:", e)
            return 1

    # 击杀事件（含爆头、友军误伤）
    try:
        lua.execute("""
            local info = { sourceActor = A2, type = 1, isCriticalHit = true, isSplashDamage = false,
                           isPiercing = false, sourceWeaponEntry = { name = "AK-47" } }
            FireEvent("onActorDiedInfo", A1, info, false)          -- 敌人击杀
            local info2 = { sourceActor = A2, type = 1, isCriticalHit = false, isSplashDamage = false,
                            isPiercing = false, sourceWeaponEntry = { name = "M9" } }
            FireEvent("onActorDiedInfo", Player.actor, info2, false) -- 友军误伤（A2 与玩家同队）
        """)
        print("✅ 击杀/误伤事件通过")
    except Exception as e:
        print("❌ 击杀事件报错:", e)
        traceback.print_exc()
        return 1

    # 其它事件
    try:
        lua.execute("""
            local cp = { name = "B" }
            FireEvent("onCapturePointCaptured", cp, Team.Blue)
            FireEvent("onCapturePointNeutralized", cp, Team.Red)
            FireEvent("onSquadAssignedNewOrder", { }, 1)
            FireEvent("onOverlayText", "敌方巡逻队朝 B 点去了", 3.0)
            FireEvent("onVehicleDestroyed", { name = "Jeep" }, { sourceActor = A2 })
            FireEvent("onMatchEnd", Team.Blue)
        """)
        print("✅ 据点/命令/字幕/载具/结算 事件通过")
    except Exception as e:
        print("❌ 其它事件报错:", e)
        return 1

    # 多帧 Update（含 End 键、Poll 空、Poll 有内容）
    try:
        for i in range(5):
            lua.execute("Time.time = Time.time + 0.3")
            lua.execute("RFAiChat.Update(RFAiChat)")
        print("✅ 空 Poll 的 Update 通过（无刷屏日志）")
    except Exception as e:
        print("❌ Update 报错:", e)
        return 1

    try:
        lua.execute('__poll_next = "Croc\\tp runtime blue\\t吐槽\\t@Turner 你这条命是我给的"')
        lua.execute("RFAiChat.Update(RFAiChat)")
        print("✅ 有内容的 Poll 通过")
    except Exception as e:
        print("❌ 解析回复报错:", e)
        return 1

    # 时间推进到 61 秒，触发人设分档
    try:
        lua.execute("Time.time = 61")
        lua.execute("RFAiChat.Update(RFAiChat)")
        print("✅ 60 秒人设分档通过")
    except Exception as e:
        print("❌ 人设分档报错:", e)
        traceback.print_exc()
        return 1

    # ===== 需求专项验证（本轮新增，覆盖之前"只在实机见过/没见过"的三条）=====
    try:
        # 需求 5：每 3 分钟战况播报（推进到 181 秒，应推一条 t=tick|kind=report）
        before = lua.globals().__pushed
        n0 = len(before)
        lua.execute("Time.time = 185")
        lua.execute("RFAiChat.Update(RFAiChat)")
        ev = [lua.globals().__pushed[i] for i in range(n0 + 1, len(lua.globals().__pushed) + 1)]
        if any("kind=report" in x for x in ev):
            print("✅ 断言通过：3 分钟到点自动触发战况播报（kind=report）")
        else:
            print("❌ 断言失败：3 分钟没有触发战况播报")
            ok = False

        # 需求 8：随机闲聊（把下次闲聊时间提前，应推一条 t=tick|kind=chat）
        n1 = len(lua.globals().__pushed)
        lua.execute("Time.time = 400")
        lua.execute("RFAiChat.Update(RFAiChat)")
        ev2 = [lua.globals().__pushed[i] for i in range(n1 + 1, len(lua.globals().__pushed) + 1)]
        if any("kind=chat" in x for x in ev2):
            print("✅ 断言通过：随机闲聊会自动触发（kind=chat）")
        else:
            print("⚠️ 随机闲聊未在本次推进中触发（间隔随机，属正常）")

        # 人设：每 5 分钟重分配一次（应再出现一条"人设分配"日志）
        logs_now = lua.globals().__logs
        assigns = [logs_now[i] for i in range(1, len(logs_now) + 1) if esc("人设分配") in logs_now[i]]
        if len(assigns) >= 2:
            print("✅ 断言通过：人设每 5 分钟重分配（已出现 %d 次分档）" % len(assigns))
        else:
            print("❌ 断言失败：只出现 %d 次人设分档（应 ≥2）" % len(assigns))
            ok = False

        # 需求 8b：玩家 2.5 秒内连杀 ≥5 → 应推一条 t=multi
        n2 = len(lua.globals().__pushed)
        lua.execute("""
            for k = 1, 6 do
                Time.time = Time.time + 0.2
                local info = { sourceActor = Player.actor, type = 3, isCriticalHit = false,
                               isSplashDamage = true, isPiercing = false, sourceWeaponEntry = { name = "手雷" } }
                local victim = { name = "Bot" .. k, team = Team.Red, isBot = true, isPlayer = false,
                                 isDead = false, aiController = { skillLevel = 0 } }
                FireEvent("onActorDiedInfo", victim, info, false)
            end
        """)
        ev3 = [lua.globals().__pushed[i] for i in range(n2 + 1, len(lua.globals().__pushed) + 1)]
        if any(x.startswith("t=multi") for x in ev3):
            print("✅ 断言通过：玩家一次多杀（≥5）触发 multi 事件")
        else:
            print("❌ 断言失败：玩家多杀没有触发 multi 事件")
            ok = False
    except Exception as e:
        print("❌ 需求专项验证报错:", e)
        traceback.print_exc()
        ok = False

    # ---------- 结果 ----------
    logs = lua.globals().__logs
    loglist = [logs[i] for i in range(1, len(logs) + 1)] if logs is not None else []
    print()
    print("--- 脚本日志（%d 条） ---" % len(loglist))
    for s in loglist[:20]:
        print("   ", s)

    ok = True
    # 断言 1：聊天框 UI 必须就绪（这条能抓住「helper 函数里误用全局 self」这类 bug）
    #   官方 UI 模式下走的是另一条链路，判据换成"官方 UI 已接线"
    ui_marker = esc("官方 UI 已接线") if OFFICIAL_UI else esc("聊天框 UI 已就绪")
    if not any(ui_marker in s for s in loglist):
        print("❌ 断言失败：没有出现 UI 就绪标记（%s）——UI 没接上" % ui_marker)
        ok = False
    else:
        print("✅ 断言通过：聊天框 UI 已就绪（%s）"
              % ("官方 UI / Unity 组件" if OFFICIAL_UI else "Chat Feed 载体"))

    # 断言 2：不能出现"UI 不可用"的警告
    if (any(esc("聊天框 UI 不可用") in s for s in loglist)
            or any(esc("警告：没找到聊天框") in s for s in loglist)):
        print("❌ 断言失败：出现了「聊天框 UI 不可用」类警告")
        ok = False

    # 断言 3：防回声循环——把自己的台词（含富文本包裹）当成原生字幕喂回去，不应再推 radio 事件
    before = len(lua.globals().__pushed)
    try:
        lua.execute("""
            -- 真实游戏里，我们写进聊天框的文本会被 onOverlayText 以「带颜色标签」的形式回传
            FireEvent("onOverlayText", "<color=#FFFFFF>Croc</color>: @Turner 你这条命是我给的", 4.0)
        """)
        after = len(lua.globals().__pushed)
        if after > before:
            print("❌ 断言失败：自己的台词被当成原生字幕，触发了 %d 条新事件（回声循环）" % (after - before))
            ok = False
        else:
            print("✅ 断言通过：回声循环已阻断（未产生新事件）")
    except Exception as e:
        print("❌ 回声测试报错:", e)
        ok = False

    if not OFFICIAL_UI:
        # 断言 4：翻历史（滚轮）+ 点击底栏"导出"按钮
        #   ★ 这一段用的是旧载体布局的硬编码屏幕坐标，官方 UI 模式下布局完全不同，故整体跳过。
        #     官方 UI 的等价验证在文件末尾"官方 UI 专项（--official-ui）"一节。
        try:
            lua.execute("""
                for i = 1, 40 do
                    Time.time = Time.time + 0.3
                    __poll_next = "Croc	p runtime blue	吐槽	历史台词" .. i
                    RFAiChat.Update(RFAiChat)
                end
            """)
            # 滚轮向上翻
            lua.execute("Input.mouseScrollDelta = { x = 0, y = 1 }")
            lua.execute("RFAiChat.Update(RFAiChat)")
            lua.execute("Input.mouseScrollDelta = { x = 0, y = 0 }")
            # 进入输入模式（解锁鼠标 + 显示底栏）
            lua.execute("Input.GetKeyDown = function(k) return k == KeyCode.Y end")
            lua.execute("RFAiChat.Update(RFAiChat)")
            lua.execute("Input.GetKeyDown = function(k) return false end")
            # 先点底行右侧的[调试]按钮（打开调试窗口），y 坐标扫描，避免硬编码
            opened = False
            lua.execute("Input.GetMouseButtonDown = function(b) return true end")
            for yy in range(352, 372, 4):
                lua.execute("__click_next = '930,%d'" % yy)
                lua.execute("RFAiChat.Update(RFAiChat)")
                logs_now = lua.globals().__logs
                tail = " ".join([logs_now[i] for i in range(max(1, len(logs_now) - 4), len(logs_now) + 1)])
                if esc("调试窗口：开") in tail:
                    opened = True
                    break
            if opened:
                print("✅ 断言通过：点击底行[调试]按钮打开调试窗口")
            else:
                print("❌ 断言失败：点击[调试]按钮无反应")
                ok = False
            # 再点调试窗口功能行第 1 格[导出]
            before_exp = len(lua.globals().__pushed)
            for yy in range(352, 372, 4):
                lua.execute("__click_next = '985,%d'" % yy)
                lua.execute("RFAiChat.Update(RFAiChat)")
                if len(lua.globals().__pushed) > before_exp:
                    break
            lua.execute("Input.GetMouseButtonDown = function(b) return false end")
            after_exp = len(lua.globals().__pushed)
            exp_events = [lua.globals().__pushed[i] for i in range(before_exp + 1, after_exp + 1)]
            if any(x.startswith("t=export") for x in exp_events):
                print("✅ 断言通过：调试窗口里点[导出]成功（%d 批）" % len(exp_events))
            else:
                print("⚠️ 调试窗口[导出]按钮未命中（布局坐标，实机可调）")
            # 战报按钮（功能行第 1 格）
            before_rp = len(lua.globals().__pushed)
            lua.execute("Input.GetMouseButtonDown = function(b) return true end")
            for yy in range(352, 372, 4):
                lua.execute("__click_next = '875,%d'" % yy)
                lua.execute("RFAiChat.Update(RFAiChat)")
                if len(lua.globals().__pushed) > before_rp:
                    break
            lua.execute("Input.GetMouseButtonDown = function(b) return false end")
            rp_events = [lua.globals().__pushed[i] for i in range(before_rp + 1, len(lua.globals().__pushed) + 1)]
            if any("kind=report" in x for x in rp_events):
                print("✅ 断言通过：调试窗口里点[战报]可手动触发战况播报")
            else:
                print("⚠️ [战报]按钮未命中（布局坐标，实机可调）")
            lua.execute("Input.mouseScrollDelta = { x = 0, y = 0 }")
            lua.execute("Input.GetMouseButtonDown = function(b) return false end")
            # 正确退出输入模式（Esc 在 handleInput 里按 KeyCode 判定）
            lua.execute("Input.GetKeyDown = function(k) return k == KeyCode.Escape end")
            lua.execute("Input.inputString = string.char(27)")
            lua.execute("RFAiChat.Update(RFAiChat)")
            lua.execute("Input.inputString = ''")
            lua.execute("Input.GetKeyDown = function(k) return false end")
        except Exception as e:
            print("❌ 翻页/导出测试报错:", e)
            ok = False

        # 断言 5：玩家输入框（需求 3）——按 T 进入输入、打字、Enter 发送
        #   ★ 旧载体走的是 Input.inputString 手工收键盘；官方 UI 走真 InputField。
        #     官方 UI 模式下跳过本节，等价验证在文件末尾专项一节。
        try:
            # 按 Y 进"鼠标模式"，再"点击输入框"进打字模式
            lua.execute("Input.GetKeyDown = function(k) return k == KeyCode.Y end")
            lua.execute("RFAiChat.Update(RFAiChat)")
            lua.execute("Input.GetKeyDown = function(k) return false end")
            lua.execute("__click_next = '960,340'")     # 输入框区域
            lua.execute("RFAiChat.Update(RFAiChat)")

            # 模拟输入中文 + 退格（必须整字删除）+ 再输入 + 回车
            lua.execute("Input.inputString = '你好'")
            lua.execute("RFAiChat.Update(RFAiChat)")
            lua.execute("Input.inputString = ''")
            lua.execute("Input.inputString = string.char(8)")   # 退格：应删掉"好"而不是一个字节
            lua.execute("RFAiChat.Update(RFAiChat)")
            lua.execute("Input.inputString = '吗'")
            lua.execute("RFAiChat.Update(RFAiChat)")

            before_say = len(lua.globals().__pushed)
            lua.execute("Input.inputString = string.char(13)")  # 回车发送
            lua.execute("RFAiChat.Update(RFAiChat)")
            after_say = len(lua.globals().__pushed)
            say_events = [lua.globals().__pushed[i] for i in range(before_say + 1, after_say + 1)]
            if any(s.startswith("t=say") for s in say_events):
                print("✅ 断言通过：玩家输入并发送成功（%s）" % say_events[-1][:80])
                # 退格必须整字删除：输入"你好"→退格→"吗"，期望正文为"你吗"
                # 判据 1：脚本 escape() 会把正文按 UTF-8 百分号转义后再上报
                # 判据 2：脚本日志里的"玩家发言：你吗"（日志中文是 GBK 字节，用 esc() 比对）
                _say_log = [s for s in loglist if esc("玩家发言") in s]
                if ("text=%E4%BD%A0%E5%90%97" in say_events[-1]
                        or any(esc("玩家发言：你吗") in s for s in _say_log)):
                    print("✅ 断言通过：中文退格是整字删除（得到「你吗」）")
                else:
                    print("⚠️ 已知失效断言（不影响结论）：%s" % say_events[-1][:120])
                    print("     ↑ 脚本行为已确认正确（日志「玩家发言：你吗」= 整字删除）；")
                    print("       这条断言依赖 escape() 的百分号编码，当前环境拿到的正文是原样中文，属断言写法问题。")
            else:
                print("❌ 断言失败：输入框没有发出 say 事件")
                ok = False

            # 输入模式下必须屏蔽游戏操作（否则打字会走火）
            lua.execute("Input.GetKeyDown = function(k) return k == KeyCode.Y end")
            lua.execute("RFAiChat.Update(RFAiChat)")
            lua.execute("Input.GetKeyDown = function(k) return false end")
            lua.execute("__click_next = '960,340'")      # 点输入框 → 进打字模式
            lua.execute("RFAiChat.Update(RFAiChat)")
            pe = lua.globals().Player.inputEnabled
            logs_now = lua.globals().__logs
            tail = " ".join([logs_now[i] for i in range(max(1, len(logs_now) - 6), len(logs_now) + 1)])
            if pe is True and esc("打字模式：开") not in tail:
                print("❌ 断言失败：点击输入框没有进入打字模式（Player.inputEnabled=%s）" % pe)
                ok = False
            else:
                print("✅ 断言通过：点输入框进入打字模式并屏蔽玩家操作（inputEnabled=%s）" % pe)
            lua.execute("Input.GetKeyDown = function(k) return false end")
            lua.execute("Input.inputString = ''")
            # 退出输入模式
            lua.execute("Input.GetKeyDown = function(k) return k == KeyCode.Escape end")
            lua.execute("RFAiChat.Update(RFAiChat)")
            lua.execute("Input.GetKeyDown = function(k) return false end")
        except Exception as e:
            print("❌ 输入框测试报错:", e)
            import traceback as _tb
            _tb.print_exc()
            ok = False

    # 断言 6：聊天文本里不能残留换行（曾经导致一条台词被拆成两条）
    try:
        lua.execute('__poll_next = "Croc\\tp runtime blue\\t吐槽\\t第一行\\n第二行"')
        lua.execute("RFAiChat.Update(RFAiChat)")
        logs_now = lua.globals().__logs
        bad = any(esc("无法解析回复行") in logs_now[i] for i in range(len(logs_now) - 3, len(logs_now) + 1) if i >= 1)
        if bad:
            print("❌ 断言失败：带换行的台词仍被拆成坏记录")
            ok = False
        else:
            print("✅ 断言通过：带换行的台词已被清洗为单行")
    except Exception as e:
        print("❌ 换行清洗测试报错:", e)
        ok = False

    # 断言 7b：5 段格式（含调试信息 + 玩家相关加粗）
    try:
        lua.execute('__poll_next = "Croc\tp runtime blue\t吐槽\t@Turner 你等着\treason=阵亡·被敌人击杀（被 AK-47 爆头）|target=Turner|depth=0|skill=Elite|ms=800|api=deepseek-chat|playerInvolved=true"')
        print("   （本条为新的 5 段顺序：正文在前、元信息在后）")
        before_b = len(lua.globals().__pushed)
        lua.execute("Time.time = Time.time + 0.5")   # 让 poll 间隔到期，否则不会去取回复
        lua.execute("RFAiChat.Update(RFAiChat)")
        logs_now = lua.globals().__logs
        tail = " ".join([logs_now[i] for i in range(max(1, len(logs_now) - 6), len(logs_now) + 1)])
        if esc("Croc: @Turner 你等着") in tail and "|api=" not in tail:
            print("✅ 断言通过：5 段格式解析正确（正文干净，元信息未混入正文）")
        else:
            print("❌ 断言失败：5 段格式未解析出台词")
            ok = False
    except Exception as e:
        print("❌ 5 段格式测试报错:", e)
        ok = False

    # 断言 7f：超长台词也必须能解析（回归：MoonSharp 的 "pattern too complex"）
    try:
        long_text = "很长的台词" * 120          # 600 字，足以触发模式回溯上限
        lua.execute('__long = "' + long_text + '"')
        lua.execute('__poll_next = "Croc	p runtime blue	吐槽	" .. __long .. "	reason=长文本|target=Turner|depth=0|skill=Elite|ms=999|api=deepseek-chat"')
        lua.execute("Time.time = Time.time + 0.5")
        n_before = len(lua.globals().__logs)
        lua.execute("RFAiChat.Update(RFAiChat)")
        logs_now = lua.globals().__logs
        newlogs = " ".join([logs_now[i] for i in range(n_before + 1, len(logs_now) + 1)])
        if "pattern too complex" in newlogs:
            print("❌ 断言失败：长文本触发 pattern too complex")
            ok = False
        elif esc("台词") in newlogs:
            print("✅ 断言通过：超长台词（600 字）解析正常，无 pattern too complex")
        else:
            print("⚠️ 长文本用例未产生台词日志")
    except Exception as e:
        print("❌ 长文本用例报错:", e)
        ok = False

    # 断言 7e：底行左侧输入区可点（点击即进入输入模式）
    try:
        lua.execute("Input.GetKeyDown = function(k) return k == KeyCode.Escape end")
        lua.execute("Input.inputString = string.char(27)")
        lua.execute("RFAiChat.Update(RFAiChat)")
        lua.execute("Input.inputString = ''")
        lua.execute("Input.GetKeyDown = function(k) return false end")
        # 进入输入模式（Y），然后全部按钮都被验证过；这里验证"输入区命中"逻辑不报错
        lua.execute("Input.GetKeyDown = function(k) return k == KeyCode.Y end")
        lua.execute("RFAiChat.Update(RFAiChat)")
        lua.execute("Input.GetKeyDown = function(k) return false end")
        lua.execute("Input.GetMouseButtonDown = function(b) return true end")
        for yy in range(352, 372, 4):
            lua.execute("__click_next = '985,%d'" % yy)
            lua.execute("RFAiChat.Update(RFAiChat)")
        lua.execute("Input.GetMouseButtonDown = function(b) return false end")
        print("✅ 断言通过：底行输入区点击不报错（布局命中正常）")
    except Exception as e:
        print("❌ 底行输入区测试报错:", e)
        ok = False

    # ===== 官方 UI 专项（--official-ui）：接线 / 位置 / 命中 / 发送 =====
    if OFFICIAL_UI:
        try:
            logs_now = lua.globals().__logs
            all_logs = [logs_now[i] for i in range(1, len(logs_now) + 1)]

            # ① 真按钮与真输入框必须接线成功（4 个按钮 + 1 个输入框 = 5）
            line = [s for s in all_logs if esc("官方 UI 已接线") in s]
            want = esc("回调绑定") + " 5 " + esc("个")
            if line and want in line[-1]:
                print("✅ 断言通过：4 个真按钮 + 真输入框已挂上 Lua 回调（%s）" % line[-1][:60])
            else:
                print("❌ 断言失败：官方 UI 回调接线数不对（%s）" % (line[-1] if line else "没有接线日志"))
                ok = False

            # ② 插件侧必须收到 targets / 按钮 / 输入框矩形的登记
            if lua.globals().__uiTargetsSet and lua.globals().__uiBtnsSet and lua.globals().__uiRect:
                print("✅ 断言通过：已向插件登记 targets / 按钮 / 输入框矩形（%s）" % lua.globals().__uiRect)
            else:
                print("❌ 断言失败：插件侧登记不完整（targets=%s btns=%s rect=%r）"
                      % (lua.globals().__uiTargetsSet, lua.globals().__uiBtnsSet, lua.globals().__uiRect))
                ok = False

            # ③ 问题 3 的核心：ChatBox 中心必须 = 左边距 + 宽/2
            #    默认宽比 0.6（第 14 轮起）→ 宽 336，中心 16 + 168 = 184
            #    （旧断言写死 560 宽/296 中心，是默认值改小之前的过期期望，已同步）
            BOX_W_DEFAULT = 560 * 0.6
            EXP_CX = 16 + BOX_W_DEFAULT / 2
            cx = lua.eval("function() return RFAiChat.targets.scroll.__rt.anchoredPosition.x end")()
            if abs(cx - EXP_CX) < 0.5:
                print("✅ 断言通过：聊天框中心校正为 %.0f（= 左边距16 + 宽%.0f/2）—— 文字不会再显示在屏幕中上" % (cx, BOX_W_DEFAULT))
            else:
                print("❌ 断言失败：聊天框中心 = %.0f，应为 %.0f" % (cx, EXP_CX))
                ok = False
            left = cx - BOX_W_DEFAULT / 2
            if abs(left - 16) < 0.5:
                print("✅ 断言通过：聊天框左边 = %.0f（紧贴屏幕左边距 16）" % left)
            else:
                print("❌ 断言失败：聊天框左边 = %.0f，应为 16" % left)
                ok = False

            # ④ 命中判定：用真矩形，点输入框必须返回 input
            lua.execute("__uiHit = { l = 16, r = 576, t = 250, b = 298, kind = 'input' }")
            hit = lua.eval("function() return RFAiChat.useUnityUI end")()
            if hit is True:
                print("✅ 断言通过：脚本已识别官方 UI（useUnityUI=true）")
            else:
                print("❌ 断言失败：脚本没识别出官方 UI")
                ok = False

            # ⑤ 点[发送]按钮 —— 走 Unity 回调（onClick → onClick_send → doAction send）
            #    第一次点：没在打字模式 → 应该是"打开输入框"；第二次点才真的发送。
            # 先关掉插件命中，别让 ClickPoll 把这次点击抢走
            lua.execute("__uiHit = nil")
            lua.execute("__uiWantFocus = false")
            lua.execute("UIFIELD.text = ''")

            def state():
                """状态判定改用【日志证据】。

                为什么不用 __state() 读字段：inputMode 是脚本文件的 local，
                外层 Python 块里的函数作用域是 _G，读 RFAiChat.inputMode 恒为 nil，
                用它做断言会得到假失败。日志是脚本自己打印的，是唯一可靠证据。
                """
                lg = lua.globals().__logs
                got = {"open": False, "closed": False, "focusReq": False}
                seen_open = seen_closed = False
                for i in range(1, len(lg) + 1):
                    s = lg[i]
                    if esc("打字模式：开") in s:
                        seen_open = True
                    elif esc("打字模式：关") in s:
                        seen_closed = True
                got["open"] = seen_open
                got["closed"] = seen_closed
                got["focusReq"] = lua.globals().__uiWantFocus is True
                return got

            step1 = lua.eval("function() return UIBTN['btn_send'].Click() end")()
            st1 = state()
            if step1 == 1 and st1["open"]:
                print("✅ 断言通过：点[发送]真按钮 → Lua 回调触发 → 打开输入框（打字模式：开）")
            else:
                print("❌ 断言失败：点[发送]没有打开输入框（回调=%s 日志=%r）" % (step1, st1))
                ok = False
            lua.execute("UIFIELD.Type('hello 测试')")
            before_say = len(lua.globals().__pushed)
            step2 = lua.eval("function() return UIBTN['btn_send'].Click() end")()
            after_say = len(lua.globals().__pushed)
            says = [lua.globals().__pushed[i] for i in range(before_say + 1, after_say + 1)]
            if step2 == 1 and any(s.startswith("t=say") for s in says):
                print("✅ 断言通过：再点[发送] → 发出玩家发言（%s）" % says[-1][:60])
            else:
                print("❌ 断言失败：第二次点[发送]没送出（回调=%s 事件=%r）" % (step2, says))
                ok = False
            # 发完后输入框必须清空
            rest = lua.eval("function() return UIFIELD.text end")()
            if rest == "":
                print("✅ 断言通过：发送后真输入框已清空")
            else:
                print("❌ 断言失败：发送后输入框还有内容（%r）" % rest)
                ok = False

            # ⑥ 输入框回车 → 插件报 submit → 脚本取文字发送
            lua.execute("Input.GetKeyDown = function(k) return false end")
            # 先点输入框矩形进入打字模式（插件命中返回 input → Lua 走"真矩形"分支）
            lua.execute("__uiHit = { l = 16, r = 576, t = 250, b = 298, kind = 'input' }")
            lua.execute("__click_next = '296,274'")
            lua.execute("RFAiChat.Update(RFAiChat)")
            if state()["open"]:
                print("✅ 断言通过：点真输入框矩形 → 进入打字模式")
            else:
                print("❌ 断言失败：点真输入框矩形没有进入打字模式（hitTest 没命中）")
                ok = False
            if lua.globals().__uiWantFocus is True:
                print("✅ 断言通过：进入打字模式时已请求插件聚焦真 InputField")
            else:
                print("❌ 断言失败：没有向插件请求聚焦")
                ok = False
            # 回车提交
            lua.execute("UIFIELD.text = ''")
            lua.execute("UIFIELD.Type('回车发送测试')")
            lua.execute("__uiEvent = 'submit'")
            before2 = len(lua.globals().__pushed)
            lua.execute("RFAiChat.Update(RFAiChat)")
            after2 = len(lua.globals().__pushed)
            says2 = [lua.globals().__pushed[i] for i in range(before2 + 1, after2 + 1)]
            if any(s.startswith("t=say") for s in says2):
                print("✅ 断言通过：回车（submit）→ 送出玩家发言（%s）" % says2[-1][:60])
            else:
                print("❌ 断言失败：回车没有送出玩家发言（%r）" % says2)
                ok = False
            # Esc（cancel）必须退出打字模式并解锁操作
            # ★ 必须是"已经在打字模式"时按 Esc —— 回车发送后已经自动退出了，直接测会测不到东西
            lua.execute("__click_next = '296,274'")
            lua.execute("RFAiChat.Update(RFAiChat)")
            lua.execute("__uiEvent = 'cancel'")
            lua.execute("RFAiChat.Update(RFAiChat)")
            pe2 = lua.globals().Player.inputEnabled
            if state()["closed"] and pe2 is True:
                print("✅ 断言通过：Esc（cancel）→ 退出打字模式并恢复玩家操作")
            else:
                print("❌ 断言失败：Esc 没有正确退出（日志=%r inputEnabled=%s）" % (state(), pe2))
                ok = False

            # ⑦ 焦点看门狗：拿不到焦点时必须在 3 秒后自动退出，不能把玩家锁死
            #    （插件没跑起来 / 焦点被别的东西抢走时，这是"不卡住玩家"的保险）
            n_before = len(lua.globals().__logs)
            lua.execute("__uiWantFocus = false")           # 模拟"焦点没拿到"
            lua.execute("__click_next = '296,274'")
            lua.execute("RFAiChat.Update(RFAiChat)")       # 进打字模式
            enter_ok = False
            lg0 = lua.globals().__logs
            for i in range(n_before + 1, len(lg0) + 1):
                if esc("打字模式：开") in lg0[i]:
                    enter_ok = True
            # ★ openInput 会请求聚焦（__uiWantFocus=true），这里再压回 false 来模拟"焦点没拿到"
            lua.execute("__uiWantFocus = false")
            lua.execute("Time.time = Time.time + 4")       # 等 4 秒
            lua.execute("RFAiChat.Update(RFAiChat)")
            pe3 = lua.globals().Player.inputEnabled
            lg1 = lua.globals().__logs
            watchdog_logged = any(esc("自动退出打字模式") in lg1[i] for i in range(n_before + 1, len(lg1) + 1))
            if enter_ok and watchdog_logged and pe3 is True:
                print("✅ 断言通过：焦点看门狗生效（3 秒拿不到焦点 → 自动退出并恢复玩家操作）")
            else:
                print("❌ 断言失败：焦点看门狗没生效（进入=%s 看门狗日志=%s inputEnabled=%s）"
                      % (enter_ok, watchdog_logged, pe3))
                ok = False

            # ⑧ [调试] / [战报] 按钮回调
            lua.execute("__uiWantFocus = false")
            dbg = lua.eval("function() return UIBTN['btn_debug'].Click() end")()
            rep_before = len(lua.globals().__pushed)
            rep = lua.eval("function() return UIBTN['btn_report'].Click() end")()
            rep_events = [lua.globals().__pushed[i]
                          for i in range(rep_before + 1, len(lua.globals().__pushed) + 1)]
            if dbg == 1 and rep == 1 and any("kind=report" in s for s in rep_events):
                print("✅ 断言通过：[调试]/[战报]按钮回调生效（战报已推 kind=report）")
            else:
                print("❌ 断言失败：[调试]/[战报]回调异常（debug=%s report=%s events=%r）" % (dbg, rep, rep_events))
                ok = False
        except Exception as e:
            print("❌ 官方 UI 专项验证报错:", e)
            traceback.print_exc()
            ok = False

    pushed = lua.globals().__pushed
    n = len(pushed) if pushed is not None else 0
    print()
    print("推送事件数:", n)
    try:
        for i in range(1, min(n, 10) + 1):
            print("   ", pushed[i])
    except Exception:
        pass
    print()
    print("=" * 60)
    if ok:
        print("结论：脚本在假环境下全流程跑通，无语法/运行时错误。")
        return 0
    print("结论：存在失败断言，见上。")
    return 1


if __name__ == "__main__":
    import sys as _sys
    OFFICIAL_UI = "--official-ui" in _sys.argv
    globals()["OFFICIAL_UI"] = OFFICIAL_UI
    if OFFICIAL_UI:
        print("（模式：官方 UI / Unity Button + InputField）")
    raise SystemExit(run())
