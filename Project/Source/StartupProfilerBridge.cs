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
                EnsureInitialized();
                if (!available || begin == null)
                {
                    return 0L;
                }

                return begin(name);
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
                EnsureInitialized();
                if (available && end != null)
                {
                    end(name, startTimestamp);
                }
            }
            catch
            {
            }
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

                bool success = false;
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
                            success = true;
                        }
                    }
                }
                catch
                {
                    available = false;
                    begin = null;
                    end = null;
                }

                initialized = success;
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
