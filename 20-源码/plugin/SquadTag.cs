// SquadTag.cs —— v1.1.3：给 AI 名字后加游戏自动分队编号（可在设置里关闭）
// 数据来源：ActorManager.instance.actors → Actor.controller as AiActorController → squad.number
using System;
using UnityEngine;

namespace RF.AiChat
{
    internal static class SquadTag
    {
        private static string _cacheName;
        private static string _cacheTag;
        private static float _cacheAt = -999f;
        private static int _debugLeft = 10;
        private static string _lastLang = "";
        private static readonly System.Collections.Generic.Dictionary<string, int> _known = new System.Collections.Generic.Dictionary<string, int>();

        internal static void ResetForNewMatch()
        {
            _known.Clear();
            _cacheName = null;
            _cacheTag = "";
            _cacheAt = -999f;
        }

        // 只对“被玩家击杀的 AI”在死亡事件里读一次生前小队；不做全表周期扫描
        internal static void CacheAtDeath(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return;
                ActorManager am = ActorManager.instance;
                if (am == null || am.actors == null) return;
                for (int i = 0; i < am.actors.Count; i++)
                {
                    Actor a = am.actors[i];
                    if (a == null || a.name != name) continue;
                    if (a.controller == null) return;
                    Squad sq = a.controller.GetSquad();
                    if (sq != null && sq.number >= 0)
                    {
                        _known[name] = sq.number;
                        if (_debugLeft > 0)
                        {
                            _debugLeft--;
                            try
                            {
                                if (AiChatPlugin.Log != null)
                                    AiChatPlugin.Log.LogInfo("[AIChat][队伍] 死亡缓存 " + name + " → " + sq.number + " 队");
                            }
                            catch (Exception) { }
                        }
                    }
                    return;
                }
            }
            catch (Exception) { }
        }

        internal static string For(string name)
        {
            try
            {
                bool on = true;
                try { on = AiChatPlugin.CfgShowSquadTag == null || AiChatPlugin.CfgShowSquadTag.Value; } catch (Exception) { }
                if (!on) return "";
                if (string.IsNullOrEmpty(name)) return "";
                if (_lastLang != ChatEngine.LangCode) { _lastLang = ChatEngine.LangCode; _debugLeft = 20; }
                if (_cacheName == name && Time.unscaledTime - _cacheAt < 2f) return _cacheTag;
                _cacheName = name;
                _cacheAt = Time.unscaledTime;
                _cacheTag = "";
                ActorManager am = ActorManager.instance;
                if (am == null || am.actors == null) return "";
                bool foundActor = false;
                bool foundController = false;
                bool foundSquad = false;
                int num = -1;
                for (int i = 0; i < am.actors.Count; i++)
                {
                    Actor a = am.actors[i];
                    if (a == null || a.name != name) continue;
                    foundActor = true;
                    if (a.controller != null)
                    {
                        foundController = true;
                        Squad sq = a.controller.GetSquad();
                        if (sq != null)
                        {
                            foundSquad = true;
                            num = sq.number;
                        }
                    }
                    break;
                }
                if (foundSquad && num >= 0)
                {
                    _known[name] = num;
                }
                else if (_known.TryGetValue(name, out num) && num >= 0)
                {
                    // 死亡/换队瞬间 GetSquad() 会变 null：用上次已知编号兜底，避免同一句话突然没标签
                    foundSquad = true;
                }
                if (foundSquad && num >= 0)
                    _cacheTag = ChatEngine.LangCode == "en" ? (" (Squad " + num + ")") : ("（" + num + "队）");
                if (_debugLeft > 0)
                {
                    _debugLeft--;
                    try
                    {
                        if (AiChatPlugin.Log != null)
                            AiChatPlugin.Log.LogInfo("[AIChat][队伍] 查找 " + name + "：lang=" + ChatEngine.LangCode
                                + " actor=" + foundActor + " controller=" + foundController
                                + " squad=" + foundSquad + " num=" + num
                                + " 总数=" + am.actors.Count + " 结果=" + _cacheTag);
                    }
                    catch (Exception) { }
                }
                return _cacheTag;
            }
            catch (Exception) { return ""; }
        }
    }
}
