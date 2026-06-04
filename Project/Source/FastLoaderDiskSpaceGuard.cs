using System;
using System.IO;
using Verse;

namespace FastLoader
{
    internal static class FastLoaderDiskSpaceGuard
    {
        private const long BytesPerMegabyte = 1024L * 1024L;
        private const long MinimumReserveBytes = 1024L * BytesPerMegabyte;
        private const long MinimumUnknownBuildBytes = 2L * 1024L * BytesPerMegabyte;
        private const long XmlLanguageReserveBytes = 256L * BytesPerMegabyte;

        public static bool TryGetInsufficientSpaceMessage(out string message)
        {
            message = null;

            try
            {
                string root = Path.Combine(GenFilePaths.ConfigFolderPath, "FastLoader");
                string fullRoot = Path.GetFullPath(root);
                string driveRoot = Path.GetPathRoot(fullRoot);
                if (string.IsNullOrEmpty(driveRoot))
                {
                    return false;
                }

                long existingCacheBytes = GetExistingCacheBytes(root);
                long estimatedBuildBytes = EstimateBuildBytes(existingCacheBytes);
                long availableAfterCleanup = GetAvailableBytes(driveRoot) + existingCacheBytes;
                long requiredBytes = estimatedBuildBytes + MinimumReserveBytes;

                if (availableAfterCleanup >= requiredBytes)
                {
                    return false;
                }

                message =
                    "Not enough disk space to build FastLoader caches.\n" +
                    "Build Cache was not started, and existing cache files were left untouched.\n\n" +
                    "Available after removing old cache: " + FormatBytes(availableAfterCleanup) + "\n" +
                    "Estimated cache build requirement: " + FormatBytes(estimatedBuildBytes) + "\n" +
                    "Required safety reserve: " + FormatBytes(MinimumReserveBytes) + "\n" +
                    "Target drive: " + driveRoot;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Disk space check failed. Build Cache will continue without preflight capacity validation.\n" + ex);
                return false;
            }
        }

        private static long EstimateBuildBytes(long existingCacheBytes)
        {
            long estimated = XmlLanguageReserveBytes;
            estimated = SafeAdd(estimated, TextureRawCache.EstimateBuildCacheBytes());
            estimated = SafeAdd(estimated, StaticAtlasCache.EstimateBuildCacheBytes());

            if (existingCacheBytes > estimated)
            {
                estimated = existingCacheBytes;
            }

            if (estimated < MinimumUnknownBuildBytes)
            {
                estimated = MinimumUnknownBuildBytes;
            }

            return estimated;
        }

        private static long GetExistingCacheBytes(string root)
        {
            if (!Directory.Exists(root))
            {
                return 0L;
            }

            long total = 0L;
            string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                if (!IsFastLoaderCacheFile(path))
                {
                    continue;
                }

                try
                {
                    total = SafeAdd(total, new FileInfo(path).Length);
                }
                catch
                {
                }
            }

            return total;
        }

        private static bool IsFastLoaderCacheFile(string path)
        {
            string extension = Path.GetExtension(path);
            string fileName = Path.GetFileName(path);
            return string.Equals(fileName, "manifest.xml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "resolved_defs.xml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "cache_state.xml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".texcache", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".flang", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".finj", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".atlascache", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
        }

        private static long GetAvailableBytes(string driveRoot)
        {
            DriveInfo drive = new DriveInfo(driveRoot);
            return drive.AvailableFreeSpace;
        }

        private static long SafeAdd(long left, long right)
        {
            if (right <= 0L)
            {
                return left;
            }

            if (long.MaxValue - left < right)
            {
                return long.MaxValue;
            }

            return left + right;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < BytesPerMegabyte)
            {
                return (bytes / 1024.0).ToString("0.0") + " KB";
            }

            if (bytes < 1024L * BytesPerMegabyte)
            {
                return (bytes / (double)BytesPerMegabyte).ToString("0.0") + " MB";
            }

            return (bytes / (1024.0 * BytesPerMegabyte)).ToString("0.00") + " GB";
        }
    }
}
