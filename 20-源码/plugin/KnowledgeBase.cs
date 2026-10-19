// RF AiChat Bridge —— 第56轮：常识库（Knowledge Base）
// 目的：给 AI 一份"这个游戏/这个圈子"的常识，避免它乱编武器名、不懂术语、说话出戏。
// 存储：BepInEx\config\rf.aichat.kb.txt（UTF-8，可直接用记事本编辑；游戏内也有编辑窗口）
// 用法：内容会追加到系统提示词的【常识库】段（对全体角色生效）；留空 = 不启用。
using System;
using System.IO;
using System.Text;

namespace RF.AiChat
{
    internal static class KnowledgeBase
    {
        internal static string FilePath
        {
            get
            {
                try { return Path.Combine(BepInEx.Paths.ConfigPath, "rf.aichat.kb.txt"); }
                catch (Exception) { return "rf.aichat.kb.txt"; }
            }
        }

        private static string _text = "";
        private static DateTime _stamp = DateTime.MinValue;
        private static long _lastCheckTicks;   // ★ 第61轮：mtime 检查节流（1 秒），避免每条请求都读盘导致卡顿

        // 读取（带 mtime 缓存；文件改动后自动重新加载）
        internal static string Text
        {
            get
            {
                try
                {
                    long nowTicks = DateTime.UtcNow.Ticks;
                    // ★ 第61轮：1 秒内不重复 stat 文件（之前每条 AI 请求都 File.Exists + GetLastWriteTimeUtc，
                    //   频繁读盘在 Windows 上会引起周期性卡顿）
                    bool due = _stamp == DateTime.MinValue || (nowTicks - _lastCheckTicks) > 10000000L;
                    if (due)
                    {
                        _lastCheckTicks = nowTicks;
                        string fp = FilePath;
                        if (File.Exists(fp))
                        {
                            DateTime t = File.GetLastWriteTimeUtc(fp);
                            if (t != _stamp)
                            {
                                _stamp = t;
                                _text = (File.ReadAllText(fp, Encoding.UTF8) ?? "").Trim();
                            }
                        }
                    }
                    return _text;
                }
                catch (Exception) { return _text; }
            }
        }

        internal static void Save(string s)
        {
            try
            {
                string fp = FilePath;
                File.WriteAllText(fp, s == null ? "" : s, new UTF8Encoding(true));
                _text = (s ?? "").Trim();
                try { _stamp = File.GetLastWriteTimeUtc(fp); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        // 内置默认常识（首次打开编辑窗口时显示；点"恢复默认"会写回这份）
        internal static string DefaultText()
        {
            return @"# 常识库（每行一条；以 # 开头的行会被忽略）
# 作用：让 AI 知道 Ravenfield 的武器、模式、术语和圈子黑话，别说错、别出戏。
# 写完后保存，下一条台词立即生效。

[武器 / 击杀方式]
- M1 GARRET 是狙击步枪；SL-DEFENDER 是反器材狙击；SIGNAL DMR 是精确射手步枪
- PATRIOT GL 是榴弹发射器；SLAM-R 是爆炸物；DAGGER 是冲锋枪；RK-44 是突击步枪
- 被爆头＝headshot；被爆炸物炸死＝splash；被载具撞死＝VehicleRam；被近战打死＝Melee；摔死＝FallDamage
- 常见载具：坦克、吉普、直升机；别说成现实里的型号（比如 M1A2、悍马）

[模式 / 地图术语]
- 常见模式：征服(Conquest)、突击、特种作战(SpecOps)、幽灵模式(Haunted)、积分/占点
- 点位说法：一号点、B点、控制点、据点、基地；比分叫「」
- 播报术语：KD（击杀/死亡比）、战报、据点丢失/夺回、推进、压点、卡点、绕后、侧翼

[圈子黑话 / 语气]
- 队友互称：兄弟、老哥、哥们；骂人不带家属，最多「菜就多练」「别送」「白给」
- 常见梗：老六（蹲角落阴人）、喷子（霰弹枪/口嗨）、奶妈、送人头、背锅、开麦、报点、打头阵
- 玩家打中文时可能是拼音缩写：w=我、y=有、6=溜、牛逼/菜/寄
- 不知道的缩写就直说「听不懂」，不要硬编

[行为规范]
- 不要编造游戏里不存在的武器、模式、地图
- 不要复述事件原文，不要报自己名字，不要解释自己在扮演
- 一句话，口语化，像真人开麦
";
        }
    }
}
