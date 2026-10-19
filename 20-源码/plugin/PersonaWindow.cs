// RF AiChat Bridge —— 第50轮：MOD 设置界面里的「人设分配」设置
// 可自定义：四档名次比例（默认 25/50/75）、开局第一次分配时间（默认 60 秒）、重新分配间隔（默认 300 秒）
// 保存后写插件配置 [Persona] 段，并立即推送给 Lua（对局中也能生效）。
using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RF.AiChat
{
    internal static class PersonaWindow
    {
        private const float WIN_W = 720f;
        private const float WIN_H = 470f;

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
        private static InputField _fR1, _fR2, _fR3, _fFirst, _fEvery;

        internal static bool IsOpen() { return _open; }

        internal static void Tick()
        {
            if (!_open) return;
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
                LoadFromConfig();
                SetHint("默认 / default 25/50/75（四档约各 1/4）；新生 AI 不单独分档，等下一次整队分配。");
                try { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; } catch (Exception) { }
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AIChat][人设] 打开失败：" + e.Message);
            }
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
                    _canvas = new GameObject("AIChat_PersonaCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                    UnityEngine.Object.DontDestroyOnLoad(_canvas);
                    var cv = _canvas.GetComponent<Canvas>();
                    cv.renderMode = RenderMode.ScreenSpaceOverlay;
                    cv.sortingOrder = 32650;
                    var sc = _canvas.GetComponent<CanvasScaler>();
                    sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    sc.referenceResolution = new Vector2(1920f, 1080f);
                    sc.matchWidthOrHeight = 0.5f;
                }
                if (_win == null)
                {
                    Image blk = MakeImage(_canvas.transform, "Blocker", new Color(0f, 0f, 0f, 0.45f), 0, 0, 0, 0, true);
                    Stretch(blk.rectTransform);
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
                    MakeText(title.transform, "T", "人设分配设置（MOD） / Persona Settings", 12, 4, 560, 22, 15, C_TEXT, TextAnchor.MiddleLeft, true);
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

                    float y = 52f;
                    _fR1 = Row(win.transform, "王牌比例 / Ace ≤ (%)：", "默认 25", ref y);
                    _fR2 = Row(win.transform, "精锐比例 / Elite ≤ (%)：", "默认 50", ref y);
                    _fR3 = Row(win.transform, "老兵比例 / Veteran ≤ (%)：", "默认 75", ref y);
                    y += 8f;
                    _fFirst = Row(win.transform, "首次分配 / First assign (s)：", "默认 60", ref y);
                    _fEvery = Row(win.transform, "重新分配 / Reassign (s)：", "默认 300", ref y);

                    _hint = MakeText(win.transform, "Hint", "", 16, WIN_H - 118f, WIN_W - 32, 40, 11, C_DIM, TextAnchor.UpperLeft, false);
                    MakeButton(win.transform, "SaveBtn", "保存 / Save", 16, WIN_H - 52f, 200, 34, new UnityAction(OnSaveClicked), 14);
                    MakeButton(win.transform, "ResetBtn", "恢复默认 / Reset", 228, WIN_H - 52f, 200, 34, new UnityAction(OnResetClicked), 14);
                    MakeButton(win.transform, "CloseBtn", "关闭 / Close", WIN_W - 216f, WIN_H - 52f, 200, 34, new UnityAction(SaveAndClose), 14);

                    _win.SetActive(false);
                }
                _canvas.SetActive(false);
                _built = true;
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AIChat][人设] 窗口创建失败：" + e.Message);
            }
        }

        private static InputField Row(Transform parent, string label, string defTip, ref float y)
        {
            MakeText(parent, "L", label, 16, y, 380, 22, 13, C_TEXT, TextAnchor.MiddleLeft, false);
            Image bg = MakeImage(parent, "Input", C_INPUT, 400, y - 2, 120, 26, true);
            var f = bg.gameObject.AddComponent<InputField>();
            f.lineType = InputField.LineType.SingleLine;
            f.characterLimit = 4;
            f.targetGraphic = bg;
            Text txt = MakeText(bg.transform, "Text", "", 6, 0, 108, 26, 14, C_TEXT, TextAnchor.MiddleLeft, false);
            txt.supportRichText = false;
            f.textComponent = txt;
            Text ph = MakeText(bg.transform, "Placeholder", defTip, 6, 0, 108, 26, 11, C_DIM, TextAnchor.MiddleLeft, false);
            ph.supportRichText = false;
            f.placeholder = ph;
            MakeText(parent, "Tip", defTip, 532, y, 180, 22, 11, C_DIM, TextAnchor.MiddleLeft, false);
            y += 46f;
            return f;
        }

        private static void OnSaveClicked()
        {
            Save();
            SetHint("已保存 / saved：立即生效（下一次分配/周期用新值）。");
        }

        private static void OnResetClicked()
        {
            SetCfg(AiChatPlugin.CfgPersonaRatio1, 25);
            SetCfg(AiChatPlugin.CfgPersonaRatio2, 50);
            SetCfg(AiChatPlugin.CfgPersonaRatio3, 75);
            SetCfg(AiChatPlugin.CfgPersonaFirstAssign, 60);
            SetCfg(AiChatPlugin.CfgPersonaReassignEvery, 300);
            SaveConfig();
            LoadFromConfig();
            ChatEngine.SendCfgToLua();
            SetHint("已恢复默认 / defaults：25/50/75, first 60s, every 300s");
        }

        private static void Save()
        {
            try
            {
                int r1 = Parse(_fR1, 25);
                int r2 = Parse(_fR2, 50);
                int r3 = Parse(_fR3, 75);
                int first = Parse(_fFirst, 60);
                int every = Parse(_fEvery, 300);
                if (r1 < 1 || r1 > 99 || r2 <= r1 || r2 > 99 || r3 <= r2 || r3 > 100)
                {
                    SetHint("比例不合法 / invalid ratios：0 < Ace < Elite < Veteran ≤ 100，已取消保存。");
                    return;
                }
                if (first < 5) first = 5;
                if (every < 10) every = 10;
                SetCfg(AiChatPlugin.CfgPersonaRatio1, r1);
                SetCfg(AiChatPlugin.CfgPersonaRatio2, r2);
                SetCfg(AiChatPlugin.CfgPersonaRatio3, r3);
                SetCfg(AiChatPlugin.CfgPersonaFirstAssign, first);
                SetCfg(AiChatPlugin.CfgPersonaReassignEvery, every);
                SaveConfig();
                ChatEngine.SendCfgToLua();
                if (AiChatPlugin.Log != null)
                    AiChatPlugin.Log.LogInfo("[AIChat][人设] 已保存：比例 " + r1 + "/" + r2 + "/" + r3
                        + "，首次 " + first + " 秒，间隔 " + every + " 秒");
            }
            catch (Exception e)
            {
                SetHint("保存失败：" + e.Message);
            }
        }

        private static void LoadFromConfig()
        {
            try
            {
                SetInputText(_fR1, GetCfg(AiChatPlugin.CfgPersonaRatio1, 25));
                SetInputText(_fR2, GetCfg(AiChatPlugin.CfgPersonaRatio2, 50));
                SetInputText(_fR3, GetCfg(AiChatPlugin.CfgPersonaRatio3, 75));
                SetInputText(_fFirst, GetCfg(AiChatPlugin.CfgPersonaFirstAssign, 60));
                SetInputText(_fEvery, GetCfg(AiChatPlugin.CfgPersonaReassignEvery, 300));
            }
            catch (Exception) { }
        }

        private static int GetCfg(BepInEx.Configuration.ConfigEntry<int> e, int def)
        {
            try { return e == null ? def : e.Value; } catch (Exception) { return def; }
        }

        private static void SetCfg(BepInEx.Configuration.ConfigEntry<int> e, int v)
        {
            try { if (e != null) e.Value = v; } catch (Exception) { }
        }

        private static int Parse(InputField f, int def)
        {
            try
            {
                if (f == null || string.IsNullOrEmpty(f.text)) return def;
                int v;
                return int.TryParse(f.text.Trim(), out v) ? v : def;
            }
            catch (Exception) { return def; }
        }

        private static void SetInputText(InputField f, int v)
        {
            if (f != null) f.text = v.ToString();
        }

        private static void SaveConfig()
        {
            try { if (AiChatPlugin.Instance != null) AiChatPlugin.Instance.Config.Save(); } catch (Exception) { }
        }

        private static void SetHint(string s)
        {
            if (_hint != null) _hint.text = s;
        }

        // ---------------- UI 工具 ----------------
        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

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
