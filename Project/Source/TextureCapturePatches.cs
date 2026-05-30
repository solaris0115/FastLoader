using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FastLoader
{
    /// <summary>
    /// Cache miss 시 바닐라 텍스처 로드 결과를 캡처하여 .texcache 파일로 저장하는 패치.
    /// 
    /// 전략: ModContentHolder<Texture2D>.ReloadAll을 Prefix+Postfix로 감싼다.
    /// - Prefix: 캡처 모드 활성화 (현재 모드 기록)
    /// - 바닐라 LoadTexture 실행 중: Texture2D.Apply를 가로채서 raw data 캡처
    /// - Postfix: 캡처된 데이터를 .texcache로 flush
    /// 
    /// Texture2D.Apply(bool, bool) Prefix에서 makeNoLongerReadable=true 호출 직전
    /// GetRawTextureData()로 바이트를 복사한다.
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_Texture2DApply_Capture
    {
        [ThreadStatic]
        private static bool captureActive;

        [ThreadStatic]
        private static List<RawTextureEntry> capturedEntries;

        [ThreadStatic]
        private static ModContentPack currentCaptureMod;

        [ThreadStatic]
        private static string currentTextureInternalPath;

        private static readonly FieldInfo TexturesField = AccessTools.Field(typeof(ModContentPack), "textures");

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Texture2D), "Apply", new[] { typeof(bool), typeof(bool) });
        }

        private static void Prefix(Texture2D __instance, bool updateMipmaps, bool makeNoLongerReadable)
        {
            if (!captureActive || !makeNoLongerReadable)
            {
                return;
            }

            try
            {
                byte[] rawData = __instance.GetRawTextureData();
                if (rawData == null || rawData.Length == 0)
                {
                    return;
                }

                RawTextureEntry entry = new RawTextureEntry
                {
                    InternalPath = currentTextureInternalPath ?? __instance.name ?? string.Empty,
                    Name = __instance.name ?? string.Empty,
                    Width = __instance.width,
                    Height = __instance.height,
                    TextureFormat = (int)__instance.format,
                    MipmapCount = __instance.mipmapCount,
                    FilterMode = (int)__instance.filterMode,
                    AnisoLevel = __instance.anisoLevel,
                    RawData = rawData
                };

                if (capturedEntries == null)
                {
                    capturedEntries = new List<RawTextureEntry>();
                }

                capturedEntries.Add(entry);
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to capture texture raw data: " + __instance.name + "\n" + ex.Message);
            }
        }

        public static void BeginCapture(ModContentPack mod)
        {
            captureActive = true;
            currentCaptureMod = mod;
            capturedEntries = new List<RawTextureEntry>();
            currentTextureInternalPath = null;
        }

        public static void SetCurrentTexturePath(string internalPath)
        {
            currentTextureInternalPath = internalPath;
        }

        public static void EndCapture()
        {
            if (!captureActive)
            {
                return;
            }

            captureActive = false;
            ModContentPack mod = currentCaptureMod;
            List<RawTextureEntry> entries = capturedEntries;
            currentCaptureMod = null;
            capturedEntries = null;
            currentTextureInternalPath = null;

            if (mod == null || entries == null || entries.Count == 0)
            {
                return;
            }

            FixupInternalPaths(mod, entries);
            TextureRawCache.SaveCacheForMod(mod, entries);
        }

        public static bool IsCaptureActive
        {
            get { return captureActive; }
        }

        private static void FixupInternalPaths(ModContentPack mod, List<RawTextureEntry> entries)
        {
            ModContentHolder<Texture2D> holder = TexturesField != null ? TexturesField.GetValue(mod) as ModContentHolder<Texture2D> : null;
            if (holder == null)
            {
                return;
            }

            Dictionary<string, string> nameToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Texture2D> kvp in holder.contentList)
            {
                if (kvp.Value != null && !string.IsNullOrEmpty(kvp.Value.name))
                {
                    nameToPath[kvp.Value.name] = kvp.Key;
                }
            }

            for (int i = 0; i < entries.Count; i++)
            {
                RawTextureEntry entry = entries[i];
                string resolvedPath;
                if (!string.IsNullOrEmpty(entry.Name) && nameToPath.TryGetValue(entry.Name, out resolvedPath))
                {
                    entry.InternalPath = resolvedPath;
                }
            }
        }
    }

    /// <summary>
    /// ModContentHolder<Texture2D>.ReloadAll에 대한 패치.
    /// Cache miss 시에만 동작하여 캡처를 시작/종료한다.
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_ModContentHolderTexture2D_ReloadAll_Capture
    {
        private static readonly FieldInfo HolderModField = AccessTools.Field(typeof(ModContentHolder<Texture2D>), "mod");

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ModContentHolder<Texture2D>), "ReloadAll");
        }

        private static void Prefix(ModContentHolder<Texture2D> __instance)
        {
            if (FastLoaderRuntime.Settings != null && !FastLoaderRuntime.Settings.CacheEnabled)
            {
                return;
            }

            ModContentPack mod = HolderModField != null ? HolderModField.GetValue(__instance) as ModContentPack : null;
            if (mod == null || mod.IsCoreMod || mod.IsOfficialMod)
            {
                return;
            }

            Patch_Texture2DApply_Capture.BeginCapture(mod);
        }

        private static void Postfix()
        {
            if (Patch_Texture2DApply_Capture.IsCaptureActive)
            {
                Patch_Texture2DApply_Capture.EndCapture();
            }
        }
    }
}
