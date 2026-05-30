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
