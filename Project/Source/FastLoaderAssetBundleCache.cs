using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml;
using RimWorld;
using UnityEngine;
using Verse;

namespace FastLoader
{
    internal sealed class AssetBundleCacheEntry
    {
        public string PackageId;
        public string PackageIdPlayerFacing;
        public string InternalPath;
        public string SourceFullPath;
        public string AssetPath;
        public string ObjectName;
        public string BundleFileName;
    }

    internal sealed class AssetBundleCacheData
    {
        public string InputHash;
        public string CreatedUtc;
        public string BundleFileName;
        public List<string> BundleFiles = new List<string>();
        public List<AssetBundleCacheEntry> Entries = new List<AssetBundleCacheEntry>();
    }

    internal static class FastLoaderAssetBundleCache
    {
        private static readonly bool AssetBundleCacheEnabled = true;
        private const string CacheFormatVersion = "1";
        private const string BundleFileName = "fastloader_assets_win";
        private const string TextureTypeName = "Texture2D";
        private const int MaxEntriesPerBundle = 1000;

        private static Dictionary<string, AssetBundle> loadedBundles;
        private static AssetBundleCacheData loadedManifest;
        private static Dictionary<string, List<AssetBundleCacheEntry>> entriesByPackageId;
        private static string activeInputHash;
        private static bool cacheAllowedThisRun;
        private static bool cacheUsableThisRun;
        private static bool loadAttemptedThisRun;
        private static bool buildQueuedThisRun;

        private static readonly string[] TextureExtensions = new string[]
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".psd"
        };

        private static string RootPath
        {
            get { return Path.Combine(GenFilePaths.ConfigFolderPath, "FastLoader"); }
        }

        private static string CachePath
        {
            get { return Path.Combine(RootPath, "AssetBundleCache"); }
        }

        private static string BundleOutputPath
        {
            get { return Path.Combine(CachePath, BundleFileName); }
        }

        private static string ManifestPath
        {
            get { return Path.Combine(CachePath, "asset_manifest.xml"); }
        }

        private static string BuildInputPath
        {
            get { return Path.Combine(CachePath, "asset_build_input.xml"); }
        }

        private static string BuildLogPath
        {
            get { return Path.Combine(CachePath, "asset_bundle_build.log"); }
        }

        private static string BuildCommandPath
        {
            get { return Path.Combine(CachePath, "run_asset_bundle_build.cmd"); }
        }

        private static string BuildPowerShellPath
        {
            get { return Path.Combine(CachePath, "run_asset_bundle_build.ps1"); }
        }

        private static string BuildProjectPath
        {
            get { return Path.Combine(CachePath, "UnityBuildProject"); }
        }

        public static bool IsReady
        {
            get { return loadedBundles != null && entriesByPackageId != null; }
        }

        public static void Begin(string inputHash, bool cacheAllowed)
        {
            if (!AssetBundleCacheEnabled)
            {
                ClearLoadedBundle();
                return;
            }

            if (loadedBundles != null)
            {
                ClearLoadedBundle();
            }

            activeInputHash = inputHash;
            cacheAllowedThisRun = cacheAllowed;
            cacheUsableThisRun = false;
            loadAttemptedThisRun = false;
            buildQueuedThisRun = false;
            loadedManifest = null;
            entriesByPackageId = null;

            if (!cacheAllowed)
            {
                return;
            }
        }

        public static bool TryGetTextureItemsForMod(ModContentPack mod, out IEnumerable<Pair<string, LoadedContentItem<Texture2D>>> items)
        {
            items = null;
            if (!AssetBundleCacheEnabled)
            {
                return false;
            }

            if (ShouldSkipTextureBundleForMod(mod))
            {
                return false;
            }

            if (!EnsureLoadedForContentReload())
            {
                return false;
            }

            List<AssetBundleCacheEntry> entries;
            using (FastProfile.Scope("Texture cache: package index lookup"))
            {
                if (!entriesByPackageId.TryGetValue(NormalizePackageId(mod.PackageId), out entries))
                {
                    return false;
                }
            }

            if (entries.Count == 0)
            {
                return false;
            }

            items = EnumerateTextureItems(entries);
            return true;
        }

        private static bool EnsureLoadedForContentReload()
        {
            if (cacheUsableThisRun && loadedBundles != null && entriesByPackageId != null)
            {
                return true;
            }

            if (!cacheAllowedThisRun || loadAttemptedThisRun)
            {
                return false;
            }

            loadAttemptedThisRun = true;
            DateTime startedUtc = DateTime.UtcNow;

            string missReason;
            AssetBundleCacheData manifest;
            Dictionary<string, AssetBundle> bundles;
            using (FastProfile.Scope("Texture cache: try load AssetBundles"))
            {
                if (TryLoad(activeInputHash, out manifest, out bundles, out missReason))
                {
                    loadedManifest = manifest;
                    loadedBundles = bundles;
                    using (FastProfile.Scope("Texture cache: build package index"))
                    {
                        entriesByPackageId = BuildPackageIndex(manifest);
                    }

                    cacheUsableThisRun = true;
                    Log.Message("[FastLoader] Texture AssetBundle cache hit in " + FormatSeconds(DateTime.UtcNow - startedUtc) + ". Entries: " + manifest.Entries.Count);
                    return true;
                }
            }

            Log.Message("[FastLoader] Texture AssetBundle cache miss in " + FormatSeconds(DateTime.UtcNow - startedUtc) + ". Reason: " + missReason);
            return false;
        }

        public static void QueueBuildIfNeeded(string inputHash)
        {
            if (!AssetBundleCacheEnabled)
            {
                return;
            }

            if (buildQueuedThisRun || string.IsNullOrEmpty(inputHash))
            {
                return;
            }

            buildQueuedThisRun = true;
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                LongEventHandler.ExecuteWhenFinished(delegate
                {
                    QueueBuildIfNeededNow(inputHash);
                });
            });
        }

        public static void BuildFromSettings(string inputHash)
        {
            if (!AssetBundleCacheEnabled)
            {
                Log.Message("[FastLoader] Texture AssetBundle cache is disabled.");
                return;
            }

            if (string.IsNullOrEmpty(inputHash))
            {
                Log.Warning("[FastLoader] Cannot build texture AssetBundle cache because input hash is empty.");
                return;
            }

            DeleteCache();
            StartExternalBuild(inputHash);
        }

        public static string PrepareExternalBuildFromSettings(string inputHash)
        {
            if (!AssetBundleCacheEnabled)
            {
                Log.Message("[FastLoader] Texture AssetBundle cache is disabled.");
                return null;
            }

            if (string.IsNullOrEmpty(inputHash))
            {
                Log.Warning("[FastLoader] Cannot prepare texture AssetBundle cache because input hash is empty.");
                return null;
            }

            DeleteCache();
            return PrepareExternalBuild(inputHash);
        }

        private static void QueueBuildIfNeededNow(string inputHash)
        {
            try
            {
                if (IsReady)
                {
                    return;
                }

                string missReason;
                AssetBundleCacheData manifest;
                Dictionary<string, AssetBundle> bundles;
                if (TryLoad(inputHash, out manifest, out bundles, out missReason))
                {
                    if (loadedBundles == null)
                    {
                        loadedBundles = bundles;
                        loadedManifest = manifest;
                        entriesByPackageId = BuildPackageIndex(manifest);
                        cacheUsableThisRun = true;
                    }
                    else
                    {
                        UnloadBundles(bundles);
                    }
                    return;
                }

                StartExternalBuild(inputHash);
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to queue texture AssetBundle build.\n" + ex);
            }
        }

        public static void ClearLoadedBundle()
        {
            if (loadedBundles != null)
            {
                UnloadBundles(loadedBundles);
            }

            loadedBundles = null;
            loadedManifest = null;
            entriesByPackageId = null;
            cacheUsableThisRun = false;
            cacheAllowedThisRun = false;
            loadAttemptedThisRun = false;
            activeInputHash = null;
        }

        public static void DeleteCache()
        {
            ClearLoadedBundle();
            buildQueuedThisRun = false;
            cacheUsableThisRun = false;
            activeInputHash = null;

            TryDeleteFile(ManifestPath);
            TryDeleteFile(BuildInputPath);
            TryDeleteFile(BuildCommandPath);
            TryDeleteFile(BuildPowerShellPath);
            TryDeleteBundleFiles();
            TryDeleteFile(Path.Combine(CachePath, Path.GetFileName(CachePath)));
            TryDeleteFile(Path.Combine(CachePath, Path.GetFileName(CachePath) + ".manifest"));
            TryDeleteEmptyDirectory(CachePath);
        }

        private static void TryDeleteBundleFiles()
        {
            if (!Directory.Exists(CachePath))
            {
                return;
            }

            foreach (string path in Directory.GetFiles(CachePath, BundleFileName + "*", SearchOption.TopDirectoryOnly))
            {
                TryDeleteFile(path);
            }
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
            catch (IOException ex)
            {
                Log.Warning("[FastLoader] Could not clear locked cache file: " + path + "\n" + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warning("[FastLoader] Could not clear protected cache file: " + path + "\n" + ex.Message);
            }
        }

        private static void TryDeleteEmptyDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, false);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static IEnumerable<Pair<string, LoadedContentItem<Texture2D>>> EmptyTextureItems()
        {
            yield break;
        }

        private static IEnumerable<Pair<string, LoadedContentItem<Texture2D>>> EnumerateTextureItems(List<AssetBundleCacheEntry> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                AssetBundleCacheEntry entry = entries[i];
                string bundleFile = string.IsNullOrEmpty(entry.BundleFileName) ? BundleFileName : entry.BundleFileName;
                AssetBundle bundle;
                if (loadedBundles == null || !loadedBundles.TryGetValue(bundleFile, out bundle))
                {
                    Log.Error("[FastLoader] Texture bundle missing from loaded cache: " + bundleFile + " -> " + entry.InternalPath);
                    continue;
                }

                Texture2D texture = bundle.LoadAsset<Texture2D>(entry.AssetPath);
                if (texture == null)
                {
                    Log.Error("[FastLoader] Texture missing from cached AssetBundle: " + entry.AssetPath + " -> " + entry.InternalPath);
                    texture = BaseContent.BadTex;
                }
                else
                {
                    texture.name = entry.ObjectName ?? Path.GetFileNameWithoutExtension(entry.InternalPath);
                    texture.filterMode = FilterMode.Trilinear;
                    texture.anisoLevel = 2;
                }

                FastLoaderVirtualFile virtualFile = new FastLoaderVirtualFile(entry.SourceFullPath, Path.GetFileName(entry.InternalPath));
                LoadedContentItem<Texture2D> item = new LoadedContentItem<Texture2D>(virtualFile, texture, null);
                yield return new Pair<string, LoadedContentItem<Texture2D>>(entry.InternalPath, item);
            }
        }

        private static bool TryLoad(string expectedInputHash, out AssetBundleCacheData manifest, out Dictionary<string, AssetBundle> bundles, out string missReason)
        {
            manifest = null;
            bundles = null;
            missReason = null;

            if (!File.Exists(ManifestPath))
            {
                missReason = "cache files missing";
                return false;
            }

            using (FastProfile.Scope("Texture cache: read manifest"))
            {
                manifest = ReadManifest(ManifestPath);
                if (manifest == null)
                {
                    missReason = "manifest unreadable";
                    return false;
                }
            }

            if (!string.Equals(manifest.InputHash, expectedInputHash, StringComparison.OrdinalIgnoreCase))
            {
                missReason = "mod list changed";
                manifest = null;
                return false;
            }

            List<string> bundleFiles = GetBundleFiles(manifest);
            bundles = new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);
            using (FastProfile.Scope("Texture cache: AssetBundle.LoadFromFile all"))
            {
                for (int i = 0; i < bundleFiles.Count; i++)
                {
                    string bundleFile = bundleFiles[i];
                    string bundlePath = Path.Combine(CachePath, bundleFile);
                    if (!File.Exists(bundlePath))
                    {
                        missReason = "bundle file missing: " + bundleFile;
                        manifest = null;
                        UnloadBundles(bundles);
                        bundles = null;
                        return false;
                    }

                    AssetBundle bundle;
                    using (FastProfile.Scope("Texture cache: AssetBundle.LoadFromFile [" + bundleFile + "]"))
                    {
                        bundle = AssetBundle.LoadFromFile(bundlePath);
                    }

                    if (bundle == null)
                    {
                        missReason = "AssetBundle.LoadFromFile failed: " + bundleFile;
                        manifest = null;
                        UnloadBundles(bundles);
                        bundles = null;
                        return false;
                    }

                    bundles.Add(bundleFile, bundle);
                }
            }

            using (FastProfile.Scope("Texture cache: validate bundle assets"))
            {
                if (!ValidateBundleAssets(bundles, manifest, out missReason))
                {
                    UnloadBundles(bundles);
                    bundles = null;
                    manifest = null;
                    return false;
                }
            }

            return true;
        }

        private static bool ValidateBundleAssets(Dictionary<string, AssetBundle> bundles, AssetBundleCacheData manifest, out string missReason)
        {
            missReason = null;
            Dictionary<string, HashSet<string>> namesByBundle = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, AssetBundle> pair in bundles)
            {
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string[] assetNames;
                using (FastProfile.Scope("Texture cache: GetAllAssetNames [" + pair.Key + "]"))
                {
                    assetNames = pair.Value.GetAllAssetNames();
                }

                for (int i = 0; i < assetNames.Length; i++)
                {
                    names.Add(assetNames[i]);
                }

                namesByBundle.Add(pair.Key, names);
            }

            for (int i = 0; i < manifest.Entries.Count; i++)
            {
                AssetBundleCacheEntry entry = manifest.Entries[i];
                string bundleFile = string.IsNullOrEmpty(entry.BundleFileName) ? BundleFileName : entry.BundleFileName;
                HashSet<string> names;
                if (!namesByBundle.TryGetValue(bundleFile, out names) || !names.Contains(entry.AssetPath))
                {
                    missReason = "bundle asset missing: " + bundleFile + " / " + entry.AssetPath;
                    return false;
                }
            }

            return true;
        }

        private static List<string> GetBundleFiles(AssetBundleCacheData manifest)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> result = new List<string>();
            for (int i = 0; i < manifest.BundleFiles.Count; i++)
            {
                string bundleFile = manifest.BundleFiles[i];
                if (!string.IsNullOrEmpty(bundleFile) && seen.Add(bundleFile))
                {
                    result.Add(bundleFile);
                }
            }

            for (int i = 0; i < manifest.Entries.Count; i++)
            {
                string bundleFile = string.IsNullOrEmpty(manifest.Entries[i].BundleFileName) ? BundleFileName : manifest.Entries[i].BundleFileName;
                if (seen.Add(bundleFile))
                {
                    result.Add(bundleFile);
                }
            }

            if (result.Count == 0 && !string.IsNullOrEmpty(manifest.BundleFileName))
            {
                result.Add(manifest.BundleFileName);
            }

            return result;
        }

        private static void UnloadBundles(Dictionary<string, AssetBundle> bundles)
        {
            if (bundles == null)
            {
                return;
            }

            foreach (AssetBundle bundle in bundles.Values)
            {
                try
                {
                    if (bundle != null)
                    {
                        bundle.Unload(false);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[FastLoader] Failed to unload texture AssetBundle cache.\n" + ex);
                }
            }
        }

        private static Dictionary<string, List<AssetBundleCacheEntry>> BuildPackageIndex(AssetBundleCacheData manifest)
        {
            Dictionary<string, List<AssetBundleCacheEntry>> result = new Dictionary<string, List<AssetBundleCacheEntry>>();
            for (int i = 0; i < manifest.Entries.Count; i++)
            {
                AssetBundleCacheEntry entry = manifest.Entries[i];
                string packageId = NormalizePackageId(entry.PackageId);
                List<AssetBundleCacheEntry> entries;
                if (!result.TryGetValue(packageId, out entries))
                {
                    entries = new List<AssetBundleCacheEntry>();
                    result.Add(packageId, entries);
                }

                entries.Add(entry);
            }

            return result;
        }

        private static void StartExternalBuild(string inputHash)
        {
            string commandPath = PrepareExternalBuild(inputHash);
            if (string.IsNullOrEmpty(commandPath))
            {
                return;
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/k " + Quote(commandPath),
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
                WorkingDirectory = BuildProjectPath
            };

            Process.Start(startInfo);
            Log.Message("[FastLoader] Started texture AssetBundle cache build for next run.\n" + BuildLogPath);
        }

        private static string PrepareExternalBuild(string inputHash)
        {
            string unityExe = FindUnityEditor();
            if (string.IsNullOrEmpty(unityExe))
            {
                Log.Warning("[FastLoader] Unity Editor was not found. Cannot build texture AssetBundle cache.");
                return null;
            }

            Directory.CreateDirectory(CachePath);
            Directory.CreateDirectory(BuildProjectPath);
            Directory.CreateDirectory(Path.Combine(BuildProjectPath, "Assets", "Editor"));
            Directory.CreateDirectory(Path.Combine(BuildProjectPath, "Packages"));
            Directory.CreateDirectory(Path.Combine(BuildProjectPath, "ProjectSettings"));

            AssetBundleCacheData buildData = BuildInputData(inputHash);
            if (buildData.Entries.Count == 0)
            {
                Log.Message("[FastLoader] No textures found for AssetBundle cache build.");
                return null;
            }

            WriteBuildInput(buildData, BuildInputPath);
            WriteUnityProjectFiles();

            WriteBuildCommand(unityExe, buildData.Entries.Count);
            Log.Message("[FastLoader] Prepared external texture AssetBundle cache build. Entries: " + buildData.Entries.Count + "\n" + BuildCommandPath);
            return BuildCommandPath;
        }

        private static void WriteBuildCommand(string unityExe, int entryCount)
        {
            WriteBuildPowerShellScript(unityExe);

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("@echo off");
            builder.AppendLine("title FastLoader AssetBundle Cache Build");
            builder.AppendLine("echo FastLoader AssetBundle cache build");
            builder.AppendLine("echo Entries: " + entryCount);
            builder.AppendLine("echo Unity: " + unityExe);
            builder.AppendLine("echo Log: " + BuildLogPath);
            builder.AppendLine("echo.");
            builder.AppendLine("if not exist " + Quote(BuildInputPath) + " (");
            builder.AppendLine("  echo Missing build input file:");
            builder.AppendLine("  echo " + BuildInputPath);
            builder.AppendLine("  echo Run Prepare resource build again from FastLoader settings.");
            builder.AppendLine("  echo.");
            builder.AppendLine("  goto :END");
            builder.AppendLine(")");
            builder.AppendLine("powershell.exe -NoProfile -ExecutionPolicy Bypass -File " + Quote(BuildPowerShellPath));
            builder.AppendLine("set FASTLOADER_EXITCODE=%ERRORLEVEL%");
            builder.AppendLine("echo.");
            builder.AppendLine("echo FastLoader AssetBundle cache build finished. Exit code: %FASTLOADER_EXITCODE%");
            builder.AppendLine("echo Log: " + BuildLogPath);
            builder.AppendLine("echo This window is intentionally left open.");
            builder.AppendLine("echo Close it manually after checking the result.");
            builder.AppendLine("echo.");
            builder.AppendLine(":END");
            File.WriteAllText(BuildCommandPath, builder.ToString(), new UTF8Encoding(false));
        }

        private static void WriteBuildPowerShellScript(string unityExe)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("$ErrorActionPreference = 'Continue'");
            builder.AppendLine("$logPath = " + PsQuote(BuildLogPath));
            builder.AppendLine("$inputPath = " + PsQuote(BuildInputPath));
            builder.AppendLine("if (-not (Test-Path -LiteralPath $inputPath)) {");
            builder.AppendLine("  Write-Host ('Missing build input file: ' + $inputPath)");
            builder.AppendLine("  Write-Host 'Run Prepare resource build again from FastLoader settings.'");
            builder.AppendLine("  exit 2");
            builder.AppendLine("}");
            builder.AppendLine("if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force }");
            builder.AppendLine("Write-Host 'Starting Unity AssetBundle build. Live Unity log follows.'");
            builder.AppendLine("Write-Host ('Log: ' + $logPath)");
            builder.AppendLine("& " + PsQuote(unityExe) + " `");
            builder.AppendLine("  -batchmode `");
            builder.AppendLine("  -nographics `");
            builder.AppendLine("  -quit `");
            builder.AppendLine("  -projectPath " + PsQuote(BuildProjectPath) + " `");
            builder.AppendLine("  -executeMethod FastLoaderAssetBundleBuilder.BuildFromCommandLine `");
            builder.AppendLine("  -fastLoaderInput " + PsQuote(BuildInputPath) + " `");
            builder.AppendLine("  -fastLoaderOutput " + PsQuote(CachePath) + " `");
            builder.AppendLine("  -fastLoaderBundleName " + PsQuote(BundleFileName) + " `");
            builder.AppendLine("  -fastLoaderManifest " + PsQuote(ManifestPath) + " `");
            builder.AppendLine("  -logFile - 2>&1 | Tee-Object -FilePath $logPath");
            builder.AppendLine("$exitCode = $LASTEXITCODE");
            builder.AppendLine("Write-Host ''");
            builder.AppendLine("Write-Host ('Unity finished. Exit code: ' + $exitCode)");
            builder.AppendLine("exit $exitCode");
            File.WriteAllText(BuildPowerShellPath, builder.ToString(), new UTF8Encoding(false));
        }

        private static AssetBundleCacheData BuildInputData(string inputHash)
        {
            AssetBundleCacheData data = new AssetBundleCacheData();
            data.InputHash = inputHash;
            data.CreatedUtc = DateTime.UtcNow.ToString("o");
            data.BundleFileName = BundleFileName;

            List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
            int assetIndex = 0;
            string currentBundleFile = BuildBundleFileName(0);
            data.BundleFiles.Add(currentBundleFile);
            for (int i = 0; i < mods.Count; i++)
            {
                ModContentPack mod = mods[i];
                if (ShouldSkipTextureBundleForMod(mod))
                {
                    continue;
                }

                Dictionary<string, FileInfo> files = ModContentPack.GetAllFilesForMod(mod, GenFilePaths.ContentPath<Texture2D>(), IsTextureExtension, null);
                List<string> keys = new List<string>(files.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);

                for (int k = 0; k < keys.Count; k++)
                {
                    int bundleIndex = assetIndex / MaxEntriesPerBundle;
                    currentBundleFile = BuildBundleFileName(bundleIndex);
                    if (!data.BundleFiles.Contains(currentBundleFile))
                    {
                        data.BundleFiles.Add(currentBundleFile);
                    }

                    string internalPath = NormalizeInternalPath(keys[k]);
                    FileInfo file = files[keys[k]];
                    data.Entries.Add(new AssetBundleCacheEntry
                    {
                        PackageId = mod.PackageId ?? string.Empty,
                        PackageIdPlayerFacing = mod.PackageIdPlayerFacing ?? string.Empty,
                        InternalPath = internalPath,
                        SourceFullPath = file.FullName,
                        AssetPath = BuildAssetPath(assetIndex, file.Extension),
                        ObjectName = Path.GetFileNameWithoutExtension(internalPath),
                        BundleFileName = currentBundleFile
                    });
                    assetIndex++;
                }
            }

            return data;
        }

        private static string BuildBundleFileName(int index)
        {
            return BundleFileName + "_" + index.ToString("D3");
        }

        private static bool ShouldSkipTextureBundleForMod(ModContentPack mod)
        {
            if (mod == null)
            {
                return true;
            }

            return mod.IsCoreMod || mod.IsOfficialMod;
        }

        private static string BuildAssetPath(int index, string extension)
        {
            string ext = string.IsNullOrEmpty(extension) ? ".asset" : extension.ToLowerInvariant();
            return "Assets/FastLoaderInput/Textures/" + index.ToString("D8") + ext;
        }

        private static bool IsTextureExtension(string extension)
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

        private static string NormalizeInternalPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }

        private static string NormalizePackageId(string packageId)
        {
            return (packageId ?? string.Empty).ToLowerInvariant();
        }

        private static string FindUnityEditor()
        {
            string versionPath = Path.Combine("C:\\Program Files\\Unity\\Hub\\Editor", Application.unityVersion, "Editor", "Unity.exe");
            if (File.Exists(versionPath))
            {
                return versionPath;
            }

            string hubRoot = "C:\\Program Files\\Unity\\Hub\\Editor";
            if (Directory.Exists(hubRoot))
            {
                DirectoryInfo root = new DirectoryInfo(hubRoot);
                DirectoryInfo[] versions = root.GetDirectories();
                Array.Sort(versions, delegate(DirectoryInfo a, DirectoryInfo b)
                {
                    return string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase);
                });

                for (int i = 0; i < versions.Length; i++)
                {
                    string candidate = Path.Combine(versions[i].FullName, "Editor", "Unity.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static void WriteUnityProjectFiles()
        {
            Directory.CreateDirectory(Path.Combine(BuildProjectPath, "Assets"));
            Directory.CreateDirectory(Path.Combine(BuildProjectPath, "Assets", "Editor"));

            string packagesManifestPath = Path.Combine(BuildProjectPath, "Packages", "manifest.json");
            if (!File.Exists(packagesManifestPath))
            {
                File.WriteAllText(packagesManifestPath, UnityPackagesManifest);
            }
            else
            {
                File.WriteAllText(packagesManifestPath, UnityPackagesManifest);
            }

            string projectVersionPath = Path.Combine(BuildProjectPath, "ProjectSettings", "ProjectVersion.txt");
            File.WriteAllText(projectVersionPath, "m_EditorVersion: " + Application.unityVersion + Environment.NewLine);

            string scriptPath = Path.Combine(BuildProjectPath, "Assets", "Editor", "FastLoaderAssetBundleBuilder.cs");
            File.WriteAllText(scriptPath, UnityBuilderScript);

            string anchorPath = Path.Combine(BuildProjectPath, "Assets", "FastLoaderAssetBundleModuleAnchor.cs");
            File.WriteAllText(anchorPath, UnityModuleAnchorScript);
        }

        private static void WriteBuildInput(AssetBundleCacheData data, string path)
        {
            XmlDocument document = new XmlDocument();
            XmlElement root = document.CreateElement("FastLoaderAssetBundleBuildInput");
            document.AppendChild(root);
            AppendElement(document, root, "formatVersion", CacheFormatVersion);
            AppendElement(document, root, "fastLoaderVersion", FastLoaderRuntime.FastLoaderVersion);
            AppendElement(document, root, "gameVersion", VersionControl.CurrentVersionStringWithRev ?? string.Empty);
            AppendElement(document, root, "inputHash", data.InputHash ?? string.Empty);
            AppendElement(document, root, "createdUtc", data.CreatedUtc ?? string.Empty);
            AppendElement(document, root, "bundleFile", data.BundleFileName ?? BundleFileName);

            XmlElement bundleFiles = document.CreateElement("bundleFiles");
            root.AppendChild(bundleFiles);
            for (int i = 0; i < data.BundleFiles.Count; i++)
            {
                AppendElement(document, bundleFiles, "li", data.BundleFiles[i]);
            }

            XmlElement entries = document.CreateElement("entries");
            root.AppendChild(entries);
            for (int i = 0; i < data.Entries.Count; i++)
            {
                AssetBundleCacheEntry entry = data.Entries[i];
                XmlElement element = document.CreateElement("entry");
                element.SetAttribute("type", TextureTypeName);
                element.SetAttribute("packageId", entry.PackageId ?? string.Empty);
                element.SetAttribute("packageIdPlayerFacing", entry.PackageIdPlayerFacing ?? string.Empty);
                element.SetAttribute("internalPath", entry.InternalPath ?? string.Empty);
                element.SetAttribute("sourceFullPath", entry.SourceFullPath ?? string.Empty);
                element.SetAttribute("assetPath", entry.AssetPath ?? string.Empty);
                element.SetAttribute("objectName", entry.ObjectName ?? string.Empty);
                element.SetAttribute("bundleFile", entry.BundleFileName ?? data.BundleFileName ?? BundleFileName);
                entries.AppendChild(element);
            }

            WriteXmlAtomically(path, document);
        }

        private static AssetBundleCacheData ReadManifest(string path)
        {
            XmlDocument document = new XmlDocument();
            using (XmlReader reader = XmlReader.Create(path, XmlSettings()))
            {
                document.Load(reader);
            }

            XmlElement root = document.DocumentElement;
            if (root == null || root.Name != "FastLoaderAssetBundleCache")
            {
                return null;
            }

            if (ChildText(root, "formatVersion") != CacheFormatVersion ||
                ChildText(root, "fastLoaderVersion") != FastLoaderRuntime.FastLoaderVersion ||
                ChildText(root, "gameVersion") != (VersionControl.CurrentVersionStringWithRev ?? string.Empty))
            {
                return null;
            }

            AssetBundleCacheData data = new AssetBundleCacheData();
            data.InputHash = ChildText(root, "inputHash");
            data.CreatedUtc = ChildText(root, "createdUtc");
            data.BundleFileName = ChildText(root, "bundleFile");
            XmlNode bundleFiles = root.SelectSingleNode("bundleFiles");
            if (bundleFiles != null)
            {
                foreach (XmlNode node in bundleFiles.ChildNodes)
                {
                    if (node.NodeType == XmlNodeType.Element && node.Name == "li")
                    {
                        data.BundleFiles.Add(node.InnerText ?? string.Empty);
                    }
                }
            }

            XmlNode entries = root.SelectSingleNode("entries");
            if (entries != null)
            {
                foreach (XmlNode node in entries.ChildNodes)
                {
                    if (node.NodeType != XmlNodeType.Element || node.Name != "entry")
                    {
                        continue;
                    }

                    if (AttributeText(node, "type") != TextureTypeName)
                    {
                        continue;
                    }

                    data.Entries.Add(new AssetBundleCacheEntry
                    {
                        PackageId = AttributeText(node, "packageId"),
                        PackageIdPlayerFacing = AttributeText(node, "packageIdPlayerFacing"),
                        InternalPath = AttributeText(node, "internalPath"),
                        SourceFullPath = AttributeText(node, "sourceFullPath"),
                        AssetPath = AttributeText(node, "assetPath"),
                        ObjectName = AttributeText(node, "objectName"),
                        BundleFileName = AttributeText(node, "bundleFile")
                    });
                }
            }

            return data;
        }

        private static XmlReaderSettings XmlSettings()
        {
            return new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                CheckCharacters = false,
                Async = false
            };
        }

        private static void WriteXmlAtomically(string path, XmlDocument document)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tempPath = path + ".tmp";
            using (XmlWriter writer = XmlWriter.Create(tempPath, new XmlWriterSettings
            {
                Indent = false,
                NewLineHandling = NewLineHandling.None
            }))
            {
                document.Save(writer);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }

        private static void AppendElement(XmlDocument document, XmlElement parent, string name, string value)
        {
            XmlElement child = document.CreateElement(name);
            child.InnerText = value ?? string.Empty;
            parent.AppendChild(child);
        }

        private static string ChildText(XmlElement parent, string name)
        {
            XmlNode node = parent.SelectSingleNode(name);
            return node != null ? node.InnerText : string.Empty;
        }

        private static string AttributeText(XmlNode node, string name)
        {
            if (node.Attributes == null)
            {
                return string.Empty;
            }

            XmlAttribute attribute = node.Attributes[name];
            return attribute != null ? attribute.Value : string.Empty;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string PsQuote(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        private static string FormatSeconds(TimeSpan elapsed)
        {
            return elapsed.TotalSeconds.ToString("0.00") + "s";
        }

        private const string UnityBuilderScript = @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using UnityEditor;
using UnityEngine;

public static class FastLoaderAssetBundleBuilder
{
    private sealed class Entry
    {
        public string Type;
        public string PackageId;
        public string PackageIdPlayerFacing;
        public string InternalPath;
        public string SourceFullPath;
        public string AssetPath;
        public string ObjectName;
        public string BundleFileName;
    }

    public static void BuildFromCommandLine()
    {
        try
        {
            Build();
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogError(ex);
            EditorApplication.Exit(1);
        }
    }

    private static void Build()
    {
        string inputPath = GetArg(""-fastLoaderInput"");
        string outputDir = GetArg(""-fastLoaderOutput"");
        string bundleName = GetArg(""-fastLoaderBundleName"");
        string manifestPath = GetArg(""-fastLoaderManifest"");

        if (string.IsNullOrEmpty(inputPath) || string.IsNullOrEmpty(outputDir) || string.IsNullOrEmpty(bundleName) || string.IsNullOrEmpty(manifestPath))
        {
            throw new InvalidOperationException(""FastLoader AssetBundle build arguments are incomplete."");
        }

        XmlDocument input = new XmlDocument();
        input.Load(inputPath);
        XmlElement root = input.DocumentElement;
        if (root == null || root.Name != ""FastLoaderAssetBundleBuildInput"")
        {
            throw new InvalidOperationException(""Invalid FastLoader AssetBundle build input."");
        }

        List<Entry> entries = ReadEntries(root);
        if (entries.Count == 0)
        {
            throw new InvalidOperationException(""No entries to build."");
        }

        string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ""..""));
        string inputRoot = Path.Combine(projectPath, ""Assets"", ""FastLoaderInput"");
        if (Directory.Exists(inputRoot))
        {
            Directory.Delete(inputRoot, true);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            if (!File.Exists(entry.SourceFullPath))
            {
                throw new FileNotFoundException(""Source asset is missing."", entry.SourceFullPath);
            }

            string targetPath = Path.Combine(projectPath, entry.AssetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            File.Copy(entry.SourceFullPath, targetPath, true);
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

        Dictionary<string, List<string>> assetNamesByBundle = new Dictionary<string, List<string>>();
        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            TextureImporter importer = AssetImporter.GetAtPath(entry.AssetPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.mipmapEnabled = true;
                importer.isReadable = false;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = 2;
                importer.SaveAndReimport();
            }

            UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(entry.AssetPath);
            if (asset == null)
            {
                throw new InvalidOperationException(""Imported asset is missing: "" + entry.AssetPath);
            }

            string entryBundleName = string.IsNullOrEmpty(entry.BundleFileName) ? bundleName : entry.BundleFileName;
            List<string> assetNames;
            if (!assetNamesByBundle.TryGetValue(entryBundleName, out assetNames))
            {
                assetNames = new List<string>();
                assetNamesByBundle.Add(entryBundleName, assetNames);
            }

            assetNames.Add(entry.AssetPath);
        }

        Directory.CreateDirectory(outputDir);
        foreach (string existing in Directory.GetFiles(outputDir, bundleName + ""*""))
        {
            File.Delete(existing);
        }

        List<AssetBundleBuild> builds = new List<AssetBundleBuild>();
        foreach (KeyValuePair<string, List<string>> pair in assetNamesByBundle)
        {
            builds.Add(new AssetBundleBuild
            {
                assetBundleName = pair.Key,
                assetNames = pair.Value.ToArray()
            });
        }

        AssetBundleManifest buildManifest = BuildPipeline.BuildAssetBundles(
            outputDir,
            builds.ToArray(),
            BuildAssetBundleOptions.UncompressedAssetBundle | BuildAssetBundleOptions.ForceRebuildAssetBundle,
            BuildTarget.StandaloneWindows64);

        if (buildManifest == null)
        {
            throw new InvalidOperationException(""Unity failed to build FastLoader AssetBundle."");
        }

        for (int i = 0; i < builds.Count; i++)
        {
            string outputBundle = Path.Combine(outputDir, builds[i].assetBundleName);
            if (!File.Exists(outputBundle))
            {
                throw new InvalidOperationException(""Unity failed to build FastLoader AssetBundle: "" + outputBundle);
            }
        }

        WriteRuntimeManifest(input, manifestPath, bundleName);
        Debug.Log(""FastLoader AssetBundle build complete. Entries: "" + entries.Count + "", bundles: "" + builds.Count + "", output: "" + outputDir);
    }

    private static List<Entry> ReadEntries(XmlElement root)
    {
        List<Entry> result = new List<Entry>();
        XmlNode entries = root.SelectSingleNode(""entries"");
        if (entries == null)
        {
            return result;
        }

        foreach (XmlNode node in entries.ChildNodes)
        {
            if (node.NodeType != XmlNodeType.Element || node.Name != ""entry"")
            {
                continue;
            }

            result.Add(new Entry
            {
                Type = Attr(node, ""type""),
                PackageId = Attr(node, ""packageId""),
                PackageIdPlayerFacing = Attr(node, ""packageIdPlayerFacing""),
                InternalPath = Attr(node, ""internalPath""),
                SourceFullPath = Attr(node, ""sourceFullPath""),
                AssetPath = Attr(node, ""assetPath"").Replace('\\', '/'),
                ObjectName = Attr(node, ""objectName""),
                BundleFileName = Attr(node, ""bundleFile"")
            });
        }

        return result;
    }

    private static void WriteRuntimeManifest(XmlDocument input, string manifestPath, string bundleName)
    {
        XmlElement inputRoot = input.DocumentElement;
        XmlDocument document = new XmlDocument();
        XmlElement root = document.CreateElement(""FastLoaderAssetBundleCache"");
        document.AppendChild(root);

        AppendElement(document, root, ""formatVersion"", ChildText(inputRoot, ""formatVersion""));
        AppendElement(document, root, ""fastLoaderVersion"", ChildText(inputRoot, ""fastLoaderVersion""));
        AppendElement(document, root, ""gameVersion"", ChildText(inputRoot, ""gameVersion""));
        AppendElement(document, root, ""inputHash"", ChildText(inputRoot, ""inputHash""));
        AppendElement(document, root, ""createdUtc"", DateTime.UtcNow.ToString(""o""));
        AppendElement(document, root, ""bundleFile"", bundleName);

        XmlElement bundleFiles = document.CreateElement(""bundleFiles"");
        root.AppendChild(bundleFiles);
        XmlNode inputBundleFiles = inputRoot.SelectSingleNode(""bundleFiles"");
        if (inputBundleFiles != null)
        {
            foreach (XmlNode inputBundleFile in inputBundleFiles.ChildNodes)
            {
                if (inputBundleFile.NodeType == XmlNodeType.Element && inputBundleFile.Name == ""li"")
                {
                    AppendElement(document, bundleFiles, ""li"", inputBundleFile.InnerText);
                }
            }
        }

        XmlElement entries = document.CreateElement(""entries"");
        root.AppendChild(entries);

        XmlNode inputEntries = inputRoot.SelectSingleNode(""entries"");
        foreach (XmlNode inputEntry in inputEntries.ChildNodes)
        {
            if (inputEntry.NodeType != XmlNodeType.Element || inputEntry.Name != ""entry"")
            {
                continue;
            }

            XmlElement entry = document.CreateElement(""entry"");
            CopyAttr(inputEntry, entry, ""type"");
            CopyAttr(inputEntry, entry, ""packageId"");
            CopyAttr(inputEntry, entry, ""packageIdPlayerFacing"");
            CopyAttr(inputEntry, entry, ""internalPath"");
            CopyAttr(inputEntry, entry, ""sourceFullPath"");
            CopyAttr(inputEntry, entry, ""assetPath"");
            CopyAttr(inputEntry, entry, ""objectName"");
            CopyAttr(inputEntry, entry, ""bundleFile"");
            entries.AppendChild(entry);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
        string tempPath = manifestPath + "".tmp"";
        document.Save(tempPath);
        if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }
        File.Move(tempPath, manifestPath);
    }

    private static string GetArg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static string ChildText(XmlElement parent, string name)
    {
        XmlNode node = parent.SelectSingleNode(name);
        return node == null ? string.Empty : node.InnerText;
    }

    private static string Attr(XmlNode node, string name)
    {
        XmlAttribute attribute = node.Attributes == null ? null : node.Attributes[name];
        return attribute == null ? string.Empty : attribute.Value;
    }

    private static void CopyAttr(XmlNode source, XmlElement target, string name)
    {
        target.SetAttribute(name, Attr(source, name));
    }

    private static void AppendElement(XmlDocument document, XmlElement parent, string name, string value)
    {
        XmlElement child = document.CreateElement(name);
        child.InnerText = value ?? string.Empty;
        parent.AppendChild(child);
    }
}
";

        private const string UnityPackagesManifest = @"{""dependencies"":{""com.unity.modules.assetbundle"":""1.0.0""}}";

        private const string UnityModuleAnchorScript = @"
using UnityEngine;

public sealed class FastLoaderAssetBundleModuleAnchor : MonoBehaviour
{
    private AssetBundle assetBundle;
    private Texture2D texture;

    public void Touch()
    {
        if (assetBundle != null && texture != null)
        {
            Debug.Log(assetBundle.name + texture.name);
        }
    }
}
";
    }
}
