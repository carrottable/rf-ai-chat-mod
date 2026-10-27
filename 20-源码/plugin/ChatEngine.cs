// ChatEngine —— 对话引擎（插件侧大脑）
// 流程：脚本事件 → 组装提示词 → 后台线程顺序调 API → 校验/强制 @ → 主线程按节流交给脚本显示
// 编译约束：C# 5（不使用插值 / ?. / 表达式体成员）
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;

namespace RF.AiChat
{
    internal class BotInfo
    {
        internal string Name = "?";
        internal string Team = "Blue";
        internal string Skill = "Beginner";
        internal int Kills;
        internal int Deaths;
        internal float LastSay = -9999f;
        internal bool IsPlayer;      // 玩家本体：不能替他发言，也不能 @ 他要求回复
        internal bool IsAlive = true;   // ★ 第70轮：存活状态（复活/阵亡时更新，死了不再安排台词）
        internal float NextSysReplyAt;  // ★ 第77轮：下次可被选中回应系统播报的时刻
    }

    internal class LineRequest
    {
        internal string Speaker;
        internal string Pose;
        internal string Style;
        internal string AtTarget;
        internal string SystemPrompt;
        internal string UserPrompt;
        internal int Priority;
        internal int Depth;
        internal string Reason = "?";   // 触发原因（用于调试窗口）
        internal string Skill = "?";    // 说话人当时的档位（用于调试窗口）
        internal bool PlayerInvolved;   // 与玩家有关（击杀玩家/被玩家击杀/回应玩家）→ 优先 + 加粗
        internal bool SystemReply;      // ★ 第76轮：系统播报回应 → 优先插队（不加粗）
        internal long CreatedTicks;     // ★ 第57轮：请求创建时刻（测量端到端延迟）
    }

    internal class ReadyLine
    {
        internal string Speaker;
        internal string Pose;
        internal string Style;
        internal string Text;
        internal string AtTarget;
        internal int Depth;          // @ 链深度（0=主动发言；越大越靠后）
        internal string Reason = "?";
        internal string Skill = "?";
        internal long Ms;            // 生成耗时（毫秒）
        internal long QueueWaitMs;   // ★ 第57轮：排队等待（毫秒）
        internal long CreatedTicks;  // ★ 第57轮：请求创建时刻
        internal bool SystemReply;   // ★ 第76轮：系统播报回应
        internal bool PlayerInvolved;
    }

    // ★ 第37轮：调试窗口的一条记录（AI 请求 或 游戏事件）
    internal class DebugRecord
    {
        internal DateTime Time;
        internal string Kind = "";          // 事件类型：击杀 / 闲聊 / 战报 / 阵亡 / 据点…
        internal string Actor = "";         // 角色（说话人 / 事件主角）
        internal string Brief = "";         // 简述
        internal int Ms = -1;               // 耗时（-1 = 非请求）
        internal string Status = "";        // 成功 / 失败 / 超时 / 本地 / 事件
        internal string SystemPrompt = "";
        internal string GlobalPrompt = "";   // 全局提示词（规则/语言约束，不含人设）
        internal string CharacterPrompt = ""; // 角色提示词（名字+人设+本次风格）
        internal string UserPrompt = "";    // 记录时送到模型的用户提示词（不含语言后缀）
        internal string Response = "";      // 模型原始回复
        internal string Error = "";
        internal string Meta = "";          // 附加信息（事件原始字段 / 请求元数据）
        internal bool IsEvent;
        internal int PromptTokens = -1;
        internal int CompletionTokens = -1;
        internal string Model = "";
    }

    internal static class ChatEngine
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, BotInfo> Roster = new Dictionary<string, BotInfo>();
        private static readonly Queue<LineRequest> Requests = new Queue<LineRequest>();
        private static readonly List<ReadyLine> Ready = new List<ReadyLine>();   // ★ 第55轮：改 List 以支持玩家相关插队
        private static readonly List<string> History = new List<string>();

        private static Thread _worker;
        private static volatile bool _running;
        private static float _lastGlobalSay = -9999f;
        private static int _apiCalls;
        private static int _fails;
        private static int _linesOut;
        private static int _dropped;

        // 当前这条台词是"为什么"产生的（事件处理函数在 Enqueue 之前设置，用于调试窗口）
        private static string _pendingReason = "?";
        // 当前这条台词是否与玩家有关（优先输出 + 加粗显示）
        internal static string ApiUrlOverride = "", ApiKeyOverride = "", ModelOverride = "";

        private static bool _pendingPlayerInvolved = false;
        private static bool _pendingSystemReply = false;   // ★ 第76轮
        // ★ 第73轮：系统播报/开局/胜负
        private static int _sysMsgsSinceReply;
        // ★ 第82轮：发言限制器
        private static int _rateCount;
        private static float _rateStart = -999f;
        private const int MAX_LINES_PER_SEC = 4;      // 普通台词每秒最多 4 条（插队的不限）
        private const int MAX_PENDING_NORMAL = 60;    // 普通台词管线最多 60 条（插队的不计入）
        private static float _openingAt = -999f;
        private static bool _openingDone;
        private static bool _matchEnded;
        private static bool _warned401 = false;   // 第28轮：本局是否已提示过 401（Key 无效），避免刷屏

        // ★ v1.1.2：主动发言名额（蓝/红独立自定义）与性能统计
        private static readonly Dictionary<string, HashSet<string>> _activeSpeakerNames
            = new Dictionary<string, HashSet<string>>();
        private static bool _activeRosterInit;
        private static bool _allowSilentSpeakers;   // 本次事件是"击杀玩家/被玩家击杀"→ 允许名单外发言
        private static int _quotaDropped;
        private static float _lastQuotaLog = -999f;

        // 由游戏内设置决定（脚本通过 cfg 事件同步过来）
        internal static string LangCode = "zh";   // zh / en
        internal static int FreqLevel = 1;        // 0=少 1=正常 2=多

        // ============================================================ 对外接口

        /// <summary>脚本 → 插件：一条事件（行协议）。</summary>
        internal static void OnEvent(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            Dictionary<string, string> f = Parse(line);
            string t;
            if (!f.TryGetValue("t", out t)) return;
            // 复位：这两个是跨事件的静态标记，不复位会把上一条"与玩家有关"的状态带过来
            // （实测隐患：玩家被击杀后的下一条据点/闲聊台词会被误判成"优先 + 高亮"）
            _pendingPlayerInvolved = false;
            _pendingReason = "?";
            // ★ v1.1.2：玩家击杀/被击杀事件允许名单外 AI 发言（其余事件严格按名额）
            _allowSilentSpeakers = IsTrue(Val(f, "pkiller")) || IsTrue(Val(f, "pvictim"));
            try { DebugAddEvent(t, f); } catch (Exception) { }   // ★ 第37轮：记录到调试窗口的事件流
            try
            {
                if (t == "persona") OnPersona(f);
                else if (t == "spawn") OnSpawn(f);
                else if (t == "death") OnDeath(f);
                else if (t == "cap") OnCapture(f);
                else if (t == "radio") OnRadio(f);
                else if (t == "order") OnOrder(f);
                else if (t == "vehicle") OnVehicle(f);
                else if (t == "tick") OnTick(f);
                else if (t == "match") OnMatch(f);
                else if (t == "selftest") AiChatPlugin.Log.LogInfo("[AiChat][引擎] 收到自检事件，插件↔脚本链路正常");
                else if (t == "cfg") OnCfg(f);
                else if (t == "cfgq") SendCfgToLua();   // ★ 第50轮：Lua 请求人设分配设置
                else if (t == "export") OnExport(f);
                else if (t == "say") OnPlayerSay(f);
                else if (t == "multi") OnMultiKill(f);
                else if (t == "ime") OnIme(f);
                // ★ 第58轮：事件处理完统一复位（即使 handler 提前 return，也不会把标记带进下一次 Tick）
                _pendingPlayerInvolved = false;
                _pendingReason = "?";
                _allowSilentSpeakers = false;
            }
            catch (Exception e)
            {
                AiChatPlugin.Log.LogError("[AiChat][引擎] 事件处理异常：" + e.GetType().Name + " : " + e.Message);
            }
        }

        // 主线程调用：按全局节流放行一条已生成的台词
        internal static void Tick()
        {
            if (!AiChatPlugin.CfgEnableChat.Value) return;
            // ★ v1.1.2：发言名额/性能统计（每 10 秒一条，别每帧刷日志）
            if (_activeRosterInit && UnityEngine.Time.unscaledTime - _lastQuotaLog >= 10f)
            {
                _lastQuotaLog = UnityEngine.Time.unscaledTime;
                int qb = QuotaForTeam("Blue"), qr = QuotaForTeam("Red");
                int sb = ActiveSet("Blue").Count, sr = ActiveSet("Red").Count;
                lock (Sync)
                {
                    AiChatPlugin.Log.LogInfo("[发言名额] 蓝=" + qb + "/已选" + sb
                        + " 红=" + qr + "/已选" + sr
                        + " 队列=" + Requests.Count + " 待显示=" + Ready.Count
                        + " 已出=" + _linesOut + " 名额外丢弃=" + _quotaDropped);
                }
            }
            // ★ 第73轮：开局播报（每局一次，等 AI 生成后）
            if (!_openingDone && _openingAt > 0f && UnityEngine.Time.unscaledTime >= _openingAt)
            {
                if (Roster.Count > 0) BroadcastOpening();
            }
            ProcessSysBatches();   // ★ 第77轮：分批补系统播报回应
            ReadyLine r = null;
            lock (Sync)
            {
                if (Ready.Count == 0) return;
                float now = UnityEngine.Time.time;
                // ★ 第55/76轮：玩家相关 > 系统播报回应 > 普通台词
                int pick = -1;
                for (int i = 0; i < Ready.Count; i++)
                {
                    if (Ready[i] != null && Ready[i].PlayerInvolved) { pick = i; break; }
                }
                if (pick < 0)
                {
                    for (int i = 0; i < Ready.Count; i++)
                    {
                        if (Ready[i] != null && Ready[i].SystemReply) { pick = i; break; }
                    }
                }
                if (pick >= 0)
                {
                    // 玩家相关 / 系统播报回应：几乎立即发（0.15 秒最小间隔，防同一事件连刷）
                    if (now - _lastGlobalSay < 0.15f) return;
                }
                else
                {
                    // 普通台词：按说话频率节流
                    float mult = (FreqLevel <= 0) ? 2f : (FreqLevel >= 2 ? 0.5f : 1f);
                    float cd = AiChatPlugin.CfgGlobalCooldown.Value * mult;
                    if (now - _lastGlobalSay < cd) return;
                    pick = 0;
                }
                ReadyLine cand = Ready[pick];
                bool priorityLine = (cand != null) && (cand.PlayerInvolved || cand.SystemReply);
                if (!priorityLine)
                {
                    // ★ 第82轮：普通台词每秒最多 4 条；超限就等下一帧（不丢，只是延后）
                    if (now - _rateStart > 1f) { _rateStart = now; _rateCount = 0; }
                    if (_rateCount >= MAX_LINES_PER_SEC) return;
                    _rateCount++;
                }
                r = cand;
                Ready.RemoveAt(pick);
                _lastGlobalSay = now;
                _linesOut++;
            }
            string text = r.Text;
            if (!string.IsNullOrEmpty(r.AtTarget)) text = "@" + r.AtTarget + " " + text;
            // 调试信息（第 4 段）：为什么说、对谁说、第几跳、什么档位、耗时
            // ★ 第61轮：只有"@ 到玩家本人"的台词才加粗；系统播报/普通台词一律不加粗
            BotInfo pb = PlayerBot();
            bool bold = pb != null && !string.IsNullOrEmpty(r.AtTarget) && r.AtTarget.Trim() == pb.Name;
            string meta = "reason=" + r.Reason
                + "|target=" + (string.IsNullOrEmpty(r.AtTarget) ? "(无)" : r.AtTarget)
                + "|depth=" + r.Depth
                + "|skill=" + r.Skill
                + "|ms=" + r.Ms
                + "|api=" + AiChatPlugin.CfgModel.Value
                + (r.PlayerInvolved ? "|playerInvolved=true" : "")
                + (bold ? "|bold=true" : "");
            meta = meta.Replace("\t", " ").Replace("\n", " ");
            // 协议顺序：名字 \t 姿态 \t 风格 \t 正文 \t 元信息
            // 正文放倒数第二段、元信息放最后：万一段里混入制表符，也只会影响调试显示，不会切坏正文
            string rawReply = r.Speaker + "\t" + r.Pose + "\t" + r.Style + "\t" + text + "\t" + meta;
            if (AiChatPlugin.CfgTestBridge.Value)
                AiChatPlugin.Log.LogInfo("[AiChat][引擎] 原始回复行：" + rawReply.Replace("\t", "¦"));
            AiChatBridgeApi.EnqueueReply(rawReply);
            AiChatPlugin.Log.LogInfo("[AiChat][引擎] 送出第 " + _linesOut + " 条 → " + r.Speaker + "（" + r.Style + "）：" + text);
            if (r.SystemReply)
            {
                long e2eR = (DateTime.Now.Ticks - r.CreatedTicks) / 10000L;
                if (AiChatPlugin.CfgVerboseLog != null && AiChatPlugin.CfgVerboseLog.Value) AiChatPlugin.Log.LogInfo("[AIChat][时序] 系统回应上屏：" + r.Speaker + " 端到端=" + e2eR + "ms");
            }
            if (r.PlayerInvolved)
            {
                long e2e = (DateTime.Now.Ticks - r.CreatedTicks) / 10000L;
                AiChatPlugin.Log.LogInfo("[AIChat][时序] 玩家台词端到端=" + e2e + "ms（排队=" + r.QueueWaitMs
                    + "ms 生成=" + r.Ms + "ms 显示等待=" + (e2e - r.QueueWaitMs - r.Ms) + "ms）");
            }
            lock (Sync)
            {
                History.Add(r.Speaker + "：" + text);
                if (History.Count > 20) History.RemoveAt(0);
                // ★ 第81轮：导出历史（干净文本，含与调试窗口一致的元信息）
                CleanHistory.Add(r.Speaker + "：" + text + "    【reason=" + r.Reason
                    + "|target=" + (string.IsNullOrEmpty(r.AtTarget) ? "(无)" : r.AtTarget)
                    + "|depth=" + r.Depth + "|skill=" + r.Skill
                    + "|ms=" + r.Ms + "|api=" + AiChatPlugin.CfgModel.Value + "】");
                if (CleanHistory.Count > 400) CleanHistory.RemoveAt(0);
            }

            // 需求 7：被 @ 的必须回复 —— 由代码保证，不依赖模型
            // 深链最多 2 跳（A@B → B回@A → A再回一次后停止），避免无限接龙刷屏
            if (r.Depth < 2 && !string.IsNullOrEmpty(r.AtTarget))
            {
                BotInfo target = Find(r.AtTarget);
                if (target != null && !target.IsPlayer && target.Name != r.Speaker)
                {
                    _pendingReason = "被 " + r.Speaker + " @ 点名，必须回复";
                    EnqueueAt(target, r.Style == "系统" ? "吐槽" : r.Style, r.Speaker, 1,
                        BuildSystem(target, r.Style == "系统" ? "吐槽" : r.Style),
                        "事件：" + r.Speaker + " 刚才 @ 了你并说：「" + r.Text + "」。请立刻回他一句（要提到他）。",
                        r.Depth + 1);
                }
            }
        }

        internal static string Status()
        {
            lock (Sync)
            {
                return "bots=" + Roster.Count + ";req=" + Requests.Count + ";ready=" + Ready.Count
                    + ";calls=" + _apiCalls + ";fail=" + _fails + ";lines=" + _linesOut + ";drop=" + _dropped
                    + ";qb=" + QuotaForTeam("Blue") + ";qr=" + QuotaForTeam("Red")
                    + ";sb=" + ActiveSet("Blue").Count + ";sr=" + ActiveSet("Red").Count
                    + ";qdrop=" + _quotaDropped;
            }
        }

        // ============================================================ 事件 → 请求

        // 游戏内设置（语言 / 频率 / 开关）——由脚本在开局时同步过来
        private static void OnCfg(Dictionary<string, string> f)
        {
            try { OverlayChat.ClearForNewMatch(); } catch (Exception) { }   // ★ 第16轮②：每局开始清空显示层
            try { SquadTag.ResetForNewMatch(); } catch (Exception) { }       // v1.1.3：每局重新缓存分队编号
            // ★ 第73轮：安排开局播报（等几秒让 AI 生成出来）
            _openingAt = UnityEngine.Time.unscaledTime + 6f;
            _openingDone = false;
            _matchEnded = false;
            _sysMsgsSinceReply = 0;
            _sysBatches.Clear();       // ★ 第88轮：清掉上一局遗留的播报回应批次
            CleanHistory.Clear();      // ★ 第88轮：导出历史按局清空（避免跨局混杂）
            // ★ v1.1.2：每局重新抽主动发言名单；设置改动下一局生效
            _activeSpeakerNames.Clear();
            _activeRosterInit = false;
            _allowSilentSpeakers = false;
            _quotaDropped = 0;
            _lastQuotaLog = -999f;
            AiChatPlugin.Log.LogInfo("[发言名额] 本局配额：蓝=" + QuotaForTeam("Blue")
                + " 红=" + QuotaForTeam("Red") + "（0-128，名单外仅玩家击杀/被杀例外）");
            string urlOv = Val(f, "apiUrl");
            if (urlOv.Length > 0) { ApiUrlOverride = urlOv; AiChatPlugin.Log.LogInfo("[AiChat][引擎] 使用模组设置里的 API 地址"); }
            string keyOv = Val(f, "apiKey");
            if (keyOv.Length > 0)
            {
                ApiKeyOverride = keyOv;
                // ★ 第85轮：不再缓存 Key（只有本次设置里填的才生效）
                // 只记长度，不记尾号/内容（避免任何形式的 Key 泄漏）
                AiChatPlugin.Log.LogInfo("[AiChat][引擎] 收到模组设置里的 API Key（长度 " + keyOv.Length + "）");
            }
            // ★ 第85轮：Key 为空就按"未填写"处理，不再读取本地缓存
            if (string.IsNullOrEmpty(ApiKeyOverride))
            {
                AiChatPlugin.Log.LogWarning("[AiChat][引擎] 没有可用的 API Key —— 本局不会产生任何台词（请在模组设置里填写）");
                try
                {
                    OverlayChat.AddLine(Loc.Sys, Loc.T(
                        "⚠ 未填写 API Key：士兵无法说话。请在 MOD 设置里填入 API 密钥后重进对局。",
                        "⚠ No API key: soldiers cannot speak. Please fill in the API key in the mod settings and re-enter the match."));
                }
                catch (Exception) { }
            }
            _warned401 = false;
            string modOv = Val(f, "model");
            if (modOv.Length > 0) { ModelOverride = modOv; AiChatPlugin.Log.LogInfo("[AiChat][引擎] 使用模组设置里的模型：" + modOv); }
            string lang = Val(f, "lang");
            if (lang.Length > 0) LangCode = lang;
            MarkPlayer(Val(f, "player"));
            string freq = Val(f, "freq");
            int lvl;
            if (int.TryParse(freq, out lvl)) FreqLevel = lvl;
            AiChatPlugin.Log.LogInfo("[AiChat][引擎] 收到游戏内设置：语言=" + LangCode
                + "（" + (LangCode == "en" ? "English" : "中文") + "） 频率档=" + FreqLevel);
        }

        // ★ 第50轮：把人设分配设置推给 Lua（对局中保存设置也能立即生效）
        internal static void SendCfgToLua()
        {
            try
            {
                int r1 = AiChatPlugin.CfgPersonaRatio1 == null ? 25 : AiChatPlugin.CfgPersonaRatio1.Value;
                int r2 = AiChatPlugin.CfgPersonaRatio2 == null ? 50 : AiChatPlugin.CfgPersonaRatio2.Value;
                int r3 = AiChatPlugin.CfgPersonaRatio3 == null ? 75 : AiChatPlugin.CfgPersonaRatio3.Value;
                int first = AiChatPlugin.CfgPersonaFirstAssign == null ? 60 : AiChatPlugin.CfgPersonaFirstAssign.Value;
                int every = AiChatPlugin.CfgPersonaReassignEvery == null ? 300 : AiChatPlugin.CfgPersonaReassignEvery.Value;
                int reportSec = AiChatPlugin.CfgAutoReportSeconds == null ? 180 : AiChatPlugin.CfgAutoReportSeconds.Value;
                string payload = "CFG|r1=" + r1 + ";r2=" + r2 + ";r3=" + r3
                               + ";first=" + first + ";every=" + every + ";report=" + reportSec + ";overlay=1";
                AiChatBridgeApi.EnqueueRaw("系统	p runtime blue	系统	" + payload + "	");
                if (AiChatPlugin.Log != null)
                    AiChatPlugin.Log.LogInfo("[AIChat][人设] 已推送分配设置 → Lua：" + payload);
            }
            catch (Exception) { }
        }

        // 聊天记录导出：脚本分批送来，集齐后写文件（脚本自己没有写文件权限）
        private static readonly StringBuilder ExportBuffer = new StringBuilder();
        // ★ 第81轮：插件侧"干净文本"历史，导出直接用它（Lua 侧字符串存在中文被损坏的问题）
        private static readonly List<string> CleanHistory = new List<string>();
        private static int _exportTotal = 1;
        private static int _exportParts;
        private static string _exportReason = "";

        private static void OnExport(Dictionary<string, string> f)
        {
            int part = IntOf(f, "part", 1);
            int total = IntOf(f, "total", 1);
            string lines = Val(f, "lines");

            if (part <= 1)
            {
                ExportBuffer.Length = 0;
                _exportParts = 0;
                _exportTotal = total;
                _exportReason = Val(f, "reason");
            }
            // ★ 第81轮：忽略 Lua 传来的 lines（其中文可能被 Lua 字符串处理损坏），改用插件侧干净历史
            if (part <= 1)
            {
                ExportBuffer.Length = 0;
                for (int i = 0; i < CleanHistory.Count; i++) ExportBuffer.AppendLine(CleanHistory[i]);
                if (CleanHistory.Count == 0 && lines.Length > 0) ExportBuffer.AppendLine(lines);   // 兜底
            }
            _exportParts++;
            if (part >= total || _exportParts >= _exportTotal) WriteExportFile();
        }

        private static void WriteExportFile()
        {
            try
            {
                string dir = AiChatPlugin.CfgExportDir.Value;
                System.IO.Directory.CreateDirectory(dir);
                string fileName = "AIChat_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                string path = System.IO.Path.Combine(dir, fileName);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("===== RF AI 对话记录 =====");
                sb.AppendLine("导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "（触发：" + _exportReason + "）");
                try
                {
                    sb.AppendLine("场景：" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
                }
                catch (Exception) { }
                sb.AppendLine("接口：" + AiChatPlugin.CfgApiUrl.Value + "　模型：" + AiChatPlugin.CfgModel.Value);
                sb.AppendLine("语言：" + LangCode + "　频率档：" + FreqLevel);
                sb.AppendLine(new string('-', 40));
                sb.AppendLine("【聊天记录】");
                sb.Append(ExportBuffer.ToString());
                sb.AppendLine();
                sb.AppendLine("【本局自建 KD 榜】");
                sb.AppendLine(ScoreboardText());
                sb.AppendLine();
                sb.AppendLine("【操作提示】按 Y 调出鼠标与输入框；滚轮翻历史；点聊天框底部按钮：调试/战报/导出/隐藏/上翻/下翻");

                System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                AiChatPlugin.Log.LogInfo("[AiChat][导出] ✅ 聊天记录已导出：" + path);
                ReadyLine rl = new ReadyLine();
                rl.Speaker = Loc.Sys;
                rl.Pose = "p runtime blue";
                rl.Style = "系统";
                rl.Text = Loc.T("聊天记录已导出到 " + fileName, "Chat log exported to " + fileName);
                lock (Sync) { Ready.Add(rl); }
            }
            catch (Exception e)
            {
                AiChatPlugin.Log.LogError("[AiChat][导出] ❌ 写文件失败：" + e.GetType().Name + " : " + e.Message);
            }
        }

        // 供导出文件使用
        internal static string ScoreboardText()
        {
            return Scoreboard();
        }

        // 玩家在游戏内聊天框发言（需求 3）：让 1~2 名士兵回应，且必须 @ 玩家（需求 7）
        private static void OnPlayerSay(Dictionary<string, string> f)
        {
            try { OverlayChat.AddLinePlayer(Val(f, "from"), Val(f, "text")); } catch (Exception) { }   // ★ 第72轮：显示玩家自己的昵称（彩虹加粗）
            string from = Val(f, "from");
            if (from.Length == 0) from = "玩家";
            string text = Val(f, "text");
            if (text.Length == 0) return;
            MarkPlayer(from);   // 玩家名字记下来，避免以后替他发言

            AiChatPlugin.Log.LogInfo("[AiChat][引擎] 玩家发言（" + from + "）：" + text);
            lock (Sync)
            {
                History.Add(from + "：" + text);
                if (History.Count > 20) History.RemoveAt(0);
            }

            List<BotInfo> pool = AllBots();
            if (pool.Count == 0) return;

            // ★ 第59轮：玩家 @ / 点名的人必回（名字出现在玩家消息里就算点名）
            int named = 0;
            for (int i = 0; i < pool.Count && named < 3; i++)
            {
                BotInfo nb = pool[i];
                if (nb == null || nb.IsPlayer) continue;
                if (string.IsNullOrEmpty(nb.Name)) continue;
                if (text.IndexOf(nb.Name, StringComparison.Ordinal) < 0) continue;
                _pendingReason = "玩家点名回应";
                _pendingPlayerInvolved = true;
                EnqueueAt(nb, "吐槽", from, 0,
                    BuildSystem(nb, "吐槽"),
                    "事件：玩家（" + from + "）在聊天框里点名了你：「" + text + "」。请正面回应他一句。",
                    0);
                named++;
            }
            AiChatPlugin.Log.LogInfo("[AIChat][时序] 玩家发言：点名必回 " + named + " 人");

            // ★ 第70轮：玩家发言时，己方和敌方都要有回应（己方 = 回应/配合，敌方 = 嘲讽）
            List<BotInfo> myPool = new List<BotInfo>();
            List<BotInfo> foePool = new List<BotInfo>();
            BotInfo pb0 = PlayerBot();
            string myTeam0 = pb0 != null ? pb0.Team : "";
            for (int i = 0; i < pool.Count; i++)
            {
                BotInfo q = pool[i];
                if (q == null || q.IsPlayer) continue;
                if (myTeam0.Length == 0) { myPool.Add(q); foePool.Add(q); }
                else if (q.Team == myTeam0) myPool.Add(q);
                else foePool.Add(q);
            }
            if (myPool.Count == 0) myPool = foePool;
            if (foePool.Count == 0) foePool = myPool;
            int n = UnityEngine.Random.Range(1, 3);
            for (int i = 0; i < n; i++)
            {
                bool pickFoe = (i == n - 1) && foePool.Count > 0 && UnityEngine.Random.value < 0.6f;
                List<BotInfo> src = pickFoe ? foePool : myPool;
                if (src.Count == 0) src = pool;
                BotInfo b = src[UnityEngine.Random.Range(0, src.Count)];
                if (b == null || b.IsPlayer) continue;
                if (pickFoe)
                {
                    _pendingReason = "敌方回应玩家发言";
                    _pendingPlayerInvolved = true;
                    EnqueueAt(b, "粗口", from, 0,
                        BuildSystem(b, "粗口"),
                        "事件：敌方玩家（" + from + "）在聊天框里说：「" + text + "」。你是他的敌人，请嘲讽/挑衅他一句，别夸他。",
                        0);
                    continue;
                }
                // 已经由"点名必回"回过的人不再重复
                if (text.IndexOf(b.Name, StringComparison.Ordinal) >= 0) continue;
                _pendingReason = "玩家发言回应";
                _pendingPlayerInvolved = true;   // 回应玩家：优先 + 加粗
                EnqueueAt(b, "吐槽", from, 0,
                    BuildSystem(b, "吐槽"),
                    "事件：玩家（" + from + "）在聊天框里说：「" + text + "」。请像队友开麦那样回应他一句。",
                    0);
            }
        }

        // 输入法（中文输入）：Ravenscript 不暴露 Input.imeCompositionMode，
        // 所以由插件这边直接开关 Unity 的输入法处理 —— 不开的话游戏内输入法打不出中文。
        private static void OnIme(Dictionary<string, string> f)
        {

            bool on = IsTrue(Val(f, "on"));
            try
            {
                UnityEngine.Input.imeCompositionMode = on
                    ? UnityEngine.IMECompositionMode.On
                    : UnityEngine.IMECompositionMode.Off;
                AiChatPlugin.Log.LogInfo("[AiChat][引擎] 输入法：" + (on ? "已开启（可打中文）" : "已关闭"));
            }
            catch (Exception e)
            {
                AiChatPlugin.Log.LogWarning("[AiChat][引擎] 设置输入法失败：" + e.Message);
            }
        }

        // 需求 8：玩家一次性大量击杀（火炮/手雷炸死 5 人以上）→ 立刻、优先、加粗地反应
        private static void OnMultiKill(Dictionary<string, string> f)
        {
            string killer = Val(f, "killer");
            if (killer.Length == 0) killer = "玩家";
            int count = IntOf(f, "count", 5);
            string weapon = Val(f, "weapon");
            if (weapon.Length == 0) weapon = "不明火力";

            AiChatPlugin.Log.LogInfo("[AiChat][引擎] 玩家多杀事件：" + killer + " 一次干掉 " + count + " 人（" + weapon + "）");

            List<BotInfo> pool = AllBots();
            if (pool.Count == 0) return;
            int n = UnityEngine.Random.Range(1, 3);
            for (int i = 0; i < n; i++)
            {
                BotInfo b = pool[UnityEngine.Random.Range(0, pool.Count)];
                if (b.IsPlayer) continue;
                _pendingReason = "玩家多杀（一次 " + count + " 人，" + weapon + "）";
                _pendingPlayerInvolved = true;
                EnqueueAt(b, "吐槽", killer, 0,
                    BuildSystem(b, "吐槽"),
                    "事件：玩家（" + killer + "）用 " + weapon + " 一口气干掉了 " + count + " 个人。请用一句话反应（震惊/夸他/后怕都行）。",
                    0);
            }
        }

        private static void OnPersona(Dictionary<string, string> f)
        {
            BotInfo b = Get(Val(f, "name"));
            b.Team = Val(f, "team");
            b.Skill = Val(f, "skill");
        }

        private static void OnSpawn(Dictionary<string, string> f)
        {
            BotInfo b = Get(Val(f, "name"));
            b.Team = Val(f, "team");
            b.IsAlive = true;   // ★ 第70轮：出生/复活
            // 脚本会带上 bot=false 表示这是玩家本人
            string isBot = Val(f, "bot");
            if (isBot == "false") b.IsPlayer = true;
        }

        private static void OnDeath(Dictionary<string, string> f)
        {
            string victimName = Val(f, "victim");
            string killerName = Val(f, "killername");
            bool sameTeam = IsTrue(Val(f, "sameTeam"));
            bool pVictim = IsTrue(Val(f, "pvictim"));
            bool pKiller = IsTrue(Val(f, "pkiller"));
            // 玩家击杀/被击杀 → 必须"及时且有反馈"：优先输出 + 加粗（用户明确要求）
            _pendingPlayerInvolved = (pVictim || pKiller);
            // 事件里带了 bot 标记时，顺便把"玩家本人"记下来（绝不为他生成台词）
            if (Val(f, "vbot") == "false") MarkPlayer(Val(f, "victim"));
            if (Val(f, "kbot") == "false") MarkPlayer(Val(f, "killername"));
            string weapon = Val(f, "weapon");
            string dtype = Val(f, "dtype");
            bool headshot = IsTrue(Val(f, "headshot"));
            bool splash = IsTrue(Val(f, "splash"));
            TrackDeathBurst(pKiller, weapon);

            BotInfo vb = Get(victimName);
            vb.Team = Val(f, "vteam");
            vb.Deaths = IntOf(f, "vdeaths", vb.Deaths);

            BotInfo kb = null;
            if (killerName.Length > 0 && killerName != "?")
            {
                kb = Get(killerName);
                kb.Team = Val(f, "kteam");
                kb.Kills = IntOf(f, "kkills", kb.Kills);
                kb.IsAlive = true;
            }
            if (vb != null) vb.IsAlive = false;   // ★ 第70轮：阵亡标记

            // 距离过滤：只对玩家附近的阵亡做反应，否则 120 人全图会把聊天框刷爆
            // 友军误杀永远播报（玩家的队友被打，玩家通常最在意）
            int dist = IntOf(f, "dist", -1);
            string killVeh = Val(f, "killveh");
            // v1.1.3：被玩家击杀的 AI，在死亡事件当下读一次生前小队（只读一次，不做周期扫描）
            if (pKiller && vb != null && !vb.IsPlayer)
            {
                try { SquadTag.CacheAtDeath(victimName); } catch (Exception) { }
            }
            bool isTeamKill = sameTeam && kb != null;
            // ★ 第59轮：只要涉及玩家（pkiller/pvictim），一律无视 80 米距离过滤——
            //   否则玩家狙击远处的敌人时整条事件被丢弃（实测：打队友秒回、打敌人没反应）
            bool playerKillEvent = pKiller || pVictim;
            if (!isTeamKill && !playerKillEvent && dist >= 0 && dist > AiChatPlugin.CfgMaxDistance.Value)
            {
                return;
            }
            if (playerKillEvent)
            {
                AiChatPlugin.Log.LogInfo("[AIChat][时序] 玩家击杀/阵亡事件进入（dist=" + dist
                    + "，载具=" + (string.IsNullOrEmpty(killVeh) ? "(无)" : killVeh)
                    + "，无视距离过滤）：" + victimName + " ← " + killerName);
            }
            if (pKiller && !vb.IsPlayer)
            {
                AiChatPlugin.Log.LogInfo("[AIChat][击杀信息] 玩家击杀 AI：受害者=" + victimName
                    + " 距离=" + dist + " 米 载具=" + (string.IsNullOrEmpty(killVeh) ? "(无)" : killVeh));
            }

            string how = DescribeKill(dtype, weapon, headshot, splash);
            string style;
            string scene;
            if (sameTeam && kb != null)
            {
                style = "粗口";
                scene = "你被自己人 " + kb.Name + " 误杀了";
            }
            else if (UnityEngine.Random.value < 0.7f)
            {
                style = "吐槽";
                scene = "你被敌人 " + (kb != null ? kb.Name : "不明火力") + " 打死了";
            }
            else
            {
                style = "粗口";
                scene = "你被敌人 " + (kb != null ? kb.Name : "不明火力") + " 打死了";
            }

            string at = (kb != null && kb.Name != vb.Name) ? kb.Name : null;
            _pendingReason = isTeamKill ? "阵亡·被友军误杀" : ("阵亡·被敌人击杀（" + how + "）");
            _pendingPlayerInvolved = (pKiller || pVictim);
            // ★ v1.1.3：玩家击杀时，把击杀距离/玩家乘坐的载具告诉受害者 AI
            string playerKillInfo = "";
            if (pKiller && !vb.IsPlayer)
            {
                if (dist >= 0) playerKillInfo += "玩家当时距离你约 " + dist + " 米。";
                if (!string.IsNullOrEmpty(killVeh)) playerKillInfo += "玩家当时乘坐载具【" + killVeh + "】。";
            }
            // 需求 9：必须针对"击杀方式"作出反应（实测模型容易忽略，所以单独列出并强调）
            Enqueue(vb, style, at, 0,
                BuildSystem(vb, style),
                "事件：你被" + (sameTeam ? "自己人 " : "敌人 ") + (kb != null ? kb.Name : "不明火力") + "打死了。"
                + playerKillInfo
                + "击杀方式：【" + how + "】。你的战绩：" + vb.Kills + " 杀 " + vb.Deaths + " 死。"
                + "硬性要求：必须针对【" + how + "】这个击杀方式作出反应（被爆头就抱怨爆头、被炸就说被炸飞、"
                + "被车碾就骂开车的、摔死就自嘲），如果玩家乘坐了载具，要提到载具名，不要只说「我又死了」。",
                0);
            if (pKiller || pVictim)
                AiChatPlugin.Log.LogInfo("[AIChat][必回] 规则1/3 受害者回应入队：" + (vb != null ? vb.Name : "?")
                    + "（pvictim=" + pVictim + " pkiller=" + pKiller + "）");

            // ★ 第56轮：玩家击杀敌人 → 玩家队友也要有反应（1~2 人，插队 + 加粗）
            if (pKiller && !sameTeam && vb != null && !vb.IsPlayer)
            {
                string myTeam = (vb.Team == "Red") ? "Blue" : "Red";
                List<BotInfo> mates = new List<BotInfo>();
                foreach (BotInfo mb in AllBots())
                {
                    if (mb != null && !mb.IsPlayer && mb.Team == myTeam) mates.Add(mb);
                }
                // 优先挑"有空说话"的队友，避免因冷却静默丢弃
                List<BotInfo> readyMates = new List<BotInfo>();
                for (int i = 0; i < mates.Count; i++)
                {
                    BotInfo mb0 = mates[i];
                    if (mb0 != null && UnityEngine.Time.time - mb0.LastSay > 0.2f) readyMates.Add(mb0);
                }
                if (readyMates.Count > 0) mates = readyMates;
                int cnt = Math.Min(2, mates.Count);
                AiChatPlugin.Log.LogInfo("[AIChat][时序] 玩家击杀 → 队友反应入队 " + cnt + " 条");
                for (int i = 0; i < cnt; i++)
                {
                    BotInfo mb = mates[UnityEngine.Random.Range(0, mates.Count)];
                    if (mb == null) continue;
                    _pendingPlayerInvolved = true;
                    _pendingReason = "玩家击杀（队友反应）";
                    EnqueueAt(mb, "吐槽", vb.Name, 0,
                        BuildSystem(mb, "吐槽"),
                        "事件：玩家刚刚用 " + weapon + " 击杀了敌人 " + victimName + "（击杀方式：【" + how + "】）。"
                        + "请用一句话起哄/夸他/提醒继续抢点，口语化。",
                        0);
                }
            }

            // 需求 7：被 @ 的人必须回复 —— 由代码保证，不依赖模型
            if (kb != null && at != null && !sameTeam)
            {
                _pendingReason = "击杀通报（" + how + "）";
                _pendingPlayerInvolved = pVictim;   // 玩家被打死 → 凶手的通报要优先、加粗
                EnqueueAt(kb, "军事", victimName, 1,
                    BuildSystem(kb, "军事"),
                    "事件：你刚用 " + weapon + " 击杀了 " + victimName + "，击杀方式：【" + how + "】。"
                    + "请用军事简报口吻确认战果，并提到这个击杀方式（例如「爆头清除」「炸翻」）。",
                    1);
            }

            // 需求 7 补充：玩家被打死时，玩家本人不能发言（绝对规则），但**必须有别人回应**。
            // 凶手是 AI → 由凶手回应（敌方：嘲讽／友军：误伤反应）；凶手不是 AI（比如摔死）→ 随机队友回应。
            if (pVictim)
            {
                _pendingPlayerInvolved = true;
                if (kb != null && kb.Name != victimName)
                {
                    _pendingReason = sameTeam ? "玩家被友军误杀 → 凶手回应" : "玩家被击杀 → 凶手嘲讽";
                    AiChatPlugin.Log.LogInfo("[AIChat][必回] 规则" + (sameTeam ? "4（误杀玩家的队友）" : "2（击杀玩家的敌人）")
                        + " 回应入队：" + kb.Name);
                    EnqueueAt(kb, sameTeam ? "吐槽" : "粗口", victimName, 0,
                        BuildSystem(kb, sameTeam ? "吐槽" : "粗口"),
                        "事件：你" + (sameTeam ? "误杀了自己人 " : "击杀了敌人 ") + victimName
                        + "，击杀方式：【" + how + "】。请立刻对他说一句"
                        + (sameTeam ? "（误伤后的反应：道歉/辩解/打哈哈）" : "（嘲讽，要提到击杀方式）") + "。",
                        0);
                }
                else
                {
                    BotInfo mate = null;
                    List<BotInfo> fallbackPool = new List<BotInfo>();
                    List<BotInfo> fallbackAll = AllBots();
                    for (int fi = 0; fi < fallbackAll.Count; fi++)
                    {
                        BotInfo fb = fallbackAll[fi];
                        if (fb != null && !fb.IsPlayer && fb.Team == vb.Team) fallbackPool.Add(fb);
                    }
                    if (fallbackPool.Count > 0) mate = fallbackPool[UnityEngine.Random.Range(0, fallbackPool.Count)];
                    if (mate != null && !mate.IsPlayer)
                    {
                        _pendingReason = "玩家阵亡 → 队友反应";
                        EnqueueAt(mate, "吐槽", victimName, 0,
                            BuildSystem(mate, "吐槽"),
                            "事件：你的队友（玩家）" + victimName + " 刚才死了，方式：【" + how + "】。"
                            + "请说一句（可以损他、可以安慰、可以喊加油）。",
                            0);
                    }
                }

                // ★ 第59轮：玩家阵亡时，除凶手外必须还有一名玩家队友回应（保证"有人理"）
                {
                    BotInfo mate2 = null;
                    List<BotInfo> pool2 = AllBots();
                    List<BotInfo> mates2 = new List<BotInfo>();
                    for (int i = 0; i < pool2.Count; i++)
                    {
                        BotInfo m2 = pool2[i];
                        if (m2 != null && !m2.IsPlayer && m2.Team == vb.Team) mates2.Add(m2);   // BUG-102：必须同队（原为 !=，选到了敌人）
                    }
                    if (mates2.Count > 0) mate2 = mates2[UnityEngine.Random.Range(0, mates2.Count)];
                    if (mate2 != null && (!(kb != null) || mate2.Name != kb.Name))
                    {
                        _pendingPlayerInvolved = true;
                        _pendingReason = "玩家阵亡 → 队友安慰";
                        AiChatPlugin.Log.LogInfo("[AIChat][必回] 规则6 队友安慰回应入队：" + mate2.Name
                            + "（team=" + mate2.Team + "，playerTeam=" + vb.Team + "）");
                        EnqueueAt(mate2, "吐槽", victimName, 0,
                            BuildSystem(mate2, "吐槽"),
                            "事件：你的队友（玩家）" + victimName + " 刚才被 " + (kb != null ? kb.Name : "不明火力")
                            + " 击杀（方式：【" + how + "】）。请说一句安慰/鼓励/报仇的话。",
                            0);
                    }
                }
            }
        }

        private static void OnCapture(Dictionary<string, string> f)
        {
            _pendingReason = "据点事件";
            string point = Val(f, "point");
            string team = Val(f, "team");
            string ev = Val(f, "event");
            BotInfo b = RandomBotReady();
            if (b == null) return;
            string facts = (ev == "captured")
                ? ("据点 " + point + " 被 " + team + " 方占领")
                : ("据点 " + point + " 被中立化（原本属于 " + team + " 方）");
            Enqueue(b, "军事", RandomOtherName(b), 0,
                BuildSystem(b, "军事"),
                "事件：" + facts + "。你属于 " + b.Team + " 方。请用军事口吻通报一句"
                + (b.Team == team ? "（是你方的好消息）" : "（是坏消息，可以抱怨）") + "。", 0);
        }

        private static void OnRadio(Dictionary<string, string> f)
        {
            _pendingReason = "指挥部播报";
            string text = Val(f, "text");
            if (text.Length == 0) return;
            BotInfo b = RandomBotReady();
            if (b == null) return;
            Enqueue(b, "军事", null, 0,
                BuildSystem(b, "军事"),
                "指挥部无线电原文：" + text + "。请用一句话回应这道指令（可带一点自己的看法）。", 0);
        }

        private static void OnOrder(Dictionary<string, string> f)
        {
            _pendingReason = "小队命令";
            string order = Val(f, "order");
            // 收到命令是最高频事件，全部响应会刷屏且句式雷同（实测 60 秒里一半是"收到，…"）
            if (UnityEngine.Random.value > 0.45f) return;
            BotInfo b = RandomBotReady();
            if (b == null) return;
            Enqueue(b, "军事", null, 0,
                BuildSystem(b, "军事"),
                "事件：你们小队收到新命令（" + order + "）。请用一句话回应（可以确认、可以吐槽命令、可以补一句自己的打算）。", 0);
        }

        private static void OnVehicle(Dictionary<string, string> f)
        {
            _pendingReason = "载具被毁";
            string name = Val(f, "name");
            BotInfo b = RandomBotReady();
            if (b == null) return;
            Enqueue(b, "吐槽", RandomOtherName(b), 0,
                BuildSystem(b, "吐槽"),
                "事件：一辆载具（" + name + "）被摧毁了。请用一句话吐槽。", 0);
        }

        private static void OnMatch(Dictionary<string, string> f)
        {
            string winner = Val(f, "winner");
            // ★ 第73轮：对局结束播报（每局一次），播报后由系统消息回应机制安排 AI 收场
            if (!_matchEnded)
            {
                _matchEnded = true;
                List<string> lines = new List<string>();
                if (LangCode == "en")
                {
                    lines.Add("【Match over】Winner: " + winner);
                    lines.Add("【Result】GG - see you next round.");
                }
                else
                {
                    lines.Add("【对局结束】胜方：" + winner);
                    lines.Add("【结果】打完了，下局再来。");
                }
                BroadcastSystem(lines, "对局结果", false);   // ★ 第81轮：结算不再排 10 条回应（场景卸载期少线程）
                AiChatPlugin.Log.LogInfo("[AIChat][播报] 对局结束播报已发送（胜方=" + winner + "）");
            }
        }

        private static void OnTick(Dictionary<string, string> f)
        {
            string kind = Val(f, "kind");
            // ★ 第32轮：战报按钮 → 本地固定格式战报（不走 AI、无冷却、无 Key 也能用）
            if (kind == "report")
            {
                BuildReportNow();
                return;
            }

            BotInfo b = RandomBotReady();
            if (b == null) b = RandomBot();          // ★ GPT P5-1：退化到任意士兵
            if (b == null)
            {
                // ★ GPT P5-1：绝不静默丢弃请求
                _dropped++;
                AiChatPlugin.Log.LogWarning("[AiChat][引擎] 丢弃请求（kind=" + kind + "）：当前没有任何可用士兵，bots=" + Roster.Count);
                return;
            }

            BotInfo other = RandomOther(b);
            _pendingReason = "随机闲聊";
            Enqueue(b, "吐槽", other != null ? other.Name : null, 0,
                BuildSystem(b, "吐槽"),
                "事件：现在是战斗间隙，聊点与战局无关的。" + (other != null ? ("顺便 @" + other.Name + " 他。") : ""), 0);
        }

        // ★ 第33轮：战报按钮的 C# 兜底入口（Lua 的 tick=report 与 C# 点击可能同时到达，0.6 秒内去重）
        internal static void OnReportButton()
        {
            BuildReportNow();
        }

        private static float _lastReportAt = -999f;

        // ★ 第32轮：点「战报」→ 插件本地生成战报 → 立即作为系统消息插播（多语言跟 MOD 设置）
        // ★ 第78轮：场面多杀——统计"2.5 秒内连续死亡人数"，在距离过滤之前调用，爆炸/载具也能识别
        private static float _deathBurstT0 = -999f;
        private static int _deathBurstCount;
        private static int _deathBurstPlayerKills;
        private static bool _deathBurstFired;
        private static float _lastBurstReplyAt = -999f;   // ★ 第79轮：场面多杀回应冷却（避免刷屏）

        private static void TrackDeathBurst(bool playerKill, string how)
        {
            try
            {
                float now = UnityEngine.Time.time;
                if (now - _deathBurstT0 > 2.5f)
                {
                    _deathBurstT0 = now;
                    _deathBurstCount = 0;
                    _deathBurstPlayerKills = 0;
                    _deathBurstFired = false;
                }
                _deathBurstCount++;
                if (playerKill) _deathBurstPlayerKills++;
                if (_deathBurstFired) return;
                // 全是玩家击杀 → 由 Lua 的玩家多杀事件（≥5）负责；这里只处理"场上出现 AI 一次性多杀"
                bool allPlayer = _deathBurstPlayerKills == _deathBurstCount;
                if (_deathBurstCount >= 3 && !allPlayer && now - _lastBurstReplyAt > 8f)
                {
                    _deathBurstFired = true;
                    _lastBurstReplyAt = now;
                    AiChatPlugin.Log.LogInfo("[AIChat][多杀] 场面多杀：2.5 秒内死亡 " + _deathBurstCount + " 人（" + how + "），立刻回应");
                    LastHeavy = "场面多杀(" + _deathBurstCount + "人)";
                    BotInfo a = RandomOfTeam("Blue");
                    BotInfo b = RandomOfTeam("Red");
                    if (a != null)
                    {
                        _pendingReason = "场面多杀回应";
                        _pendingPlayerInvolved = false;
                        _pendingSystemReply = true;
                        EnqueueAt(a, "吐槽", null, 0, BuildSystem(a, "吐槽"),
                            "事件：战场上刚刚一波死了 " + _deathBurstCount + " 个人（" + how + "）。请用一句话反应（震惊/吐槽/提醒别扎堆）。", 0);
                    }
                    if (b != null)
                    {
                        _pendingReason = "场面多杀回应";
                        _pendingPlayerInvolved = false;
                        _pendingSystemReply = true;
                        EnqueueAt(b, "吐槽", null, 0, BuildSystem(b, "吐槽"),
                            "事件：战场上刚刚一波死了 " + _deathBurstCount + " 个人（" + how + "）。请用一句话反应（震惊/吐槽/提醒别扎堆）。", 0);
                    }
                }
            }
            catch (Exception) { }
        }

        // ★ 第76轮：AI 单位一次性死亡 ≥3（爆炸/载具等）→ 立刻引起回应；玩家多杀由 Lua 的 multi 事件负责（≥5）
        private static readonly Dictionary<string, float> _burstT0 = new Dictionary<string, float>();
        private static readonly Dictionary<string, int> _burstCnt = new Dictionary<string, int>();
        private static readonly HashSet<string> _burstFired = new HashSet<string>();

        private static void TrackBurstKill(BotInfo killer, string how)
        {
            try
            {
                if (killer == null || killer.IsPlayer) return;
                string key = killer.Name;
                float now = UnityEngine.Time.time;
                float t0;
                if (!_burstT0.TryGetValue(key, out t0) || now - t0 > 2.5f)
                {
                    _burstT0[key] = now;
                    _burstCnt[key] = 0;
                    _burstFired.Remove(key);
                }
                int c = 0;
                if (_burstCnt.TryGetValue(key, out c)) c++; else c = 1;
                _burstCnt[key] = c;
                if (c >= 3 && !_burstFired.Contains(key))
                {
                    _burstFired.Add(key);
                    AiChatPlugin.Log.LogInfo("[AIChat][多杀] 检测到 AI 一次性击杀 " + c + " 人（" + key + "，" + how + "），立刻回应");
                    // 杀手己方 1 人起哄 + 敌方 1 人震惊
                    BotInfo mate = RandomSameTeam(killer, true);
                    BotInfo foe = killer.Team == "Blue" ? RandomOfTeam("Red") : RandomOfTeam("Blue");
                    if (mate != null)
                    {
                        _pendingReason = "AI 多杀回应（己方）";
                        _pendingPlayerInvolved = false;
                        _pendingSystemReply = true;
                        EnqueueAt(mate, "吐槽", killer.Name, 0, BuildSystem(mate, "吐槽"),
                            "事件：你的队友 " + killer.Name + " 刚刚一次性干掉 " + c + " 个敌人（" + how + "）。请用一句话夸他/起哄。", 0);
                    }
                    if (foe != null)
                    {
                        _pendingReason = "AI 多杀回应（敌方）";
                        _pendingPlayerInvolved = false;
                        _pendingSystemReply = true;
                        EnqueueAt(foe, "粗口", killer.Name, 0, BuildSystem(foe, "粗口"),
                            "事件：敌方 " + killer.Name + " 刚刚一次性干掉 " + c + " 个你们的人（" + how + "）。请用一句话表示震惊/愤怒。", 0);
                    }
                }
            }
            catch (Exception) { }
        }

        private static BotInfo RandomSameTeam(BotInfo self, bool aliveFirst)
        {
            List<BotInfo> l = new List<BotInfo>();
            foreach (BotInfo b in AllBots()) if (b != null && !b.IsPlayer && b != self && b.Team == self.Team && b.IsAlive) l.Add(b);
            if (l.Count == 0) foreach (BotInfo b in AllBots()) if (b != null && !b.IsPlayer && b != self && b.Team == self.Team) l.Add(b);
            return l.Count > 0 ? l[UnityEngine.Random.Range(0, l.Count)] : null;
        }

        private static BotInfo RandomOfTeam(string team)
        {
            List<BotInfo> l = new List<BotInfo>();
            foreach (BotInfo b in AllBots()) if (b != null && !b.IsPlayer && b.Team == team && b.IsAlive) l.Add(b);
            if (l.Count == 0) foreach (BotInfo b in AllBots()) if (b != null && !b.IsPlayer && b.Team == team) l.Add(b);
            return l.Count > 0 ? l[UnityEngine.Random.Range(0, l.Count)] : null;
        }

        // ★ 第73轮：统一系统播报入口（推给 Lua + 显示层 + 调试记录；并按概率安排 AI 回应）
        private static void BroadcastSystem(List<string> lines, string kind, bool forceReply)
        {
            try
            {
                if (lines == null || lines.Count == 0) return;
                string sys = Loc.Sys;
                for (int i = 0; i < lines.Count; i++)
                {
                    try { AiChatBridgeApi.Push(sys + "\tp runtime blue\t系统\t" + lines[i]); } catch (Exception) { }
                }
                LastHeavy = "播报:" + kind + "(" + lines.Count + "行)";
                OverlayChat.AddReport(sys, lines);
                try { DebugAddLocal(kind, sys, lines[0], string.Join(" / ", lines.ToArray())); } catch (Exception) { }
                TriggerSystemReply(lines[0], forceReply);
            }
            catch (Exception e)
            {
                AiChatPlugin.Log.LogWarning("[AIChat][播报] 失败：" + e.Message);
            }
        }

        // ★ 第74轮：每条系统播报 → 双方各派最多 N 名 AI 回应（默认每队 5 名，人数不足按实际）
        private static void TriggerSystemReply(string sysText, bool force)
        {
            try
            {
                if (!force) return;   // ★ 第88轮：结算等场景不排回应
                int perTeam = 5;
                try { perTeam = AiChatPlugin.CfgSystemReplyPerTeam == null ? 5 : AiChatPlugin.CfgSystemReplyPerTeam.Value; } catch (Exception) { }
                if (perTeam < 1) perTeam = 1;
                if (perTeam > 20) perTeam = 20;
                // ★ 第79轮：分批补齐 —— 每批 2 名/队，直到凑满 perTeam
                for (int off = 0; off < perTeam; off += 2)
                {
                    int cnt = Math.Min(2, perTeam - off);
                    ScheduleSystemReplies(sysText, perTeam, off, cnt);
                }
                AiChatPlugin.Log.LogInfo("[AIChat][播报] 系统消息开始安排回应（每队至少 " + perTeam + " 名，每批 2 名）：" + sysText);
            }
            catch (Exception) { }
        }

        // ★ 第77轮：系统播报回应的分批调度（降低瞬时请求/渲染洪峰）
        private class SysReplyBatch
        {
            public float At;
            public string Text;
            public string Team;
            public int Offset;
            public int Count;
        }
        private static readonly List<SysReplyBatch> _sysBatches = new List<SysReplyBatch>();

        private static void ScheduleSystemReplies(string sysText, int perTeam, int offset, int count)
        {
            for (int teamIdx = 0; teamIdx < 2; teamIdx++)
            {
                string team = (teamIdx == 0) ? "Blue" : "Red";
                SysReplyBatch bt = new SysReplyBatch();
                bt.Text = sysText;
                bt.Team = team;
                bt.Offset = offset;
                bt.Count = count;
                bt.At = UnityEngine.Time.unscaledTime + (offset == 0 ? 0f : (offset / 2f) * 1.2f);
                _sysBatches.Add(bt);
                AiChatPlugin.Log.LogInfo("[AIChat][播报] 分批安排：" + team + " 第 " + offset + " 名起 / 本批 " + count + " 名");
            }
        }

        private static void ProcessSysBatches()
        {
            try
            {
                for (int i = _sysBatches.Count - 1; i >= 0; i--)
                {
                    SysReplyBatch bt = _sysBatches[i];
                    if (bt == null) { _sysBatches.RemoveAt(i); continue; }
                    if (UnityEngine.Time.unscaledTime < bt.At) continue;
                    _sysBatches.RemoveAt(i);
                    List<BotInfo> pool = AllBots();
                    List<BotInfo> cand = new List<BotInfo>();
                    for (int k = 0; k < pool.Count; k++)
                    {
                        BotInfo b = pool[k];
                        if (b == null || b.IsPlayer || b.Team != bt.Team) continue;
                        if (!b.IsAlive && !AllowDeadSpeak()) continue;
                        if (b.NextSysReplyAt > UnityEngine.Time.unscaledTime) continue;   // 不重复选同一人
                        cand.Add(b);
                    }
                    for (int k = 0; k < cand.Count && k < bt.Count; k++)
                    {
                        BotInfo b = cand[k];
                        b.LastSay = -999f;
                        b.NextSysReplyAt = UnityEngine.Time.unscaledTime + 20f;
                        _pendingReason = "系统播报回应";
                        _pendingPlayerInvolved = false;
                        _pendingSystemReply = true;
                        EnqueueAt(b, "吐槽", null, 0,
                            BuildSystem(b, "吐槽"),
                            "事件：系统播报「" + bt.Text + "」。请用一句话回应这条播报（吐槽/附和/分析/喊口号都行）。",
                            0);
                    }
                }
            }
            catch (Exception) { }
        }

        private static bool AllowDeadSpeak()
        {
            try { return AiChatPlugin.CfgAllowDeadAI == null || AiChatPlugin.CfgAllowDeadAI.Value; }
            catch (Exception) { return true; }
        }

        // ★ 第73轮：开局播报（每局一次）
        private static void BroadcastOpening()
        {
            try
            {
                _openingDone = true;
                string map = "?";
                try { map = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch (Exception) { }
                string mode = BattleReport.ModeKeyPublic;
                List<string> lines = new List<string>();
                if (LangCode == "en")
                {
                    lines.Add("【Match start】Mode: " + mode + " | Map: " + map);
                    lines.Add("【Objective】Capture and hold the points; watch your reinforcements.");
                }
                else
                {
                    lines.Add("【开局】模式：" + mode + " ｜ 地图：" + map);
                    lines.Add("【目标】抢占并守住据点，注意复活次数；开局也别乱送。");
                }
                BroadcastSystem(lines, "开局播报", true);
                AiChatPlugin.Log.LogInfo("[AIChat][播报] 开局播报已发送（模式=" + mode + "，地图=" + map + "）");
            }
            catch (Exception) { }
        }

        private static void BuildReportNow()
        {
            try
            {
                if (Time.unscaledTime - _lastReportAt < 0.6f) return;
                _lastReportAt = Time.unscaledTime;
                List<string> lines = BattleReport.Build();
                if (lines == null || lines.Count == 0) return;
                BroadcastSystem(lines, "战报", true);   // ★ 第88轮：战报必须有 AI 回应（结算除外）
                AiChatPlugin.Log.LogInfo("[AiChat][战报] 已本地生成 " + lines.Count + " 行（语言=" + LangCode
                    + "，模式=" + BattleReport.ModeKeyPublic + "）");
            }
            catch (Exception e)
            {
                AiChatPlugin.Log.LogWarning("[AiChat][战报] 生成失败：" + e.Message);
            }
        }

        // ============================================================ 提示词

        private static string PersonaDesc(string skill)
        {
            if (skill == "Elite") return "王牌老兵，战绩顶尖，说话狂、爱挑衅、爱总结战果";
            if (skill == "Veteran") return "精锐士兵，专业冷静，术语多，惜字如金";
            if (skill == "Normal") return "老兵，稳重务实，短句";
            return "新兵，紧张、话多、爱解释、爱喊救命";
        }

        private static string StyleHint(string style)
        {
            if (style == "军事") return "军事简报口吻，简短专业";
            if (style == "粗口") return "暴躁粗口，可以带脏字，但不要涉及现实群体与人身威胁";
            return "轻松吐槽，像玩家开麦互喷但不脏";
        }

        private static string BuildSystem(BotInfo b, string style)
        {
            // ★ 第46轮：全局提示词 / 阵营提示词 / 角色提示词 都可在 MOD 设置里自定义（留空用内置默认）
            StringBuilder sb = new StringBuilder();
            string global = BuildGlobalRules();
            if (global.Length > 0) sb.Append(global);
            string teamCtx = TeamContextPrompt(b);
            if (teamCtx.Length > 0) { if (sb.Length > 0) sb.Append('\n'); sb.Append(teamCtx); }
            string faction = FactionPrompt(b.Team);
            if (faction.Length > 0) { if (sb.Length > 0) sb.Append('\n'); sb.Append(faction); }
            string match = MatchPromptText();
            if (match.Length > 0) { if (sb.Length > 0) sb.Append('\n'); sb.Append(match); }
            string chara = BuildCharacterPrompt(b.Name, b.Skill, style);
            if (chara.Length > 0) { if (sb.Length > 0) sb.Append('\n'); sb.Append(chara); }
            // ★ 第71轮：让 AI 知道自己是否已阵亡/未部署
            string state = BotStatePrompt(b);
            if (state.Length > 0) { if (sb.Length > 0) sb.Append('\n'); sb.Append(state); }
            return sb.ToString();
        }

        private static string PersonaDescEn(string skill)
        {
            if (skill == "Elite") return "an ace veteran with top stats, cocky and boastful";
            if (skill == "Veteran") return "an elite soldier, professional, calm, uses jargon";
            if (skill == "Normal") return "a seasoned grunt, steady and practical";
            return "a rookie, nervous, chatty, always shouting for help";
        }

        private static string StyleHintEn(string style)
        {
            if (style == "军事") return "military radio brevity, short and professional";
            if (style == "粗口") return "angry trash talk, may swear, but never target real-world groups";
            return "light-hearted banter, like a player joking on voice chat";
        }

        /// <summary>某队 KD 前三（按 KD 排，KD 相同看击杀数）</summary>
        private static string TopThree(string team)
        {
            List<BotInfo> l = new List<BotInfo>();
            foreach (BotInfo b in AllBots())
            {
                if (b.Team != team) continue;
                if (b.Kills == 0 && b.Deaths == 0) continue;
                l.Add(b);
            }
            l.Sort(delegate (BotInfo x, BotInfo y)
            {
                float kx = x.Kills / (float)Math.Max(1, x.Deaths);
                float ky = y.Kills / (float)Math.Max(1, y.Deaths);
                if (kx == ky) return y.Kills.CompareTo(x.Kills);
                return ky.CompareTo(kx);
            });
            if (l.Count == 0) return "暂无数据";
            StringBuilder sb = new StringBuilder();
            int n = Math.Min(3, l.Count);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append("，");
                sb.Append(l[i].Name).Append(" ").Append(l[i].Kills).Append("杀").Append(l[i].Deaths).Append("死");
            }
            return sb.ToString();
        }

        /// <summary>需求：每 3 分钟播报战况，要含"双方 KD 前三"</summary>
        private static string Scoreboard()
        {
            return "红队前三：" + TopThree("Red") + "；蓝队前三：" + TopThree("Blue");
        }

        private static string DescribeKill(string dtype, string weapon, bool headshot, bool splash)
        {
            // 游戏里 tostring(info.type) 一般给名字（如 Projectile），但为稳妥也兼容数字形式：
            // 0 Unknown / 1 Projectile / 2 Melee / 3 Explosion / 4 StickyExplosive
            // 5 VehicleRam / 6 FallDamage / 7 DamageZone / 8 Exception / 9 Scripted / 10 ProjectileShatter
            if (dtype == "5") dtype = "VehicleRam";
            else if (dtype == "2") dtype = "Melee";
            else if (dtype == "6") dtype = "FallDamage";
            else if (dtype == "3" || dtype == "4") dtype = "Explosion";

            if (dtype == "VehicleRam") return "被载具碾死";
            if (dtype == "Melee") return "被近战武器（" + weapon + "）打死";
            if (dtype == "FallDamage") return "摔死了";
            if (dtype == "Explosion" || splash) return "被爆炸物（" + weapon + "）炸死";
            if (headshot) return "被 " + weapon + " 爆头";
            return "被 " + weapon + " 打死";
        }

        // ============================================================ 入队

        private static void Enqueue(BotInfo speaker, string style, string at, int priority, string sys, string usr, int depth)
        {
            EnqueueAt(speaker, style, at, priority, sys, usr, depth);
        }

        private static void EnqueueAt(BotInfo b, string style, string at, int priority, string sys, string usr, int depth)
        {
            // ★ 第58轮：一进 EnqueueAt 就消费"玩家相关"标记——
            //   之前冷却/无 bot 等早退会把标记留在静态字段里，被下一条 @ 回复继承 → 错误加粗+错误插队
            bool involved = _pendingPlayerInvolved;
            _pendingPlayerInvolved = false;
            bool sysReply = _pendingSystemReply;
            _pendingSystemReply = false;
            if (b == null) return;
            // ★ 第74轮：开关控制死亡/未部署 AI 是否发言（默认允许；关闭后阵亡遗言除外一律不安排）
            if (!b.IsAlive && !AllowDeadSpeak() && !IsDeathLine(_pendingReason)) return;
            if (b.IsPlayer) return;                 // 绝不替玩家发言
            if (depth > 3) return;                  // @ 链防死循环
            // ★ v1.1.2：非主动发言名单内 → 静默（玩家击杀/被击杀事件例外）
            if (!_allowSilentSpeakers && !IsActiveSpeaker(b))
            {
                _quotaDropped++;
                if (_quotaDropped == 1 || _quotaDropped % 50 == 0)
                    AiChatPlugin.Log.LogInfo("[发言名额] 静默丢弃 #" + _quotaDropped
                        + "：" + b.Name + "（team=" + b.Team + "）");
                return;
            }
            float now = UnityEngine.Time.time;
            float botCd = AiChatPlugin.CfgBotCooldown.Value;
            // 与玩家有关（击杀/被杀/回应玩家/多杀）：把 30 秒冷却放宽到 8 秒，
            // 否则"需求 7/8 要及时反应"会被冷却静默吞掉（静态复查发现的隐患）
            if (involved) botCd = Math.Min(botCd, 0.2f);   // ★ 第58轮：玩家相关几乎不冷却（保证"一定有人回"）
            if (now - b.LastSay < botCd) return;
            b.LastSay = now;

            // 需求 7：所有发言都必须带 @ —— 没有指定目标时自动挑一个别人
            if (string.IsNullOrEmpty(at))
            {
                BotInfo other = RandomOther(b);
                if (other != null && !other.IsPlayer) at = other.Name;
            }

            // ★ v1.1.3：只把当前事件需要的常识模块塞进系统提示词
            string kbSelected = KnowledgeBase.SelectFor(usr + "\n" + (_pendingReason ?? ""));
            if (!string.IsNullOrEmpty(kbSelected))
                sys = (sys == null ? "" : sys) + "\n\n【常识库·当前场景】\n" + kbSelected;

            LineRequest r = new LineRequest();
            r.Speaker = b.Name;
            r.Pose = (b.Team == "Red") ? "p runtime red" : "p runtime blue";
            r.Style = style;
            r.AtTarget = at;
            r.Priority = priority;
            r.Depth = depth;
            r.SystemPrompt = sys;
            r.UserPrompt = usr;
            r.Reason = _pendingReason;
            r.Skill = b.Skill;
            r.PlayerInvolved = involved;
            r.SystemReply = sysReply;
            r.CreatedTicks = DateTime.Now.Ticks;   // ★ 第57轮：延迟测量起点

            lock (Sync)
            {
                // ★ 第82轮：普通台词管线上限 60（插队的不计入、也不受限制）
                if (!involved && !sysReply)
                {
                    int pendingNormal = 0;
                    foreach (LineRequest q in Requests) if (q != null && !q.PlayerInvolved && !q.SystemReply) pendingNormal++;
                    for (int i = 0; i < Ready.Count; i++) if (Ready[i] != null && !Ready[i].PlayerInvolved && !Ready[i].SystemReply) pendingNormal++;
                    if (pendingNormal >= MAX_PENDING_NORMAL)
                    {
                        _dropped++;
                        AiChatPlugin.Log.LogInfo("[AIChat][限流] 普通台词管线已达 " + MAX_PENDING_NORMAL + " 条，丢弃一条（" + style + "/" + b.Name + "）");
                        return;
                    }
                }
                int cap = AiChatPlugin.CfgMaxQueue.Value;
                if (Requests.Count >= cap)
                {
                    // 队列满时的准入策略（实测：160 个 bot 时队列会被灌满，
                    // 连"回应玩家发言"都会被丢掉 —— 那条恰恰最不能丢）：
                    //   与玩家有关 / 主动发言（priority==0）进来时，先挤掉一条"@链式回复"（depth>0）
                    bool important = involved || priority == 0;
                    LineRequest victim = null;
                    if (important)
                    {
                        foreach (LineRequest q in Requests)
                        {
                            if (q.Depth > 0) { victim = q; break; }
                        }
                    }
                    if (victim == null)
                    {
                        _dropped++;
                        AiChatPlugin.Log.LogWarning("[AiChat][引擎] 请求队列已满，丢弃一条（"
                            + style + "/" + b.Name + "，重要=" + important + "）");
                        return;
                    }
                    Queue<LineRequest> keep = new Queue<LineRequest>();
                    bool removed = false;
                    while (Requests.Count > 0)
                    {
                        LineRequest q = Requests.Dequeue();
                        if (!removed && q == victim) { removed = true; continue; }
                        keep.Enqueue(q);
                    }
                    while (keep.Count > 0) Requests.Enqueue(keep.Dequeue());
                    AiChatPlugin.Log.LogInfo("[AiChat][引擎] 队列已满：为重要请求让位，挤掉一条链式回复");
                }
                Requests.Enqueue(r);
            }
            EnsureWorker();
            if (r.PlayerInvolved || r.SystemReply) EnsureFastWorker();   // ★ 第76轮：系统播报回应也走并行快速通道
        }

        // ★ 第70轮：阵亡遗言/击杀通报类台词允许由刚死的单位说出
        private static bool IsDeathLine(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return false;
            return reason.Contains("阵亡") || reason.Contains("击杀通报") || reason.Contains("阵亡·被友军误杀");
        }

        private static BotInfo Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (Sync)
            {
                BotInfo b;
                if (Roster.TryGetValue(name, out b)) return b;
            }
            return null;
        }

        // 显示层上色用：查某人的阵营 / 是否玩家本人
        internal static string TeamOf(string name)
        {
            BotInfo b = Find(name);
            return b != null ? b.Team : "";
        }

        internal static bool IsPlayerName(string name)
        {
            BotInfo b = Find(name);
            return b != null && b.IsPlayer;
        }

        private static void MarkPlayer(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "?") return;
            BotInfo b = Get(name);
            b.IsPlayer = true;
        }

        // ============================================================ 后台工人

        private static void EnsureWorker()
        {
            if (_running) return;
            _running = true;
            _worker = new Thread(new ThreadStart(WorkerLoop));
            _worker.IsBackground = true;
            _worker.Start();
            AiChatPlugin.Log.LogInfo("[AiChat][引擎] API 工作线程已启动");
        }

        private static void WorkerLoop()
        {
            while (true)
            {
                LineRequest r = null;
                lock (Sync)
                {
                    if (Requests.Count > 0)
                    {
                        // ★ 第55轮（1.1）：与玩家相关的请求插队，优先调用 API
                        LineRequest pick = null;
                        foreach (LineRequest q in Requests)
                        {
                            if (q != null && (q.PlayerInvolved || q.SystemReply)) { pick = q; break; }
                        }
                        if (pick == null)
                        {
                            r = Requests.Dequeue();
                        }
                        else
                        {
                            r = RemoveRequest(pick);
                        }
                    }
                }
                if (r == null)
                {
                    Thread.Sleep(80);
                    continue;
                }
                ProcessOne(r);
            }
        }

        // ★ 第57轮：从请求队列里移除指定的那一条（调用方需持锁）
        private static LineRequest RemoveRequest(LineRequest pick)
        {
            Queue<LineRequest> keep = new Queue<LineRequest>();
            LineRequest found = null;
            while (Requests.Count > 0)
            {
                LineRequest q = Requests.Dequeue();
                if (found == null && q == pick) { found = q; continue; }
                keep.Enqueue(q);
            }
            while (keep.Count > 0) Requests.Enqueue(keep.Dequeue());
            return found;
        }

        // ★ 第57轮：玩家事件快速通道——与主线程并行，避免排在普通请求后面
        private static int _fastWorkers;
        internal static string LastHeavy = "-";   // ★ 第77轮：卡顿看门狗上下文
        private static void EnsureFastWorker()
        {
            lock (Sync)
            {
                if (_fastWorkers >= 5) return;   // ★ 第78轮：最多 5 条并行（播报10条更快出）
                _fastWorkers++;
            }
            Thread t = new Thread(FastWorkerLoop);
            t.IsBackground = true;
            try { t.Priority = System.Threading.ThreadPriority.AboveNormal; } catch (Exception) { }
            t.Start();
        }

        private static void FastWorkerLoop()
        {
            try
            {
                while (true)
                {
                    LineRequest r = null;
                    lock (Sync)
                    {
                        LineRequest pick = null;
                        foreach (LineRequest q in Requests)
                        {
                            if (q != null && (q.PlayerInvolved || q.SystemReply)) { pick = q; break; }
                        }
                        if (pick != null) r = RemoveRequest(pick);
                    }
                    if (r == null) break;
                    ProcessOne(r);
                }
            }
            catch (Exception) { }
            finally
            {
                lock (Sync) { _fastWorkers--; if (_fastWorkers < 0) _fastWorkers = 0; }
            }
        }

        // 处理一条请求：调 API → 清洗 → 调试记录 → 进 Ready 队列
        private static void ProcessOne(LineRequest r)
        {
            if (r == null) return;
            long queueWait = (DateTime.Now.Ticks - r.CreatedTicks) / 10000L;
            string err;
            string userPrompt = r.UserPrompt;
            if (LangCode == "en") userPrompt = userPrompt + " (Reply in English, one short sentence.)";
            else userPrompt = userPrompt + "（用中文回答，一句话。）";
            long t0 = DateTime.Now.Ticks;
            DateTime reqTime = DateTime.Now;
            string text = AiChatPlugin.CallApi(r.SystemPrompt, userPrompt, out err);
            long ms = (DateTime.Now.Ticks - t0) / 10000;
            _apiCalls++;
            if (text == null)
            {
                _fails++;
                DebugAdd(BuildDebugRequest(r, userPrompt, reqTime, (int)ms, null, err));
                AiChatPlugin.Log.LogWarning("[AiChat][引擎] API 调用失败（" + r.Speaker + "）：" + err);
                if (!_warned401 && err != null && err.IndexOf("401") >= 0
                    && !string.IsNullOrEmpty(ApiKeyOverride))
                {
                    _warned401 = true;
                    try
                    {
                        OverlayChat.AddLine(Loc.Sys, Loc.T(
                            "⚠ API 返回 401：API Key 无效或已过期。请到 MOD 设置重新填写密钥（填完退出重进对局）。",
                            "⚠ API returned 401: the API key is invalid or expired. Please re-enter it in the mod settings and rejoin the match."));
                    }
                    catch (Exception) { }
                }
                return;
            }

            text = Sanitize(text);
            if (text.Length == 0) { _fails++; DebugAdd(BuildDebugRequest(r, userPrompt, reqTime, (int)ms, "", "模型返回空内容")); return; }
            DebugAdd(BuildDebugRequest(r, userPrompt, reqTime, (int)ms, text, null));

            ReadyLine rl = new ReadyLine();
            rl.Speaker = r.Speaker;
            rl.Pose = r.Pose;
            rl.Style = r.Style;
            rl.Text = text;
            rl.AtTarget = r.AtTarget;
            rl.Depth = r.Depth;
            rl.Reason = r.Reason;
            rl.Skill = r.Skill;
            rl.Ms = ms;
            rl.QueueWaitMs = queueWait;
            rl.CreatedTicks = r.CreatedTicks;
            rl.PlayerInvolved = r.PlayerInvolved;
            rl.SystemReply = r.SystemReply;   // ★ 第88轮：补上漏赋值（否则系统回应插队失效）

            if (r.PlayerInvolved)
            {
                if (AiChatPlugin.CfgVerboseLog != null && AiChatPlugin.CfgVerboseLog.Value) AiChatPlugin.Log.LogInfo("[AIChat][时序] 玩家请求完成：" + r.Speaker
                    + " 排队=" + queueWait + "ms 生成=" + ms + "ms");
            }
            else if (r.SystemReply)
            {
                if (AiChatPlugin.CfgVerboseLog != null && AiChatPlugin.CfgVerboseLog.Value) AiChatPlugin.Log.LogInfo("[AIChat][时序] 系统回应完成：" + r.Speaker
                    + " 排队=" + queueWait + "ms 生成=" + ms + "ms");
            }
            lock (Sync)
            {
                if (Ready.Count > 30)
                {
                    // 超上限：优先丢普通台词，绝不丢与玩家相关的
                    int victim = -1;
                    for (int i = 0; i < Ready.Count; i++)
                    {
                        if (!Ready[i].PlayerInvolved) { victim = i; break; }
                    }
                    if (victim >= 0) Ready.RemoveAt(victim); else Ready.RemoveAt(0);
                }
                Ready.Add(rl);
            }
        }

        // BUG-103：按句末标点截断，避免模型回复说到一半被硬切
        private static string ClampAtSentence(string t, int maxLen)
        {
            int windowStart = maxLen / 2;
            int scanTo = t.Length < maxLen ? t.Length : maxLen;
            int lastEnd = -1;
            for (int i = scanTo - 1; i >= windowStart; i--)
            {
                char c = t[i];
                if (c == '。' || c == '！' || c == '？' || c == '；' || c == '…'
                    || c == '.' || c == '!' || c == '?' || c == ';')
                {
                    lastEnd = i + 1;
                    break;
                }
            }
            if (lastEnd > 0) return t.Substring(0, lastEnd).TrimEnd();
            string cut = t.Substring(0, scanTo).TrimEnd();
            if (cut.EndsWith("…")) return cut;
            return cut + "…";
        }

        private static string Sanitize(string s)
        {
            if (s == null) return "";
            // 任何换行/制表/行分隔符都必须清掉：行协议用 \n 分隔记录、用 \t 分隔字段，
            // 一旦文本里混入这些字符，就会被 Lua 侧拆成两条"坏记录"（实测出现过）
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\n' || c == '\r' || c == '\t' || c == '\v' || c == '\f'
                    || c == '\u2028' || c == '\u2029' || c == '\u0085')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                }
                else sb.Append(c);
            }
            string t = sb.ToString().Trim();
            t = t.Trim('"', '\'', '“', '”', '‘', '’', ' ');
            // TMP 会把尖括号当富文本，必须替换
            t = t.Replace("<", "＜").Replace(">", "＞");
            // BUG-103：不再硬截 60 字符；默认 160，可配置，优先按句末标点截断
            int maxLen = 160;
            try
            {
                if (AiChatPlugin.CfgMaxLineChars != null)
                {
                    maxLen = AiChatPlugin.CfgMaxLineChars.Value;
                    if (maxLen < 40) maxLen = 40;
                    if (maxLen > 200) maxLen = 200;
                }
            }
            catch (Exception) { }
            if (t.Length > maxLen) t = ClampAtSentence(t, maxLen);
            return t.Trim();
        }

        // ============================================================ 调试窗口数据（第37轮）

        private static readonly List<DebugRecord> _debugRecords = new List<DebugRecord>();
        private static int _debugVersion;
        private const int DEBUG_MAX = 500;

        internal static int DebugVersion
        {
            get { lock (Sync) { return _debugVersion; } }
        }

        internal static List<DebugRecord> DebugSnapshot()
        {
            lock (Sync) { return new List<DebugRecord>(_debugRecords); }
        }

        internal static void DebugClearRecords()
        {
            lock (Sync) { _debugRecords.Clear(); _debugVersion++; }
        }

        // 「重置对话」：清空记录并重置统计
        internal static void DebugResetAll()
        {
            lock (Sync) { _debugRecords.Clear(); _debugVersion++; _apiCalls = 0; _fails = 0; }
        }

        internal static int DebugApiCalls { get { return _apiCalls; } }
        internal static int DebugFails { get { return _fails; } }
        internal static int DebugQueueCount { get { lock (Sync) { return Requests.Count + Ready.Count; } } }

        internal static void DebugAdd(DebugRecord r)
        {
            if (r == null) return;
            lock (Sync)
            {
                _debugRecords.Add(r);
                while (_debugRecords.Count > DEBUG_MAX) _debugRecords.RemoveAt(0);
                _debugVersion++;
            }
        }

        // 本地生成（不经过 API）的记录，例如「战报」按钮播报
        internal static void DebugAddLocal(string kind, string actor, string brief, string detail)
        {
            DebugRecord r = new DebugRecord();
            r.Time = DateTime.Now;
            r.Kind = kind;
            r.Actor = actor;
            r.Brief = brief == null ? "" : brief;
            r.Status = "本地";
            r.Ms = -1;
            r.IsEvent = true;
            r.Meta = detail == null ? "" : detail;
            DebugAdd(r);
        }

        // 游戏事件流（击杀 / 据点 / 指令 / 播报…）
        private static void DebugAddEvent(string t, Dictionary<string, string> f)
        {
            try
            {
                if (t == "tick" || t == "ime" || t == "export" || t == "selftest") return;
                DebugRecord r = new DebugRecord();
                r.Time = DateTime.Now;
                r.IsEvent = true;
                r.Status = "事件";
                r.Ms = -1;
                r.Kind = EventKindLabel(t);
                r.Actor = PickField(f, new string[] { "name", "victim", "killername", "player", "speaker", "who" });
                r.Brief = EventBrief(t, f);
                r.Meta = FieldsToText(f);
                DebugAdd(r);
            }
            catch (Exception) { }
        }

        private static string EventKindLabel(string t)
        {
            if (t == "death") return "阵亡";
            if (t == "cap") return "据点";
            if (t == "radio") return "播报";
            if (t == "order") return "指令";
            if (t == "vehicle") return "载具";
            if (t == "spawn") return "出生";
            if (t == "multi") return "多杀";
            if (t == "match") return "对局";
            if (t == "say") return "玩家发言";
            if (t == "persona") return "人设";
            if (t == "cfg") return "设置";
            return t;
        }

        private static string EventBrief(string t, Dictionary<string, string> f)
        {
            try
            {
                if (t == "death")
                {
                    string v = Val(f, "victim"), k = Val(f, "killername"), how = Val(f, "reason");
                    return ClipText(v + " 被 " + k + " 击杀" + (how.Length > 0 ? "（" + how + "）" : ""), 60);
                }
                if (t == "cap")
                {
                    return ClipText(Val(f, "point") + " " + Val(f, "team") + "（" + Val(f, "event") + "）", 60);
                }
                if (t == "spawn") return ClipText(Val(f, "name") + " 加入 " + Val(f, "team"), 60);
                if (t == "radio") return ClipText(Val(f, "text") + Val(f, "msg"), 60);
                if (t == "order") return ClipText(Val(f, "name") + " " + Val(f, "order"), 60);
                if (t == "vehicle") return ClipText(Val(f, "vehicle") + " 被毁", 60);
                if (t == "say") return ClipText(Val(f, "text"), 60);
                if (t == "multi") return ClipText(Val(f, "weapon") + " 多杀 " + Val(f, "count"), 60);
                if (t == "match") return ClipText("对局结束：" + Val(f, "winner") + " 胜", 60);
                if (t == "cfg") return ClipText("语言=" + Val(f, "lang") + " 频率=" + Val(f, "freq"), 60);
                if (t == "persona") return ClipText(Val(f, "name") + " / " + Val(f, "skill"), 60);
            }
            catch (Exception) { }
            return "";
        }

        private static string PickField(Dictionary<string, string> f, string[] keys)
        {
            try
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    string v = Val(f, keys[i]);
                    if (v.Length > 0 && v != "?") return v;
                }
            }
            catch (Exception) { }
            return "";
        }

        private static string FieldsToText(Dictionary<string, string> f)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in f)
                {
                    if (sb.Length > 0) sb.Append(" | ");
                    sb.Append(kv.Key).Append('=').Append(kv.Value);
                }
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        private static string ClipText(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ").Replace("\r", " ").Replace("\t", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        private static DebugRecord BuildDebugRequest(LineRequest r, string sentUserPrompt, DateTime t, int ms, string response, string err)
        {
            DebugRecord d = new DebugRecord();
            d.Time = t;
            d.Ms = ms;
            d.Kind = (r.Reason == null || r.Reason.Length == 0) ? "AI请求" : r.Reason;
            d.Actor = r.Speaker;
            // ★ 第43轮：列表「简述」应与右侧「响应」一致（而不是事件提示词/角色上下文）
            string briefSrc = (response != null && response.Length > 0) ? response
                            : (err != null && err.Length > 0 ? ((LangCode == "en" ? "Failed: " : "失败：") + err) : sentUserPrompt);
            d.Brief = ClipText(briefSrc, 46);
            d.SystemPrompt = r.SystemPrompt == null ? "" : r.SystemPrompt;
            d.GlobalPrompt = BuildGlobalRules()
                           + (KnowledgeBase.Text.Length > 0 ? "\n\n【常识库】\n" + KnowledgeBase.Text : "");
            BotInfo dbgBot = Find(r.Speaker);
            string dbgFaction = dbgBot != null ? FactionPrompt(dbgBot.Team) : "";
            d.CharacterPrompt = BuildCharacterPrompt(r.Speaker, r.Skill, r.Style)
                              + (dbgFaction.Length > 0 ? "\n" + dbgFaction : "")
                              + (MatchPromptText().Length > 0 ? "\n" + MatchPromptText() : "");
            d.UserPrompt = r.UserPrompt == null ? "" : r.UserPrompt;
            d.Response = response == null ? "" : response;
            d.Error = err == null ? "" : err;
            d.Model = AiChatPlugin.EffModel;
            d.PromptTokens = AiChatPlugin.LastPromptTokens;
            d.CompletionTokens = AiChatPlugin.LastCompletionTokens;
            d.Meta = "reason=" + r.Reason + " | style=" + r.Style + " | skill=" + r.Skill
                   + " | depth=" + r.Depth + " | priority=" + r.Priority + " | player=" + r.PlayerInvolved;
            if (err == null && response != null) d.Status = "成功";
            else if (err != null && (err.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                     || err.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0
                     || err.IndexOf("超时") >= 0)) d.Status = "超时";
            else d.Status = "失败";
            return d;
        }

        // ★ 第42轮：把系统提示词拆成「全局规则」和「角色提示词」两段（调试窗口分开显示）
        internal static string BuildGlobalRules()
        {
            string ov = CfgText(AiChatPlugin.CfgPromptGlobal);
            if (ov.Length > 0) return ov;
            return DefaultGlobalRules();
        }

        // ★ 第47轮：内置默认全局提示词（给设置窗口显示用）
        internal static string DefaultGlobalRules()
        {
            if (LangCode == "en")
            {
                return "You are role-playing a soldier in the game Ravenfield. "
                     + "STRICT: output ONE sentence only; at most 20 words; no quotes, no newlines, "
                     + "no explanations, no emoji; do not repeat the event text; do not sign your name; "
                     + "do NOT start with 'Roger' every time - vary your wording. Sound like a real player on voice chat.";
            }
            return "你在《战地模拟器》里扮演一名士兵。"
                 + "硬性要求：只输出一句话；不超过 25 个字；不要引号、不要换行、不要解释、不要 emoji；"
                 + "不要复述事件原文；不要报自己的名字；如果事件给了【击杀方式】，必须针对它反应；"
                 + "不要每次都从「收到」开头，句式要有变化（可以反问、吐槽、补充打算、喊人掩护）；"
                 + "要像真人开麦那样口语化。";
        }

        // ★ 第71轮：自身状态提示（存活 / 已阵亡未部署）
        private static string BotStatePrompt(BotInfo b)
        {
            try
            {
                if (b == null) return "";
                bool en = LangCode == "en";
                if (!b.IsAlive)
                {
                    return en
                        ? "YOU ARE DEAD / NOT DEPLOYED (waiting for respawn or reinforcements exhausted). You can see the battle and talk, but you CANNOT act: no charging, no covering, no shooting. Speak with that awareness (complain you are dead, joke about watching from above, tell teammates what to do)."
                        : "你目前【已阵亡 / 未部署】（等待复活，或队伍复活次数已用完）。你能看到战况、也能说话，但【不能行动】：不要说自己在冲锋、掩护、开枪。说话要带着「我已经死了」的意识（可以吐槽自己躺了、从上帝视角指挥队友、抱怨复活点）。";
                }
                return "";
            }
            catch (Exception) { return ""; }
        }

        private static string BuildCharacterPrompt(string name, string skill, string style)
        {
            string persona = SkillPrompt(skill);
            if (LangCode == "en")
            {
                return "Your persona: " + (name == null ? "" : name) + ", " + persona + ". "
                     + "Speaking style: " + StyleHintEn(style) + ".";
            }
            return "人设：" + (name == null ? "" : name) + "，" + persona + "。"
                 + "本次风格：" + StyleHint(style) + "。";
        }

        // 读一个配置里的提示词（null/空白 → ""）
        internal static string CfgText(BepInEx.Configuration.ConfigEntry<string> e)
        {
            try { return e == null || e.Value == null ? "" : e.Value.Trim(); }
            catch (Exception) { return ""; }
        }

        // ★ 第47轮：内置默认角色提示词（给设置窗口显示用）
        internal static string DefaultSkillPrompt(string skill)
        {
            return LangCode == "en" ? PersonaDescEn(skill) : PersonaDesc(skill);
        }

        // 角色提示词（按档位）：可被 MOD 设置覆盖，留空用内置 PersonaDesc
        internal static string SkillPrompt(string skill)
        {
            try
            {
                string ov = "";
                if (skill == "Elite") ov = CfgText(AiChatPlugin.CfgPromptAce);
                else if (skill == "Veteran") ov = CfgText(AiChatPlugin.CfgPromptVeteran);
                else if (skill == "Normal") ov = CfgText(AiChatPlugin.CfgPromptNormal);
                else ov = CfgText(AiChatPlugin.CfgPromptRookie);
                if (ov.Length > 0) return ov;
            }
            catch (Exception) { }
            return LangCode == "en" ? PersonaDescEn(skill) : PersonaDesc(skill);
        }

        // ★ 第51轮：对局提示词（全体角色追加；留空 = 不加）
        internal static string MatchPromptText()
        {
            try
            {
                string ov = CfgText(AiChatPlugin.CfgPromptMatch);
                if (ov.Length > 0) return (LangCode == "en" ? "Match: " : "对局：") + ov;
            }
            catch (Exception) { }
            return "";
        }

        // ★ 第63轮：玩家阵营（显示层给玩家名字上色用）
        internal static string PlayerTeamName()
        {
            BotInfo pb = PlayerBot();
            return pb != null ? pb.Team : "";
        }

        // ★ 第60轮：玩家信息（谁是玩家、哪一队）与队伍名
        private static BotInfo PlayerBot()
        {
            lock (Sync)
            {
                foreach (BotInfo b in Roster.Values)
                {
                    if (b != null && b.IsPlayer) return b;
                }
            }
            return null;
        }

        private static string TeamNameOf(string team)
        {
            bool en = LangCode == "en";
            if (team == "Red") return en ? "Raven (red)" : "鸦队（红方）";
            return en ? "Eagle (blue)" : "鹰队（蓝方）";
        }

        // ★ 第69轮：玩家是否在观察者模式（gameModeParameters.playerTeam < 0）
        internal static bool IsSpectator()
        {
            try { return BattleReport.PlayerTeamNow() < 0; } catch (Exception) { return false; }
        }

        private static string SpectatorTitle()
        {
            string t = "";
            try { t = AiChatPlugin.CfgSpectatorTitle == null ? "" : (AiChatPlugin.CfgSpectatorTitle.Value ?? "").Trim(); }
            catch (Exception) { }
            return t.Length > 0 ? t : (LangCode == "en" ? "War Correspondent" : "战地记者");
        }

        private static string PlayerTitle()
        {
            string t = "";
            try { t = AiChatPlugin.CfgPlayerTitle == null ? "" : (AiChatPlugin.CfgPlayerTitle.Value ?? "").Trim(); }
            catch (Exception) { }
            // 旧默认值是"战地最高指挥官"，用户已改为默认"指挥官"
            if (t == "战地最高指挥官") t = "指挥官";
            return t.Length > 0 ? t : (LangCode == "en" ? "Commander" : "指挥官");
        }

        // ★ 第60轮：敌我识别 + 玩家指挥官身份（只对玩家自己那一队成立）
        private static string TeamContextPrompt(BotInfo b)
        {
            try
            {
                if (b == null) return "";
                bool en = LangCode == "en";
                BotInfo pb = PlayerBot();
                string mine = TeamNameOf(b.Team);
                string enemyTeam = (b.Team == "Red") ? "Blue" : "Red";
                string foe = TeamNameOf(enemyTeam);
                StringBuilder sb = new StringBuilder();
                if (en)
                {
                    sb.Append("You are on ").Append(mine).Append("; the enemy is ").Append(foe).Append(". ");
                    sb.Append("Ask cover/coordination ONLY from your own teammates, never from the enemy. ");
                    sb.Append("To the enemy you may only taunt or threaten. ");
                    sb.Append("Do NOT call any AI soldier 'commander' - only the human player may be addressed that way.");
                }
                else
                {
                    sb.Append("你属于").Append(mine).Append("，敌人是").Append(foe).Append("。");
                    sb.Append("只能向队友请求掩护/配合，绝不能把敌人当队友、也绝不能向敌人请求掩护；对敌人只能嘲讽或威胁。");
                    sb.Append("只有玩家本人才可以被称为「").Append(PlayerTitle()).Append("」；其他任何队友或敌人都绝不能叫「指挥官」。提到玩家时请写 @玩家名字，不要用指挥官三个字指代别的 AI。");
                }
                if (IsSpectator())
                {
                    string spTitle = SpectatorTitle();
                    if (en) sb.Append("The human player is a SPECTATOR / war correspondent ('").Append(spTitle)
                             .Append("') - not on any team, NOT your commander, do not obey him; you may report the battle to him.");
                    else sb.Append("玩家处于观察者模式，身份是「").Append(spTitle)
                             .Append("」，不属于任何一队，不是指挥官，不要听他的指挥；可以把他当记者，向他介绍战况。");
                }
                else if (pb != null)
                {
                    string pname = pb.Name;
                    string title = PlayerTitle();
                    if (b.Team == pb.Team)
                    {
                        if (en) sb.Append(" Player '").Append(pname).Append("' is the supreme field commander of YOUR team (obey him); call him '").Append(title).Append("'.");
                        else sb.Append("玩家「").Append(pname).Append("」是我方战地最高指挥官（你要听他的），称呼他为「").Append(title).Append("」。");
                    }
                    else
                    {
                        if (en) sb.Append(" Player '").Append(pname).Append("' is the ENEMY supreme field commander - NOT your commander, do not obey him.");
                        else sb.Append("玩家「").Append(pname).Append("」是敌方战地最高指挥官，不是你的指挥官（不要听他的，可嘲讽）。");
                    }
                }
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        // 阵营提示词（按说话人队伍）：留空 = 不加
        internal static string FactionPrompt(string team)
        {
            try
            {
                string ov = (team == "Red") ? CfgText(AiChatPlugin.CfgPromptRaven) : CfgText(AiChatPlugin.CfgPromptEagle);
                if (ov.Length > 0) return (LangCode == "en" ? "Faction: " : "阵营：") + ov;
            }
            catch (Exception) { }
            return "";
        }

        // 调试窗口「重新发送」：用记录里的提示词再请求一次（走正常队列，仍会自动显示）
        internal static bool DebugResend(DebugRecord rec)
        {
            try
            {
                if (rec == null || string.IsNullOrEmpty(rec.SystemPrompt)) return false;
                LineRequest r = new LineRequest();
                r.Speaker = rec.Actor;
                r.Pose = "";
                r.Style = "";
                r.AtTarget = "";
                r.SystemPrompt = rec.SystemPrompt;
                r.UserPrompt = rec.UserPrompt;
                r.Priority = 1;
                r.Depth = 0;
                r.Reason = "调试重发";
                r.Skill = "?";
                r.PlayerInvolved = false;
                lock (Sync) { Requests.Enqueue(r); }
                EnsureWorker();
                return true;
            }
            catch (Exception) { return false; }
        }

        // 调试窗口「历史对话」分组用
        internal static string DebugHistoryText()
        {
            try
            {
                lock (Sync)
                {
                    StringBuilder sb = new StringBuilder();
                    int start = Math.Max(0, History.Count - 12);
                    for (int i = start; i < History.Count; i++)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(History[i]);
                    }
                    return sb.ToString();
                }
            }
            catch (Exception) { return ""; }
        }

        // ============================================================ 工具

        private static BotInfo Get(string name)
        {
            if (name == null || name.Length == 0) name = "?";
            lock (Sync)
            {
                BotInfo b;
                if (!Roster.TryGetValue(name, out b))
                {
                    b = new BotInfo();
                    b.Name = name;
                    Roster[name] = b;
                }
                return b;
            }
        }

        private static List<BotInfo> AllBots()
        {
            lock (Sync) { return new List<BotInfo>(Roster.Values); }
        }

        // ------------------------------------------------------------ 主动发言名额（v1.1.2）

        private static int QuotaForTeam(string team)
        {
            int v = 30;
            try
            {
                if (team == "Red")
                {
                    if (AiChatPlugin.CfgActiveSpeakerLimitRed != null) v = AiChatPlugin.CfgActiveSpeakerLimitRed.Value;
                }
                else
                {
                    if (AiChatPlugin.CfgActiveSpeakerLimitBlue != null) v = AiChatPlugin.CfgActiveSpeakerLimitBlue.Value;
                }
            }
            catch (Exception) { }
            if (v < 0) v = 0;
            if (v > 128) v = 128;
            return v;
        }

        private static HashSet<string> ActiveSet(string team)
        {
            HashSet<string> set;
            if (!_activeSpeakerNames.TryGetValue(team, out set))
            {
                set = new HashSet<string>();
                _activeSpeakerNames[team] = set;
            }
            return set;
        }

        private static void InitActiveRosterIfNeeded()
        {
            if (_activeRosterInit) return;
            List<BotInfo> all = AllBots();
            if (all.Count == 0) return;   // 等士兵出现后再抽
            _activeRosterInit = true;
            string[] teams = new string[] { "Blue", "Red" };
            for (int ti = 0; ti < teams.Length; ti++)
            {
                string team = teams[ti];
                List<BotInfo> pool = new List<BotInfo>();
                for (int i = 0; i < all.Count; i++)
                {
                    BotInfo b = all[i];
                    if (b != null && !b.IsPlayer && b.Team == team) pool.Add(b);
                }
                for (int i = pool.Count - 1; i > 0; i--)
                {
                    int j = UnityEngine.Random.Range(0, i + 1);
                    BotInfo tmp = pool[i]; pool[i] = pool[j]; pool[j] = tmp;
                }
                int quota = QuotaForTeam(team);
                int take = pool.Count < quota ? pool.Count : quota;
                HashSet<string> set = ActiveSet(team);
                for (int i = 0; i < take; i++) set.Add(pool[i].Name);
            }
            AiChatPlugin.Log.LogInfo("[发言名额] 已抽取本局名单：蓝=" + ActiveSet("Blue").Count
                + " 红=" + ActiveSet("Red").Count + "（配额 蓝=" + QuotaForTeam("Blue")
                + " 红=" + QuotaForTeam("Red") + "）");
        }

        // 是否允许主动发言；名单未满时允许新出现的兵补位（复活潮/中途加入不会全被误伤）
        private static bool IsActiveSpeaker(BotInfo b)
        {
            if (b == null || b.IsPlayer) return false;
            InitActiveRosterIfNeeded();
            string team = (b.Team == "Red") ? "Red" : "Blue";
            HashSet<string> other = ActiveSet((team == "Red") ? "Blue" : "Red");
            other.Remove(b.Name);   // 换队后从旧队名单移除
            HashSet<string> set = ActiveSet(team);
            if (set.Contains(b.Name)) return true;
            if (set.Count < QuotaForTeam(team))
            {
                set.Add(b.Name);
                return true;
            }
            return false;
        }

        /// <summary>优先挑"冷却已好"的士兵：否则事件入队时会被冷却挡掉，表现为"该说话时没动静"</summary>
        private static BotInfo RandomBotReady()
        {
            float now = UnityEngine.Time.time;
            float cd = AiChatPlugin.CfgBotCooldown.Value;
            List<BotInfo> ready = new List<BotInfo>();
            foreach (BotInfo b in AllBots())
            {
                if (b.IsPlayer) continue;
                if (!IsActiveSpeaker(b)) continue;
                if (now - b.LastSay >= cd) ready.Add(b);
            }
            if (ready.Count == 0) return RandomBot();
            return ready[UnityEngine.Random.Range(0, ready.Count)];
        }

        private static BotInfo RandomBot()
        {
            // 绝不能抽到玩家本人：抽到会导致这条台词被"绝不替玩家发言"静默丢弃（实测隐患）
            List<BotInfo> l = new List<BotInfo>();
            foreach (BotInfo b in AllBots()) if (!b.IsPlayer && b.IsAlive && IsActiveSpeaker(b)) l.Add(b);
            if (l.Count == 0 && AllowDeadSpeak()) foreach (BotInfo b in AllBots()) if (!b.IsPlayer && IsActiveSpeaker(b)) l.Add(b);
            if (l.Count == 0) return null;
            return l[UnityEngine.Random.Range(0, l.Count)];
        }

        private static BotInfo RandomOther(BotInfo self)
        {
            // ★ 第60轮：@ 目标只从队友里选——之前会抽到敌人，出现"@敌人来给我掩护"的荒谬台词
            List<BotInfo> l = new List<BotInfo>();
            foreach (BotInfo b in AllBots()) if (!b.IsPlayer && b != self && b.Team == self.Team && b.IsAlive && IsActiveSpeaker(b)) l.Add(b);
            if (l.Count == 0) foreach (BotInfo b in AllBots()) if (!b.IsPlayer && b != self && b.Team == self.Team && IsActiveSpeaker(b)) l.Add(b);
            if (l.Count == 0 && AllowDeadSpeak()) foreach (BotInfo b in AllBots()) if (!b.IsPlayer && b != self && IsActiveSpeaker(b)) l.Add(b);
            if (l.Count == 0) return null;
            for (int i = 0; i < 8; i++)
            {
                BotInfo c = l[UnityEngine.Random.Range(0, l.Count)];
                if (c != self) return c;
            }
            return null;
        }

        private static string RandomOtherName(BotInfo self)
        {
            BotInfo o = RandomOther(self);
            return o != null ? o.Name : null;
        }

        private static string Val(Dictionary<string, string> f, string k)
        {
            string v;
            if (f.TryGetValue(k, out v)) return v;
            return "";
        }

        private static bool IsTrue(string s)
        {
            return s == "true" || s == "True" || s == "1";
        }

        private static int IntOf(Dictionary<string, string> f, string k, int def)
        {
            string v = Val(f, k);
            int n;
            if (int.TryParse(v, out n)) return n;
            return def;
        }

        internal static Dictionary<string, string> Parse(string line)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            string[] parts = line.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) continue;
                d[parts[i].Substring(0, eq)] = Unescape(parts[i].Substring(eq + 1));
            }
            return d;
        }

        internal static string Unescape(string s)
        {
            if (s == null) return "";
            return s.Replace("%0A", "\n").Replace("%7C", "|").Replace("%3D", "=").Replace("%25", "%");
        }
    }


}
