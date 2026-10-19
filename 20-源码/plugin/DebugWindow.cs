// RF AiChat Bridge —— 第42轮：AI 对话调试窗口 v3
// 依据用户反馈：
//   - 只有把列表滚到最底部后，窗口才跟随滚动（用户输入驱动的跟随判定，避免"一直在滚"）
//   - 列表/响应/请求详情 都加可拖拽滚动条
//   - 提示词拆分为：全局提示词（规则）/ 角色提示词（人设+风格）/ 角色上下文（事件提示词+元数据）
//   - 类型下拉新增：仅展示对话 / 仅展示非对话
//   - 耗时/状态列加同色底，简述截断，不再互相盖住
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RF.AiChat
{
    internal static class DebugWindow
    {
        private const float WIN_W = 1100f;
        private const float WIN_H = 620f;
        private const float ROW_H = 22f;
        private const int ROW_POOL = 20;
        private const int DEBUG_MAX = 500;

        private static readonly Color C_WIN = new Color(0.10f, 0.12f, 0.16f, 0.98f);
        private static readonly Color C_TITLE = new Color(0.17f, 0.21f, 0.27f, 1f);
        private static readonly Color C_PANEL = new Color(0.13f, 0.16f, 0.21f, 1f);
        private static readonly Color C_HDR = new Color(0.22f, 0.27f, 0.34f, 1f);
        private static readonly Color C_ROW = new Color(0.16f, 0.19f, 0.24f, 1f);
        private static readonly Color C_ROW_SEL = new Color(0.24f, 0.34f, 0.46f, 1f);
        private static readonly Color C_TEXT = new Color(0.92f, 0.94f, 0.97f, 1f);
        private static readonly Color C_DIM = new Color(0.65f, 0.70f, 0.76f, 1f);
        private static readonly Color C_BTN = new Color(0.25f, 0.33f, 0.43f, 1f);
        private static readonly Color C_BTN2 = new Color(0.34f, 0.42f, 0.52f, 1f);
        private static readonly Color C_CLOSE = new Color(0.55f, 0.28f, 0.30f, 1f);
        private static readonly Color C_INPUT = new Color(0.09f, 0.11f, 0.14f, 1f);
        private static readonly Color C_AREA = new Color(0.11f, 0.13f, 0.17f, 1f);
        private static readonly Color C_SB = new Color(0.16f, 0.19f, 0.24f, 1f);
        private static readonly Color C_SB_HANDLE = new Color(0.55f, 0.62f, 0.70f, 1f);

        private static Font _font;
        private static bool _built, _open, _en, _langLoaded;
        private static GameObject _canvasGO, _winGO, _blockerGO, _ddRoot;
        private static ScrollRect _scroll;
        private static RectTransform _viewportRt, _contentRt;
        private static readonly List<GameObject> _rowGO = new List<GameObject>();
        private static readonly List<Image> _rowBg = new List<Image>();
        private static readonly List<Image> _rowPatchMs = new List<Image>();
        private static readonly List<Image> _rowPatchSt = new List<Image>();
        private static readonly List<Text[]> _rowTxt = new List<Text[]>();
        private static readonly int[] _rowRec = new int[ROW_POOL];
        private static List<DebugRecord> _view = new List<DebugRecord>();
        private static DebugRecord _selRec;
        private static int _versionSeen = -1;
        private static float _lastScrollY = -999f;
        private static bool _followBottom;   // 只有列表滚到最底部时才为 true（跟随新记录）
        private static float _lastMaxY;      // 上一次刷新时的最大滚动距离（用于判定刷新前是否在底部）

        private static int _timeFilter;
        private static int _statusFilter;
        private static string _typeSel = "";
        private static string _search = "";
        private static InputField _searchField;

        private static Text _titleTxt, _stat1, _stat2, _hintTxt;
        private static Text _respTxt, _detailBox;
        private static ScrollRect _respScroll, _detailScroll;
        private static Text _timeBtnTxt, _typeBtnTxt, _statusBtnTxt, _searchBtnTxt, _recentTxt, _autoTxt, _langBtnTxt;
        private static readonly Text[] _hdrTxt = new Text[6];
        private static readonly Text[] _groupTxt = new Text[7];
        private static readonly Text[] _btnTxt = new Text[4];
        private static Text _respHdrTxt, _detailHdrTxt;

        private static int _openGroup = -1;
        private static DebugRecord _shown;
        private static string _snapshotCache = "";
        private static float _lastStatsAt = -999f;

        // ============================================================ 对外
        internal static void Toggle()
        {
            if (_open) Close(); else Open();
        }

        internal static void Open()
        {
            try
            {
                EnsureBuilt();
                if (_winGO == null) return;
                _open = true;
                _langLoaded = false;
                _winGO.SetActive(true);
                if (_blockerGO != null) _blockerGO.SetActive(true);
                SetGameInput(false);
                RefreshAll(true);
                SetHint(Lo("提示：把列表滚到最底部时才会跟随新记录；不在底部时窗口固定，只有滚动条变化。",
                          "Tip: follows new records only while scrolled to the bottom; otherwise the view stays fixed."));
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogInfo("[AIChat][调试窗] 已打开");
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AIChat][调试窗] 打开失败：" + e.Message);
            }
        }

        internal static void Close()
        {
            try
            {
                _open = false;
                CloseDropdown();
                if (_winGO != null) _winGO.SetActive(false);
                if (_blockerGO != null) _blockerGO.SetActive(false);
                SetGameInput(true);
            }
            catch (Exception) { }
        }

        internal static void Tick()
        {
            if (!_open) return;
            try
            {
                if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
                if (!_langLoaded) { _en = LoadLang(); _langLoaded = true; ApplyLanguage(); }
                if (ChatEngine.DebugVersion != _versionSeen) RefreshAll(false);

                if (_scroll != null && _contentRt != null)
                {
                    float y = _contentRt.anchoredPosition.y;
                    if (Math.Abs(y - _lastScrollY) > 0.5f)
                    {
                        _lastScrollY = y;
                        AssignRows();
                    }
                    // ★ 跟随判定：只看"列表当前是不是在底部"——
                    //   在底部 → 新记录自动滚进来；不在底部 → 窗口固定，只有滚动条变化
                    _followBottom = IsAtBottom();
                }
                if (Time.unscaledTime - _lastStatsAt > 1f)
                {
                    _lastStatsAt = Time.unscaledTime;
                    UpdateStats();
                }
            }
            catch (Exception) { }
        }

        // ============================================================ 构建
        private static void EnsureBuilt()
        {
            if (_built && _winGO != null && _canvasGO != null) return;
            try
            {
                EnsureEventSystem();
                if (_font == null)
                {
                    _font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 14);
                    if (_font == null) _font = Font.CreateDynamicFontFromOSFont("SimHei", 14);
                    if (_font == null) return;
                }
                if (_canvasGO == null)
                {
                    _canvasGO = new GameObject("AIChat_DebugWindowCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                    UnityEngine.Object.DontDestroyOnLoad(_canvasGO);
                    var canvas = _canvasGO.GetComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.sortingOrder = 31000;
                    var scaler = _canvasGO.GetComponent<CanvasScaler>();
                    scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    scaler.referenceResolution = new Vector2(1920f, 1080f);
                    scaler.matchWidthOrHeight = 0.5f;
                }
                _canvasGO.SetActive(true);
                if (_blockerGO == null)
                {
                    var bl = MakeImage(_canvasGO.transform, "Blocker", new Color(0f, 0f, 0f, 0.45f), 0, 0, 0, 0, true);
                    var rt = bl.rectTransform;
                    rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                    rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                    var bbtn = bl.gameObject.AddComponent<Button>();
                    bbtn.transition = Selectable.Transition.None;
                    bbtn.onClick.AddListener(new UnityAction(Close));
                    _blockerGO = bl.gameObject;
                }
                if (_winGO == null) BuildWindow();
                _built = true;
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AIChat][调试窗] 构建失败：" + e.Message + " @ " + e.StackTrace);
            }
        }

        private static void BuildWindow()
        {
            Image win = MakeImage(_canvasGO.transform, "Window", C_WIN, 0, 0, WIN_W, WIN_H, true);
            var wrt = win.rectTransform;
            wrt.anchorMin = new Vector2(0.5f, 0.5f);
            wrt.anchorMax = new Vector2(0.5f, 0.5f);
            wrt.pivot = new Vector2(0.5f, 0.5f);
            wrt.anchoredPosition = Vector2.zero;
            wrt.sizeDelta = new Vector2(WIN_W, WIN_H);
            _winGO = win.gameObject;

            Image title = MakeImage(win.transform, "TitleBar", C_TITLE, 0, 0, WIN_W, 30, true);
            _titleTxt = MakeText(title.transform, "Title", "AI 对话调试窗口", 12, 4, 620, 22, 15, C_TEXT, TextAnchor.MiddleLeft, true);
            _langBtnTxt = MakeButton(title.transform, "LangBtn", "中文 ▾", 850, 3, 120, 24, new UnityAction(OnLangClicked), 12);
            MakeButton(title.transform, "CloseX", "×", 1064, 3, 26, 24, new UnityAction(Close), 15);
            try
            {
                Transform cx = title.transform.Find("CloseX");
                if (cx != null)
                {
                    var cbx = cx.GetComponent<Button>();
                    if (cbx != null) cbx.colors = CloseColors();
                    var imgx = cx.GetComponent<Image>();
                    if (imgx != null) imgx.color = C_CLOSE;
                }
            }
            catch (Exception) { }

            Image filter = MakeImage(win.transform, "FilterBar", C_PANEL, 8, 34, WIN_W - 16, 34, true);
            _timeBtnTxt = MakeButton(filter.transform, "TimeBtn", "时间 ▾", 2, 5, 120, 24, new UnityAction(OnTimeClicked), 12);
            _typeBtnTxt = MakeButton(filter.transform, "TypeBtn", "类型 ▾", 126, 5, 120, 24, new UnityAction(OnTypeClicked), 12);
            _statusBtnTxt = MakeButton(filter.transform, "StatusBtn", "状态 ▾", 250, 5, 120, 24, new UnityAction(OnStatusClicked), 12);
            BuildSearchField(filter.transform, 378, 5, 300, 24);
            _searchBtnTxt = MakeButton(filter.transform, "SearchBtn", "搜索", 684, 5, 60, 24, new UnityAction(OnSearchClicked), 12);
            _recentTxt = MakeText(filter.transform, "Recent", "最近 500 条", 752, 3, 110, 28, 12, C_DIM, TextAnchor.MiddleLeft, false);
            _autoTxt = MakeText(filter.transform, "Auto", "自动刷新", 866, 3, 90, 28, 12, C_DIM, TextAnchor.MiddleLeft, false);
            MakeButton(filter.transform, "RefreshBtn", "刷新", 962, 5, 100, 24, new UnityAction(OnRefreshClicked), 12);

            Image listArea = MakeImage(win.transform, "ListArea", C_PANEL, 8, 72, 620, 418, true);
            string[] hdrs = { "时间戳", "事件", "角色", "简述", "耗时", "状态" };
            int[] hx = { 4, 96, 162, 250, 490, 548 };
            int[] hw = { 92, 66, 88, 240, 58, 52 };
            for (int i = 0; i < 6; i++)
            {
                var himg = MakeImage(listArea.transform, "Hdr" + i, C_HDR, hx[i], 2, hw[i], 24, false);
                _hdrTxt[i] = MakeText(himg.transform, "T", hdrs[i], 2, 0, hw[i] - 4, 24, 12, C_TEXT,
                    i >= 4 ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft, false);
            }
            BuildListScroll(listArea.transform, 620, 28, 390);

            Image right = MakeImage(win.transform, "RightArea", C_PANEL, 636, 72, 456, 418, true);
            var rimg = MakeImage(right.transform, "RespHdr", C_HDR, 0, 0, 456, 24, false);
            _respHdrTxt = MakeText(rimg.transform, "T", "响应（模型输出原文）", 4, 0, 448, 24, 12, C_TEXT, TextAnchor.MiddleLeft, false);
            _respScroll = MakeTextScroll(right.transform, "RespArea", 0, 24, 456, 118, 12, out _respTxt);

            var dimg = MakeImage(right.transform, "DetailHdr", C_HDR, 0, 146, 456, 24, false);
            _detailHdrTxt = MakeText(dimg.transform, "T", "请求详情（点击展开）", 4, 0, 448, 24, 12, C_TEXT, TextAnchor.MiddleLeft, false);
            for (int i = 0; i < 7; i++)
            {
                _groupTxt[i] = MakeButton(right.transform, "Group" + i, "▸ " + GroupName(i), 0, 174 + i * 22, 456, 20, MakeGroupAction(i), 11);
            }
            _detailScroll = MakeTextScroll(right.transform, "DetailArea", 0, 330, 456, 88, 12, out _detailBox);

            _stat1 = MakeText(win.transform, "Stat1", "", 10, 494, WIN_W - 20, 20, 12, C_TEXT, TextAnchor.MiddleLeft, false);
            _stat2 = MakeText(win.transform, "Stat2", "", 10, 514, WIN_W - 20, 20, 12, C_TEXT, TextAnchor.MiddleLeft, false);

            string[] btns = { "导出对话", "重置对话", "复制选中", "重新发送" };
            for (int i = 0; i < 4; i++)
            {
                float x = 10f + i * 272f;
                _btnTxt[i] = MakeButton(win.transform, "DBtn" + i, btns[i], x, 538, 260, 30, MakeBottomAction(i), 13);
            }
            _hintTxt = MakeText(win.transform, "Hint", "", 10, 574, WIN_W - 20, 22, 11, C_DIM, TextAnchor.MiddleLeft, false);
        }

        private static void BuildSearchField(Transform parent, float x, float y, float w, float h)
        {
            Image bg = MakeImage(parent, "Search", C_INPUT, x, y, w, h, true);
            bg.gameObject.AddComponent<RectMask2D>();
            _searchField = bg.gameObject.AddComponent<InputField>();
            _searchField.lineType = InputField.LineType.SingleLine;
            _searchField.characterLimit = 40;
            _searchField.targetGraphic = bg;

            Text txt = MakeText(bg.transform, "Text", "", 6, 0, w - 12, h, 12, C_TEXT, TextAnchor.MiddleLeft, false);
            txt.supportRichText = false;
            _searchField.textComponent = txt;

            Text ph = MakeText(bg.transform, "Placeholder", "搜索角色 / 关键词…", 6, 0, w - 12, h, 12, C_DIM, TextAnchor.MiddleLeft, false);
            ph.supportRichText = false;
            _searchField.placeholder = ph;

            _searchField.onValueChanged.AddListener(new UnityAction<string>(OnSearchValueChanged));
            _searchField.onEndEdit.AddListener(new UnityAction<string>(OnSearchValueChanged));
        }

        private static void BuildListScroll(Transform parent, float w, float top, float h)
        {
            Image area = MakeImage(parent, "ScrollArea", C_PANEL, 0, top, w, h, true);
            _scroll = area.gameObject.AddComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 30f;
            _scroll.inertia = false;

            GameObject vp = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            vp.transform.SetParent(area.transform, false);
            _viewportRt = vp.GetComponent<RectTransform>();
            _viewportRt.anchorMin = Vector2.zero;
            _viewportRt.anchorMax = Vector2.one;
            _viewportRt.offsetMin = Vector2.zero;
            _viewportRt.offsetMax = Vector2.zero;

            GameObject content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(vp.transform, false);
            _contentRt = content.GetComponent<RectTransform>();
            _contentRt.anchorMin = new Vector2(0f, 1f);
            _contentRt.anchorMax = new Vector2(1f, 1f);
            _contentRt.pivot = new Vector2(0.5f, 1f);
            _contentRt.anchoredPosition = Vector2.zero;
            _contentRt.sizeDelta = new Vector2(0f, h);
            _scroll.viewport = _viewportRt;
            _scroll.content = _contentRt;

            Scrollbar sb = MakeScrollbar(area.transform, w - 12f, 2f, 10f, h - 4f);
            _scroll.verticalScrollbar = sb;
            _scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            for (int p = 0; p < ROW_POOL; p++) BuildRow(p);
        }

        private static void BuildRow(int p)
        {
            GameObject row = new GameObject("Row" + p, typeof(RectTransform));
            row.transform.SetParent(_contentRt, false);
            var rt = row.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, ROW_H);
            rt.anchoredPosition = new Vector2(0f, 0f);

            Image bg = MakeImage(row.transform, "Bg", C_ROW, 0, 0, 0, 0, false);
            var brt = bg.rectTransform;
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
            brt.offsetMin = new Vector2(0f, 1f); brt.offsetMax = new Vector2(0f, 0f);

            int[] xs = { 4, 96, 162, 250, 490, 548 };
            int[] ws = { 92, 66, 88, 240, 58, 52 };
            Text[] texts = new Text[6];
            for (int i = 0; i < 4; i++)
            {
                texts[i] = MakeText(row.transform, "C" + i, "", xs[i], 0, ws[i], ROW_H, 11, C_TEXT,
                    TextAnchor.MiddleLeft, false);
                texts[i].horizontalOverflow = HorizontalWrapMode.Wrap;
                texts[i].verticalOverflow = VerticalWrapMode.Truncate;
            }
            // 耗时/状态：先铺两块与行同色的底，盖住简述溢出，再放文字
            Image pms = MakeImage(row.transform, "PatchMs", C_ROW, 486, 0, 62, ROW_H, false);
            Image pst = MakeImage(row.transform, "PatchSt", C_ROW, 544, 0, 60, ROW_H, false);
            texts[4] = MakeText(row.transform, "C4", "", xs[4], 0, ws[4], ROW_H, 11, C_TEXT, TextAnchor.MiddleCenter, false);
            texts[5] = MakeText(row.transform, "C5", "", xs[5], 0, ws[5], ROW_H, 11, C_TEXT, TextAnchor.MiddleCenter, false);
            texts[5].horizontalOverflow = HorizontalWrapMode.Overflow;

            Image hit = MakeImage(row.transform, "Hit", new Color(0f, 0f, 0f, 0f), 0, 0, 0, 0, true);
            var hrt = hit.rectTransform;
            hrt.anchorMin = Vector2.zero; hrt.anchorMax = Vector2.one;
            hrt.offsetMin = Vector2.zero; hrt.offsetMax = Vector2.zero;
            var btn = hit.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            int pp = p;
            btn.onClick.AddListener(delegate { OnRowClicked(pp); });

            _rowGO.Add(row);
            _rowBg.Add(bg);
            _rowPatchMs.Add(pms);
            _rowPatchSt.Add(pst);
            _rowTxt.Add(texts);
            _rowRec[p] = -1;
            row.SetActive(false);
        }

        // ============================================================ 滚动条 / 文本滚动区
        private static Scrollbar MakeScrollbar(Transform parent, float x, float yTop, float w, float h)
        {
            Image track = MakeImage(parent, "VScroll", C_SB, x, yTop, w, h, true);
            var sb = track.gameObject.AddComponent<Scrollbar>();
            sb.direction = Scrollbar.Direction.BottomToTop;

            Image area = MakeImage(track.transform, "SlidingArea", new Color(0f, 0f, 0f, 0f), 0, 0, 0, 0, false);
            var art = area.rectTransform;
            art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one;
            art.offsetMin = Vector2.zero; art.offsetMax = Vector2.zero;

            Image handle = MakeImage(area.transform, "Handle", C_SB_HANDLE, 0, 0, 0, 0, true);
            var hrt = handle.rectTransform;
            hrt.anchorMin = new Vector2(0f, 0f); hrt.anchorMax = new Vector2(1f, 1f);
            hrt.offsetMin = new Vector2(1f, 1f); hrt.offsetMax = new Vector2(-1f, -1f);

            sb.handleRect = hrt;
            sb.targetGraphic = handle;
            sb.size = 0.35f;
            sb.value = 1f;
            return sb;
        }

        private static ScrollRect MakeTextScroll(Transform parent, string name, float x, float yTop, float w, float h,
            float sbw, out Text text)
        {
            Image area = MakeImage(parent, name, C_AREA, x, yTop, w, h, true);
            var sr = area.gameObject.AddComponent<ScrollRect>();
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 25f;
            sr.inertia = false;

            GameObject vp = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            vp.transform.SetParent(area.transform, false);
            var vrt = vp.GetComponent<RectTransform>();
            vrt.anchorMin = Vector2.zero;
            vrt.anchorMax = Vector2.one;
            vrt.offsetMin = new Vector2(0f, 0f);
            vrt.offsetMax = new Vector2(-(sbw + 2f), 0f);

            GameObject content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(vp.transform, false);
            var crt = content.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.anchoredPosition = Vector2.zero;
            crt.sizeDelta = new Vector2(0f, h);

            text = MakeText(content.transform, "Text", "", 4, 2, w - sbw - 10f, h - 4f, 12, C_TEXT, TextAnchor.UpperLeft, false);
            var trt = text.rectTransform;
            trt.anchorMin = new Vector2(0f, 1f);
            trt.anchorMax = new Vector2(1f, 1f);
            trt.pivot = new Vector2(0.5f, 1f);
            trt.anchoredPosition = new Vector2(0f, 0f);
            trt.sizeDelta = new Vector2(-8f, h - 4f);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            sr.viewport = vrt;
            sr.content = crt;
            Scrollbar sb = MakeScrollbar(area.transform, w - sbw, 2f, sbw, h - 4f);
            sr.verticalScrollbar = sb;
            sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            return sr;
        }

        private static void SetScrollText(ScrollRect sr, Text t, string s)
        {
            try
            {
                if (t == null) return;
                string want = s == null ? "" : s;
                if (t.text == want) return;    // ★ 内容没变：保持滚动条位置，不重置到顶端
                t.text = want;
                if (sr == null || sr.content == null) return;
                Canvas.ForceUpdateCanvases();
                float ph = t.preferredHeight;
                float vh = sr.viewport != null ? sr.viewport.rect.height : 100f;
                sr.content.sizeDelta = new Vector2(0f, Mathf.Max(vh, ph + 12f));
                sr.verticalNormalizedPosition = 1f;
            }
            catch (Exception) { }
        }

        // ============================================================ 下拉列表
        private static void CloseDropdown()
        {
            if (_ddRoot != null)
            {
                try { UnityEngine.Object.Destroy(_ddRoot); } catch (Exception) { }
                _ddRoot = null;
            }
        }

        private static void ShowDropdown(float x, float yTop, float w, string[] labels, int current, UnityAction<int> onPick)
        {
            try
            {
                CloseDropdown();
                if (labels == null || labels.Length == 0) return;
                float ph = 4 + labels.Length * 22;
                Image panel = MakeImage(_winGO.transform, "DropDown", new Color(0.15f, 0.19f, 0.25f, 1f), x, yTop, w, ph, true);
                panel.gameObject.transform.SetAsLastSibling();
                _ddRoot = panel.gameObject;
                for (int i = 0; i < labels.Length; i++)
                {
                    int idx = i;
                    Color c = (i == current) ? new Color(0.26f, 0.36f, 0.48f, 1f) : new Color(0.17f, 0.21f, 0.27f, 1f);
                    Image item = MakeImage(panel.transform, "Item" + i, c, 2, 2 + i * 22, w - 4, 20, true);
                    MakeText(item.transform, "Label", labels[i], 8, 0, w - 16, 20, 12, C_TEXT, TextAnchor.MiddleLeft, false);
                    var btn = item.gameObject.AddComponent<Button>();
                    btn.targetGraphic = item;
                    btn.transition = Selectable.Transition.ColorTint;
                    ColorBlock cb = btn.colors;
                    cb.normalColor = c;
                    cb.highlightedColor = new Color(0.26f, 0.34f, 0.44f, 1f);
                    cb.pressedColor = new Color(0.20f, 0.28f, 0.38f, 1f);
                    btn.colors = cb;
                    btn.onClick.AddListener(delegate
                    {
                        CloseDropdown();
                        if (onPick != null) onPick(idx);
                    });
                }
            }
            catch (Exception) { }
        }

        private static void OnTimeClicked()
        {
            CloseDropdown();
            string[] labels = { Lo("全部", "All"), Lo("最近5分钟", "Last 5 min"), Lo("最近50条", "Last 50") };
            ShowDropdown(10f, 63f, 120f, labels, _timeFilter, delegate(int i)
            {
                _timeFilter = i;
                ApplyLanguage();
                RefreshAll(true);
            });
        }

        private static void OnStatusClicked()
        {
            CloseDropdown();
            string[] labels = { Lo("全部", "All"), Lo("成功", "OK"), Lo("失败", "Fail"), Lo("本地", "Local"), Lo("事件", "Event") };
            ShowDropdown(258f, 63f, 120f, labels, _statusFilter, delegate(int i)
            {
                _statusFilter = i;
                ApplyLanguage();
                RefreshAll(true);
            });
        }

        private static void OnTypeClicked()
        {
            CloseDropdown();
            List<string> cats = new List<string>();
            cats.Add("");
            cats.Add("__dialog__");
            cats.Add("__nondialog__");
            try
            {
                List<DebugRecord> all = ChatEngine.DebugSnapshot();
                for (int i = all.Count - 1; i >= 0 && cats.Count < 18; i--)
                {
                    string c = CanonCategory(all[i]);
                    if (c.Length > 0 && !cats.Contains(c)) cats.Add(c);
                }
            }
            catch (Exception) { }
            string[] labels = new string[cats.Count];
            int cur = 0;
            for (int i = 0; i < cats.Count; i++)
            {
                labels[i] = CategoryLabel(cats[i]);
                if (cats[i] == _typeSel) cur = i;
            }
            List<string> catsCopy = cats;
            ShowDropdown(134f, 63f, 160f, labels, cur, delegate(int i)
            {
                _typeSel = catsCopy[i];
                ApplyLanguage();
                RefreshAll(true);
            });
        }

        private static void OnLangClicked()
        {
            CloseDropdown();
            string[] labels = { "中文", "English" };
            ShowDropdown(850f, 27f, 120f, labels, _en ? 1 : 0, delegate(int i)
            {
                _en = (i == 1);
                SaveLang(_en ? "en" : "zh");
                ApplyLanguage();
                ShowSelected();
                UpdateStats();
                SetHint(Lo("界面语言已切换：中文", "UI language switched: English"));
            });
        }

        // ============================================================ 刷新
        private static void RefreshAll(bool resetScroll)
        {
            try
            {
                bool atBottomBefore = !resetScroll && _lastMaxY > 0f && _lastScrollY >= _lastMaxY - 3f;
                bool follow = !resetScroll && (_followBottom || atBottomBefore);
                _versionSeen = ChatEngine.DebugVersion;
                RebuildView();
                if (resetScroll)
                {
                    _selRec = null;
                }
                else if (_selRec != null && !_view.Contains(_selRec))
                {
                    _selRec = null;
                }

                float viewH = _viewportRt != null ? _viewportRt.rect.height : 390f;
                float contentH = Mathf.Max(viewH, _view.Count * ROW_H);
                if (_contentRt != null) _contentRt.sizeDelta = new Vector2(0f, contentH);
                float maxY = Mathf.Max(0f, contentH - viewH);
                _lastMaxY = maxY;

                if (resetScroll)
                {
                    _lastMaxY = 0f;
                    if (_scroll != null) _scroll.verticalNormalizedPosition = 1f;
                    if (_contentRt != null) _contentRt.anchoredPosition = new Vector2(0f, 0f);
                    _lastScrollY = 0f;
                }
                else if (follow)
                {
                    // 用户正停在最底部：新记录自动滚进来（用 ScrollRect 自己的归一化位置）
                    if (_scroll != null) _scroll.verticalNormalizedPosition = 0f;
                    _lastScrollY = _contentRt != null ? _contentRt.anchoredPosition.y : 0f;
                }
                else
                {
                    // 不在底部：窗口固定，保持阅读位置（只有滚动条长度变化）
                    float cur = _contentRt != null ? _contentRt.anchoredPosition.y : 0f;
                    if (cur > maxY) cur = maxY;
                    if (cur < 0f) cur = 0f;
                    if (_contentRt != null) _contentRt.anchoredPosition = new Vector2(0f, cur);
                    _lastScrollY = cur;
                }
                AssignRows();
                ShowSelected();
                UpdateStats();
                ApplyLanguage();
            }
            catch (Exception) { }
        }


        private static bool IsAtBottom()
        {
            try
            {
                if (_scroll == null || _contentRt == null || _viewportRt == null) return false;
                if (_contentRt.rect.height <= _viewportRt.rect.height + 1f) return false;  // 内容没满屏：不算在底部
                return _scroll.verticalNormalizedPosition <= 0.03f;
            }
            catch (Exception) { return false; }
        }

        private static void RebuildView()
        {
            List<DebugRecord> all = ChatEngine.DebugSnapshot();
            _view = new List<DebugRecord>();
            DateTime now = DateTime.Now;
            for (int i = 0; i < all.Count; i++)
            {
                DebugRecord r = all[i];
                if (r == null) continue;
                if (_timeFilter == 1 && (now - r.Time).TotalMinutes > 5.0) continue;
                if (!StatusMatch(r)) continue;
                if (!TypeMatch(r)) continue;
                if (_search.Length > 0 && !SearchMatch(r)) continue;
                _view.Add(r);
                if (_view.Count >= DEBUG_MAX) break;
            }
            if (_timeFilter == 2 && _view.Count > 50)
                _view.RemoveRange(0, _view.Count - 50);
        }

        private static bool TypeMatch(DebugRecord r)
        {
            if (_typeSel.Length == 0) return true;
            if (_typeSel == "__dialog__") return !r.IsEvent;
            if (_typeSel == "__nondialog__") return r.IsEvent;
            return CanonCategory(r) == _typeSel;
        }

        private static bool StatusMatch(DebugRecord r)
        {
            if (_statusFilter == 0) return true;
            if (_statusFilter == 1) return r.Status == "成功";
            if (_statusFilter == 2) return r.Status == "失败" || r.Status == "超时";
            if (_statusFilter == 3) return r.Status == "本地";
            if (_statusFilter == 4) return r.Status == "事件" || (r.IsEvent && r.Status != "本地");
            return true;
        }

        private static bool SearchMatch(DebugRecord r)
        {
            string s = _search;
            return Contains(r.Actor, s) || Contains(r.Kind, s) || Contains(r.Brief, s)
                || Contains(r.Response, s) || Contains(r.UserPrompt, s);
        }

        private static bool Contains(string src, string key)
        {
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(key)) return false;
            return src.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AssignRows()
        {
            try
            {
                if (_contentRt == null) return;
                float y = _contentRt.anchoredPosition.y;
                int first = Mathf.Max(0, Mathf.FloorToInt(y / ROW_H));
                for (int p = 0; p < ROW_POOL; p++)
                {
                    int idx = first + p;
                    if (idx >= 0 && idx < _view.Count)
                    {
                        _rowRec[p] = idx;
                        DebugRecord r = _view[idx];
                        _rowGO[p].SetActive(true);
                        var rt = _rowGO[p].GetComponent<RectTransform>();
                        rt.anchoredPosition = new Vector2(0f, -idx * ROW_H);
                        _rowTxt[p][0].text = r.Time.ToString("HH:mm:ss");
                        _rowTxt[p][1].text = CategoryLabel(CanonCategory(r));
                        _rowTxt[p][2].text = r.Actor;
                        _rowTxt[p][3].text = r.Brief;
                        _rowTxt[p][4].text = r.Ms >= 0 ? (r.Ms + "ms") : "—";
                        _rowTxt[p][5].text = r.Status;
                        bool sel = ReferenceEquals(_view[idx], _selRec);
                        Color rowc = sel ? C_ROW_SEL : C_ROW;
                        _rowBg[p].color = rowc;
                        _rowPatchMs[p].color = rowc;
                        _rowPatchSt[p].color = rowc;
                        _rowTxt[p][3].color = C_TEXT;
                        _rowTxt[p][4].color = C_TEXT;
                        _rowTxt[p][5].color = StatusColor(r.Status);
                    }
                    else
                    {
                        _rowRec[p] = -1;
                        _rowGO[p].SetActive(false);
                    }
                }
            }
            catch (Exception) { }
        }

        private static Color StatusColor(string s)
        {
            if (s == "失败" || s == "超时") return new Color(1f, 0.62f, 0.62f, 1f);
            if (s == "成功") return new Color(0.62f, 0.92f, 0.66f, 1f);
            if (s == "本地") return new Color(0.98f, 0.88f, 0.58f, 1f);
            return new Color(0.85f, 0.88f, 0.93f, 1f);
        }

        private static void OnRowClicked(int poolIdx)
        {
            try
            {
                CloseDropdown();
                int idx = _rowRec[poolIdx];
                if (idx < 0 || idx >= _view.Count) return;
                _selRec = _view[idx];
                _openGroup = -1;
                AssignRows();
                ShowSelected();
            }
            catch (Exception) { }
        }

        private static void ShowSelected()
        {
            try
            {
                DebugRecord r = _selRec;
                if (r == null || !_view.Contains(r)) r = null;
                _shown = r;
                if (r == null)
                {
                    SetScrollText(_respScroll, _respTxt, Lo("（左侧选一条记录）", "(select a record)"));
                    SetScrollText(_detailScroll, _detailBox, "");
                    UpdateGroupLabels();
                    return;
                }
                if (r.IsEvent) SetScrollText(_respScroll, _respTxt, r.Brief + (r.Meta.Length > 0 ? "\n\n" + r.Meta : ""));
                else SetScrollText(_respScroll, _respTxt, r.Response.Length > 0 ? r.Response : ("（" + (r.Error.Length > 0 ? r.Error : "空") + "）"));
                ShowGroup(_openGroup);
                UpdateGroupLabels();
            }
            catch (Exception) { }
        }

        private static void ShowGroup(int g)
        {
            DebugRecord r = _shown;
            if (r == null || g < 0) { SetScrollText(_detailScroll, _detailBox, ""); return; }
            string s = "";
            if (g == 0) s = r.GlobalPrompt.Length > 0 ? r.GlobalPrompt : r.SystemPrompt;
            else if (g == 1) s = r.CharacterPrompt.Length > 0 ? r.CharacterPrompt : "（无）";
            else if (g == 2)
            {
                s = "model = " + (r.Model.Length > 0 ? r.Model : AiChatPlugin.EffModel)
                  + "\nmax_tokens = " + (AiChatPlugin.CfgMaxTokens != null ? AiChatPlugin.CfgMaxTokens.Value.ToString() : "?")
                  + "\n超时 = " + (AiChatPlugin.CfgTimeout != null ? AiChatPlugin.CfgTimeout.Value.ToString() : "?") + " 秒"
                  + "\n接口 = " + AiChatPlugin.EffApiUrl
                  + "\n界面语言 = " + (_en ? "English" : "中文") + " / 台词语言 = " + ChatEngine.LangCode;
            }
            else if (g == 3)
            {
                // 角色上下文：原来放错位置的"用户提示词（事件/@）"迁到这里
                s = Lo("【事件提示词】", "[Event prompt]") + "\n" + (r.UserPrompt.Length > 0 ? r.UserPrompt : "（无）")
                  + "\n\n" + Lo("【角色上下文 / 元数据】", "[Actor context / meta]") + "\n" + (r.Meta.Length > 0 ? r.Meta : "（无）");
            }
            else if (g == 4)
            {
                if (_snapshotCache.Length == 0)
                {
                    try { _snapshotCache = string.Join("\n", BattleReport.Build().ToArray()); }
                    catch (Exception) { _snapshotCache = "（读取失败）"; }
                }
                s = _snapshotCache;
            }
            else if (g == 5) s = ChatEngine.DebugHistoryText();
            else if (g == 6)
            {
                s = "时间 = " + r.Time.ToString("yyyy-MM-dd HH:mm:ss")
                  + "\n耗时 = " + (r.Ms >= 0 ? r.Ms + " ms" : "—")
                  + "\n状态 = " + r.Status
                  + "\n模型 = " + r.Model
                  + "\ntokens = " + TokensText(r)
                  + "\n错误 = " + (r.Error.Length > 0 ? r.Error : "（无）");
            }
            if (string.IsNullOrEmpty(s)) s = Lo("（无内容）", "(empty)");
            SetScrollText(_detailScroll, _detailBox, s);
        }

        private static string TokensText(DebugRecord r)
        {
            if (r.PromptTokens < 0 && r.CompletionTokens < 0) return "—";
            return r.PromptTokens.ToString() + " / " + r.CompletionTokens.ToString();
        }

        private static void UpdateGroupLabels()
        {
            try
            {
                for (int i = 0; i < 7; i++)
                {
                    if (_groupTxt[i] == null) continue;
                    string arrow = (i == _openGroup) ? "▾ " : "▸ ";
                    _groupTxt[i].text = arrow + GroupName(i);
                    _groupTxt[i].color = (i == _openGroup) ? new Color(1f, 0.86f, 0.55f, 1f) : C_TEXT;
                }
            }
            catch (Exception) { }
        }

        private static string GroupName(int i)
        {
            switch (i)
            {
                case 0: return Lo("1. 全局提示词", "1. Global prompt");
                case 1: return Lo("2. 角色提示词", "2. Character prompt");
                case 2: return Lo("3. 请求参数", "3. Request params");
                case 3: return Lo("4. 角色上下文", "4. Actor context");
                case 4: return Lo("5. 战局快照", "5. Match snapshot");
                case 5: return Lo("6. 历史对话", "6. Chat history");
                case 6: return Lo("7. 响应元数据", "7. Response meta");
                default: return "";
            }
        }

        private static void UpdateStats()
        {
            try
            {
                List<DebugRecord> all = ChatEngine.DebugSnapshot();
                int calls = ChatEngine.DebugApiCalls;
                int fails = ChatEngine.DebugFails;
                int ok = Math.Max(0, calls - fails);
                long sum = 0; int n = 0;
                string lastTime = "—";
                int tok = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    DebugRecord r = all[i];
                    if (r == null) continue;
                    lastTime = r.Time.ToString("HH:mm:ss");
                    if (!r.IsEvent && r.Ms >= 0) { sum += r.Ms; n++; }
                    if (r.PromptTokens > 0) tok += r.PromptTokens;
                    if (r.CompletionTokens > 0) tok += r.CompletionTokens;
                }
                long avg = n > 0 ? sum / n : -1;
                string apiState = string.IsNullOrEmpty(ChatEngine.ApiKeyOverride) ? Lo("未配置 Key", "no key") : Lo("正常", "OK");
                if (_stat1 != null)
                {
                    _stat1.text = Lo("总调用 ", "Calls ") + calls + " ｜ " + Lo("成功 ", "OK ") + ok
                        + " ｜ " + Lo("失败 ", "Fail ") + fails
                        + " ｜ " + Lo("队列 ", "Queue ") + ChatEngine.DebugQueueCount
                        + " ｜ " + Lo("平均耗时 ", "Avg ") + (avg >= 0 ? avg + "ms" : "—")
                        + " ｜ " + Lo("语言 ", "Lang ") + ChatEngine.LangCode
                        + " ｜ " + Lo("模式 ", "Mode ") + BattleReport.ModeKeyPublic;
                }
                if (_stat2 != null)
                {
                    _stat2.text = Lo("最近请求：", "Last: ") + lastTime
                        + " ｜ API：" + apiState
                        + " ｜ tokens：" + (tok > 0 ? tok.ToString() : "—")
                        + " ｜ " + Lo("记录 ", "Records ") + all.Count + "/" + DEBUG_MAX
                        + " ｜ " + Lo("跟随：", "Follow: ") + (_followBottom ? Lo("开", "on") : Lo("关", "off"));
                }
            }
            catch (Exception) { }
        }

        private static void ApplyLanguage()
        {
            try
            {
                if (_titleTxt != null) _titleTxt.text = Lo("AI 对话调试窗口", "AI Chat Debug Window");
                if (_timeBtnTxt != null) _timeBtnTxt.text = Lo("时间：", "Time: ") + TimeFilterName() + " ▾";
                if (_typeBtnTxt != null) _typeBtnTxt.text = Lo("类型：", "Type: ") + CategoryLabel(_typeSel) + " ▾";
                if (_statusBtnTxt != null) _statusBtnTxt.text = Lo("状态：", "Status: ") + StatusFilterName() + " ▾";
                if (_langBtnTxt != null) _langBtnTxt.text = _en ? "English ▾" : "中文 ▾";
                if (_recentTxt != null) _recentTxt.text = Lo("最近 500 条", "Last 500");
                if (_autoTxt != null) _autoTxt.text = Lo("自动刷新", "Auto refresh");
                if (_searchBtnTxt != null) _searchBtnTxt.text = Lo("搜索", "Search");
                string[] hdrs = { Lo("时间戳", "Time"), Lo("事件", "Event"), Lo("角色", "Actor"),
                                  Lo("简述", "Brief"), Lo("耗时", "Ms"), Lo("状态", "Status") };
                for (int i = 0; i < 6; i++) if (_hdrTxt[i] != null) _hdrTxt[i].text = hdrs[i];
                if (_respHdrTxt != null) _respHdrTxt.text = Lo("响应（模型输出原文）", "Response (raw)");
                if (_detailHdrTxt != null) _detailHdrTxt.text = Lo("请求详情（点击展开）", "Request details (click)");
                string[] btns = { Lo("导出对话", "Export"), Lo("重置对话", "Reset"), Lo("复制选中", "Copy"), Lo("重新发送", "Resend") };
                for (int i = 0; i < 4; i++) if (_btnTxt[i] != null) _btnTxt[i].text = btns[i];
                UpdateGroupLabels();
            }
            catch (Exception) { }
        }

        private static string TimeFilterName()
        {
            if (_timeFilter == 0) return Lo("全部", "All");
            if (_timeFilter == 1) return Lo("最近5分钟", "Last 5 min");
            return Lo("最近50条", "Last 50");
        }

        private static string StatusFilterName()
        {
            if (_statusFilter == 0) return Lo("全部", "All");
            if (_statusFilter == 1) return Lo("成功", "OK");
            if (_statusFilter == 2) return Lo("失败", "Fail");
            if (_statusFilter == 3) return Lo("本地", "Local");
            return Lo("事件", "Event");
        }

        private static string CanonCategory(DebugRecord r)
        {
            if (r == null) return "";
            if (!r.IsEvent)
            {
                string k = r.Kind;
                if (k == "战报") return "战报";
                if (k.IndexOf("阵亡") >= 0) return "阵亡";
                if (k.IndexOf("击杀") >= 0) return "击杀";
                if (k.IndexOf("据点") >= 0) return "据点";
                if (k.IndexOf("闲聊") >= 0) return "闲聊";
                if (k.IndexOf("玩家发言") >= 0) return "玩家发言";
                if (k.IndexOf("调试重发") >= 0) return "调试重发";
                return "台词";
            }
            if (r.Status == "本地") return "本地";
            return r.Kind;
        }

        private static string CategoryLabel(string canon)
        {
            if (string.IsNullOrEmpty(canon)) return Lo("全部", "All");
            switch (canon)
            {
                case "__dialog__": return Lo("仅展示对话", "Dialogue only");
                case "__nondialog__": return Lo("仅展示非对话", "Non-dialogue only");
                case "战报": return Lo("战报", "Report");
                case "阵亡": return Lo("阵亡", "KIA");
                case "击杀": return Lo("击杀", "Kill");
                case "据点": return Lo("据点", "Point");
                case "闲聊": return Lo("闲聊", "Chatter");
                case "玩家发言": return Lo("玩家发言", "Player say");
                case "调试重发": return Lo("调试重发", "Resend");
                case "台词": return Lo("台词", "Line");
                case "本地": return Lo("本地", "Local");
                case "出生": return Lo("出生", "Spawn");
                case "指令": return Lo("指令", "Order");
                case "播报": return Lo("播报", "Radio");
                case "载具": return Lo("载具", "Vehicle");
                case "多杀": return Lo("多杀", "Multi-kill");
                case "对局": return Lo("对局", "Match");
                case "设置": return Lo("设置", "Settings");
                case "人设": return Lo("人设", "Persona");
                default: return canon;
            }
        }

        // ============================================================ 控件事件
        private static void OnSearchClicked()
        {
            CloseDropdown();
            if (_searchField != null) _search = _searchField.text == null ? "" : _searchField.text.Trim();
            RefreshAll(true);
        }

        private static void OnSearchValueChanged(string v)
        {
            _search = v == null ? "" : v.Trim();
            RefreshAll(false);
        }

        private static void OnRefreshClicked()
        {
            CloseDropdown();
            _versionSeen = -1;
            RefreshAll(true);
        }

        private static UnityAction MakeGroupAction(int i)
        {
            int gi = i;
            return delegate
            {
                CloseDropdown();
                _openGroup = (_openGroup == gi) ? -1 : gi;
                ShowSelected();
            };
        }

        private static UnityAction MakeBottomAction(int i)
        {
            int bi = i;
            return delegate
            {
                CloseDropdown();
                if (bi == 0) OnExport();
                else if (bi == 1) OnReset();
                else if (bi == 2) OnCopy();
                else if (bi == 3) OnResend();
            };
        }

        private static void OnExport()
        {
            try
            {
                List<DebugRecord> all = ChatEngine.DebugSnapshot();
                string dir = AiChatPlugin.CfgExportDir != null ? AiChatPlugin.CfgExportDir.Value : Application.dataPath;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "DebugWindow_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("AI 对话调试窗口导出 / Debug window export");
                sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("记录数：" + all.Count + "（上限 " + DEBUG_MAX + "）");
                sb.AppendLine(new string('=', 70));
                for (int i = 0; i < all.Count; i++)
                {
                    DebugRecord r = all[i];
                    if (r == null) continue;
                    sb.AppendLine("[" + (i + 1) + "] " + r.Time.ToString("yyyy-MM-dd HH:mm:ss")
                        + " | " + r.Kind + " | " + r.Actor + " | " + r.Status
                        + " | " + (r.Ms >= 0 ? r.Ms + "ms" : "—")
                        + " | tokens=" + TokensText(r));
                    if (r.Brief.Length > 0) sb.AppendLine("  简述：" + r.Brief);
                    if (r.GlobalPrompt.Length > 0) sb.AppendLine("  [全局提示词] " + r.GlobalPrompt);
                    if (r.CharacterPrompt.Length > 0) sb.AppendLine("  [角色提示词] " + r.CharacterPrompt);
                    if (r.UserPrompt.Length > 0) sb.AppendLine("  [事件提示词] " + r.UserPrompt);
                    if (r.Response.Length > 0) sb.AppendLine("  [响应] " + r.Response);
                    if (r.Error.Length > 0) sb.AppendLine("  [错误] " + r.Error);
                    if (r.Meta.Length > 0) sb.AppendLine("  [元数据] " + r.Meta);
                    sb.AppendLine();
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                SetHint(Lo("已导出：" + path, "Exported: " + path));
            }
            catch (Exception e)
            {
                SetHint(Lo("导出失败：" + e.Message, "Export failed: " + e.Message));
            }
        }

        private static void OnReset()
        {
            try
            {
                ChatEngine.DebugResetAll();
                _selRec = null;
                _snapshotCache = "";
                _followBottom = false;
                RefreshAll(true);
                SetHint(Lo("已重置：记录清空、统计归零", "Reset: records cleared, stats zeroed"));
            }
            catch (Exception) { }
        }

        private static void OnCopy()
        {
            try
            {
                DebugRecord r = _shown;
                if (r == null) return;
                string s = r.Response.Length > 0 ? r.Response : r.Brief;
                TextEditor te = new TextEditor();
                te.text = s;
                te.SelectAll();
                te.Copy();
                SetHint(Lo("已复制到剪贴板", "Copied to clipboard"));
            }
            catch (Exception e)
            {
                SetHint(Lo("复制失败：" + e.Message, "Copy failed: " + e.Message));
            }
        }

        private static void OnResend()
        {
            try
            {
                DebugRecord r = _shown;
                if (r == null || r.IsEvent || string.IsNullOrEmpty(r.SystemPrompt))
                {
                    SetHint(Lo("请先选一条 AI 请求记录（事件/本地记录不能重发）", "Select an AI request record first"));
                    return;
                }
                bool ok = ChatEngine.DebugResend(r);
                SetHint(ok ? Lo("已重新发送，结果会出现在聊天框", "Resent; result will appear in chat")
                           : Lo("重新发送失败", "Resend failed"));
            }
            catch (Exception) { }
        }

        private static void SetHint(string s)
        {
            if (_hintTxt != null) _hintTxt.text = s;
        }

        // ============================================================ 语言存档
        private static string Lo(string zh, string en)
        {
            return _en ? en : zh;
        }

        private static bool LoadLang()
        {
            try
            {
                if (AiChatPlugin.CfgDebugLang != null)
                    return string.Equals(AiChatPlugin.CfgDebugLang.Value, "en", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { }
            return false;
        }

        private static void SaveLang(string v)
        {
            try
            {
                if (AiChatPlugin.CfgDebugLang != null)
                {
                    AiChatPlugin.CfgDebugLang.Value = v;
                    if (AiChatPlugin.Instance != null) AiChatPlugin.Instance.Config.Save();
                }
            }
            catch (Exception) { }
        }

        // ============================================================ 输入锁
        private static void SetGameInput(bool enableGame)
        {
            try
            {
                Type pt = AccessTools.TypeByName("Player");
                if (pt != null)
                {
                    SetStaticBool(pt, "allowMouseLook", enableGame);
                    SetStaticBool(pt, "inputEnabled", enableGame);
                }
            }
            catch (Exception) { }
            try
            {
                Cursor.visible = !enableGame;
                Cursor.lockState = enableGame ? CursorLockMode.Locked : CursorLockMode.None;
            }
            catch (Exception) { }
        }

        private static void SetStaticBool(Type t, string name, bool v)
        {
            try
            {
                FieldInfo f = t.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(bool)) { f.SetValue(null, v); return; }
                PropertyInfo p = t.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite && p.PropertyType == typeof(bool)) p.SetValue(null, v, null);
            }
            catch (Exception) { }
        }

        private static void EnsureEventSystem()
        {
            try
            {
                if (EventSystem.current != null) return;
                EventSystem[] all = UnityEngine.Object.FindObjectsOfType<EventSystem>();
                if (all != null && all.Length > 0) { EventSystem.current = all[0]; return; }
                GameObject go = new GameObject("AIChat_DebugEventSystem");
                go.AddComponent<EventSystem>();
                go.AddComponent<StandaloneInputModule>();
            }
            catch (Exception) { }
        }

        // ============================================================ UI 工具
        private static ColorBlock CloseColors()
        {
            ColorBlock cb = ColorBlock.defaultColorBlock;
            cb.normalColor = C_CLOSE;
            cb.highlightedColor = new Color(0.7f, 0.36f, 0.38f, 1f);
            cb.pressedColor = new Color(0.45f, 0.22f, 0.24f, 1f);
            return cb;
        }

        private static void RectTopLeft(RectTransform rt, float x, float yTop, float w, float h)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -yTop);
            rt.sizeDelta = new Vector2(w, h);
        }

        private static Image MakeImage(Transform parent, string name, Color color, float x, float yTop, float w, float h, bool raycast)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            RectTopLeft(rt, x, yTop, w, h);
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        private static Text MakeText(Transform parent, string name, string s, float x, float yTop, float w, float h,
            int fontSize, Color color, TextAnchor anchor, bool bold)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            RectTopLeft(rt, x, yTop, w, h);
            var t = go.GetComponent<Text>();
            t.font = _font;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = anchor;
            t.supportRichText = false;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            if (bold) t.fontStyle = FontStyle.Bold;
            t.text = s;
            return t;
        }

        private static Text MakeButton(Transform parent, string name, string label, float x, float yTop, float w, float h,
            UnityAction onClick, int fontSize)
        {
            Image img = MakeImage(parent, name, C_BTN, x, yTop, w, h, true);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.ColorTint;
            ColorBlock cb = btn.colors;
            cb.normalColor = C_BTN;
            cb.highlightedColor = C_BTN2;
            cb.pressedColor = new Color(0.18f, 0.24f, 0.32f, 1f);
            btn.colors = cb;
            Text t = MakeText(img.transform, "Label", label, 0, 0, w, h, fontSize, C_TEXT, TextAnchor.MiddleCenter, false);
            if (onClick != null) btn.onClick.AddListener(onClick);
            return t;
        }
    }
}
