// RF AiChat Bridge —— 战报系统（第 32 轮）
// 目标：点击聊天框底部「战报」按钮 → 插件本地生成固定格式战报 → 立即作为系统消息插播。
// 特点：不走 AI、不消耗 API、无冷却；语言跟随 MOD 设置里的"语言"（zh/en），模板完全双语化。
// 数据：全部通过反射读游戏私有字段（Assembly-CSharp），读不到时自动降级，不抛异常。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace RF.AiChat
{
    // 双语小工具：跟 MOD 设置里的语言开关走
    internal static class Loc
    {
        internal static bool En { get { return ChatEngine.LangCode == "en"; } }
        internal static string T(string zh, string en) { return En ? en : zh; }
        internal static string Sys { get { return En ? "System" : "系统"; } }
    }

    internal static class BattleReport
    {
        // ============================================================
        // 公共入口：返回若干行战报正文（每行会作为一条系统消息显示）
        // ============================================================
        internal static string ModeKeyPublic { get { return ModeKey(); } }

        internal static List<string> Build()
        {
            List<string> lines = new List<string>();
            try
            {
                string key = ModeKey();
                object mode = ActiveMode();
                if (key == "Battle" && mode != null) AddBattle(lines, mode);
                else if (key == "PointMatch" && mode != null) AddPointMatch(lines, mode);
                else if (key == "Skirmish" && mode != null) AddSkirmish(lines, mode);
                else if (key == "Domination" && mode != null) AddDomination(lines, mode);
                else if (key == "SpecOps" && mode != null) AddSpecOps(lines, mode);
                else if (key == "Haunted" && mode != null) AddHaunted(lines, mode);
                else AddGeneric(lines);
                if (key != "Haunted") AddKdLine(lines);
                if (lines.Count == 0) AddGeneric(lines);
            }
            catch (Exception e)
            {
                try { AiChatPlugin.Log.LogWarning("[AiChat][战报] 生成异常：" + e.Message); } catch (Exception) { }
                if (lines.Count == 0) lines.Add(Title("") + " " + Loc.T("读取战况失败", "Failed to read battle status"));
            }
            return lines;
        }

        // ============================================================
        // 各模式正文
        // ============================================================
        private static void AddBattle(List<string> lines, object mode)
        {
            int[] tickets = IntArr(Field(mode, "tickets"));
            int[] bats = IntArr(Field(mode, "remainingBattalions"));
            lines.Add(Title("Battle") + " "
                + Loc.T("红方", "Red") + " " + Loc.T("复活票", "tickets") + " " + At(tickets, 1)
                + " · " + Loc.T("营", "battalions") + " " + At(bats, 1)
                + " ｜ " + Loc.T("蓝方", "Blue") + " " + Loc.T("复活票", "tickets") + " " + At(tickets, 0)
                + " · " + Loc.T("营", "battalions") + " " + At(bats, 0));
            AddPointsLine(lines);
        }

        private static void AddPointMatch(List<string> lines, object mode)
        {
            int red = I(Field(mode, "redScore"), -1);
            int blue = I(Field(mode, "blueScore"), -1);
            int rf = I(Field(mode, "redFlags"), -1);
            int bf = I(Field(mode, "blueFlags"), -1);
            int vp = I(Field(mode, "victoryPoints"), -1);
            string s = Title("PointMatch") + " "
                + Loc.T("红方", "Red") + " " + Loc.T("比分", "score") + " " + N(red)
                + " · " + Loc.T("据点", "points") + " " + N(rf)
                + " ｜ " + Loc.T("蓝方", "Blue") + " " + Loc.T("比分", "score") + " " + N(blue)
                + " · " + Loc.T("据点", "points") + " " + N(bf);
            if (vp > 0) s += Loc.T("（胜利分 " + vp + "）", " (victory " + vp + ")");
            lines.Add(s);
            AddPointsLine(lines);
        }

        private static void AddSkirmish(List<string> lines, object mode)
        {
            int[] waves = IntArr(Field(mode, "reinforcementWavesRemaining"));
            lines.Add(Title("Skirmish") + " "
                + Loc.T("红方", "Red") + " " + Loc.T("存活", "alive") + " " + N(Alive(1))
                + " · " + Loc.T("支援波", "waves") + " " + At(waves, 1)
                + " ｜ " + Loc.T("蓝方", "Blue") + " " + Loc.T("存活", "alive") + " " + N(Alive(0))
                + " · " + Loc.T("支援波", "waves") + " " + At(waves, 0));
            AddPointsLine(lines);
        }

        private static void AddDomination(List<string> lines, object mode)
        {
            int[] bats = IntArr(Field(mode, "remainingBattalions"));
            float[] ratio = FloatArr(Field(mode, "dominationRatio"));
            lines.Add(Title("Domination") + " "
                + Loc.T("红方", "Red") + " " + Loc.T("营", "battalions") + " " + At(bats, 1)
                + " · " + Loc.T("推进", "progress") + " " + Pct(ratio, 1)
                + " ｜ " + Loc.T("蓝方", "Blue") + " " + Loc.T("营", "battalions") + " " + At(bats, 0)
                + " · " + Loc.T("推进", "progress") + " " + Pct(ratio, 0));
            string contested = ContestedFlags(mode);
            if (contested.Length > 0)
                lines.Add(Loc.T("当前争夺组：", "Contested flags: ") + contested);
            AddPointsLine(lines);
        }

        // ★ 特种作战（SP）播报格式（用户指定）：
        //   第1行：任务进度 + 我方身份
        //   第2行：敌方队员存活总数 + 敌方 KD 前三
        //   第3行：我方队员存活总数 + 每个队员的 K/D 与存活状态（如：张三 4/1 存活）
        private static void AddSpecOps(List<string> lines, object mode)
        {
            IList objs = Ls(Field(mode, "activeObjectives"));
            int total = 0, done = 0;
            string current = "";
            if (objs != null)
            {
                for (int i = 0; i < objs.Count; i++)
                {
                    object o = objs[i];
                    object entry = Field(o, "objective");
                    if (entry == null) continue;
                    total++;
                    if (B(Field(entry, "isCompleted"), false)) done++;
                    else if (!B(Field(entry, "isFailed"), false) && current.Length == 0)
                        current = TextOf(entry);
                }
            }
            int atk = I(Field(mode, "attackingTeam"), -2);
            int def = I(Field(mode, "defendingTeam"), -2);
            int me = PlayerTeamNow();
            if (me < 0) me = atk;                       // SP 里玩家通常是进攻方
            string side = (me == atk) ? Loc.T("进攻方", "attacker")
                        : ((me == def) ? Loc.T("防守方", "defender") : Loc.T("观战", "spectator"));

            string head = Title("SpecOps") + " " + Loc.T("任务进度 ", "Objectives ") + done + "/" + N(total)
                        + " ｜ " + Loc.T("我方：", "Your side: ") + side;
            lines.Add(head);
            if (current.Length > 0)
                lines.Add(Loc.T("当前目标：", "Current objective: ") + current);

            if (me < 0) return;
            int enemy = (me == 0) ? 1 : 0;

            // 敌方：存活总数 + KD 前三
            int enemyAlive = Alive(enemy);
            string enemyTop = Top3(enemy);
            StringBuilder e = new StringBuilder();
            e.Append(Loc.T("敌方：存活 ", "Enemy: alive ")).Append(N(enemyAlive));
            if (enemyTop.Length > 0)
                e.Append(Loc.T(" ｜ KD前三：", " | Top KD: ")).Append(enemyTop);
            lines.Add(e.ToString());

            // 我方：存活总数 + 每个队员的 K/D 与状态
            int myAlive = Alive(me);
            string members = SquadKdStatus(me);
            StringBuilder o2 = new StringBuilder();
            o2.Append(Loc.T("我方：存活 ", "Our squad: alive ")).Append(N(myAlive));
            if (members.Length > 0)
                o2.Append(Loc.T(" ｜ ", " | ")).Append(members);
            lines.Add(o2.ToString());
        }

        // 每个队员的 "名字 K/D 存活状态"，例如：张三 4/1 存活、李四 2/2 阵亡
        private static string SquadKdStatus(int team)
        {
            try
            {
                object am = StaticField(T("ActorManager"), "instance");
                Array at = Arr(Field(am, "actorsOnTeam"));
                if (at == null || team < 0 || team >= at.Length) return "";
                IList list = at.GetValue(team) as IList;
                if (list == null || list.Count == 0) return "";
                StringBuilder sb = new StringBuilder();
                int shown = 0;
                for (int i = 0; i < list.Count && shown < 12; i++)
                {
                    object a = list[i];
                    if (a == null) continue;
                    string nm = ActorDisplayName(a);
                    if (nm.Length == 0) continue;
                    int k, d;
                    ActorKd(a, out k, out d);
                    bool dead = B(Field(a, "dead"), false);
                    if (sb.Length > 0) sb.Append(Loc.T("、", ", "));
                    sb.Append(nm).Append(' ').Append(k).Append('/').Append(d)
                      .Append(' ').Append(dead ? Loc.T("阵亡", "KIA") : Loc.T("存活", "alive"));
                    shown++;
                }
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        // 取某个 Actor 的击杀/死亡数（优先游戏记分板条目，其次 0/0）
        private static void ActorKd(object actor, out int kills, out int deaths)
        {
            kills = 0; deaths = 0;
            try
            {
                object entry = Field(actor, "scoreboardEntry");
                if (entry != null)
                {
                    kills = I(Field(entry, "kills"), 0);
                    deaths = I(Field(entry, "deaths"), 0);
                }
            }
            catch (Exception) { }
        }

        private static void AddHaunted(List<string> lines, object mode)
        {
            int phase = I(Field(mode, "currentPhase"), -1);
            int totalW = -1;
            Array wsc = Arr(StaticField(T("SpookOpsMode"), "WAVE_SKELETON_COUNT"));
            if (wsc != null) totalW = wsc.Length;
            int zombies = 0;
            IList wave = Ls(Field(mode, "waveSkeletons"));
            if (wave != null)
            {
                for (int i = 0; i < wave.Count; i++)
                    if (!B(Field(wave[i], "dead"), false)) zombies++;
            }
            int kill = I(Field(mode, "killCount"), -1);
            int target = I(Field(mode, "targetKillCount"), -1);
            string s = Title("Haunted") + " " + Loc.T("第 ", "Wave ") + N(phase);
            if (totalW > 0) s += "/" + totalW;
            s += Loc.T(" 波", "");
            if (zombies > 0 || wave != null) s += " · " + Loc.T("场上剩余僵尸 ", "skeletons left ") + zombies;
            if (kill >= 0)
            {
                s += " · " + Loc.T("本波已击杀 ", "kills this wave ") + kill;
                if (target > 0) s += "/" + target;
            }
            lines.Add(s);

            object am = StaticField(T("ActorManager"), "instance");
            object player = Field(am, "player");
            object entry = Field(player, "scoreboardEntry");
            int pk = I(Field(entry, "kills"), -1);
            int pd = I(Field(entry, "deaths"), -1);
            if (pk >= 0)
                lines.Add(Loc.T("你：", "You: ") + pk + Loc.T(" 杀 ", "/") + N(pd) + Loc.T(" 死", " deaths"));
        }

        private static void AddGeneric(List<string> lines)
        {
            lines.Add(Title("") + " "
                + Loc.T("红方", "Red") + " " + Loc.T("存活", "alive") + " " + N(Alive(1))
                + " ｜ " + Loc.T("蓝方", "Blue") + " " + Loc.T("存活", "alive") + " " + N(Alive(0)));
        }

        private static void AddKdLine(List<string> lines)
        {
            string red = Top3(1);
            string blue = Top3(0);
            if (red.Length == 0 && blue.Length == 0) return;
            StringBuilder sb = new StringBuilder();
            sb.Append(Loc.T("KD前三：", "Top KD: "));
            if (red.Length > 0) sb.Append(Loc.T("红方 ", "Red ")).Append(red);
            if (red.Length > 0 && blue.Length > 0) sb.Append(" ｜ ");
            if (blue.Length > 0) sb.Append(Loc.T("蓝方 ", "Blue ")).Append(blue);
            lines.Add(sb.ToString());
        }

        private static void AddPointsLine(List<string> lines)
        {
            int neutral;
            List<string>[] pts = PointsByTeam(out neutral);
            bool any = (pts[0].Count + pts[1].Count) > 0;
            if (!any) return;
            StringBuilder sb = new StringBuilder();
            sb.Append(Loc.T("据点：", "Points: "));
            sb.Append(Loc.T("红方 ", "Red ")).Append(JoinNames(pts[1]));
            sb.Append(" ｜ ");
            sb.Append(Loc.T("蓝方 ", "Blue ")).Append(JoinNames(pts[0]));
            if (neutral > 0) sb.Append(" ｜ ").Append(Loc.T("中立 " + neutral, "neutral " + neutral));
            lines.Add(sb.ToString());
        }

        // ============================================================
        // 数据读取
        // ============================================================
        private static object ActiveMode()
        {
            return StaticField(T("GameModeBase"), "activeGameMode");
        }

        private static string ModeKey()
        {
            // ★ 优先看当前真正的 GameModeBase 实例类型（legacyType 对 Haunted 会误报成 SpecOps）
            object active = ActiveMode();
            if (active != null)
            {
                string tn = active.GetType().Name;
                if (tn == "BattleMode") return "Battle";
                if (tn == "PointMatch") return "PointMatch";
                if (tn == "SkirmishMode") return "Skirmish";
                if (tn == "DominationMode") return "Domination";
                if (tn == "SpecOpsMode") return "SpecOps";
                if (tn == "SpookOpsMode") return "Haunted";
            }
            string s = "";
            try
            {
                object gm = StaticField(T("GameManager"), "instance");
                object gp = Field(gm, "gameModeParameters");
                object gi = Field(gp, "gameMode");
                object leg = Field(gi, "legacyType");
                s = leg == null ? "" : leg.ToString();
            }
            catch (Exception) { }
            return s;
        }

        private static string Title(string key)
        {
            string label;
            switch (key)
            {
                case "Battle": label = Loc.T("战争", "BATTLE"); break;
                case "PointMatch": label = Loc.T("积分", "POINT MATCH"); break;
                case "Skirmish": label = Loc.T("遭遇战", "SKIRMISH"); break;
                case "Domination": label = Loc.T("支配", "DOMINATION"); break;
                case "SpecOps": label = Loc.T("特种作战", "SPEC OPS"); break;
                case "Haunted": label = Loc.T("幽灵", "HAUNTED"); break;
                default: label = Loc.T("战报", "REPORT"); break;
            }
            return Loc.T("【战报·" + label + "】", "[REPORT · " + label + "]");
        }

        private static int Alive(int team)
        {
            try
            {
                object am = StaticField(T("ActorManager"), "instance");
                object d = Field(am, "aliveActors");
                IDictionary dict = d as IDictionary;
                if (dict != null && dict.Contains(team))
                {
                    IList l = dict[team] as IList;
                    if (l != null) return l.Count;
                }
                object at = Field(am, "actorsOnTeam");
                Array arr = at as Array;
                if (arr != null && arr.Length > team)
                {
                    IList l2 = arr.GetValue(team) as IList;
                    if (l2 != null)
                    {
                        int n = 0;
                        for (int i = 0; i < l2.Count; i++)
                            if (!B(Field(l2[i], "dead"), false)) n++;
                        return n;
                    }
                }
            }
            catch (Exception) { }
            return -1;
        }

        private static List<string>[] PointsByTeam(out int neutral)
        {
            neutral = 0;
            List<string>[] res = new List<string>[] { new List<string>(), new List<string>() };
            try
            {
                object am = StaticField(T("ActorManager"), "instance");
                Array sp = Arr(Field(am, "spawnPoints"));
                if (sp == null) return res;
                for (int i = 0; i < sp.Length; i++)
                {
                    object p = sp.GetValue(i);
                    if (p == null || p.GetType().Name != "CapturePoint") continue;
                    int owner = I(Field(p, "owner"), -1);
                    string nm = S(Field(p, "shortName")).Trim();
                    if (nm.Length == 0) nm = ObjName(p);
                    if (nm.Length == 0) nm = Loc.T("据点", "Point") + (i + 1);
                    if (owner == 0) res[0].Add(nm);
                    else if (owner == 1) res[1].Add(nm);
                    else neutral++;
                }
            }
            catch (Exception) { }
            return res;
        }

        private static string ContestedFlags(object mode)
        {
            try
            {
                object fs = Field(mode, "activeFlagSet");
                if (fs == null) return "";
                Array flags = Arr(Field(fs, "flags"));
                if (flags == null) return "";
                List<string> names = new List<string>();
                for (int i = 0; i < flags.Length; i++)
                {
                    object p = flags.GetValue(i);
                    if (p == null) continue;
                    string nm = ObjName(p);
                    if (nm.Length == 0) nm = Loc.T("据点", "Point") + (i + 1);
                    names.Add(nm);
                }
                return JoinNames(names);
            }
            catch (Exception) { return ""; }
        }

        private static string Top3(int team)
        {
            try
            {
                object sui = StaticField(T("ScoreboardUi"), "instance");
                object d = Field(sui, "entriesOfTeam");
                IDictionary dict = d as IDictionary;
                if (dict == null || !dict.Contains(team)) return "";
                IList list = dict[team] as IList;
                if (list == null || list.Count == 0) return "";
                List<object> arr = new List<object>();
                for (int i = 0; i < list.Count; i++) arr.Add(list[i]);
                arr.Sort(delegate(object x, object y)
                {
                    int kx = I(Field(x, "kills"), 0), ky = I(Field(y, "kills"), 0);
                    if (kx != ky) return ky.CompareTo(kx);
                    int dx = I(Field(x, "deaths"), 0), dy = I(Field(y, "deaths"), 0);
                    return dx.CompareTo(dy);
                });
                StringBuilder sb = new StringBuilder();
                int n = Math.Min(3, arr.Count);
                for (int i = 0; i < n; i++)
                {
                    object e = arr[i];
                    string nm = ActorDisplayName(Field(e, "actor"));
                    if (nm.Length == 0) nm = Loc.T("无名", "Unknown");
                    if (sb.Length > 0) sb.Append(Loc.T("、", ", "));
                    sb.Append(nm).Append(' ')
                      .Append(I(Field(e, "kills"), 0)).Append('/')
                      .Append(I(Field(e, "deaths"), 0));
                }
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        // 士兵的显示名：Actor.name 属性（游戏内中文名）优先，其次 _name，最后才是对象名
        private static string ActorDisplayName(object actor)
        {
            try
            {
                if (actor == null) return "";
                PropertyInfo pi = actor.GetType().GetProperty("name");
                if (pi != null)
                {
                    object v = pi.GetValue(actor, null);
                    string s = v == null ? "" : Convert.ToString(v);
                    if (s != null && s.Trim().Length > 0) return s.Trim();
                }
                string n2 = S(Field(actor, "_name")).Trim();
                if (n2.Length > 0) return n2;
                return ObjName(actor);
            }
            catch (Exception) { return ObjName(actor); }
        }

        // 玩家阵营：优先读 gameModeParameters.playerTeam（各模式都准），读不到再用 GameManager.PlayerTeam()
        internal static int PlayerTeamNow()
        {
            try
            {
                object gm = StaticField(T("GameManager"), "instance");
                object gp = Field(gm, "gameModeParameters");
                int t = I(Field(gp, "playerTeam"), -1);
                if (t >= 0 && t <= 1) return t;
            }
            catch (Exception) { }
            return PlayerTeam();
        }

        private static int PlayerTeam()
        {
            try
            {
                object gm = StaticField(T("GameManager"), "instance");
                Type t = T("GameManager");
                if (gm == null || t == null) return -1;
                MethodInfo m = AccessTools.Method(t, "PlayerTeam", new Type[0]);
                if (m == null) return -1;
                return Convert.ToInt32(m.Invoke(gm, null));
            }
            catch (Exception) { return -1; }
        }

        private static string TextOf(object objectiveEntry)
        {
            try
            {
                object txt = Field(objectiveEntry, "text");
                UnityEngine.UI.Text t = txt as UnityEngine.UI.Text;
                if (t != null && !string.IsNullOrEmpty(t.text)) return t.text.Trim();
                if (txt != null) return Convert.ToString(txt).Trim();
            }
            catch (Exception) { }
            return "";
        }

        // ============================================================
        // 反射 / 格式化小工具
        // ============================================================
        private static Type T(string name)
        {
            try { return AccessTools.TypeByName(name); } catch (Exception) { return null; }
        }

        private static object StaticField(Type t, string name)
        {
            try
            {
                FieldInfo f = FindField(t, name);
                return f == null ? null : f.GetValue(null);
            }
            catch (Exception) { return null; }
        }

        private static object Field(object obj, string name)
        {
            try
            {
                if (obj == null) return null;
                FieldInfo f = FindField(obj.GetType(), name);
                return f == null ? null : f.GetValue(obj);
            }
            catch (Exception) { return null; }
        }

        // 沿继承链找字段（游戏里的 owner/shortName 等字段声明在基类 SpawnPoint 上）
        private static FieldInfo FindField(Type t, string name)
        {
            while (t != null)
            {
                FieldInfo f = t.GetField(name,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        private static int I(object v, int def)
        {
            try { return v == null ? def : Convert.ToInt32(v); } catch (Exception) { return def; }
        }

        private static bool B(object v, bool def)
        {
            try { return v == null ? def : Convert.ToBoolean(v); } catch (Exception) { return def; }
        }

        private static string S(object v)
        {
            return v == null ? "" : Convert.ToString(v);
        }

        private static Array Arr(object v) { return v as Array; }
        private static IList Ls(object v) { return v as IList; }

        private static int[] IntArr(object v)
        {
            try
            {
                Array a = v as Array;
                if (a == null) return null;
                int[] r = new int[a.Length];
                for (int i = 0; i < a.Length; i++) r[i] = Convert.ToInt32(a.GetValue(i));
                return r;
            }
            catch (Exception) { return null; }
        }

        private static float[] FloatArr(object v)
        {
            try
            {
                Array a = v as Array;
                if (a == null) return null;
                float[] r = new float[a.Length];
                for (int i = 0; i < a.Length; i++) r[i] = Convert.ToSingle(a.GetValue(i));
                return r;
            }
            catch (Exception) { return null; }
        }

        private static string ObjName(object o)
        {
            try
            {
                UnityEngine.Object uo = o as UnityEngine.Object;
                return uo == null ? "" : (uo.name == null ? "" : uo.name.Trim());
            }
            catch (Exception) { return ""; }
        }

        private static string N(int v) { return v < 0 ? "?" : v.ToString(); }

        private static string At(int[] a, int i)
        {
            if (a == null || i < 0 || i >= a.Length) return "?";
            return a[i].ToString();
        }

        private static string Pct(float[] a, int i)
        {
            if (a == null || i < 0 || i >= a.Length) return "?";
            return ((int)Math.Round(a[i] * 100f)).ToString() + "%";
        }

        private static string JoinNames(List<string> names)
        {
            if (names == null || names.Count == 0) return Loc.T("无", "none");
            StringBuilder sb = new StringBuilder();
            int n = Math.Min(names.Count, 6);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(Loc.T("、", ", "));
                sb.Append(names[i]);
            }
            if (names.Count > n) sb.Append(Loc.T(" 等" + names.Count + "个", " (+" + (names.Count - n) + ")"));
            return sb.ToString();
        }
    }
}
