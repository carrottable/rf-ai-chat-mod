// OverlayChat.cs —— Plan B：插件自建"对话显示层"（真聊天框的文本渲染）
// ============================================================================
// 2026-10-18 第 27 轮（按用户最终确认的规则）：
//
// 【尺寸】聊天框大小**局内固定**，只能从 MOD 设置更改（宽倍/高倍/左边距）；
//        本插件不改框宽高，只把模块设置好的尺寸当成固定值使用。
// 【位置】聊天框中线 = 屏幕中线（垂直居中，anchoredPosition.y = 0），设置改高度时上下对称变化。
//
// 【内部结构（三段式，全部在框内）】
//   ┌──────────────────────────┐
//   │ 对话1                    │  ← 文字区：顶部对齐、向下排满；
//   │ 对话2                    │     排满后新对话从底部进入、最顶行被顶出
//   │ ...                      │
//   ├──────────────────────────┤
//   │ 发送 │ 调试 │ 战报 │ 返回 │  ← 按钮行（36px，框宽四等分）
//   ├──────────────────────────┤
//   │        请输入文本         │  ← 输入框（48px，框内底部）
//   └──────────────────────────┘
//   文字区高度 = 框高 − 8(顶) − 96(底部三段)，行数固定；不随对话条数变化。
//
// 换行：手工字符级换行（第 24 轮验证）——UGUI 词换行只在空格处断，
//   "名字: @对象 内容" 里 @对象 后的空格是唯一断点，中文内容会被整体挪到下一行。
// 裁剪：RectMask2D；入口统一单行化；关闭 richText 保证"测量=渲染"。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace RF.AiChat
{
    public static class OverlayChat
    {
        private const int HARD_CAP = 60;          // 消息缓冲上限
        private const int FONT_SIZE = 16;
        private const float TOP_PAD = 8f;         // 框顶到第一行文字
        private const float BOTTOM_PAD = 4f;      // 框底到输入框
        private const float INPUT_H = 48f;        // 输入框行高
        private const float BTN_H = 36f;          // 按钮行高
        private const float GAP = 4f;             // 行与行之间的空隙
        private const float ROWS_H = BOTTOM_PAD + INPUT_H + GAP + BTN_H + GAP;   // 框底以上固定部分 = 96

        // 每行 = 纯文本 + 每个字符的颜色（0=白 1=蓝队 2=红队 3=玩家黑）
        private class Msg
        {
            public string Text;
            public byte[] Styles;
            public int BoldStart = -1;   // ★ 第55轮：加粗区间起点（-1 = 不加粗）
            public int BoldEnd = -1;     // ★ 第63轮：加粗区间终点（不含）
            public Color[] Colors;       // ★ 第64轮：逐字颜色（炫彩）；null = 用 Styles
            public List<string> Wrapped; // ★ 第66轮：该条消息的换行缓存（增量换行，避免每条新台词都重排全部）
            public float WrappedWidth = -1f;
        }
        private static readonly List<Msg> _msgs = new List<Msg>();
        private static Color[] _pendingColors;   // ★ 第65轮：本轮消息的逐字颜色（炫彩 @玩家）
        private static readonly List<string> _wrapped = new List<string>();
        // ★ 第49轮（性能）：复用角点数组、渲染脏标记、Ensure 重试节流
        private static readonly Vector3[] _corners = new Vector3[4];
        private static int _wrapVer;
        private static int _lastWrapVer = -1;
        private static float _lastClipHRender = -1f;
        private static float _lastLineHRender = -1f;
        private static float _nextEnsureAt;
        private static float _lastRenderAt = -999f;   // ★ 第69轮：渲染节流计时
        private static GameObject _root;
        private static RectTransform _rootRt;
        private static GameObject _go;
        private static RectTransform _rt;
        private static Text _text;
        private static Font _font;
        private static GameObject _canvasGO;
        private static bool _created = false;
        private static bool _dirty = true;
        private static bool _refsOk = false;
        private static string _lastRendered = null;

        private static GameObject _chatBoxGO;
        private static RectTransform _chatBoxRt;
        private static GameObject _inputGO;
        private static RectTransform _inputRt;
        private static readonly GameObject[] _btnGO = new GameObject[4];
        private static readonly RectTransform[] _btnRt = new RectTransform[4];

        // 底部"表格"样式（第30轮）：文字/按钮分隔线、按钮/输入框分隔线、按钮四等分竖线
        private static GameObject _gridRoot;
        private static RectTransform _lineTop;
        private static RectTransform _lineBottom;
        private static readonly RectTransform[] _vLines = new RectTransform[3];

        private static float _wrapWidth = 316f;
        private static float _clipW = 0f;
        private static float _clipH = 0f;
        private static float _lineH = 0f;
        private static float _fixedBoxH = 0f;     // 局内固定的框高（= MOD 设置里的聊天框高度）
        private static float _canvasLeft = 0f;
        private static float _canvasTop = 0f;
        private static float _canvasCenterX = 0f;
        private static float _canvasCenterY = 0f;
        private static float _canvasScale = 1f;

        private static float _lastLogTime = -999f;
        private static int _lastVisualLines = -1;
        private static int _lastShownLines = -1;
        private static float _lastSizeWarn = -999f;
        private static float _armAt = -1f;        // 等 Lua 先完成一次性几何调整，之后本插件才接管框高
        private static GameObject _reportBtnHooked;   // 已挂过 C# 兜底回调的「战报」按钮
        private static GameObject _debugBtnHooked;    // 已接管的「调试」按钮
        private static float _lastDebugHookAt = -999f;
        private static GameObject _strayHiddenFor;    // 已隐藏过手工 UI 残留文字层的聊天框
        private static float _lastStrayScan = -999f;  // 残留文字巡检节流
        // ★ 第92轮：显示层自愈 + UI 状态取证
        private static float _lastVerifyAt = -999f;
        private static string _lastStateSig = "";
        private static float _dumpAt = -1f;
        private static string _dumpTag = "";

        // ------------------------------------------------------------------
        // 台词入缓冲（名字/正文颜色规则见下）
        // ------------------------------------------------------------------
        public static void AddLine(string name, string body)
        {
            AddLineInternal(name, body, 0, "", 0);
        }

        // AI 台词：pose 判断说话人阵营；target = 被 @ 的人（给 @对象 上其阵营色）
        public static void AddLine(string name, string body, string pose, string target)
        {
            int nameStyle = ColorOfSpeaker(name, pose);
            int targetStyle = 0;
            if (!string.IsNullOrEmpty(target) && target != "(无)")
                targetStyle = ColorOfTarget(target);
            AddLineInternal(name, body, nameStyle, target, targetStyle);
        }

        // ★ 第55轮：带粗体标记的 AI 台词（涉及玩家时正文加粗）
        public static void AddLine(string name, string body, string pose, string target, bool boldBody)
        {
            int nameStyle = ColorOfSpeaker(name, pose);
            int targetStyle = 0;
            bool playerTarget = false;
            if (!string.IsNullOrEmpty(target) && target != "(无)")
            {
                targetStyle = ColorOfTarget(target);
                try { playerTarget = ChatEngine.IsPlayerName(target); } catch (Exception) { }
            }
            AddLineInternal(name, body, nameStyle, target, targetStyle, boldBody, playerTarget);
        }

        // 玩家自己的发言：名字黑色（正文仍白色，按用户示意图）
        public static void AddLinePlayer(string name, string body)
        {
            AddLineInternal(name, body, 3, "", 0);
        }

        // 战报/系统播报：多行一起进显示层（最高优先级直接插入，不走 AI 队列、无冷却）
        public static void AddReport(string speaker, List<string> lines)
        {
            try
            {
                if (lines == null) return;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (!string.IsNullOrEmpty(lines[i]))
                        AddLineInternal(speaker, lines[i], 0, "", 0);
                }
            }
            catch (Exception) { }
        }

        // 「战报」按钮的 C# 兜底回调（Lua 换局后可能没重新接线）
        private static void OnReportButtonClicked()
        {
            try { ChatEngine.OnReportButton(); } catch (Exception) { }
        }

        // ★ 第37轮：「调试」按钮 → 打开/关闭调试窗口（并清掉 Lua 的旧调试排版回调）
        private static void OnDebugButtonClicked()
        {
            try { DebugWindow.Toggle(); } catch (Exception) { }
        }

        private static int ColorOfSpeaker(string name, string pose)
        {
            try
            {
                if (ChatEngine.IsPlayerName(name)) return 3;
                if (name == "系统" || name == "System") return 0;   // 系统发言永远白色（导出提示/战报等也走这里）
                string team = ChatEngine.TeamOf(name);        // 优先用名单里的真实阵营
                if (team == "Red") return 2;
                if (team == "Blue") return 1;
                if (pose != null)                             // 名单里查不到时再退回姿态
                {
                    if (pose.IndexOf("red") >= 0) return 2;
                    if (pose.IndexOf("blue") >= 0) return 1;
                }
            }
            catch (Exception) { }
            return 0;
        }

        private static int ColorOfTarget(string target)
        {
            try
            {
                if (ChatEngine.IsPlayerName(target)) return 3;
                string team = ChatEngine.TeamOf(target);
                if (team == "Red") return 2;
                if (team == "Blue") return 1;
            }
            catch (Exception) { }
            return 0;
        }

        private static void AddLineInternal(string name, string body, int nameStyle, string target, int targetStyle, bool boldBody = false, bool playerTarget = false)
        {
            try
            {
                if (string.IsNullOrEmpty(body)) return;
                string safeBody = OneLine(body);
                string safeName = OneLine(name);
                if (safeBody.Length > 400) safeBody = safeBody.Substring(0, 400) + "…";
                // ★ 第70轮：玩家自己的发言显示为"我：XXXX"（全角冒号，无空格）
                string sep = (nameStyle == 3) ? "：" : ": ";
                string prefix = string.IsNullOrEmpty(safeName) ? "" : (safeName + sep);
                string text = prefix + safeBody;
                byte[] styles = new byte[text.Length];
                int idx = 0;
                if (prefix.Length > 0)
                {
                    for (int i = 0; i < safeName.Length; i++) styles[idx++] = (byte)nameStyle;
                    while (idx < prefix.Length) styles[idx++] = 0;      // ": " 白色
                }
                // 正文默认白；把正文开头 "@目标" 里的目标字样上目标阵营色
                if (safeBody.Length > 0 && safeBody[0] == '@'
                    && !string.IsNullOrEmpty(target) && target != "(无)" && targetStyle != 0)
                {
                    int tPos = text.IndexOf(target, prefix.Length + 1);
                    if (tPos >= 0)
                    {
                        int end = Math.Min(text.Length, tPos + target.Length);
                        for (int i = tPos; i < end; i++) styles[i] = (byte)targetStyle;
                    }
                }
                // ★ 第65轮：@到玩家时，目标字样也做成炫彩（原来只有玩家自己那行有）
                if (playerTarget && !string.IsNullOrEmpty(target) && target != "(无)")
                {
                    int tp = text.IndexOf(target, Math.Max(0, prefix.Length));
                    if (tp >= 0)
                    {
                        Color[] pc = new Color[text.Length];
                        for (int i = 0; i < pc.Length; i++) pc[i] = new Color(-1f, 0f, 0f, 0f);
                        float span = Math.Max(1, target.Length - 1);
                        for (int i = 0; i < target.Length; i++)
                        {
                            float hue = (float)i / span;
                            pc[tp + i] = Color.HSVToRGB(hue * 0.85f, 0.85f, 1f);
                        }
                        _pendingColors = pc;
                    }
                }
                Msg m = new Msg();
                m.Text = text;
                m.Styles = styles;
                // ★ 第63轮：玩家自己的名字——用自己阵营的颜色，并且始终加粗
                bool playerLine = (nameStyle == 3);
                // ★ 第67轮：名字/目标任何一段配置成"炫彩"就逐字上色；否则用 ColorHex 的纯色
                Color[] colors = null;
                if (nameStyle >= 1 && nameStyle <= 3 && NameColors.Get(nameStyle) == "Rainbow")
                {
                    colors = new Color[text.Length];
                    for (int i = 0; i < text.Length; i++) colors[i] = new Color(-1f, 0f, 0f, 0f);
                    float span = Math.Max(1, safeName.Length - 1);
                    for (int i = 0; i < safeName.Length; i++)
                        colors[i] = Color.HSVToRGB((float)i / span * 0.85f, 0.85f, 1f);
                }
                if (targetStyle >= 1 && targetStyle <= 3 && NameColors.Get(targetStyle) == "Rainbow")
                {
                    int tp = text.IndexOf(target, Math.Max(0, prefix.Length));
                    if (tp >= 0)
                    {
                        if (colors == null)
                        {
                            colors = new Color[text.Length];
                            for (int i = 0; i < text.Length; i++) colors[i] = new Color(-1f, 0f, 0f, 0f);
                        }
                        float span = Math.Max(1, target.Length - 1);
                        for (int i = 0; i < target.Length; i++)
                            colors[tp + i] = Color.HSVToRGB((float)i / span * 0.85f, 0.85f, 1f);
                    }
                }
                m.Colors = colors;
                if (playerLine)
                {
                    m.BoldStart = 0;
                    m.BoldEnd = prefix.Length;
                }
                else
                {
                    m.BoldStart = boldBody ? prefix.Length : -1;
                    m.BoldEnd = boldBody ? text.Length : -1;
                }
                if (_pendingColors != null) { m.Colors = _pendingColors; _pendingColors = null; }
                _msgs.Add(m);
                while (_msgs.Count > HARD_CAP) _msgs.RemoveAt(0);
                _dirty = true;
            }
            catch (Exception) { }
        }

        public static void Tick()
        {
            try
            {
                Ensure();
                if (_dumpAt > 0f && Time.unscaledTime >= _dumpAt)
                {
                    _dumpAt = -1f;
                    DumpUiState(_dumpTag);
                }
                if (_text == null || _rootRt == null) return;
                CaptureCanvas();
                Layout();
                Render();
                MaybeLog();
            }
            catch (Exception e)
            {
                if (Time.unscaledTime - _lastLogTime > 3f)
                {
                    _lastLogTime = Time.unscaledTime;
                    LogWarn("[AIChat][UI] Tick 异常：" + e.GetType().Name + " : " + e.Message);
                }
            }
        }

        public static void ClearForNewMatch()
        {
            Clear();
            Log("[AIChat][PlanB4] 已清空对话显示层（新对局/对局结束）");
        }

        public static void Clear()
        {
            _msgs.Clear();
            _wrapped.Clear();
            _lastRendered = null;
            _dirty = true;
            _lastVisualLines = -1;
            _lastShownLines = -1;
            try { if (_text != null) _text.text = ""; } catch (Exception) { }
        }

        // ------------------------------------------------------------------
        private static string OneLine(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            bool lastSpace = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool isBreak = (c == ' ') || c == '\r' || c == '\n' || c == '\t' || c == '\v' || c == '\f'
                               || c == '\u2028' || c == '\u2029' || c == '\u0085' || c < 32;
                if (isBreak)
                {
                    if (!lastSpace && sb.Length > 0) { sb.Append(' '); lastSpace = true; }
                }
                else
                {
                    sb.Append(c);
                    lastSpace = false;
                }
            }
            return sb.ToString().Trim();
        }

        // ------------------------------------------------------------------
        private static void Ensure()
        {
            if (_created && _root != null && _rootRt != null && _text != null)
            {
                VerifyOverlayState();   // ★ 第92轮：不再无脑 return，定期检查/自愈
                return;
            }
            if (Time.unscaledTime < _nextEnsureAt) return;
            _nextEnsureAt = Time.unscaledTime + 0.5f;
            try
            {
                GameObject canvasGO = null;
                var chatText = FindSceneObject("ChatText");
                if (chatText != null)
                {
                    var c = chatText.GetComponentInParent<Canvas>();
                    if (c != null) canvasGO = c.gameObject;
                }
                if (canvasGO == null) return;
                _canvasGO = canvasGO;

                var crt = canvasGO.GetComponent<RectTransform>();
                if (crt != null)
                {
                    var cc = new Vector3[4];
                    crt.GetWorldCorners(cc);
                    _canvasLeft = Math.Min(cc[0].x, Math.Min(cc[1].x, Math.Min(cc[2].x, cc[3].x)));
                    _canvasTop = Math.Max(cc[0].y, Math.Max(cc[1].y, Math.Max(cc[2].y, cc[3].y)));
                    _canvasCenterX = (_canvasLeft + Math.Max(cc[0].x, Math.Max(cc[1].x, Math.Max(cc[2].x, cc[3].x)))) * 0.5f;
                    _canvasCenterY = (_canvasTop + Math.Min(cc[0].y, Math.Min(cc[1].y, Math.Min(cc[2].y, cc[3].y)))) * 0.5f;
                }
                try
                {
                    var cv = canvasGO.GetComponent<Canvas>();
                    if (cv != null && cv.scaleFactor > 0.01f) _canvasScale = cv.scaleFactor;
                }
                catch (Exception) { }
                if (_canvasScale <= 0.01f) _canvasScale = 1f;

                if (_font == null || (_text != null && _text.font == null))
                {
                    _font = null;
                    _font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", FONT_SIZE);
                    if (_font == null) _font = Font.CreateDynamicFontFromOSFont("SimHei", FONT_SIZE);
                    if (_font == null) return;
                    if (_text != null) { _text.font = _font; _dirty = true; }
                    Log("[AIChat][UI] 字体已重建（场景切换后动态字体被销毁）");
                }

                if (_root == null)
                {
                    _root = new GameObject("AIChat_OverlayClip", typeof(RectTransform), typeof(RectMask2D));
                    _rootRt = _root.GetComponent<RectTransform>();
                    _rootRt.SetParent(canvasGO.transform, false);
                    _rootRt.localScale = Vector3.one;
                }
                if (_go == null)
                {
                    _go = new GameObject("AIChat_OverlayText", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                    _rt = _go.GetComponent<RectTransform>();
                    _rt.SetParent(_rootRt, false);
                    _rt.localScale = Vector3.one;
                    _rt.anchorMin = new Vector2(0f, 0f);
                    _rt.anchorMax = new Vector2(1f, 1f);
                    _rt.pivot = new Vector2(0.5f, 0.5f);
                    _rt.offsetMin = Vector2.zero;
                    _rt.offsetMax = Vector2.zero;

                    _text = _go.GetComponent<Text>();
                    _text.font = _font;
                    _text.fontSize = FONT_SIZE;
                    _text.alignment = TextAnchor.UpperLeft;
                    _text.supportRichText = true;    // 名字/@对象按阵营上色（富文本标签宽度为 0，换行仍按纯文本算）
                    _text.horizontalOverflow = HorizontalWrapMode.Wrap;
                    _text.verticalOverflow = VerticalWrapMode.Overflow;
                    _text.raycastTarget = false;
                    _text.color = Color.white;
                    _text.text = "";
                }
                _created = true;
                _dirty = true;
                _armAt = Time.unscaledTime + 1.0f;   // 给 Lua 的 tuneUI 留出 1 秒，避免它读到我们改过的框高

                // 第30轮：底部"表格"线（父节点必须铺满画布，子线才能按画布左上角定位）
                if (_gridRoot == null)
                {
                    _gridRoot = new GameObject("AIChat_Grid", typeof(RectTransform));
                    var grt = _gridRoot.GetComponent<RectTransform>();
                    grt.SetParent(canvasGO.transform, false);
                    grt.anchorMin = Vector2.zero;
                    grt.anchorMax = Vector2.one;
                    grt.pivot = new Vector2(0.5f, 0.5f);
                    grt.offsetMin = Vector2.zero;
                    grt.offsetMax = Vector2.zero;
                    grt.localScale = Vector3.one;
                    _lineTop = CreateLine(grt, "LineTop");
                    _lineBottom = CreateLine(grt, "LineBottom");
                    for (int i = 0; i < 3; i++) _vLines[i] = CreateLine(grt, "VLine" + i);
                    Log("[AIChat][PlanB4] 底部表格线已创建（2 横线 + 3 竖线，固定尺寸版）");
                }
                Log("[AIChat][PlanB4] 显示层已创建：三段式（文字/按钮/输入框），框尺寸局内固定、居中对齐屏幕中线（字体=" + _font.name + "）");
            }
            catch (Exception e)
            {
                LogWarn("[AIChat][PlanB4] 创建失败：" + e.Message);
            }
        }

        private static void CaptureCanvas()
        {
            try
            {
                if (_canvasGO == null) return;
                var crt = _canvasGO.GetComponent<RectTransform>();
                if (crt == null) return;
                var cc = _corners;
                crt.GetWorldCorners(cc);
                _canvasLeft = Math.Min(cc[0].x, Math.Min(cc[1].x, Math.Min(cc[2].x, cc[3].x)));
                _canvasTop = Math.Max(cc[0].y, Math.Max(cc[1].y, Math.Max(cc[2].y, cc[3].y)));
                _canvasCenterX = (_canvasLeft + Math.Max(cc[0].x, Math.Max(cc[1].x, Math.Max(cc[2].x, cc[3].x)))) * 0.5f;
                _canvasCenterY = (_canvasTop + Math.Min(cc[0].y, Math.Min(cc[1].y, Math.Min(cc[2].y, cc[3].y)))) * 0.5f;
            }
            catch (Exception) { }
        }

        // ★ 第84轮：聊天框底部按钮/输入提示——游戏原生 UI 不支持切语言，改为中英双语
        private static GameObject _bilingualDoneFor;
        private static void ApplyChatBilingualOnce()
        {
            try
            {
                if (_chatBoxGO == null) return;
                if (_bilingualDoneFor == _chatBoxGO) return;
                Transform root = _canvasGO != null ? _canvasGO.transform : _chatBoxGO.transform;
                if (root == null) return;   // 下一帧重试
                // ① TMP 文本（设置页那套）
                TextMeshProUGUI[] tmps = root.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < tmps.Length; i++)
                {
                    TextMeshProUGUI t = tmps[i];
                    if (t == null || t.text == null) continue;
                    string cur = t.text;
                    if (cur.Contains(" / ")) continue;
                    if (cur == "发送") t.text = "发送 / Send";
                    else if (cur == "调试") t.text = "调试 / Debug";
                    else if (cur == "战报") t.text = "战报 / Report";
                    else if (cur == "返回") t.text = "返回 / Back";
                    else if (cur.Contains("点击这里输入文字")) t.text = "点击这里输入文字… / Type here…";
                }
                // ② UGUI Text 文本（模组自带的聊天框按钮/输入框占位符用的是 UGUI Text！）
                UnityEngine.UI.Text[] uts = root.GetComponentsInChildren<UnityEngine.UI.Text>(true);
                for (int i = 0; i < uts.Length; i++)
                {
                    UnityEngine.UI.Text t = uts[i];
                    if (t == null || t.text == null) continue;
                    string cur = t.text;
                    if (cur.Contains(" / ")) continue;
                    if (cur == "发送") t.text = "发送 / Send";
                    else if (cur == "调试") t.text = "调试 / Debug";
                    else if (cur == "战报") t.text = "战报 / Report";
                    else if (cur == "返回") t.text = "返回 / Back";
                    else if (cur.Contains("点击这里输入文字")) t.text = "点击这里输入文字… / Type here…";
                }
                _bilingualDoneFor = _chatBoxGO;
            }
            catch (Exception) { }
        }

        // ★ 第92轮：显示层自愈检查（1 秒节流；只作用于本插件的聊天显示层）
        private static void VerifyOverlayState()
        {
            try
            {
                if (Time.unscaledTime - _lastVerifyAt < 1f) return;
                _lastVerifyAt = Time.unscaledTime;
                bool fontBad = (_font == null) || (_text != null && _text.font == null);
                bool inactive = _go != null && !_go.activeSelf;
                bool texDisabled = _text != null && !_text.enabled;
                bool mismatch = (_text != null && _lastRendered != null && _text.text != _lastRendered);
                if (fontBad || inactive || texDisabled || mismatch)
                {
                    if (fontBad)
                    {
                        _font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", FONT_SIZE);
                        if (_font == null) _font = Font.CreateDynamicFontFromOSFont("SimHei", FONT_SIZE);
                        if (_text != null) _text.font = _font;
                    }
                    if (inactive && _go != null) _go.SetActive(true);
                    if (texDisabled && _text != null) _text.enabled = true;
                    if (mismatch) _lastRendered = null;
                    string sig = "font=" + (fontBad ? "坏" : "好") + ";inactive=" + inactive + ";disabled=" + texDisabled + ";mismatch=" + mismatch;
                    if (sig != _lastStateSig)
                    {
                        _lastStateSig = sig;
                        LogWarn("[AIChat][UI] 显示层自愈：" + sig + "（已修复并强制重绘）");
                        DumpUiState("自愈");
                    }
                }
            }
            catch (Exception) { }
        }

        // ★ 第92轮：UI 状态取证（点击战报前后各一次，精确记录组件真实状态）
        internal static void DumpUiState(string tag)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("[AIChat][UI取证] ").Append(tag);
                DumpOne(sb, "OverlayClip", _root);
                DumpOne(sb, "OverlayText", _go);
                DumpOne(sb, "ChatText", FindSceneObject("ChatText"));
                sb.Append(" ｜ msgs=").Append(_msgs.Count)
                  .Append(" wrapped=").Append(_wrapped.Count)
                  .Append(" wrapVer=").Append(_wrapVer).Append("/").Append(_lastWrapVer)
                  .Append(" dirty=").Append(_dirty)
                  .Append(" clip=").Append(_clipW.ToString("F0")).Append("x").Append(_clipH.ToString("F0"))
                  .Append(" fixedBoxH=").Append(_fixedBoxH.ToString("F0"))
                  .Append(" lastRendered=").Append(_lastRendered == null ? "null" : ((_lastRendered.Length).ToString() + "字"));
                if (_text != null)
                {
                    sb.Append(" ｜ UI文本: 长度=").Append(_text.text == null ? -1 : _text.text.Length)
                      .Append(" enabled=").Append(_text.enabled)
                      .Append(" colorA=").Append(_text.color.a.ToString("F2"));
                    try { sb.Append(" font=").Append(_text.font == null ? "null" : _text.font.name); } catch (Exception) { sb.Append(" font=?"); }
                    try
                    {
                        CanvasRenderer cr = _go != null ? _go.GetComponent<CanvasRenderer>() : null;
                        if (cr != null) sb.Append(" cull=").Append(cr.cull).Append(" alpha=").Append(cr.GetAlpha().ToString("F2"));
                    }
                    catch (Exception) { }
                    try
                    {
                        var m = _text.materialForRendering;
                        if (m != null && m.shader != null) sb.Append(" shader=").Append(m.shader.name);
                    }
                    catch (Exception) { }
                    try
                    {
                        var rr = _text.rectTransform;
                        Vector3[] c = new Vector3[4];
                        rr.GetWorldCorners(c);
                        sb.Append(" 屏矩形=").Append(c[0].x.ToString("F0")).Append(",").Append(c[0].y.ToString("F0"))
                          .Append("~").Append(c[2].x.ToString("F0")).Append(",").Append(c[2].y.ToString("F0"));
                    }
                    catch (Exception) { }
                }
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception) { }
        }

        private static void DumpOne(StringBuilder sb, string label, GameObject go)
        {
            try
            {
                if (go == null) { sb.Append(" ｜ ").Append(label).Append("=null"); return; }
                sb.Append(" ｜ ").Append(label).Append(": activeSelf=").Append(go.activeSelf)
                  .Append(" activeHierarchy=").Append(go.activeInHierarchy)
                  .Append(" path=").Append(PathOf(go.transform));
                Text ut = go.GetComponent<Text>();
                if (ut != null) sb.Append(" UGUI.enabled=").Append(ut.enabled);
            }
            catch (Exception) { }
        }

        private static string PathOf(Transform t)
        {
            try
            {
                string p = t.name;
                Transform c = t.parent;
                int n = 0;
                while (c != null && n < 6) { p = c.name + "/" + p; c = c.parent; n++; }
                return p;
            }
            catch (Exception) { return "?"; }
        }

        private static bool Refs()
        {
            try
            {
                ApplyChatBilingualOnce();
                if (_chatBoxRt == null)
                {
                    if (_chatBoxGO == null) _chatBoxGO = FindSceneObject("ChatBox");
                    if (_chatBoxGO != null) _chatBoxRt = _chatBoxGO.GetComponent<RectTransform>();
                }
                if (_inputRt == null)
                {
                    if (_inputGO == null) _inputGO = FindSceneObject("InputBox");
                    if (_inputGO != null) _inputRt = _inputGO.GetComponent<RectTransform>();
                }
                string[] names = { "Btn_发送", "Btn_调试", "Btn_战报", "Btn_返回" };
                for (int i = 0; i < 4; i++)
                {
                    if (_btnRt[i] == null)
                    {
                        if (_btnGO[i] == null) _btnGO[i] = FindSceneObject(names[i]);
                        if (_btnGO[i] != null) _btnRt[i] = _btnGO[i].GetComponent<RectTransform>();
                    }
                }
                _refsOk = (_chatBoxRt != null && _inputRt != null
                           && _btnRt[0] != null && _btnRt[1] != null && _btnRt[2] != null && _btnRt[3] != null);
                return _refsOk;
            }
            catch (Exception) { return false; }
        }

        // 判断某个 Transform 是否挂在 Btn_* 按钮下面（第38轮：用于识别游离的按钮文字）
        private static bool UnderButton(Transform tr)
        {
            try
            {
                Transform cur = tr;
                for (int d = 0; d < 5 && cur != null; d++)
                {
                    string nm = cur.name;
                    if (nm != null && nm.StartsWith("Btn_")) return true;
                    cur = cur.parent;
                }
            }
            catch (Exception) { }
            return false;
        }

        // 只在数值变化时才写 RectTransform，避免每帧触发 UI 重建
        private static void ApplyRect(RectTransform rt, Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            if (rt == null) return;
            if (rt.anchorMin != aMin) rt.anchorMin = aMin;
            if (rt.anchorMax != aMax) rt.anchorMax = aMax;
            if (rt.pivot != pivot) rt.pivot = pivot;
            if (rt.sizeDelta != size) rt.sizeDelta = size;
            if (rt.anchoredPosition != pos) rt.anchoredPosition = pos;
        }

        // 细白线（用于底部三段的分隔线/单元格竖线）
        private static RectTransform CreateLine(Transform parent, string name)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            var img = go.GetComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.92f);
            img.raycastTarget = false;
            return rt;
        }

        // 画布级定位：anchor/pivot 都在左上角，参数是世界坐标（1080p 下=屏幕像素）
        private static void PositionLine(RectTransform rt, float xWorld, float yTopWorld, float wWorld, float hWorld)
        {
            if (rt == null) return;
            ApplyRect(rt,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2((xWorld - _canvasLeft) / _canvasScale, (yTopWorld - _canvasTop) / _canvasScale),
                new Vector2(wWorld / _canvasScale, hWorld / _canvasScale));
        }

        // ------------------------------------------------------------------
        // 主布局
        // ------------------------------------------------------------------
        private static void Layout()
        {
            if (!Refs() || Time.unscaledTime < _armAt)
            {
                FallbackLayout();
                return;
            }
            var cc = _corners;
            _chatBoxRt.GetWorldCorners(cc);
            float x0 = Math.Min(cc[0].x, Math.Min(cc[1].x, Math.Min(cc[2].x, cc[3].x)));
            float x1 = Math.Max(cc[0].x, Math.Max(cc[1].x, Math.Max(cc[2].x, cc[3].x)));
            float y0 = Math.Min(cc[0].y, Math.Min(cc[1].y, Math.Min(cc[2].y, cc[3].y)));
            float y1 = Math.Max(cc[0].y, Math.Max(cc[1].y, Math.Max(cc[2].y, cc[3].y)));
            float boxW = Mathf.Max(60f, x1 - x0);
            float curH = y1 - y0;
            float centerX = (x0 + x1) * 0.5f;

            // 换行宽度随框宽变化
            float wrapW = Mathf.Max(60f, boxW - 20f);
            if (Math.Abs(wrapW - _wrapWidth) > 0.5f)
            {
                _wrapWidth = wrapW;
                _dirty = true;
            }
            if (_dirty) RebuildWrapped();
            float lineH = LineHeight();

            // 框大小：**局内固定**（来自 MOD 设置）；本插件不改宽高，只捕捉"设置被改过"后的新尺寸
            if (_fixedBoxH < 100f || Math.Abs(curH - _fixedBoxH) > 1.0f)
            {
                // ★ 第89轮：异常尺寸（突然变高很多/超出屏幕）不采纳，避免整个聊天框被拉满屏
                float maxH = Mathf.Max(300f, UnityEngine.Screen.height * 0.85f);
                if (curH > maxH)
                {
                    if (Time.unscaledTime - _lastSizeWarn > 2f)
                    {
                        _lastSizeWarn = Time.unscaledTime;
                        LogWarn("[AIChat][布局] 忽略异常聊天框高度：" + curH.ToString("F0") + "px（屏高=" + UnityEngine.Screen.height
                            + "，当前固定=" + _fixedBoxH.ToString("F0") + "）");
                    }
                }
                else
                {
                    _fixedBoxH = Mathf.Max(120f, curH);
                }
            }
            float boxH = _fixedBoxH;

            // 聊天框中线 = 屏幕中线：锚到画布垂直中线（anchoredPosition.y = 0）
            ApplyRect(_chatBoxRt,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2((centerX - _canvasLeft) / _canvasScale, 0f),
                new Vector2(boxW, boxH));
            float boxTop = _canvasCenterY + boxH * 0.5f;
            float boxBottom = _canvasCenterY - boxH * 0.5f;

            // ② 输入框（框内底部）
            float inputCenterY = boxBottom + BOTTOM_PAD + INPUT_H * 0.5f;
            ApplyRect(_inputRt,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2((centerX - _canvasCenterX) / _canvasScale, (inputCenterY - _canvasCenterY) / _canvasScale),
                new Vector2(Mathf.Max(80f, boxW - 8f), INPUT_H));

            // ③ 按钮行（输入框上方，四等分框宽）
            float btnW = Mathf.Max(40f, (boxW - 8f - 3f * GAP) / 4f);
            float btnCenterY = boxBottom + BOTTOM_PAD + INPUT_H + GAP + BTN_H * 0.5f;
            string[] names = { "Btn_发送", "Btn_调试", "Btn_战报", "Btn_返回" };
            for (int i = 0; i < 4; i++)
            {
                var rt = _btnRt[i];
                if (rt == null) continue;
                float left = (centerX - boxW * 0.5f) + 4f + i * (btnW + GAP);
                ApplyRect(rt,
                    new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2((left + btnW * 0.5f - _canvasCenterX) / _canvasScale,
                                (btnCenterY - _canvasCenterY) / _canvasScale),
                    new Vector2(btnW, BTN_H));
                try
                {
                    // 按钮单元格：浅色底（配合分隔线形成"表格"观感）
                    var btn = _btnGO[i].GetComponent<Button>();
                    if (btn != null)
                    {
                        ColorBlock cb = btn.colors;
                        Color target = new Color(1f, 1f, 1f, 0.22f);
                        if (cb.normalColor != target)
                        {
                            cb.normalColor = target;
                            cb.highlightedColor = new Color(1f, 1f, 1f, 0.45f);
                            cb.pressedColor = new Color(1f, 0.82f, 0.29f, 0.85f);
                            btn.colors = cb;
                        }
                    }
                }
                catch (Exception) { }
            }

            // ★ 第92轮：给「战报」按钮挂"仅取证"回调（不触发战报，避免双链路；用于定位点击后 UI 状态）
            try
            {
                if (_btnGO[2] != null && _reportBtnHooked != _btnGO[2])
                {
                    var rb = _btnGO[2].GetComponent<Button>();
                    if (rb != null)
                    {
                        rb.onClick.AddListener(new UnityEngine.Events.UnityAction(delegate
                        {
                            try { DumpUiState("战报按钮点击-立即"); } catch (Exception) { }
                            _dumpAt = Time.unscaledTime + 1.2f;
                            _dumpTag = "战报按钮点击后1.2s";
                        }));
                        _reportBtnHooked = _btnGO[2];
                        Log("[AIChat][UI] 已给「战报」按钮挂取证回调（仅记录状态，不重复触发战报）");
                    }
                }
            }
            catch (Exception) { }
            // ★ 第37轮：「调试」按钮 → 打开调试窗口（每 1.5 秒重挂一次，保证 Lua 的旧调试回调不会同时触发）
            try
            {
                if (_btnGO[1] != null && Time.unscaledTime - _lastDebugHookAt > 1.5f)
                {
                    var db = _btnGO[1].GetComponent<Button>();
                    if (db != null)
                    {
                        db.onClick.RemoveAllListeners();
                        db.onClick.AddListener(OnDebugButtonClicked);
                        _lastDebugHookAt = Time.unscaledTime;
                        if (_debugBtnHooked != _btnGO[1])
                        {
                            _debugBtnHooked = _btnGO[1];
                            Log("[AIChat][调试] 已接管「调试」按钮 → 打开调试窗口");
                        }
                    }
                }
            }
            catch (Exception) { }

            try
            {
                // ★ 第38轮：定时巡检（不只一次）：隐藏手工 UI 残留 + 游离的按钮文字（聊天框里那个"返回"）
                if (_chatBoxGO != null && Time.unscaledTime - _lastStrayScan > 3f)
                {
                    _lastStrayScan = Time.unscaledTime;
                    int hidden = 0;
                    var texts = _chatBoxGO.GetComponentsInChildren<Text>(true);
                    for (int i = 0; i < texts.Length; i++)
                    {
                        if (texts[i] == null) continue;
                        string nm = texts[i].gameObject.name;
                        if (nm.StartsWith("AIChat_Txt_"))
                        {
                            texts[i].gameObject.SetActive(false);
                            hidden++;
                        }
                    }
                    // 游离的 "Label"：不在任何 Btn_* 按钮下面的（正常按钮文字都在 Btn_xxx/Label）
                    if (_canvasGO != null)
                    {
                        var allTexts = _canvasGO.GetComponentsInChildren<Text>(true);
                        for (int i = 0; i < allTexts.Length; i++)
                        {
                            Text t = allTexts[i];
                            if (t == null) continue;
                            if (t.gameObject.name != "Label") continue;
                            if (!t.gameObject.activeSelf) continue;
                            if (UnderButton(t.transform)) continue;
                            t.gameObject.SetActive(false);
                            hidden++;
                        }
                    }
                    // ★ 第90轮：清掉游离的"返回"文字（不在 Btn_返回 下的孤立标签）
                    if (_canvasGO != null)
                    {
                        var tmpAll = _canvasGO.GetComponentsInChildren<TextMeshProUGUI>(true);
                        for (int i = 0; i < tmpAll.Length; i++)
                        {
                            var t = tmpAll[i];
                            if (t == null || t.text == null) continue;
                            string cur = t.text.Trim();
                            if (cur != "返回" && cur != "返回 / Back") continue;
                            if (UnderButton(t.transform)) continue;
                            t.gameObject.SetActive(false);
                            hidden++;
                        }
                    }
                    _strayHiddenFor = _chatBoxGO;
                    if (hidden > 0)
                        Log("[AIChat][UI] 已隐藏残留文字层 " + hidden + " 个（手工 UI/游离按钮文字）");
                }
            }
            catch (Exception) { }

            // ④ 文字裁剪区：框顶+8 → 按钮行上沿-4（高度 = 框高 - 104，固定）
            float clipTop = boxTop - TOP_PAD;
            float clipBottom = boxBottom + ROWS_H;          // 按钮行上沿 + 4
            float clipH = Mathf.Max(lineH, clipTop - clipBottom);
            float clipW = Mathf.Max(40f, boxW - 16f);
            if (Math.Abs(clipW - _clipW) > 0.5f || Math.Abs(clipH - _clipH) > 0.5f)
            {
                _clipW = clipW;
                _clipH = clipH;
            }
            _rootRt.anchorMin = new Vector2(0f, 1f);
            _rootRt.anchorMax = new Vector2(0f, 1f);
            _rootRt.pivot = new Vector2(0f, 1f);
            _rootRt.sizeDelta = new Vector2(clipW, clipH);
            _rootRt.anchoredPosition = new Vector2((centerX - boxW * 0.5f + 8f - _canvasLeft) / _canvasScale,
                                                   (clipTop - _canvasTop) / _canvasScale);
            _rootRt.SetAsLastSibling();

            // ⑤ 底部"表格"线（第30轮）：文字/按钮 分隔线、按钮/输入框 分隔线、按钮四等分竖线
            if (_gridRoot != null && _lineTop != null && _lineBottom != null)
            {
                float boxLeft = centerX - boxW * 0.5f;
                float yTextBottom = boxBottom + ROWS_H;                          // 96
                float yBtnTop = boxBottom + BOTTOM_PAD + INPUT_H + GAP + BTN_H;  // 92
                float yBtnBottom = boxBottom + BOTTOM_PAD + INPUT_H + GAP;       // 56
                float yInputTop = boxBottom + BOTTOM_PAD + INPUT_H;              // 52
                float yLineTop = (yTextBottom + yBtnTop) * 0.5f;                 // 94
                float yLineBottom = (yBtnBottom + yInputTop) * 0.5f;             // 54
                PositionLine(_lineTop, boxLeft, yLineTop, boxW, 2f);
                PositionLine(_lineBottom, boxLeft, yLineBottom, boxW, 2f);
                for (int i = 0; i < 3; i++)
                {
                    float boundary = boxLeft + 4f + (i + 1) * (btnW + GAP) - GAP * 0.5f;
                    PositionLine(_vLines[i], boundary - 1f, yLineTop, 2f, yLineTop - yLineBottom);
                }
                _gridRoot.transform.SetAsLastSibling();
            }
        }

        // 兜底：UI 对象找不到时，文字区 = 整个聊天框内部（不自动改框高）
        private static void FallbackLayout()
        {
            try
            {
                if (_chatBoxRt == null) return;
                var cc = new Vector3[4];
                _chatBoxRt.GetWorldCorners(cc);
                float x0 = Math.Min(cc[0].x, Math.Min(cc[1].x, Math.Min(cc[2].x, cc[3].x)));
                float x1 = Math.Max(cc[0].x, Math.Max(cc[1].x, Math.Max(cc[2].x, cc[3].x)));
                float y0 = Math.Min(cc[0].y, Math.Min(cc[1].y, Math.Min(cc[2].y, cc[3].y)));
                float y1 = Math.Max(cc[0].y, Math.Max(cc[1].y, Math.Max(cc[2].y, cc[3].y)));
                float wrapW = Mathf.Max(60f, (x1 - x0) - 20f);
                if (Math.Abs(wrapW - _wrapWidth) > 0.5f) { _wrapWidth = wrapW; _dirty = true; }
                if (_dirty) RebuildWrapped();
                _clipW = Mathf.Max(40f, (x1 - x0) - 16f);
                _clipH = Mathf.Max(30f, (y1 - y0) - 16f);
                _rootRt.anchorMin = new Vector2(0f, 1f);
                _rootRt.anchorMax = new Vector2(0f, 1f);
                _rootRt.pivot = new Vector2(0f, 1f);
                _rootRt.sizeDelta = new Vector2(_clipW, _clipH);
                _rootRt.anchoredPosition = new Vector2((x0 + 8f - _canvasLeft) / _canvasScale,
                                                       (y1 - 8f - _canvasTop) / _canvasScale);
                _rootRt.SetAsLastSibling();
            }
            catch (Exception) { }
        }

        private static void RebuildWrapped()
        {
            if (_forceRewrap)
            {
                _forceRewrap = false;
                for (int i = 0; i < _msgs.Count; i++)
                {
                    if (_msgs[i] != null) { _msgs[i].Wrapped = null; _msgs[i].WrappedWidth = -1f; }
                }
            }
            _wrapped.Clear();
            for (int i = 0; i < _msgs.Count; i++)
            {
                WrapStyled(_msgs[i], _wrapWidth);
            }
            _lastVisualLines = _wrapped.Count;
            _dirty = false;
            _wrapVer++;   // ★ 第66轮：换行结果更新后统一 bump（供 Render 判断）
        }

        private static float LineHeight()
        {
            if (_lineH > 0f) return _lineH;
            try
            {
                _text.text = "测";
                float h1 = _text.preferredHeight;
                _text.text = "测\n测";
                float h2 = _text.preferredHeight;
                _text.text = "";
                _lineH = Mathf.Max(8f, h2 - h1);
            }
            catch (Exception) { }
            if (_lineH <= 0f) _lineH = FONT_SIZE * 1.35f;
            return _lineH;
        }

        // ------------------------------------------------------------------
        // 渲染：只显示最新的 N 行（顶部对齐；旧行从顶部被顶出）
        // ------------------------------------------------------------------
        private static void Render()
        {
            try
            {
                if (_text == null) return;
                // ★ 第92轮：先确认实际组件状态，避免"缓存认为已显示、实际被清空"导致永久空白
                if (!_text.enabled || !_text.gameObject.activeInHierarchy)
                {
                    _text.enabled = true;
                    _text.gameObject.SetActive(true);
                    _lastRendered = null;
                    LogWarn("[AIChat][UI] 渲染前发现文字组件被禁用/失活，已重新启用");
                }
                if (_lastRendered != null && _text.text != _lastRendered)
                {
                    // 实际文本被外部改写（例如 Lua/游戏 UI 抢写）→ 强制重绘
                    if (_text.text.Length == 0 && _lastRendered.Length > 0)
                        LogWarn("[AIChat][UI] 检测到实际 Text 被清空（缓存=" + _lastRendered.Length + "字），强制重绘");
                    _lastRendered = null;
                }
                float lineH = LineHeight();
                // ★ 第49轮：没有新内容/尺寸变化就不重拼字符串（原来每帧都拼一次，产生 GC）
                if (_lastRendered != null && _wrapVer == _lastWrapVer
                    && Math.Abs(_clipH - _lastClipHRender) < 0.01f
                    && Math.Abs(lineH - _lastLineHRender) < 0.01f)
                    return;
                // ★ 第69轮：渲染节流——0.12 秒内最多重设一次 Text（批量合并高频新台词，减少 UGUI 网格重建卡顿）
                if (Time.unscaledTime - _lastRenderAt < 0.25f) return;   // ★ 第87轮：进一步降渲染频率
                _lastRenderAt = Time.unscaledTime;
                _lastWrapVer = _wrapVer;
                _lastClipHRender = _clipH;
                _lastLineHRender = lineH;
                int maxLines = Mathf.Max(1, (int)(_clipH / lineH + 0.01f));
                if (_wrapped.Count == 0)
                {
                    if (_lastRendered != "")
                    {
                        _text.text = "";
                        _lastRendered = "";
                        _lastShownLines = 0;
                    }
                    return;
                }
                int skip = Mathf.Max(0, _wrapped.Count - maxLines);
                StringBuilder sb = new StringBuilder();
                for (int i = skip; i < _wrapped.Count; i++)
                {
                    if (i > skip) sb.Append('\n');
                    sb.Append(_wrapped[i]);
                }
                string joined = sb.ToString();
                if (joined != _lastRendered)
                {
                    _text.alignment = TextAnchor.UpperLeft;
                    _text.text = joined;
                    try { ChatEngine.LastHeavy = "聊天渲染:" + joined.Length + "字/" + (_wrapped.Count - skip) + "行"; } catch (Exception) { }
                    _lastRendered = joined;
                    _lastShownLines = _wrapped.Count - skip;
                }
            }
            catch (Exception e)
            {
                LogWarn("[AIChat][PlanB4] 渲染失败：" + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // 手工字符级换行 + 按字符颜色拼富文本（富文本标签不参与宽度计算）
        // ------------------------------------------------------------------
        private static void WrapStyled(Msg m, float maxW)
        {
            // ★ 第66轮：增量换行——宽度没变就直接复用该条缓存，新台词只排版自己
            if (m != null && m.Wrapped != null && Math.Abs(m.WrappedWidth - maxW) < 0.01f)
            {
                _wrapped.AddRange(m.Wrapped);
                return;
            }
            if (m == null || string.IsNullOrEmpty(m.Text)) { m.Wrapped = new List<string>(); m.Wrapped.Add(""); m.WrappedWidth = maxW; _wrapped.Add(""); return; }
            List<string> lines = new List<string>();
            string s = m.Text;
            byte[] styles = m.Styles;
            try { if (_font != null) _font.RequestCharactersInTexture(s, FONT_SIZE, FontStyle.Normal); } catch (Exception) { }
            int n = s.Length;
            int lineStart = 0;
            while (lineStart < n)
            {
                while (lineStart < n && s[lineStart] == ' ') lineStart++;
                if (lineStart >= n) break;
                float w = 0f;
                int cut = lineStart;
                while (cut < n)
                {
                    float cw = CharWidth(s[cut]);
                    if (w + cw > maxW && cut > lineStart) break;
                    w += cw;
                    cut++;
                }
                if (cut < n && cut > lineStart + 1)
                {
                    int c2 = cut;
                    while (c2 > lineStart + 1)
                    {
                        if (AsciiWord(s[c2]) && AsciiWord(s[c2 - 1])) { c2--; continue; }
                        if (s[c2 - 1] == '@') { c2--; continue; }
                        break;
                    }
                    cut = c2;
                }
                if (cut <= lineStart) cut = lineStart + 1;
                lines.Add(BuildRich(s, styles, m.Colors, lineStart, cut, m.BoldStart, m.BoldEnd));
                lineStart = cut;
            }
            m.Wrapped = lines;
            m.WrappedWidth = maxW;
            _wrapped.AddRange(lines);
        }

        private static string BuildRich(string s, byte[] styles, Color[] colors, int start, int end, int boldStart, int boldEnd)
        {
            // ★ 第64轮：有逐字颜色（炫彩玩家名）时，按字符逐个上色
            if (colors != null)
            {
                StringBuilder rb = new StringBuilder(end - start + 60);
                for (int k = start; k < end; k++)
                {
                    bool bold = boldStart >= 0 && boldEnd > boldStart && k >= boldStart && k < boldEnd;
                    string col = null;
                    if (k < colors.Length)
                    {
                        Color c = colors[k];
                        if (c.r >= 0f) col = "#" + ColorUtility.ToHtmlStringRGB(c);
                    }
                    if (col == null) col = ColorHex(styles[k]);
                    if (col != null) rb.Append("<color=").Append(col).Append('>');
                    if (bold) rb.Append("<b>");
                    rb.Append(s[k]);
                    if (bold) rb.Append("</b>");
                    if (col != null) rb.Append("</color>");
                }
                return rb.ToString();
            }
            StringBuilder sb = new StringBuilder(end - start + 40);
            int i = start;
            while (i < end)
            {
                int st = styles[i];
                int j = i + 1;
                while (j < end && styles[j] == st) j++;
                string color = ColorHex(st);
                if (color != null) sb.Append("<color=").Append(color).Append('>');
                bool hasBold = boldStart >= 0 && boldEnd > boldStart && i < boldEnd && j > boldStart;
                if (!hasBold)
                {
                    sb.Append(s, i, j - i);
                }
                else
                {
                    int b0 = Math.Max(i, boldStart);
                    int b1 = Math.Min(j, boldEnd);
                    if (b0 > i) sb.Append(s, i, b0 - i);
                    sb.Append("<b>");
                    sb.Append(s, b0, b1 - b0);
                    sb.Append("</b>");
                    if (b1 < j) sb.Append(s, b1, j - b1);
                }
                if (color != null) sb.Append("</color>");
                i = j;
            }
            return sb.ToString();
        }

        private static string ColorHex(int style)
        {
            // ★ 第67轮：颜色可在 MOD 设置里改（蓝/红/黄/绿/橙/灰/黑/白/炫彩）
            if (style >= 1 && style <= 3)
            {
                string v = NameColors.Get(style);
                if (v == "Rainbow") return null;      // 炫彩由逐字颜色实现
                return NameColors.HexFor(v);
            }
            return null;
        }

        // ★ 第67轮：颜色改动后强制重排/重绘
        public static void MarkDirty()
        {
            _forceRewrap = true;
            _dirty = true;
        }
        private static bool _forceRewrap;

        private static bool AsciiWord(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == '-';
        }

        private static float CharWidth(char c)
        {
            try
            {
                CharacterInfo ci;
                if (_font != null && _font.GetCharacterInfo(c, out ci, FONT_SIZE, FontStyle.Normal))
                    return ci.advance;
            }
            catch (Exception) { }
            if (c == ' ') return FONT_SIZE * 0.30f;
            return FONT_SIZE * ((c < 128) ? 0.55f : 1.00f);
        }

        // ------------------------------------------------------------------
        private static void MaybeLog()
        {
            try
            {
                float now = Time.unscaledTime;
                if (now - _lastLogTime < 5f) return;
                _lastLogTime = now;
                string sample = _lastRendered == null ? "(空)" : _lastRendered.Replace("\n", "⏎");
                if (sample.Length > 130) sample = sample.Substring(0, 130) + "…";
                Log("[AIChat][PlanB4] 几何：框宽=" + ((int)(_clipW + 16f))
                    + " 框高=" + ((int)(_clipH + TOP_PAD + ROWS_H))
                    + " ｜ 行高=" + _lineH.ToString("0.0")
                    + " ｜ 消息数=" + _msgs.Count
                    + " ｜ 视觉行=" + _lastVisualLines
                    + " ｜ 显示行=" + _lastShownLines
                    + " ｜ 上限高=" + ((int)_fixedBoxH)
                    + " ｜ 文本样本=" + sample);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        private static GameObject FindSceneObject(string name)
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < all.Length; i++)
                {
                    var go = all[i];
                    if (go == null || go.name != name) continue;
                    if (go.scene.IsValid()) return go;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static void Log(string msg)
        {
            try { if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogInfo(msg); } catch (Exception) { }
        }

        private static void LogWarn(string msg)
        {
            try { if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning(msg); } catch (Exception) { }
        }
    }
}
