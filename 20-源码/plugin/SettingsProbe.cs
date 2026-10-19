// SettingsProbe.cs —— 在【游戏的模组设置界面】里挂一个真的"连接测试"按钮 + 右侧结果显示
//
// 为什么放在插件里：游戏设置页是引擎按 MutatorConfiguration 的 7 种控件自动生成的（没有按钮类型），
// 但插件是 C#，可以自己创建 UI 对象，所以能在设置界面弹出时覆盖上一个真按钮。
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RF.AiChat;

    public static class SettingsProbe
    {
        public static string LastResult = "";     // 自检结果（由 AiChatPlugin.SelfTestApi 写入）
        public static bool Busy = false;
        private static float BusySince = 0f;   // 看门狗用：Busy 起始时刻

        private static GameObject _root;
        private static Text _resultText;
        private static float _nextCheck = 0f;
        private static float _nextAlive = 0f;
        private static bool _loggedFirst = false;
        internal static bool SettingsPageVisible = false;   // ★ 第46轮：给提示词设置入口用（设置页是否打开）
        private static int _errCount = 0;

        public static void Tick()
        {
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                try { AiChatPlugin.Log.LogInfo("[AIChat][面板] Tick 被调用（首次）"); } catch (Exception) { }
            }
            try
            {
                if (_root == null) Build();
                // 每 0.25 秒查一次"设置界面是否打开"（FindObjectsOfTypeAll 较重，不能每帧）
                if (Time.unscaledTime < _nextCheck) { UpdateText(); return; }
                // ★ 第48轮（降卡顿）：局内不再做"全场景 TMP/Text 扫描"——
                //   设置页只可能在主菜单打开，所以局内直接隐藏面板，只保证一次性显示层修复已执行。
                bool inMatch = InMatch();
                _nextCheck = Time.unscaledTime + (inMatch ? 0.5f : 0.25f);
                if (inMatch)
                {
                    DisableChatText();          // 一次性；完成后立即 return，不再扫描
                    EnsureChatTextAlignment();  // 一次性；完成后立即 return
                    if (_root != null && _root.activeSelf) _root.SetActive(false);
                    SettingsPageVisible = false;
                    if (_lastShown)
                    {
                        _lastShown = false;
                        try { AiChatPlugin.Log.LogInfo("[AIChat][面板] 局内隐藏（不扫描场景，避免卡顿）"); } catch (Exception) { }
                    }
                    UpdateText();
                    return;
                }
                // 菜单/设置页：低频检测（去掉渲染探针，避免每 0.25s 扫全场景 Text）
                DumpChatComponents(); // ★ 第 13 轮补充①：组件清单补查（一次）
                EnsureChatTextAlignment(); // ★ 第 13 轮合并版：确定性排版修复（一次）
                DisableChatText();   // ★ 第 19 轮：停用 ChatText 渲染层（一次）
                ReconIfNeeded();     // ★ 侦查：设置页一出现就把真实对象名/组件类型/层级打进日志（只打一次）
                var tg = FindTestConnToggle();
                bool pageOpen = tg != null;
                // ★ 第87轮：即使找不到"连接测试"文字，只要模组设置面板存在也算打开
                if (!pageOpen)
                {
                    try
                    {
                        GameObject pnl = GameObject.Find("Main Menu Canvas New/Mutator Config Panel");
                        pageOpen = pnl != null && pnl.activeInHierarchy;
                    }
                    catch (Exception) { }
                }
                SettingsPageVisible = pageOpen;
                bool toggleOn = tg != null && tg.isOn;
                bool open = pageOpen && toggleOn;
                if (_root != null && _root.activeSelf != open) _root.SetActive(open);   // ★ 真正应用（GLM 指出的缺失行）
                Heartbeat(tg);   // ★ 第 6 轮：每 2 秒一条心跳，消灭"扫到没扫到"靠猜的问题
                // ★ 看门狗：Busy 超过 超时+5 秒仍未复位 → 强制复位，保证永不永久卡死
                if (Busy && BusySince > 0f && Time.unscaledTime - BusySince > 40f)
                {
                    Busy = false; BusySince = 0f;
                    LastResult = "⚠️ 测试超时（40 秒未返回），已复位。请检查网络或接口地址。";
                    try { AiChatPlugin.Log.LogWarning("[AIChat][面板] 看门狗：测试超时，已强制复位 Busy"); } catch (Exception) { }
                }
                if (open != _lastShown)
                {
                    _lastShown = open;
                    try { AiChatPlugin.Log.LogInfo("[AIChat][面板] 显示=" + open
                        + "（设置页打开=" + pageOpen + " ｜ 连接测试勾选=" + toggleOn + "）"); } catch (Exception) { }
                }
                UpdateText();
            }
            catch (Exception e)
            {
                if (_errCount < 5)
                {
                    _errCount++;
                    try { AiChatPlugin.Log.LogWarning("[AIChat][面板] Tick 异常：" + e.GetType().Name + " " + e.Message); } catch (Exception) { }
                }
            }
        }

        private static Type _amType;
        private static FieldInfo _amInstanceField;
        private static PropertyInfo _amInstanceProp;
        private static FieldInfo _amPlayerField;
        private static PropertyInfo _amPlayerProp;

        // ★ 第52轮修正：局内判定改为「ActorManager.instance.player 存在」——
        //   之前用 GameManager.instance 判，主菜单里它也存在，导致设置页被当成"局内"跳过，
        //   提示词/人设分配入口按钮全都不显示了。
        private static bool InMatch()
        {
            try
            {
                if (_amType == null)
                {
                    _amType = AccessTools.TypeByName("ActorManager");
                    if (_amType == null) return false;
                    const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    _amInstanceField = _amType.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    _amInstanceProp = _amType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    _amPlayerField = _amType.GetField("player", F);
                    _amPlayerProp = _amType.GetProperty("player", F);
                    if ((_amInstanceField == null && _amInstanceProp == null) || (_amPlayerField == null && _amPlayerProp == null))
                        return false;
                }
                object am = _amInstanceField != null ? _amInstanceField.GetValue(null)
                            : (_amInstanceProp != null ? _amInstanceProp.GetValue(null, null) : null);
                if (am == null) return false;
                object pl = _amPlayerField != null ? _amPlayerField.GetValue(am)
                            : (_amPlayerProp != null ? _amPlayerProp.GetValue(am, null) : null);
                return pl != null;
            }
            catch (Exception) { return false; }
        }

        private static bool IsSettingsOpen()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                for (int i = 0; i < all.Length; i++)
                {
                    var mb = all[i];
                    if (mb == null) continue;
                    string tn = mb.GetType().Name;
                    if ((tn == "MutatorConfigurationMenu" || tn == "MutatorEntryPanel")
                        && mb.gameObject != null && mb.gameObject.activeInHierarchy)
                        return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        private static void UpdateText()
        {
            if (_resultText == null) return;
            string s = Busy ? "测试中… / Testing…" : (LastResult.Length > 0 ? LastResult : "点左边按钮测试连接 / Click the TEST button to test");
            if (_resultText.text != s) _resultText.text = s;
        }

        private static Font _font;
        private static Font GetFont()
        {
            if (_font != null) return _font;
            try { _font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { }
            if (_font == null) { try { _font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 16); } catch (Exception) { } }
            return _font;
        }




        // 找到设置页里"连接测试"那一行的 Toggle
        // ★ 第 6 轮：设置页标签实际是 TextMeshProUGUI（GLM 第 5 轮 Cecil 证据），故双通道扫描
        private static Toggle FindTestConnToggle()
        {
            try
            {
                // 通道 1（主）：TMP 标签
                var tmps = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
                for (int i = 0; i < tmps.Length; i++)
                {
                    var t = tmps[i];
                    if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy) continue;
                    if (string.IsNullOrEmpty(t.text) || !t.text.Contains("连接测试")) continue;
                    var tg = FindToggleUp(t.transform);
                    if (tg != null) return tg;
                }
                // 通道 2（兜底）：UGUI Text 标签
                var texts = Resources.FindObjectsOfTypeAll<Text>();
                for (int i = 0; i < texts.Length; i++)
                {
                    var t = texts[i];
                    if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy) continue;
                    if (string.IsNullOrEmpty(t.text) || !t.text.Contains("连接测试")) continue;
                    var tg = FindToggleUp(t.transform);
                    if (tg != null) return tg;
                }
            }
            catch (Exception) { }
            return null;
        }

        // 从文字节点向上最多 6 级找 UGUI Toggle；找不到再在同父级子树兜底
        private static Toggle FindToggleUp(Transform tr)
        {
            try
            {
                var n = tr; int d = 0;
                while (n != null && d < 6)
                {
                    var tg = n.GetComponent<Toggle>();
                    if (tg != null) return tg;
                    n = n.parent; d++;
                }
                var par = tr.parent;
                if (par != null) { var tg2 = par.GetComponentInChildren<Toggle>(true); if (tg2 != null) return tg2; }
            }
            catch (Exception) { }
            return null;
        }


        private static float _nextBeat = 0f;
        private static void Heartbeat(Toggle tg)
        {
            if (Time.unscaledTime < _nextBeat) return;
            _nextBeat = Time.unscaledTime + 2f;
            try
            {
                int nText = Resources.FindObjectsOfTypeAll<Text>().Length;
                int nTmp = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>().Length;
                int nTg = Resources.FindObjectsOfTypeAll<Toggle>().Length;
                AiChatPlugin.Log.LogInfo("[AIChat][面板] 存活：UGUI-Text=" + nText + " TMP-Text=" + nTmp
                    + " Toggle=" + nTg + " 命中=" + (tg != null ? "是(isOn=" + tg.isOn + ")" : "否"));
            }
            catch (Exception) { }
        }


        // ===== 第 19 轮：停用 ChatText 渲染层（消除双文本叠加，Plan B 为唯一显示层）=====
        private static bool _chatTextDisabled = false;
        private static void DisableChatText()
        {
            if (_chatTextDisabled) return;
            try
            {
                var texts = Resources.FindObjectsOfTypeAll<Text>();
                for (int i = 0; i < texts.Length; i++)
                {
                    var t = texts[i];
                    if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy) continue;
                    if (t.name != "ChatText") continue;
                    t.enabled = false;          // 组件保留、Lua 照常写入，但不再渲染
                    _chatTextDisabled = true;
                    AiChatPlugin.Log.LogInfo("[AIChat][显示层] ChatText 已停用（Plan B 为唯一显示层）");
                    return;
                }
            }
            catch (Exception e)
            {
                try { AiChatPlugin.Log.LogWarning("[AIChat][显示层] 停用 ChatText 失败：" + e.Message); } catch (Exception) { }
            }
        }


        // ===== 第 13 轮任务①补充①：ChatText / Content 的 CanvasGroup 与 Text.enabled 补查 =====
        private static bool _compDumped = false;
        private static void DumpChatComponents()
        {
            if (_compDumped) return;
            try
            {
                var texts = Resources.FindObjectsOfTypeAll<Text>();
                for (int i = 0; i < texts.Length; i++)
                {
                    var t = texts[i];
                    if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy) continue;
                    if (t.name != "ChatText") continue;
                    _compDumped = true;
                    AiChatPlugin.Log.LogInfo("[AIChat][补查] ===== ChatText / Content 组件清单 =====");
                    LogOne("ChatText", t.gameObject);
                    var par = t.transform.parent;
                    if (par != null) LogOne("Content", par.gameObject);
                    else AiChatPlugin.Log.LogInfo("[AIChat][补查] Content = 不存在");
                    AiChatPlugin.Log.LogInfo("[AIChat][补查] ===== 结束 =====");
                    return;
                }
            }
            catch (Exception e) { try { AiChatPlugin.Log.LogWarning("[AIChat][补查] 异常：" + e.Message); } catch (Exception) { } }
        }

        private static void LogOne(string label, GameObject go)
        {
            string cg = "无 CanvasGroup";
            try
            {
                var c = go.GetComponent<CanvasGroup>();
                cg = c == null ? "无 CanvasGroup" : ("CanvasGroup(alpha=" + c.alpha + " blocksRaycasts=" + c.blocksRaycasts + " interactable=" + c.interactable + ")");
            }
            catch (Exception) { cg = "CanvasGroup 读取失败"; }
            string te = "无 Text 组件";
            try
            {
                var t = go.GetComponent<Text>();
                te = t == null ? "无 Text 组件" : ("Text.enabled=" + t.enabled + " font=" + (t.font == null ? "null" : t.font.name));
            }
            catch (Exception) { te = "Text 读取失败"; }
            AiChatPlugin.Log.LogInfo("[AIChat][补查] " + label + "：active=" + go.activeInHierarchy + " ｜ " + cg + " ｜ " + te);
        }


        // ===== 第 13 轮任务①第 3 次侦查：运行时渲染探针（全 C#，只读）=====
        private static int _lastVertex = -2;
        private static bool _probeLoggedOnce = false;

        private static void DumpRenderProbe()
        {
            try
            {
                var texts = Resources.FindObjectsOfTypeAll<Text>();
                Text target = null;
                int sameName = 0;
                for (int i = 0; i < texts.Length; i++)
                {
                    var t = texts[i];
                    if (t == null) continue;
                    if (t.name == "ChatText") { sameName++; if (target == null && t.gameObject != null && t.gameObject.activeInHierarchy) target = t; }
                }
                if (target == null) return;

                // ★ 双胞胎排查：把【每一个】ChatText 实例分别 dump（路径/文字长度/顶点数/激活）
                for (int i = 0; i < texts.Length; i++)
                {
                    var ti = texts[i];
                    if (ti == null || ti.name != "ChatText") continue;
                    int vci = -1; try { vci = ti.cachedTextGenerator.vertexCount; } catch (Exception) { vci = -1; }
                    int leni = -1; try { leni = ti.text == null ? 0 : ti.text.Length; } catch (Exception) { }
                    string pathi = PathOf(ti.transform);
                    bool acti = ti.gameObject != null && ti.gameObject.activeInHierarchy;
                    AiChatPlugin.Log.LogInfo("[AIChat][孪生] 实例 path=" + pathi + " ｜ active=" + acti
                        + " ｜ 文字长度=" + leni + " ｜ vertexCount=" + vci
                        + " ｜ font=" + (ti.font == null ? "null" : ti.font.name));
                }
                // ③ 决定性：网格生成了没有
                int vc = -1;
                try { vc = target.cachedTextGenerator.vertexCount; } catch (Exception) { vc = -1; }
                // 只在顶点数变化或首次时打印，避免刷屏
                bool changed = (vc != _lastVertex) || !_probeLoggedOnce;
                _lastVertex = vc;
                _probeLoggedOnce = true;
                if (!changed) return;

                string fontName = "null";
                try { fontName = target.font == null ? "null" : target.font.name; } catch (Exception) { }
                string cull = "?";
                try { cull = target.canvasRenderer.cull.ToString(); } catch (Exception) { }
                string txt = "";
                try { txt = "#" + (target.text == null ? 0 : target.text.Length); } catch (Exception) { }
                AiChatPlugin.Log.LogInfo("[AIChat][探针] ① font.name=" + fontName
                    + " ｜ ② enabled=" + target.enabled
                    + " ｜ ③ vertexCount=" + vc + "  ←★决定性"
                    + " ｜ ④ canvasRenderer.cull=" + cull
                    + " ｜ ⑤ 同名ChatText实例数=" + sameName
                    + " ｜ 文字长度=" + txt);

                // 对 ChatText 本身 / Content / ChatBox 各查一次 CanvasGroup
                LogCanvasGroup("ChatText", target.transform);
                var p1 = target.transform.parent;
                if (p1 != null) LogCanvasGroup("Content", p1);
                else AiChatPlugin.Log.LogInfo("[AIChat][探针] Content = 不存在");
                var p2 = p1 != null ? p1.parent : null;      // Viewport
                var p3 = p2 != null ? p2.parent : null;      // ChatBox
                if (p3 != null) LogCanvasGroup("ChatBox", p3);
            }
            catch (Exception e) { try { AiChatPlugin.Log.LogWarning("[AIChat][探针] 异常：" + e.Message); } catch (Exception) { } }
        }

        private static void LogCanvasGroup(string label, Transform tr)
        {
            try
            {
                var c = tr.GetComponent<CanvasGroup>();
                string s = c == null ? "无 CanvasGroup" : ("CanvasGroup(alpha=" + c.alpha + " blocksRaycasts=" + c.blocksRaycasts + " interactable=" + c.interactable + ")");
                AiChatPlugin.Log.LogInfo("[AIChat][探针] " + label + " active=" + tr.gameObject.activeInHierarchy + " ｜ " + s);
            }
            catch (Exception) { }
        }


        // ===== 第 13 轮任务①合并版：确定性对齐修复 + 可见性探针（世界四角/交集）=====
        private static bool _alignFixed = false;
        private static float _nextVisProbe = 0f;

        private static Text FindChatText()
        {
            try
            {
                var texts = Resources.FindObjectsOfTypeAll<Text>();
                for (int i = 0; i < texts.Length; i++)
                {
                    var t = texts[i];
                    if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy) continue;
                    if (t.name == "ChatText") return t;
                }
            }
            catch (Exception) { }
            return null;
        }

        // 打印一个 RectTransform 的世界四角 + 返回其纵向范围
        private static void LogCorners(string label, RectTransform rt, out float yMin, out float yMax, out float xMin, out float xMax)
        {
            yMin = 0; yMax = 0; xMin = 0; xMax = 0;
            if (rt == null) { AiChatPlugin.Log.LogInfo("[AIChat][可见性] " + label + " = 不存在"); return; }
            try
            {
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                xMin = Math.Min(corners[0].x, Math.Min(corners[1].x, Math.Min(corners[2].x, corners[3].x)));
                xMax = Math.Max(corners[0].x, Math.Max(corners[1].x, Math.Max(corners[2].x, corners[3].x)));
                yMin = Math.Min(corners[0].y, Math.Min(corners[1].y, Math.Min(corners[2].y, corners[3].y)));
                yMax = Math.Max(corners[0].y, Math.Max(corners[1].y, Math.Max(corners[2].y, corners[3].y)));
                AiChatPlugin.Log.LogInfo(string.Format("[AIChat][可见性] {0} 世界四角: 左下({1:F0},{2:F0}) 左上({3:F0},{4:F0}) 右上({5:F0},{6:F0}) 右下({7:F0},{8:F0}) ⇒ x[{9:F0}..{10:F0}] y[{11:F0}..{12:F0}]",
                    label, corners[0].x, corners[0].y, corners[1].x, corners[1].y, corners[2].x, corners[2].y, corners[3].x, corners[3].y,
                    xMin, xMax, yMin, yMax));
            }
            catch (Exception e) { AiChatPlugin.Log.LogWarning("[AIChat][可见性] " + label + " GetWorldCorners 失败：" + e.Message); }
        }

        // 完整可见性探针（修复前后各打一次）
        private static void VisibilityProbe(string tag)
        {
            try
            {
                var t = FindChatText();
                if (t == null) { AiChatPlugin.Log.LogInfo("[AIChat][可见性][" + tag + "] ChatText 不存在"); return; }
                AiChatPlugin.Log.LogInfo("[AIChat][可见性][" + tag + "] ===== 开始 =====");
                // 文字自身属性（C# 侧可读，Lua 读不到）
                string al = "?", vo = "?", ho = "?";
                try { al = t.alignment.ToString(); } catch (Exception) { }
                try { vo = t.verticalOverflow.ToString(); } catch (Exception) { }
                try { ho = t.horizontalOverflow.ToString(); } catch (Exception) { }
                AiChatPlugin.Log.LogInfo("[AIChat][可见性][" + tag + "] alignment=" + al + " ｜ verticalOverflow=" + vo + " ｜ horizontalOverflow=" + ho + " ｜ fontSize=" + t.fontSize + " ｜ 文字长度=" + (t.text == null ? 0 : t.text.Length));

                float ty0, ty1, tx0, tx1;
                LogCorners("ChatText", t.rectTransform, out ty0, out ty1, out tx0, out tx1);

                var content = t.transform.parent as RectTransform;
                float cy0 = 0, cy1 = 0, cx0 = 0, cx1 = 0;
                if (content != null) LogCorners("Content", content, out cy0, out cy1, out cx0, out cx1);

                RectTransform viewport = null;
                if (content != null && content.parent != null) viewport = content.parent as RectTransform;
                float vy0 = 0, vy1 = 0, vx0 = 0, vx1 = 0;
                if (viewport != null) LogCorners("Viewport(遮罩)", viewport, out vy0, out vy1, out vx0, out vx1);

                if (viewport != null)
                {
                    float interH = Math.Min(ty1, vy1) - Math.Max(ty0, vy0);
                    float interW = Math.Min(tx1, vx1) - Math.Max(tx0, vx0);
                    AiChatPlugin.Log.LogInfo(string.Format("[AIChat][可见性][{0}] ★ 文字∩遮罩 交集: 宽={1:F0} 高={2:F0} {3}",
                        tag, interW, interH, (interH > 0 && interW > 0) ? "(有交集)" : "(无交集！文字在遮罩外)"));
                }
                AiChatPlugin.Log.LogInfo("[AIChat][可见性][" + tag + "] ===== 结束 =====");
            }
            catch (Exception e) { try { AiChatPlugin.Log.LogWarning("[AIChat][可见性] 异常：" + e.Message); } catch (Exception) { } }
        }

        // 确定性修复：聊天框的三个排版属性（常识性正确，不依赖判读）
        private static void EnsureChatTextAlignment()
        {
            if (_alignFixed) return;
            try
            {
                var t = FindChatText();
                if (t == null) return;
                VisibilityProbe("修复前");
                t.alignment = TextAnchor.UpperLeft;
                t.verticalOverflow = VerticalWrapMode.Overflow;
                t.horizontalOverflow = HorizontalWrapMode.Wrap;
                _alignFixed = true;
                AiChatPlugin.Log.LogInfo("[AIChat][可见性] 已修复排版：alignment=UpperLeft, verticalOverflow=Overflow, horizontalOverflow=Wrap");
                VisibilityProbe("修复后");
            }
            catch (Exception e) { try { AiChatPlugin.Log.LogWarning("[AIChat][可见性] 修复失败：" + e.Message); } catch (Exception) { } }
        }

        private static bool _reconDone = false;

        // 侦查：找到任何含"连接测试"的 Text 时，dump 设置页的真实结构（只打一次，避免刷屏）
        private static void ReconIfNeeded()
        {
            if (_reconDone) return;
            try
            {
                var texts = Resources.FindObjectsOfTypeAll<Text>();
                var tmps = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
                bool found = false;
                for (int i = 0; i < tmps.Length; i++)
                {
                    var t = tmps[i];
                    if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy) continue;
                    if (!string.IsNullOrEmpty(t.text) && t.text.Contains("连接测试")) { found = true; break; }
                }
                if (!found)
                {
                    for (int i = 0; i < texts.Length; i++)
                    {
                        var t = texts[i];
                        if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy) continue;
                        if (t.text != null && t.text.Contains("连接测试")) { found = true; break; }
                    }
                }
                if (!found) return;      // 设置页还没打开
                _reconDone = true;
                AiChatPlugin.Log.LogInfo("[AIChat][侦查] ===== 发现含「连接测试」的文字，开始 dump 设置页结构 ===== TMP总数=" + tmps.Length + " UGUI总数=" + texts.Length);
                // ① 所有含"连接测试"的 Text 及其向上 6 级父链
                for (int i = 0; i < texts.Length; i++)
                {
                    var t = texts[i];
                    if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy) continue;
                    if (t.text == null || !t.text.Contains("连接测试")) continue;
                    AiChatPlugin.Log.LogInfo("[AIChat][侦查] Text 内容=" + t.text + " ｜ 路径=" + PathOf(t.transform));
                    // 父链 + 每级组件类型
                    var tr = t.transform; int d = 0;
                    while (tr != null && d < 6)
                    {
                        var comps = tr.GetComponents<Component>();
                        var names = new System.Text.StringBuilder();
                        for (int c = 0; c < comps.Length; c++) { if (comps[c] != null) names.Append(comps[c].GetType().Name).Append(","); }
                        AiChatPlugin.Log.LogInfo("[AIChat][侦查]   父[" + d + "] " + tr.name + " 组件={" + names + "} active=" + tr.gameObject.activeInHierarchy);
                        tr = tr.parent; d++;
                    }
                }
                // ② 所有 Toggle 及其标签
                var toggles = Resources.FindObjectsOfTypeAll<Toggle>();
                AiChatPlugin.Log.LogInfo("[AIChat][侦查] 场景中 Toggle 数量=" + toggles.Length);
                for (int i = 0; i < toggles.Length; i++)
                {
                    var tg = toggles[i];
                    if (tg == null || tg.gameObject == null || !tg.gameObject.activeInHierarchy) continue;
                    string lab = CollectText(tg.transform);
                    AiChatPlugin.Log.LogInfo("[AIChat][侦查] Toggle[" + i + "] isOn=" + tg.isOn + " 路径=" + PathOf(tg.transform) + " 标签=" + lab);
                }
                // ③ 所有 Mutator/Config/Menu/Panel 相关的 MonoBehaviour 类型名
                var all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                var seen = new System.Collections.Generic.HashSet<string>();
                for (int i = 0; i < all.Length; i++)
                {
                    var mb = all[i];
                    if (mb == null || mb.gameObject == null || !mb.gameObject.activeInHierarchy) continue;
                    string tn = mb.GetType().Name;
                    if (tn.Contains("Mutator") || tn.Contains("Config") || tn.Contains("Menu") || tn.Contains("Panel") || tn.Contains("Check"))
                    {
                        if (seen.Add(tn)) AiChatPlugin.Log.LogInfo("[AIChat][侦查] MonoBehaviour 类型=" + tn + " 物体=" + mb.gameObject.name + " 路径=" + PathOf(mb.transform));
                    }
                }
                AiChatPlugin.Log.LogInfo("[AIChat][侦查] ===== dump 结束 =====");
            }
            catch (Exception e) { try { AiChatPlugin.Log.LogWarning("[AIChat][侦查] 异常：" + e.Message); } catch (Exception) { } }
        }

        private static string PathOf(Transform t)
        {
            try
            {
                var parts = new System.Collections.Generic.List<string>();
                var n = t; int g = 0;
                while (n != null && g < 8) { parts.Insert(0, n.name); n = n.parent; g++; }
                return string.Join("/", parts.ToArray());
            }
            catch (Exception) { return "?"; }
        }

        private static bool _lastShown = false;

        // 设置页是否打开：按"已确认存在于游戏程序集"的类型名/对象名判定，并记录命中项便于核对
        private static bool IsSettingsPageOpen()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                for (int i = 0; i < all.Length; i++)
                {
                    var mb = all[i];
                    if (mb == null) continue;
                    var go = mb.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;
                    string tn = mb.GetType().Name;
                    string gn = go.name;
                    if (tn == "MutatorConfigurationMenu" || tn == "MutatorEntryPanel"
                        || (gn != null && gn.Contains("MutatorConfiguration")))
                        return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        // "连接测试"勾选框是否勾上：找到设置页里文本含"连接测试"的 Toggle，读取其真实 isOn
        private static bool IsTestConnToggled()
        {
            try
            {
                var toggles = Resources.FindObjectsOfTypeAll<Toggle>();
                for (int i = 0; i < toggles.Length; i++)
                {
                    var tg = toggles[i];
                    if (tg == null || tg.gameObject == null || !tg.gameObject.activeInHierarchy) continue;
                    // 该 Toggle 的自身或子物体上是否有含"连接测试"的文字
                    string label = CollectText(tg.transform);
                    if (label != null && label.Contains("连接测试"))
                        return tg.isOn;
                }
            }
            catch (Exception) { }
            return false;
        }

        // 收集一个节点的子文字（层级最多 4 层，避免遍历整棵树）
        private static string CollectText(Transform t)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                CollectTextRec(t, sb, 0);
                return sb.ToString();
            }
            catch (Exception) { return null; }
        }

        private static void CollectTextRec(Transform t, System.Text.StringBuilder sb, int depth)
        {
            if (t == null || depth > 4) return;
            var txt = t.GetComponent<Text>();
            if (txt != null && !string.IsNullOrEmpty(txt.text)) sb.Append(txt.text);
            int n = t.childCount;
            for (int i = 0; i < n; i++) CollectTextRec(t.GetChild(i), sb, depth + 1);
        }

        private static void Build()
        {
          try
          {
            _root = new GameObject("AIChat_SettingsProbe");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32767;   // 置顶                       // 盖在游戏设置界面之上
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();

            // 底板（右上角一块，避免遮住设置列表）
            var panel = NewImage(_root.transform, "ProbePanel", new Color(0f, 0f, 0f, 0.72f),
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -90f), new Vector2(640f, 64f));

            // 真按钮
            var btnRT = NewImage(panel.transform, "ProbeButton", new Color(0.20f, 0.45f, 0.85f, 1f),
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(14f, 0f), new Vector2(140f, 36f));
            btnRT.pivot = new Vector2(0f, 0.5f);
            btnRT.GetComponent<Image>().raycastTarget = true;
            var btn = btnRT.gameObject.AddComponent<Button>();
            btn.targetGraphic = btnRT.GetComponent<Image>();
            var cb = btn.colors;
            cb.normalColor = new Color(0.20f, 0.45f, 0.85f, 1f);
            cb.highlightedColor = new Color(0.30f, 0.58f, 1f, 1f);
            cb.pressedColor = new Color(0.95f, 0.75f, 0.2f, 1f);
            btn.colors = cb;
            btn.onClick.AddListener(OnTestClicked);
            var btnLabel = NewText(btnRT.transform, "Label", "连接测试 / Test", TextAnchor.MiddleCenter,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            btnLabel.fontSize = 18;

            // 右侧结果
            var resRT = new GameObject("ProbeResult", typeof(RectTransform));
            var rrt = resRT.GetComponent<RectTransform>();
            rrt.SetParent(panel.transform, false);
            rrt.anchorMin = new Vector2(0f, 0.5f); rrt.anchorMax = new Vector2(1f, 0.5f);
            rrt.pivot = new Vector2(0f, 0.5f);
            rrt.offsetMin = new Vector2(166f, -22f);
            rrt.offsetMax = new Vector2(-12f, 22f);
            _resultText = resRT.AddComponent<Text>();
            _resultText.font = GetFont();
            _resultText.fontSize = 16;
            _resultText.alignment = TextAnchor.MiddleLeft;
            _resultText.color = new Color(0.95f, 0.95f, 0.95f, 1f);
            _resultText.raycastTarget = false;
            _resultText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _resultText.verticalOverflow = VerticalWrapMode.Truncate;

            _root.SetActive(false);       // ★ 初始隐藏；之后完全由 Tick() 按"设置页+勾选"控制（唯一主人）
            try { AiChatPlugin.Log.LogInfo("[AIChat][面板] 已创建 连接测试面板：sortingOrder=" + canvas.sortingOrder
                + " 尺寸=" + ((RectTransform)panel).sizeDelta + " 位置=" + ((RectTransform)panel).anchoredPosition); } catch (Exception) { }
          }
          catch (Exception e)
          {
              // ★ GPT P2-1：创建失败必须可重试——清掉半成品对象并把失败步骤写进日志
              try { AiChatPlugin.Log.LogWarning("[AIChat][面板] Build 失败（将下次重试）：" + e.GetType().Name + " " + e.Message); } catch (Exception) { }
              try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch (Exception) { }
              _root = null; _resultText = null;
          }
        }


        // ===== 第 9 轮：直读设置页当前输入值（TMP_InputField） =====
        public static string TestUrlOverride = null;
        public static string TestKeyOverride = null;
        public static string TestModelOverride = null;
        private static bool _fieldReconDone = false;

        // 在一行标签旁找 TMP_InputField，返回它的 text；找不到返回 null
        private static string ReadSettingField(string labelKey)
        {
            try
            {
                var tmps = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
                for (int i = 0; i < tmps.Length; i++)
                {
                    var t = tmps[i];
                    if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy) continue;
                    if (string.IsNullOrEmpty(t.text) || !t.text.Contains(labelKey)) continue;
                    // ① 自身/向上 6 级找 TMP_InputField
                    var tr = t.transform; int d = 0;
                    while (tr != null && d < 6)
                    {
                        var inf = tr.GetComponent<TMP_InputField>();
                        if (inf != null) return inf.text;
                        tr = tr.parent; d++;
                    }
                    // ② 同父级子树找
                    var par = t.transform.parent;
                    if (par != null)
                    {
                        var inf2 = par.GetComponentInChildren<TMP_InputField>(true);
                        if (inf2 != null) return inf2.text;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        // 读取并校验三个值；任何一项失败 → 返回 false（调用方静默退回旧行为）
        private static bool ReadSettingsPageValues(out string url, out string key, out string model)
        {
            url = null; key = null; model = null;
            try
            {
                string u = ReadSettingField("API 接口");
                string k = ReadSettingField("API 密钥");
                string m = ReadSettingField("模型名称");
                // ---- 一次性侦查（第 9 轮第 1 步）：只打标签/是否配对/长度，绝不打印内容 ----
                if (!_fieldReconDone)
                {
                    _fieldReconDone = true;
                    AiChatPlugin.Log.LogInfo("[AIChat][侦查] 设置页输入框配对："
                        + "API 接口=" + (u == null ? "未找到" : "长度" + u.Trim().Length)
                        + " ｜ API 密钥=" + (k == null ? "未找到" : "长度" + k.Trim().Length)
                        + " ｜ 模型名称=" + (m == null ? "未找到" : "长度" + m.Trim().Length));
                }
                // ---- 格式护栏 ----
                if (!string.IsNullOrEmpty(u) && u.Trim().StartsWith("http")) url = u.Trim();
                if (!string.IsNullOrEmpty(k))
                {
                    string kk = k.Trim();
                    if (kk.StartsWith("sk-") && kk.Length >= 30) key = kk;
                }
                if (!string.IsNullOrEmpty(m)) model = m.Trim();
                if (string.IsNullOrEmpty(model)) model = "deepseek-chat";
                bool ok = !string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(key);
                if (!_fieldReconDone) { }
                AiChatPlugin.Log.LogInfo("[AIChat][自检] 直读设置页：url=" + (url != null ? "有" : "无")
                    + " key=" + (key != null ? ("长度" + key.Length + " sk-前缀=是") : "无/不合规")
                    + " model=" + model);
                return ok;
            }
            catch (Exception e)
            {
                try { AiChatPlugin.Log.LogWarning("[AIChat][自检] 直读设置页失败（已退回旧行为）：" + e.Message); } catch (Exception) { }
                return false;
            }
        }


        // ★ 第 11 轮：三字段校验。返回 null 表示全部合格；否则返回用户可读的失败原因
        private static string ValidateSettingsPage(out string url, out string key, out string model)
        {
            url = null; key = null; model = null;
            string u = null, k = null, m = null;
            try
            {
                u = ReadSettingField("API 接口");
                k = ReadSettingField("API 密钥");
                m = ReadSettingField("模型名称");
            }
            catch (Exception e) { return "无法读取设置页输入（" + e.Message + "）"; }
            if (u == null && k == null && m == null) return "无法读取设置页输入（未找到这三个输入框）";
            // ★ 第 28 轮：设置页三项留空时回落到"本地配置 / 本机缓存"，
            //   这样游戏重启后不会因为设置页空着就"对话全没了"（Key 会沿用上次填过的）。
            if (string.IsNullOrEmpty(u) || u.Trim().Length == 0)
            {
                if (AiChatPlugin.CfgApiUrl != null) u = AiChatPlugin.CfgApiUrl.Value;
            }
            if (string.IsNullOrEmpty(k) || k.Trim().Length == 0)
            {
                if (AiChatPlugin.CfgApiKeyCache != null) k = AiChatPlugin.CfgApiKeyCache.Value;
            }
            if (string.IsNullOrEmpty(m) || m.Trim().Length == 0)
            {
                if (AiChatPlugin.CfgModel != null) m = AiChatPlugin.CfgModel.Value;
            }
            if (string.IsNullOrEmpty(u) || u.Trim().Length == 0) return "未填入 API 接口";
            if (!u.Trim().StartsWith("http")) return "API 接口格式不正确（应以 http 开头）";
            if (string.IsNullOrEmpty(k) || k.Trim().Length == 0) return "未填入 API 密钥";
            string kk = k.Trim();
            // ★ 第 11 轮补充：通用 OpenAI 兼容 —— 不限定 sk- 前缀与长度，只拦两类明显不是 Key 的输入
            //   ① 含空格/换行（Key 进 HTTP 头，含空白必然是粘错了）
            //   ② 以 http 开头（误把接口地址粘进密钥框）
            if (kk.IndexOf(' ') >= 0 || kk.IndexOf('\n') >= 0 || kk.IndexOf('\r') >= 0 || kk.IndexOf('\t') >= 0)
                return "API 密钥格式不正确（含空格/换行，或误粘了接口地址）";
            if (kk.StartsWith("http")) return "API 密钥格式不正确（含空格/换行，或误粘了接口地址）";
            if (string.IsNullOrEmpty(m) || m.Trim().Length == 0) return "未填入模型名称";
            url = u.Trim(); key = kk; model = m.Trim();
            return null;
        }

        private static void OnTestClicked()
        {
            if (Busy) return;                       // 防重复点击
            // ★ 第 11 轮：测试只对"用户此刻填的配置"负责 —— 校验不合格直接失败，绝不发请求、绝不回落缓存
            try
            {
                string vu, vk, vm;
                var fail = ValidateSettingsPage(out vu, out vk, out vm);
                if (fail != null)
                {
                    TestUrlOverride = null; TestKeyOverride = null; TestModelOverride = null;
                    LastResult = "❌ 连接失败：" + fail;
                    AiChatPlugin.Log.LogInfo("[AIChat][自检] 校验未通过，不发请求：" + fail);
                    Busy = false; BusySince = 0f;
                    return;
                }
                TestUrlOverride = vu; TestKeyOverride = vk; TestModelOverride = vm;
                ChatEngine.ApiUrlOverride = vu;
                ChatEngine.ApiKeyOverride = vk;
                ChatEngine.ModelOverride = vm;
                AiChatPlugin.Log.LogInfo("[AIChat][自检] 已同步直读值到内存 override（仅记长度，不记内容）");
            }
            catch (Exception e)
            {
                LastResult = "❌ 连接失败：无法读取设置页输入（" + e.Message + "）";
                Busy = false; BusySince = 0f;
                return;
            }
            Busy = true;
            BusySince = Time.unscaledTime;
            LastResult = "测试中…请稍候";
            // ★ GLM 第 6 轮：改后台线程执行（原来是 UI 主线程同步阻塞，最坏冻 60 秒）
            //   并且用 finally 兜底复位 Busy —— 任何出口都不会再卡在"测试中…"
            var th = new System.Threading.Thread(() =>
            {
                try { AiChatPlugin.SelfTestApi(); }
                catch (Exception e) { LastResult = "❌ 测试失败：" + e.Message; }
                finally { Busy = false; }
            });
            th.IsBackground = true;
            try { th.Start(); } catch (Exception e) { Busy = false; LastResult = "❌ 无法启动测试线程：" + e.Message; }
        }

        private static RectTransform NewImage(Transform parent, string name, Color color,
            Vector2 aMin, Vector2 aMax, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = size; rt.localScale = Vector3.one;
            var img = go.GetComponent<Image>();
            img.color = color;
            return rt;
        }

        private static Text NewText(Transform parent, string name, string txt, TextAnchor anchor,
            Vector2 aMin, Vector2 aMax, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = size; rt.localScale = Vector3.one;
            var t = go.GetComponent<Text>();
            t.font = GetFont();
            t.fontSize = 16;
            t.alignment = anchor;
            t.text = txt;
            t.color = Color.white;
            t.raycastTarget = false;
            return t;
        }
    }
