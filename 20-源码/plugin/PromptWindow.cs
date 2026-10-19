// RF AiChat Bridge —— 第46轮：MOD 设置界面里的「提示词」面板
// 在游戏的模组设置页右上角挂一个「提示词设置」按钮；点开一个插件侧窗口，可编辑：
//   全局提示词 / 阵营提示词（鹰队=蓝、鸦队=红）/ 角色提示词（新兵/老兵/精锐/王牌）
// 保存后立即生效（写 plugin 配置 [Prompts] 段；留空 = 使用内置默认）。
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RF.AiChat
{
    internal static class PromptWindow
    {
        private const float WIN_W = 980f;
        private const float WIN_H = 790f;

        private static readonly Color C_WIN = new Color(0.10f, 0.12f, 0.16f, 0.98f);
        private static readonly Color C_TITLE = new Color(0.17f, 0.21f, 0.27f, 1f);
        private static readonly Color C_PANEL = new Color(0.13f, 0.16f, 0.21f, 1f);
        private static readonly Color C_TEXT = new Color(0.92f, 0.94f, 0.97f, 1f);
        private static readonly Color C_DIM = new Color(0.65f, 0.70f, 0.76f, 1f);
        private static readonly Color C_BTN = new Color(0.25f, 0.33f, 0.43f, 1f);
        private static readonly Color C_BTN2 = new Color(0.34f, 0.42f, 0.52f, 1f);
        private static readonly Color C_INPUT = new Color(0.09f, 0.11f, 0.14f, 1f);
        private static readonly Color C_BLUE = new Color(0.24f, 0.45f, 1f, 1f);
        private static readonly Color C_RED = new Color(1f, 0.32f, 0.32f, 1f);
        private static readonly Color C_CLOSE = new Color(0.55f, 0.28f, 0.30f, 1f);

        private static Font _font;
        private static bool _windowBuilt, _entryBuilt, _open;
        private static GameObject _entryCanvas, _entryGO, _canvas, _win;
        private static Text _hint;
        private static InputField _fGlobal, _fEagle, _fRaven, _fRookie, _fNormal, _fVeteran, _fAce, _fMatch, _fPlayerTitle, _fSpectatorTitle, _fAutoReport;
        private static float _nextPageCheck = 0f;

        // ============================================================ 对外
        internal static void Tick()
        {
            try
            {
                if (_open)
                {
                    if (Input.GetKeyDown(KeyCode.Escape)) { CloseSave(); return; }
                    // 有输入框被选中时打开中文输入法，否则关掉（避免影响别的输入）
                    bool editing = false;
                    try { editing = EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null; } catch (Exception) { }
                    try { Input.imeCompositionMode = editing ? IMECompositionMode.On : IMECompositionMode.Off; } catch (Exception) { }
                    return;
                }
                // 设置页入口按钮：只在模组设置页可见
                if (Time.unscaledTime < _nextPageCheck) return;
                _nextPageCheck = Time.unscaledTime + 0.3f;
                EnsureEntryBuilt();
                if (_entryCanvas != null)
                {
                    bool show = NameColorRows.PageVisible();   // ★ 第87轮
                    if (_entryCanvas.activeSelf != show) _entryCanvas.SetActive(show);
                }
            }
            catch (Exception) { }
        }

        internal static void Toggle()
        {
            if (_open) CloseSave(); else Open();
        }

        internal static void Open()
        {
            try
            {
                EnsureWindowBuilt();
                if (_win == null) return;
                _open = true;
                _canvas.SetActive(true);
                _win.SetActive(true);
                LoadFromConfig();
                SetHint("已显示内置默认 / built-in defaults shown；修改后点「保存 / Save」覆盖，点「恢复默认 / Reset」还原。");
                try { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; } catch (Exception) { }
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogInfo("[AIChat][提示词] 设置窗口已打开");
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AIChat][提示词] 打开失败：" + e.Message);
            }
        }

        private static void CloseSave()
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

        // ============================================================ 设置页入口按钮
        private static void EnsureEntryBuilt()
        {
            if (_entryBuilt && _entryCanvas != null) return;
            try
            {
                EnsureEventSystem();
                if (_font == null)
                {
                    _font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 14);
                    if (_font == null) _font = Font.CreateDynamicFontFromOSFont("SimHei", 14);
                    if (_font == null) return;
                }
                _entryCanvas = new GameObject("AIChat_PromptEntryCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                UnityEngine.Object.DontDestroyOnLoad(_entryCanvas);
                var cv = _entryCanvas.GetComponent<Canvas>();
                cv.renderMode = RenderMode.ScreenSpaceOverlay;
                cv.sortingOrder = 32500;
                var sc = _entryCanvas.GetComponent<CanvasScaler>();
                sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                sc.referenceResolution = new Vector2(1920f, 1080f);
                sc.matchWidthOrHeight = 0.5f;

                _entryGO = MakeButton(_entryCanvas.transform, "PromptEntryBtn", "提示词设置 / Prompts", 0, 0, 200, 34,
                    new UnityAction(Toggle), 14).transform.parent.gameObject;
                var rt = _entryGO.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(1f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(-36f, -190f);
                rt.sizeDelta = new Vector2(200f, 34f);

                // ★ 第50轮：人设分配设置入口
                GameObject pbtn = MakeButton(_entryCanvas.transform, "PersonaEntryBtn", "人设分配 / Persona", 0, 0, 200, 34,
                    new UnityAction(PersonaWindow.Toggle), 14).transform.parent.gameObject;
                var prt = pbtn.GetComponent<RectTransform>();
                prt.anchorMin = new Vector2(1f, 1f);
                prt.anchorMax = new Vector2(1f, 1f);
                prt.pivot = new Vector2(1f, 1f);
                prt.anchoredPosition = new Vector2(-36f, -232f);
                prt.sizeDelta = new Vector2(200f, 34f);

                // ★ 第56轮：常识库入口
                GameObject kbtn = MakeButton(_entryCanvas.transform, "KBEntryBtn", "常识库 / Knowledge", 0, 0, 200, 34,
                    new UnityAction(KBWindow.Toggle), 14).transform.parent.gameObject;
                var krt = kbtn.GetComponent<RectTransform>();
                krt.anchorMin = new Vector2(1f, 1f);
                krt.anchorMax = new Vector2(1f, 1f);
                krt.pivot = new Vector2(1f, 1f);
                krt.anchoredPosition = new Vector2(-36f, -274f);
                krt.sizeDelta = new Vector2(200f, 34f);
                _entryCanvas.SetActive(false);
                _entryBuilt = true;
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AIChat][提示词] 入口按钮创建失败：" + e.Message);
            }
        }

        // ============================================================ 主窗口
        private static void EnsureWindowBuilt()
        {
            if (_windowBuilt && _win != null && _canvas != null) return;
            try
            {
                EnsureEntryBuilt();
                if (_canvas == null)
                {
                    _canvas = new GameObject("AIChat_PromptWindowCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                    UnityEngine.Object.DontDestroyOnLoad(_canvas);
                    var cv = _canvas.GetComponent<Canvas>();
                    cv.renderMode = RenderMode.ScreenSpaceOverlay;
                    cv.sortingOrder = 32600;
                    var sc = _canvas.GetComponent<CanvasScaler>();
                    sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    sc.referenceResolution = new Vector2(1920f, 1080f);
                    sc.matchWidthOrHeight = 0.5f;
                }
                if (_win == null)
                {
                    Image blk = MakeImage(_canvas.transform, "Blocker", new Color(0f, 0f, 0f, 0.45f), 0, 0, 0, 0, true);
                    var brt = blk.rectTransform;
                    brt.anchorMin = Vector2.zero;
                    brt.anchorMax = Vector2.one;
                    brt.offsetMin = Vector2.zero;
                    brt.offsetMax = Vector2.zero;
                    var bbtn = blk.gameObject.AddComponent<Button>();
                    bbtn.transition = Selectable.Transition.None;

                    Image win = MakeImage(_canvas.transform, "Window", C_WIN, 0, 0, WIN_W, WIN_H, true);
                    var wrt = win.rectTransform;
                    wrt.anchorMin = new Vector2(0.5f, 0.5f);
                    wrt.anchorMax = new Vector2(0.5f, 0.5f);
                    wrt.pivot = new Vector2(0.5f, 0.5f);
                    wrt.anchoredPosition = Vector2.zero;
                    wrt.sizeDelta = new Vector2(WIN_W, WIN_H);
                    _win = win.gameObject;

                    Image title = MakeImage(win.transform, "TitleBar", C_TITLE, 0, 0, WIN_W, 30, true);
                    MakeText(title.transform, "Title", "提示词设置（MOD） / Prompt Settings", 12, 4, 620, 22, 15, C_TEXT, TextAnchor.MiddleLeft, true);
                    MakeButton(title.transform, "CloseX", "×", WIN_W - 34f, 3f, 26f, 24f, new UnityAction(CloseSave), 15);
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

                    MakeText(win.transform, "Header", "—— 提示词 / PROMPTS ——", 0, 34, WIN_W, 24, 14, C_TEXT, TextAnchor.MiddleCenter, true);

                    // 左列：全局 + 阵营
                    MakeText(win.transform, "L1", "全局提示词 / Global prompt：", 16, 64, 460, 20, 13, C_TEXT, TextAnchor.MiddleLeft, true);
                    _fGlobal = MakeInput(win.transform, 16, 86, 460, 150, "");
                    MakeText(win.transform, "L2", "阵营提示词 / Faction prompt", 16, 246, 460, 20, 13, C_TEXT, TextAnchor.MiddleLeft, true);
                    MakeText(win.transform, "L3", "鹰队（蓝方）/ Eagle (Blue)：", 16, 272, 460, 20, 13, C_BLUE, TextAnchor.MiddleLeft, true);
                    _fEagle = MakeInput(win.transform, 16, 294, 460, 70, "");
                    MakeText(win.transform, "L4", "鸦队（红方）/ Raven (Red)：", 16, 374, 460, 20, 13, C_RED, TextAnchor.MiddleLeft, true);
                    _fRaven = MakeInput(win.transform, 16, 396, 460, 70, "");

                    // 右列：角色
                    MakeText(win.transform, "R1", "角色提示词 / Character prompt", 504, 64, 460, 20, 13, C_TEXT, TextAnchor.MiddleLeft, true);
                    MakeText(win.transform, "R2", "新兵 / Rookie (K/D 4/4)：", 504, 88, 460, 20, 13, C_TEXT, TextAnchor.MiddleLeft, false);
                    _fRookie = MakeInput(win.transform, 504, 110, 460, 66, "");
                    MakeText(win.transform, "R3", "老兵 / Veteran (K/D 3/4)：", 504, 184, 460, 20, 13, C_TEXT, TextAnchor.MiddleLeft, false);
                    _fNormal = MakeInput(win.transform, 504, 206, 460, 66, "");
                    MakeText(win.transform, "R4", "精锐 / Elite (K/D 2/4)：", 504, 280, 460, 20, 13, C_TEXT, TextAnchor.MiddleLeft, false);
                    _fVeteran = MakeInput(win.transform, 504, 302, 460, 66, "");
                    MakeText(win.transform, "R5", "王牌 / Ace (K/D 1/4)：", 504, 376, 460, 20, 13, C_TEXT, TextAnchor.MiddleLeft, false);
                    _fAce = MakeInput(win.transform, 504, 398, 460, 66, "");
                    MakeText(win.transform, "R6", "对局提示词 / Match prompt（全体角色 / all actors）：", 504, 476, 460, 20, 13, C_TEXT, TextAnchor.MiddleLeft, false);
                    _fMatch = MakeInput(win.transform, 504, 498, 460, 80, "");
                    MakeText(win.transform, "L6", "玩家称呼 / Player title：", 16, 486, 460, 20, 13, C_TEXT, TextAnchor.MiddleLeft, true);
                    _fPlayerTitle = MakeInput(win.transform, 16, 508, 460, 34, "");
                    MakeText(win.transform, "L7", "观察者称呼 / Spectator title：", 16, 550, 460, 20, 13, C_TEXT, TextAnchor.MiddleLeft, true);
                    _fSpectatorTitle = MakeInput(win.transform, 16, 572, 460, 34, "");
                    MakeText(win.transform, "L8", "自动战报间隔 / Auto report (s)：", 16, 614, 460, 20, 13, C_TEXT, TextAnchor.MiddleLeft, true);
                    _fAutoReport = MakeInput(win.transform, 16, 636, 160, 34, "");

                    _hint = MakeText(win.transform, "Hint", "", 16, 690, WIN_W - 32, 22, 11, C_DIM, TextAnchor.MiddleLeft, false);

                    MakeButton(win.transform, "SaveBtn", "保存 / Save", 16, WIN_H - 52f, 200, 34, new UnityAction(OnSaveClicked), 14);
                    MakeButton(win.transform, "ResetBtn", "恢复默认 / Reset", 228, WIN_H - 52f, 200, 34, new UnityAction(OnResetClicked), 14);
                    MakeButton(win.transform, "CloseBtn", "关闭 / Close", WIN_W - 216f, WIN_H - 52f, 200, 34, new UnityAction(CloseSave), 14);

                    _win.SetActive(false);
                }
                _canvas.SetActive(false);
                _windowBuilt = true;
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AIChat][提示词] 窗口创建失败：" + e.Message + " @ " + e.StackTrace);
            }
        }

        private static InputField MakeInput(Transform parent, float x, float y, float w, float h, string value)
        {
            Image bg = MakeImage(parent, "Input", C_INPUT, x, y, w, h, true);
            var f = bg.gameObject.AddComponent<InputField>();
            f.lineType = InputField.LineType.MultiLineNewline;
            f.targetGraphic = bg;
            f.characterLimit = 2000;

            Text txt = MakeText(bg.transform, "Text", "", 6, 4, w - 12, h - 8, 12, C_TEXT, TextAnchor.UpperLeft, false);
            txt.supportRichText = false;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Truncate;
            f.textComponent = txt;

            Text ph = MakeText(bg.transform, "Placeholder", "（留空 = 使用内置默认）", 6, 4, w - 12, 20, 11, C_DIM, TextAnchor.UpperLeft, false);
            ph.supportRichText = false;
            f.placeholder = ph;

            f.text = value == null ? "" : value;
            return f;
        }

        // ============================================================ 保存 / 重置
        private static void OnSaveClicked()
        {
            Save();
            SetHint("已保存 / saved，立即生效（下一条台词就用新提示词 / takes effect immediately）。");
        }

        private static void OnResetClicked()
        {
            SetCfg(AiChatPlugin.CfgPromptGlobal, "");
            SetCfg(AiChatPlugin.CfgPromptEagle, "");
            SetCfg(AiChatPlugin.CfgPromptRaven, "");
            SetCfg(AiChatPlugin.CfgPromptRookie, "");
            SetCfg(AiChatPlugin.CfgPromptNormal, "");
            SetCfg(AiChatPlugin.CfgPromptVeteran, "");
            SetCfg(AiChatPlugin.CfgPromptAce, "");
            SetCfg(AiChatPlugin.CfgPromptMatch, "");
            SetCfg(AiChatPlugin.CfgPlayerTitle, "指挥官");
            SetCfg(AiChatPlugin.CfgSpectatorTitle, "战地记者");
            if (AiChatPlugin.CfgAutoReportSeconds != null) AiChatPlugin.CfgAutoReportSeconds.Value = 180;
            SaveConfig();
            LoadFromConfig();
            SetHint("已恢复默认 / defaults restored（文本框已重新显示内置提示词）。");
        }

        private static void Save()
        {
            try
            {
                SetCfg(AiChatPlugin.CfgPromptGlobal, SaveText(_fGlobal, ChatEngine.DefaultGlobalRules()));
                SetCfg(AiChatPlugin.CfgPromptEagle, TextOf(_fEagle));
                SetCfg(AiChatPlugin.CfgPromptRaven, TextOf(_fRaven));
                SetCfg(AiChatPlugin.CfgPromptRookie, SaveText(_fRookie, ChatEngine.DefaultSkillPrompt("Beginner")));
                SetCfg(AiChatPlugin.CfgPromptNormal, SaveText(_fNormal, ChatEngine.DefaultSkillPrompt("Normal")));
                SetCfg(AiChatPlugin.CfgPromptVeteran, SaveText(_fVeteran, ChatEngine.DefaultSkillPrompt("Veteran")));
                SetCfg(AiChatPlugin.CfgPromptAce, SaveText(_fAce, ChatEngine.DefaultSkillPrompt("Elite")));
                SetCfg(AiChatPlugin.CfgPromptMatch, TextOf(_fMatch));
                SetCfg(AiChatPlugin.CfgPlayerTitle, TextOf(_fPlayerTitle));
                SetCfg(AiChatPlugin.CfgSpectatorTitle, TextOf(_fSpectatorTitle));
                {
                    int sec = 180;
                    if (!int.TryParse(TextOf(_fAutoReport), out sec)) sec = 180;
                    if (sec < 10) sec = 10;
                    if (sec > 3600) sec = 3600;
                    if (AiChatPlugin.CfgAutoReportSeconds != null) AiChatPlugin.CfgAutoReportSeconds.Value = sec;
                    try { ChatEngine.SendCfgToLua(); } catch (Exception) { }
                }
                SaveConfig();
                if (AiChatPlugin.Log != null)
                    AiChatPlugin.Log.LogInfo("[AIChat][提示词] 已保存（长度：全局=" + Len(_fGlobal)
                        + " 鹰=" + Len(_fEagle) + " 鸦=" + Len(_fRaven)
                        + " 新兵=" + Len(_fRookie) + " 老兵=" + Len(_fNormal)
                        + " 精锐=" + Len(_fVeteran) + " 王牌=" + Len(_fAce) + " 对局=" + Len(_fMatch) + "）");
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
                SetInputText(_fGlobal, CfgOr(AiChatPlugin.CfgPromptGlobal, ChatEngine.DefaultGlobalRules()));
                SetInputText(_fEagle, CfgText(AiChatPlugin.CfgPromptEagle));
                SetInputText(_fRaven, CfgText(AiChatPlugin.CfgPromptRaven));
                SetInputText(_fRookie, CfgOr(AiChatPlugin.CfgPromptRookie, ChatEngine.DefaultSkillPrompt("Beginner")));
                SetInputText(_fNormal, CfgOr(AiChatPlugin.CfgPromptNormal, ChatEngine.DefaultSkillPrompt("Normal")));
                SetInputText(_fVeteran, CfgOr(AiChatPlugin.CfgPromptVeteran, ChatEngine.DefaultSkillPrompt("Veteran")));
                SetInputText(_fAce, CfgOr(AiChatPlugin.CfgPromptAce, ChatEngine.DefaultSkillPrompt("Elite")));
                SetInputText(_fMatch, CfgText(AiChatPlugin.CfgPromptMatch));
                SetInputText(_fPlayerTitle, CfgOr(AiChatPlugin.CfgPlayerTitle, "指挥官"));
                SetInputText(_fSpectatorTitle, CfgOr(AiChatPlugin.CfgSpectatorTitle, "战地记者"));
                try { if (_fAutoReport != null) _fAutoReport.text = (AiChatPlugin.CfgAutoReportSeconds == null ? 180 : AiChatPlugin.CfgAutoReportSeconds.Value).ToString(); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        private static void SetInputText(InputField f, string s)
        {
            if (f != null) f.text = s == null ? "" : s;
        }

        private static string CfgOr(BepInEx.Configuration.ConfigEntry<string> e, string def)
        {
            string v = CfgText(e);
            return v.Length > 0 ? v : (def == null ? "" : def);
        }

        // 与内置默认相同 → 存空串（下次仍用内置默认，中英自动切换）
        private static string SaveText(InputField f, string def)
        {
            string v = TextOf(f);
            if (def == null) def = "";
            return v == def.Trim() ? "" : v;
        }

        private static string TextOf(InputField f)
        {
            return f == null || f.text == null ? "" : f.text.Trim();
        }

        private static int Len(InputField f)
        {
            return f == null || f.text == null ? 0 : f.text.Trim().Length;
        }

        private static string CfgText(BepInEx.Configuration.ConfigEntry<string> e)
        {
            try { return e == null || e.Value == null ? "" : e.Value; }
            catch (Exception) { return ""; }
        }

        private static void SetCfg(BepInEx.Configuration.ConfigEntry<string> e, string v)
        {
            try { if (e != null) e.Value = v == null ? "" : v; } catch (Exception) { }
        }

        private static void SaveConfig()
        {
            try { if (AiChatPlugin.Instance != null) AiChatPlugin.Instance.Config.Save(); } catch (Exception) { }
        }

        private static void SetHint(string s)
        {
            if (_hint != null) _hint.text = s;
        }

        // ============================================================ UI 工具
        private static void EnsureEventSystem()
        {
            try
            {
                if (EventSystem.current != null) return;
                EventSystem[] all = UnityEngine.Object.FindObjectsOfType<EventSystem>();
                if (all != null && all.Length > 0) { EventSystem.current = all[0]; return; }
                GameObject go = new GameObject("AIChat_PromptEventSystem");
                go.AddComponent<EventSystem>();
                go.AddComponent<StandaloneInputModule>();
            }
            catch (Exception) { }
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
