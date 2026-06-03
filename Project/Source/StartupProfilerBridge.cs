using System;
using System.Reflection;

namespace FastLoader
{
    internal static class StartupProfilerBridge
    {
        private static readonly object LockObject = new object();
        private static bool initialized;
        private static bool available;
        private static Func<string, long> begin;
        private static Action<string, long> end;

        public static bool Available
        {
            get
            {
                EnsureInitialized();
                return available;
            }
        }

        public static long Begin(string name)
        {
            try
            {
                string profilerName = NormalizeStartupProfilerScope(name);
                if (profilerName == null)
                {
                    return 0L;
                }

                EnsureInitialized();
                if (!available || begin == null)
                {
                    return 0L;
                }

                return begin(profilerName);
            }
            catch
            {
                return 0L;
            }
        }

        public static void End(string name, long startTimestamp)
        {
            if (startTimestamp == 0L)
            {
                return;
            }

            try
            {
                string profilerName = NormalizeStartupProfilerScope(name);
                if (profilerName == null)
                {
                    return;
                }

                EnsureInitialized();
                if (available && end != null)
                {
                    end(profilerName, startTimestamp);
                }
            }
            catch
            {
            }
        }

        private static string NormalizeStartupProfilerScope(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }

            if (!name.StartsWith("FastLoader.", StringComparison.Ordinal))
            {
                return name;
            }

            if (name.StartsWith("FastLoader.LanguageCache.TryRead", StringComparison.Ordinal))
            {
                return "FastLoader.LanguageCache.TryRead";
            }

            if (name.StartsWith("FastLoader.LanguageCache.Apply", StringComparison.Ordinal))
            {
                return "FastLoader.LanguageCache.Apply";
            }

            if (name.StartsWith("FastLoader.DefInjectedCache.TryRead", StringComparison.Ordinal))
            {
                return "FastLoader.DefInjectedCache.TryRead";
            }

            if (name.StartsWith("FastLoader.DefInjected.FastBefore", StringComparison.Ordinal))
            {
                return "FastLoader.DefInjected.FastBefore";
            }

            if (name.StartsWith("FastLoader.DefInjected.FastAfter", StringComparison.Ordinal))
            {
                return "FastLoader.DefInjected.FastAfter";
            }

            if (name.StartsWith("FastLoader.TextureCache.CachedReload", StringComparison.Ordinal))
            {
                return "FastLoader.TextureCache.CachedReload";
            }

            if (name.StartsWith("FastLoader.StaticAtlasCache.TryRestore", StringComparison.Ordinal))
            {
                return "FastLoader.StaticAtlasCache.TryRestore";
            }

            return null;
        }

        private static void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            lock (LockObject)
            {
                if (initialized)
                {
                    return;
                }

                try
                {
                    Type profilerType = FindType("RimWorldStartupProfiler.StartupProfiler");
                    if (profilerType != null)
                    {
                        MethodInfo beginMethod = profilerType.GetMethod("Begin", BindingFlags.Public | BindingFlags.Static);
                        MethodInfo endMethod = profilerType.GetMethod("End", BindingFlags.Public | BindingFlags.Static);
                        if (beginMethod != null && endMethod != null)
                        {
                            begin = (Func<string, long>)Delegate.CreateDelegate(typeof(Func<string, long>), beginMethod);
                            end = (Action<string, long>)Delegate.CreateDelegate(typeof(Action<string, long>), endMethod);
                            available = true;
                        }
                    }
                }
                catch
                {
                    available = false;
                    begin = null;
                    end = null;
                }

                initialized = true;
            }
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = assemblies[i].GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }
    }
}
