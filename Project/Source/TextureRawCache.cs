using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;

namespace FastLoader
{
    internal static class TextureRawCache
    {
        private static readonly string[] TextureExtensions = new string[] { ".png", ".jpg", ".jpeg", ".psd" };

        private static Dictionary<string, List<RawTextureEntry>> loadedCaches;
        private static int cacheHitCount;
        private static int cacheMissCount;
        private static List<string> cacheMissList;

        private static string CacheRootPath
        {
            get { return Path.Combine(GenFilePaths.ConfigFolderPath, "FastLoader", "TextureCache"); }
        }

        public static bool TryLoadCacheForMod(ModContentPack mod, out List<RawTextureEntry> entries)
        {
            entries = null;

            if (mod == null || mod.IsCoreMod || mod.IsOfficialMod)
            {
                return false;
            }

            if (FastLoaderRuntime.Settings != null && !FastLoaderRuntime.Settings.CacheEnabled)
            {
                return false;
            }

            string packageId = NormalizePackageId(mod.PackageId);

            if (loadedCaches != null && loadedCaches.TryGetValue(packageId, out entries))
            {
                cacheHitCount++;
                return entries != null && entries.Count > 0;
            }

            string hash = ComputeModHash(mod);
            string cachePath = GetCachePathForMod(packageId);

            TextureCacheGroup group = TextureCacheGroupManager.FindGroupForMod(packageId);
            if (group != null)
            {
                cachePath = GetCachePathForGroup(group.GroupId);
                hash = ComputeGroupHash(group);
            }

            List<RawTextureEntry> loaded;
            bool hit;
            using (FastProfile.Scope("TextureRawCache: TryRead [" + packageId + "]"))
            {
                hit = TextureCacheFile.TryRead(cachePath, hash, out loaded);
            }

            if (hit && loaded != null && loaded.Count > 0)
            {
                if (loadedCaches == null)
                {
                    loadedCaches = new Dictionary<string, List<RawTextureEntry>>(StringComparer.OrdinalIgnoreCase);
                }

                if (group != null)
                {
                    AssignGroupEntriesToMods(group, loaded);
                    loadedCaches.TryGetValue(packageId, out entries);
                }
                else
                {
                    loadedCaches[packageId] = loaded;
                    entries = loaded;
                }

                cacheHitCount++;
                return entries != null && entries.Count > 0;
            }

            cacheMissCount++;
            if (cacheMissList == null)
            {
                cacheMissList = new List<string>();
            }
            cacheMissList.Add(packageId);
            return false;
        }

        public static void SaveCacheForMod(ModContentPack mod, List<RawTextureEntry> entries)
        {
            if (mod == null || entries == null || entries.Count == 0)
            {
                return;
            }

            string packageId = NormalizePackageId(mod.PackageId);
            string hash = ComputeModHash(mod);
            string cachePath = GetCachePathForMod(packageId);

            try
            {
                using (FastProfile.Scope("TextureRawCache: Write [" + packageId + "]"))
                {
                    TextureCacheFile.Write(cachePath, hash, entries);
                }

                Log.Message("[FastLoader] Texture raw cache saved for " + packageId + ". Entries: " + entries.Count);
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to save texture raw cache for " + packageId + ".\n" + ex);
            }
        }

        public static void SaveCacheForGroup(TextureCacheGroup group, List<RawTextureEntry> entries)
        {
            if (group == null || entries == null || entries.Count == 0)
            {
                return;
            }

            string hash = ComputeGroupHash(group);
            string cachePath = GetCachePathForGroup(group.GroupId);

            try
            {
                using (FastProfile.Scope("TextureRawCache: Write group [" + group.GroupId + "]"))
                {
                    TextureCacheFile.Write(cachePath, hash, entries);
                }

                Log.Message("[FastLoader] Texture raw cache saved for group " + group.GroupName + ". Entries: " + entries.Count);
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to save texture raw cache for group " + group.GroupName + ".\n" + ex);
            }
        }

        public static string ComputeModHash(ModContentPack mod)
        {
            using (SHA256 sha = SHA256.Create())
            {
                AppendHash(sha, FastLoaderRuntime.FastLoaderVersion);
                AppendHash(sha, VersionControl.CurrentVersionStringWithRev ?? string.Empty);
                AppendHash(sha, mod.PackageId ?? string.Empty);
                AppendHash(sha, mod.Name ?? string.Empty);
                AppendHash(sha, mod.RootDir ?? string.Empty);

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BytesToHex(sha.Hash);
            }
        }

        public static string ComputeGroupHash(TextureCacheGroup group)
        {
            if (group == null || group.PackageIds == null)
            {
                return string.Empty;
            }

            using (SHA256 sha = SHA256.Create())
            {
                AppendHash(sha, FastLoaderRuntime.FastLoaderVersion);
                AppendHash(sha, VersionControl.CurrentVersionStringWithRev ?? string.Empty);
                AppendHash(sha, group.GroupId ?? string.Empty);
                AppendHash(sha, group.PackageIds.Count.ToString());

                for (int i = 0; i < group.PackageIds.Count; i++)
                {
                    AppendHash(sha, group.PackageIds[i] ?? string.Empty);
                    ModContentPack mod = FastLoaderRuntime.FindMod(group.PackageIds[i]);
                    if (mod != null)
                    {
                        AppendHash(sha, mod.Name ?? string.Empty);
                        AppendHash(sha, mod.RootDir ?? string.Empty);
                    }
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BytesToHex(sha.Hash);
            }
        }

        public static void DeleteAll()
        {
            loadedCaches = null;
            cacheHitCount = 0;
            cacheMissCount = 0;

            string root = CacheRootPath;
            if (!Directory.Exists(root))
            {
                return;
            }

            try
            {
                string[] files = Directory.GetFiles(root, "*.texcache", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    TryDeleteFile(files[i]);
                }

                TryDeleteFile(Path.Combine(root, "groups.xml"));
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to delete texture cache files.\n" + ex);
            }
        }

        public static void DeleteForMod(string packageId)
        {
            string path = GetCachePathForMod(NormalizePackageId(packageId));
            TryDeleteFile(path);

            if (loadedCaches != null)
            {
                loadedCaches.Remove(NormalizePackageId(packageId));
            }
        }

        public static void OnClearDestroy()
        {
            loadedCaches = null;
            cacheHitCount = 0;
            cacheMissCount = 0;
            cacheMissList = null;
        }

        public static string GetStatusSummary()
        {
            if (!Directory.Exists(CacheRootPath))
            {
                return "no cache files";
            }

            try
            {
                string[] files = Directory.GetFiles(CacheRootPath, "*.texcache", SearchOption.TopDirectoryOnly);
                long totalSize = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    FileInfo info = new FileInfo(files[i]);
                    totalSize += info.Length;
                }

                return files.Length + " files, " + FormatSize(totalSize) + " (hits: " + cacheHitCount + ", misses: " + cacheMissCount + ")";
            }
            catch
            {
                return "unable to read cache status";
            }
        }

        public static List<string> GetCacheMissList()
        {
            return cacheMissList;
        }

        public static bool IsTextureExtension(string extension)
        {
            for (int i = 0; i < TextureExtensions.Length; i++)
            {
                if (string.Equals(extension, TextureExtensions[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static int RebuildFromLoadedMods()
        {
            List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
            int totalSaved = 0;

            for (int i = 0; i < mods.Count; i++)
            {
                ModContentPack mod = mods[i];
                if (mod == null || mod.IsCoreMod || mod.IsOfficialMod)
                {
                    continue;
                }

                ModContentHolder<UnityEngine.Texture2D> holder = TexturesField != null
                    ? TexturesField.GetValue(mod) as ModContentHolder<UnityEngine.Texture2D>
                    : null;

                if (holder == null || holder.contentList == null || holder.contentList.Count == 0)
                {
                    continue;
                }

                List<RawTextureEntry> entries = new List<RawTextureEntry>();
                foreach (KeyValuePair<string, UnityEngine.Texture2D> kvp in holder.contentList)
                {
                    UnityEngine.Texture2D tex = kvp.Value;
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
                        Log.Warning("[FastLoader] Rebuild: failed to read texture '" + kvp.Key + "': " + ex.Message);
                    }
                }

                if (entries.Count > 0)
                {
                    SaveCacheForMod(mod, entries);
                    totalSaved += entries.Count;
                }
            }

            return totalSaved;
        }

        private static byte[] ReadTextureRawFromGPU(UnityEngine.Texture2D source, out int resultFormat)
        {
            UnityEngine.RenderTexture rt = UnityEngine.RenderTexture.GetTemporary(
                source.width, source.height, 0,
                UnityEngine.RenderTextureFormat.Default,
                UnityEngine.RenderTextureReadWrite.sRGB);

            UnityEngine.Graphics.Blit(source, rt);
            UnityEngine.RenderTexture previous = UnityEngine.RenderTexture.active;
            UnityEngine.RenderTexture.active = rt;

            UnityEngine.Texture2D readable = new UnityEngine.Texture2D(
                source.width, source.height,
                UnityEngine.TextureFormat.RGBA32, false);
            readable.ReadPixels(new UnityEngine.Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply(false, false);

            UnityEngine.RenderTexture.active = previous;
            UnityEngine.RenderTexture.ReleaseTemporary(rt);

            readable.Compress(true);
            readable.Apply(false, false);

            resultFormat = (int)readable.format;
            byte[] rawData = readable.GetRawTextureData();
            UnityEngine.Object.Destroy(readable);
            return rawData;
        }

        private static readonly System.Reflection.FieldInfo TexturesField =
            HarmonyLib.AccessTools.Field(typeof(ModContentPack), "textures");

        private static void AssignGroupEntriesToMods(TextureCacheGroup group, List<RawTextureEntry> allEntries)
        {
            if (loadedCaches == null)
            {
                loadedCaches = new Dictionary<string, List<RawTextureEntry>>(StringComparer.OrdinalIgnoreCase);
            }

            for (int i = 0; i < group.PackageIds.Count; i++)
            {
                string pid = NormalizePackageId(group.PackageIds[i]);
                if (!loadedCaches.ContainsKey(pid))
                {
                    loadedCaches[pid] = new List<RawTextureEntry>();
                }
            }

            for (int i = 0; i < allEntries.Count; i++)
            {
                RawTextureEntry entry = allEntries[i];
                string ownerPid = ExtractPackageIdFromPath(entry.InternalPath, group);
                if (!string.IsNullOrEmpty(ownerPid))
                {
                    List<RawTextureEntry> list;
                    if (loadedCaches.TryGetValue(ownerPid, out list))
                    {
                        list.Add(entry);
                    }
                }
            }
        }

        private static string ExtractPackageIdFromPath(string internalPath, TextureCacheGroup group)
        {
            if (string.IsNullOrEmpty(internalPath) || group == null || group.PackageIds == null)
            {
                return null;
            }

            int separatorIdx = internalPath.IndexOf('|');
            if (separatorIdx > 0)
            {
                return NormalizePackageId(internalPath.Substring(0, separatorIdx));
            }

            if (group.PackageIds.Count == 1)
            {
                return NormalizePackageId(group.PackageIds[0]);
            }

            return null;
        }

        private static string GetCachePathForMod(string normalizedPackageId)
        {
            string safeId = SanitizeFileName(normalizedPackageId);
            return Path.Combine(CacheRootPath, "mod_" + safeId + ".texcache");
        }

        private static string GetCachePathForGroup(string groupId)
        {
            string safeId = SanitizeFileName(groupId ?? "default");
            return Path.Combine(CacheRootPath, "group_" + safeId + ".texcache");
        }

        private static string NormalizePackageId(string packageId)
        {
            return (packageId ?? string.Empty).ToLowerInvariant();
        }

        private static string SanitizeFileName(string name)
        {
            StringBuilder sb = new StringBuilder(name.Length);
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

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void AppendHash(HashAlgorithm sha, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > 0)
            {
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }

            sha.TransformBlock(new byte[] { 0 }, 0, 1, null, 0);
        }

        private static string BytesToHex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }

            return sb.ToString();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024.0).ToString("0.0") + " KB";
            }

            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        }
    }
}
