--[[
================================================================================
 RFAiChat —— RF 对话脚本 AI 接入 MOD（游戏侧脚本）
 基座：Chat Feed 原版（SteelRaven7）的 UI 与显示约定
 设计依据：../10-设计文档/设计书-总览.md

 通信协议（行协议，避免在 Lua 里手写 JSON）：
   脚本 → 插件： AiChatBridge.Push("类型|k=v|k=v|...")
   插件 → 脚本： AiChatBridge.Poll() 返回若干条记录，记录间 \n 分隔，
                每条 5 段 \t 分隔： 显示名 \t 姿态 \t 风格 \t 调试信息 \t 正文
   调试：       AiChatBridge.Status()

 键位（可在游戏 MOD 设置界面改）：
   chatkey   默认 Y   开关打字模式（打开后 Enter 发送 / Esc 取消）
   debugkey  默认 F8  开关"调试窗口"（显示每条台词：为什么说、对谁说、档位、耗时）
   exportkey 默认 F9  导出聊天记录到文件
   另有 F10（测试用：伪造友军误伤）/ F11（测试用：立刻播报战况）
   PageUp / 鼠标滚轮上滚 翻历史；PageDown 回最新；End 显示/隐藏聊天框
================================================================================
]]

behaviour("RFAiChat")

-- ============================== 基础配置 ==============================
local CFG = {
    pollInterval   = 0.25,
    ratio1         = 25,    -- 名次比例 ≤ ratio1% → 王牌
    ratio2         = 50,    -- 名次比例 ≤ ratio2% → 精锐
    ratio3         = 75,    -- 名次比例 ≤ ratio3% → 老兵；其余 → 新兵
    firstAssignAt  = 60,
    reassignEvery  = 300,
    reportEvery    = 180,
    planB          = false,   -- ★ 第92轮：C# 显示层已接管 → 停止 Lua 视觉写入
    idleChatMin    = 40,
    idleChatMax    = 110,
    radioMinGap    = 3.0,
    logEvery       = 30,
    historyMax     = 400,
    promptSeconds  = 45,     -- 开局显示操作提示的秒数
}

-- 游戏内设置（Mutator 设置界面可改）
local CONF = {
    apiUrl = "", apiKey = "", model = "",
    lang = "zh",
    freq = 1,
    enable = true,
    chatkey = "Y",        -- 唯一快捷键：调出鼠标 + 输入框
    fontSize = 16,        -- 字号（绝对值，不再用比例；原版是 14）
    boxWidth = 0.60,      -- 聊天框宽度（倍数，1.0=原宽）
    boxHeight = 1.00,     -- 聊天框高度倍数（只往上下拉长，不往左右拉宽）
    leftMargin = 0,       -- 左边距（0 = 完全不动位置，用预制体原样；>0 才微调横向位置）
    maxLines = 14,
}

-- ============================== 状态 ==============================
local history = {}        -- 聊天正文（可翻阅、可导出）
local historyMeta = {}    -- 与 history 一一对应的调试信息
local viewOffset = 0
local debugMode = false
local maxLines = 14   -- 第17轮：内部固定值（不再是设置项）
local hoverBtn = nil
local lastDiagTxt = nil
local textFoundLogged = false
local textMissingLogged = false
local pressedBtn = nil
local pressedUntil = 0
local uiMode = false      -- 鼠标模式（按 Y 只调鼠标，不直接进入打字）
local inputMode = false   -- 打字模式（点输入框才进）
local scroll_ref, BASE_W, BASE_H = nil, nil, nil
local UI_TXT = {}         -- 独立的文字图层（按钮/输入框专用，不再是聊天文本的行）
local UI_OBJ = {}         -- 包里手工新增的图层（白线边框 / 按钮底板）
local inputText = ""
local lastRows = 1        -- 上次渲染的行数（底行按钮位置按行数算）
local SCROLL_RT = nil     -- 聊天框滚动区（用于鼠标命中判定，比按文字估算可靠）
local BOX_W = 200         -- 文字区宽度（本地单位）
local CANVAS_SCALE = 1    -- 画布缩放系数（CanvasScaler 决定；分辨率≠1080p 时不是 1，必须算进去）
local INPUT_RECT = nil    -- 输入框在屏幕上的矩形（命中判定用）
local CHAT_RECT = nil     -- 聊天框在屏幕上的矩形
local GAP = 8             -- 两个框之间的空隙（本地单位）
local CHAT_H = 300        -- 聊天框高度（本地单位）
local ROWS_CHAT = 15      -- 聊天区行数（tuneUI 里算好，renderView 与底板定位共用）
local ROWS_GAP = 1        -- 两框之间的空行数
local TEXT_TOP_PAD = 16   -- 从聊天框顶边到文字第一行的距离（视口6 + 内容6 + 文本4）
local INPUT_H = 68        -- 输入框高度（本地单位）
local function lineHeight() return math.max(8, (CONF.fontSize or 16) * 1.25) end
local BOX_H = 400         -- 聊天框高度（本地单位）
-- 估算一行文字会占几个"视觉行"：长句会自动换行，必须算进去，否则布局会错
local function stripTags(s) return (string.gsub(tostring(s), "<[^>]*>", "")) end
local function colsOf(s)
    local t = stripTags(s)
    local cols = 0
    for i = 1, #t do
        local b = string.byte(t, i)
        if b < 128 then cols = cols + 0.5
        elseif b >= 192 then cols = cols + 1 end
    end
    return cols
end
local function cutAscii(s, maxCols)
    local t = stripTags(s)
    local acc, c, i = {}, 0, 1
    while i <= #t do
        local b = string.byte(t, i)
        local len = 1
        if b >= 240 then len = 4 elseif b >= 224 then len = 3 elseif b >= 192 then len = 2 end
        local w = (len == 1) and 1 or 2
        if c + w > maxCols then break end
        acc[#acc + 1] = string.sub(t, i, i + len - 1)
        c = c + w
        i = i + len
    end
    return table.concat(acc), c
end

-- 收集包里新增的图层（名字 AIChat_UI_0..N）
local function collectUIObjects(scroll)
    UI_OBJ = {}
    if type(scroll) == "nil" then return end
    local n = 0
    pcall(function() n = scroll.childCount or 0 end)
    for i = 0, n - 1 do
        local ch = nil
        pcall(function() ch = scroll.GetChild(i) end)
        if type(ch) ~= "nil" then
            local nm = nil
            pcall(function() nm = ch.name end)
            nm = tostring(nm or "")
            if string.sub(nm, 1, 10) == "AIChat_UI_" then
                local idx = tonumber(string.sub(nm, 11))
                if type(idx) ~= "nil" then
                    pcall(function() UI_OBJ[idx] = ch.asRectTransform end)
                end
            end
        end
    end
end

-- 摆一个新增图层：右上是锚点，坐标用"相对 Scroll View 顶边中心"
local function placeUI(idx, x, y, ww, hh, alpha, r, g, b)
    local o = UI_OBJ[idx]
    if type(o) == "nil" then return end
    pcall(function()
        o.anchorMin = Vector2(0.5, 1); o.anchorMax = Vector2(0.5, 1); o.pivot = Vector2(0.5, 1)
        o.sizeDelta = Vector2(ww, hh)
        -- ★ 锚点是"Scroll View 顶边中心"，所以绝对坐标要减去框中心/框顶边
        o.anchoredPosition = Vector2(x - (BOX_X or 0), y - (BOX_TOP or 0))
        o.gameObject.SetActive(true)
    end)
    if type(alpha) ~= "nil" then
        pcall(function()
            local img = o.gameObject.GetComponent(Image)
            if type(img) ~= "nil" then
                img.color = Color(r or 1, g or 1, b or 1, alpha)
                img.raycastTarget = false
            end
        end)
    end
end

local function collectUITexts(scroll)
    UI_TXT = {}
    if type(scroll) == "nil" then return end
    local n = 0
    pcall(function() n = scroll.childCount or 0 end)
    for i = 0, n - 1 do
        local ch = nil
        pcall(function() ch = scroll.GetChild(i) end)
        if type(ch) ~= "nil" then
            local nm = nil
            pcall(function() nm = ch.name end)
            nm = tostring(nm or "")
            if string.sub(nm, 1, 11) == "AIChat_Txt_" then
                local idx = tonumber(string.sub(nm, 12))
                if type(idx) ~= "nil" then
                    local comp = nil
                    pcall(function() comp = ch.gameObject.GetComponent(Text) end)
                    if type(comp) ~= "nil" then UI_TXT[idx] = comp end
                end
            end
        end
    end
end

local function placeTxt(idx, x, y, ww, hh, cr, cg, cb, ca)
    local t = UI_TXT[idx]
    if type(t) == "nil" then return end
    pcall(function()
        local r = t.rectTransform
        r.anchorMin = Vector2(0.5, 1); r.anchorMax = Vector2(0.5, 1); r.pivot = Vector2(0.5, 0.5)
        r.sizeDelta = Vector2(ww, hh)
        r.anchoredPosition = Vector2(x - (BOX_X or 0), y - (BOX_TOP or 0))
    end)
    pcall(function() t.color = Color(cr or 1, cg or 1, cb or 1, ca or 1) end)
end

local function setTxt(idx, str)
    local t = UI_TXT[idx]
    if type(t) == "nil" then return end
    pcall(function() t.text = str end)
end

local function colsPerLine()
    -- 故意取小一点（-34 而不是 -26）：宁可少显示一行，也不要因换行估算不足把输入框顶出框外
    return math.max(6, math.floor((BOX_W - 34) / (CONF.fontSize or 16)))
end
-- 画 ASCII 方框用的宽度单位：ASCII 字符占 1 个单位（半个中文宽）
local function asciiCols(s)
    local t = stripTags(s)
    local n = 0
    for i = 1, #t do
        local b = string.byte(t, i)
        if b < 128 then n = n + 1
        elseif b >= 192 then n = n + 2 end
    end
    return n
end
local function visualRows(s)
    local r = math.ceil(colsOf(s) / colsPerLine())
    if r < 1 then r = 1 end
    return r
end
-- 按"显示列数"截断字符串（不会把汉字切成半个）
local function cutToCols(s, maxCols)
    local acc, c, i = {}, 0, 1
    while i <= #s do
        local b = string.byte(s, i)
        local len = 1
        if b >= 240 then len = 4 elseif b >= 224 then len = 3 elseif b >= 192 then len = 2 end
        local w = (len == 1) and 0.5 or 1
        if c + w > maxCols then break end
        acc[#acc + 1] = string.sub(s, i, i + len - 1)
        c = c + w
        i = i + len
    end
    return table.concat(acc), c
end
local MAX_LINES = 12      -- 运行时会按字号与框高重算
local UI_FONT = 28
-- 聊天框底部的可点按钮（用鼠标点击，不再占用快捷键）
local BTN_LABELS = { "调试", "导出", "隐藏", "上翻", "下翻" }  -- 保留：旧命名，实际布局见 hitTest
local BTN_COUNT = 5

local roster = {}
local bots = {}
local startedAt = -1
local lastPoll = 0
local lastAssign = -9999
local lastReport = -9999
local lastRadio = -9999
local nextIdleChat = 0
local lastLog = -9999
local pushCount = 0
local sayCount = 0
local bridgeOK = nil
local uiReady = false
local DELAY_LEN, DELAY_AT = nil, nil   -- 延迟读回用
local SELF = nil
local fallbackUsed = 0
local ownRecent = {}
local inputBusy = false
local exportBusy = false
local inputFocused = false   -- 官方 UI：真 InputField 是否拿到键盘焦点（插件回报）
local focusTriedAt = 0       -- 进入打字模式的时刻（焦点看门狗用：拿不到焦点就自动退出，避免"卡在没法操作"）
local focusWarned = false
local inputFocusLogged = false   -- 诊断用：每个打字模式只打一次焦点实测
-- 需求 8：玩家短时间连杀统计
local burst = { count = 0, t0 = -999, fired = false }

local pushLine   -- 前向声明（输入模块会用到）

-- 前向声明：按钮动作与官方 UI 支持要用到下面才定义的函数
-- （Unity Button.onClick 的回调在文件前半段注册，必须先把这些名字占住，否则会解析成全局 nil）
local closeInput, exportHistory, openInput, sendInput, openUI, closeUI, doAction
local hookUnityUI, refreshUIRects, uiDiag
-- 官方 UI 状态
local UI_BTNS = {}       -- { {go=GameObject, act="send"}, ... } —— 真按钮对象与动作名
local UI_BTN_N = 0
local INPUT_SCREEN_RECT = nil   -- 输入框在屏幕上的矩形 {left,top,right,bottom}（y 向下）
local hookUIReady = false
local uiHookErrors = {}         -- 接线失败原因（实机排查用）

-- ============================== 工具 ==============================
local function now() return Time.time end

local function teamName(t)
    if t == Team.Red then return "Red" end
    return "Blue"
end

local function escape(v)
    if type(v) == "nil" then return "" end
    local s = tostring(v)
    s = string.gsub(s, "%%", "%%25")
    s = string.gsub(s, "|", "%%7C")
    s = string.gsub(s, "=", "%%3D")
    s = string.gsub(s, "\n", "%%0A")
    s = string.gsub(s, "\r", "")
    s = string.gsub(s, "[\t\v\f%z]", " ")
    return s
end

local function pushRaw(fields)
    if bridgeOK == false then return end
    if type(AiChatBridge) == "nil" then
        if type(bridgeOK) == "nil" then
            bridgeOK = false
            print("[RFAiChat] 未发现 AiChatBridge（插件未加载），脚本进入静默模式")
        end
        return
    end
    bridgeOK = true
    local parts = {}
    for i = 1, #fields do
        parts[#parts + 1] = tostring(fields[i][1]) .. "=" .. escape(fields[i][2])
    end
    pushCount = pushCount + 1
    AiChatBridge.Push(table.concat(parts, "|"))
end

local function push(fields)
    if bridgeOK == false then return end
    if CONF.enable == false then return end
    pushRaw(fields)
end

-- 把设置里的键名转成 KeyCode。
-- 注意：Ravenscript 的 Input.GetKeyDown(名字) 不接受 "Y" 这类单字母（实测报错
-- "Input Key named: Y is unknown"），所以必须自己映射成 KeyCode 枚举。
local KEYMAP = {
    Y = KeyCode.Y, T = KeyCode.T, U = KeyCode.U, I = KeyCode.I, O = KeyCode.O, P = KeyCode.P,
    H = KeyCode.H, J = KeyCode.J, K = KeyCode.K, L = KeyCode.L, B = KeyCode.B, N = KeyCode.N, M = KeyCode.M,
    G = KeyCode.G, F = KeyCode.F, C = KeyCode.C, V = KeyCode.V, X = KeyCode.X, Z = KeyCode.Z,
    R = KeyCode.R, E = KeyCode.E, Q = KeyCode.Q, W = KeyCode.W, A = KeyCode.A, S = KeyCode.S, D = KeyCode.D,
    F1 = KeyCode.F1, F2 = KeyCode.F2, F3 = KeyCode.F3, F4 = KeyCode.F4, F5 = KeyCode.F5, F6 = KeyCode.F6,
    F7 = KeyCode.F7, F8 = KeyCode.F8, F9 = KeyCode.F9, F10 = KeyCode.F10, F11 = KeyCode.F11, F12 = KeyCode.F12,
    SPACE = KeyCode.Space, RETURN = KeyCode.Return, ESCAPE = KeyCode.Escape,
    TAB = KeyCode.Tab, BACKSPACE = KeyCode.Backspace, SLASH = KeyCode.Slash,
}

local function chatKeyDown()
    local name = string.upper(tostring(CONF.chatkey or "Y"))
    local k = KEYMAP[name]
    if type(k) == "nil" then return false end
    local ok, v = pcall(function() return Input.GetKeyDown(k) end)
    return ok and v == true
end

local function isBot(a)
    if type(a) == "nil" then return false end
    local ok, v = pcall(function() return a.isBot end)
    if ok then return v == true end
    return false
end

-- 取（或建立）士兵记录
local function rec(a)
    if type(a) == "nil" then return nil end
    local r = roster[a]
    if type(r) == "nil" then
        r = { actor = a, name = "?", team = "Blue", kills = 0, deaths = 0, teamkills = 0,
              skill = "Beginner", lastSay = -9999, isPlayer = false }
        roster[a] = r
        bots[#bots + 1] = r
    end
    local ok1, n = pcall(function() return a.name end)
    if ok1 and type(n) ~= "nil" and n ~= "" then r.name = n end
    local ok2, t = pcall(function() return a.team end)
    if ok2 and type(t) ~= "nil" then r.team = teamName(t) end
    local ok3, p = pcall(function() return a.isPlayer end)
    if ok3 and p == true then r.isPlayer = true end
    return r
end

local function refreshRoster()
    local alive = {}
    for i = 1, #bots do
        local r = bots[i]
        local ok = pcall(function() return r.actor.isDead end)
        if ok then alive[#alive + 1] = r end
    end
    bots = alive
end

local function skillEnum(name)
    if name == "Elite" then return SkillLevel.Elite end
    if name == "Veteran" then return SkillLevel.Veteran end
    if name == "Normal" then return SkillLevel.Normal end
    return SkillLevel.Beginner
end

local function setSkill(r, skillName)
    if type(r) == "nil" or type(r.actor) == "nil" then return false end
    if r.skill == skillName then return false end
    local ok, err = pcall(function()
        local ctrl = r.actor.aiController
        if type(ctrl) ~= "nil" then ctrl.skillLevel = skillEnum(skillName) end
    end)
    if ok then r.skill = skillName; return true end
    print("[RFAiChat] 设置 skillLevel 失败(" .. r.name .. "): " .. tostring(err))
    return false
end

-- ============================== 游戏内设置读取 ==============================
local function cfgDropdown(mut, id, def)
    local v = nil
    pcall(function() v = mut.configuration.GetDropdown(id) end)
    if type(v) == "nil" then pcall(function() v = mut.GetConfigurationDropdown(id) end) end
    if type(v) == "nil" then return def end
    return v
end

local function cfgBool(mut, id, def)
    local v = nil
    pcall(function() v = mut.configuration.GetBool(id) end)
    if type(v) == "nil" then pcall(function() v = mut.GetConfigurationBool(id) end) end
    if type(v) == "nil" then return def end
    return v
end

local function cfgString(mut, id, def)
    local v = nil
    pcall(function() v = mut.configuration.GetString(id) end)
    if type(v) == "nil" then pcall(function() v = mut.GetConfigurationString(id) end) end
    if type(v) == "nil" or v == "" then return def end
    return v
end

local function cfgFloat(mut, id, def)
    local v = nil
    pcall(function() v = mut.configuration.GetFloat(id) end)
    if type(v) == "nil" then pcall(function() v = mut.GetConfigurationFloat(id) end) end
    if type(v) == "nil" then return def end
    return v
end

local function cfgInt(mut, id, def)
    local v = nil
    pcall(function() v = mut.configuration.GetInt(id) end)
    if type(v) == "nil" then pcall(function() v = mut.GetConfigurationInt(id) end) end
    if type(v) == "nil" then return def end
    return v
end

local function readConfig(self)
    local mut = nil
    pcall(function() mut = self.script.mutator end)
    if type(mut) == "nil" then
        print("[RFAiChat] 未取到 mutator 配置，使用默认值")
        return
    end
    CONF.lang = (cfgDropdown(mut, "lang", 0) == 1) and "en" or "zh"
    CONF.freq = cfgDropdown(mut, "freq", 1)
    CONF.enable = cfgBool(mut, "enable", true)
    CONF.chatkey = cfgString(mut, "chatkey", "Y")
    -- API 设置（发布工坊用：玩家在 MOD 设置界面里填自己的地址/Key/模型，不写死在代码里）
    CONF.apiUrl = cfgString(mut, "ApiUrl", "")
    CONF.apiKey = cfgString(mut, "ApiKey", "")
    CONF.model  = cfgString(mut, "Model", "")
    CONF.fontSize = cfgInt(mut, "fontSize", 16)
    CONF.boxWidth = cfgFloat(mut, "boxWidth", 0.60)
    CONF.leftMargin = cfgFloat(mut, "leftMargin", 0)
    CONF.boxHeight = cfgFloat(mut, "boxHeight", 2.00)
    print("[RFAiChat] 设置：语言=" .. CONF.lang .. " 频率=" .. tostring(CONF.freq)
        .. " 启用=" .. tostring(CONF.enable) .. " 输入键=" .. CONF.chatkey
        .. " 字号=" .. tostring(CONF.fontSize) .. " 宽比=" .. tostring(CONF.boxWidth)
        .. " 左边距=" .. tostring(CONF.leftMargin))
end

-- ============================== 聊天框 UI ==============================
local function actorTag(name, team)
    local tag = nil
    pcall(function()
        local c = ColorScheme.GetTeamColorBrighter(team)
        c = Color.Lerp(c, Color.white, 0.5)
        tag = ColorScheme.RichTextColorTag(c) .. name .. "</color>"
    end)
    if type(tag) == "nil" then tag = name end
    return tag
end

-- 兜底：确保拿到 Text 组件（原实现只靠模组"目标绑定"，实测会拿不到 → 整个界面空白）
local function ensureText()
    if type(SELF) ~= "nil" and type(SELF.text) ~= "nil" then return true end
    if type(SELF) == "nil" then return false end
    pcall(function() SELF.text = SELF.targets.text.GetComponent(Text) end)
    if type(SELF.text) == "nil" then
        pcall(function() SELF.text = SELF.targets.text.GetComponentInChildren(Text) end)
    end
    if type(SELF.text) == "nil" and type(SELF.targets) ~= "nil" and type(SELF.targets.canvas) ~= "nil" then
        pcall(function()
            local ct = SELF.targets.canvas.transform
            for i = 0, (ct.childCount or 0) - 1 do
                local ch = ct.GetChild(i)
                if tostring(ch.name) == "Scroll View" then
                    local vp = ch.GetChild(0)
                    local content = vp.GetChild(0)
                    for j = 0, (content.childCount or 0) - 1 do
                        local comp = content.GetChild(j).gameObject.GetComponent(Text)
                        if type(comp) ~= "nil" then SELF.text = comp end
                    end
                end
            end
        end)
    end
    if type(SELF.text) ~= "nil" and not textFoundLogged then
        textFoundLogged = true
        pcall(function() SELF.text.color = Color(1, 1, 1, 1) end)   -- 防"颜色透明"看不见
        print("[RFAiChat][诊断] 文字组件已找到（兜底查找成功）")
    end
    return type(SELF.text) ~= "nil"
end

-- ============================================================================
-- [第2项·第2轮] 写入时刻取证：只在 updateText 真正写入时打印（不写任何属性）
--   目的：回答用户提出的四组问题
--     ① 异常文本(reason=…|target=…)从哪来 ② 写入对象的路径/实例ID/前后长度
--     ③ 同刻的渲染条件（字体/颜色/组件启用/Canvas/遮罩/可见区） ④ 是否被清空覆盖
--   全部只读：不修改 transform、不修改组件属性、不改任何功能。
-- ============================================================================
local WDIAG_EVERY = 60          -- 每 60 次写入打一条，避免刷屏
local WDIAG_COUNT = 0
local WDIAG_LASTLEN = -1

local function wdiagPath(tr)
    local parts = {}
    local n = tr
    local guard = 0
    while n ~= nil and guard < 8 do
        local nm = "?"
        pcall(function() nm = tostring(n.name) end)
        parts[#parts + 1] = nm
        pcall(function() n = n.parent end)
        guard = guard + 1
    end
    -- 反转为 由外向内
    local out = {}
    for i = #parts, 1, -1 do out[#out + 1] = parts[i] end
    return table.concat(out, "/")
end

-- 找最近的 Canvas（只读）
local function wdiagCanvasInfo(txtComp)
    local n = nil
    pcall(function() n = txtComp.transform end)
    local guard = 0
    while n ~= nil and guard < 8 do
        local c = nil
        pcall(function() c = n.gameObject.GetComponent(Canvas) end)
        if type(c) ~= "nil" then
            local s = {}
            pcall(function() s[#s + 1] = "renderMode=" .. tostring(c.renderMode) end)
            pcall(function() s[#s + 1] = "sortingOrder=" .. tostring(c.sortingOrder) end)
            pcall(function() s[#s + 1] = "overrideSorting=" .. tostring(c.overrideSorting) end)
            pcall(function() s[#s + 1] = "scaleFactor=" .. string.format("%.2f", c.scaleFactor) end)
            return "Canvas(" .. table.concat(s, ",") .. ") 位于层级 " .. wdiagPath(n)
        end
        pcall(function() n = n.parent end)
        guard = guard + 1
    end
    return "Canvas=未找到"
end

-- 写入时刻取证（由 updateText 在"写入完成之后"调用）
local function wdiagAfterWrite(textComp, beforeLen, written)
    WDIAG_COUNT = WDIAG_COUNT + 1
    local afterLen = -1
    pcall(function() afterLen = #tostring(textComp.text or "") end)
    -- 仅在第 1 次、以及每 WDIAG_EVERY 次、以及"由非空变空"时打印
    local isFirst = (WDIAG_COUNT == 1)
    local periodic = (WDIAG_COUNT % WDIAG_EVERY == 0)
    local becameEmpty = (WDIAG_LASTLEN > 0 and afterLen == 0)
    WDIAG_LASTLEN = afterLen
    if not (isFirst or periodic or becameEmpty) then return end

    print("[RFAiChat][写入取证] ---------- 第 " .. WDIAG_COUNT .. " 次写入（" ..
        (isFirst and "首次" or (becameEmpty and "变空！" or "周期采样")) .. "） ----------")
    -- ① 写入对象身份
    pcall(function()
        print(string.format("[RFAiChat][写入取证] 对象: 名称=%s 路径=%s", tostring(textComp.name), wdiagPath(textComp.transform)))
    end)
    pcall(function()
        print("[RFAiChat][写入取证] 实例ID=" .. tostring(textComp.GetInstanceID()) ..
            "  ｜ GameObject 实例ID=" .. tostring(textComp.gameObject.GetInstanceID()) ..
            "  ｜ 活跃=" .. tostring(textComp.gameObject.activeInHierarchy))
    end)
    -- ② 内容
    local head = ""
    pcall(function() head = string.sub(tostring(textComp.text or ""), 1, 60) end)
    print("[RFAiChat][写入取证] 长度：本次传入=" .. tostring(written) ..
        " 写入前=" .. tostring(beforeLen) .. " 写入后读回=" .. tostring(afterLen))
    print("[RFAiChat][写入取证] 前 60 字：" .. head)
    print("[RFAiChat][写入取证] debugMode=" .. tostring(debugMode) ..
        "  ｜ 历史行数=" .. tostring(#history) .. "  ｜ 视图偏移=" .. tostring(viewOffset) ..
        "  ｜ 输入模式=" .. tostring(inputMode))
    -- ③ 同刻渲染条件
    local rt2 = nil
    pcall(function() rt2 = textComp.transform.asRectTransform end)
    pcall(function()
        local pos, r = rt2.position, rt2.rect
        print(string.format("[RFAiChat][写入取证] 文字矩形: pos=(%.0f,%.0f) rect=(%.1fx%.1f) sizeDelta=(%.1f,%.1f)",
            pos.x, pos.y, r.width, r.height, rt2.sizeDelta.x, rt2.sizeDelta.y))
    end)
    local props = {}
    pcall(function() props[#props + 1] = "font=" .. tostring(type(textComp.font) ~= "nil") end)
    pcall(function() props[#props + 1] = "fontSize=" .. tostring(textComp.fontSize) end)
    pcall(function() props[#props + 1] = string.format("color=%.2f/%.2f/%.2f/%.2f",
        textComp.color.r, textComp.color.g, textComp.color.b, textComp.color.a) end)
    pcall(function() props[#props + 1] = "alignment=" .. tostring(textComp.alignment) end)
    pcall(function() props[#props + 1] = "preferredHeight=" .. string.format("%.0f", textComp.preferredHeight) end)
    pcall(function() props[#props + 1] = "verticalOverflow=" .. tostring(textComp.verticalOverflow) end)
    pcall(function() props[#props + 1] = "horizontalOverflow=" .. tostring(textComp.horizontalOverflow) end)
    pcall(function() props[#props + 1] = "enabled=" .. tostring(textComp.enabled) end)
    pcall(function() props[#props + 1] = "raycastTarget=" .. tostring(textComp.raycastTarget) end)
    print("[RFAiChat][写入取证] 组件属性：" .. table.concat(props, " "))
    print("[RFAiChat][写入取证] " .. wdiagCanvasInfo(textComp))
    -- 可见区（沿层级找 Mask / RectMask2D）
    do
        local n = nil
        pcall(function() n = textComp.transform end)
        local guard = 0
        local foundAny = false
        while n ~= nil and guard < 8 do
            local hasMask, hasRM = false, false
            local msgInfo = ""
            pcall(function() if type(n.gameObject.GetComponent(Mask)) ~= "nil" then
                hasMask = true
                local m = n.gameObject.GetComponent(Mask)
                msgInfo = " showMaskGraphic=" .. tostring(m.showMaskGraphic)
            end end)
            pcall(function() if type(n.gameObject.GetComponent(RectMask2D)) ~= "nil" then hasRM = true end end)
            if hasMask or hasRM then
                foundAny = true
                local rtx = nil
                pcall(function() rtx = n.asRectTransform end)
                local rstr = "?"
                pcall(function()
                    local pos, r = rtx.position, rtx.rect
                    rstr = string.format("pos=(%.0f,%.0f) rect=(%.1fx%.1f)", pos.x, pos.y, r.width, r.height)
                end)
                print(string.format("[RFAiChat][写入取证] 遮挡/裁剪层: %s 类型=%s%s ｜ %s",
                    tostring(n.name), hasMask and "Mask" or "RectMask2D", msgInfo, rstr))
            end
            pcall(function() n = n.parent end)
            guard = guard + 1
        end
        if not foundAny then
            print("[RFAiChat][写入取证] 遮挡/裁剪层: 向上 8 层内未找到 Mask / RectMask2D（不存在）")
        end
    end
    print("[RFAiChat][写入取证] --------------------------------------------------")
end

-- ============================================================================
-- [P1·第一步] 三对象只读对比取证
--   对象：① ChatText（看不到文字的）② 一个可见按钮的文字 ③ 输入框占位文字
--   原则：全部只读；读不到的属性一律打印"无法读取"，不得解释为属性异常
--   触发：第 1 次"文字非空"写入时（保证对比发生在真实内容存在时）
-- ============================================================================
local TDIAG_DONE = false

-- 读一个属性，返回 值 或 "无法读取"
local function rd(obj, key)
    if type(obj) == "nil" then return "对象为空" end
    local v = nil
    local ok = pcall(function() v = obj[key] end)
    if not ok then return "无法读取" end
    if type(v) == "nil" then return "nil（读取成功但为空）" end
    return v
end

-- 读一个可能不存在的方法返回值
local function rdCall(obj, method)
    if type(obj) == "nil" then return "对象为空" end
    local v = nil
    local ok = pcall(function() v = obj[method](obj) end)
    if not ok then return "无法读取" end
    if type(v) == "nil" then return "nil（读取成功但为空）" end
    return v
end

local function tpath(tr)
    local parts = {}
    local n = tr
    local g = 0
    while type(n) ~= "nil" and g < 8 do
        local nm = "?"
        pcall(function() nm = tostring(n.name) end)
        parts[#parts + 1] = nm
        pcall(function() n = n.parent end)
        g = g + 1
    end
    local out = {}
    for i = #parts, 1, -1 do out[#out + 1] = parts[i] end
    return table.concat(out, "/")
end

local function canvasOf(tr)
    local n, g = tr, 0
    while type(n) ~= "nil" and g < 8 do
        local c = nil
        pcall(function() c = n.gameObject.GetComponent(Canvas) end)
        if type(c) ~= "nil" then
            local s = {}
            local v = rd(c, "renderMode");      s[#s + 1] = "renderMode=" .. tostring(v)
            v = rd(c, "sortingOrder");          s[#s + 1] = "sortingOrder=" .. tostring(v)
            v = rd(c, "overrideSorting");       s[#s + 1] = "overrideSorting=" .. tostring(v)
            v = rd(c, "sortingLayerName");      s[#s + 1] = "sortingLayerName=" .. tostring(v)
            v = rd(c, "scaleFactor");           s[#s + 1] = "scaleFactor=" .. tostring(v)
            return table.concat(s, " ") .. " ｜ 层级=" .. tpath(n)
        end
        pcall(function() n = n.parent end)
        g = g + 1
    end
    return "无法读取（向上 8 层未找到 Canvas）"
end

-- 逐个 Text 出报告
local function probeText(label, t)
    print("[RFAiChat][三对象取证] ===== " .. label .. " =====")
    if type(t) == "nil" then
        print("[RFAiChat][三对象取证] 对象为空（未拿到该 Text 组件）")
        return
    end
    -- 对象身份
    local nm, path = "无法读取", "无法读取"
    pcall(function() nm = tostring(t.name) end)
    pcall(function() path = tpath(t.transform) end)
    print("[RFAiChat][三对象取证] 名称=" .. nm .. " ｜ 完整路径=" .. path)
    -- 组件启用状态（Ravenscript 一般读不到 enabled，如实标记）
    print("[RFAiChat][三对象取证] enabled=" .. tostring(rd(t, "enabled")))
    -- 字体 / 材质
    local fnull, fname = "无法读取", "无法读取"
    pcall(function()
        if type(t.font) == "nil" then fnull = "字体为 null" else
            fnull = "字体非 null"
            pcall(function() fname = tostring(t.font.name) end)
        end
    end)
    print("[RFAiChat][三对象取证] font: " .. fnull .. " ｜ font.name=" .. fname)
    local mnull, mname = "无法读取", "无法读取"
    pcall(function()
        if type(t.material) == "nil" then mnull = "材质为 null" else
            mnull = "材质非 null"
            pcall(function() mname = tostring(t.material.name) end)
        end
    end)
    print("[RFAiChat][三对象取证] material: " .. mnull .. " ｜ material.name=" .. mname)
    print("[RFAiChat][三对象取证] 材质(备选读取)= " .. tostring(rdCall(t, "GetMaterialForRendering")))
    -- 文本参数
    local cr, cg, cb, ca = "无法读取", "无法读取", "无法读取", "无法读取"
    pcall(function() cr = string.format("%.2f", t.color.r) end)
    pcall(function() cg = string.format("%.2f", t.color.g) end)
    pcall(function() cb = string.format("%.2f", t.color.b) end)
    pcall(function() ca = string.format("%.2f", t.color.a) end)
    local tlen = -1
    pcall(function() tlen = #tostring(t.text or "") end)
    print(string.format("[RFAiChat][三对象取证] fontSize=%s ｜ color=%s/%s/%s/%s ｜ alignment=%s ｜ 文字长度=%s",
        tostring(rd(t, "fontSize")), cr, cg, cb, ca, tostring(rd(t, "alignment")), tostring(tlen)))
    print("[RFAiChat][三对象取证] preferredHeight=" .. tostring(rd(t, "preferredHeight")) ..
        " ｜ preferredWidth=" .. tostring(rd(t, "preferredWidth")) ..
        " ｜ horizontalOverflow=" .. tostring(rd(t, "horizontalOverflow")) ..
        " ｜ verticalOverflow=" .. tostring(rd(t, "verticalOverflow")) ..
        " ｜ richText=" .. tostring(rd(t, "supportRichText")))
    local head = ""
    pcall(function() head = string.sub(tostring(t.text or ""), 1, 40) end)
    print("[RFAiChat][三对象取证] 前 40 字=" .. head)
    -- RectTransform
    local rt = nil
    pcall(function() rt = t.transform.asRectTransform end)
    if type(rt) == "nil" then
        pcall(function() rt = t.rectTransform end)
    end
    if type(rt) ~= "nil" then
        pcall(function()
            local pos, r = rt.position, rt.rect
            print(string.format("[RFAiChat][三对象取证] Rect: pos=(%.1f,%.1f) rect=(%.1fx%.1f) sizeDelta=(%.1f,%.1f) pivot=(%.2f,%.2f)",
                pos.x, pos.y, r.width, r.height, rt.sizeDelta.x, rt.sizeDelta.y, rt.pivot.x, rt.pivot.y))
        end)
    else
        print("[RFAiChat][三对象取证] Rect: 无法读取（拿不到 RectTransform）")
    end
    -- 所属画布
    local tr = nil
    pcall(function() tr = t.transform end)
    print("[RFAiChat][三对象取证] 画布: " .. canvasOf(tr))
    print("[RFAiChat][三对象取证] 组件是否启用(activeInHierarchy)=" ..
        tostring((function() local v="无法读取"; pcall(function() v=t.gameObject.activeInHierarchy end); return v end)()))
end

-- 交集计算：文字矩形 与 最近的 Mask/RectMask2D 矩形
local function probeIntersection(textComp, maskGO, maskName)
    if type(textComp) == "nil" or type(maskGO) == "nil" then
        print("[RFAiChat][三对象取证] ChatText∩Mask 交集: 无法计算（对象为空）")
        return
    end
    local trt, mrt = nil, nil
    pcall(function() trt = textComp.transform.asRectTransform end)
    pcall(function() mrt = maskGO.transform.asRectTransform end)
    if type(trt) == "nil" or type(mrt) == "nil" then
        print("[RFAiChat][三对象取证] ChatText∩Mask 交集: 无法计算（拿不到 RectTransform）")
        return
    end
    pcall(function()
        local tp, tr2 = trt.position, trt.rect
        local mp, mr = mrt.position, mrt.rect
        local tTop, tBot = tp.y + tr2.height * (1 - trt.pivot.y), tp.y - tr2.height * trt.pivot.y
        local mTop, mBot = mp.y + mr.height * (1 - mrt.pivot.y), mp.y - mr.height * mrt.pivot.y
        local top, bot = math.min(tTop, mTop), math.max(tBot, mBot)
        print(string.format("[RFAiChat][三对象取证] 可见性几何：文字 y=[%.1f, %.1f] ｜ %s y=[%.1f, %.1f] ｜ 交集高度=%.1f %s",
            tBot, tTop, maskName, mBot, mTop, top - bot, (top - bot > 0 and "(>0)" or "(<=0！)")))
    end)
end

local function tdiagRun()
    print("[RFAiChat][三对象取证] ################ 开始（只读对比） ################")
    -- ① ChatText
    probeText("① ChatText（看不到文字的）", SELF.text)
    -- ② 可见按钮的文字：取发送按钮
    local btnTxt = nil
    local btnName = "无法读取"
    pcall(function()
        local b = SELF.targets.btn_send
        if type(b) ~= "nil" then
            btnName = tostring(b.name)
            local comps = nil
            pcall(function() comps = b.GetComponentsInChildren(Text) end)
            if type(comps) ~= "nil" then
                pcall(function() btnTxt = comps[1] end)
            end
        end
    end)
    probeText("② 可见按钮文字（" .. btnName .. " 的子 Text）", btnTxt)
    -- ③ 输入框占位文字
    local phTxt = nil
    pcall(function()
        local inf = nil
        pcall(function() inf = SELF.targets.input.GetComponent(InputField) end)
        if type(inf) ~= "nil" then
            pcall(function() phTxt = inf.placeholder end)
        end
    end)
    probeText("③ 输入框占位文字（InputField.placeholder）", phTxt)
    -- ④ 交集（找 ChatText 向上的 Mask / RectMask2D）
    do
        local n = nil
        pcall(function() n = SELF.text.transform end)
        local g, found = 0, false
        while type(n) ~= "nil" and g < 8 do
            local hasMask = false
            pcall(function() if type(n.gameObject.GetComponent(Mask)) ~= "nil" then hasMask = true end end)
            if hasMask then
                found = true
                local nm2 = "?"
                pcall(function() nm2 = tostring(n.name) end)
                probeIntersection(SELF.text, n.gameObject, nm2)
                break
            end
            pcall(function() n = n.parent end)
            g = g + 1
        end
        if not found then
            print("[RFAiChat][三对象取证] 未找到 Mask/RectMask2D（不存在）⇒ 无裁剪层")
        end
    end
    print("[RFAiChat][三对象取证] ################ 结束 ################")
end

-- 触发：第一次"文字非空"时执行一次
local function tdiagTick()
    if TDIAG_DONE then return end
    if type(SELF) == "nil" or type(SELF.text) == "nil" then return end
    local len = -1
    pcall(function() len = #tostring(SELF.text.text or "") end)
    if len and len > 0 then
        TDIAG_DONE = true
        pcall(tdiagRun)
    end
end

-- ============================================================================
-- [P1·第二步] 只读：逐级父链 + 屏幕坐标换算 + 占位文字枚举
--   本轮不做任何赋值；读不到一律打印"无法读取"；坐标换算优先用 Unity 原生接口，
--   若接口不可用则明确报告"无法换算"，绝不用 lossyScale 乘除冒充换算结果。
-- ============================================================================
local C2_DONE = false

local function r2(obj, key)
    if type(obj) == "nil" then return "对象为空" end
    local v, ok = nil, false
    ok = pcall(function() v = obj[key] end)
    if not ok then return "无法读取" end
    if type(v) == "nil" then return "nil（读取成功但为空）" end
    return tostring(v)
end

local function path2(tr)
    local parts, n, g = {}, tr, 0
    while type(n) ~= "nil" and g < 10 do
        local nm = "?"
        pcall(function() nm = tostring(n.name) end)
        parts[#parts + 1] = nm
        pcall(function() n = n.parent end)
        g = g + 1
    end
    local out = {}
    for i = #parts, 1, -1 do out[#out + 1] = parts[i] end
    return table.concat(out, "/")
end

local function vecStr(v)
    if type(v) == "nil" then return "无法读取" end
    local s = nil
    pcall(function() s = string.format("(%.2f,%.2f)", v.x, v.y) end)
    if s == nil then pcall(function() s = string.format("(%.2f,%.2f,%.2f)", v.x, v.y, v.z) end) end
    if s == nil then return "无法读取" end
    return s
end

-- 逐级打印一个 RectTransform 的全部关键字段
local function level2(label, tr)
    print("[RFAiChat][父链取证] ----- " .. label .. " -----")
    if type(tr) == "nil" then
        print("[RFAiChat][父链取证] 对象为空")
        return
    end
    print("[RFAiChat][父链取证] 完整路径=" .. path2(tr))
    -- Transform 基础量
    print("[RFAiChat][父链取证] localPosition=" .. vecStr((function() local v=nil; pcall(function() v=tr.localPosition end); return v end)()))
    print("[RFAiChat][父链取证] localScale=" .. vecStr((function() local v=nil; pcall(function() v=tr.localScale end); return v end)()))
    print("[RFAiChat][父链取证] lossyScale=" .. vecStr((function() local v=nil; pcall(function() v=tr.lossyScale end); return v end)()))
    print("[RFAiChat][父链取证] position(世界)=" .. vecStr((function() local v=nil; pcall(function() v=tr.position end); return v end)()))
    -- 父子关系
    local pname, gname = "无法读取", "无法读取"
    pcall(function() pname = tostring(tr.parent.name) end)
    print("[RFAiChat][父链取证] parent=" .. pname)
    -- RectTransform 明细
    local rt = nil
    pcall(function() rt = tr.asRectTransform end)
    if type(rt) == "nil" then
        print("[RFAiChat][父链取证] RectTransform=无法读取（该层不是 RectTransform）")
        return
    end
    print("[RFAiChat][父链取证] pivot=" .. vecStr((function() local v=nil; pcall(function() v=rt.pivot end); return v end)()))
    print("[RFAiChat][父链取证] anchorMin=" .. vecStr((function() local v=nil; pcall(function() v=rt.anchorMin end); return v end)()))
    print("[RFAiChat][父链取证] anchorMax=" .. vecStr((function() local v=nil; pcall(function() v=rt.anchorMax end); return v end)()))
    print("[RFAiChat][父链取证] anchoredPosition=" .. vecStr((function() local v=nil; pcall(function() v=rt.anchoredPosition end); return v end)()))
    print("[RFAiChat][父链取证] sizeDelta=" .. vecStr((function() local v=nil; pcall(function() v=rt.sizeDelta end); return v end)()))
    pcall(function()
        local r = rt.rect
        print(string.format("[RFAiChat][父链取证] rect: x=%.1f y=%.1f w=%.1f h=%.1f", r.x, r.y, r.width, r.height))
    end)
    -- 四角（世界坐标，由 position/pivot/rect 精确推出；不使用 lossyScale 乘除）
    pcall(function()
        local pos, r = rt.position, rt.rect
        local left  = pos.x + r.x
        local top   = pos.y + r.y + r.height
        local right = pos.x + r.x + r.width
        local bottom= pos.y + r.y
        print(string.format("[RFAiChat][父链取证] 世界矩形(四角): 左=%.1f 右=%.1f 下=%.1f 上=%.1f", left, right, bottom, top))
    end)
    -- Canvas（若该层就是画布）
    local c = nil
    pcall(function() c = tr.gameObject.GetComponent(Canvas) end)
    if type(c) ~= "nil" then
        print("[RFAiChat][父链取证] Canvas: renderMode=" .. r2(c, "renderMode") ..
            " ｜ scaleFactor=" .. r2(c, "scaleFactor") ..
            " ｜ sortingOrder=" .. r2(c, "sortingOrder") ..
            " ｜ overrideSorting=" .. r2(c, "overrideSorting") ..
            " ｜ sortingLayerName=" .. r2(c, "sortingLayerName") ..
            " ｜ pixelPerfect=" .. r2(c, "pixelPerfect"))
    end
end

-- 屏幕坐标换算：优先 Unity 原生接口
local function screenOf(label, rt)
    if type(rt) == "nil" then
        print("[RFAiChat][坐标换算] " .. label .. ": 对象为空")
        return
    end
    local pos = nil
    pcall(function() pos = rt.position end)
    if type(pos) == "nil" then
        print("[RFAiChat][坐标换算] " .. label .. ": 无法读取 position")
        return
    end
    -- ① Camera.main.WorldToScreenPoint
    local done = false
    local cam = nil
    pcall(function() cam = Camera.main end)
    if type(cam) ~= "nil" then
        pcall(function()
            local sp = cam.WorldToScreenPoint(pos)
            print(string.format("[RFAiChat][坐标换算] %s: Camera.main.WorldToScreenPoint(世界%.1f,%.1f) = 屏幕(%.1f,%.1f,%.1f)  [%s]",
                label, pos.x, pos.y, sp.x, sp.y, sp.z, tostring(cam.name)))
            done = true
        end)
    end
    if not done then
        print("[RFAiChat][坐标换算] " .. label .. ": Camera.main / WorldToScreenPoint 无法读取 ⇒ 未换算（不伪造）")
    end
    -- ② RectTransformUtility.WorldToScreenPoint（若 Ravenscript 暴露）
    local ok2 = pcall(function()
        local sp2 = RectTransformUtility.WorldToScreenPoint(nil, pos)
        print(string.format("[RFAiChat][坐标换算] %s: RectTransformUtility.WorldToScreenPoint = (%.1f,%.1f)", label, sp2.x, sp2.y))
    end)
    if not ok2 then
        print("[RFAiChat][坐标换算] " .. label .. ": RectTransformUtility 无法读取（Ravenscript 未暴露）")
    end
    print(string.format("[RFAiChat][坐标换算] %s: 世界坐标 position=(%.1f,%.1f)（若画布为 ScreenSpaceOverlay，Unity 下世界坐标即屏幕像素坐标）", label, pos.x, pos.y))
end

-- 枚举某个对象下所有 Text
local function enumTexts(label, go)
    print("[RFAiChat][占位枚举] ----- " .. label .. " -----")
    if type(go) == "nil" then
        print("[RFAiChat][占位枚举] 对象为空")
        return
    end
    print("[RFAiChat][占位枚举] 对象路径=" .. (function() local v="无法读取"; pcall(function() v=path2(go.transform) end); return v end)())
    local arr = nil
    local ok = pcall(function() arr = go.GetComponentsInChildren(Text) end)
    if not ok or type(arr) == "nil" then
        print("[RFAiChat][占位枚举] GetComponentsInChildren(Text) 无法读取 ⇒ 未捕获")
        return
    end
    local n = 0
    pcall(function() n = #arr end)
    print("[RFAiChat][占位枚举] Text 组件数量=" .. tostring(n))
    for i = 1, n do
        local t = nil
        pcall(function() t = arr[i] end)
        if type(t) ~= "nil" then
            local pp, tx, fs, head = "无法读取", "无法读取", "无法读取", ""
            pcall(function() pp = path2(t.transform) end)
            pcall(function() tx = #tostring(t.text or "") end)
            pcall(function() fs = tostring(t.fontSize) end)
            pcall(function() head = string.sub(tostring(t.text or ""), 1, 30) end)
            local rects = "无法读取"
            pcall(function()
                local r = (t.transform.asRectTransform or t.rectTransform).rect
                rects = string.format("%.0fx%.0f", r.width, r.height)
            end)
            print(string.format("[RFAiChat][占位枚举] [%d] 路径=%s 文字长度=%s 字号=%s 矩形=%s 内容=%s",
                i, pp, tostring(tx), fs, rects, head))
        end
    end
end

local function c2Run()
    print("[RFAiChat][父链取证] ################ 开始（只读） ################")
    -- 一、完整父级链（由内向外逐级）
    local tr = nil
    pcall(function() tr = SELF.text.transform end)
    local g = 0
    while type(tr) ~= "nil" and g < 6 do
        local nm = "?"
        pcall(function() nm = tostring(tr.name) end)
        level2("第 " .. g .. " 级：" .. nm, tr)
        pcall(function() tr = tr.parent end)
        g = g + 1
    end
    -- 二、坐标换算（ChatText / ChatBox / Viewport）
    print("[RFAiChat][坐标换算] ################ 开始 ################")
    local function rtOfName(name)
        local n = nil
        pcall(function() n = SELF.text.transform end)
        local gg = 0
        while type(n) ~= "nil" and gg < 8 do
            local nm = "?"
            pcall(function() nm = tostring(n.name) end)
            if nm == name then
                local rt = nil
                pcall(function() rt = n.asRectTransform end)
                return rt
            end
            pcall(function() n = n.parent end)
            gg = gg + 1
        end
        return nil
    end
    local trt = nil
    pcall(function() trt = SELF.text.transform.asRectTransform end)
    screenOf("ChatText", trt)
    screenOf("Content", rtOfName("Content"))
    screenOf("Viewport", rtOfName("Viewport"))
    screenOf("ChatBox", rtOfName("ChatBox"))
    screenOf("AIChatCanvas", rtOfName("AIChatCanvas(Clone)"))
    print("[RFAiChat][坐标换算] ################ 结束 ################")
    -- 三、输入框子对象里的所有 Text
    local inputGO = nil
    pcall(function() inputGO = SELF.targets.input end)
    if type(inputGO) == "nil" then
        print("[RFAiChat][占位枚举] SELF.targets.input 为空 ⇒ 未捕获")
    else
        enumTexts("输入框对象及其子对象", inputGO)
    end
    print("[RFAiChat][父链取证] ################ 结束（只读） ################")
end

local function c2Tick()
    if C2_DONE then return end
    if type(SELF) == "nil" or type(SELF.text) == "nil" then return end
    local len = -1
    pcall(function() len = #tostring(SELF.text.text or "") end)
    if len and len > 0 then
        C2_DONE = true
        pcall(c2Run)
    end
end

local function updateText(s, rows)
    lastRows = rows or 1
    local wBefore = -1
    pcall(function() wBefore = #tostring(SELF.text.text or "") end)
    if not ensureText() then
        if lastDiagTxt ~= "NIL" then
            lastDiagTxt = "NIL"
            print("[RFAiChat][诊断] 找不到文字组件：SELF.text = nil → 什么都不显示（需修目标绑定）")
        end
        return
    end
    local ok, err = pcall(function() SELF.text.text = s end)
    pcall(wdiagAfterWrite, SELF.text, wBefore, #tostring(s))   -- [第2项·第2轮] 写入时刻取证（只读）
    pcall(tdiagTick)                                            -- [P1·第一步] 三对象对比取证（只读）
    pcall(c2Tick)                                               -- [P1·第二步] 父链+坐标换算取证（只读）
    if lastDiagTxt ~= s then
        lastDiagTxt = s
        local got, txt, pos, sz, sc = 0, "", "?", "?", "?"
        pcall(function() txt = tostring(SELF.text.text or "") end)
        pcall(function()
            local r = SELF.text.rectTransform
            pos = string.format("%.0f,%.0f", r.position.x, r.position.y)
            sz = string.format("%.0fx%.0f", r.sizeDelta.x, r.sizeDelta.y)
            sc = string.format("%.2f", r.localScale.x)
        end)
        local col, fnt, al = "?", "?", "?"
        pcall(function() col = string.format("%.2f/%.2f/%.2f/%.2f",
            SELF.text.color.r, SELF.text.color.g, SELF.text.color.b, SELF.text.color.a) end)
        pcall(function() fnt = tostring(type(SELF.text.font) ~= "nil") end)
        pcall(function() al = tostring(SELF.text.alignment) end)
        print(string.format("[RFAiChat][诊断] 写入 %d 字 ok=%s 读回=%d 位置=%s 尺寸=%s 缩放=%s 颜色=%s 字体=%s 对齐=%s 首20字=%s",
            #tostring(s), tostring(ok), #txt, pos, sz, sc, col, fnt, al, string.sub(txt, 1, 24)))
    end
end

local function renderView()
    -- ★ 第92轮：Plan B（C# OverlayChat）负责实际显示时，Lua 不再写官方 UI 文本/布局（避免双控制打架）
    if CFG.planB == true and type(SELF) ~= "nil" and SELF.useUnityUI == true then return end
    if type(SELF) ~= "nil" and SELF.useUnityUI then
        -- 官方 UI：布局/滚动交给 Unity 的 ScrollRect，这里只把文字写进去（不做任何摆位）
        ensureText()
    end
    if not ensureText() then
        if not textMissingLogged then
            textMissingLogged = true
            print("[RFAiChat][诊断] 文字组件仍未找到：Canvas/Scroll View/Viewport/Content 结构不符合预期")
        end
        return
    end
    -- ★ 官方 UI 的文本高度必须自己算：
    --   Unity 导出器打出来的 Content 是固定高度（如 400），文字一多就溢出到视口外，
    --   ScrollRect 也就没有可滚动区域 —— 表现就是"文字跑到框外/只看见一半"。
    --   取父级链要小心：能拿到的是 Transform（它有 asRectTransform），不是 GameObject。
    local uiScroll, uiContent, uiViewport, uiText = nil, nil, nil, nil
    if type(SELF) ~= "nil" and SELF.useUnityUI then
        pcall(function() SELF.text.supportRichText = true end)
        uiText = SELF.text
        pcall(function() uiContent = uiText.rectTransform.parent.asRectTransform end)
        if type(uiContent) ~= "nil" then
            pcall(function() uiViewport = uiContent.parent.asRectTransform end)
        end
        if type(uiContent) == "nil" then
            print("[RFAiChat][诊断-内容] 取不到 Content（文字框的父级）→ Content 高度自适应不生效")
        end
    end
    local out = {}
    local lineH = lineHeight()
    -- 三段：聊天区（可放 chatRows 行）｜空隙（gapRows 行空白）｜输入框（3 行 + 调试时 1 行功能行）
    local chatRows = ROWS_CHAT
    local gapRows = ROWS_GAP
    local chatBudget = chatRows
    -- 官方 UI：真正可滚动，不再需要"只留 N 行"的截断（否则历史一多就只能看到最后几行）
    if type(SELF) ~= "nil" and SELF.useUnityUI == true then
        chatBudget = math.max(chatRows, CFG.historyMax)
    end

    local picked, used = {}, 0
    if debugMode then
        local n = #history
        for i = n, 1, -1 do
            local txt = "<b>" .. tostring(i) .. "</b> " .. tostring(historyMeta[i] or "(无调试信息)")
            local c = visualRows(txt)
            if used + c > chatBudget then break end
            table.insert(picked, 1, txt)
            used = used + c
        end
        if #picked == 0 then picked = { "<b>[调试窗口]</b> 共 " .. tostring(n) .. " 条" }; used = 1 end
    else
        local n = #history
        local last = n - viewOffset
        if last > n then last = n end
        if last < 1 then last = n end
        for i = last, 1, -1 do
            local c = visualRows(history[i])
            if used + c > chatBudget then break end
            table.insert(picked, 1, history[i])
            used = used + c
        end
        if #picked == 0 then
            picked = { "<i>（暂无台词，士兵正在作战…）</i>" }
            used = 1
        end
    end
    for i = 1, #picked do out[#out + 1] = picked[i] end

    -- 空行补齐：消息少时内容贴顶往下排；补齐到聊天区行数
    -- 官方 UI 下【不补空行】：真正的滚动由 ScrollRect 负责，补空行只会把内容撑得没必要的长
    local padBlank = (type(SELF) == "nil" or SELF.useUnityUI ~= true)
    if padBlank then
        for i = 1, math.max(0, chatBudget - used) do out[#out + 1] = " " end
        for i = 1, gapRows do out[#out + 1] = " " end
    end

    if debugMode and padBlank then
        out[#out + 1] = "<b>[战报] [导出] [隐藏] [上翻] [下翻]</b>"
    end

    -- ===== 按钮文字与输入框文字：交给独立 UI 图层（不再进聊天文本，永不被挤走）=====
    -- 官方 UI 下按钮是【真 Unity 按钮 + 真文字】，标签由 Unity 决定，这里不碰
    if padBlank then
        local names = debugMode and { "重置", "导出", "测试", "返回" } or { "发送", "调试", "战报", "返回" }
        local keys  = { debugMode and "reset" or "send", debugMode and "export" or "debug", debugMode and "test" or "report", debugMode and "closedebug" or "back" }
        for i = 1, 4 do
            local t = UI_TXT[i - 1]
            if type(t) ~= "nil" then
                pcall(function() t.text = "<b>" .. names[i] .. "</b>" end)
                if pressedBtn == keys[i] then
                    pcall(function() t.color = Color(1, 0.82, 0.29, 1) end)
                elseif hoverBtn == keys[i] then
                    pcall(function() t.color = Color(1, 1, 1, 1) end)
                else
                    pcall(function() t.color = Color(0.78, 0.82, 0.86, 1) end)
                end
            end
        end
        local tIn = UI_TXT[4]
        if type(tIn) ~= "nil" then
            if inputMode then
                local comp = ""
                pcall(function() comp = Input.compositionString end)
                pcall(function() tIn.text = "> " .. tostring(inputText) .. tostring(comp or "") .. "_" end)
                pcall(function() tIn.color = Color(1, 1, 1, 1) end)
            else
                pcall(function() tIn.text = "<color=#9A9A9A>点击这里输入文字…</color>" end)
            end
        end
    end

    updateText(table.concat(out, "\n"), #out)
    -- ===== 官方 UI：文字写完后按【真实文字高度】撑开 Content，并滚到最新一行 =====
    -- 背景：Unity 导出器打出来的 Content 高度是写死的（如 400），文字一多就溢出视口、
    --       ScrollRect 也没有可滚动区域 —— 表现就是"文字跑到框外/只看见一半"。
    if type(uiContent) ~= "nil" then
        pcall(function()
            local vh = 0
            local okv, vr = pcall(function() return uiViewport.rect.height end)
            if okv and type(vr) ~= "nil" then vh = vr end
            local th = 0
            local okt, ph = pcall(function() return uiText.preferredHeight end)
            if okt and type(ph) ~= "nil" then th = ph end
            local target = math.max(vh, th + 8)
            -- 注意：不要再设 offsetMin/offsetMax —— 它们会覆盖 sizeDelta 与 anchoredPosition，
            -- 正是这个原因导致上一版"改了等于没改"。
            uiContent.anchorMin = Vector2(0, 1)
            uiContent.anchorMax = Vector2(1, 1)
            uiContent.pivot = Vector2(0.5, 1)
            uiContent.sizeDelta = Vector2(-12, target)
            uiContent.anchoredPosition = Vector2(0, -4)
            local trt = uiText.rectTransform
            trt.anchorMin = Vector2(0, 1)
            trt.anchorMax = Vector2(1, 1)
            trt.pivot = Vector2(0.5, 1)
            trt.sizeDelta = Vector2(-12, target)
            trt.anchoredPosition = Vector2(0, 0)
            -- 滚到最新（Content 比视口高时才真的滚动；否则等于贴顶）
            if type(SELF) ~= "nil" and type(SELF.targets) ~= "nil" and type(SELF.targets.scroll) ~= "nil" then
                pcall(function() SELF.targets.scroll.verticalNormalizedPosition = 0 end)
            end
        end)
    end
    -- 高度固定 = 整块（顶部锚定 → 输入框那几行永远在底部）
    if type(SELF) ~= "nil" and type(SELF.text) ~= "nil" then
        pcall(function()
-- （已移除：运行时不改文字框高度，避免最后一行被截断）
        end)
    end
end

-- 按设置调整字号/行距/尺寸（全部运行时生效，不需要重新打包）
local function tuneUI()
    if type(SELF) == "nil" or type(SELF.text) == "nil" then return end
    local t = SELF.text

    -- 字号：用绝对值（设置里可调，默认 16；原版是 14）
    local base = nil
    pcall(function() base = t.fontSize end)
    if type(base) == "nil" or base <= 0 then base = UI_FONT end
    UI_FONT = base
    local target = CONF.fontSize or 16
    if target <= 0 then target = base end
    pcall(function() t.fontSize = target end)
    pcall(function() t.lineSpacing = 1.0 end)
    pcall(function() t.resizeTextForBestFit = false end)
    pcall(function() t.supportRichText = true end)
    -- 关键：文字不能吃鼠标点击，否则会挡住装备选择界面
    pcall(function() t.raycastTarget = false end)

    -- 尺寸：宽度略收窄、高度往上下拉长。
    -- ⚠️ 绝对不要改 anchoredPosition：位置由模组预制体决定，上一版在这里动过，
    --    结果聊天框整个跑偏（实测截图确认），所以这里只改尺寸。
    local okChain, errChain = pcall(function()
        local rt = t.rectTransform

        -- ① 沿父级链找"聊天容器"：优先找名字里含 scroll 的那一层。
        --    这样无论是 Chat Feed 载体、还是你在 Unity 里自建的预制体，都能找到（名字略有差别也行）。
        local scroll, node = nil, rt
        for depth = 1, 6 do
            local okp, p = pcall(function() return node.parent.asRectTransform end)
            if not okp or type(p) == "nil" then break end
            node = p
            local nm = nil
            pcall(function() nm = node.name end)
            if type(nm) ~= "nil" and string.find(string.lower(tostring(nm)), "scroll") ~= nil then
                scroll = node
                break
            end
        end
        if type(scroll) == "nil" then
            -- ② 找不到就按标准结构退到第 3 层祖先；再不行就用文字框自己（保证不报错、仍能点按钮）
            local okp, p = pcall(function()
                return rt.parent.asRectTransform.parent.asRectTransform.parent.asRectTransform
            end)
            if okp and type(p) ~= "nil" then scroll = p else scroll = rt end
        end
        SCROLL_RT = scroll

        local content, viewport = nil, nil
        pcall(function()
            content = rt.parent.asRectTransform
            viewport = content.parent.asRectTransform
        end)

        BOX_W = scroll.sizeDelta.x * (CONF.boxWidth or 0.9)

        local w = scroll.sizeDelta.x * (CONF.boxWidth or 0.9)
        local hTotal = scroll.sizeDelta.y * (CONF.boxHeight or 2.0)

        INPUT_H = lineHeight() + 12      -- 独立输入框（框外，红框区）
        GAP = math.max(8, lineHeight() * 0.5)
        ROWS_BTN = 1                     -- 按钮行：在聊天框内最底部（蓝框区）
        local hChat = hTotal - INPUT_H - GAP          -- 聊天区高度（在整块内部）

        -- ★ 布局原则（踩坑后的定稿）：整个部件仍是一个矩形，**所有文字都在矩形内部**，
        --   只用"两块底色 + 中间一条透明空隙"做出两个框的观感。
        --   这样做完全不依赖遮罩区大小 —— 之前把输入框放到矩形外面，一旦遮罩没跟着延长就被裁掉。
        scroll.sizeDelta = Vector2(w, hTotal)
        -- ★ 位置：画面【左中】（锚到左侧垂直中点），左边距可用设置里的"左边距"调
        pcall(function()
            scroll.anchorMin = Vector2(0, 0.5)
            scroll.anchorMax = Vector2(0, 0.5)
            scroll.pivot = Vector2(0.5, 0.5)
            scroll.anchoredPosition = Vector2(12 + (CONF.leftMargin or 0) + w / 2, 0)
        end)
        -- ★★ 唯一几何基准：读 Scroll View 的【真实矩形】。
        -- 之前用 sizeDelta 推算，而锚点是拉伸的 → 推算值(184) 与屏上实测(≈309) 不符，
        -- 导致"文字只占半个框、白线偏、命中算不中"这类系统性错位。
        local realW, realH = w, hTotal
        pcall(function()
            local r = scroll.rect
            if type(r) ~= "nil" and type(r.height) ~= "nil" and r.height > 10 then
                realH = r.height
                realW = r.width or w
            end
        end)
        GEOM_H = realH
        GEOM_W = realW
        -- ★ 唯一换算：画布单位 × SCALE_PX = 屏幕像素；框左上角在 (BOX_LEFT_PX, BOX_TOP_PX)
        do
            local px, py, sxs = 0, 0, 1
            pcall(function() px = scroll.position.x; py = scroll.position.y; sxs = scroll.localScale.x end)
            SCALE_PX = (CANVAS_SCALE or 1) * (sxs or 1)
            BOX_LEFT_PX = px - (realW * SCALE_PX) / 2
            BOX_TOP_PX = py + (realH * SCALE_PX) / 2
        end
        -- 遮罩/内容区按"真实高度"重设：整块 + 下方独立输入框，都要能看到
        local maskH2 = realH + GAP + INPUT_H
        if type(viewport) ~= "nil" then
            pcall(function()
                viewport.anchorMin = Vector2(0, 1); viewport.anchorMax = Vector2(1, 1)
                viewport.pivot = Vector2(0.5, 1)
                viewport.sizeDelta = Vector2(-12, maskH2)
                viewport.anchoredPosition = Vector2(0, -6)
            end)
        end
        if type(content) ~= "nil" then
            pcall(function()
                content.anchorMin = Vector2(0, 1); content.anchorMax = Vector2(1, 1)
                content.pivot = Vector2(0.5, 1)
                content.sizeDelta = Vector2(-12, maskH2 - 12)
                content.anchoredPosition = Vector2(0, -6)
            end)
        end
        hChat = realH - INPUT_H - GAP
        ROWS_CHAT = math.max(2, math.floor((hChat - 8) / lineHeight()))   -- 第20轮 A：去掉硬编码 -3
        local boxX = scroll.anchoredPosition.x
        local boxCY = scroll.anchoredPosition.y

        -- 行数（文字排版与底板定位共用）
        -- 多留 2 行余量：万一某行换行比估算多，也不会把输入框顶出去
        ROWS_CHAT = math.max(2, math.floor((hChat - 8) / lineHeight()) - 1 - ROWS_BTN)
        ROWS_GAP = math.max(1, math.ceil(GAP / lineHeight()))

        -- 遮罩/内容区：保持"在聊天框内缩 6"，文字全部落在里面 → 永不被裁
        if type(viewport) ~= "nil" then
            viewport.anchorMin = Vector2(0, 0)
            viewport.anchorMax = Vector2(1, 1)
            viewport.pivot = Vector2(0.5, 0.5)
            viewport.sizeDelta = Vector2(-12, -12)
            viewport.anchoredPosition = Vector2(0, 0)
        end
        -- 遮罩区必须延长到"独立输入框"那一行，否则输入框里的字会被裁掉
        local maskH = hChat + GAP + INPUT_H
        if type(viewport) ~= "nil" then
            viewport.anchorMin = Vector2(0, 1)
            viewport.anchorMax = Vector2(1, 1)
            viewport.pivot = Vector2(0.5, 1)
            viewport.sizeDelta = Vector2(-12, maskH)
            viewport.anchoredPosition = Vector2(0, -6)
        end
        if type(content) ~= "nil" then
            content.anchorMin = Vector2(0, 1)
            content.anchorMax = Vector2(1, 1)
            content.pivot = Vector2(0.5, 1)
            content.sizeDelta = Vector2(-12, maskH - 12)
            content.anchoredPosition = Vector2(0, -6)
        end

        -- 文本：顶部锚定（消息从上往下排，满了顶掉最旧的）
        rt.anchorMin = Vector2(0.5, 1)
        rt.anchorMax = Vector2(0.5, 1)
        rt.pivot = Vector2(0.5, 1)
        rt.anchoredPosition = Vector2(0, -4)
        rt.sizeDelta = Vector2(math.max(40, realW - 24), math.max(20, realH - 8))   -- 铺满整块

        local chatTop = boxCY + hTotal / 2                       -- 整块顶边
        local rowH = lineHeight()
        local rowsTop = chatTop - TEXT_TOP_PAD                    -- 第一行聊天文字的顶边
        local chatRowsH = ROWS_CHAT * rowH                        -- 聊天文字占的高度
        local inputRowsN = 3 + (debugMode and 1 or 0)
        local inputRowsH = inputRowsN * rowH
        -- ★ 两块底色都按【行数】精确摆位，中间那条空隙（ROWS_GAP 行）谁都不盖 → 真正分得开
        local chatBgH = chatRowsH + 8
        local chatBgTop = rowsTop + 4
        local inputTop = rowsTop - chatRowsH - ROWS_GAP * rowH    -- 输入行顶边
        local inputBgH = inputRowsH + 8
        local inputBgTop = inputTop + 4
        local frameTop = inputTop                                  -- 兼容旧变量

        -- ============ 两个"真框"：白线边框 + 半透明底 ============
        -- 图层（从后到前）：
        --   Panel(白)       → 聊天框白线（比聊天底大 2px，露出白边）
        --   Scrollbar(黑)   → 聊天框底
        --   Viewport(白)    → 输入框白线
        --   Handle(黑)      → 输入框底
        --   Text            → 文字（永不越界：溢出方式=截断）
        local ring = 2
        local chatRingTop = chatTop + ring
        local chatRingH = hChat + ring * 2
        local inRingTop = inputBgTop + ring
        local inRingH = inputBgH + ring * 2

        local canvasRT = scroll.parent.asRectTransform
        local cnt = 0
        pcall(function() cnt = canvasRT.childCount end)

        -- ① 聊天框白线：Canvas 级 Panel（在最底层 → 只露一圈白边）
        for i = 0, (cnt or 0) - 1 do
            local child = nil
            pcall(function() child = canvasRT.GetChild(i) end)
            if type(child) ~= "nil" then
                local cname = nil
                pcall(function() cname = child.name end)
                if cname == "Panel" then
                    local prt = child.asRectTransform
                    pcall(function()
                        prt.anchorMin = Vector2(0, 0.5)
                        prt.anchorMax = Vector2(0, 0.5)
                        prt.pivot = Vector2(0.5, 0.5)
                        prt.sizeDelta = Vector2(w + ring * 2, chatRingH)
                        prt.anchoredPosition = Vector2(boxX, chatRingTop - chatRingH / 2)
                    end)
                    pcall(function()
                        local img = child.gameObject.GetComponent(Image)
                        if type(img) ~= "nil" then
                            img.color = Color(0, 0, 0, 0)      -- 先不拿它当白底（会发灰）
                            img.raycastTarget = false
                        end
                    end)
                end
            end
        end

        -- ② 聊天底 / 输入底 / 输入白线：三块现成图层
        pcall(function()
            local sc2 = scroll.childCount or 0
            for i = 0, sc2 - 1 do
                local ch = nil
                pcall(function() ch = scroll.GetChild(i) end)
                if type(ch) ~= "nil" then
                    local nm = nil
                    pcall(function() nm = ch.name end)
                    nm = tostring(nm or "")
                    if string.find(string.lower(nm), "scrollbar") ~= nil then
                        local prt = ch.asRectTransform
                        -- 滚动条底板 → 黑色"聊天框底"
                        pcall(function()
                            prt.anchorMin = Vector2(0.5, 1); prt.anchorMax = Vector2(0.5, 1)
                            prt.pivot = Vector2(0.5, 1)
                            prt.sizeDelta = Vector2(w, inputBgH)
                            prt.anchoredPosition = Vector2(0, inputBgTop - chatTop)
                        end)
                        pcall(function()
                            local img = ch.gameObject.GetComponent(Image)
                            if type(img) ~= "nil" then
                                img.color = Color(0, 0, 0, 0.55)      -- 输入框：黑色半透明底
                                img.raycastTarget = false
                            end
                        end)
                        -- 关掉滚动条组件，免得它运行时改 Handle 的尺寸
                        pcall(function()
                            local sb = ch.gameObject.GetComponent(Scrollbar)
                            if type(sb) ~= "nil" then sb.enabled = false end
                        end)
                        -- 把中间两层拉满，让 Handle 的坐标系等于 Scroll View
                        pcall(function()
                            local cc = ch.childCount or 0
                            for j = 0, cc - 1 do
                                local c2 = nil
                                pcall(function() c2 = ch.GetChild(j) end)
                                if type(c2) ~= "nil" then
                                    local r2 = c2.asRectTransform
                                    pcall(function()
                                        r2.anchorMin = Vector2(0, 0); r2.anchorMax = Vector2(1, 1)
                                        r2.pivot = Vector2(0.5, 0.5)
                                        r2.sizeDelta = Vector2(0, 0)
                                        r2.anchoredPosition = Vector2(0, 0)
                                    end)
                                    -- Handle → 黑色"输入框底"
                                    local c3n = nil
                                    pcall(function() c3n = c2.name end)
                                    local g3 = c2.gameObject
                                    local img3 = nil
                                    pcall(function() img3 = g3.GetComponent(Image) end)
                                    if type(img3) ~= "nil" then
                                        pcall(function()
                                            r2.anchorMin = Vector2(0.5, 1); r2.anchorMax = Vector2(0.5, 1)
                                            r2.pivot = Vector2(0.5, 1)
                                            r2.sizeDelta = Vector2(w, inputBgH)
                                            r2.anchoredPosition = Vector2(0, inputBgTop - chatTop)
                                        end)
                                        pcall(function()
                                            img3.color = Color(0, 0, 0, 0)   -- 输入底由滚动条那块图承担
                                            img3.raycastTarget = false
                                        end)
                                        pcall(function() c2:SetSiblingIndex(9) end)   -- 排到最前（文字下面最后一块）
                                    end
                                end
                            end
                        end)
                    end
                    -- ★ Viewport 是遮罩：只保持"整块拉伸"，绝不再挪用它的图层（挪用会把文字裁光）
                end
            end
        end)

        BOX_X = boxX
        BOX_TOP = chatTop
        scroll_ref = scroll
        BASE_W = realW
        BASE_H = realH
        collectUITexts(scroll)
        -- 四个按钮文字：聊天框内最后一行（四等分）
        local btnTop2 = chatTop - TEXT_TOP_PAD - ROWS_CHAT * rowH
        local cwT = w / 4
        for i = 0, 3 do
            placeTxt(i, boxX - w / 2 + cwT * i + cwT / 2, btnTop2 - rowH / 2, cwT - 6, rowH, 0.78, 0.82, 0.86, 1)
        end
        -- 输入框文字：框下独立输入框
        placeTxt(4, boxX, (chatTop - hChat - GAP) - INPUT_H / 2, w - 16, INPUT_H, 1, 1, 1, 1)
        -- ============ ④ 真白线边框 + 按钮底板（包里手工新增的 12 个图层）============
        collectUIObjects(scroll)
        do
            do
                local bt = TEXT_TOP_PAD + ROWS_CHAT * lineHeight() - 10
                local bb2 = (GEOM_H or 0) - INPUT_H - GAP
                local topPx = (BOX_TOP_PX or 0) - bt * (SCALE_PX or 1)
                local botPx = (BOX_TOP_PX or 0) - bb2 * (SCALE_PX or 1)
                local inTopPx = (BOX_TOP_PX or 0) - ((GEOM_H or 0) - INPUT_H) * (SCALE_PX or 1)
                print(string.format("[RFAiChat][按钮区] 屏幕 y=%.0f~%.0f ｜ x=%.0f~%.0f ｜ 输入框 屏幕 y=%.0f~%.0f",
                    botPx, topPx, (BOX_LEFT_PX or 0), (BOX_LEFT_PX or 0) + (GEOM_W or 0) * (SCALE_PX or 1),
                    (BOX_TOP_PX or 0) - ((GEOM_H or 0)) * (SCALE_PX or 1), inTopPx))
            end
            print(string.format("[RFAiChat][几何表] 左上角=(%.0f,%.0f) 像素缩放=%.3f ｜ 框实际=%.0fx%.0f ｜ 聊天区高=%.0f 行高=%.0f 聊天行数=%d ｜ 输入框高=%.0f ｜ 空隙=%.0f",
                BOX_LEFT_PX or -1, BOX_TOP_PX or -1, SCALE_PX or -1,
                realW, realH, hChat, lineHeight(), ROWS_CHAT, INPUT_H, GAP))
            local no, nt = 0, 0
            for _ in pairs(UI_OBJ) do no = no + 1 end
            for _ in pairs(UI_TXT) do nt = nt + 1 end
            local btnY = chatTop - TEXT_TOP_PAD - ROWS_CHAT * rowH
            print(string.format("[RFAiChat][诊断] 新图层：白线/底板=%d 个 文字=%d 个 ｜ 按钮行y=%.0f 聊天框底=%.0f 输入框底=%.0f",
                no, nt, btnY, chatTop - hChat, chatTop - hChat - GAP - INPUT_H))
        end
        local bw = 2
        local rowH2 = rowH
        local chatLeft = boxX - w / 2
        local chatBot = chatTop - hChat
        -- 聊天框四条白线
        placeUI(0, boxX, chatTop, w, bw, 1, 1, 1, 1)
        placeUI(1, boxX, chatBot - bw, w, bw, 1, 1, 1, 1)
        placeUI(2, chatLeft, chatTop, bw, hChat, 1, 1, 1, 1)
        placeUI(3, chatLeft + w - bw, chatTop, bw, hChat, 1, 1, 1, 1)
        -- 独立输入框（在聊天框下面）四条白线
        local inTop = chatBot - GAP
        local inBot = inTop - INPUT_H
        placeUI(4, boxX, inTop, w, bw, 1, 1, 1, 1)
        placeUI(5, boxX, inBot - bw, w, bw, 1, 1, 1, 1)
        placeUI(6, chatLeft, inTop, bw, INPUT_H + bw, 1, 1, 1, 1)
        placeUI(7, chatLeft + w - bw, inTop, bw, INPUT_H + bw, 1, 1, 1, 1)
        -- 按钮底板：聊天框内最后一行（蓝框区，四等分）
        local btnTop = chatTop - TEXT_TOP_PAD - ROWS_CHAT * rowH2
        local cw = w / 4
        for i = 0, 3 do
            placeUI(8 + i, boxX - w / 2 + cw * i + cw / 2, btnTop - 2, cw - 8, rowH2 - 6, 0.5, 1, 1, 1)
        end
        -- 独立输入框的黑底：用 Canvas 级 Panel 摆到红框区
        pcall(function()
            local canvasT = scroll.parent
            for i = 0, (canvasT.childCount or 0) - 1 do
                local child = canvasT.GetChild(i)
                if tostring(child.name) == "Panel" then
                    local prt = child.asRectTransform
                    prt.anchorMin = Vector2(0, 0.5); prt.anchorMax = Vector2(0, 0.5)
                    prt.pivot = Vector2(0.5, 0.5)
                    prt.sizeDelta = Vector2(w, INPUT_H)
                    prt.anchoredPosition = Vector2(boxX, inTop - INPUT_H / 2)
                    local img = child.gameObject.GetComponent(Image)
                    if type(img) ~= "nil" then img.color = Color(0, 0, 0, 0.55); img.raycastTarget = false end
                end
            end
        end)
        -- 层级：滚动条(输入底) 最底 → 12 个新图层 → Viewport(文字) 最上
        local kids = 0
        pcall(function() kids = scroll.childCount or 0 end)
        for i = 1, 12 do
            if type(UI_OBJ[i]) ~= "nil" then pcall(function() UI_OBJ[i]:SetSiblingIndex(i) end) end
        end
        if type(viewport) ~= "nil" then pcall(function() viewport:SetSiblingIndex(kids - 1) end) end

        -- ③ Scroll View 自己的底图设透明 → 中间的"空隙"能透出战场（两个框才分得开）
        pcall(function()
            local si = scroll.gameObject.GetComponent(Image)
            if type(si) ~= "nil" then
                si.color = Color(0, 0, 0, 0.30)      -- 聊天框：黑色半透明底（恢复）
                si.raycastTarget = false
            end
        end)

        -- 画布缩放系数：CanvasScaler 在非参考分辨率下会 != 1，
        -- 而 RectTransform.position 是屏幕像素、sizeDelta 是画布单位，换算必须带上它
        CANVAS_SCALE = 1
        if type(SELF) ~= "nil" and type(SELF.targets) ~= "nil" and type(SELF.targets.canvas) ~= "nil" then
            pcall(function()
                local cv = SELF.targets.canvas.GetComponent(Canvas)
                if type(cv) ~= "nil" and type(cv.scaleFactor) ~= "nil" and cv.scaleFactor > 0 then
                    CANVAS_SCALE = cv.scaleFactor
                end
            end)
        end

        local sw, sh = 0, 0
        pcall(function() sw = Screen.width; sh = Screen.height end)
        local px, py, sx, sy, lsc = 0, 0, 0, 0, 1
        local pvxX, pvyY = 0.5, 0.5
        pcall(function()
            px = scroll.position.x; py = scroll.position.y
            sx = scroll.sizeDelta.x; sy = scroll.sizeDelta.y
            lsc = scroll.localScale.x
            pvxX = scroll.pivot.x; pvyY = scroll.pivot.y
        end)

        -- 记下两个框的屏幕矩形（命中判定用）——必须放在读取 px/py/sx/sy 之后！
        local sc2 = lsc * CANVAS_SCALE
        local bl = px - sx * sc2 * pvxX
        local bb = py - sy * sc2 * pvyY                     -- 整块底边
        local wPix = sx * sc2
        local totalH = sy * sc2
        local inH = INPUT_H * sc2
        local gapPix = GAP * sc2
        local chatPix = math.max(1, totalH - inH - gapPix)
        -- 输入区在最底部，聊天区在顶部，中间是空隙
        -- 输入框在聊天框【下面】：底部对齐整块底边
        INPUT_RECT = { left = bl, bottom = bb, width = wPix, height = inH }
        CHAT_RECT = { left = bl, bottom = bb + inH + gapPix, width = wPix, height = chatPix }
        print(string.format("[RFAiChat] 位置诊断：屏幕 %dx%d ｜ 框中心=(%.0f,%.0f) 尺寸=(%.0f,%.0f) 缩放=%.2f（画布×%.2f）｜ 左边距=%.0f",
            sw, sh, px, py, sx, sy, lsc, CANVAS_SCALE, px - sx * lsc * CANVAS_SCALE / 2))
        -- 点击区诊断：按钮点不动时，直接用这几个数核对
        local totalScale = lsc * CANVAS_SCALE
        local zLeft = px - sx * totalScale * pvxX
        local zBottom = py - sy * totalScale * pvyY
        do
            local function rd(o)
                local a, b2, c2, d2 = 0, 0, 0, 0
                pcall(function() a = o.position.x; b2 = o.position.y; c2 = o.sizeDelta.x; d2 = o.sizeDelta.y end)
                return a, b2, c2, d2
            end
            local vx, vy, vw, vh = rd(viewport or rt)
            local cx2, cy2, cw, chh = rd(content or rt)
            local tx, ty, tw, th = rd(rt)
            print(string.format("[RFAiChat] 元素诊断：Viewport(%.0f,%.0f %.0fx%.0f) Content(%.0f,%.0f %.0fx%.0f) Text(%.0f,%.0f %.0fx%.0f)",
                vx, vy, vw, vh, cx2, cy2, cw, chh, tx, ty, tw, th))
        end
        print(string.format("[RFAiChat] 底色诊断：聊天底 [%.0f, %.0f] 高%.0f ｜ 输入底 [%.0f, %.0f] 高%.0f ｜ 中间空隙 %.0f",
            chatBgTop - chatBgH, chatBgTop, chatBgH,
            inputBgTop - inputBgH, inputBgTop, inputBgH,
            (chatBgTop - chatBgH) - inputBgTop))
        if type(CHAT_RECT) ~= "nil" and type(INPUT_RECT) ~= "nil" then
            print(string.format("[RFAiChat] 点击区：聊天区 底=%.0f 高=%.0f ｜ 空隙=%.0f ｜ 输入区 底=%.0f 高=%.0f ｜ 左=%.0f 宽=%.0f ｜ 行高=%.0f",
                CHAT_RECT.bottom, CHAT_RECT.height, GAP * (lsc * CANVAS_SCALE),
                INPUT_RECT.bottom, INPUT_RECT.height, INPUT_RECT.left, INPUT_RECT.width,
                lineHeight() * (lsc * CANVAS_SCALE)))
        end
    end)
    if not okChain then
        print("[RFAiChat] 尺寸调整未完全成功（不影响使用）：" .. tostring(errChain))
    end

    -- 可见行数：从"聊天框本身的高度"算（绝不能用文字框——它刚被设成很小的高度，会算出只剩 3 行）
    local boxH = 400
    pcall(function() boxH = SCROLL_RT.sizeDelta.y end)
    BOX_H = boxH
    -- ★ 第20轮 A：不再硬编码 -3；可用高度 = 框高 − 实测按钮行高（找不到按钮才退回 -3 的保守值）
    local btnRowH = 0
    pcall(function()
        local b = SELF and SELF.targets and SELF.targets.btn_send
        if type(b) ~= "nil" then
            local brt = b.transform.asRectTransform
            if type(brt) ~= "nil" then btnRowH = brt.rect.height end
        end
    end)
    local usableH = boxH - (btnRowH > 0 and (btnRowH + 8) or (target * 1.25 * 3))
    local cap = math.floor(usableH / math.max(8, target * 1.25))
    MAX_LINES = math.max(3, math.min(40, cap))
    print("[RFAiChat] 字号=" .. tostring(target) .. " 可见行数=" .. tostring(MAX_LINES))
end

local function initChatUI(beh)
    if type(beh) == "nil" or type(beh.targets) == "nil" then
        print("[RFAiChat] 警告：targets 为空，聊天框 UI 不可用")
        return
    end
    local ok, err = pcall(function() beh.text = beh.targets.text.GetComponent(Text) end)
    if type(beh.text) == "nil" then
        pcall(function() beh.text = beh.targets.text.GetComponentInChildren(Text) end)
    end
    if type(beh.text) == "nil" then
        print("[RFAiChat] 警告：没找到聊天框 Text 组件（err=" .. tostring(err) .. "），退化为 Overlay 提示")
        return
    end
    uiReady = true
    print("[RFAiChat] 聊天框 UI 已就绪（Chat Feed 同款 Text 组件已取得）")
    tuneUI()
    renderView()
end

-- ================================================================
--  官方 UI（Unity Button / InputField）接入
--
--  为什么这样做：Ravenscript 的代理类型表在游戏启动时就固定了 161 个类型，
--  里面【没有 EventSystem】——所以 Lua 既拿不到焦点、也不能 AddComponent。
--  好消息是 Button.onClick 与 InputField.onEndEdit 的类型是 ScriptEventProxy，
--  它暴露了 AddListener(script, "方法名")，所以「按钮点了执行什么」可以完全放在 Lua 里，
--  只有「键盘焦点」必须由插件（C#）完成。
-- ================================================================

-- 取按钮组件：targets 里给的可能是 GameObject，也可能已经是组件
local function btnComponent(go)
    local b = nil
    local err = nil
    pcall(function() b = go.GetComponent(Button) end)
    if type(b) == "nil" then pcall(function() b = go.GetComponentInChildren(Button) end) end
    if type(b) == "nil" then
        pcall(function() err = tostring(go.name) end)
        uiHookErrors[#uiHookErrors + 1] = "取不到 Button 组件（" .. tostring(err or "?") .. "）"
    end
    return b
end

-- 把 UI 物体的屏幕矩形读出来（世界坐标 → 屏幕像素，y 向下）
local function screenRectOf(rt)
    if type(rt) == "nil" then return nil end
    local px, py, sx, sy = nil, nil, nil, nil
    local w, h, pvx, pvy = nil, nil, 0.5, 0.5
    pcall(function()
        px = rt.position.x; py = rt.position.y
        w = rt.rect.width; h = rt.rect.height
        sx = rt.localScale.x; sy = rt.localScale.y
        pvx = rt.pivot.x; pvy = rt.pivot.y
    end)
    if type(px) == "nil" or type(w) == "nil" or w <= 0 then return nil end
    local scale = (CANVAS_SCALE or 1)
    local ww = w * sx * scale
    local hh = h * sy * scale
    local left = px - ww * pvx
    local bottom = py - hh * pvy
    return { left = left, top = bottom + hh, right = left + ww, bottom = bottom,
             width = ww, height = hh }
end

-- 重新读一次画布缩放（CanvasScaler 在分辨率 ≠ 1080p 时会 != 1，必须算进去）
local function refreshCanvasScale()
    pcall(function()
        local cv = SELF.targets.canvas.GetComponent(Canvas)
        if type(cv) ~= "nil" and type(cv.scaleFactor) ~= "nil" and cv.scaleFactor > 0 then
            CANVAS_SCALE = cv.scaleFactor
        end
    end)
end

-- 刷新「输入框 + 四个真按钮」的屏幕矩形与插件侧登记
refreshUIRects = function()
    if type(SELF) == "nil" or SELF.useUnityUI ~= true then return end
    refreshCanvasScale()
    -- ★ 用 SELF.rtOf 取（走 go.transform.asRectTransform，退路 GetComponent(RectTransform)）；
    --   绝不再用 xxx.asRectTransform 直接取 —— targets 给的是 GameObject，那样取到的是 nil（实机已确证）。
    local rt = nil
    if type(SELF.rtOf) ~= "nil" then pcall(function() rt = SELF.rtOf(SELF.targets.input) end) end
    local box = nil
    pcall(function() box = screenRectOf(rt) end)
    if type(box) == "nil" then
        print("[RFAiChat][诊断-矩形] 取不到输入框矩形（rt=" .. tostring(rt) .. "）")
    end
    INPUT_SCREEN_RECT = box
    if type(box) ~= "nil" and type(AiChatBridge) ~= "nil" then
        pcall(function()
            AiChatBridge.SetInputRect(box.left, box.top, box.width, box.height)
        end)
    end
end

-- 绑定真按钮的点击回调 + 真输入框的回车回调
hookUnityUI = function(self)
    if type(self) == "nil" or type(self.targets) == "nil" then return end
    UI_BTNS = {}
    UI_BTN_N = 0
    uiHookErrors = {}
    local defs = {
        { key = "btn_send",   act = "send" },
        { key = "btn_debug",  act = "debug" },
        { key = "btn_report", act = "report" },
        { key = "btn_back",   act = "back" },
    }
    local bound = 0
    for i = 1, #defs do
        local go = nil
        pcall(function() go = self.targets[defs[i].key] end)
        if type(go) ~= "nil" then
            UI_BTN_N = UI_BTN_N + 1
            UI_BTNS[UI_BTN_N] = { go = go, act = defs[i].act, hooked = false }
            local b = btnComponent(go)
            if type(b) ~= "nil" then
                local act = defs[i].act
                local me = self
                local ok, err = pcall(function()
                    b.onClick.AddListener(me, "onClick_" .. act)
                end)
                if ok then
                    bound = bound + 1
                    UI_BTNS[UI_BTN_N].hooked = true
                else
                    uiHookErrors[#uiHookErrors + 1] = defs[i].key .. " 回调绑定失败：" .. tostring(err)
                end
            end
        end
    end
    -- 登记按钮矩形给插件（插件侧命中判定用；与 Lua 侧互为备份）
    if type(AiChatBridge) ~= "nil" and UI_BTN_N >= 4 then
        pcall(function()
            AiChatBridge.SetButtonRects(UI_BTNS[1].go, UI_BTNS[2].go, UI_BTNS[3].go, UI_BTNS[4].go)
        end)
    end

    -- 输入框：回车提交（onEndEdit 在提交与失焦时都会触发，用 text 是否变化过滤）
    local inf = nil
    local errInf = nil
    pcall(function() inf = self.targets.input.GetComponent(InputField) end)
    if type(inf) ~= "nil" then
        self.inputField = inf
        pcall(function() inf.characterLimit = 120 end)
        local ok, err = pcall(function() inf.onEndEdit.AddListener(self, "onInputEndEdit") end)
        if ok then
            bound = bound + 1
        else
            uiHookErrors[#uiHookErrors + 1] = "输入框回车回调绑定失败：" .. tostring(err)
        end
    else
        uiHookErrors[#uiHookErrors + 1] = "取不到 InputField 组件（targets.input 指向的不是输入框？）"
    end
    print("[RFAiChat] 官方 UI 已接线：真按钮 " .. UI_BTN_N .. " 个，回调绑定 " .. bound .. " 个")
    if #uiHookErrors > 0 then
        print("[RFAiChat] 接线问题：" .. table.concat(uiHookErrors, " ｜ "))
    end
end

-- 这个动作对应的按钮是否已经挂上 Unity 回调（挂了就不需要脚本再执行一遍，否则会点一次动两次）
local function isHooked(act)
    for i = 1, UI_BTN_N do
        if UI_BTNS[i] ~= nil and UI_BTNS[i].act == act and UI_BTNS[i].hooked == true then return true end
    end
    return false
end

-- 按钮点击回调（由 Unity 的 Button.onClick 触发）
function RFAiChat:onClick_send()   doAction("send")   end
function RFAiChat:onClick_debug()  doAction("debug")  end
function RFAiChat:onClick_report() doAction("report") end
function RFAiChat:onClick_back()   doAction("back")   end

-- 输入框提交回调
function RFAiChat:onInputEndEdit(value)
    if not inputMode then return end
    local txt = tostring(value or "")
    if txt ~= "" then
        inputText = txt
        sendInput()
    end
end

-- 官方 UI 实测数据（按 F12 或点[调试]按钮时打印），用于实机核对布局/焦点
uiDiag = function(tag)
    local parts = {}
    parts[#parts + 1] = "[RFAiChat][UI实测] " .. tostring(tag or "")
    pcall(function()
        local r = INPUT_SCREEN_RECT
        if type(r) ~= "nil" then
            parts[#parts + 1] = string.format("输入框 屏幕矩形 左=%.0f 上=%.0f 右=%.0f 下=%.0f",
                r.left, r.top, r.right, r.bottom)
        else
            parts[#parts + 1] = "输入框 屏幕矩形=读不到"
        end
    end)
    pcall(function()
        local rt = SELF.rtOf(SELF.targets.scroll)
        parts[#parts + 1] = string.format("聊天框 中心=(%.0f,%.0f) 尺寸=%.0fx%.0f",
            rt.position.x, rt.position.y, rt.rect.width, rt.rect.height)
    end)
    pcall(function()
        local rt = SELF.rtOf(SELF.targets.text)
        parts[#parts + 1] = string.format("文字 位置=(%.0f,%.0f) 宽=%.0f 高=%.0f",
            rt.position.x, rt.position.y, rt.rect.width, rt.rect.height)
    end)
    pcall(function()
        if type(SELF.inputField) ~= "nil" then
            parts[#parts + 1] = "输入框 焦点=" .. tostring(SELF.inputField.isFocused)
                .. " 字数=" .. tostring(#tostring(SELF.inputField.text or ""))
        end
    end)
    pcall(function() parts[#parts + 1] = "画布缩放=" .. tostring(CANVAS_SCALE) end)
    if type(AiChatBridge) ~= "nil" then
        pcall(function() parts[#parts + 1] = tostring(AiChatBridge.DiagUI()) end)
    end
    print(table.concat(parts, " ｜ "))
end

-- ============================== 聊天记录与导出 ==============================
pushLine = function(line, meta)
    if type(line) == "nil" or line == "" then return end
    history[#history + 1] = line
    historyMeta[#historyMeta + 1] = meta or ""
    while #history > CFG.historyMax do
        table.remove(history, 1)
        table.remove(historyMeta, 1)
    end
    if viewOffset == 0 then renderView() end
    if not uiReady then
        if fallbackUsed < 1 then
            fallbackUsed = fallbackUsed + 1
            pcall(function() Overlay.ShowMessage(line, 4) end)
        end
    end
end

local function isOwnText(text)
    if type(text) == "nil" or text == "" then return false end
    for i = 1, #ownRecent do
        local body = ownRecent[i]
        if body ~= "" and string.find(text, body, 1, true) ~= nil then return true end
    end
    return false
end

local function rememberOwn(body)
    if type(body) == "nil" or body == "" then return end
    ownRecent[#ownRecent + 1] = body
    while #ownRecent > 8 do table.remove(ownRecent, 1) end
end

-- 导出时把富文本标签清掉，写成干净的纯文本
local function stripTags(s)
    if type(s) == "nil" then return "" end
    return (string.gsub(tostring(s), "<[^>]*>", ""))
end

-- 官方 UI 的具体实现见本文件「官方 UI（Unity Button / InputField）接入」一节
-- （前向声明已提前到状态区，这里不再重复声明，避免遮蔽）

-- 鼠标点击命中判定：
--   底行 = [输入框 ............. ][调试]           （输入区 78%，按钮区 22%）
--   调试模式下，底行上一行 = [导出][隐藏][上翻][下翻] （四等分）
local function hitTest(mx, my)
    if type(SELF) ~= "nil" and SELF.useUnityUI then
        -- ===== 官方 UI：用【真按钮/真输入框自己的矩形】判定 =====
        -- 位置全部由 Unity 决定，脚本只读矩形 —— 这是"点了没反应"的根本修复。
        -- 1) 先问插件（C# 侧用 RectTransformUtility 判，最准）
        if type(AiChatBridge) ~= "nil" then
            local ok, hit = pcall(function() return AiChatBridge.HitElement(mx, my) end)
            if ok and type(hit) ~= "nil" and tostring(hit) ~= "" then
                local h = tostring(hit)
                if h == "input" then return "input" end
                if h == "btn_send" then return "send" end
                if h == "btn_debug" then return "debug" end
                if h == "btn_report" then return "report" end
                if h == "btn_back" then return "back" end
            end
        end
        -- 2) 插件不可用时，退化为脚本自己读矩形
        if type(INPUT_SCREEN_RECT) ~= "nil" then
            local r = INPUT_SCREEN_RECT
            if mx >= r.left - 6 and mx <= r.right + 6 and my >= r.top - 6 and my <= r.bottom + 6 then
                return "input"
            end
        end
        for i = 1, UI_BTN_N do
            local b = UI_BTNS[i]
            local go = type(b) ~= "nil" and b.go or nil
            if type(go) ~= "nil" then
                local rt = nil
                if type(SELF) ~= "nil" and type(SELF.rtOf) ~= "nil" then
                    pcall(function() rt = SELF.rtOf(go) end)   -- GameObject → GetComponent(RectTransform)
                end
                local r = screenRectOf(rt)
                if type(r) ~= "nil" and mx >= r.left - 4 and mx <= r.right + 4 and my >= r.top - 4 and my <= r.bottom + 4 then
                    return b.act
                end
            end
        end
        return nil
    end
    -- 每次都按"当前实际矩形"重算（调试窗口会放大/还原，缓存值会过期 → 导致按钮点不中、窗口关不掉）
    pcall(function()
        if type(scroll_ref) ~= "nil" then
            local r = scroll_ref.rect
            if type(r) ~= "nil" and type(r.height) ~= "nil" and r.height > 10 then
                GEOM_H = r.height
                GEOM_W = r.width or GEOM_W
                local px, py, sxs = 0, 0, 1
                pcall(function() px = scroll_ref.position.x; py = scroll_ref.position.y; sxs = scroll_ref.localScale.x end)
                SCALE_PX = (CANVAS_SCALE or 1) * (sxs or 1)
                BOX_LEFT_PX = px - (GEOM_W * SCALE_PX) / 2
                BOX_TOP_PX = py + (GEOM_H * SCALE_PX) / 2
            end
        end
    end)
    if type(BOX_LEFT_PX) == "nil" or type(SCALE_PX) == "nil" then return nil end
    local dx = (mx - BOX_LEFT_PX) / SCALE_PX
    local dy = (BOX_TOP_PX - my) / SCALE_PX
    local w = GEOM_W or 0
    local H = GEOM_H or 0
    -- ★ 把框的下半部划成两段（宽容命中，避免"看着在上面、实际差几十像素"）
    local inZone = H - INPUT_H - 30                       -- 输入段起点（下移+加宽）
    if dx >= -14 and dx <= w + 14 and dy >= inZone and dy <= H + 60 then
        return "input"                                    -- 底部一段 = 输入框
    end
    local btnTop = TEXT_TOP_PAD + ROWS_CHAT * lineHeight() - 10
    if dx >= -12 and dx <= w + 12 and dy >= btnTop and dy < inZone then
        local fx = dx / math.max(1, w)                    -- 其上一段 = 四个按钮
        if fx < 0.25 then return debugMode and "reset" or "send" end
        if fx < 0.50 then return debugMode and "export" or "debug" end
        if fx < 0.75 then return debugMode and "test" or "report" end
        return debugMode and "closedebug" or "back"
    end
    return nil
end

-- 调试窗口：占屏幕大部的居中大窗（进入/退出时切换尺寸）
-- ★ 官方 UI 下【不放大】：布局是 Unity 定死的，放大后四个真按钮会被推出屏幕，
--   结果"调试窗口关不掉"。官方 UI 的调试内容本身就画在聊天框里（可滚动）。
local function applyDebugLayout(on)
    if type(SELF) ~= "nil" and SELF.useUnityUI == true then return end
    if type(scroll_ref) == "nil" then return end
    pcall(function()
        if on then
            local bw2 = (GEOM_W or 218) * 3.2
            local bh2 = (GEOM_H or 418) * 2.0
            scroll_ref.sizeDelta = Vector2(bw2, bh2)
            scroll_ref.anchorMin = Vector2(0.5, 0.5)
            scroll_ref.anchorMax = Vector2(0.5, 0.5)
            scroll_ref.pivot = Vector2(0.5, 0.5)
            scroll_ref.anchoredPosition = Vector2(0, 0)
        else
            scroll_ref.sizeDelta = Vector2(BASE_W or 218, BASE_H or 418)
            scroll_ref.anchorMin = Vector2(0, 0.5)
            scroll_ref.anchorMax = Vector2(0, 0.5)
            scroll_ref.pivot = Vector2(0.5, 0.5)
            scroll_ref.anchoredPosition = Vector2(12 + (CONF.leftMargin or 0) + (BASE_W or 218) / 2, 0)
        end
    end)
end

doAction = function(a)
    if a == "input" then
        if not inputMode then openInput() end
    elseif a == "back" then
        if inputMode then closeInput() else closeUI() end
    elseif a == "send" then
        if inputMode then sendInput() else openInput() end
    elseif a == "debug" then
        -- 官方 UI：按钮标签固定，所以[调试]做成"开/关"切换（再点一次关掉）
        local on = true
        if type(SELF) ~= "nil" and SELF.useUnityUI == true and debugMode then on = false end
        debugMode = on
        viewOffset = 0
        applyDebugLayout(on)
        renderView()
        print("[RFAiChat] 调试窗口：" .. (on and "开" or "关"))
        pcall(function() uiDiag(on and "点[调试]开" or "点[调试]关") end)
    elseif a == "closedebug" then
        debugMode = false
        viewOffset = 0
        applyDebugLayout(false)
        renderView()
        print("[RFAiChat] 调试窗口：关")
    elseif a == "reset" then
        history = {}
        viewOffset = 0
        renderView()
        print("[RFAiChat] 调试日志已重置")
    elseif a == "test" then
        pushRaw({ { "t", "selftest" } })
        print("[RFAiChat] 已发起连接测试（结果会出现在聊天框里）")
    elseif a == "report" then
        pushRaw({ { "t", "tick" }, { "kind", "report" }, { "forced", "true" } })
        print("[RFAiChat] 已手动触发战况播报（验证用）")
    elseif a == "export" then
        exportHistory("按钮")
    elseif a == "hide" then
        if type(SELF) ~= "nil" and type(SELF.targets) ~= "nil" and type(SELF.targets.canvas) ~= "nil" then
            pcall(function() SELF.targets.canvas.SetActive(false) end)
        end
        closeUI()
        print("[RFAiChat] 已隐藏聊天框")
    elseif a == "up" then
        viewOffset = math.min(viewOffset + MAX_LINES, math.max(0, #history - MAX_LINES))
        renderView()
    elseif a == "down" then
        viewOffset = 0
        renderView()
    end
end

exportHistory = function(reason)
    if exportBusy then return end
    if #history == 0 then
        print("[RFAiChat] 没有可导出的聊天记录")
        return
    end
    exportBusy = true
    local per = 20
    local total = math.ceil(#history / per)
    for part = 1, total do
        local seg = {}
        for i = (part - 1) * per + 1, math.min(part * per, #history) do
            local m = historyMeta[i]
            if type(m) ~= "nil" and m ~= "" then
                seg[#seg + 1] = stripTags(history[i]) .. "    【" .. m .. "】"
            else
                seg[#seg + 1] = stripTags(history[i])
            end
        end
        pushRaw({
            { "t", "export" }, { "part", part }, { "total", total },
            { "reason", reason or "manual" }, { "lines", table.concat(seg, "\n") },
        })
    end
    print("[RFAiChat] 已请求导出：" .. #history .. " 行，分 " .. total .. " 批（" .. tostring(reason) .. "）")
    exportBusy = false
end

-- ============================== 打字模式 ==============================
local function applyInputLock(on)
    pcall(function() Player.inputEnabled = not on end)
    pcall(function() Player.allowMouseLook = not on end)
    pcall(function() Player.movementInputEnabled = not on end)
end

openUI = function()
    if uiMode then return end
    uiMode = true
    pcall(function() Screen.UnlockCursor() end)
    pcall(function() Player.allowMouseLook = false end)
    renderView()
    print("[RFAiChat] 鼠标模式：开（点输入框才能打字；Esc 或 " .. CONF.chatkey .. " 关闭）")
end

closeUI = function()
    if inputMode then closeInput() end
    uiMode = false
    pcall(function() Screen.LockCursor() end)
    pcall(function() Player.allowMouseLook = true end)
    pcall(function() Player.inputEnabled = true end)
    pushRaw({ { "t", "ime" }, { "on", "false" } })
    renderView()
    print("[RFAiChat] 鼠标模式：关")
end

openInput = function()
    if inputMode then return end
    if type(SELF) ~= "nil" and type(SELF.targets) ~= "nil" and type(SELF.targets.canvas) ~= "nil" then
        pcall(function()
            if SELF.targets.canvas.activeSelf ~= true then SELF.targets.canvas.SetActive(true) end
        end)
    end
    inputMode = true
    inputText = ""
    applyInputLock(true)
    pcall(function() Screen.UnlockCursor() end)
    pushRaw({ { "t", "ime" }, { "on", "true" } })   -- 让插件打开 Unity 输入法（否则打不出中文）
    -- ★ 官方 UI：让真 InputField 拿到键盘焦点（这一步只能在插件里做，Lua 拿不到 EventSystem）
    if type(SELF) ~= "nil" and SELF.useUnityUI then
        refreshUIRects()
        focusTriedAt = now()
        focusWarned = false
        if type(AiChatBridge) ~= "nil" then
            pcall(function() AiChatBridge.FocusInput() end)
        end
    end
    viewOffset = 0
    renderView()
    print("[RFAiChat] 打字模式：开（Enter 或点[发送]发送；Esc 退出）")
end

closeInput = function()
    if not inputMode then return end
    inputMode = false
    inputText = ""
    focusTriedAt = 0
    applyInputLock(false)
    pcall(function() Player.allowMouseLook = false end)   -- 回到鼠标模式：仍不转视角
    pushRaw({ { "t", "ime" }, { "on", "false" } })
    -- ★ 官方 UI：交还键盘焦点（否则游戏还在被我们占着键盘）
    if type(SELF) ~= "nil" and SELF.useUnityUI and type(AiChatBridge) ~= "nil" then
        pcall(function() AiChatBridge.UnfocusInput() end)
    end
    renderView()
    print("[RFAiChat] 打字模式：关")
end

sendInput = function()
    -- 官方 UI：文字以【真 InputField】为准（玩家在真输入框里打的字）
    local text = inputText
    if type(SELF) ~= "nil" and SELF.useUnityUI and type(SELF.inputField) ~= "nil" then
        local ok, v = pcall(function() return SELF.inputField.text end)
        if ok and type(v) ~= "nil" and tostring(v) ~= "" then text = tostring(v) end
    end
    inputText = ""
    if type(SELF) ~= "nil" and type(SELF.inputField) ~= "nil" then
        pcall(function() SELF.inputField.text = "" end)
    end
    if inputMode then closeInput() end
    if type(text) == "nil" or text == "" then return end
    local me = "PLAYER"
    pcall(function() me = Player.actor.name end)
    pushRaw({ { "t", "say" }, { "from", me }, { "text", text } })
    pushLine("<b>你</b>: " .. text, "你（玩家）发言 · 等待士兵回应")
    print("[RFAiChat] 玩家发言：" .. text)
end

local function utf8LastCharLen(s)
    local n = #s
    if n == 0 then return 0 end
    local i = n
    while i >= 1 do
        local b = string.byte(s, i)
        if type(b) == "nil" then return 0 end
        if b < 128 or b >= 192 then
            local len = 1
            if b >= 240 then len = 4 elseif b >= 224 then len = 3 elseif b >= 192 then len = 2 end
            if i + len - 1 > n then return n - i + 1 end
            return len
        end
        i = i - 1
    end
    return n
end

local function handleInput()
    -- ===== 官方 UI（真 InputField）：文字由 Unity 收，脚本只读结果 =====
    -- 这就是"打不了英文"的修复：脚本不再自己抢 Input.inputString，
    -- 键盘交给真 InputField，英文自然能进输入框（且不再漏给游戏）。
    if type(SELF) ~= "nil" and SELF.useUnityUI and type(SELF.inputField) ~= "nil" then
        -- 插件报来的回车/取消（插件在 C# 里采集，最可靠）
        if type(AiChatBridge) ~= "nil" then
            local okEv, ev = pcall(function() return AiChatBridge.InputEvent() end)
            if okEv and type(ev) ~= "nil" then
                ev = tostring(ev)
                if ev == "submit" then
                    local now2 = ""
                    pcall(function() now2 = tostring(SELF.inputField.text or "") end)
                    inputText = now2
                    sendInput()
                    return
                elseif ev == "cancel" then
                    closeInput(); closeUI(); return
                end
            end
        end
        -- 焦点状态 + 文字镜像（显示用）
        local focused = false
        if type(AiChatBridge) ~= "nil" then
            pcall(function() focused = (AiChatBridge.InputFocused() == true) end)
        end
        inputFocused = focused
        -- ★ 诊断：键盘到底进没进输入框
        if inputMode and not inputFocusLogged then
            inputFocusLogged = true
            local raw = ""
            pcall(function() raw = tostring(Input.inputString or "") end)
            local realFocused = nil
            pcall(function() realFocused = (SELF.inputField.isFocused == true) end)
            print("[RFAiChat][诊断-焦点] 进打字模式：插件报聚焦=" .. tostring(focused)
                .. " 输入框自报 isFocused=" .. tostring(realFocused)
                .. " 本帧 Input.inputString=" .. string.format("%q", raw))
        end
        -- ★ 焦点看门狗：进打字模式后 3 秒还没拿到焦点，就自动退出。
        --   否则 Player.inputEnabled 一直是 false —— 玩家的"人"不动、鼠标也被解锁，
        --   在实机上看起来就是"卡住了"。宁可退出，也不能把玩家锁死。
        if not focused and focusTriedAt > 0 and (now() - focusTriedAt) > 3 then
            focusTriedAt = 0
            print("[RFAiChat][诊断] 3 秒内没拿到输入框焦点 → 自动退出打字模式（避免卡住操作）")
            pcall(function() uiDiag("焦点看门狗触发") end)
            closeInput()
            closeUI()
            return
        end
        local txt = nil
        pcall(function() txt = SELF.inputField.text end)
        if type(txt) ~= "nil" then txt = tostring(txt) else txt = inputText end
        if txt ~= inputText then
            inputText = txt
            renderView()
        end
        -- 键盘没给到输入框时的兜底退出键（有焦点时由插件报 cancel）
        if not focused and Input.GetKeyDown(KeyCode.Escape) then closeInput(); closeUI(); return end
        return
    end
    if Input.GetKeyDown(KeyCode.Escape) then closeInput(); return end
    local s = nil
    pcall(function() s = Input.inputString end)
    if type(s) == "nil" or s == "" then return end
    local i = 1
    while i <= #s do
        local b = string.byte(s, i)
        if b == 8 then
            local cut = utf8LastCharLen(inputText)
            inputText = string.sub(inputText, 1, math.max(0, #inputText - cut))
            i = i + 1
        elseif b == 13 or b == 10 then
            sendInput(); return
        elseif b == 27 then
            closeInput(); return
        elseif b < 32 then
            i = i + 1
        else
            local len = 1
            if b >= 240 then len = 4 elseif b >= 224 then len = 3 elseif b >= 192 then len = 2 end
            local piece = string.sub(s, i, i + len - 1)
            if #piece < len then piece = string.sub(s, i) end
            if #inputText + #piece <= 80 then inputText = inputText .. piece end
            i = i + len
        end
    end
    renderView()
end

-- ============================== 显示插件回复 ==============================
local function say(speakerPose, displayName, text, meta, bold)
    if type(text) == "nil" or text == "" then return end
    text = string.gsub(tostring(text), "[\r\n\t]", " ")
    displayName = string.gsub(tostring(displayName), "[\r\n\t]", " ")
    sayCount = sayCount + 1
    local team = Team.Blue
    if type(speakerPose) ~= "nil" and string.find(speakerPose, "red") ~= nil then team = Team.Red end
    -- ★ 第80轮：修复导出缺字——Lua 的 C locale 会把 UTF-8 里的 0x80~0x9F 当成控制字符，
    --   原来的 `[%c]+` / `%s%s+` 会吃掉/替换这些字节，导致"蔡依婷→蔡 婷"这类缺字。
    --   改为按字节安全清理：只去掉 ASCII 控制字符(0-31,127)，并把连续 ASCII 空格压缩成一个。
    text = tostring(text or "")
    do
        local cleaned = {}
        local lastSpace = false
        for i = 1, #text do
            local b = string.byte(text, i)
            if b == 32 then
                if not lastSpace then cleaned[#cleaned + 1] = " "; lastSpace = true end
            elseif b >= 32 and b ~= 127 then
                cleaned[#cleaned + 1] = string.sub(text, i, i)
                lastSpace = false
            end
        end
        text = table.concat(cleaned)
    end
    local body = displayName .. ": " .. text
    rememberOwn(text)
    rememberOwn(body)
    local rendered = actorTag(displayName, team) .. ": "
    if bold then rendered = rendered .. "<color=#FFE066><b>" .. text .. "</b></color>" else rendered = rendered .. text end
    pushLine(rendered, meta)
    print("[RFAiChat] 台词 " .. tostring(sayCount) .. " → " .. body)
end

-- 手工按制表符切分，最多切出 n 段（第 n 段含剩余全部内容）。
-- 为什么不用模式匹配："([^	]*)	..." 这种写法在 MoonSharp 里对**长文本**会抛
-- "pattern too complex"（正则回溯有上限，实测报错），手工扫描则永远安全。
local function splitTabs(s, n)
    local parts = {}
    local start = 1
    local i = 1
    while i <= #s and #parts < n - 1 do
        if string.byte(s, i) == 9 then
            parts[#parts + 1] = string.sub(s, start, i - 1)
            start = i + 1
        end
        i = i + 1
    end
    parts[#parts + 1] = string.sub(s, start)
    return parts
end

-- ★ 第50轮：接收插件推来的人设分配设置（CFG|r1=..;r2=..;r3=..;first=..;every=..）
local cfgRequested = false
local cfgFromPlugin = false
local function applyCfgFromPlugin(payload)
    local vals = {}
    for k, v in string.gmatch(payload, "([%a%d]+)=(%d+)") do vals[k] = tonumber(v) end
    if vals.r1 and vals.r2 and vals.r3 and vals.r1 < vals.r2 and vals.r2 < vals.r3 then
        CFG.ratio1, CFG.ratio2, CFG.ratio3 = vals.r1, vals.r2, vals.r3
    end
    if vals.first and vals.first >= 5 then CFG.firstAssignAt = vals.first end
    if vals.every and vals.every >= 10 then CFG.reassignEvery = vals.every end
    if vals.report and vals.report >= 10 then CFG.reportEvery = vals.report end
    if vals.overlay == "1" then
        CFG.planB = true
        print("[RFAiChat] C# 显示层已接管聊天文本（Lua 停止视觉写入，仅保留历史/协议）")
    end
    cfgFromPlugin = true
    print("[RFAiChat] 人设设置已更新：比例 " .. CFG.ratio1 .. "/" .. CFG.ratio2 .. "/" .. CFG.ratio3
        .. "，首次 " .. CFG.firstAssignAt .. "s，间隔 " .. CFG.reassignEvery .. "s，战报间隔 " .. CFG.reportEvery .. "s")
end

local function drainReplies()
    if bridgeOK == false then return end
    if type(AiChatBridge) == "nil" then return end
    local s = AiChatBridge.Poll()
    if type(s) == "nil" then return end
    s = tostring(s)
    if s == "" or s == "[]" then return end
    -- ★ 先把"记录内部"的换行收拾掉：记录之间是用 \n 分隔的，
    --   所以台词正文里一旦带换行（模型偶尔输出多行），就会被误切成两条 → 第二条报
    --   `无法解析回复行: xxx`。这里做"合并续行"：只有新行像一条完整记录
    --   （含 ≥3 个制表符）才算新记录，否则并回上一条。
    local merged = {}
    for raw in string.gmatch(s, "[^\n]+") do
        local tabs = 0
        for i = 1, #raw do
            if string.byte(raw, i) == 9 then tabs = tabs + 1 end
        end
        if #merged > 0 and tabs < 3 then
            merged[#merged] = merged[#merged] .. " " .. raw
        else
            merged[#merged + 1] = raw
        end
    end
    for k = 1, #merged do
        local line = string.gsub(merged[k], "^ +", "")
        if line ~= "" and line ~= "[]" then
            -- 5 段：名字 <TAB> 姿态 <TAB> 风格 <TAB> 正文 <TAB> 调试信息
            local parts = splitTabs(line, 5)
            local name = parts[1]
            local pose = parts[2]
            local style = parts[3]
            local text = parts[4]
            local meta = parts[5] or ""
            if type(name) ~= "nil" and name ~= "" and type(text) ~= "nil" then
                if type(text) == "string" and string.sub(text, 1, 4) == "CFG|" then
                    applyCfgFromPlugin(text)
                else
                    local bold = false
                    if string.find(meta, "playerInvolved=true", 1, true) ~= nil then bold = true end
                    say(pose, name, text, meta, bold)
                end
            elseif string.find(line, "=", 1, true) == nil then
                print("[RFAiChat] 无法解析回复行: " .. line)
            end
        end
    end
end

-- ============================== 人设分配 ==============================
local function assignPersonas(reason)
    refreshRoster()
    local pool = {}
    for i = 1, #bots do
        local r = bots[i]
        if isBot(r.actor) and not r.isPlayer then pool[#pool + 1] = r end
    end
    local n = #pool
    if n == 0 then
        print("[RFAiChat] 人设分配：当前无可分档的 AI 士兵")
        return 0
    end
    table.sort(pool, function(x, y)
        local kx = x.kills / math.max(1, x.deaths)
        local ky = y.kills / math.max(1, y.deaths)
        if kx == ky then return x.kills > y.kills end
        return kx > ky
    end)
    local changed = 0
    local cntElite, cntVet, cntNorm, cntBeg = 0, 0, 0, 0
    for i = 1, n do
        local pct = i * 100 / n
        local skill = "Beginner"
        if pct <= CFG.ratio1 then skill = "Elite"
        elseif pct <= CFG.ratio2 then skill = "Veteran"
        elseif pct <= CFG.ratio3 then skill = "Normal" end
        if skill == "Elite" then cntElite = cntElite + 1
        elseif skill == "Veteran" then cntVet = cntVet + 1
        elseif skill == "Normal" then cntNorm = cntNorm + 1
        else cntBeg = cntBeg + 1 end
        if setSkill(pool[i], skill) then changed = changed + 1 end
    end
    print("[RFAiChat] 人设分配(" .. tostring(reason) .. ") " .. n .. " 人：王牌 " .. cntElite
        .. " / 精锐 " .. cntVet .. " / 老兵 " .. cntNorm .. " / 新兵 " .. cntBeg
        .. "，变更 " .. changed .. " 人")
    for i = 1, n do
        pushRaw({ { "t", "persona" }, { "name", pool[i].name }, { "team", pool[i].team }, { "skill", pool[i].skill } })
    end
    return n
end

-- ============================== 事件 ==============================
function RFAiChat:Start()
    SELF = self
    startedAt = now()
    nextIdleChat = startedAt + math.random(CFG.idleChatMin, CFG.idleChatMax)

    readConfig(self)
    -- 官方 UI（Unity 里做的 InputField/Button）：布局在 Unity 内完成，脚本不再手工摆位
    local hasUnityUI = false
    pcall(function() hasUnityUI = (type(self.targets) ~= "nil" and type(self.targets.input) ~= "nil") end)
    if hasUnityUI then
        self.useUnityUI = true
        pcall(function() self.text = self.targets.text.GetComponent(Text) end)
        pcall(function() self.inputField = self.targets.input.GetComponent(InputField) end)

        -- ============================================================
        -- ★★ 根因修复（2026-10-16 实机确证）★★
        --   以前这里写的是 `self.targets.xxx.asRectTransform` —— 取到的是 nil！
        --   原因：targets 里给的是 **GameObject**（实机日志：ChatBox (UnityEngine.GameObject)），
        --   而 `asRectTransform` 是 **Transform** 上的属性，GameObject 上根本没有。
        --   于是 `r = nil` → 下一行 `r.sizeDelta` 在 nil 上取字段 → 抛异常 →
        --   被 pcall 吞掉（连错误都没打出来）→ **后面所有校正一行都没执行**，
        --   包里那套算错的值（框中心=16、Content 高 400）就一直原样生效。
        --   这就是"每次改都跟没改一样"的真正原因。
        --   正确取法（两条都通）：
        --     ① go.GetComponent(RectTransform)      —— GameObject 上确实有 GetComponent
        --     ② go.transform.asRectTransform        —— 先拿 Transform，再取 RectTransform
        --   ★ 并且：pcall 必须 [返回值 + 错误] 两个都接，否则失败是静默的。
        -- ============================================================
        -- 拿到一个"像 RectTransform"的对象才算数：必须能读到 sizeDelta / anchoredPosition
        -- ★ MoonSharp 安全写法：绝不用 == nil 去比"代理 userdata"（会抛 argument 'rhs' is nil）
        local function looksLikeRT(o)
            if type(o) == "nil" then return false end
            local v = nil
            pcall(function() v = o.sizeDelta end)
            if type(v) == "nil" then pcall(function() v = o.anchoredPosition end) end
            return type(v) ~= "nil"
        end

        local function rtOf(go)
            if type(go) == "nil" then return nil end
            local rt = nil
            -- ① go.transform.asRectTransform —— 这条最稳：GameObjectProxy 有 .transform（TransformProxy），
            --    而 asRectTransform 正是 Transform 上的属性（实机日志已确证 GameObject 上没有）
            pcall(function() rt = go.transform.asRectTransform end)
            -- ② 退路：GameObject.GetComponent(RectTransform)（RectTransform 是注册过的全局类型，也能用）
            if not looksLikeRT(rt) then
                pcall(function() rt = go.GetComponent(RectTransform) end)
            end
            if not looksLikeRT(rt) then
                print("[RFAiChat][诊断-rect] 取不到可用的 RectTransform：go=" .. tostring(go)
                    .. " 拿到=" .. tostring(rt))
                return nil
            end
            return rt
        end
        self.rtOf = rtOf

        -- ★ 按 GPT 建议：官方 UI 路径下 Lua 【绝不修改任何 RectTransform】——布局唯一控制者 = Unity 预制体
        --   这里只做"只读实测"，把真实矩形打进日志，便于用数据核对
        do
            local t = self.targets.text
            pcall(function()
                local rt = t.transform.asRectTransform
                local p2 = rt.position
                local sd = rt.sizeDelta
                print(string.format("[RFAiChat][UI实测] ChatText 中心(%.0f,%.0f) 尺寸 %.0fx%.0f  pivot=(%.2f,%.2f)",
                    p2.x, p2.y, sd.x, sd.y, rt.pivot.x, rt.pivot.y))
            end)
        end

-- 聊天框：锚到屏幕左中，中心 = 左边距 + 框宽/2
        -- （生成器写的是 16 = 框中心到左边缘，等于把 560 宽的框推出屏幕 264px）
        local boxRT = rtOf(self.targets.scroll)
        local boxW = 560
        if type(boxRT) ~= "nil" then
            local okS, errS = pcall(function()
                boxW = boxRT.sizeDelta.x
                if type(boxW) == "nil" or boxW < 10 then boxW = 560 end
                -- ★ 让设置界面里的"聊天框宽度比例 / 高度倍数"在官方 UI 路径下也生效
                boxW = boxW * (CONF.boxWidth or 1.0)
                local hFactory = (CONF.boxHeight or 1.0)
                -- ★ 第 14 轮：宽度写回改为【无条件执行】（原来被 if hFactory ~= 1.0 挡住，
                --   导致按比例缩好的宽度从不写回，只有中心位置用缩放值算 → 表现为"把窗口往屏幕外挪"）
                pcall(function()
                    local sd = boxRT.sizeDelta
                    boxRT.sizeDelta = Vector2(boxW, sd.y * hFactory)
                end)
                -- 写回后校验（读回实际宽度，不符即告警）
                pcall(function()
                    local realW = boxRT.sizeDelta.x
                    local dh = boxRT.sizeDelta.y
                    if math.abs(realW - boxW) > 1 then
                        print("[RFAiChat] ⚠️ 宽度写回未生效：期望 " .. tostring(boxW) .. " 实际 " .. tostring(realW))
                    else
                        print(string.format("[RFAiChat] 聊天框尺寸已写回：宽=%.0f 高=%.0f（比例 %.2f / 高倍 %.2f / 边距 %.0f）",
                            realW, dh, CONF.boxWidth or 1.0, hFactory, CONF.leftMargin or 0))
                    end
                end)
                self.chatBoxW = boxW
                boxRT.anchorMin = Vector2(0, 0.5)
                boxRT.anchorMax = Vector2(0, 0.5)
                boxRT.pivot = Vector2(0.5, 0.5)
                boxRT.anchoredPosition = Vector2(16 + (CONF.leftMargin or 0) + boxW / 2, 0)
            end)
            local nx = nil
            pcall(function() nx = boxRT.anchoredPosition.x end)
            print("[RFAiChat] 聊天框定位：ok=" .. tostring(okS) .. " err=" .. tostring(errS)
                .. " 框宽=" .. tostring(boxW) .. " 写后中心x=" .. tostring(nx)
                .. " 期望=" .. tostring(16 + (CONF.leftMargin or 0) + boxW / 2))
        else
            print("[RFAiChat] ❌ 聊天框定位失败：取不到 ChatBox 的 RectTransform（targets.scroll="
                .. tostring(self.targets.scroll) .. "）")
        end

        -- 登记给插件（焦点/命中判定只能在 C# 侧做）
        if type(AiChatBridge) ~= "nil" then
            pcall(function()
                AiChatBridge.SetTargets(self.targets.input, self.targets.scroll, self.targets.text)
            end)
            pcall(function() AiChatBridge.SetFontSize(CONF.fontSize or 16) end)
        end

        -- 真按钮 + 真输入框：直接挂脚本回调（不再靠手算坐标命中）
        pcall(function() hookUnityUI(self) end)
        hookUIReady = true

        -- 布局改完后立刻重算，否则本帧读到的 position 还是旧值
        if type(AiChatBridge) ~= "nil" then
            pcall(function() AiChatBridge.RebuildLayout() end)
        end
        pcall(function() refreshUIRects() end)

        -- ★ 第 13 轮：官方 UI 路径同样要置 uiReady，否则台词会走 Overlay 兜底（屏幕上那行大字）
        if type(self.text) ~= "nil" then
            uiReady = true
            print("[RFAiChat] 聊天框 UI 已就绪（官方 UI 路径，ChatText 已绑定）")
        else
            print("[RFAiChat] 警告：官方 UI 路径下 ChatText 为空 → 台词将走 Overlay 兜底")
        end
        print("[RFAiChat] 检测到官方 UI（InputField/Button）→ 使用 Unity 布局，跳过手工布局")
    else
        pcall(function() initChatUI(self) end)
    end

    local me = "PLAYER"
    pcall(function() me = Player.actor.name end)
    print("[RFAiChat] 脚本启动（Chat Feed 基座版） 玩家=" .. me .. " 时间=" .. tostring(startedAt))

    -- 把设置与玩家名同步给插件
    pushRaw({ { "t", "cfg" }, { "lang", CONF.lang }, { "freq", CONF.freq },
              { "apiUrl", CONF.apiUrl }, { "apiKey", CONF.apiKey }, { "model", CONF.model }, { "enable", tostring(CONF.enable) }, { "player", me } })

    GameEvents.onActorSpawn.AddListener(self, "onActorSpawn")
    GameEvents.onActorCreated.AddListener(self, "onActorCreated")
    GameEvents.onActorDiedInfo.AddListener(self, "onActorDied")
    GameEvents.onCapturePointCaptured.AddListener(self, "onCapCaptured")
    GameEvents.onCapturePointNeutralized.AddListener(self, "onCapNeutralized")
    GameEvents.onSquadAssignedNewOrder.AddListener(self, "onSquadOrder")
    GameEvents.onOverlayText.AddListener(self, "onOverlayText")
    GameEvents.onMatchEnd.AddListener(self, "onMatchEnd")
    GameEvents.onVehicleDestroyed.AddListener(self, "onVehicleDestroyed")
end

function RFAiChat:onActorCreated(actor)
    local r = rec(actor)
    -- ★ 第50轮：中途出生/新增的 AI 不单独计算档位，等大部队的下一次周期分配
end

function RFAiChat:onActorSpawn(actor)
    local r = rec(actor)
    if type(r) == "nil" then return end
    -- ★ 第50轮：同上，出生时不再单独 setSkill
    pushRaw({ { "t", "spawn" }, { "name", r.name }, { "team", r.team },
              { "bot", tostring(isBot(actor)) } })
end

function RFAiChat:onActorDied(actor, info, isSilentKill)
    if isSilentKill == true then return end
    if type(info) == "nil" then return end

    local victim = rec(actor)
    local killer = nil
    local okk, ka = pcall(function() return info.sourceActor end)
    if okk and type(ka) ~= "nil" then killer = rec(ka) end

    if type(victim) ~= "nil" then victim.deaths = victim.deaths + 1 end
    if type(killer) ~= "nil" and killer ~= victim then killer.kills = killer.kills + 1 end

    local sameTeam = false
    if type(victim) ~= "nil" and type(killer) ~= "nil" and killer ~= victim then
        sameTeam = (victim.team == killer.team)
        if sameTeam then killer.teamkills = killer.teamkills + 1 end
    end

    local dtype, weapon, headshot, splash, piercing = "Unknown", "?", "false", "false", "false"
    pcall(function() dtype = tostring(info.type) end)
    pcall(function() headshot = tostring(info.isCriticalHit) end)
    pcall(function() splash = tostring(info.isSplashDamage) end)
    pcall(function() piercing = tostring(info.isPiercing) end)
    pcall(function()
        local we = info.sourceWeaponEntry
        if type(we) ~= "nil" and type(we.name) ~= "nil" then weapon = tostring(we.name) end
    end)

    local dist = -1
    pcall(function()
        local p = Player.actor
        if type(p) ~= "nil" and type(actor) ~= "nil" then dist = math.floor(ActorManager.ActorsDistance(actor, p)) end
    end)


    -- 玩家相关（需求：玩家击杀/被击杀必须立即且有反馈）
    local pVictim = "false"
    local pKiller = "false"
    if type(victim) ~= "nil" and victim.isPlayer then pVictim = "true" end
    if type(killer) ~= "nil" and killer.isPlayer then pKiller = "true" end

    -- ★ v1.1.3：玩家击杀 AI 时，记录玩家当时乘坐的载具（供 AI 台词使用）
    local killveh = ""
    if pKiller == "true" then
        pcall(function()
            local pa = Player.actor
            if type(pa) ~= "nil" then
                local v = pa.activeVehicle
                if type(v) ~= "nil" and type(v.name) ~= "nil" then
                    killveh = tostring(v.name)
                end
            end
        end)
    end
    -- 玩家阵亡时自动收起输入框（否则死后鼠标还解锁着、操作被屏蔽）
    if pVictim == "true" and inputMode then closeInput() end

    -- 需求 8：玩家在极短时间内连杀（火炮/手雷一次炸死多人）→ 单独发一条"多杀"事件
    if pKiller == "true" then
        if (now() - burst.t0) > 2.5 then
            burst.count = 0
            burst.t0 = now()
            burst.fired = false
        end
        burst.count = burst.count + 1
        if burst.count >= 5 and not burst.fired then
            burst.fired = true
            local pname = "玩家"
            pcall(function() pname = Player.actor.name end)
            pushRaw({ { "t", "multi" }, { "killer", pname }, { "count", burst.count }, { "weapon", weapon } })
            print("[RFAiChat] 检测到玩家多杀：" .. tostring(burst.count) .. " 人（" .. weapon .. "）")
        end
    end

    pushRaw({
        { "t", "death" },
        { "victim", victim and victim.name or "?" },
        { "vteam", victim and victim.team or "Blue" },
        { "vbot", tostring(type(victim) ~= "nil" and not victim.isPlayer) },
        { "killername", killer and killer.name or "" },
        { "kteam", killer and killer.team or "" },
        { "kbot", tostring(type(killer) ~= "nil" and not killer.isPlayer) },
        { "sameTeam", tostring(sameTeam) },
        { "pvictim", pVictim }, { "pkiller", pKiller },
        { "weapon", weapon }, { "dtype", dtype },
        { "headshot", headshot }, { "splash", splash }, { "piercing", piercing },
        { "dist", dist }, { "killveh", killveh },
        { "vkills", victim and victim.kills or 0 }, { "vdeaths", victim and victim.deaths or 0 },
        { "kkills", killer and killer.kills or 0 }, { "kdeaths", killer and killer.deaths or 0 },
    })
end

function RFAiChat:onCapCaptured(point, newOwner)
    local pname = "?"
    pcall(function() pname = tostring(point.name) end)
    push({ { "t", "cap" }, { "point", pname }, { "team", teamName(newOwner) }, { "event", "captured" } })
end

function RFAiChat:onCapNeutralized(point, prevOwner)
    local pname = "?"
    pcall(function() pname = tostring(point.name) end)
    push({ { "t", "cap" }, { "point", pname }, { "team", teamName(prevOwner) }, { "event", "neutralized" } })
end

function RFAiChat:onSquadOrder(squad, order)
    local oname = "?"
    pcall(function() oname = tostring(order) end)
    push({ { "t", "order" }, { "order", oname } })
end

function RFAiChat:onOverlayText(text, showDuration)
    if type(text) == "nil" or text == "" then return end
    if isOwnText(text) then return end
    if (now() - lastRadio) < CFG.radioMinGap then return end
    lastRadio = now()
    push({ { "t", "radio" }, { "text", text }, { "dur", showDuration } })
end

function RFAiChat:onMatchEnd(winner)
    if inputMode then closeInput() end            -- 对局结束自动收起输入框（恢复鼠标与操作）
    push({ { "t", "match" }, { "state", "end" }, { "winner", teamName(winner) } })
    exportHistory("对局结束自动导出")
end

function RFAiChat:onVehicleDestroyed(vehicle, info)
    local vname = "?"
    pcall(function() vname = tostring(vehicle.name) end)
    push({ { "t", "vehicle" }, { "name", vname } })
end

-- ============================== 自测（正式版会去掉） ==============================
local function testTeamKill()
    local me = "PLAYER"
    pcall(function() me = Player.actor.name end)
    local mate = nil
    for i = 1, #bots do
        local r = bots[i]
        if r.name ~= me and not r.isPlayer and r.team == "Blue" then mate = r.name; break end
    end
    if type(mate) == "nil" then
        print("[RFAiChat] 自测：还没记录到同队士兵，先打一会儿再按 F10")
        return
    end
    pushRaw({
        { "t", "death" }, { "victim", me }, { "vteam", "Blue" }, { "vbot", "false" },
        { "killername", mate }, { "kteam", "Blue" }, { "kbot", "true" },
        { "sameTeam", "true" }, { "pvictim", "true" }, { "pkiller", "false" },
        { "weapon", "友军误伤·自测" }, { "dtype", "Projectile" },
        { "headshot", "false" }, { "splash", "false" }, { "piercing", "false" }, { "dist", "3" },
        { "vkills", "0" }, { "vdeaths", "1" }, { "kkills", "0" }, { "kdeaths", "0" },
    })
    print("[RFAiChat] 自测：已伪造一次友军误伤（凶手=" .. mate .. "）")
end

-- ============================== 主循环 ==============================
-- ============================================================================
-- [第2项] P1 分层诊断：只打印事实，不做任何修改
--   目的：确认"文字看起来在框外"到底是
--     A 框太淡/文字其实在框内上部
--     B 文字属于另一个对象
--     C 被遮挡 / 被裁剪 / 未激活
--   本段代码【不写任何 transform、不写任何组件属性】，只读 + 打印。
-- ============================================================================
local diagDone = false
local diagFrames = 0

local function diagRT(rt)
    if type(rt) == "nil" then return "RectTransform=不存在" end
    local s = "RectTransform=存在"
    pcall(function()
        local pos, r = rt.position, rt.rect
        s = s .. string.format(" pos=(%.0f,%.0f) rect=(%.1fx%.1f) sizeDelta=(%.1f,%.1f) pivot=(%.2f,%.2f) anchorMin=(%.2f,%.2f) anchorMax=(%.2f,%.2f)",
            pos.x, pos.y, r.width, r.height, rt.sizeDelta.x, rt.sizeDelta.y,
            rt.pivot.x, rt.pivot.y, rt.anchorMin.x, rt.anchorMin.y, rt.anchorMax.x, rt.anchorMax.y)
    end)
    pcall(function()
        local c = rt.rect  -- 世界四角（若可用）
        s = s .. ""
    end)
    return s
end

local function diagComponents(go)
    local list = {}
    pcall(function() if type(go.GetComponent(CanvasRenderer)) ~= "nil" then list[#list + 1] = "CanvasRenderer" end end)
    pcall(function() if type(go.GetComponent(Canvas)) ~= "nil" then list[#list + 1] = "Canvas" end end)
    pcall(function() if type(go.GetComponent(Mask)) ~= "nil" then list[#list + 1] = "Mask" end end)
    pcall(function() if type(go.GetComponent(RectMask2D)) ~= "nil" then list[#list + 1] = "RectMask2D" end end)
    pcall(function() if type(go.GetComponent(Text)) ~= "nil" then list[#list + 1] = "Text" end end)
    pcall(function() if type(go.GetComponent(ScrollRect)) ~= "nil" then list[#list + 1] = "ScrollRect" end end)
    if #list == 0 then return "（无相关组件）" end
    return table.concat(list, ",")
end

local function diagRun()
    print("[RFAiChat][分层诊断] ================ 开始 ================")
    if SELF == nil or type(SELF.text) == "nil" then
        print("[RFAiChat][分层诊断] ❌ SELF.text 为空，无法诊断")
        return
    end
    -- ① 自内向外打印层级路径
    local node = nil
    pcall(function() node = SELF.text.transform end)
    local depth = 0
    local textGO = nil
    pcall(function() textGO = SELF.text.gameObject end)
    if textGO ~= nil then
        print(string.format("[RFAiChat][分层诊断] ChatText: activeSelf=%s activeInHierarchy=%s 组件={%s}",
            tostring(textGO.activeSelf), tostring(textGO.activeInHierarchy), diagComponents(textGO)))
    end
    print("[RFAiChat][分层诊断] 层级（由内向外，含每层的 active 与 RectTransform 实测）：")
    while node ~= nil and depth < 8 do
        local nm, act, acth = "?", "?", "?"
        pcall(function() nm = tostring(node.name) end)
        pcall(function()
            act = tostring(node.gameObject.activeSelf)
            acth = tostring(node.gameObject.activeInHierarchy)
        end)
        local rt = nil
        pcall(function() rt = node.asRectTransform end)
        local go = nil
        pcall(function() go = node.gameObject end)
        print(string.format("     [%d] %s  activeSelf=%s activeInHierarchy=%s 组件={%s} ｜ %s",
            depth, nm, act, acth, (go ~= nil and diagComponents(go) or "?"), diagRT(rt)))
        pcall(function() node = node.parent end)
        depth = depth + 1
    end
    -- ② Canvas 与排序层级
    do
        local c = nil
        pcall(function() c = SELF.text.GetComponentInParent(Canvas) end)
        if type(c) == "nil" then
            print("[RFAiChat][分层诊断] Canvas = 不存在（向上找不到 Canvas）")
        else
            local info = {}
            pcall(function() info[#info + 1] = "renderMode=" .. tostring(c.renderMode) end)
            pcall(function() info[#info + 1] = "sortingOrder=" .. tostring(c.sortingOrder) end)
            pcall(function() info[#info + 1] = "overrideSorting=" .. tostring(c.overrideSorting) end)
            pcall(function() info[#info + 1] = "sortingLayerName=" .. tostring(c.sortingLayerName) end)
            pcall(function() info[#info + 1] = "scaleFactor=" .. string.format("%.2f", c.scaleFactor) end)
            print("[RFAiChat][分层诊断] Canvas: " .. table.concat(info, " "))
        end
    end
    -- ③ Viewport / Content / ChatText 三个矩形 + Mask 状态
    local function reportOne(label, go)
        if type(go) == "nil" then
            print("[RFAiChat][分层诊断] " .. label .. " = 不存在")
            return
        end
        local rt = nil
        pcall(function() rt = go.transform.asRectTransform end)
        local comp = {}
        pcall(function() if type(go.GetComponent(Mask)) ~= "nil" then
            local m = go.GetComponent(Mask)
            comp[#comp + 1] = "Mask(showMaskGraphic=" .. tostring(m.showMaskGraphic) .. ")"
        end end)
        pcall(function() if type(go.GetComponent(RectMask2D)) ~= "nil" then
            comp[#comp + 1] = "RectMask2D"
        end end)
        pcall(function() if type(go.GetComponent(CanvasRenderer)) ~= "nil" then
            comp[#comp + 1] = "CanvasRenderer"
        end end)
        print(string.format("[RFAiChat][分层诊断] %s: activeSelf=%s activeInHierarchy=%s 组件={%s} ｜ %s",
            label, tostring(go.activeSelf), tostring(go.activeInHierarchy),
            (#comp > 0 and table.concat(comp, ",") or "无 Mask/RectMask2D/CanvasRenderer"),
            diagRT(rt)))
    end
    -- 从 ChatText 往上找 Viewport / Content（按名字）
    local up = nil
    pcall(function() up = SELF.text.transform end)
    local guard = 0
    local found = {}
    while up ~= nil and guard < 8 do
        local nm = "?"
        pcall(function() nm = tostring(up.name) end)
        if nm == "Content" then pcall(function() found.content = up.gameObject end) end
        if nm == "Viewport" then pcall(function() found.viewport = up.gameObject end) end
        if nm == "ChatBox" then pcall(function() found.chatbox = up.gameObject end) end
        pcall(function() up = up.parent end)
        guard = guard + 1
    end
    reportOne("ChatBox（聊天框）", found.chatbox)
    reportOne("Viewport（遮罩区）", found.viewport)
    reportOne("Content（内容区）", found.content)
    if found.chatbox == nil then print("[RFAiChat][分层诊断] 注意：向上 8 层内没找到名为 ChatBox 的对象（名字可能与预期不同）") end
    if found.viewport == nil then print("[RFAiChat][分层诊断] 注意：向上 8 层内没找到名为 Viewport 的对象（Mask 可能在别的对象上）") end
    if found.content == nil then print("[RFAiChat][分层诊断] 注意：向上 8 层内没找到名为 Content 的对象") end
    -- ④ 文字自身属性（只读）
    do
        local t = SELF.text
        local info = {}
        pcall(function() info[#info + 1] = "text长度=" .. tostring(#tostring(t.text or "")) end)
        pcall(function() info[#info + 1] = "fontSize=" .. tostring(t.fontSize) end)
        pcall(function() info[#info + 1] = "alignment=" .. tostring(t.alignment) end)
        pcall(function() info[#info + 1] = "color=" .. string.format("%.2f/%.2f/%.2f/%.2f", t.color.r, t.color.g, t.color.b, t.color.a) end)
        pcall(function() info[#info + 1] = "font=" .. tostring(type(t.font) ~= "nil") end)
        pcall(function() info[#info + 1] = "raycastTarget=" .. tostring(t.raycastTarget) end)
        pcall(function() info[#info + 1] = "preferredHeight=" .. string.format("%.1f", t.preferredHeight) end)
        pcall(function() info[#info + 1] = "horizontalOverflow=" .. tostring(t.horizontalOverflow) end)
        pcall(function() info[#info + 1] = "verticalOverflow=" .. tostring(t.verticalOverflow) end)
        print("[RFAiChat][分层诊断] ChatText 属性: " .. table.concat(info, " "))
        local head = ""
        pcall(function() head = string.sub(tostring(t.text or ""), 1, 40) end)
        print("[RFAiChat][分层诊断] ChatText 前 40 字: " .. head)
    end
    -- ⑤ 屏幕信息
    do
        local info = {}
        pcall(function() info[#info + 1] = "Screen=" .. tostring(Screen.width) .. "x" .. tostring(Screen.height) end)
        pcall(function() info[#info + 1] = "CANVAS_SCALE=" .. tostring(CANVAS_SCALE) end)
        pcall(function() info[#info + 1] = "BOX_LEFT_PX=" .. tostring(BOX_LEFT_PX) .. " BOX_TOP_PX=" .. tostring(BOX_TOP_PX) .. " SCALE_PX=" .. tostring(SCALE_PX) end)
        print("[RFAiChat][分层诊断] 屏幕/换算: " .. table.concat(info, " "))
    end
    print("[RFAiChat][分层诊断] ================ 结束 ================")
end

local function diagTick()
    if diagDone then return end
    diagFrames = diagFrames + 1
    if diagFrames >= 180 then        -- 约 3 秒后（等 UI 完全建好、文字已写入）
        diagDone = true
        pcall(diagRun)
    end
end

function RFAiChat:Update()
    local t = now()
    -- ★ 第 13 轮任务①补充②：延迟读回 —— 写入后 2 秒再读一次，侦测是否有 hook 在清文本
    if type(SELF) ~= "nil" and type(SELF.text) ~= "nil" then
        local lenNow = 0
        pcall(function() lenNow = #tostring(SELF.text.text or "") end)
        if lenNow > 0 then
            if type(DELAY_LEN) == "nil" then
                DELAY_LEN = lenNow; DELAY_AT = t
            elseif t - DELAY_AT >= 2.0 then
                local len2 = 0
                pcall(function() len2 = #tostring(SELF.text.text or "") end)
                print("[RFAiChat][延迟读回] 2秒前=" .. tostring(DELAY_LEN) .. " 现在=" .. tostring(len2)
                    .. " " .. (len2 == 0 and "⚠️ 被清空了！有 hook 在清文本" or "(未被清空)"))
                DELAY_LEN = lenNow; DELAY_AT = t
            end
        end
    end
    pcall(diagTick)          -- [第2项] 分层诊断：只读打印，3 秒后执行一次

    -- 鼠标模式：每帧解锁鼠标（游戏会不断锁回去，只解一次会被覆盖）
    if uiMode then pcall(function() Screen.UnlockCursor() end) end

    -- 悬停/按下高亮（按钮反馈）
    if type(AiChatBridge) ~= "nil" then
        local okm, mv = pcall(function() return AiChatBridge.MousePos() end)
        if okm and type(mv) ~= "nil" and tostring(mv) ~= "" then
            local hx, hy = string.match(tostring(mv), "^(%-?%d+),(%-?%d+)$")
            hx = tonumber(hx); hy = tonumber(hy)
            if type(hx) ~= "nil" then
                local ha = hitTest(hx, hy)
                local hb = nil
                if ha == "send" or ha == "debug" or ha == "closedebug" or ha == "report"
                   or ha == "export" or ha == "back" then hb = ha end
                if hb ~= hoverBtn then
                    hoverBtn = hb
                    renderView()
                end
            end
        end
    end
    if type(pressedBtn) ~= "nil" and t > pressedUntil then
        pressedBtn = nil
        renderView()
    end

    -- 点击检测：由插件采集，脚本只做命中判定
    -- ★ 官方 UI 下这里【只更新高亮】，不重复执行动作 —— 动作已由 Unity Button.onClick 回调触发，
    --   两边都执行会导致"点一次发两条"。
    if type(AiChatBridge) ~= "nil" then
        local okc, cv = pcall(function() return AiChatBridge.ClickPoll() end)
        if okc and type(cv) ~= "nil" and tostring(cv) ~= "" then
            local mx, my = string.match(tostring(cv), "^(%-?%d+),(%-?%d+)$")
            mx = tonumber(mx); my = tonumber(my)
            if type(mx) ~= "nil" and type(my) ~= "nil" then
                local act = hitTest(mx, my)
                print(string.format("[RFAiChat] 点击 (%.0f,%.0f) → %s", mx, my, tostring(act)))
                if type(act) ~= "nil" then
                    pressedBtn = act
                    pressedUntil = t + 0.15
                    -- 官 方 UI：若该动作的 Unity 回调已挂上，就只更新高亮（避免点一次动两次）；
                    -- 没挂上（例如代理不支持 AddListener）则退回脚本执行，保证按钮一定有用。
                    if type(SELF) ~= "nil" and SELF.useUnityUI == true and isHooked(act) then
                        renderView()
                    else
                        doAction(act)
                    end
                end
            end
        end
    end

    if inputMode then
        -- 官方 UI：键盘（含 Esc / 回车）都交给真 InputField，退出统一走 handleInput 的 cancel 分支，
        -- 这里不能再用 Input.GetKeyDown 抢按键，否则会和输入框抢同一个 Esc。
        if type(SELF) == "nil" or SELF.useUnityUI ~= true then
            -- Esc：先退打字；再按一次退鼠标模式（双出口，避免被输入法吞掉按键而卡死）
            if Input.GetKeyDown(KeyCode.Escape) then
                closeInput()
                closeUI()
            end
        end
        if inputMode then handleInput() end
    else
        if chatKeyDown() then
            if not uiMode then openUI() end      -- ★ Y 只打开，不再"再按一次返回"
        elseif uiMode and Input.GetKeyDown(KeyCode.Escape) then
            closeUI()
        end
        local okScroll, d = pcall(function() return Input.mouseScrollDelta.y end)
        if okScroll and type(d) ~= "nil" and d ~= 0 then
            -- 官方 UI：滚动交给 ScrollRect（Unity 自己处理），脚本不动它
            if type(SELF) == "nil" or SELF.useUnityUI ~= true then
                local maxOff = math.max(0, #history - MAX_LINES)
                if d > 0 then viewOffset = math.min(viewOffset + 2, maxOff)
                else viewOffset = math.max(viewOffset - 2, 0) end
                renderView()
            end
        end
        -- F12：打印官方 UI 实测数据（布局/焦点排查用）
        local okF12, f12 = pcall(function() return Input.GetKeyDown(KeyCode.F12) end)
        if okF12 and f12 == true then
            pcall(function() uiDiag("手动 F12") end)
        end
    end

    -- ★ 第50轮：开局向插件请求一次人设分配设置
    if not cfgRequested and startedAt >= 0 and (t - startedAt) >= 1.0 then
        cfgRequested = true
        pcall(function() pushRaw({ { "t", "cfgq" } }) end)
    end

    -- 取回插件生成的回复
    if (t - lastPoll) >= CFG.pollInterval then
        lastPoll = t
        drainReplies()
    end

    -- 人设：开局第一次分配（池子里还没人时不算完成，过一会继续尝试）
    if startedAt >= 0 and lastAssign < 0 and (t - startedAt) >= CFG.firstAssignAt then
        local nFirst = assignPersonas("首次 " .. tostring(CFG.firstAssignAt) .. "s")
        if nFirst and nFirst > 0 then lastAssign = t end
    end
    -- 人设：按设置的间隔重新分配
    if lastAssign >= 0 and (t - lastAssign) >= CFG.reassignEvery then
        local nCycle = assignPersonas("周期 " .. tostring(CFG.reassignEvery) .. "s")
        if nCycle and nCycle > 0 then lastAssign = t end
    end

    -- 3 分钟战况播报
    if startedAt >= 0 and (t - startedAt) >= CFG.reportEvery and (t - lastReport) >= CFG.reportEvery then
        lastReport = t
        push({ { "t", "tick" }, { "kind", "report" } })
    end

    -- 随机闲聊
    if t >= nextIdleChat then
        nextIdleChat = t + math.random(CFG.idleChatMin, CFG.idleChatMax)
        push({ { "t", "tick" }, { "kind", "chat" } })
    end

    -- 心跳
    if (t - lastLog) >= CFG.logEvery then
        lastLog = t
        local s = "?"
        if type(AiChatBridge) ~= "nil" then
            local ok, v = pcall(function() return AiChatBridge.Status() end)
            if ok then s = tostring(v) end
        end
        print("[RFAiChat] 心跳 t=" .. string.format("%.0f", t - startedAt)
            .. " 士兵=" .. tostring(#bots) .. " 已推送=" .. tostring(pushCount)
            .. " 已发言=" .. tostring(sayCount) .. " 行数=" .. tostring(#history)
            .. " 插件=" .. s)
    end
end
