using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FastLoader
{
    internal static class FastLoaderDebug
    {
        private const string HarmonyId = "solaris.fastloader";
        private static readonly FieldInfo ModTexturesField = AccessTools.Field(typeof(ModContentPack), "textures");
        private static bool loggedMainMenuPatchState;

        public static void LogPatchState()
        {
            try
            {
                LogPatchTarget("LoadedLanguage.LoadData", AccessTools.Method(typeof(LoadedLanguage), nameof(LoadedLanguage.LoadData)));
                LogPatchTarget("LoadedLanguage.InjectIntoData_BeforeImpliedDefs", AccessTools.Method(typeof(LoadedLanguage), nameof(LoadedLanguage.InjectIntoData_BeforeImpliedDefs)));
                LogPatchTarget("LoadedLanguage.InjectIntoData_AfterImpliedDefs", AccessTools.Method(typeof(LoadedLanguage), nameof(LoadedLanguage.InjectIntoData_AfterImpliedDefs)));
                LogPatchTarget("LoadedModManager.LoadModXML", AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML)));
                LogPatchTarget("LoadedModManager.CombineIntoUnifiedXML", AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.CombineIntoUnifiedXML)));
                LogPatchTarget("LoadedModManager.ApplyPatches", AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ApplyPatches)));
                LogPatchTarget("LoadedModManager.ParseAndProcessXML", AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ParseAndProcessXML)));
                LogPatchTarget("ModContentPack.ReloadContentInt", AccessTools.Method(typeof(ModContentPack), "ReloadContentInt"));
                LogPatchTarget("StaticTextureAtlas.Bake", AccessTools.Method(typeof(StaticTextureAtlas), nameof(StaticTextureAtlas.Bake)));
                LogPatchTarget("MainMenuDrawer.Init", AccessTools.Method(typeof(MainMenuDrawer), nameof(MainMenuDrawer.Init)));
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to inspect Harmony patch state.\n" + ex);
            }
        }

        public static void LogPatchStateAfterMainMenu()
        {
            if (loggedMainMenuPatchState)
            {
                return;
            }

            loggedMainMenuPatchState = true;
            LogPatchState();
        }

        public static string DescribeMod(ModContentPack mod)
        {
            if (mod == null)
            {
                return "mod=(null)";
            }

            return "modName=" + Safe(mod.Name) +
                ", packageId=" + Safe(mod.PackageId) +
                ", root=" + Safe(mod.RootDir) +
                ", core=" + mod.IsCoreMod +
                ", official=" + mod.IsOfficialMod;
        }

        public static string DescribeTexture(ModContentPack mod, string internalPath, Texture2D texture)
        {
            return DescribeMod(mod) +
                ", texturePath=" + Safe(internalPath) +
                ", " + DescribeTextureObject(texture);
        }

        public static string DescribeRawTextureEntry(ModContentPack mod, RawTextureEntry entry)
        {
            if (entry == null)
            {
                return DescribeMod(mod) + ", rawTexture=(null)";
            }

            int bytes = entry.RawData != null ? entry.RawData.Length : 0;
            return DescribeMod(mod) +
                ", texturePath=" + Safe(entry.InternalPath) +
                ", textureName=" + Safe(entry.Name) +
                ", size=" + entry.Width + "x" + entry.Height +
                ", format=" + entry.TextureFormat +
                ", mipmaps=" + entry.MipmapCount +
                ", bytes=" + bytes;
        }

        public static string DescribeTextureOrigin(Texture2D texture)
        {
            string fallback = DescribeTextureObject(texture);
            if (texture == null || ModTexturesField == null)
            {
                return fallback;
            }

            List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
            for (int i = 0; i < mods.Count; i++)
            {
                ModContentPack mod = mods[i];
                ModContentHolder<Texture2D> holder = ModTexturesField.GetValue(mod) as ModContentHolder<Texture2D>;
                if (holder == null || holder.contentList == null)
                {
                    continue;
                }

                foreach (KeyValuePair<string, Texture2D> kvp in holder.contentList)
                {
                    if (object.ReferenceEquals(kvp.Value, texture))
                    {
                        return DescribeTexture(mod, kvp.Key, texture);
                    }
                }
            }

            return fallback + ", originMod=(not found in loaded mod texture holders)";
        }

        public static string DescribeAtlas(StaticTextureAtlas atlas, List<Texture2D> textures)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("atlasGroup=");
            builder.Append(atlas != null ? atlas.groupKey.ToString() : "(null)");
            builder.Append(", textureCount=");
            builder.Append(textures != null ? textures.Count : 0);

            if (textures != null)
            {
                int count = Math.Min(5, textures.Count);
                for (int i = 0; i < count; i++)
                {
                    builder.Append("\n  texture[");
                    builder.Append(i);
                    builder.Append("]: ");
                    builder.Append(DescribeTextureOrigin(textures[i]));
                }

                if (textures.Count > count)
                {
                    builder.Append("\n  ... ");
                    builder.Append(textures.Count - count);
                    builder.Append(" more textures");
                }
            }

            return builder.ToString();
        }

        public static string FormatExceptionContext(string stage, string context, Exception exception)
        {
            Exception root = exception != null ? exception.GetBaseException() : null;
            return "[FastLoader] " + Safe(stage) + " failed." +
                "\nContext: " + Safe(context) +
                "\nReason: " + (root != null ? root.GetType().Name + ": " + root.Message : "unknown") +
                "\nException:\n" + exception;
        }

        private static void LogPatchTarget(string label, MethodBase target)
        {
            if (target == null)
            {
                Log.Warning("[FastLoader] Harmony target missing: " + label);
                return;
            }

            Patches patches = Harmony.GetPatchInfo(target);
            if (patches == null)
            {
                Log.Warning("[FastLoader] Harmony target has no patch info: " + label);
                return;
            }

            bool hasFastLoader = ContainsOwner(patches.Prefixes) ||
                ContainsOwner(patches.Postfixes) ||
                ContainsOwner(patches.Transpilers) ||
                ContainsOwner(patches.Finalizers);
            string otherOwners = GetOtherOwners(patches);
            if (!hasFastLoader)
            {
                Log.Warning("[FastLoader] FastLoader Harmony patch is missing from target " + label + ". Other patches: " + otherOwners);
                return;
            }

            if (!string.IsNullOrEmpty(otherOwners))
            {
                Log.Message("[FastLoader] Harmony target " + label + " also has patches from: " + otherOwners);
            }
        }

        private static bool ContainsOwner(IEnumerable<Patch> patches)
        {
            if (patches == null)
            {
                return false;
            }

            foreach (Patch patch in patches)
            {
                if (patch != null && string.Equals(patch.owner, HarmonyId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetOtherOwners(Patches patches)
        {
            List<string> owners = new List<string>();
            AppendOtherOwners(owners, patches.Prefixes, "prefix");
            AppendOtherOwners(owners, patches.Postfixes, "postfix");
            AppendOtherOwners(owners, patches.Transpilers, "transpiler");
            AppendOtherOwners(owners, patches.Finalizers, "finalizer");
            return string.Join("; ", owners.ToArray());
        }

        private static void AppendOtherOwners(List<string> owners, IEnumerable<Patch> patches, string role)
        {
            if (patches == null)
            {
                return;
            }

            foreach (Patch patch in patches)
            {
                if (patch == null || string.Equals(patch.owner, HarmonyId, StringComparison.Ordinal))
                {
                    continue;
                }

                owners.Add(role + "=" + Safe(patch.owner) +
                    ", priority=" + patch.priority +
                    "@" + DescribeMethod(patch.PatchMethod));
            }
        }

        private static string DescribeTextureObject(Texture2D texture)
        {
            if (texture == null)
            {
                return "texture=(null)";
            }

            string readable;
            try
            {
                readable = texture.isReadable.ToString();
            }
            catch
            {
                readable = "unknown";
            }

            return "textureName=" + Safe(texture.name) +
                ", size=" + texture.width + "x" + texture.height +
                ", format=" + texture.format +
                ", graphicsFormat=" + texture.graphicsFormat +
                ", mipmaps=" + texture.mipmapCount +
                ", readable=" + readable;
        }

        private static string DescribeMethod(MethodBase method)
        {
            if (method == null)
            {
                return "(unknown method)";
            }

            string typeName = method.DeclaringType != null ? method.DeclaringType.FullName : "(unknown type)";
            return typeName + "." + method.Name;
        }

        private static string Safe(string value)
        {
            return string.IsNullOrEmpty(value) ? "(empty)" : value;
        }
    }
}
