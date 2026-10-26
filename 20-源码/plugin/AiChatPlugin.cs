// RF AiChat Bridge —— BepInEx 5 插件
// M1：验证游戏进程内能否用 HTTPS 调通 OpenAI 兼容接口
// M2：把 AiChatBridge 静态接口注入 Ravenscript 全局作用域，并自测「Lua → C#」调用是否真的通
// 编译约束：用 .NET Framework 自带 csc（C# 5 语法）——不使用字符串插值 / ?. / 表达式体成员。
using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;
using UnityEngine;

namespace RF.AiChat
{
    [BepInPlugin(GUID, "RF AiChat Bridge", "1.6.2")]
    public class AiChatPlugin : BaseUnityPlugin
    {
        public const string GUID = "rf.aichat.bridge";
        public const string VERSION = "1.6.2";

        internal static ManualLogSource Log;
        internal static AiChatPlugin Instance;
        internal static ConfigEntry<string> CfgApiUrl;
        // 实际生效值：模组设置界面里填了就用它，留空则回落到配置文件
        internal static string EffApiUrl { get { return ChatEngine.ApiUrlOverride; } }   // 只认模组设置界面里的值
        internal static string EffApiKey { get { return ChatEngine.ApiKeyOverride; } }   // 只认玩家自己填的 Key（不回落、不带内置 Key）
        internal static string EffModel { get { return ChatEngine.ModelOverride; } }      // 只认模组设置界面里的模型名
        // ★ 第37轮：调试窗口用 —— 最近一次 API 返回的 token 用量（接口不返回 usage 时为 -1）
        internal static int LastPromptTokens = -1;
        internal static int LastCompletionTokens = -1;
        internal static ConfigEntry<string> CfgDebugLang;
        // ★ 第46轮：提示词自定义（MOD 设置界面里的「提示词」面板写入；留空=内置默认）
        internal static ConfigEntry<string> CfgPromptGlobal;
        internal static ConfigEntry<string> CfgPromptEagle;
        internal static ConfigEntry<string> CfgPromptRaven;
        internal static ConfigEntry<string> CfgPromptRookie;
        internal static ConfigEntry<string> CfgPromptNormal;
        internal static ConfigEntry<string> CfgPromptVeteran;
        internal static ConfigEntry<string> CfgPromptAce;
        internal static ConfigEntry<string> CfgPromptMatch;   // ★ 第51轮：对局提示词（全体角色）
        internal static ConfigEntry<string> CfgPlayerTitle;   // ★ 第60轮：玩家（指挥官）称呼
        // ★ 第67轮：名称颜色（Blue/Red/Yellow/Green/Orange/Gray/Black/White/Rainbow）
        internal static ConfigEntry<string> CfgNameColorBlue;
        internal static ConfigEntry<string> CfgNameColorRed;
        internal static ConfigEntry<string> CfgNameColorPlayer;
        internal static ConfigEntry<string> CfgSpectatorTitle;   // ★ 第69轮：观察者（战地记者）称呼
        internal static ConfigEntry<int> CfgSystemReplyChance;   // ★ 第73轮：系统播报被 AI 回应的概率（%）——第74轮起由 PerTeam 取代
        internal static ConfigEntry<int> CfgSystemReplyPerTeam;  // ★ 第74轮：每队派几名 AI 回应系统播报
        internal static ConfigEntry<bool> CfgAllowDeadAI;        // ★ 第74轮：是否允许死亡/未部署 AI 发言
        internal static ConfigEntry<int> CfgAutoReportSeconds;   // ★ 第77轮：自动战报间隔（秒）
        internal static ConfigEntry<int> CfgActiveSpeakerLimitBlue;  // v1.1.2：蓝队主动发言名额
        internal static ConfigEntry<int> CfgActiveSpeakerLimitRed;   // v1.1.2：红队主动发言名额
        internal static ConfigEntry<bool> CfgVerboseLog;         // ★ 第89轮：详细日志（默认关，减少日志 I/O 卡顿）
        // ★ 第50轮：人设分配（比例/时间）自定义
        internal static ConfigEntry<int> CfgPersonaRatio1;
        internal static ConfigEntry<int> CfgPersonaRatio2;
        internal static ConfigEntry<int> CfgPersonaRatio3;
        internal static ConfigEntry<int> CfgPersonaFirstAssign;
        internal static ConfigEntry<int> CfgPersonaReassignEvery;
        internal static ConfigEntry<string> CfgModel;
        // ★ 第 28 轮：Key 本地缓存。模组设置界面的 Key 在部分情况下不会跨游戏重启保存，
        //   导致"重启后对话全没了"（实机 21:31 已发生）。把玩家填过的 Key 缓存到本机
        //   BepInEx 配置里（仅本机、不进日志、不随模组包发布），重进游戏时自动沿用。
        internal static ConfigEntry<string> CfgApiKeyCache;
        internal static ConfigEntry<bool> CfgTestOnStartup;
        internal static ConfigEntry<int> CfgTimeout;
        internal static ConfigEntry<bool> CfgTestBridge;

        // 对话引擎相关（可在配置里调）
        internal static ConfigEntry<bool> CfgEnableChat;
        internal static ConfigEntry<float> CfgGlobalCooldown;
        internal static ConfigEntry<float> CfgBotCooldown;
        internal static ConfigEntry<int> CfgMaxQueue;
        internal static ConfigEntry<int> CfgMaxTokens;
        internal static ConfigEntry<int> CfgMaxLineChars;   // BUG-103：单条台词最大字符数
        internal static ConfigEntry<float> CfgMaxDistance;
        internal static ConfigEntry<string> CfgExportDir;

        private static Script _lastScript;
        private static bool _channelTested;
        private static bool _typesRegistered;
        private static bool _globalsInjected;
        private static bool _channelTestPending;
        private static float _injectedAt;
        private float _elapsed;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            CfgApiUrl = Config.Bind("AI", "ApiUrl", "https://api.deepseek.com/v1",
                "兼容 OpenAI 的接口地址，通常以 /v1 结尾。例：https://api.deepseek.com/v1");
            CfgModel = Config.Bind("AI", "Model", "deepseek-chat",
                "模型名称。例：deepseek-chat / gpt-4o-mini / qwen2.5:7b");
            CfgApiKeyCache = Config.Bind("AI", "ApiKeyCache", "",
                "【仅本机】自动同步你在模组设置界面里填过的 API Key（重启游戏后沿用）。文件位置就是本文件 BepInEx\\config\\rf.aichat.bridge.cfg。"
                + "清空本项 = 回到「未填写 Key」状态（会显示未填提示）。不会写日志明文、不会随模组包发布。");
            CfgTimeout = Config.Bind("AI", "TimeoutSeconds", 30, "单次请求超时（秒）");
            CfgTestOnStartup = Config.Bind("Debug", "TestOnStartup", true,
                "启动时自动发一次 AI 接口请求，结果写日志（验证用）");
            CfgTestBridge = Config.Bind("Debug", "TestBridge", true,
                "启动后自动自测一次「Lua → C#」通道调用（验证用）");
            CfgDebugLang = Config.Bind("Debug", "WindowLanguage", "zh",
                "调试窗口的界面语言：zh=中文 / en=English（窗口右上角按钮也能切换，切换后会保存到这里）");

            CfgPromptGlobal = Config.Bind("Prompts", "Global", "",
                "全局提示词（覆盖内置的全局规则；留空=用内置默认）");
            CfgPromptEagle = Config.Bind("Prompts", "FactionEagle", "",
                "鹰队（蓝方）阵营提示词（留空=不加）");
            CfgPromptRaven = Config.Bind("Prompts", "FactionRaven", "",
                "鸦队（红方）阵营提示词（留空=不加）");
            CfgPromptRookie = Config.Bind("Prompts", "SkillRookie", "",
                "角色提示词·新兵（留空=用内置默认）");
            CfgPromptNormal = Config.Bind("Prompts", "SkillNormal", "",
                "角色提示词·老兵（留空=用内置默认）");
            CfgPromptVeteran = Config.Bind("Prompts", "SkillVeteran", "",
                "角色提示词·精锐（留空=用内置默认）");
            CfgPromptAce = Config.Bind("Prompts", "SkillAce", "",
                "角色提示词·王牌（留空=用内置默认）");

            CfgPromptMatch = Config.Bind("Prompts", "Match", "",
                "对局提示词（对全体角色的系统提示词追加；留空=不加）");
            CfgNameColorBlue = Config.Bind("UI", "NameColorBlue", "Blue", "鹰队（蓝方）名称颜色");
            CfgNameColorRed = Config.Bind("UI", "NameColorRed", "Red", "鸦队（红方）名称颜色");
            CfgNameColorPlayer = Config.Bind("UI", "NameColorPlayer", "Rainbow", "玩家名称颜色（含彩虹 Rainbow）");
            CfgSpectatorTitle = Config.Bind("Prompts", "SpectatorTitle", "战地记者",
                "玩家处于观察者模式时的称呼（默认：战地记者）");
            CfgSystemReplyChance = Config.Bind("Chat", "SystemReplyChance", 10,
                "系统播报被 AI 回应的概率（%，默认 10；连续 10 条没回应会强制回应一次）");
            CfgSystemReplyPerTeam = Config.Bind("Chat", "SystemReplyPerTeam", 5,
                "每条系统播报：每队【至少】派几名 AI 回应（默认 5；人数不足时按实际人数）");
            CfgAllowDeadAI = Config.Bind("Chat", "AllowDeadAI", true,
                "是否允许死亡/未部署的 AI 发言（默认 true；false 时阵亡者不再说话）");
            CfgVerboseLog = Config.Bind("Log", "Verbose", false,
                "详细日志：每条 AI 台词的原始响应/送出记录/时序都会写日志。默认 false（减少日志 I/O 造成的卡顿）；排查问题时再开");
            CfgAutoReportSeconds = Config.Bind("Chat", "AutoReportSeconds", 180,
                "自动战报间隔（秒，默认 180；可在 MOD 设置里改）");
            CfgActiveSpeakerLimitBlue = Config.Bind("Chat", "ActiveSpeakerLimitBlue", 30,
                "蓝队（鹰队）主动发言名额：0-128，默认 30；名单外 AI 只会在击杀玩家/被玩家击杀时发言");
            CfgActiveSpeakerLimitRed = Config.Bind("Chat", "ActiveSpeakerLimitRed", 30,
                "红队（鸦队）主动发言名额：0-128，默认 30；名单外 AI 只会在击杀玩家/被玩家击杀时发言");

            CfgPlayerTitle = Config.Bind("Prompts", "PlayerTitle", "指挥官",
                "玩家称呼（我方角色对玩家的称呼；玩家默认是我方战地最高指挥官）");

            // ★ 第85轮：清空历史遗留的 Key 缓存（Key 只认本次模组设置里填写的）
            try
            {
                if (CfgApiKeyCache != null && !string.IsNullOrEmpty(CfgApiKeyCache.Value))
                {
                    CfgApiKeyCache.Value = "";
                    Config.Save();
                    Log.LogInfo("[AiChat] 已清空本机 API Key 缓存（现在只认模组设置里填写的 Key）");
                }
            }
            catch (Exception) { }

            // ★ 第61轮：旧默认值迁移（"战地最高指挥官" → "指挥官"）
            try
            {
                if ((CfgPlayerTitle.Value ?? "") == "战地最高指挥官")
                {
                    CfgPlayerTitle.Value = "指挥官";
                    Config.Save();
                }
            }
            catch (Exception) { }

            CfgPersonaRatio1 = Config.Bind("Persona", "Ratio1", 25, "人设分配：王牌名次比例上限（%），默认 25");
            CfgPersonaRatio2 = Config.Bind("Persona", "Ratio2", 50, "人设分配：精锐名次比例上限（%），默认 50");
            CfgPersonaRatio3 = Config.Bind("Persona", "Ratio3", 75, "人设分配：老兵名次比例上限（%），默认 75");
            CfgPersonaFirstAssign = Config.Bind("Persona", "FirstAssignSeconds", 60, "开局第几秒做第一次人设分配，默认 60");
            CfgPersonaReassignEvery = Config.Bind("Persona", "ReassignSeconds", 300, "之后每隔多少秒重新分配一次，默认 300");

            CfgEnableChat = Config.Bind("Chat", "EnableChat", true,
                "总开关：是否让士兵真的说话（关掉只会记录事件，不调用 API）");
            CfgGlobalCooldown = Config.Bind("Chat", "GlobalCooldownSeconds", 4f,
                "全局节流：两条台词之间至少间隔多少秒（防刷屏、防限流）");
            CfgBotCooldown = Config.Bind("Chat", "BotCooldownSeconds", 30f,
                "同一名士兵两次发言的最小间隔（秒）");
            CfgMaxQueue = Config.Bind("Chat", "MaxQueue", 80,
                "待生成队列上限，超出则丢弃新事件");
            CfgMaxTokens = Config.Bind("Chat", "MaxTokens", 1024,
                "单条回复的最大 token 数（越小越快越省）");
            CfgMaxLineChars = Config.Bind("Chat", "MaxLineChars", 160,
                "单条台词最大字符数（超过会尽量按句末标点截断；默认 160，建议 80-200）");
            CfgMaxDistance = Config.Bind("Chat", "MaxDistanceMeters", 80f,
                "只对玩家这个距离内的阵亡做出反应（友军误伤不受此限；设很大则全图都反应）");
            CfgExportDir = Config.Bind("Log", "ExportDir",
                System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "AIChatLogs"),
                "聊天记录导出目录。可填绝对路径（例：D:\\RF聊天记录）；默认 BepInEx\\AIChatLogs");

            Log.LogInfo("==================================================");
            Log.LogInfo("[AiChat] 插件已加载 v" + VERSION + "  配置文件：" + Config.ConfigFilePath);


            Log.LogInfo("[AiChat] ApiUrl=" + CfgApiUrl.Value + " | Model=" + CfgModel.Value
                + " | ApiKey=" + (string.IsNullOrEmpty(EffApiKey) ? "(空)" : "(已填写, 长度 " + EffApiKey.Length + ")"));
            Log.LogInfo("[AiChat] 游戏版本：Unity " + Application.unityVersion + " | BepInEx " + BepInEx.Paths.BepInExRootPath);

            InstallHarmonyPatches();

            if (CfgTestOnStartup.Value)
            {
                Thread t = new Thread(new ThreadStart(SelfTestApi));
                t.IsBackground = true;
                t.Start();
            }
            Log.LogInfo("==================================================");
        }

        // ✅ 第 28 轮：把玩家在模组设置里填的 Key 缓存到本机配置（仅本机；日志只记长度，不记内容）
        internal static void CacheApiKey(string key)
        {
            try
            {
                if (string.IsNullOrEmpty(key)) return;
                key = key.Trim();
                if (key.Length == 0 || CfgApiKeyCache == null) return;
                if (CfgApiKeyCache.Value == key) return;
                CfgApiKeyCache.Value = key;
                if (Instance != null) Instance.Config.Save();
                Log.LogInfo("[AiChat] 已把 API Key 同步到本机配置（长度 " + key.Length
                    + "，位置：" + (Instance != null ? Instance.Config.ConfigFilePath : "BepInEx\\config")
                    + "；清空 ApiKeyCache 可回到未填写状态）");
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- 通道注入

        private void InstallHarmonyPatches()
        {
            try
            {
                Harmony h = new Harmony(GUID);

                // 1) 注册类型（必须在 Script 实例化之前完成）
                Type registar = AccessTools.TypeByName("Lua.Proxy.Registrar");
                if (registar == null)
                {
                    Log.LogError("[AiChat][通道] 未找到类型 Lua.Proxy.Registrar —— 游戏版本可能已变更，注入失败");
                    return;
                }

                MethodInfo mRegister = AccessTools.Method(registar, "RegisterTypes");
                MethodInfo mExpose = AccessTools.Method(registar, "ExposeTypes");
                Log.LogInfo("[AiChat][通道] 找到 Registrar.RegisterTypes=" + (mRegister != null)
                    + " / Registrar.ExposeTypes=" + (mExpose != null));

                if (mRegister != null)
                {
                    h.Patch(mRegister,
                        prefix: new HarmonyMethod(typeof(Patches).GetMethod("RegisterTypes_Prefix", BindingFlags.Static | BindingFlags.NonPublic)));
                }
                if (mExpose != null)
                {
                    h.Patch(mExpose,
                        prefix: new HarmonyMethod(typeof(Patches).GetMethod("ExposeTypes_Prefix", BindingFlags.Static | BindingFlags.NonPublic)));
                }
                // 2) BUG-104：聊天输入期间屏蔽游戏全局热键（M 地图 / Tab 记分板 / Esc 菜单等）
                try
                {
                    Type steelInput = AccessTools.TypeByName("SteelInput");
                    if (steelInput != null)
                    {
                        MethodInfo pBtn = typeof(Patches).GetMethod("SteelInput_Button_Prefix",
                            BindingFlags.Static | BindingFlags.NonPublic);
                        int patchedBtn = 0;
                        string[] btnNames = new string[] { "GetButton", "GetButtonDown", "GetButtonUp" };
                        for (int bi = 0; bi < btnNames.Length; bi++)
                        {
                            MethodInfo bm = AccessTools.Method(steelInput, btnNames[bi]);
                            if (bm != null && pBtn != null)
                            {
                                h.Patch(bm, prefix: new HarmonyMethod(pBtn));
                                patchedBtn++;
                            }
                        }
                        Log.LogInfo("[AiChat][输入] 打字期间热键屏蔽补丁已安装：" + patchedBtn + " 个方法");
                    }
                    else
                    {
                        Log.LogWarning("[AiChat][输入] 未找到 SteelInput，打字热键屏蔽补丁跳过");
                    }
                }
                catch (Exception e)
                {
                    Log.LogWarning("[AiChat][输入] 安装热键屏蔽补丁失败：" + e.GetType().Name + " : " + e.Message);
                }

                Log.LogInfo("[AiChat][通道] Harmony 补丁安装完成，等待游戏创建脚本引擎…");
            }
            catch (Exception e)
            {
                Log.LogError("[AiChat][通道] 安装补丁失败：" + e.GetType().Name + " : " + e.Message);
            }
        }

        internal static class Patches
        {
            // 对应 Lua.Proxy.Registrar.RegisterTypes()
            internal static bool RegisterTypes_Prefix()
            {
                try
                {
                    if (!_typesRegistered)
                    {
                        UserData.RegisterType(typeof(AiChatBridgeApi), InteropAccessMode.Default, null);
                        _typesRegistered = true;
                        Log.LogInfo("[AiChat][通道] 已向 MoonSharp 注册类型 AiChatBridge");
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("[AiChat][通道] 注册类型失败：" + e.GetType().Name + " : " + e.Message);
                }
                return true; // 继续执行游戏原本的注册
            }

            // BUG-104：聊天输入期间让游戏"看不到"按键，避免打字触发地图/记分板/菜单等
            internal static bool SteelInput_Button_Prefix(ref bool __result)
            {
                try
                {
                    if (AiChatBridgeApi.IsInputActive())
                    {
                        __result = false;
                        return false;   // 跳过游戏原方法
                    }
                }
                catch (Exception) { }
                return true;
            }

            // 对应 Lua.Proxy.Registrar.ExposeTypes(Script script)
            internal static bool ExposeTypes_Prefix(Script script)
            {
                try
                {
                    if (!_typesRegistered)
                    {
                        UserData.RegisterType(typeof(AiChatBridgeApi), InteropAccessMode.Default, null);
                        _typesRegistered = true;
                        Log.LogInfo("[AiChat][通道] 已向 MoonSharp 注册类型 AiChatBridge（迟注册）");
                    }
                    if (script != null && script.Globals != null)
                    {
                        script.Globals["AiChatBridge"] = typeof(AiChatBridgeApi);
                        _globalsInjected = true;
                        _lastScript = script;
                        _channelTestPending = true;   // 由 Update() 在主线程延时自测（不能在这里直接调 Lua）
                        _injectedAt = Time.time;
                        Log.LogInfo("[AiChat][通道] 已注入全局变量 AiChatBridge（脚本侧可用 AiChatBridge.Push / Poll / Status）");
                    }
                    else
                    {
                        Log.LogWarning("[AiChat][通道] ExposeTypes 收到空的 Script/Globals，无法注入");
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("[AiChat][通道] 注入全局变量失败：" + e.GetType().Name + " : " + e.Message);
                }
                return true;
            }
        }

        // 主线程延时自测：在 Lua 里真的调用一次我们的接口
        // 触发条件改为「注入完成后 2 秒」，而不是固定 6 秒——上一版因为注入发生在 6 秒之后，自测被跳过
        private float _lastStutterLog = -999f;
        private static string SafeSceneName()
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch (Exception) { return "?"; }
        }
        private long _lastTickMs, _lastUiMs;

        private void Update()
        {
            _elapsed += Time.deltaTime;
            long __t0 = DateTime.Now.Ticks;
            try { SettingsProbe.Tick(); try { OverlayChat.Tick(); } catch (Exception) { } } catch (Exception e2) { Log.LogWarning("[AIChat][面板] Tick 外层异常：" + e2.GetType().Name + " " + e2.Message + " @ " + e2.StackTrace); }   // ★ 驱动"设置界面连接测试面板"
            try { DebugWindow.Tick(); } catch (Exception) { }   // ★ 第37轮：调试窗口（关闭时零开销）
            try { PromptWindow.Tick(); } catch (Exception) { }   // ★ 第46轮：提示词设置窗口 + 设置页入口按钮
            try { PersonaWindow.Tick(); } catch (Exception) { }  // ★ 第50轮：人设分配设置窗口
            try { KBWindow.Tick(); } catch (Exception) { }       // ★ 第56轮：常识库编辑窗口
            try { NameColorRows.Tick(); } catch (Exception) { }  // ★ 第67轮：设置页"名称颜色"三行
            _lastUiMs = (DateTime.Now.Ticks - __t0) / 10000L;    // ★ 第88轮：修好 UI 耗时统计（之前漏赋值）

            // 对话引擎：按节流把已生成的台词交给脚本显示（每帧调用，开销极小）
            // ★ 第76轮：卡顿看门狗——单帧明显超时时记录当时队列状态，方便定位
            try
            {
                float dt = Time.unscaledDeltaTime;
                if (dt > 0.2f && Time.unscaledTime - _lastStutterLog > 5f)
                {
                    _lastStutterLog = Time.unscaledTime;
                    Log.LogWarning("[AIChat][卡顿] 单帧 " + (int)(dt * 1000f) + "ms｜队列=" + ChatEngine.DebugQueueCount
                        + "｜API调用=" + ChatEngine.DebugApiCalls + "｜UI=" + _lastUiMs + "ms｜引擎=" + _lastTickMs
                        + "ms｜场景=" + SafeSceneName()
                        + "｜内存=" + (GC.GetTotalMemory(false) / (1024 * 1024)) + "MB"
                        + "｜最近重活=" + ChatEngine.LastHeavy);
                }
            }
            catch (Exception) { }
            long __t1 = DateTime.Now.Ticks;
            try { ChatEngine.Tick();
                // 官方 UI 支持：采集点击 + 维持 InputField 焦点 + 采集回车/取消（v1.7）
                AiChatBridgeApi.TickFocus(); }
            catch (Exception e) { Log.LogError("[AiChat][引擎] Tick 异常：" + e.Message); }
            _lastTickMs = (DateTime.Now.Ticks - __t1) / 10000L;   // ★ 第81轮：引擎 Tick 耗时

            if (_channelTested || !CfgTestBridge.Value) return;

            if (_channelTestPending && _lastScript != null && (Time.time - _injectedAt) >= 2f)
            {
                _channelTestPending = false;
                _channelTested = true;
                try
                {
                    _lastScript.DoString("AiChatBridge.Push('t=selftest%7Cfrom=plugin%7Cnote=self%20test')");
                    Log.LogInfo("[AiChat][通道] ✅ Lua → C# 调用成功：已在 Lua 内执行 AiChatBridge.Push(...)");
                    try
                    {
                        DynValue dv = _lastScript.DoString("return AiChatBridge.Status()");
                        Log.LogInfo("[AiChat][通道] ✅ C# → Lua 返回值：" + (dv == null ? "(null)" : dv.ToPrintString()));
                    }
                    catch (Exception e2)
                    {
                        Log.LogWarning("[AiChat][通道] Status() 调用异常：" + e2.Message);
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("[AiChat][通道] ❌ Lua → C# 调用失败：" + e.GetType().Name + " : " + e.Message);
                }
                return;
            }

            if (!_globalsInjected && _elapsed > 25f)
            {
                _channelTested = true;
                Log.LogWarning("[AiChat][通道] 25 秒内未见脚本引擎初始化（没进对局属正常）。若已进对局仍无注入日志，说明注入未生效");
            }
        }

        // ---------------------------------------------------------------- M1 接口自检

        // ---------------------------------------------------------------- 统一的 API 调用
        // 返回助手正文；失败返回 null 并把原因写入 err。
        internal static string CallApi(string systemPrompt, string userPrompt, out string err)
        {
            err = null;
            LastPromptTokens = -1;
            LastCompletionTokens = -1;
            string baseUrl = EffApiUrl.Trim();
            // 智能拼接（统一走 BuildChatEndpointUrl）
            string url = BuildChatEndpointUrl(baseUrl);
            StringBuilder body = new StringBuilder();
            body.Append("{\"model\":\"").Append(Json.Escape(EffModel)).Append("\",\"messages\":[");
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                body.Append("{\"role\":\"system\",\"content\":\"").Append(Json.Escape(systemPrompt)).Append("\"},");
            }
            body.Append("{\"role\":\"user\",\"content\":\"").Append(Json.Escape(userPrompt)).Append("\"}],");
            body.Append("\"max_tokens\":").Append(CfgMaxTokens.Value).Append(",\"stream\":false}");

            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Accept = "application/json";
                req.Timeout = CfgTimeout.Value * 1000;
                req.ReadWriteTimeout = CfgTimeout.Value * 1000;
                req.UserAgent = "RF-AiChat/" + VERSION;
                if (!string.IsNullOrEmpty(EffApiKey))
                {
                    req.Headers["Authorization"] = "Bearer " + EffApiKey.Trim();
                }

                byte[] data = Encoding.UTF8.GetBytes(body.ToString());
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string text = sr.ReadToEnd();
                    ParseUsage(text);   // ★ 第37轮：调试窗口显示 token 用量（接口返回 usage 时才有）
                    return ExtractContent(text, out err);
                }
            }
            catch (WebException we)
            {
                string detail = "";
                if (we.Response != null)
                {
                    try
                    {
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8)) detail = sr.ReadToEnd();
                    }
                    catch (Exception) { }
                }
                err = we.Status + " " + we.Message + " " + Clip(detail, 200);
                return null;
            }
            catch (Exception e)
            {
                err = e.GetType().Name + " : " + e.Message;
                return null;
            }
        }

        // ★ 第37轮：从响应 JSON 里解析 usage（没有就保持 -1）
        private static void ParseUsage(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            LastPromptTokens = FindIntAfter(json, "\"prompt_tokens\"");
            LastCompletionTokens = FindIntAfter(json, "\"completion_tokens\"");
            if (LastPromptTokens < 0)
            {
                int total = FindIntAfter(json, "\"total_tokens\"");
                if (total >= 0) LastPromptTokens = total;
            }
        }

        private static int FindIntAfter(string json, string key)
        {
            try
            {
                int i = json.IndexOf(key, StringComparison.Ordinal);
                if (i < 0) return -1;
                int colon = json.IndexOf(':', i + key.Length);
                if (colon < 0) return -1;
                int p = colon + 1;
                while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
                int sign = 1;
                if (p < json.Length && json[p] == '-') { sign = -1; p++; }
                int start = p;
                while (p < json.Length && json[p] >= '0' && json[p] <= '9') p++;
                if (p <= start) return -1;
                int v = 0;
                if (!int.TryParse(json.Substring(start, p - start), out v)) return -1;
                return sign * v;
            }
            catch (Exception) { return -1; }
        }

        // 从 OpenAI 兼容响应里取出 choices[0].message.content
        internal static string ExtractContent(string json, out string err)
        {
            err = null;
            if (string.IsNullOrEmpty(json)) { err = "空响应"; return null; }
            int i = json.IndexOf("\"content\"");
            if (i < 0) { err = "响应里没有 content 字段：" + Clip(json, 200); return null; }
            int colon = json.IndexOf(':', i);
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) { err = "content 字段格式异常"; return null; }

            StringBuilder sb = new StringBuilder();
            for (int p = q1 + 1; p < json.Length; p++)
            {
                char c = json[p];
                if (c == '\\')
                {
                    if (p + 1 >= json.Length) break;
                    char n = json[p + 1];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 'u')
                    {
                        if (p + 5 < json.Length)
                        {
                            int code;
                            if (int.TryParse(json.Substring(p + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                            {
                                sb.Append((char)code);
                                p += 4;
                            }
                        }
                    }
                    else sb.Append(n);
                    p++;
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            string result = sb.ToString().Trim();
            if (result.Length == 0) { err = "模型返回了空内容"; return null; }
            return result;
        }

        // ★ 第 11 轮：引擎与测试共用的唯一 URL 拼接实现（禁止两处各写一份）
        internal static string BuildChatEndpointUrl(string rawUrl)
        {
            string url = (rawUrl ?? "").Trim().TrimEnd('/');
            // ★ 第88轮：没写协议头时自动补 https://（否则 WebRequest.Create 会抛 UriFormatException）
            if (url.Length > 0 && !url.StartsWith("http://") && !url.StartsWith("https://"))
                url = "https://" + url.TrimStart('/');
            if (!url.EndsWith("/chat/completions")) url = url + "/chat/completions";
            return url;
        }

        public static void SelfTestApi()
        {
            Log.LogInfo("[AiChat][自检] 开始 —— 目的：确认游戏进程内能否用 HTTPS 访问 AI 接口");
            try
            {
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // Tls12
            }
            catch (Exception e)
            {
                Log.LogWarning("[AiChat][自检] 设置 TLS 失败（不致命）：" + e.Message);
            }

            // ★ GLM 第 7 轮：测试的 Key/Model 加回落（只影响测试，不影响 AI 正式请求链的工坊安全设计）
            //   来源优先级：MOD 设置（Lua 已同步） > 插件配置文件
            // ★ 第 11 轮：只认"设置页直读值"，不回落任何缓存/旧 Key
            string testKey = SettingsProbe.TestKeyOverride ?? "";
            string keySource = "设置页当前输入";
            string testModel = !string.IsNullOrEmpty(SettingsProbe.TestModelOverride) ? SettingsProbe.TestModelOverride
                : (!string.IsNullOrEmpty(EffModel) ? EffModel : (CfgModel != null ? CfgModel.Value : "deepseek-chat"));
            Log.LogInfo("[AiChat][自检] Key 来源=" + keySource + " ｜ Key 长度=" + (testKey == null ? 0 : testKey.Trim().Length) + " ｜ 模型=" + testModel);
            if (string.IsNullOrEmpty(testKey))
            {
                Log.LogWarning("[AiChat][自检] ApiKey 为空 —— 仍会发请求：若拿到 HTTP 401，说明网络与 TLS 是通的（预期结果）");
            }

            // ★ 第 11 轮：来源链与 Key 链对称（直读 > MOD 设置已同步 > 配置文件默认），且走统一拼接
            string rawUrl = SettingsProbe.TestUrlOverride ?? "";
            string url = BuildChatEndpointUrl(rawUrl);
            string body = "{\"model\":\"" + Json.Escape(testModel)
                + "\",\"messages\":[{\"role\":\"user\",\"content\":\"请只回复两个字：连通\"}],\"max_tokens\":" + CfgMaxTokens.Value + ",\"stream\":false}";
            DateTime t0 = DateTime.Now;
            long ms = 0;

            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Accept = "application/json";
                req.Timeout = CfgTimeout.Value * 1000;
                req.ReadWriteTimeout = CfgTimeout.Value * 1000;
                req.UserAgent = "RF-AiChat/" + VERSION;
                if (!string.IsNullOrEmpty(testKey))
                {
                    req.Headers["Authorization"] = "Bearer " + testKey.Trim();
                }

                byte[] data = Encoding.UTF8.GetBytes(body);
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    ms = (long)(DateTime.Now - t0).TotalMilliseconds;
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string text = sr.ReadToEnd();
                        Log.LogInfo("[AiChat][自检] ✅ 成功 HTTP " + (int)resp.StatusCode + "  用时 " + ms + "ms");
                        SettingsProbe.Busy = false;
                        SettingsProbe.LastResult = "✅ 连接成功（HTTP " + (int)resp.StatusCode + "，用时 " + ms + "ms，模型 " + testModel + "，Key 来源：" + keySource + "）";
                        AiChatBridgeApi.Push("系统	p runtime blue	系统	连接测试成功（模型 " + EffModel + "）	");
                        Log.LogInfo("[AiChat][自检] 返回内容：" + Clip(text, 700));
                    }
                }
            }
            catch (WebException we)
            {
                ms = (long)(DateTime.Now - t0).TotalMilliseconds;
                if (we.Response != null)
                {
                    int status = 0;
                    string detail = "";
                    try
                    {
                        HttpWebResponse er = (HttpWebResponse)we.Response;
                        status = (int)er.StatusCode;
                        using (StreamReader sr = new StreamReader(er.GetResponseStream(), Encoding.UTF8)) detail = sr.ReadToEnd();
                    }
                    catch (Exception) { }

                    if (status == 401 || status == 403)
                    {
                        Log.LogWarning("[AiChat][自检] ✅ 收到 HTTP " + status + " —— 网络/TLS/地址都通，只是 Key 未填或不对。用时 " + ms + "ms");
                        // ★ GLM 第 6 轮：401 出口也必须收尾，否则面板永远停在"测试中…"
                        SettingsProbe.Busy = false;
                        SettingsProbe.LastResult = status == 401 || status == 403
                            ? "❌ 连接失败：Key 被拒绝（HTTP " + status + "）——请核对密钥是否正确"
                            : (status == 404 ? "❌ 连接失败：接口地址不存在（HTTP 404）——请核对 API 接口"
                                             : "❌ 连接失败：接口返回 HTTP " + status + "——" + we.Message);
                    }
                    else
                    {
                        Log.LogError("[AiChat][自检] ❌ HTTP " + status + " " + we.Message + " 用时 " + ms + "ms");
                        SettingsProbe.Busy = false;
                        SettingsProbe.LastResult = "❌ 连接失败：接口返回 HTTP " + status + "——" + we.Message;
                        AiChatBridgeApi.Push("系统	p runtime blue	系统	连接测试失败：" + we.Message + "	");
                    }
                    if (detail.Length > 0) Log.LogError("[AiChat][自检] 返回内容：" + Clip(detail, 600));
                }
                else
                {
                    Log.LogError("[AiChat][自检] ❌ 请求失败（无响应）：" + we.Status + " / " + we.Message + " 用时 " + ms + "ms");
                    Exception inner = we.InnerException;
                    while (inner != null)
                    {
                        Log.LogError("[AiChat][自检]   内层异常：" + inner.GetType().Name + " : " + inner.Message);
                        inner = inner.InnerException;
                    }
                }
            }
            catch (Exception e)
            {
                ms = (long)(DateTime.Now - t0).TotalMilliseconds;
                Log.LogError("[AiChat][自检] ❌ 未预期异常：" + e.GetType().Name + " : " + e.Message);
            }
            Log.LogInfo("[AiChat][自检] 结束（本行出现即表示自检流程完整跑完）");
        }

        private static string Clip(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…(截断)";
        }

        internal static class Json
        {
            internal static string Escape(string s)
            {
                if (s == null) return "";
                StringBuilder sb = new StringBuilder(s.Length + 8);
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
                return sb.ToString();
            }
        }
    }
}
