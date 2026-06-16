using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FastLoader
{
    [HarmonyPatch(typeof(LoadedLanguage), nameof(LoadedLanguage.LoadData))]
    internal static class Patch_LoadedLanguage_LoadData_LanguageCache
    {
        private static bool Prefix(LoadedLanguage __instance, ref FastLoaderProfileScope __state)
        {
            __state = FastLoaderProfiler.Scope("FastLoader.LanguageLoad.Total");
            if (LanguageBinaryCache.IsDataLoaded(__instance))
            {
                return true;
            }

            if (LanguageBinaryCache.TryLoadDataFromCache(__instance))
            {
                return false;
            }

            return true;
        }

        private static void Postfix(FastLoaderProfileScope __state)
        {
            if (__state != null)
            {
                __state.Dispose();
            }
        }
    }

    [HarmonyPatch(typeof(LoadedLanguage), nameof(LoadedLanguage.InjectIntoData_BeforeImpliedDefs))]
    internal static class Patch_LoadedLanguage_InjectBefore_FastDefInjected
    {
        private static bool Prefix(LoadedLanguage __instance, ref FastLoaderProfileScope __state)
        {
            __state = FastLoaderProfiler.Scope("FastLoader.DefInjected.Before.Total");
            return !FastDefInjectedApplier.TryInjectBefore(__instance);
        }

        private static void Postfix(FastLoaderProfileScope __state)
        {
            if (__state != null)
            {
                __state.Dispose();
            }
        }
    }

    [HarmonyPatch(typeof(LoadedLanguage), nameof(LoadedLanguage.InjectIntoData_AfterImpliedDefs))]
    internal static class Patch_LoadedLanguage_InjectAfter_FastDefInjected
    {
        private static bool Prefix(LoadedLanguage __instance, ref FastLoaderProfileScope __state)
        {
            __state = FastLoaderProfiler.Scope("FastLoader.DefInjected.After.Total");
            return !FastDefInjectedApplier.TryInjectAfter(__instance);
        }

        private static void Postfix(FastLoaderProfileScope __state)
        {
            if (__state != null)
            {
                __state.Dispose();
            }
        }
    }

    [HarmonyPatch(typeof(MainMenuDrawer), nameof(MainMenuDrawer.Init))]
    internal static class Patch_MainMenuDrawer_Init_FastLoaderCommandBridge
    {
        private static void Postfix()
        {
            FastLoaderDebug.LogPatchStateAfterMainMenu();
            FastLoaderCommandBridge.ProcessCommandLineBuildRequests();
            FastLoaderModUpdateChecker.StartAfterMainMenu();
        }
    }

    [HarmonyPatch(typeof(Root), nameof(Root.Update))]
    internal static class Patch_Root_Update_FastLoaderModUpdateChecker
    {
        private static void Postfix()
        {
            FastLoaderModUpdateChecker.PollUi();
            FastLoaderCacheIssueReporter.PollUi();
        }
    }

    [HarmonyPatch(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML))]
    internal static class Patch_LoadModXML
    {
        private static bool Prefix(bool hotReload, ref List<LoadableXmlAsset> __result, ref FastLoaderDualProfileScope __state)
        {
            FastLoaderRuntime.BeginLoad(hotReload);
            __state = FastLoaderProfiler.Scope("LoadedModManager.LoadModXML", "FastLoader.Xml.LoadModXML.Total");

            if (FastLoaderRuntime.IsCacheHit)
            {
                __result = new List<LoadableXmlAsset>();
                return false;
            }

            return true;
        }

        private static void Postfix(FastLoaderDualProfileScope __state)
        {
            if (__state != null)
            {
                __state.Dispose();
            }
        }
    }

    [HarmonyPatch(typeof(LoadedModManager), nameof(LoadedModManager.CombineIntoUnifiedXML))]
    internal static class Patch_CombineIntoUnifiedXML
    {
        private static bool Prefix(ref XmlDocument __result, ref FastLoaderDualProfileScope __state)
        {
            __state = FastLoaderProfiler.Scope("LoadedModManager.CombineIntoUnifiedXML", "FastLoader.Xml.CombineIntoUnifiedXML.Total");
            if (FastLoaderRuntime.IsCacheHit)
            {
                __result = FastLoaderRuntime.CachedResolvedDefs;
                return false;
            }

            return true;
        }

        private static void Postfix(FastLoaderDualProfileScope __state)
        {
            if (__state != null)
            {
                __state.Dispose();
            }
        }
    }

    [HarmonyPatch(typeof(LoadedModManager), nameof(LoadedModManager.ErrorCheckPatches))]
    internal static class Patch_ErrorCheckPatches
    {
        private static bool Prefix(ref FastLoaderDualProfileScope __state)
        {
            __state = FastLoaderProfiler.Scope("LoadedModManager.ErrorCheckPatches", "FastLoader.Xml.ErrorCheckPatches.Total");
            return !FastLoaderRuntime.IsCacheHit;
        }

        private static void Postfix(FastLoaderDualProfileScope __state)
        {
            if (__state != null)
            {
                __state.Dispose();
            }
        }
    }

    [HarmonyPatch(typeof(LoadedModManager), nameof(LoadedModManager.ApplyPatches))]
    internal static class Patch_ApplyPatches
    {
        private static bool Prefix(ref FastLoaderDualProfileScope __state)
        {
            __state = FastLoaderProfiler.Scope("LoadedModManager.ApplyPatches", "FastLoader.Xml.ApplyPatches.Total");
            return !FastLoaderRuntime.IsCacheHit;
        }

        private static void Postfix(FastLoaderDualProfileScope __state)
        {
            if (__state != null)
            {
                __state.Dispose();
            }
        }
    }

    [HarmonyPatch(typeof(LoadedModManager), nameof(LoadedModManager.ParseAndProcessXML))]
    internal static class Patch_ParseAndProcessXML
    {
        private sealed class ParseState
        {
            public FastLoaderDualProfileScope Scope;
            public bool UsedCache;
            public int ParsedDefCount;
        }

        private static bool Prefix(XmlDocument xmlDoc, bool hotReload, ref ParseState __state)
        {
            __state = new ParseState
            {
                Scope = FastLoaderProfiler.Scope("LoadedModManager.ParseAndProcessXML", "FastLoader.Xml.ParseAndProcessXML.Total")
            };

            if (!FastLoaderRuntime.IsCacheHit)
            {
                return true;
            }

            __state.UsedCache = true;
            try
            {
                using (FastProfile.Scope("Cache hit parse resolved XML"))
                {
                    __state.ParsedDefCount = FastLoaderXmlParser.ParseCachedResolvedXml(xmlDoc, FastLoaderRuntime.CacheEntries, hotReload);
                    FastLoaderRuntime.SetParsedDefCount(__state.ParsedDefCount);
                }
            }
            catch (Exception ex)
            {
                FastLoaderRuntime.ActivateVanillaFallback(FastLoaderCacheKind.Xml, "cached XML parse failed: " + ex.GetType().Name);
                Log.Error("[FastLoader] Cache hit parse failed. This load cannot safely continue from cached XML.\n" + ex);
                throw;
            }

            return false;
        }

        private static void Postfix(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup, ParseState __state)
        {
            int parsedDefCount = __state != null && __state.UsedCache ? __state.ParsedDefCount : CountCurrentXmlElements(xmlDoc);
            try
            {
                FastLoaderRuntime.SaveCacheFromResolvedXml(xmlDoc, assetlookup);
                FastLoaderRuntime.SetParsedDefCount(parsedDefCount);
            }
            finally
            {
                if (__state != null && __state.Scope != null)
                {
                    __state.Scope.Dispose();
                }
            }
        }

        private static int CountCurrentXmlElements(XmlDocument xmlDoc)
        {
            if (xmlDoc == null || xmlDoc.DocumentElement == null)
            {
                return 0;
            }

            int count = 0;
            foreach (XmlNode node in xmlDoc.DocumentElement.ChildNodes)
            {
                if (node.NodeType == XmlNodeType.Element)
                {
                    count++;
                }
            }

            return count;
        }
    }

    [HarmonyPatch]
    internal static class Patch_ModContentPackReloadContentInt_TextureCache
    {
        private static readonly FieldInfo AudioClipsField = AccessTools.Field(typeof(ModContentPack), "audioClips");
        private static readonly FieldInfo TexturesField = AccessTools.Field(typeof(ModContentPack), "textures");
        private static readonly FieldInfo StringsField = AccessTools.Field(typeof(ModContentPack), "strings");
        private static readonly FieldInfo AllAssetNamesInBundleCachedField = AccessTools.Field(typeof(ModContentPack), "allAssetNamesInBundleCached");
        private static readonly FieldInfo AllAssetNamesInBundleCachedTrieField = AccessTools.Field(typeof(ModContentPack), "allAssetNamesInBundleCachedTrie");
        private static readonly FieldInfo ContentListTrieField = AccessTools.Field(typeof(ModContentHolder<Texture2D>), "contentListTrie");
        private static MethodInfo trieAddMethod;

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ModContentPack), "ReloadContentInt");
        }

        private static bool Prefix(ModContentPack __instance, bool hotReload, ref FastLoaderProfileScope __state)
        {
            if (hotReload)
            {
                return true;
            }

            if (__instance.IsCoreMod || __instance.IsOfficialMod)
            {
                return true;
            }

            __state = FastLoaderProfiler.Scope("FastLoader.TextureReload.Total");

            List<RawTextureEntry> entries;
            string modDescription = FastLoaderProfiler.DescribeMod(__instance);
            using (FastLoaderProfiler.Scope("FastLoader.TextureCache.CacheDecision | " + modDescription))
            {
                if (!TextureRawCache.TryLoadCacheForMod(__instance, out entries))
                {
                    return true;
                }
            }

            using (FastLoaderProfiler.Scope("FastLoader.TextureCache.CachedReload | " + modDescription))
            {
                ModContentHolder<AudioClip> audioClips = AudioClipsField != null ? AudioClipsField.GetValue(__instance) as ModContentHolder<AudioClip> : null;
                ModContentHolder<Texture2D> textures = TexturesField != null ? TexturesField.GetValue(__instance) as ModContentHolder<Texture2D> : null;
                ModContentHolder<string> strings = StringsField != null ? StringsField.GetValue(__instance) as ModContentHolder<string> : null;
                if (audioClips == null || textures == null || strings == null || __instance.assetBundles == null)
                {
                    FastLoaderRuntime.ActivateVanillaFallback(FastLoaderCacheKind.Texture, "texture cache holder unavailable: " + modDescription);
                    return true;
                }

                try
                {
                    ReloadContentWithCachedTextures(__instance, audioClips, textures, strings, entries, hotReload);
                }
                catch (Exception ex)
                {
                    FastLoaderRuntime.ActivateVanillaFallback(FastLoaderCacheKind.Texture, modDescription + " cached texture reload failed: " + ex.GetType().Name);
                    Log.Warning(FastLoaderDebug.FormatExceptionContext(
                        "TextureCache.CachedReload",
                        FastLoaderDebug.DescribeMod(__instance),
                        ex));
                    return true;
                }
            }

            return false;
        }

        private static void Postfix(FastLoaderProfileScope __state)
        {
            if (__state != null)
            {
                __state.Dispose();
            }
        }

        private static void ReloadContentWithCachedTextures(
            ModContentPack mod,
            ModContentHolder<AudioClip> audioClips,
            ModContentHolder<Texture2D> textures,
            ModContentHolder<string> strings,
            List<RawTextureEntry> cachedEntries,
            bool hotReload)
        {
            string modDescription = FastLoaderProfiler.DescribeMod(mod);
            DeepProfiler.Start("Reload audio clips");
            try
            {
                using (FastLoaderProfiler.Scope("FastLoader.TextureCache.AudioReload | " + modDescription))
                {
                    audioClips.ReloadAll(hotReload);
                }
            }
            finally
            {
                DeepProfiler.End();
            }

            DeepProfiler.Start("Reload textures");
            try
            {
                using (FastLoaderProfiler.Scope("FastLoader.TextureCache.InjectRawTextures | " + modDescription))
                {
                    LoadRawCachedTexturesIntoHolder(mod, textures, cachedEntries, hotReload);
                }
            }
            finally
            {
                DeepProfiler.End();
            }

            DeepProfiler.Start("Reload strings");
            try
            {
                using (FastLoaderProfiler.Scope("FastLoader.TextureCache.StringsReload | " + modDescription))
                {
                    strings.ReloadAll(hotReload);
                }
            }
            finally
            {
                DeepProfiler.End();
            }

            DeepProfiler.Start("Reload asset bundles");
            try
            {
                using (FastLoaderProfiler.Scope("FastLoader.TextureCache.AssetBundlesReload | " + modDescription))
                {
                    mod.assetBundles.ReloadAll(hotReload);
                }

                if (AllAssetNamesInBundleCachedField != null)
                {
                    AllAssetNamesInBundleCachedField.SetValue(mod, null);
                }

                if (AllAssetNamesInBundleCachedTrieField != null)
                {
                    AllAssetNamesInBundleCachedTrieField.SetValue(mod, null);
                }
            }
            finally
            {
                DeepProfiler.End();
            }
        }

        private static void LoadRawCachedTexturesIntoHolder(ModContentPack mod, ModContentHolder<Texture2D> holder, List<RawTextureEntry> entries, bool hotReload)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                RawTextureEntry entry = entries[i];
                try
                {
                    if (holder.contentList.ContainsKey(entry.InternalPath))
                    {
                        if (!hotReload)
                        {
                            Log.Warning("[FastLoader] Duplicate texture path from raw cache. " + FastLoaderDebug.DescribeRawTextureEntry(mod, entry));
                        }
                        continue;
                    }

                    Texture2D texture = new Texture2D(entry.Width, entry.Height, (TextureFormat)entry.TextureFormat, entry.MipmapCount > 1);
                    texture.LoadRawTextureData(entry.RawData);
                    texture.name = entry.Name;
                    texture.filterMode = (FilterMode)entry.FilterMode;
                    texture.anisoLevel = entry.AnisoLevel;
                    texture.Apply(false, true);

                    holder.contentList.Add(entry.InternalPath, texture);
                    AddToTrie(holder, entry.InternalPath);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Failed to restore raw cached texture: " + FastLoaderDebug.DescribeRawTextureEntry(mod, entry), ex);
                }
            }
        }

        internal static void AddToTrie(ModContentHolder<Texture2D> holder, string path)
        {
            object trie = ContentListTrieField != null ? ContentListTrieField.GetValue(holder) : null;
            if (trie == null)
            {
                return;
            }

            if (trieAddMethod == null)
            {
                trieAddMethod = AccessTools.Method(trie.GetType(), "Add", new[] { typeof(string) });
            }

            if (trieAddMethod != null)
            {
                trieAddMethod.Invoke(trie, new object[] { path });
            }
        }
    }

    [HarmonyPatch(typeof(LoadedModManager), nameof(LoadedModManager.ClearCachedPatches))]
    internal static class Patch_ClearCachedPatches
    {
        private static bool Prefix(ref ProfileScope __state)
        {
            __state = FastProfile.Scope("LoadedModManager.ClearCachedPatches");
            return !FastLoaderRuntime.IsCacheHit;
        }

        private static void Postfix(ProfileScope __state)
        {
            try
            {
                if (__state != null)
                {
                    __state.Dispose();
                }
            }
            finally
            {
                FastLoaderRuntime.CompleteLoad();
            }
        }
    }

    [HarmonyPatch(typeof(LoadedModManager), nameof(LoadedModManager.ClearDestroy))]
    internal static class Patch_LoadedModManager_ClearDestroy
    {
        private static void Postfix()
        {
            TextureRawCache.OnClearDestroy();
        }
    }

}
