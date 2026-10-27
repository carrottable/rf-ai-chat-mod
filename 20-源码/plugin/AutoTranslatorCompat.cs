// AutoTranslatorCompat —— 与 XUnity.AutoTranslator 的兼容层（v1.1.2+）
//
// 背景：汉化包使用 XUnity.AutoTranslator；它会把 UGUI/TMP 的新文字送去翻译，
// 网络不通时每个未知文本都会等待超时，导致整帧卡顿。我们的聊天框和设置页
// 是动态文字，不应该被它翻译（我们本身已经是中英双语/目标语言内容）。
//
// 做法：通过反射调用 AutoTranslator 的 ITranslator.IgnoreTextComponent(component)，
// 把我们的 Text/TMP 组件加入它自己的忽略列表；不硬引用 AutoTranslator 程序集，
// 没装它时完全无副作用。
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace RF.AiChat
{
    internal static class AutoTranslatorCompat
    {
        private static bool _resolved;
        private static object _translator;
        private static MethodInfo _ignoreMethod;
        private static float _nextTry;
        private static readonly HashSet<int> _ignored = new HashSet<int>();
        private static readonly List<object> _pending = new List<object>();
        private static bool _logged;

        internal static void Ignore(object component)
        {
            if (component == null) return;
            try
            {
                UnityEngine.Object uo = component as UnityEngine.Object;
                int id = uo != null ? uo.GetInstanceID() : component.GetHashCode();
                if (_ignored.Contains(id)) return;
                if (!Resolve())
                {
                    if (_pending.Count < 64 && !_pending.Contains(component)) _pending.Add(component);
                    return;
                }
                if (_translator == null || _ignoreMethod == null) return;
                _ignoreMethod.Invoke(_translator, new object[] { component });
                _ignored.Add(id);
                if (!_logged)
                {
                    _logged = true;
                    try
                    {
                        if (AiChatPlugin.Log != null)
                            AiChatPlugin.Log.LogInfo("[AIChat][兼容] 已让 XUnity.AutoTranslator 忽略本 MOD 的聊天/设置文字（防止它等待翻译超时造成卡顿）");
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        internal static void Tick()
        {
            if (_pending.Count == 0) return;
            if (!Resolve()) return;
            if (_translator == null || _ignoreMethod == null) return;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                object c = _pending[i];
                if (c == null) { _pending.RemoveAt(i); continue; }
                try
                {
                    _ignoreMethod.Invoke(_translator, new object[] { c });
                    UnityEngine.Object uo = c as UnityEngine.Object;
                    int id = uo != null ? uo.GetInstanceID() : c.GetHashCode();
                    _ignored.Add(id);
                    _pending.RemoveAt(i);
                }
                catch (Exception) { }
            }
            if (!_logged && _pending.Count == 0)
            {
                _logged = true;
                try
                {
                    if (AiChatPlugin.Log != null)
                        AiChatPlugin.Log.LogInfo("[AIChat][兼容] 已让 XUnity.AutoTranslator 忽略本 MOD 的聊天/设置文字（防止它等待翻译超时造成卡顿）");
                }
                catch (Exception) { }
            }
        }

        private static bool Resolve()
        {
            if (_translator != null && _ignoreMethod != null) return true;
            if (Time.unscaledTime < _nextTry) return false;
            _nextTry = Time.unscaledTime + 2f;
            try
            {
                Type at = null;
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length && at == null; i++)
                {
                    try { at = asms[i].GetType("XUnity.AutoTranslator.Plugin.Core.AutoTranslator", false); }
                    catch (Exception) { }
                }
                if (at == null)
                {
                    try { at = Type.GetType("XUnity.AutoTranslator.Plugin.Core.AutoTranslator, XUnity.AutoTranslator.Plugin.Core", false); }
                    catch (Exception) { }
                }
                if (at == null) return false;

                PropertyInfo def = at.GetProperty("Default", BindingFlags.Public | BindingFlags.Static);
                if (def == null) return false;
                object tr = def.GetValue(null, null);
                if (tr == null) return false;

                MethodInfo mi = null;
                Type it = tr.GetType().GetInterface("XUnity.AutoTranslator.Plugin.Core.ITranslator");
                if (it != null) mi = it.GetMethod("IgnoreTextComponent");
                if (mi == null)
                {
                    Type[] ifaces = tr.GetType().GetInterfaces();
                    for (int i = 0; i < ifaces.Length && mi == null; i++)
                    {
                        MethodInfo[] ms = ifaces[i].GetMethods();
                        for (int j = 0; j < ms.Length; j++)
                        {
                            if (ms[j].Name == "IgnoreTextComponent") { mi = ms[j]; break; }
                        }
                    }
                }
                if (mi == null) return false;
                _translator = tr;
                _ignoreMethod = mi;
                _resolved = true;
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
