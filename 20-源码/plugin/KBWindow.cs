// RF AiChat Bridge —— 第56轮：MOD 设置里的「常识库」编辑窗口
// 入口：模组设置页右上角「常识库」按钮（与「提示词设置」「人设分配」并列）
// 内容：多行文本，保存后写入 BepInEx\config\rf.aichat.kb.txt，并立即作用于系统提示词。
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RF.AiChat
{
    internal static class KBWindow
    {
        private const float WIN_W = 920f;
        private const float WIN_H = 700f;

        private static readonly Color C_WIN = new Color(0.10f, 0.12f, 0.16f, 0.98f);
        private static readonly Color C_TITLE = new Color(0.17f, 0.21f, 0.27f, 1f);
        private static readonly Color C_TEXT = new Color(0.92f, 0.94f, 0.97f, 1f);
        private static readonly Color C_DIM = new Color(0.65f, 0.70f, 0.76f, 1f);
        private static readonly Color C_BTN = new Color(0.25f, 0.33f, 0.43f, 1f);
        private static readonly Color C_BTN2 = new Color(0.34f, 0.42f, 0.52f, 1f);
        private static readonly Color C_INPUT = new Color(0.09f, 0.11f, 0.14f, 1f);
        private static readonly Color C_CLOSE = new Color(0.55f, 0.28f, 0.30f, 1f);

        private static Font _font;
        private static bool _built, _open;
        private static GameObject _canvas, _win;
        private static Text _hint;
        private static InputField _field;
        private static string _module = "base";
        private static Image[] _tabImgs;
        private static Text[] _tabTexts;
        private static ScrollRect _scroll;
        private static RectTransform _content;
        private static Text _editorText;

        internal static void Tick()
        {
            if (!_open) return;
            ResizeEditor();
            try
            {
                if (Input.GetKeyDown(KeyCode.Escape)) { SaveAndClose(); return; }
                bool editing = false;
                try { editing = EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null; } catch (Exception) { }
                try { Input.imeCompositionMode = editing ? IMECompositionMode.On : IMECompositionMode.Off; } catch (Exception) { }
            }
            catch (Exception) { }
        }

        internal static void Toggle()
        {
            if (_open) SaveAndClose(); else Open();
        }

        internal static void Open()
        {
            try
            {
                EnsureBuilt();
                if (_win == null) return;
                _open = true;
                _canvas.SetActive(true);
                _win.SetActive(true);
                if (string.IsNullOrEmpty(_module)) _module = "base";
                string t = KnowledgeBase.TextFor(_module);
                if (string.IsNullOrEmpty(t)) t = KnowledgeBase.DefaultFor(_module);
                if (_field != null) _field.text = t;
                UpdateTabs();
                JumpToTop();
                SetHint("当前编辑：[" + ModuleLabel(_module) + "]；保存后只改这个模块，下一条台词立即生效 / save & takes effect immediately.");
                try { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; } catch (Exception) { }
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogInfo("[AIChat][常识库] 编辑窗口已打开");
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AIChat][常识库] 打开失败：" + e.Message);
            }
        }

        private static string ModuleLabel(string key)
        {
            for (int i = 0; i < KnowledgeBase.ModuleKeys.Length; i++)
                if (KnowledgeBase.ModuleKeys[i] == key) return KnowledgeBase.ModuleNames[i];
            return key;
        }

        private static void SwitchModule(string key)
        {
            if (string.IsNullOrEmpty(key) || key == _module) return;
            try { Save(); } catch (Exception) { }
            _module = key;
            string t = KnowledgeBase.TextFor(key);
            if (string.IsNullOrEmpty(t)) t = KnowledgeBase.DefaultFor(key);
            if (_field != null) _field.text = t;
            UpdateTabs();
            JumpToTop();
            SetHint("当前编辑：[" + ModuleLabel(_module) + "]；保存后只改这个模块。");
        }

        private static void UpdateTabs()
        {
            try
            {
                if (_tabImgs == null) return;
                for (int i = 0; i < _tabImgs.Length; i++)
                {
                    if (_tabImgs[i] == null) continue;
                    bool on = KnowledgeBase.ModuleKeys[i] == _module;
                    _tabImgs[i].color = on ? C_BTN2 : C_BTN;
                    if (_tabTexts != null && _tabTexts[i] != null)
                        _tabTexts[i].color = on ? Color.white : C_TEXT;
                }
            }
            catch (Exception) { }
        }

        private static void SaveAndClose()
        {
            try
            {
                Save();
                _open = false;
                if (_win != null) _win.SetActive(false);
                if (_canvas != null) _canvas.SetActive(false);
                try { Input.imeCompositionMode = IMECompositionMode.Off; } catch (Exception) { }
            }
            catch (Exception) { }
        }

        private static void JumpToTop()
        {
            try
            {
                if (_field == null) return;
                try { _field.ActivateInputField(); } catch (Exception) { }
                _field.caretPosition = 0;
                try { _field.selectionAnchorPosition = 0; _field.selectionFocusPosition = 0; } catch (Exception) { }
                _field.ForceLabelUpdate();
                // 不走 InputField 的局部 draw range，直接把完整文本放进可滚动的内容区
                if (_editorText != null && _field.text != null) _editorText.text = _field.text;
                ResizeEditor();
                if (_scroll != null) _scroll.verticalNormalizedPosition = 1f;
            }
            catch (Exception) { }
        }

        private static void ResizeEditor()
        {
            try
            {
                if (_editorText == null || _content == null) return;
                float h = Mathf.Max(WIN_H - 216f, _editorText.preferredHeight + 12f);
                _content.sizeDelta = new Vector2(_content.sizeDelta.x, h);
                _editorText.rectTransform.sizeDelta = new Vector2(_editorText.rectTransform.sizeDelta.x, h - 8f);
            }
            catch (Exception) { }
        }

        private static void Save()
        {
            try
            {
                string t = _field == null || _field.text == null ? "" : _field.text;
                KnowledgeBase.Save(_module, t);
                int len = (t ?? "").Trim().Length;
                SetHint("已保存 [" + ModuleLabel(_module) + "] / saved（" + len + " 字），下一条台词即带上该模块。");
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogInfo("[AIChat][常识库] 已保存 [" + ModuleLabel(_module) + "]：" + len + " 字");
            }
            catch (Exception e)
            {
                SetHint("保存失败：" + e.Message);
            }
        }

        private static void ResetDefault()
        {
            try
            {
                string d = KnowledgeBase.DefaultFor(_module);
                if (_field != null) _field.text = d;
                KnowledgeBase.Save(_module, d);
                JumpToTop();
                SetHint("已恢复 [" + ModuleLabel(_module) + "] 默认 / defaults restored.");
            }
            catch (Exception) { }
        }

        private static void EnsureBuilt()
        {
            if (_built && _win != null) return;
            try
            {
                if (_font == null)
                {
                    _font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 14);
                    if (_font == null) _font = Font.CreateDynamicFontFromOSFont("SimHei", 14);
                    if (_font == null) return;
                }
                if (_canvas == null)
                {
                    _canvas = new GameObject("AIChat_KBCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                    UnityEngine.Object.DontDestroyOnLoad(_canvas);
                    var cv = _canvas.GetComponent<Canvas>();
                    cv.renderMode = RenderMode.ScreenSpaceOverlay;
                    cv.sortingOrder = 32700;
                    var sc = _canvas.GetComponent<CanvasScaler>();
                    sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    sc.referenceResolution = new Vector2(1920f, 1080f);
                    sc.matchWidthOrHeight = 0.5f;
                }
                if (_win == null)
                {
                    Image blk = MakeImage(_canvas.transform, "Blocker", new Color(0f, 0f, 0f, 0.45f), 0, 0, 0, 0, true);
                    var brt = blk.rectTransform;
                    brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
                    brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
                    blk.gameObject.AddComponent<Button>().transition = Selectable.Transition.None;

                    Image win = MakeImage(_canvas.transform, "Window", C_WIN, 0, 0, WIN_W, WIN_H, true);
                    var wrt = win.rectTransform;
                    wrt.anchorMin = new Vector2(0.5f, 0.5f);
                    wrt.anchorMax = new Vector2(0.5f, 0.5f);
                    wrt.pivot = new Vector2(0.5f, 0.5f);
                    wrt.anchoredPosition = Vector2.zero;
                    wrt.sizeDelta = new Vector2(WIN_W, WIN_H);
                    _win = win.gameObject;

                    Image title = MakeImage(win.transform, "TitleBar", C_TITLE, 0, 0, WIN_W, 30, true);
                    MakeText(title.transform, "T", "常识库（MOD） / Knowledge Base", 12, 4, 700, 22, 15, C_TEXT, TextAnchor.MiddleLeft, true);
                    MakeButton(title.transform, "CloseX", "×", WIN_W - 34f, 3f, 26f, 24f, new UnityAction(SaveAndClose), 15);
                    try
                    {
                        Transform cx = title.transform.Find("CloseX");
                        if (cx != null)
                        {
                            var cbx = cx.GetComponent<Button>(); if (cbx != null) cbx.colors = CloseColors();
                            var imgx = cx.GetComponent<Image>(); if (imgx != null) imgx.color = C_CLOSE;
                        }
                    }
                    catch (Exception) { }

                    MakeText(win.transform, "Tip", "六个模块：基础 / 武器 / 载具 / 地图 / 模式 / 其他。点上方标签切换，保存只改当前模块 / click a tab, save only edits that module.",
                        12, 36, WIN_W - 24, 20, 11, C_DIM, TextAnchor.MiddleLeft, false);

                    _tabImgs = new Image[KnowledgeBase.ModuleKeys.Length];
                    _tabTexts = new Text[KnowledgeBase.ModuleKeys.Length];
                    for (int ti = 0; ti < KnowledgeBase.ModuleKeys.Length; ti++)
                    {
                        string key = KnowledgeBase.ModuleKeys[ti];
                        Text tab = MakeButton(win.transform, "Tab" + ti, KnowledgeBase.ModuleNames[ti],
                            12f + ti * 146f, 58f, 140f, 26f, new UnityAction(() => SwitchModule(key)), 12);
                        _tabTexts[ti] = tab;
                        if (tab != null && tab.transform != null && tab.transform.parent != null)
                            _tabImgs[ti] = tab.transform.parent.GetComponent<Image>();
                    }

                    Image bg = MakeImage(win.transform, "Editor", C_INPUT, 12, 94, WIN_W - 24, WIN_H - 204, true);
                    var bgRt = bg.rectTransform;
                    bg.gameObject.AddComponent<RectMask2D>();
                    var sr = bg.gameObject.AddComponent<ScrollRect>();
                    sr.viewport = bgRt;
                    sr.horizontal = false;
                    sr.vertical = true;
                    sr.movementType = ScrollRect.MovementType.Clamped;
                    sr.scrollSensitivity = 40f;

                    GameObject contentGo = new GameObject("Content", typeof(RectTransform));
                    var crt = contentGo.GetComponent<RectTransform>();
                    crt.SetParent(bg.transform, false);
                    crt.anchorMin = new Vector2(0f, 1f);
                    crt.anchorMax = new Vector2(1f, 1f);
                    crt.pivot = new Vector2(0.5f, 1f);
                    crt.anchoredPosition = Vector2.zero;
                    crt.sizeDelta = new Vector2(0f, 100f);

                    GameObject textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                    var trt = textGo.GetComponent<RectTransform>();
                    trt.SetParent(crt, false);
                    trt.anchorMin = new Vector2(0f, 1f);
                    trt.anchorMax = new Vector2(1f, 1f);
                    trt.pivot = new Vector2(0.5f, 1f);
                    trt.anchoredPosition = new Vector2(0f, -4f);
                    trt.sizeDelta = new Vector2(-16f, 100f);
                    Text txt = textGo.GetComponent<Text>();
                    txt.font = _font;
                    txt.fontSize = 13;
                    txt.color = C_TEXT;
                    txt.alignment = TextAnchor.UpperLeft;
                    txt.supportRichText = false;
                    txt.raycastTarget = false;
                    txt.horizontalOverflow = HorizontalWrapMode.Wrap;
                    txt.verticalOverflow = VerticalWrapMode.Overflow;

                    var f = bg.gameObject.AddComponent<InputField>();
                    f.lineType = InputField.LineType.MultiLineNewline;
                    f.targetGraphic = bg;
                    f.characterLimit = 20000;
                    f.textComponent = txt;
                    Text ph = MakeText(bg.transform, "Placeholder", "（留空 = 不启用 / empty = off；点「恢复默认」载入内置常识）",
                        8, 6, WIN_W - 40, 20, 12, C_DIM, TextAnchor.UpperLeft, false);
                    ph.supportRichText = false;
                    f.placeholder = ph;
                    sr.content = crt;
                    _scroll = sr;
                    _content = crt;
                    _editorText = txt;
                    _field = f;

                    _hint = MakeText(win.transform, "Hint", "", 12, WIN_H - 100, WIN_W - 24, 20, 11, C_DIM, TextAnchor.MiddleLeft, false);
                    MakeButton(win.transform, "SaveBtn", "保存 / Save", 12, WIN_H - 60, 200, 36, new UnityAction(Save), 14);
                    MakeButton(win.transform, "ResetBtn", "恢复默认 / Reset", 224, WIN_H - 60, 200, 36, new UnityAction(ResetDefault), 14);
                    MakeButton(win.transform, "CloseBtn", "关闭 / Close", WIN_W - 212, WIN_H - 60, 200, 36, new UnityAction(SaveAndClose), 14);

                    _win.SetActive(false);
                }
                _canvas.SetActive(false);
                _built = true;
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AIChat][常识库] 窗口创建失败：" + e.Message + " @ " + e.StackTrace);
            }
        }

        private static void SetHint(string s)
        {
            if (_hint != null) _hint.text = s;
        }

        // ---------------- UI 工具 ----------------
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
