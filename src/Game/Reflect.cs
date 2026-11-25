using System;
using System.Collections.Generic;
using System.Reflection;

namespace Vendetta.Game
{
    /// <summary>Reads and writes the game's fields/properties by name. Lookups are cached.</summary>
    public static class Reflect
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly Dictionary<string, MemberInfo> Cache = new Dictionary<string, MemberInfo>();

        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();

        public static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            Type cached;
            if (Types.TryGetValue(fullName, out cached)) return cached;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(fullName, false); } catch (Exception) { continue; }
                if (t != null) { Types[fullName] = t; return t; }
            }
            return null;
        }

        /// <summary>"Class:Method" to MethodInfo (the first overload with that name).</summary>
        public static MethodInfo FindMethod(string classColonMethod)
        {
            if (string.IsNullOrEmpty(classColonMethod)) return null;
            int i = classColonMethod.LastIndexOf(':');
            if (i <= 0) return null;
            var type = FindType(classColonMethod.Substring(0, i));
            if (type == null) return null;
            string name = classColonMethod.Substring(i + 1);
            for (var t = type; t != null; t = t.BaseType)
                foreach (var m in t.GetMethods(All | BindingFlags.DeclaredOnly))
                    if (m.Name == name) return m;
            return null;
        }

        /// <summary>Calls the first method with this name on obj (virtual methods use the object's own version).</summary>
        public static object Call(object obj, string method, params object[] args)
        {
            if (obj == null) return null;
            for (var t = obj.GetType(); t != null; t = t.BaseType)
                foreach (var m in t.GetMethods(All | BindingFlags.DeclaredOnly))
                    if (m.Name == method && m.GetParameters().Length == args.Length) return m.Invoke(obj, args);
            return null;
        }

        private static MemberInfo Member(Type type, string name)
        {
            string key = type.FullName + "." + name;
            MemberInfo m;
            if (Cache.TryGetValue(key, out m)) return m;
            for (var t = type; t != null && m == null; t = t.BaseType)
            {
                m = (MemberInfo)t.GetField(name, All | BindingFlags.DeclaredOnly)
                    ?? t.GetProperty(name, All | BindingFlags.DeclaredOnly);
            }
            Cache[key] = m;
            return m;
        }

        /// <summary>For a dotted path ("PlayerStats.isDead"), moves obj to the last part's owner and name to the last part.</summary>
        private static void FollowPath(ref object obj, ref string name)
        {
            if (obj == null || string.IsNullOrEmpty(name)) return;
            int i = name.LastIndexOf('.');
            if (i <= 0) return;
            obj = Get(obj, name.Substring(0, i));
            name = name.Substring(i + 1);
        }

        public static bool Has(object obj, string name)
        {
            FollowPath(ref obj, ref name);
            return obj != null && !string.IsNullOrEmpty(name) && Member(obj.GetType(), name) != null;
        }

        public static object Get(object obj, string name)
        {
            FollowPath(ref obj, ref name);
            if (obj == null || string.IsNullOrEmpty(name)) return null;
            var m = Member(obj.GetType(), name);
            var f = m as FieldInfo;
            if (f != null) return f.GetValue(obj);
            var p = m as PropertyInfo;
            if (p != null && p.CanRead) return p.GetValue(obj, null);
            return null;
        }

        /// <summary>Every object along a dotted path must be a class: a change inside a struct would be lost.</summary>
        public static bool Set(object obj, string name, object value)
        {
            FollowPath(ref obj, ref name);
            if (obj == null || string.IsNullOrEmpty(name)) return false;
            var m = Member(obj.GetType(), name);
            try
            {
                var f = m as FieldInfo;
                if (f != null) { f.SetValue(obj, Convert(value, f.FieldType)); return true; }
                var p = m as PropertyInfo;
                if (p != null && p.CanWrite) { p.SetValue(obj, Convert(value, p.PropertyType), null); return true; }
            }
            catch (Exception) { }
            return false;
        }

        public static float GetFloat(object obj, string name, float fallback)
        {
            var v = Get(obj, name);
            if (v == null) return fallback;
            try { return System.Convert.ToSingle(v); } catch (Exception) { return fallback; }
        }

        public static bool GetBool(object obj, string name, bool fallback)
        {
            var v = Get(obj, name);
            return v is bool ? (bool)v : fallback;
        }

        private static object Convert(object value, Type to)
        {
            if (value == null || to.IsInstanceOfType(value)) return value;
            if (to.IsPrimitive || to == typeof(decimal)) return System.Convert.ChangeType(value, to);
            return value;
        }
    }
}
