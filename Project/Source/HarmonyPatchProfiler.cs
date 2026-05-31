using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace FastLoader
{
    internal static class HarmonyPatchProfiler
    {
        private static bool installed;

        public static void TryInstall(Harmony harmony)
        {
            if (installed || harmony == null)
            {
                return;
            }

            try
            {
                Type alienHarmonyType = AccessTools.TypeByName("AlienRace.AlienHarmony");
                if (alienHarmonyType == null)
                {
                    return;
                }

                MethodInfo patchMethod = AccessTools.Method(
                    alienHarmonyType,
                    "Patch",
                    new[]
                    {
                        typeof(MethodBase),
                        typeof(HarmonyMethod),
                        typeof(HarmonyMethod),
                        typeof(HarmonyMethod),
                        typeof(HarmonyMethod)
                    });

                if (patchMethod == null)
                {
                    return;
                }

                MethodInfo prefix = AccessTools.Method(typeof(HarmonyPatchProfiler), nameof(Prefix));
                MethodInfo postfix = AccessTools.Method(typeof(HarmonyPatchProfiler), nameof(Postfix));
                harmony.Patch(patchMethod, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
                installed = true;
                Log.Message("[FastLoader] Startup profiler hook installed for AlienRace.AlienHarmony.Patch.");
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Could not install AlienRace patch profiler hook.\n" + ex);
            }
        }

        private static void Prefix(object[] __args, ref PatchProfileState __state)
        {
            string name = BuildScopeName(__args);
            __state = new PatchProfileState
            {
                Name = name,
                StartTimestamp = StartupProfilerBridge.Begin(name)
            };
        }

        private static void Postfix(PatchProfileState __state)
        {
            if (__state == null)
            {
                return;
            }

            StartupProfilerBridge.End(__state.Name, __state.StartTimestamp);
        }

        private static string BuildScopeName(object[] args)
        {
            MethodBase original = args != null && args.Length > 0 ? args[0] as MethodBase : null;
            HarmonyMethod prefix = args != null && args.Length > 1 ? args[1] as HarmonyMethod : null;
            HarmonyMethod postfix = args != null && args.Length > 2 ? args[2] as HarmonyMethod : null;
            HarmonyMethod transpiler = args != null && args.Length > 3 ? args[3] as HarmonyMethod : null;
            HarmonyMethod finalizer = args != null && args.Length > 4 ? args[4] as HarmonyMethod : null;

            List<string> parts = new List<string>();
            parts.Add("original=" + DescribeMethod(original));
            AppendHarmonyMethod(parts, "prefix", prefix);
            AppendHarmonyMethod(parts, "postfix", postfix);
            AppendHarmonyMethod(parts, "transpiler", transpiler);
            AppendHarmonyMethod(parts, "finalizer", finalizer);
            return "AlienRace.AlienHarmony.Patch | " + string.Join(", ", parts.ToArray());
        }

        private static void AppendHarmonyMethod(List<string> parts, string role, HarmonyMethod method)
        {
            if (method == null || method.method == null)
            {
                return;
            }

            parts.Add(role + "=" + DescribeMethod(method.method));
        }

        private static string DescribeMethod(MethodBase method)
        {
            if (method == null)
            {
                return "(null)";
            }

            string typeName = method.DeclaringType == null ? "(unknown)" : method.DeclaringType.FullName;
            return typeName + "." + method.Name;
        }

        private sealed class PatchProfileState
        {
            public string Name;
            public long StartTimestamp;
        }
    }
}
