using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FastLoader
{
    [HarmonyPatch(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML))]
    internal static class Patch_LoadModXML
    {
        private static bool Prefix(bool hotReload, ref List<LoadableXmlAsset> __result, ref ProfileScope __state)
        {
            FastLoaderRuntime.BeginLoad(hotReload);
            __state = FastProfile.Scope("LoadedModManager.LoadModXML");

            if (FastLoaderRuntime.IsCacheHit)
            {
                __result = new List<LoadableXmlAsset>();
                return false;
            }

            return true;
        }

        private static void Postfix(ProfileScope __state)
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
        private static bool Prefix(ref XmlDocument __result, ref ProfileScope __state)
        {
            __state = FastProfile.Scope("LoadedModManager.CombineIntoUnifiedXML");
            if (FastLoaderRuntime.IsCacheHit)
            {
                __result = FastLoaderRuntime.CachedResolvedDefs;
                return false;
            }

            return true;
        }

        private static void Postfix(ProfileScope __state)
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
        private static bool Prefix(ref ProfileScope __state)
        {
            __state = FastProfile.Scope("LoadedModManager.ErrorCheckPatches");
            return !FastLoaderRuntime.IsCacheHit;
        }

        private static void Postfix(ProfileScope __state)
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
        private static bool Prefix(ref ProfileScope __state)
        {
            __state = FastProfile.Scope("LoadedModManager.ApplyPatches");
            return !FastLoaderRuntime.IsCacheHit;
        }

        private static void Postfix(ProfileScope __state)
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
            public ProfileScope Scope;
            public bool UsedCache;
            public int ParsedDefCount;
        }

        private static bool Prefix(XmlDocument xmlDoc, bool hotReload, ref ParseState __state)
        {
            __state = new ParseState
            {
                Scope = FastProfile.Scope("LoadedModManager.ParseAndProcessXML")
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
        private static readonly FieldInfo HolderModField = AccessTools.Field(typeof(ModContentHolder<Texture2D>), "mod");
        private static readonly FieldInfo ContentListTrieField = AccessTools.Field(typeof(ModContentHolder<Texture2D>), "contentListTrie");
        private static MethodInfo trieAddMethod;

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ModContentPack), "ReloadContentInt");
        }

        private static bool Prefix(ModContentPack __instance, bool hotReload)
        {
            if (hotReload)
            {
                return true;
            }

            IEnumerable<Pair<string, LoadedContentItem<Texture2D>>> enumerableItems;
            if (!FastLoaderAssetBundleCache.TryGetTextureItemsForMod(__instance, out enumerableItems))
            {
                return true;
            }

            List<Pair<string, LoadedContentItem<Texture2D>>> cachedTextureItems;
            using (FastProfile.Scope("Texture cache: enumerate cached Texture2D LoadAsset"))
            {
                cachedTextureItems = new List<Pair<string, LoadedContentItem<Texture2D>>>(enumerableItems);
            }

            if (cachedTextureItems.Count == 0)
            {
                return true;
            }

            ModContentHolder<AudioClip> audioClips = AudioClipsField != null ? AudioClipsField.GetValue(__instance) as ModContentHolder<AudioClip> : null;
            ModContentHolder<Texture2D> textures = TexturesField != null ? TexturesField.GetValue(__instance) as ModContentHolder<Texture2D> : null;
            ModContentHolder<string> strings = StringsField != null ? StringsField.GetValue(__instance) as ModContentHolder<string> : null;
            if (audioClips == null || textures == null || strings == null || __instance.assetBundles == null)
            {
                return true;
            }

            ReloadContentWithCachedTextures(__instance, audioClips, textures, strings, cachedTextureItems, hotReload);
            return false;
        }

        private static void ReloadContentWithCachedTextures(
            ModContentPack mod,
            ModContentHolder<AudioClip> audioClips,
            ModContentHolder<Texture2D> textures,
            ModContentHolder<string> strings,
            List<Pair<string, LoadedContentItem<Texture2D>>> cachedTextureItems,
            bool hotReload)
        {
            DeepProfiler.Start("Reload audio clips");
            try
            {
                using (FastProfile.Scope("ReloadContentInt: vanilla audio reload"))
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
                using (FastProfile.Scope("ReloadContentInt: inject cached textures"))
                {
                    LoadCachedTexturesIntoHolder(textures, cachedTextureItems, hotReload);
                }
            }
            finally
            {
                DeepProfiler.End();
            }

            DeepProfiler.Start("Reload strings");
            try
            {
                using (FastProfile.Scope("ReloadContentInt: vanilla strings reload"))
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
                using (FastProfile.Scope("ReloadContentInt: vanilla assetBundles reload"))
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

        private static void LoadCachedTexturesIntoHolder(ModContentHolder<Texture2D> holder, IEnumerable<Pair<string, LoadedContentItem<Texture2D>>> items, bool hotReload)
        {
            foreach (Pair<string, LoadedContentItem<Texture2D>> item in items)
            {
                string internalPath = NormalizeInternalPath(item.First);
                if (holder.contentList.ContainsKey(internalPath))
                {
                    if (!hotReload)
                    {
                        Log.Warning("Tried to load duplicate " + typeof(Texture2D) + " with path: " + item.Second.internalFile + " and internal path: " + internalPath);
                    }

                    continue;
                }

                holder.contentList.Add(internalPath, item.Second.contentItem);
                AddToTrie(holder, internalPath);
                if (item.Second.extraDisposable != null)
                {
                    holder.extraDisposables.Add(item.Second.extraDisposable);
                }
            }
        }

        private static string NormalizeInternalPath(string path)
        {
            string result = (path ?? string.Empty).Replace('\\', '/');
            string contentPath = GenFilePaths.ContentPath<Texture2D>();
            if (result.StartsWith(contentPath))
            {
                result = result.Substring(contentPath.Length);
            }

            string extension = Path.GetExtension(result);
            if (!string.IsNullOrEmpty(extension) && result.EndsWith(extension))
            {
                result = result.Substring(0, result.Length - extension.Length);
            }

            return result;
        }

        private static void AddToTrie(ModContentHolder<Texture2D> holder, string path)
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
            FastLoaderAssetBundleCache.ClearLoadedBundle();
        }
    }

}
