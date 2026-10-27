// RF AiChat Bridge —— 第68轮：设置页「名称颜色」改为下拉菜单 + 修复游戏原生下拉/移除无意义开关
// - 名称颜色三行（鹰队/鸦队/玩家）：右侧按钮点开"下拉菜单"，配色与参考图一致（EAGLE/RAVEN/DUCK/PENGUIN/SPARROW/CROW/CUSTOM COLOR）
// - 修复 语言 / 说话频率 两个下拉菜单选项空白的问题（运行时补齐选项文本）
// - 隐藏"开启 AI 对话"勾选行（玩家在游戏内即可开关，不再需要设置页这个开关）
// - 标题「———— 字号 ————」→「———— 字体 ————」
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RF.AiChat
{
    internal static class NameColors
    {
        internal static readonly string[] Palette =
        {
            "Blue", "Red", "Yellow", "Green", "Orange", "Gray", "Black", "White", "Rainbow"
        };
        internal static readonly string[] Display =
        {
            "EAGLE", "RAVEN", "DUCK", "PENGUIN", "SPARROW", "CROW", "BLACK", "WHITE", "CUSTOM COLOR"
        };

        internal static int IndexOf(string v)
        {
            int i = Array.IndexOf(Palette, v);
            return i < 0 ? 0 : i;
        }

        internal static string HexFor(string v)
        {
            switch (v)
            {
                case "Blue": return "#3C41FD";
                case "Red": return "#FF5252";
                case "Yellow": return "#FFD400";
                case "Green": return "#00C853";
                case "Orange": return "#E07B39";
                case "Gray": return "#9E9E9E";
                case "Black": return "#000000";
                case "White": return "#FFFFFF";
                default: return null;
            }
        }

        internal static string Get(int style)
        {
            try
            {
                BepInEx.Configuration.ConfigEntry<string> e = null;
                if (style == 1) e = AiChatPlugin.CfgNameColorBlue;
                else if (style == 2) e = AiChatPlugin.CfgNameColorRed;
                else if (style == 3) e = AiChatPlugin.CfgNameColorPlayer;
                if (e != null && e.Value != null && e.Value.Length > 0)
                {
                    string v = e.Value.Trim();
                    if (Array.IndexOf(Palette, v) >= 0) return v;
                }
            }
            catch (Exception) { }
            if (style == 3) return "Rainbow";
            return style == 2 ? "Red" : "Blue";
        }

        internal static void Set(int style, string v)
        {
            try
            {
                BepInEx.Configuration.ConfigEntry<string> e = null;
                if (style == 1) e = AiChatPlugin.CfgNameColorBlue;
                else if (style == 2) e = AiChatPlugin.CfgNameColorRed;
                else if (style == 3) e = AiChatPlugin.CfgNameColorPlayer;
                if (e != null) e.Value = v;
                if (AiChatPlugin.Instance != null) AiChatPlugin.Instance.Config.Save();
                try { OverlayChat.MarkDirty(); } catch (Exception) { }
            }
            catch (Exception) { }
        }
    }

    internal static class NameColorRows
    {
        private static readonly string[] Titles = { "鹰队名色 / Eagle", "鸦队名色 / Raven", "玩家名色 / Player" };
        private static readonly int[] Styles = { 1, 2, 3 };
        private static readonly List<GameObject> _rows = new List<GameObject>();
        private static readonly List<TextMeshProUGUI> _valueTxt = new List<TextMeshProUGUI>();
        private static float _nextCheck;
        private static GameObject _dropPanel;
        private static int _dropStyle;
        private static bool _fontRenamed;
        private static bool _bilingualDone;
        private static GameObject _deadAiRow;
        private static GameObject _squadTagRow;
        private static GameObject _reportRow;
        private static GameObject _quotaBlueRow;
        private static GameObject _quotaRedRow;
        private static TMP_InputField _quotaBlueInput;
        private static TMP_InputField _quotaRedInput;
        private static TMP_FontAsset _cjkFallback;
        private static bool _cjkFallbackTried;

        // ★ 第87轮：直接检测模组设置面板是否存在（退出对局再进设置也能恢复）
        internal static bool PageVisible()
        {
            try
            {
                GameObject panel = GameObject.Find("Main Menu Canvas New/Mutator Config Panel");
                if (panel == null) return false;
                return panel.activeInHierarchy;
            }
            catch (Exception) { return false; }
        }

        internal static void Tick()
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.3f;
            try
            {
                if (!PageVisible())
                {
                    if (_dropPanel != null) { UnityEngine.Object.Destroy(_dropPanel); _dropPanel = null; }
                    _fontRenamed = false;
                    _bilingualDone = false;
                    return;
                }
                Transform content = FindContent();
                if (content == null) return;
                if (!_fontRenamed) { RenameFontHeader(content); _fontRenamed = true; }
                if (!_bilingualDone) { ApplyBilingualLabels(content); _bilingualDone = true; }
                FixGameDropdowns(content);
                HideMeaninglessToggle(content);
                EnsureDeadAiToggle(content);
                EnsureSquadTagToggle(content);
                EnsureAutoReportRow(content);
                EnsureQuotaRows(content);
                if (_rows.Count == 3 && _rows[0] != null && _rows[0].transform.parent == content)
                {
                    RefreshLabels();
                    return;
                }
                Rebuild(content);
            }
            catch (Exception) { }
        }

        private static Transform FindContent()
        {
            try
            {
                GameObject panel = GameObject.Find("Main Menu Canvas New/Mutator Config Panel");
                if (panel == null) return null;
                return panel.transform.Find("Scroll View/Viewport/Content");
            }
            catch (Exception) { return null; }
        }

        private static void RefreshLabels()
        {
            for (int i = 0; i < 3; i++)
                if (_valueTxt[i] != null) _valueTxt[i].text = NameColors.Display[NameColors.IndexOf(NameColors.Get(Styles[i]))];
        }

        // ★ 第83轮：设置页内容不支持切换语言 → 统一显示中英双语
        private static void ApplyBilingualLabels(Transform content)
        {
            try
            {
                string[,] map = new string[,]
                {
                    { "基础设置", "基础设置 / BASIC" },
                    { "配置 RF AI 对话", "配置 RF AI 对话 / CONFIGURE" },
                    { "聊天字号", "聊天字号 / Font Size" },
                    { "字号", "字体 / FONTS" },
                    { "语言", "语言 / Language" },
                    { "输入键", "输入键 / Input Key" },
                    { "说话频率", "说话频率 / Speech Rate" },
                    { "API 接口", "API 接口 / API URL" },
                    { "API 密钥", "API 密钥 / API Key" },
                    { "连接测试", "连接测试 / Connection Test" },
                    { "模型名称", "模型名称 / Model" },
                    { "聊天框设置", "聊天框设置 / CHAT BOX" },
                    { "聊天框宽度", "聊天框宽度 / Box Width" },
                    { "聊天框高度", "聊天框高度 / Box Height" },
                    { "左边距", "左边距 / Left Margin" },
                };
                // 先扫 content（列表项）
                TextMeshProUGUI[] all = content.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    TextMeshProUGUI t = all[i];
                    if (t == null) continue;
                    EnsureCjkFallback(t.font);
                    AutoTranslatorCompat.Ignore(t);
                    if (t.text == null) continue;
                    string cur = t.text;
                    if (cur.Contains(" / ") || cur.Length == 0) continue;
                    for (int k = 0; k < map.GetLength(0); k++)
                    {
                        if (cur.Contains(map[k, 0]))
                        {
                            t.text = map[k, 1];
                            break;
                        }
                    }
                }
                // 再扫全局，处理设置页标题（不在 content 内）
                TextMeshProUGUI[] world = UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>();
                for (int i = 0; i < world.Length; i++)
                {
                    TextMeshProUGUI t = world[i];
                    if (t == null || t.text == null) continue;
                    if (!t.text.Contains("配置 RF AI 对话")) continue;
                    if (t.text.Contains(" / ")) continue;
                    t.text = "配置 RF AI 对话 / CONFIGURE";
                }
            }
            catch (Exception) { }
        }

        // 「———— 字号 ————」→「———— 字体 ————」
        private static void RenameFontHeader(Transform content)
        {
            try
            {
                // 先扫 content（列表项）
                TextMeshProUGUI[] all = content.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    TextMeshProUGUI t = all[i];
                    if (t == null || t.text == null) continue;
                    if (!t.text.Contains("字号") || t.text.Contains("聊天字号")) continue;
                    t.text = t.text.Replace("字号", "字体 / FONTS");
                }
            }
            catch (Exception) { }
        }

        // ★ 第70轮：语言/说话频率下拉的选项文字修不好，直接把下拉换成"自绘按钮+自绘下拉"，
        //   选择时写回 TMP_Dropdown.value（游戏字段会同步更新）。
        private static void FixGameDropdowns(Transform content)
        {
            try
            {
                for (int i = 0; i < content.childCount; i++)
                {
                    Transform row = content.GetChild(i);
                    if (row == null) continue;
                    string label = RowLabel(row);
                    if (label == null) continue;
                    if (label.Contains("语言"))
                        ReplaceDropdown(row, new string[] { "中文", "English" });
                    else if (label.Contains("说话频率"))
                        ReplaceDropdown(row, new string[] { "少 (SMALL)", "正常 (NORMAL)", "多 (FULL)" });
                }
            }
            catch (Exception) { }
        }

        private static void ReplaceDropdown(Transform row, string[] labels)
        {
            TMP_Dropdown dd = row.GetComponentInChildren<TMP_Dropdown>(true);
            if (dd == null) return;
            if (!dd.gameObject.activeSelf) return;   // 已经替换过
            RectTransform drt = dd.transform as RectTransform;
            if (drt == null) return;
            TextMeshProUGUI fontSrc = row.GetComponentInChildren<TextMeshProUGUI>(true);
            Transform parent = drt.parent;
            GameObject btn = new GameObject("AIChatDDReplace", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            btn.transform.SetParent(parent, false);
            RectTransform brt = btn.GetComponent<RectTransform>();
            brt.anchorMin = drt.anchorMin; brt.anchorMax = drt.anchorMax;
            brt.pivot = drt.pivot; brt.anchoredPosition = drt.anchoredPosition;
            brt.sizeDelta = drt.sizeDelta;
            Image img = btn.GetComponent<Image>();
            img.color = new Color(0.11f, 0.13f, 0.17f, 1f);

            GameObject lgo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            lgo.transform.SetParent(btn.transform, false);
            RectTransform lrt = lgo.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(8f, 0f); lrt.offsetMax = new Vector2(-22f, 0f);
            TextMeshProUGUI lt = lgo.GetComponent<TextMeshProUGUI>();
            if (fontSrc != null) { lt.font = fontSrc.font; lt.fontSize = fontSrc.fontSize; }
            lt.alignment = TextAlignmentOptions.MidlineLeft;
            lt.enableWordWrapping = false;
            lt.color = new Color(0.92f, 0.94f, 0.97f, 1f);
            lt.text = labels[Mathf.Clamp(dd.value, 0, labels.Length - 1)];

            GameObject arrow = new GameObject("Arrow", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            arrow.transform.SetParent(btn.transform, false);
            RectTransform art = arrow.GetComponent<RectTransform>();
            art.anchorMin = new Vector2(1f, 0f); art.anchorMax = new Vector2(1f, 1f);
            art.pivot = new Vector2(1f, 0.5f);
            art.sizeDelta = new Vector2(22f, 0f);
            TextMeshProUGUI at = arrow.GetComponent<TextMeshProUGUI>();
            if (fontSrc != null) { at.font = fontSrc.font; at.fontSize = fontSrc.fontSize; }
            at.alignment = TextAlignmentOptions.Center;
            at.text = "▼";
            at.color = new Color(0.8f, 0.85f, 0.9f, 1f);

            TMP_Dropdown ddRef = dd;
            TextMeshProUGUI labelRef = lt;
            string[] labelsRef = labels;
            Button b = btn.GetComponent<Button>();
            b.targetGraphic = img;
            b.onClick.AddListener(new UnityEngine.Events.UnityAction(delegate
            {
                ShowStringDropdown(btn, labelsRef, ddRef.value, delegate (int idx)
                {
                    ddRef.value = idx;
                    try { ddRef.onValueChanged.Invoke(idx); } catch (Exception) { }
                    if (labelRef != null) labelRef.text = labelsRef[Mathf.Clamp(idx, 0, labelsRef.Length - 1)];
                });
            }));

            dd.gameObject.SetActive(false);
        }

        private static void ShowStringDropdown(GameObject btn, string[] labels, int current, Action<int> onPick)
        {
            try
            {
                if (_dropPanel != null && _dropStyle == -100) { UnityEngine.Object.Destroy(_dropPanel); _dropPanel = null; return; }
                if (_dropPanel != null) { UnityEngine.Object.Destroy(_dropPanel); _dropPanel = null; }
                Canvas canvas = btn.GetComponentInParent<Canvas>();
                if (canvas == null) return;
                Transform root = canvas.rootCanvas.transform;
                RectTransform crt = root as RectTransform;
                RectTransform brt = btn.GetComponent<RectTransform>();
                Vector3[] corners = new Vector3[4];
                brt.GetWorldCorners(corners);
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, new Vector3(corners[0].x, corners[0].y, 0f));
                Vector2 local;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(crt, screen, null, out local);

                float rowH = 26f;
                float w = Math.Max(150f, brt.rect.width);
                float h = rowH * labels.Length + 6f;
                GameObject panel = new GameObject("AIChatDDList", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                panel.transform.SetParent(root, false);
                panel.transform.SetAsLastSibling();
                RectTransform prt = panel.GetComponent<RectTransform>();
                prt.anchorMin = new Vector2(0.5f, 0.5f);
                prt.anchorMax = new Vector2(0.5f, 0.5f);
                prt.pivot = new Vector2(0f, 1f);
                prt.anchoredPosition = local;
                prt.sizeDelta = new Vector2(w, h);
                panel.GetComponent<Image>().color = new Color(0.13f, 0.16f, 0.21f, 1f);
                _dropPanel = panel;
                _dropStyle = -100;

                TextMeshProUGUI fontSrc = _valueTxt.Count > 0 ? _valueTxt[0] : null;
                for (int i = 0; i < labels.Length; i++)
                {
                    int idx = i;
                    GameObject item = new GameObject("Item" + i, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                    item.transform.SetParent(panel.transform, false);
                    RectTransform irt = item.GetComponent<RectTransform>();
                    irt.anchorMin = new Vector2(0f, 1f); irt.anchorMax = new Vector2(1f, 1f);
                    irt.pivot = new Vector2(0.5f, 1f);
                    irt.anchoredPosition = new Vector2(0f, -3f - i * rowH);
                    irt.sizeDelta = new Vector2(-6f, rowH);
                    item.GetComponent<Image>().color = new Color(0.13f, 0.16f, 0.21f, 1f);

                    GameObject lgo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                    lgo.transform.SetParent(item.transform, false);
                    RectTransform lrt = lgo.GetComponent<RectTransform>();
                    lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
                    lrt.offsetMin = new Vector2(10f, 0f); lrt.offsetMax = new Vector2(-6f, 0f);
                    TextMeshProUGUI lt = lgo.GetComponent<TextMeshProUGUI>();
                    if (fontSrc != null) { lt.font = fontSrc.font; lt.fontSize = fontSrc.fontSize; }
                    lt.alignment = TextAlignmentOptions.MidlineLeft;
                    lt.enableWordWrapping = false;
                    lt.color = new Color(0.92f, 0.94f, 0.97f, 1f);
                    lt.text = labels[i];

                    Button ib = item.GetComponent<Button>();
                    ib.targetGraphic = item.GetComponent<Image>();
                    ib.onClick.AddListener(new UnityEngine.Events.UnityAction(delegate
                    {
                        if (onPick != null) onPick(idx);
                        if (_dropPanel != null) { UnityEngine.Object.Destroy(_dropPanel); _dropPanel = null; }
                    }));
                }
            }
            catch (Exception) { }
        }

        private static string RowLabel(Transform row)
        {
            try
            {
                TMP_Dropdown dd = row.GetComponentInChildren<TMP_Dropdown>(true);
                string caption = dd != null && dd.captionText != null ? dd.captionText.text : null;
                TextMeshProUGUI[] tmps = row.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < tmps.Length; i++)
                {
                    TextMeshProUGUI t = tmps[i];
                    if (t == null || t.text == null) continue;
                    if (caption != null && t.text == caption) continue;
                    if (t.text.Length == 0) continue;
                    if (t.text.Contains("——") || t.text.Length > 40) continue;
                    if (t.text == "中文" || t.text == "English" || t.text == "正常" || t.text == "少" || t.text == "多") continue;
                    return t.text;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static void FillDropdown(Transform row, string[] labels)
        {
            try
            {
                TMP_Dropdown dd = row.GetComponentInChildren<TMP_Dropdown>(true);
                if (dd == null) return;
                bool need = dd.options == null || dd.options.Count != labels.Length;
                if (!need)
                {
                    for (int i = 0; i < labels.Length; i++)
                        if (string.IsNullOrEmpty(dd.options[i].text)) { need = true; break; }
                }
                if (!need) return;
                int cur = dd.value;
                dd.ClearOptions();
                dd.AddOptions(new List<string>(labels));
                dd.value = Mathf.Clamp(cur, 0, labels.Length - 1);
                dd.RefreshShownValue();
            }
            catch (Exception) { }
        }

        // 隐藏"开启 AI 对话"勾选行
        private static void HideMeaninglessToggle(Transform content)
        {
            try
            {
                for (int i = 0; i < content.childCount; i++)
                {
                    Transform row = content.GetChild(i);
                    if (row == null) continue;
                    string label = RowLabel(row);
                    if (label != null && label.Contains("开启 AI 对话"))
                    {
                        if (row.gameObject.activeSelf) row.gameObject.SetActive(false);
                        return;
                    }
                }
            }
            catch (Exception) { }
        }

        // ★ 第79轮：基础设置里"自动战报间隔"改为**下拉预设**（自绘按钮+列表，彻底绕开输入框不可编辑问题）
        private static readonly int[] ReportPresets = { 60, 90, 120, 180, 240, 300, 600, 900, 1800 };
        private static TextMeshProUGUI _reportTxt;

        private static void EnsureAutoReportRow(Transform content)
        {
            try
            {
                if (_reportRow != null && _reportRow.transform.parent == content)
                {
                    if (_reportTxt != null)
                    {
                        int cur = 180;
                        try { cur = AiChatPlugin.CfgAutoReportSeconds == null ? 180 : AiChatPlugin.CfgAutoReportSeconds.Value; } catch (Exception) { }
                        string want = cur + " s";
                        if (_reportTxt.text != want) _reportTxt.text = want;
                    }
                    return;
                }
                GameObject template = null;
                for (int i = 0; i < content.childCount && template == null; i++)
                {
                    Transform row = content.GetChild(i);
                    if (row == null) continue;
                    string label = RowLabel(row);
                    if (label != null && label.Contains("聊天字号")) template = row.gameObject;
                }
                if (template == null) return;
                GameObject clone = UnityEngine.Object.Instantiate(template, content);
                clone.name = "AIChatAutoReportRow";
                int idxRow = (_deadAiRow != null && _deadAiRow.transform.parent == content)
                    ? _deadAiRow.transform.GetSiblingIndex() + 1 : template.transform.GetSiblingIndex() + 1;
                clone.transform.SetSiblingIndex(idxRow);

                TMP_InputField inClone = clone.GetComponentInChildren<TMP_InputField>(true);
                TextMeshProUGUI valueTmp = inClone != null ? (inClone.textComponent as TextMeshProUGUI) : null;
                TextMeshProUGUI[] tmps = clone.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < tmps.Length; i++)
                {
                    if (tmps[i] == null || tmps[i] == valueTmp) continue;
                    if (tmps[i].text != null && tmps[i].text.Contains("聊天字号")) { tmps[i].text = "AI 战报间隔 / Auto report"; break; }
                }
                if (valueTmp == null) { _reportRow = clone; return; }
                if (inClone != null) inClone.enabled = false;
                valueTmp.gameObject.SetActive(false);
                Transform container = valueTmp.transform.parent;
                TMP_InputField[] others = container.GetComponentsInChildren<TMP_InputField>(true);
                for (int i = 0; i < others.Length; i++) if (others[i] != null) others[i].enabled = false;

                GameObject btn = new GameObject("AutoReportBtn", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                btn.transform.SetParent(container, false);
                RectTransform brt = btn.GetComponent<RectTransform>();
                brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
                brt.offsetMin = new Vector2(0f, 2f); brt.offsetMax = new Vector2(0f, -2f);
                Image img = btn.GetComponent<Image>();
                img.color = new Color(0.11f, 0.13f, 0.17f, 1f);

                GameObject lgo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                lgo.transform.SetParent(btn.transform, false);
                RectTransform lrt = lgo.GetComponent<RectTransform>();
                lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
                lrt.offsetMin = new Vector2(8f, 0f); lrt.offsetMax = new Vector2(-8f, 0f);
                TextMeshProUGUI lt = lgo.GetComponent<TextMeshProUGUI>();
                lt.font = valueTmp.font;
                lt.fontSize = valueTmp.fontSize;
                EnsureCjkFallback(lt.font);
                AutoTranslatorCompat.Ignore(lt);
                lt.alignment = TextAlignmentOptions.Center;
                lt.enableWordWrapping = false;
                lt.color = new Color(0.92f, 0.94f, 0.97f, 1f);
                int cur2 = 180;
                try { cur2 = AiChatPlugin.CfgAutoReportSeconds == null ? 180 : AiChatPlugin.CfgAutoReportSeconds.Value; } catch (Exception) { }
                lt.text = cur2 + " 秒";
                _reportTxt = lt;

                Button b = btn.GetComponent<Button>();
                b.targetGraphic = img;
                b.onClick.AddListener(new UnityEngine.Events.UnityAction(delegate
                {
                    string[] labels = new string[ReportPresets.Length];
                    int cur3 = 180;
                    try { cur3 = AiChatPlugin.CfgAutoReportSeconds == null ? 180 : AiChatPlugin.CfgAutoReportSeconds.Value; } catch (Exception) { }
                    int curIdx = 0;
                    for (int i = 0; i < ReportPresets.Length; i++)
                    {
                        labels[i] = ReportPresets[i] + " 秒";
                        if (ReportPresets[i] == cur3) curIdx = i;
                    }
                    ShowStringDropdown(btn, labels, curIdx, delegate (int idx)
                    {
                        int sec = ReportPresets[Mathf.Clamp(idx, 0, ReportPresets.Length - 1)];
                        try
                        {
                            if (AiChatPlugin.CfgAutoReportSeconds != null) AiChatPlugin.CfgAutoReportSeconds.Value = sec;
                            if (AiChatPlugin.Instance != null) AiChatPlugin.Instance.Config.Save();
                        }
                        catch (Exception) { }
                        try { ChatEngine.SendCfgToLua(); } catch (Exception) { }
                        if (_reportTxt != null) _reportTxt.text = sec + " 秒";
                        if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogInfo("[AIChat][设置] 自动战报间隔已设为 " + sec + " 秒");
                    });
                }));
                _reportRow = clone;
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogInfo("[AIChat][设置] 自动战报间隔下拉已插入");
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AIChat][设置] 战报间隔行创建失败：" + e.Message);
            }
        }

        // ============================================================ v1.1.2 主动发言名额（两行数字输入）
        private static void EnsureQuotaRows(Transform content)
        {
            EnsureQuotaRow(content, "AI 说话名额·鹰队（蓝） / Blue active speakers", true, ref _quotaBlueRow, ref _quotaBlueInput);
            EnsureQuotaRow(content, "AI 说话名额·鸦队（红） / Red active speakers", false, ref _quotaRedRow, ref _quotaRedInput);
        }

        private static void EnsureQuotaRow(Transform content, string title, bool blue, ref GameObject rowField, ref TMP_InputField inputField)
        {
            try
            {
                int cur = 30;
                try
                {
                    if (blue && AiChatPlugin.CfgActiveSpeakerLimitBlue != null) cur = AiChatPlugin.CfgActiveSpeakerLimitBlue.Value;
                    if (!blue && AiChatPlugin.CfgActiveSpeakerLimitRed != null) cur = AiChatPlugin.CfgActiveSpeakerLimitRed.Value;
                }
                catch (Exception) { }
                if (cur < 0) cur = 0;
                if (cur > 128) cur = 128;
                if (rowField != null && rowField.transform.parent == content)
                {
                    if (inputField != null && !inputField.isFocused && inputField.text != cur.ToString())
                        inputField.text = cur.ToString();
                    return;
                }
                GameObject template = null;
                for (int i = 0; i < content.childCount && template == null; i++)
                {
                    Transform row = content.GetChild(i);
                    if (row == null) continue;
                    string label = RowLabel(row);
                    if (label != null && label.Contains("聊天字号")) template = row.gameObject;
                }
                if (template == null) return;
                GameObject clone = UnityEngine.Object.Instantiate(template, content);
                clone.name = blue ? "AIChatQuotaBlueRow" : "AIChatQuotaRedRow";
                int idxRow = (_reportRow != null && _reportRow.transform.parent == content)
                    ? _reportRow.transform.GetSiblingIndex() + (blue ? 1 : 2) : template.transform.GetSiblingIndex() + 1;
                clone.transform.SetSiblingIndex(idxRow);
                TMP_InputField input = clone.GetComponentInChildren<TMP_InputField>(true);
                TextMeshProUGUI valueTmp = input != null ? (input.textComponent as TextMeshProUGUI) : null;
                TextMeshProUGUI[] tmps = clone.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < tmps.Length; i++)
                {
                    if (tmps[i] == null || tmps[i] == valueTmp) continue;
                    if (tmps[i].text != null && tmps[i].text.Contains("聊天字号"))
                    {
                        tmps[i].text = title;
                        EnsureCjkFallback(tmps[i].font);
                        AutoTranslatorCompat.Ignore(tmps[i]);
                        break;
                    }
                }
                if (input == null) { rowField = clone; return; }
                input.enabled = true;
                input.interactable = true;
                try { input.readOnly = false; } catch (Exception) { }
                input.characterValidation = TMP_InputField.CharacterValidation.Integer;
                input.contentType = TMP_InputField.ContentType.IntegerNumber;
                input.characterLimit = 3;
                try { input.onValueChanged.RemoveAllListeners(); } catch (Exception) { }
                try { input.onEndEdit.RemoveAllListeners(); } catch (Exception) { }
                try { input.onSubmit.RemoveAllListeners(); } catch (Exception) { }
                input.text = cur.ToString();
                input.onEndEdit.AddListener(new UnityEngine.Events.UnityAction<string>(delegate (string v)
                {
                    int n = cur;
                    int.TryParse(v, out n);
                    if (n < 0) n = 0;
                    if (n > 128) n = 128;
                    try
                    {
                        if (blue && AiChatPlugin.CfgActiveSpeakerLimitBlue != null) AiChatPlugin.CfgActiveSpeakerLimitBlue.Value = n;
                        if (!blue && AiChatPlugin.CfgActiveSpeakerLimitRed != null) AiChatPlugin.CfgActiveSpeakerLimitRed.Value = n;
                        if (AiChatPlugin.Instance != null) AiChatPlugin.Instance.Config.Save();
                    }
                    catch (Exception) { }
                    if (input != null) input.text = n.ToString();
                    AiChatPlugin.Log.LogInfo("[发言名额] " + (blue ? "蓝队" : "红队")
                        + " 主动发言名额已设为 " + n + "（下一局生效）");
                }));
                rowField = clone;
                inputField = input;
                if (AiChatPlugin.Log != null)
                    AiChatPlugin.Log.LogInfo("[发言名额] 设置页行已插入：" + (blue ? "蓝队" : "红队") + "（当前 " + cur + "）");
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null)
                    AiChatPlugin.Log.LogWarning("[发言名额] 行创建失败：" + e.Message);
            }
        }

        // v1.1.3：AI 名字后显示 (x队) 的开关（克隆"开启 AI 对话"行）
        private static void EnsureSquadTagToggle(Transform content)
        {
            try
            {
                if (_squadTagRow != null && _squadTagRow.transform.parent == content)
                {
                    Toggle tg0 = _squadTagRow.GetComponentInChildren<Toggle>(true);
                    bool want = true;
                    try { want = AiChatPlugin.CfgShowSquadTag == null || AiChatPlugin.CfgShowSquadTag.Value; } catch (Exception) { }
                    if (tg0 != null && tg0.isOn != want) tg0.isOn = want;
                    return;
                }
                GameObject template = null;
                GameObject enableRow = null;
                for (int i = 0; i < content.childCount; i++)
                {
                    Transform row = content.GetChild(i);
                    if (row == null) continue;
                    string label = RowLabel(row);
                    if (label == null) continue;
                    if (label.Contains("开启 AI 对话")) template = row.gameObject;
                    else if (label.Contains("说话频率")) enableRow = row.gameObject;
                }
                if (template == null) return;
                if (enableRow == null) enableRow = template;
                GameObject clone = UnityEngine.Object.Instantiate(template, content);
                clone.name = "AIChatSquadTagRow";
                clone.transform.SetSiblingIndex(enableRow.transform.GetSiblingIndex() + 2);
                TextMeshProUGUI[] tmps = clone.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < tmps.Length; i++)
                {
                    if (tmps[i] == null || tmps[i].text == null) continue;
                    if (tmps[i].text.Contains("开启 AI 对话"))
                    {
                        tmps[i].text = "AI 小队编号显示 / Squad tag";
                        EnsureCjkFallback(tmps[i].font);
                        AutoTranslatorCompat.Ignore(tmps[i]);
                        break;
                    }
                }
                Toggle tg = clone.GetComponentInChildren<Toggle>(true);
                if (tg != null)
                {
                    bool want = true;
                    try { want = AiChatPlugin.CfgShowSquadTag == null || AiChatPlugin.CfgShowSquadTag.Value; } catch (Exception) { }
                    tg.isOn = want;
                    tg.onValueChanged.AddListener(new UnityEngine.Events.UnityAction<bool>(delegate (bool v)
                    {
                        try
                        {
                            if (AiChatPlugin.CfgShowSquadTag != null) AiChatPlugin.CfgShowSquadTag.Value = v;
                            if (AiChatPlugin.Instance != null) AiChatPlugin.Instance.Config.Save();
                        }
                        catch (Exception) { }
                    }));
                }
                clone.SetActive(true);
                _squadTagRow = clone;
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogInfo("[AIChat][设置] 小队编号开关已插入");
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AIChat][设置] 小队编号开关创建失败：" + e.Message);
            }
        }

        // ★ 第74轮：基础设置里加        // ★ 第74轮：基础设置里加"允许死亡/未部署 AI 发言"勾选框（克隆"开启 AI 对话"那一行）
        private static void EnsureDeadAiToggle(Transform content)
        {
            try
            {
                if (_deadAiRow != null && _deadAiRow.transform.parent == content)
                {
                    Toggle tg0 = _deadAiRow.GetComponentInChildren<Toggle>(true);
                    bool want = true;
                    try { want = AiChatPlugin.CfgAllowDeadAI == null || AiChatPlugin.CfgAllowDeadAI.Value; } catch (Exception) { }
                    if (tg0 != null && tg0.isOn != want) tg0.isOn = want;
                    return;
                }

                GameObject template = null;
                int idx = -1;
                GameObject enableRow = null;
                for (int i = 0; i < content.childCount; i++)
                {
                    Transform row = content.GetChild(i);
                    if (row == null) continue;
                    string label = RowLabel(row);
                    if (label == null) continue;
                    if (label.Contains("开启 AI 对话")) { template = row.gameObject; idx = i; }
                    else if (label.Contains("说话频率")) enableRow = row.gameObject;
                }
                if (template == null) return;
                if (enableRow == null) enableRow = template;

                GameObject clone = UnityEngine.Object.Instantiate(template, content);
                clone.name = "AIChatDeadAiRow";
                clone.transform.SetSiblingIndex(enableRow.transform.GetSiblingIndex() + 1);
                // 改标签
                TextMeshProUGUI[] tmps = clone.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < tmps.Length; i++)
                {
                    if (tmps[i] == null || tmps[i].text == null) continue;
                    if (tmps[i].text.Contains("开启 AI 对话"))
                    {
                        tmps[i].text = "允许死亡AI说话 / Dead AI speak";
                        EnsureCjkFallback(tmps[i].font);
                        AutoTranslatorCompat.Ignore(tmps[i]);
                        break;
                    }
                }
                Toggle tg = clone.GetComponentInChildren<Toggle>(true);
                if (tg != null)
                {
                    bool want = true;
                    try { want = AiChatPlugin.CfgAllowDeadAI == null || AiChatPlugin.CfgAllowDeadAI.Value; } catch (Exception) { }
                    tg.isOn = want;
                    tg.onValueChanged.AddListener(new UnityEngine.Events.UnityAction<bool>(delegate (bool v)
                    {
                        try
                        {
                            if (AiChatPlugin.CfgAllowDeadAI != null) AiChatPlugin.CfgAllowDeadAI.Value = v;
                            if (AiChatPlugin.Instance != null) AiChatPlugin.Instance.Config.Save();
                        }
                        catch (Exception) { }
                    }));
                }
                clone.SetActive(true);
                _deadAiRow = clone;
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AIChat][设置] 死亡AI发言开关创建失败：" + e.Message);
            }
        }

        private static void Rebuild(Transform content)
        {
            try
            {
                for (int i = 0; i < _rows.Count; i++) { if (_rows[i] != null) UnityEngine.Object.Destroy(_rows[i]); }
                _rows.Clear();
                _valueTxt.Clear();

                GameObject template = null;
                int templateIndex = -1;
                for (int i = 0; i < content.childCount && template == null; i++)
                {
                    Transform row = content.GetChild(i);
                    if (row == null) continue;
                    TextMeshProUGUI[] tmps = row.GetComponentsInChildren<TextMeshProUGUI>(true);
                    for (int k = 0; k < tmps.Length; k++)
                    {
                        bool isChatFont = tmps[k] != null && tmps[k].text != null && tmps[k].text.Contains("聊天字号");
                        if (isChatFont)
                        {
                            template = row.gameObject;
                            templateIndex = i;
                            break;
                        }
                    }
                }
                if (template == null || templateIndex < 0) return;

                for (int i = 0; i < 3; i++)
                {
                    GameObject row = UnityEngine.Object.Instantiate(template, content);
                    row.name = "AIChatNameColorRow" + i;
                    row.transform.SetSiblingIndex(templateIndex + 1 + i);
                    BuildRow(row, Titles[i], Styles[i]);
                    _rows.Add(row);
                }
                if (AiChatPlugin.Log != null)
                    AiChatPlugin.Log.LogInfo("[AIChat][名色] 已在设置页插入 3 行名称颜色（下拉菜单）");
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AIChat][名色] 插入失败：" + e.Message);
            }
        }

        // 游戏自带的 TMP 字体只覆盖游戏内用到的字；我们插入的“主动发言/自动战报”等新词
        // 可能缺字（动、发等）。这里从系统字体创建一个 TMP fallback，只给缺字时使用。
        // 关掉 XUnity.AutoTranslator 后它原本提供的 fallback 没了，必须由我们自己补。
        private static void EnsureCjkFallback(TMP_FontAsset baseFont)
        {
            if (_cjkFallback == null && !_cjkFallbackTried)
            {
                _cjkFallbackTried = true;
                try
                {
                    Font osFont = null;
                    string[] names = new string[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "SimSun" };
                    for (int i = 0; i < names.Length && osFont == null; i++)
                    {
                        try { osFont = Font.CreateDynamicFontFromOSFont(names[i], 20); } catch (Exception) { }
                    }
                    if (osFont != null)
                    {
                        TMP_FontAsset fa = TMP_FontAsset.CreateFontAsset(osFont);
                        if (fa != null)
                        {
                            fa.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                            fa.isMultiAtlasTexturesEnabled = true;
                            _cjkFallback = fa;
                            if (AiChatPlugin.Log != null)
                                AiChatPlugin.Log.LogInfo("[AIChat][字体] 已创建中文 fallback 字体（来自系统字体，用于补 动/发 等缺字）");
                        }
                    }
                    if (_cjkFallback == null && AiChatPlugin.Log != null)
                        AiChatPlugin.Log.LogWarning("[AIChat][字体] 未找到可用的系统中文字体，无法补缺字");
                }
                catch (Exception e)
                {
                    if (AiChatPlugin.Log != null)
                        AiChatPlugin.Log.LogWarning("[AIChat][字体] 创建中文 fallback 失败：" + e.Message);
                }
            }
            if (_cjkFallback == null || baseFont == null) return;
            try
            {
                if (baseFont.fallbackFontAssetTable == null)
                    baseFont.fallbackFontAssetTable = new System.Collections.Generic.List<TMP_FontAsset>();
                if (!baseFont.fallbackFontAssetTable.Contains(_cjkFallback))
                    baseFont.fallbackFontAssetTable.Add(_cjkFallback);
            }
            catch (Exception) { }
        }

        private static void BuildRow(GameObject row, string title, int style)
        {
            try
            {
                TextMeshProUGUI label = null;
                TMP_InputField input = row.GetComponentInChildren<TMP_InputField>(true);
                TextMeshProUGUI valueTmp = input != null ? (input.textComponent as TextMeshProUGUI) : null;
                TextMeshProUGUI[] tmps = row.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < tmps.Length; i++)
                {
                    if (tmps[i] == null || tmps[i] == valueTmp) continue;
                    if (tmps[i].text != null && tmps[i].text.Contains("聊天字号")) { label = tmps[i]; break; }
                }
                if (label == null) { for (int i = 0; i < tmps.Length; i++) if (tmps[i] != valueTmp) { label = tmps[i]; break; } }
                if (label != null) label.text = title;
                if (valueTmp != null) valueTmp.gameObject.SetActive(false);
                if (input != null) input.enabled = false;
                if (valueTmp == null) return;

                Transform container = valueTmp.transform.parent;
                TMP_InputField[] cinputs = container.GetComponentsInChildren<TMP_InputField>(true);
                for (int i = 0; i < cinputs.Length; i++) if (cinputs[i] != null) cinputs[i].enabled = false;

                GameObject btn = new GameObject("NameColorBtn", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                btn.transform.SetParent(container, false);
                RectTransform brt = btn.GetComponent<RectTransform>();
                brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
                brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;

                GameObject txtGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                txtGo.transform.SetParent(btn.transform, false);
                RectTransform trt = txtGo.GetComponent<RectTransform>();
                trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
                trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
                TextMeshProUGUI txt = txtGo.GetComponent<TextMeshProUGUI>();
                try
                {
                    txt.font = valueTmp.font;
                    txt.fontSize = valueTmp.fontSize;
                    AutoTranslatorCompat.Ignore(txt);
                    txt.enableWordWrapping = false;
                    txt.alignment = TextAlignmentOptions.Center;
                    txt.color = new Color(0.92f, 0.94f, 0.97f, 1f);
                    txt.text = NameColors.Display[NameColors.IndexOf(NameColors.Get(style))];
                }
                catch (Exception) { }
                _valueTxt.Add(txt);

                Image img = btn.GetComponent<Image>();
                img.color = new Color(0.20f, 0.24f, 0.30f, 1f);
                Button b = btn.GetComponent<Button>();
                b.targetGraphic = img;
                int st = style;
                b.onClick.AddListener(new UnityEngine.Events.UnityAction(delegate { ShowDropdown(btn, st); }));
            }
            catch (Exception) { }
        }

        // 打开"名称颜色"下拉菜单（模仿游戏原生下拉外观）
        private static void ShowDropdown(GameObject btn, int style)
        {
            try
            {
                if (_dropPanel != null && _dropStyle == style)
                {
                    UnityEngine.Object.Destroy(_dropPanel);
                    _dropPanel = null;
                    return;
                }
                if (_dropPanel != null) { UnityEngine.Object.Destroy(_dropPanel); _dropPanel = null; }

                Canvas canvas = btn.GetComponentInParent<Canvas>();
                if (canvas == null) return;
                Transform root = canvas.rootCanvas.transform;
                RectTransform crt = root as RectTransform;
                RectTransform brt = btn.GetComponent<RectTransform>();
                Vector3[] corners = new Vector3[4];
                brt.GetWorldCorners(corners);
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, new Vector3(corners[0].x, corners[0].y, 0f));
                Vector2 local;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(crt, screen, null, out local);

                float rowH = 26f;
                float w = Math.Max(160f, brt.rect.width);
                float h = rowH * NameColors.Palette.Length + 6f;
                GameObject panel = new GameObject("NameColorDropdown", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                panel.transform.SetParent(root, false);
                panel.transform.SetAsLastSibling();
                RectTransform prt = panel.GetComponent<RectTransform>();
                prt.anchorMin = new Vector2(0.5f, 0.5f);
                prt.anchorMax = new Vector2(0.5f, 0.5f);
                prt.pivot = new Vector2(0f, 1f);
                prt.anchoredPosition = local;
                prt.sizeDelta = new Vector2(w, h);
                Image pbg = panel.GetComponent<Image>();
                pbg.color = new Color(0.13f, 0.16f, 0.21f, 1f);
                _dropPanel = panel;
                _dropStyle = style;

                TextMeshProUGUI fontSrc = _valueTxt.Count > 0 ? _valueTxt[0] : null;
                for (int i = 0; i < NameColors.Palette.Length; i++)
                {
                    int idx = i;
                    string key = NameColors.Palette[i];
                    string hex = NameColors.HexFor(key);
                    string label = NameColors.Display[i];
                    GameObject item = new GameObject("Item" + i, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                    item.transform.SetParent(panel.transform, false);
                    RectTransform irt = item.GetComponent<RectTransform>();
                    irt.anchorMin = new Vector2(0f, 1f); irt.anchorMax = new Vector2(1f, 1f);
                    irt.pivot = new Vector2(0.5f, 1f);
                    irt.anchoredPosition = new Vector2(0f, -3f - i * rowH);
                    irt.sizeDelta = new Vector2(-6f, rowH);
                    Image ibg = item.GetComponent<Image>();
                    ibg.color = new Color(0.13f, 0.16f, 0.21f, 1f);

                    GameObject lgo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                    lgo.transform.SetParent(item.transform, false);
                    RectTransform lrt = lgo.GetComponent<RectTransform>();
                    lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
                    lrt.offsetMin = new Vector2(10f, 0f); lrt.offsetMax = new Vector2(-6f, 0f);
                    TextMeshProUGUI lt = lgo.GetComponent<TextMeshProUGUI>();
                    if (fontSrc != null) { lt.font = fontSrc.font; lt.fontSize = fontSrc.fontSize; }
                    lt.alignment = TextAlignmentOptions.MidlineLeft;
                    lt.enableWordWrapping = false;
                    if (key == "Rainbow")
                    {
                        string rb = "";
                        for (int k = 0; k < label.Length; k++)
                        {
                            Color c = Color.HSVToRGB((float)k / Math.Max(1, label.Length - 1) * 0.85f, 0.85f, 1f);
                            rb += "<color=#" + ColorUtility.ToHtmlStringRGB(c) + ">" + label[k] + "</color>";
                        }
                        lt.text = rb;
                    }
                    else
                    {
                        lt.text = label;
                        lt.color = hex != null ? HexColor(hex) : Color.white;
                    }

                    Button ib = item.GetComponent<Button>();
                    ib.targetGraphic = ibg;
                    ib.onClick.AddListener(new UnityEngine.Events.UnityAction(delegate
                    {
                        NameColors.Set(style, key);
                        if (_valueTxt.Count > style - 1 && _valueTxt[style - 1] != null)
                            _valueTxt[style - 1].text = label;
                        if (_dropPanel != null) { UnityEngine.Object.Destroy(_dropPanel); _dropPanel = null; }
                    }));
                }
            }
            catch (Exception e)
            {
                if (AiChatPlugin.Log != null) AiChatPlugin.Log.LogWarning("[AIChat][名色] 下拉打开失败：" + e.Message);
            }
        }

        private static Color HexColor(string hex)
        {
            Color c;
            if (ColorUtility.TryParseHtmlString(hex, out c)) return c;
            return Color.white;
        }
    }
}
