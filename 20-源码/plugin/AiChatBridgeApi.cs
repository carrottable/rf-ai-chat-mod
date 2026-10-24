// AiChatBridgeApi —— 暴露给 Ravenscript 的静态接口
// 脚本侧用法（Lua）：
//   AiChatBridge.Push('{"t":"death","victim":"Croc",...}')   -- 上报游戏事件
//   local replies = AiChatBridge.Poll()                       -- 取回待显示的回复（JSON 数组字符串）
//   local s = AiChatBridge.Status()                           -- 取回状态摘要（调试用）
//
// 官方 UI（Unity InputField/Button）专用接口（v1.7 新增）：
//   AiChatBridge.SetTargets(goInput, goScroll, goText)  -- 一次性登记三个 UI 物体
//   AiChatBridge.SetInputRect(x, y, w, h)               -- 登记输入框屏幕矩形（左上角 x,y）
//   AiChatBridge.FocusInput() / UnfocusInput()          -- 让真 InputField 获得/失去键盘焦点
//   AiChatBridge.InputFocused() / InputText()           -- 焦点状态 / 输入框里的字
//   AiChatBridge.InputEvent()                           -- 取走一次 submit / cancel
//   AiChatBridge.RebuildLayout()                        -- 让 uGUI 布局立刻生效（刷新世界坐标）
//   AiChatBridge.HitElement(x, y)                       -- 插件侧按【真矩形】做点中判定
//   AiChatBridge.DiagUI()                               -- 官方 UI 实测数据（排查布局用）
// 为什么焦点必须在 C# 里做：Ravenscript 的代理类型表是游戏启动时写死的 161 个，
// 里面【没有 EventSystem】，Lua 侧拿不到也无法 AddComponent，所以焦点只能由插件处理。
using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RF.AiChat
{
    public static class AiChatBridgeApi
    {
        private static readonly object Sync = new object();
        private static readonly List<string> Pending = new List<string>();
        private static int _pushCount;
        private static int _pollCount;
        private static string _lastPush = "(无)";

        /// <summary>脚本 → 插件：上报一条游戏事件（行协议字符串）。</summary>
        public static void Push(string json)
        {
            lock (Sync)
            {
                _pushCount++;
                _lastPush = json;
            }
            // 交给对话引擎处理（内部自行判类型）
            try
            {
                ChatEngine.OnEvent(json);
            }
            catch (Exception e)
            {
                AiChatPlugin.Log.LogError("[AiChat][通道] 事件转交引擎失败：" + e.GetType().Name + " : " + e.Message);
            }
        }

        private static float _clickX, _clickY;
        private static bool _clickPending;

        /// <summary>插件每帧调用：记录一次鼠标左键点击的屏幕坐标。</summary>
        public static void CaptureClick()
        {
            try
            {
                if (UnityEngine.Input.GetMouseButtonDown(0))
                {
                    UnityEngine.Vector3 p = UnityEngine.Input.mousePosition;
                    _clickX = p.x; _clickY = p.y;
                    _clickPending = true;
                }
            }
            catch (Exception) { }
        }

        /// <summary>脚本轮询：当前鼠标屏幕坐标 "x,y"。</summary>
        public static string MousePos()
        {
            try
            {
                UnityEngine.Vector3 p = UnityEngine.Input.mousePosition;
                return ((int)p.x) + "," + ((int)p.y);
            }
            catch (Exception) { return ""; }
        }

        /// <summary>脚本轮询：有点击则返回 "x,y"，否则返回空串（取走即清）。</summary>
        public static string ClickPoll()
        {
            if (!_clickPending) return "";
            _clickPending = false;
            return ((int)_clickX) + "," + ((int)_clickY);
        }

        /// <summary>插件 → 脚本：取回本轮待显示的回复。无内容时返回空字符串（不要返回 "[]"，Lua 侧会当成记录解析）。</summary>
        public static string Poll()
        {
            lock (Sync)
            {
                _pollCount++;
                if (Pending.Count == 0) return "";
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < Pending.Count; i++)
                {
                    if (i > 0) sb.Append('\n');
                    sb.Append(Pending[i]);
                }
                Pending.Clear();
                return sb.ToString();
            }
        }

        /// <summary>状态摘要（脚本侧调试用）。</summary>
        public static string Status()
        {
            lock (Sync)
            {
                return "push=" + _pushCount + ";poll=" + _pollCount + ";pending=" + Pending.Count
                    + ";last=" + Clip(_lastPush, 80);
            }
        }

        // ================================================================
        //  官方 UI（Unity InputField / Button）支持
        //  设计原则：位置一律由 Unity 决定，脚本只把「真矩形」报给插件；
        //            焦点由插件用 EventSystem 设置（Lua 拿不到 EventSystem）。
        // ================================================================

        private static InputField _inputField;
        private static RectTransform _scrollRT;
        private static RectTransform _textRT;
        private static string _targetsDiag = "(未登记)";

        private static bool _wantFocus;          // Lua 说要打字
        private static float _rectL, _rectT, _rectR, _rectB;
        private static bool _rectValid;
        private static bool _focusedNow;         // 插件真的把焦点给到输入框了吗
        private static int _focusTries;
        private static int _focusRequests;
        private static int _focusFail;
        private static string _lastFocusMsg = "(未尝试)";

        private static bool _submitPending;
        private static bool _cancelPending;

        /// <summary>
        /// 脚本启动时登记三个 UI 物体（只登记一次）。传 GameObject 而不是组件，
        /// 避免 Lua 侧的对象封送歧义。
        /// </summary>
        public static void SetTargets(GameObject goInput, GameObject goScroll, GameObject goText)
        {
            _inputField = null;
            _scrollRT = null;
            _textRT = null;
            try { if (goInput != null) _inputField = goInput.GetComponent<InputField>(); } catch (Exception) { }
            try { if (goScroll != null) _scrollRT = goScroll.GetComponent<RectTransform>(); } catch (Exception) { }
            try { if (goText != null) _textRT = goText.GetComponent<RectTransform>(); } catch (Exception) { }
            _targetsDiag = "input=" + (_inputField != null ? "ok" : "null")
                + " scroll=" + (_scrollRT != null ? "ok" : "null")
                + " text=" + (_textRT != null ? "ok" : "null");
            if (AiChatPlugin.Log != null)
                AiChatPlugin.Log.LogInfo("[AiChat][UI] 已登记官方 UI：" + _targetsDiag);
        }

        /// <summary>登记输入框的屏幕矩形：x,y = 左上角；w,h = 宽高（屏幕像素，y 向下）。</summary>
        public static void SetInputRect(float x, float y, float w, float h)
        {
            if (w <= 1f || h <= 1f) { _rectValid = false; return; }
            _rectL = x; _rectT = y; _rectR = x + w; _rectB = y + h;
            _rectValid = true;
        }

        /// <summary>脚本进入"打字模式"：允许插件把键盘焦点交给 InputField。</summary>
        public static void FocusInput()
        {
            _wantFocus = true;
            _focusTries = 0;
            _focusWait = 0;
            _focusConfirmed = false;
            _activateLogged = false;
        }

        /// <summary>脚本退出"打字模式"：撤销焦点并关掉输入法。</summary>
        public static void UnfocusInput()
        {
            _wantFocus = false;
            _focusedNow = false;
            _activateLogged = false;
            try
            {
                if (_inputField != null && _inputField.isFocused) _inputField.DeactivateInputField();
            }
            catch (Exception) { }
            try
            {
                EventSystem es = EventSystem.current;
                if (es != null && es.currentSelectedGameObject != null
                    && (_inputField == null || es.currentSelectedGameObject == _inputField.gameObject))
                {
                    es.SetSelectedGameObject(null);
                }
            }
            catch (Exception) { }
        }

        /// <summary>插件真的拿到键盘焦点了吗（Lua 用它决定是否接管按键）。</summary>
        public static bool InputFocused() { return _focusedNow; }

        /// <summary>当前是否处于"聊天输入"状态（打字中）：用于屏蔽游戏全局热键（BUG-104）。</summary>
        public static bool IsInputActive()
        {
            if (_wantFocus || _focusedNow) return true;
            try
            {
                EventSystem es = EventSystem.current;
                if (es == null) return false;
                GameObject sel = es.currentSelectedGameObject;
                if (sel == null) return false;
                if (_inputField != null && sel == _inputField.gameObject) return true;
                return sel.GetComponent<InputField>() != null;
            }
            catch (Exception) { return false; }
        }

        /// <summary>输入框里的文字（Lua 侧显示与发送都以它为准）。</summary>
        public static string InputText()
        {
            try { return _inputField != null ? (_inputField.text ?? "") : ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>把 Lua 侧设置的字号同步给真输入框。</summary>
        public static void SetFontSize(int size)
        {
            if (size <= 0) return;
            try
            {
                if (_inputField != null && _inputField.textComponent != null)
                    _inputField.textComponent.fontSize = size;
                if (_inputField != null && _inputField.placeholder is Text)
                    ((Text)_inputField.placeholder).fontSize = size;
            }
            catch (Exception) { }
        }

        /// <summary>让 uGUI 立刻重算布局（否则 position/世界坐标要等下一帧才是新的）。</summary>
        public static void RebuildLayout()
        {
            try { Canvas.ForceUpdateCanvases(); } catch (Exception) { }
            try { if (_scrollRT != null) LayoutRebuilder.ForceRebuildLayoutImmediate(_scrollRT); } catch (Exception) { }
        }

        /// <summary>
        /// 取走一次"回车/取消"事件（取走即清）。返回 "" / "submit" / "cancel"。
        /// </summary>
        public static string InputEvent()
        {
            if (_submitPending) { _submitPending = false; return "submit"; }
            if (_cancelPending) { _cancelPending = false; return "cancel"; }
            return "";
        }

        /// <summary>
        /// 插件侧点中判定：用【真 RectTransform 矩形】而不是自己推算的坐标。
        /// 返回 "input" / "btn_send" / "btn_debug" / "btn_report" / "btn_back" / ""。
        /// 注意：_inputField 为空时返回 ""。
        /// </summary>
        public static string HitElement(float x, float y)
        {
            try
            {
                if (_inputField != null && Contains(_inputField.transform as RectTransform, x, y)) return "input";
                if (_btnSend != null && Contains(_btnSend, x, y)) return "btn_send";
                if (_btnDebug != null && Contains(_btnDebug, x, y)) return "btn_debug";
                if (_btnReport != null && Contains(_btnReport, x, y)) return "btn_report";
                if (_btnBack != null && Contains(_btnBack, x, y)) return "btn_back";
            }
            catch (Exception) { }
            return "";
        }

        private static RectTransform _btnSend, _btnDebug, _btnReport, _btnBack;
        private static bool _activateLogged;

        /// <summary>登记四个按钮（Lua 侧绑定 onClick 时顺手调用，用于插件侧命中判定）。</summary>
        public static void SetButtonRects(GameObject send, GameObject debug, GameObject report, GameObject back)
        {
            try { if (send != null) _btnSend = send.GetComponent<RectTransform>(); } catch (Exception) { }
            try { if (debug != null) _btnDebug = debug.GetComponent<RectTransform>(); } catch (Exception) { }
            try { if (report != null) _btnReport = report.GetComponent<RectTransform>(); } catch (Exception) { }
            try { if (back != null) _btnBack = back.GetComponent<RectTransform>(); } catch (Exception) { }
        }

        private static bool Contains(RectTransform rt, float x, float y)
        {
            if (rt == null) return false;
            Camera cam = null;
            try
            {
                Canvas c = rt.GetComponentInParent<Canvas>();
                if (c != null && c.renderMode != RenderMode.ScreenSpaceOverlay) cam = c.worldCamera;
            }
            catch (Exception) { }
            try { return RectTransformUtility.RectangleContainsScreenPoint(rt, new Vector2(x, y), cam); }
            catch (Exception) { return false; }
        }

        private static bool _wasMouseDown;

        /// <summary>
        /// 插件每帧调用（主线程）：
        ///  ① 采集鼠标点击（供 Lua 的 ClickPoll 使用）
        ///  ② 若 Lua 进入打字模式，把键盘焦点交给真 InputField
        ///  ③ 在打字模式下采集回车/取消
        /// 返回是否处于"脚本要求焦点"状态。
        /// </summary>
        public static bool TickFocus()
        {
            CaptureClick();
            EnsureEventSystemExists();   // 兜底：场景里没有 EventSystem 就补一个

            if (!_wantFocus)
            {
                if (_focusedNow) _focusedNow = false;
                _wasMouseDown = false;
                _lastEsName = "";
                _lastSelName = "";
                _lastInputString = "";
                _diagTicks = 0;
                return false;
            }

            EnsureEventSystem();

            // 回车 / 小键盘回车 → submit；Esc → cancel
            try
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) _submitPending = true;
                if (Input.GetKeyDown(KeyCode.Escape)) _cancelPending = true;
            }
            catch (Exception) { }

            if (_inputField == null) return true;

            // 点中输入框 → 请求聚焦（不等 EventSystem 自己判定，最稳）
            bool down = false;
            try { down = Input.GetMouseButtonDown(0); } catch (Exception) { }
            if (down)
            {
                Vector3 mp = Input.mousePosition;
                if (Contains(_inputField.transform as RectTransform, mp.x, mp.y)) Activate(true);
            }
            _wasMouseDown = down;

            // ★ 只认 Unity 的真相：InputField.isFocused。
            //   以前这里是 `if (!_focusedNow) Activate(false)` —— 而 _focusedNow 在"调用没抛异常"时就被置 1，
            //   于是插件连报 7 次"聚焦成功"，Unity 却始终 isFocused=false（实机日志：ok=1 但 [输入]焦点=0）。
            //   现在改成：只有 isFocused==true 才算拿到；没拿到就重试，并且说清卡在哪一步。
            bool realFocused = false;
            try { realFocused = _inputField.isFocused; } catch (Exception) { }
            _focusedNow = realFocused;
            if (!realFocused)
            {
                Activate(false);
                _focusWait++;
                if (_focusWait == 1 || _focusWait % 120 == 0)
                {
                    string whyEs = "(null)";
                    string whySel = "(null)";
                    try
                    {
                        EventSystem es2 = EventSystem.current;
                        if (es2 != null)
                        {
                            whyEs = NameOf(es2.gameObject);
                            whySel = NameOf(es2.currentSelectedGameObject);
                        }
                    }
                    catch (Exception) { }
                    if (AiChatPlugin.Log != null)
                        AiChatPlugin.Log.LogWarning("[AiChat][诊断-焦点] 输入框仍无焦点（第 " + _focusWait + " 帧）"
                            + " EventSystem=" + whyEs + " 选中=" + whySel
                            + " 输入框interactable=" + SafeInteractable()
                            + " imeMode=" + SafeIme());
                }
            }
            else if (!_focusConfirmed)
            {
                _focusConfirmed = true;
                _lastFocusMsg = "✅ 确认拿到键盘焦点（isFocused=true，第 " + _focusWait + " 帧）";
                if (AiChatPlugin.Log != null)
                    AiChatPlugin.Log.LogInfo("[AiChat][UI] " + _lastFocusMsg
                        + " EventSystem=" + SafeEsName() + " 选中=" + SafeSelName());
                // 焦点确认到手后才开输入法（否则会打扰游戏内打字）
                try { Input.imeCompositionMode = IMECompositionMode.On; } catch (Exception) { }
            }

            DiagTick();
            return true;
        }

        // 焦点诊断用的小工具（全部容错，绝不抛）
        private static int _focusWait;
        private static bool _focusConfirmed;

        private static string SafeEsName()
        {
            try { EventSystem es = EventSystem.current; return es == null ? "(null)" : NameOf(es.gameObject); }
            catch (Exception) { return "(?)"; }
        }

        private static string SafeSelName()
        {
            try
            {
                EventSystem es = EventSystem.current;
                return (es == null) ? "(null)" : NameOf(es.currentSelectedGameObject);
            }
            catch (Exception) { return "(?)"; }
        }

        private static string SafeInteractable()
        {
            try { return _inputField != null && _inputField.interactable ? "1" : "0"; }
            catch (Exception) { return "?"; }
        }

        private static string SafeIme()
        {
            try { return Input.imeCompositionMode.ToString(); }
            catch (Exception) { return "?"; }
        }

        // ================================================================
        //  诊断（纯只读，不改任何逻辑）：实机排查"打字打不进去"用
        //  目的：把 Unity 侧的真相打出来 ——
        //        EventSystem 到底是谁、当前选中谁、按键有没有落进我们的 InputField。
        // ================================================================
        private static string _lastEsName = "";
        private static string _lastSelName = "";
        private static string _lastInputString = "";
        private static int _diagTicks;
        private static int _keyEventsLogged;

        private static string NameOf(GameObject go)
        {
            try { return go == null ? "(null)" : go.name; }
            catch (Exception) { return "(?)"; }
        }

        private static void DiagTick()
        {
            string esName = "(null)";
            string selName = "(null)";
            try
            {
                EventSystem es = EventSystem.current;
                if (es != null)
                {
                    esName = NameOf(es.gameObject);
                    selName = NameOf(es.currentSelectedGameObject);
                }
            }
            catch (Exception) { }

            string inputStr = "";
            try { inputStr = Input.inputString ?? ""; } catch (Exception) { }
            bool realFocused = false;
            int textLen = -1;
            try
            {
                realFocused = _inputField.isFocused;
                textLen = _inputField.text != null ? _inputField.text.Length : -1;
            }
            catch (Exception) { }

            if (selName != _lastSelName || esName != _lastEsName)
            {
                _lastEsName = esName;
                _lastSelName = selName;
                if (AiChatPlugin.Log != null)
                    AiChatPlugin.Log.LogInfo("[AiChat][诊断-焦点] EventSystem=" + esName
                        + " 当前选中=" + selName
                        + " ｜ 我们输入框=" + NameOf(_inputField.gameObject)
                        + " 输入框自报isFocused=" + (realFocused ? "1" : "0")
                        + " 插件标志=" + (_focusedNow ? "1" : "0"));
            }

            if (inputStr != _lastInputString)
            {
                _lastInputString = inputStr;
                _keyEventsLogged++;
                if (_keyEventsLogged <= 25 && AiChatPlugin.Log != null)
                    AiChatPlugin.Log.LogInfo("[AiChat][诊断-按键] Input.inputString 收到 "
                        + inputStr.Length + " 字符：" + Clip(inputStr, 40)
                        + " ｜ 输入框isFocused=" + (realFocused ? "1" : "0")
                        + " 输入框字数=" + textLen + " 选中=" + selName);
            }

            _diagTicks++;
            if (_diagTicks % 120 == 0 && AiChatPlugin.Log != null)
                AiChatPlugin.Log.LogInfo("[AiChat][诊断-焦点] 心跳 输入框isFocused=" + (realFocused ? "1" : "0")
                    + " 插件标志=" + (_focusedNow ? "1" : "0")
                    + " 输入框字数=" + textLen + " 选中=" + selName
                    + " 键盘事件累计=" + _keyEventsLogged);
        }

        private static void Activate(bool fromClick)
        {
            _focusTries++;
            _focusRequests++;
            try
            {
                EnsureEventSystem();
                EventSystem es = EventSystem.current;
                if (es != null) es.SetSelectedGameObject(_inputField.gameObject, null);
                _inputField.Select();
                // ★ ActivateInputField 的真实效果在【下一帧】才生效（内部是 m_ShouldActivateNextUpdate）。
                //   所以这里绝不能因为"没抛异常"就认定聚焦成功 —— 只记"已请求"，
                //   成功与否由 TickFocus 里读 InputField.isFocused 判定（正是上一版假成功的根源）。
                _inputField.ActivateInputField();
                if (fromClick)
                {
                    _lastFocusMsg = "第 " + _focusTries + " 次尝试：点中输入框 → 已请求聚焦";
                    if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogInfo("[AiChat][UI] " + _lastFocusMsg);
                }
                else if (!_activateLogged)
                {
                    _activateLogged = true;
                    _lastFocusMsg = "第 " + _focusTries + " 次尝试：已请求补焦点";
                    if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogInfo("[AiChat][UI] " + _lastFocusMsg);
                }
            }
            catch (Exception e)
            {
                _focusFail++;
                _focusedNow = false;
                _lastFocusMsg = "第 " + _focusTries + " 次尝试失败：" + e.GetType().Name + " " + e.Message;
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AiChat][UI] " + _lastFocusMsg);
            }
        }

        private static void EnsureEventSystem()
        {
            try
            {
                if (EventSystem.current != null) return;
                EventSystem[] all = UnityEngine.Object.FindObjectsOfType<EventSystem>();
                if (all != null && all.Length > 0)
                    EventSystem.current = all[0];
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 每帧检查一次：若场景里连一个 EventSystem 都没有，就自己建一个。
        /// 为什么需要兜底：真按钮的 onClick 与 InputField 的焦点都依赖 EventSystem，
        /// 一旦它没被实例化/被销毁，表现就是"按钮有按下变色但什么都不发生、打字打不进去"。
        /// </summary>
        private static void EnsureEventSystemExists()
        {
            try
            {
                if (EventSystem.current != null) return;
                // ★ 第92轮：先做全量枚举（含未激活对象），统计真实数量与输入模块，避免重复创建
                EventSystem[] all = null;
                try
                {
                    var res = Resources.FindObjectsOfTypeAll<EventSystem>();
                    if (res != null && res.Length > 0) all = res;
                }
                catch (Exception) { }
                if (all != null && all.Length > 0)
                {
                    EventSystem pick = null;
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] == null || !all[i].gameObject.scene.IsValid()) continue;
                        pick = all[i];
                        break;
                    }
                    if (pick == null) pick = all[0];
                    if (!pick.gameObject.activeSelf) pick.gameObject.SetActive(true);
                    if (!pick.enabled) pick.enabled = true;
                    EventSystem.current = pick;
                    if (!_esFoundLogged)
                    {
                        _esFoundLogged = true;
                        int mods = 0;
                        try { mods = pick.GetComponents<BaseInputModule>().Length; } catch (Exception) { }
                        if (AiChatPlugin.Log != null)
                            AiChatPlugin.Log.LogInfo("[AiChat][UI] 检测到已有 EventSystem：" + pick.gameObject.name
                                + "（共 " + all.Length + " 个候选中选此；输入模块=" + mods + "）");
                    }
                    return;
                }
                if (_esMissingLogged) return;
                GameObject go = new GameObject("AIChatEventSystem(Runtime)");
                go.AddComponent<EventSystem>();
                go.AddComponent<StandaloneInputModule>();
                _esMissingLogged = true;
                if (AiChatPlugin.Log != null)
                    AiChatPlugin.Log.LogWarning("[AiChat][UI] 场景里没有 EventSystem，已在运行时补建一个（否则按钮与输入框都不会有反应）");
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null)
                    AiChatPlugin.Log.LogWarning("[AiChat][UI] 补建 EventSystem 失败：" + e.Message);
            }
        }

        private static bool _esMissingLogged;
        private static bool _esFoundLogged;

        /// <summary>官方 UI 实测数据（排查"文字位置/命中/焦点"用）。</summary>
        public static string DiagUI()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[登记]").Append(_targetsDiag);
            bool nowFocused = false;
            try { nowFocused = _inputField != null && _inputField.isFocused; } catch (Exception) { }
            sb.Append(" [焦点]want=").Append(_wantFocus ? 1 : 0)
              .Append(" 真拿到的=").Append(nowFocused ? 1 : 0)
              .Append(" 等待帧=").Append(_focusWait)
              .Append(" 请求/失败=").Append(_focusRequests).Append("/").Append(_focusFail);
            sb.Append(" [事件]").Append(_lastFocusMsg);
            try { sb.Append(" [矩形]").Append(_rectValid ? "有" : "无"); } catch (Exception) { }
            try
            {
                if (_scrollRT != null)
                {
                    Vector3 p = _scrollRT.position;
                    sb.Append(" [框]center=(").Append((int)p.x).Append(",").Append((int)p.y).Append(")")
                      .Append(" size=").Append((int)_scrollRT.rect.width).Append("x").Append((int)_scrollRT.rect.height);
                }
            }
            catch (Exception) { }
            try
            {
                if (_textRT != null)
                {
                    Vector3 p = _textRT.position;
                    Rect r = _textRT.rect;
                    sb.Append(" [文字]pos=(").Append((int)p.x).Append(",").Append((int)p.y).Append(")")
                      .Append(" w=").Append((int)r.width);
                    if (_inputField != null && _inputField.textComponent != null)
                        sb.Append(" 首选高=").Append((int)_inputField.textComponent.preferredHeight);
                }
            }
            catch (Exception) { }
            try
            {
                if (_inputField != null)
                {
                    sb.Append(" [输入]焦点=").Append(_inputField.isFocused ? 1 : 0)
                      .Append(" 字数=").Append(_inputField.text != null ? _inputField.text.Length : -1);
                    Canvas c = _inputField.GetComponentInParent<Canvas>();
                    if (c != null) sb.Append(" 画布缩放=").Append(c.scaleFactor.ToString("0.###"));
                }
            }
            catch (Exception) { }
            try { sb.Append(" 屏幕=").Append(Screen.width).Append("x").Append(Screen.height); } catch (Exception) { }
            return sb.ToString();
        }

        // ---- 供插件内部使用 ----

        /// <summary>把一条待显示的回复放入队列（插件内部调用）。</summary>
        // ★ 第50轮：只发给 Lua、不显示到聊天框（人设分配设置等控制消息）
        public static void EnqueueRaw(string raw)
        {
            try { lock (Sync) { Pending.Add(raw); } } catch (Exception) { }
        }

        public static void EnqueueReply(string replyJson)
        {
            // ★ Plan B：插件生产即显示（绕过 Lua 渲染链）——在这里就写入自己的显示层
            try
            {
                var ps = (replyJson ?? "").Split('	');
                if (ps.Length >= 5)
                {
                    // 第31轮：把姿态（阵营）与 @目标 一起交给显示层，用于名字/目标上色
                    string target = ExtractMetaField(ps[4], "target");
                    bool bold = ExtractMetaField(ps[4], "bold") == "true";   // ★ 第61轮：只加粗"@到玩家"的台词
                    OverlayChat.AddLine(ps[0], ps[3], ps[1], target, bold);
                }
                else if (ps.Length >= 4)
                {
                    OverlayChat.AddLine(ps[0], ps[3]);
                }
            }
            catch (Exception) { }
            lock (Sync) { Pending.Add(replyJson); }
        }

        // 从调试信息（第 5 段）里取一个字段，如 target=xxx
        private static string ExtractMetaField(string meta, string key)
        {
            if (string.IsNullOrEmpty(meta) || string.IsNullOrEmpty(key)) return "";
            string[] parts = meta.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.StartsWith(key + "=")) return p.Substring(key.Length + 1);
            }
            return "";
        }

        public static int PendingCount
        {
            get { lock (Sync) { return Pending.Count; } }
        }

        private static string Clip(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
