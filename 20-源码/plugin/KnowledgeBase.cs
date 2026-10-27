// RF AiChat Bridge —— 常识库（Knowledge Base，六模块版）
// 存储：BepInEx\config\rf.aichat.kb.base/weapon/vehicle/map/mode/other.txt
// 规则：基础模块每句都带；武器/载具/地图/模式/其他按事件类型按需加载。
// 兼容：首次运行会把旧的单文件 rf.aichat.kb.txt 拆成六个模块，并把旧文件改名为 .legacy.txt.bak。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RF.AiChat
{
    internal static class KnowledgeBase
    {
        internal static readonly string[] ModuleKeys = { "base", "weapon", "vehicle", "map", "mode", "other" };
        internal static readonly string[] ModuleNames = { "基础", "武器", "载具", "地图", "模式", "其他" };

        private static readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        private static readonly Dictionary<string, DateTime> _stamps = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, long> _lastChecks = new Dictionary<string, long>();
        private static bool _init;

        private const string Default_base = @"Ravenfield 是围绕据点争夺的战场模拟：Eagle（鹰队，蓝方）对 Raven（鸦队，红方）。
玩家是队伍里的指挥官，也是真正在开枪的人；你（AI）是玩家身边或对面的普通士兵。
战斗核心是抢占据点、消耗对方复活票数；票数先耗尽的队伍输掉这一局。
常见说法：票数、KD、战报、推进、压点、卡点、绕后、侧翼。";

        private const string Default_weapon = @"- RK-44：标准突击步枪，全自动，综合性能均衡。
- Greaser：老式冲锋枪，射速较慢但稳定，适合中近距离。
- Signal DMR：带瞄准镜的射手步枪，中远距离点射。
- Quicksilver：消音冲锋枪，适合潜行和绕后。
- Patriot：带全息瞄准镜的突击步枪，爱国者系列基础型。
- Patriot GL：爱国者突击步枪加下挂榴弹发射器。
- Patriot TAC：爱国者突击步枪的消音版本。
- HMG：重机枪，弹容大、压制强，但机动性差。
- SL-Defender：半自动反器材狙击枪，对人员和轻载具都有效。
- Recon LRR：无托远程步枪，高精度半自动，适合远距离。
- M1 Garret：栓动狙击步枪，射速慢但远距离一枪致命。
- 76 Eagle：霰弹枪，近距离火力强。
- Automatico Akimbo：双持 Automatico 冲锋枪，近距离爆发高。
- S-IND7：半自动手枪，标准副武器。
- S-IND7 [SUP]：S-IND7 的消音版本。
- .357 Condor：左轮手枪，停止力强但射速慢。
- Bundle O' Buss：多管手铳，近距离爆发武器。
- AA-AA：个人防空手枪，用于对付空中目标。
- Automatico：高射速冲锋枪，后坐力需要控制。
- Dagger：火箭筒，反坦克武器。
- Slam-R：肩射反器材/反坦克武器，可打远距离或高速目标。
- Scalpel：制导导弹，锁定敌方载具。
- Hydra：九头蛇制导导弹，多弹头反装甲。
- Railgun (Unpowered)：未充能电磁炮。
- Railgun：充能电磁炮，高伤害、射速慢。
- Spearhead：眩晕手榴弹，干扰敌人。
- Smoke Grenade：烟雾弹，遮挡视线。
- Frag：破片手雷。
- C-4：塑胶炸弹，可遥控引爆。
- Thumper：榴弹发射器，可打高爆弹或烟雾弹。
- Wrench：扳手，可维修载具。
- Sabre：军刀，近战武器。
- Squad Leader Kit：小队指挥套件/战术望远镜。
- Resistance Sword：彩蛋近战武器“圣剑”。
- Medic Bag：医疗包，治疗队友。
- Ammo Bag：弹药包，补充弹药。
- Air Horn：喇叭，制造声响或嘲讽。";

        private const string Default_vehicle = @"- Ground Transport：无武装地面运输载具，用来快速运兵。
- MG Turret：固定机枪炮塔，不能驾驶，用于防守。
- Razorback MG：武装吉普，车顶带机枪。
- Prowler Quadbike：四轮摩托，速度快，适合两人快速移动。
- Buzzard Plane：攻击机，对地攻击。
- Rhino Tank：主战坦克，装甲厚、主炮伤害高。
- Anti Air turret：固定防空炮塔，专门打空中目标。
- Attack Boat：攻击艇，水面火力支援。
- Cassowary Bomber：轰炸机，投掷炸弹。
- Mudskipper APC：两栖装甲运输车，带机炮。
- Blackbird Helicopter：运输直升机，运送步兵。
- Raptor Helicopter：攻击直升机，带火箭巢和加特林，不是坦克。
注意：固定机枪/防空炮塔不是可驾驶载具；玩家可能坐在载具里用枪或载具武器击杀你，提到载具要说名字，不要用现实型号（M1A2、悍马、阿帕奇）替代。";

        private const string Default_map = @"- 官方地图：Island（海岛）、Twin Islands（双子岛）、Archipelago（群岛）、Coastline（海岸线）。
- 还有 Desert Hill（荒漠）、Dustbowl（沙漠基地）、Citadel（要塞）。
- 地图名用英文原名，不要编不存在的地图，也不要把游戏地图说成现实地点。";

        private const string Default_mode = @"- Conquest（征服）：抢占据点并消耗对方票数，最基础的模式。
- Battle（战争模式）：大规模战线战斗，推进并守住阵地。
- Domination（兵力对抗）：围绕据点控制消耗对方兵力。
- Skirmish（死斗）：小规模遭遇战，以击杀/消耗为目标。
- SpecOps（特别行动）：围绕特定目标展开的攻防战。
- Haunted（亡灵模式）：夜间/亡灵主题的特殊玩法。
- Arcade（休闲战役）：连续 9 局难度递增，每局选择新武器、载具或增援。";

        private const string Default_other = @"- 队友互称：兄弟、老哥、哥们；骂人不带家属，最多「菜就多练」「别送」「白给」。
- 常见梗：老六（蹲角落阴人）、喷子（霰弹枪/口嗨）、奶妈、送人头、背锅、开麦、报点、打头阵。
- 玩家打中文时可能是拼音缩写：w=我、y=有、6=溜、牛逼/菜/寄。
- 不知道的缩写就直说「听不懂」，不要硬编。

- 不要编造游戏里不存在的武器、模式、地图；不要复述事件原文，不要报自己名字，不要解释自己在扮演。
- 一句话，口语化，像真人开麦。";

        internal static string FilePathFor(string module)
        {
            try { return Path.Combine(BepInEx.Paths.ConfigPath, "rf.aichat.kb." + module + ".txt"); }
            catch (Exception) { return "rf.aichat.kb." + module + ".txt"; }
        }

        internal static string DefaultFor(string module)
        {
            switch (module)
            {
                case "base": return Default_base.Trim();
                case "weapon": return Default_weapon.Trim();
                case "vehicle": return Default_vehicle.Trim();
                case "map": return Default_map.Trim();
                case "mode": return Default_mode.Trim();
                case "other": return Default_other.Trim();
                default: return "";
            }
        }

        private static void EnsureInit()
        {
            if (_init) return;
            _init = true;
            try
            {
                string dir = BepInEx.Paths.ConfigPath;
                bool anyModule = false;
                for (int i = 0; i < ModuleKeys.Length; i++)
                {
                    if (File.Exists(Path.Combine(dir, "rf.aichat.kb." + ModuleKeys[i] + ".txt"))) { anyModule = true; break; }
                }
                string legacy = Path.Combine(dir, "rf.aichat.kb.txt");
                if (anyModule)
                {
                    if (File.Exists(legacy))
                    {
                        try { File.Move(legacy, legacy + ".legacy.txt.bak"); } catch (Exception) { }
                    }
                    return;
                }
                if (!File.Exists(legacy)) return;

                string src = File.ReadAllText(legacy, Encoding.UTF8) ?? "";
                Dictionary<string, string> parts = SplitLegacy(src);
                for (int i = 0; i < ModuleKeys.Length; i++)
                {
                    string key = ModuleKeys[i];
                    string text;
                    if (parts.TryGetValue(key, out text) && text.Length > 0)
                    {
                        try { File.WriteAllText(Path.Combine(dir, "rf.aichat.kb." + key + ".txt"), text, new UTF8Encoding(true)); }
                        catch (Exception) { }
                    }
                }
                try { File.Move(legacy, legacy + ".legacy.txt.bak"); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        private static Dictionary<string, string> SplitLegacy(string src)
        {
            var dict = new Dictionary<string, string>();
            string current = null;
            StringBuilder sb = null;
            string[] lines = (src ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i] ?? "";
                string t = line.Trim();
                if (t.Length > 2 && t[0] == '[' && t[t.Length - 1] == ']')
                {
                    if (current != null && sb != null) Add(dict, current, sb.ToString());
                    string h = t.Substring(1, t.Length - 2);
                    if (h.IndexOf("背景", StringComparison.Ordinal) >= 0) current = "base";
                    else if (h.IndexOf("武器", StringComparison.Ordinal) >= 0) current = "weapon";
                    else if (h.IndexOf("载具", StringComparison.Ordinal) >= 0) current = "vehicle";
                    else if (h.IndexOf("地图", StringComparison.Ordinal) >= 0) current = "map";
                    else if (h.IndexOf("模式", StringComparison.Ordinal) >= 0) current = "mode";
                    else if (h.IndexOf("黑话", StringComparison.Ordinal) >= 0
                          || h.IndexOf("语气", StringComparison.Ordinal) >= 0
                          || h.IndexOf("行为", StringComparison.Ordinal) >= 0) current = "other";
                    else current = null;
                    sb = current == null ? null : new StringBuilder();
                }
                else if (current != null && sb != null) sb.AppendLine(line);
            }
            if (current != null && sb != null) Add(dict, current, sb.ToString());
            if (dict.Count == 0 && !string.IsNullOrEmpty(src)) dict["other"] = src.Trim();
            return dict;
        }

        private static void Add(Dictionary<string, string> d, string k, string v)
        {
            if (string.IsNullOrEmpty(v)) return;
            string old;
            if (d.TryGetValue(k, out old) && old.Length > 0) v = old + "\n" + v;
            d[k] = v;
        }

        internal static string TextFor(string module)
        {
            EnsureInit();
            try
            {
                string fp = FilePathFor(module);
                if (!File.Exists(fp)) return DefaultFor(module);
                long now = DateTime.UtcNow.Ticks;
                long last;
                _lastChecks.TryGetValue(module, out last);
                bool due = !_stamps.ContainsKey(module) || (now - last) > 10000000L;
                if (due)
                {
                    _lastChecks[module] = now;
                    DateTime t = File.GetLastWriteTimeUtc(fp);
                    DateTime old;
                    if (!_stamps.TryGetValue(module, out old) || old != t)
                    {
                        _stamps[module] = t;
                        _cache[module] = (File.ReadAllText(fp, Encoding.UTF8) ?? "").Trim();
                    }
                }
                string v;
                return _cache.TryGetValue(module, out v) ? v : DefaultFor(module);
            }
            catch (Exception) { return DefaultFor(module); }
        }

        internal static string Text
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < ModuleKeys.Length; i++)
                {
                    string t = TextFor(ModuleKeys[i]);
                    if (string.IsNullOrEmpty(t)) continue;
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append("[").Append(ModuleNames[i]).Append("]\n").Append(t);
                }
                return sb.ToString();
            }
        }

        internal static void Save(string module, string s)
        {
            EnsureInit();
            try
            {
                string fp = FilePathFor(module);
                File.WriteAllText(fp, s == null ? "" : s, new UTF8Encoding(true));
                _cache[module] = (s ?? "").Trim();
                try { _stamps[module] = File.GetLastWriteTimeUtc(fp); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        private static bool HasAny(string text, params string[] keys)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < keys.Length; i++) if (text.IndexOf(keys[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        internal static string SelectFor(string context)
        {
            EnsureInit();
            string ctx = context ?? "";
            bool opening = HasAny(ctx, "开局", "对局开始", "部署", "战斗开始", "match start");
            bool vehicle = HasAny(ctx, "载具", "RAPTOR", "RHINO", "RAZORBACK", "MUDSKIPPER", "BLACKBIRD",
                                  "BUZZARD", "CASSOWARY", "PROWLER", "ATTACK BOAT", "VehicleRam", "被车", "撞死");
            bool weapon = HasAny(ctx, "击杀方式", "武器", "枪", "爆头", "RECON", "SLAM", "SL-DEFENDER",
                                 "RK-44", "DAGGER", "THUMPER", "炸死", "打死", "手雷", "C-4", "刀");
            bool tactical = HasAny(ctx, "战报", "据点", "站点", "控制点", "占领", "夺取", "丢失",
                                   "票数", "比分", "优先级", "小队命令", "命令", "队长", "cap", "flag", "point");

            StringBuilder sb = new StringBuilder();
            Append(sb, "base");
            if (vehicle) Append(sb, "vehicle");
            else if (weapon) Append(sb, "weapon");
            if (opening || tactical) { Append(sb, "map"); Append(sb, "mode"); }
            if (!opening && !vehicle && !weapon && !tactical) Append(sb, "other");
            string result = sb.ToString().Trim();
            if (result.Length > 4000) result = result.Substring(0, 4000);
            return result;
        }

        private static void Append(StringBuilder sb, string module)
        {
            string t = TextFor(module);
            if (string.IsNullOrEmpty(t)) return;
            if (sb.Length > 0) sb.Append("\n");
            sb.Append(t.Trim());
        }
    }
}
