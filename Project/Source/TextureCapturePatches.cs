using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FastLoader
{
    /// <summary>
    /// Cache miss 시 바닐라 로딩 완료 후 GPU readback으로 텍스처를 캡처하여 .texcache로 저장.
    /// 
    /// ReloadContentInt의 Postfix에서 로드된 Texture2D를 GPU에서 읽어온다.
    /// ModContentHolder&lt;T&gt;.ReloadAll을 패치하지 않으므로
    /// AudioClip/string 등 다른 타입의 로딩 경로에 영향을 주지 않는다.
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_ReloadContentInt_TextureCapture
    {
        [ThreadStatic]
        private static ModContentPack pendingCaptureMod;

        private static readonly FieldInfo TexturesField = AccessTools.Field(typeof(ModContentPack), "textures");

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ModContentPack), "ReloadContentInt");
        }

        private static void Prefix(ModContentPack __instance, bool hotReload)
        {
            pendingCaptureMod = null;

            if (hotReload)
            {
                return;
            }

            if (__instance == null || __instance.IsCoreMod || __instance.IsOfficialMod)
            {
                return;
            }

            if (FastLoaderRuntime.Settings != null && !FastLoaderRuntime.Settings.CacheEnabled)
            {
                return;
            }

            pendingCaptureMod = __instance;
        }

        private static void Postfix(ModContentPack __instance, bool hotReload)
        {
            ModContentPack mod = pendingCaptureMod;
            pendingCaptureMod = null;

            if (mod == null || mod != __instance)
            {
                return;
            }

            try
            {
                CaptureAndSave(mod);
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Texture capture failed for " + (mod.PackageId ?? "unknown") + ": " + ex.Message);
            }
        }

        private static void CaptureAndSave(ModContentPack mod)
        {
            ModContentHolder<Texture2D> holder = TexturesField != null
                ? TexturesField.GetValue(mod) as ModContentHolder<Texture2D>
                : null;

            if (holder == null || holder.contentList == null || holder.contentList.Count == 0)
            {
                return;
            }

            string packageId = (mod.PackageId ?? string.Empty).ToLowerInvariant();
            string cachePath = System.IO.Path.Combine(
                GenFilePaths.ConfigFolderPath, "FastLoader", "TextureCache",
                "mod_" + SanitizeFileName(packageId) + ".texcache");

            if (System.IO.File.Exists(cachePath))
            {
                return;
            }

            List<RawTextureEntry> entries = new List<RawTextureEntry>();

            foreach (KeyValuePair<string, Texture2D> kvp in holder.contentList)
            {
                Texture2D tex = kvp.Value;
                if (tex == null)
                {
                    continue;
                }

                try
                {
                    int capturedFormat;
                    byte[] rawData = ReadTextureRawFromGPU(tex, out capturedFormat);
                    if (rawData == null || rawData.Length == 0)
                    {
                        continue;
                    }

                    entries.Add(new RawTextureEntry
                    {
                        InternalPath = kvp.Key,
                        Name = tex.name ?? string.Empty,
                        Width = tex.width,
                        Height = tex.height,
                        TextureFormat = capturedFormat,
                        MipmapCount = 1,
                        FilterMode = (int)tex.filterMode,
                        AnisoLevel = tex.anisoLevel,
                        RawData = rawData
                    });
                }
                catch (Exception ex)
                {
                    Log.Warning("[FastLoader] Capture: failed to read texture '" + kvp.Key + "': " + ex.Message);
                }
            }

            if (entries.Count > 0)
            {
                TextureRawCache.SaveCacheForMod(mod, entries);
            }
        }

        private static byte[] ReadTextureRawFromGPU(Texture2D source, out int resultFormat)
        {
            RenderTexture rt = RenderTexture.GetTemporary(
                source.width, source.height, 0,
                RenderTextureFormat.Default,
                RenderTextureReadWrite.sRGB);

            Graphics.Blit(source, rt);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D readable = new Texture2D(
                source.width, source.height,
                TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply(false, false);

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            readable.Compress(true);
            readable.Apply(false, false);

            resultFormat = (int)readable.format;
            byte[] rawData = readable.GetRawTextureData();
            UnityEngine.Object.Destroy(readable);
            return rawData;
        }

        private static string SanitizeFileName(string name)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('_');
                }
            }
            return sb.ToString();
        }
    }
}
