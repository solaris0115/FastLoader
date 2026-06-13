using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.IO;
using UnityEngine;
using Verse;

namespace FastLoader
{
    internal sealed class LanguageCachePayload
    {
        public readonly Dictionary<string, LoadedLanguage.KeyedReplacement> KeyedReplacements =
            new Dictionary<string, LoadedLanguage.KeyedReplacement>(StringComparer.Ordinal);

        public readonly Dictionary<string, List<string>> StringFiles =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
    }

    internal static class LanguageBinaryCache
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FLLC");
        private const int FormatVersion = 1;
        private const int MaxKeyedEntries = 1000000;
        private const int MaxStringFiles = 200000;
        private const int MaxStringLinesPerFile = 1000000;
        private const string DefInjectedCacheExtension = ".finj";
        private static readonly FieldInfo DataIsLoadedField = AccessTools.Field(typeof(LoadedLanguage), "dataIsLoaded");
        private static readonly FieldInfo TmpAlreadyLoadedFilesField = AccessTools.Field(typeof(LoadedLanguage), "tmpAlreadyLoadedFiles");

        private static string CacheRootPath
        {
            get { return Path.Combine(GenFilePaths.ConfigFolderPath, "FastLoader", "LanguageCache"); }
        }

        public static void DeleteAll()
        {
            if (!Directory.Exists(CacheRootPath))
            {
                return;
            }

            try
            {
                string[] files = Directory.GetFiles(CacheRootPath, "*.flang", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    TryDeleteFile(files[i]);
                }

                files = Directory.GetFiles(CacheRootPath, "*" + DefInjectedCacheExtension, SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    TryDeleteFile(files[i]);
                }

                TryDeleteEmptyDirectory(CacheRootPath);
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to delete language cache files.\n" + ex);
            }
        }

        public static bool TryLoadDataFromCache(LoadedLanguage language)
        {
            if (!IsEnabled() || language == null)
            {
                return false;
            }

            string cachePath = GetCachePath(language);
            string expectedHash = ComputeLanguageHash(language);
            LanguageCachePayload payload;
            using (FastLoaderProfiler.Scope("FastLoader.LanguageCache.TryRead | " + Describe(language)))
            {
                if (!LanguageCacheFile.TryRead(cachePath, expectedHash, language.folderName, out payload))
                {
                    return false;
                }
            }

            try
            {
                using (FastLoaderProfiler.Scope("FastLoader.LanguageCache.Apply | " + Describe(language)))
                {
                    LoadDataWithCachedKeyedAndStrings(language, payload);
                }

                Log.Message("[FastLoader] Language cache hit: " + Describe(language) +
                    ", keyed=" + payload.KeyedReplacements.Count +
                    ", stringFiles=" + payload.StringFiles.Count);
                return true;
            }
            catch (Exception ex)
            {
                ResetAfterFailedCustomLoad(language);
                FastLoaderRuntime.ActivateVanillaFallback(FastLoaderCacheKind.Language, Describe(language) + " language cache restore failed: " + ex.GetType().Name);
                Log.Warning("[FastLoader] Language cache restore failed. Falling back to vanilla language load.\n" + ex);
                return false;
            }
        }

        public static bool IsDataLoaded(LoadedLanguage language)
        {
            return DataIsLoadedField != null && language != null && (bool)DataIsLoadedField.GetValue(language);
        }

        public static int BuildFromLoadedLanguages()
        {
            if (!IsBuildEnabled())
            {
                return 0;
            }

            int written = 0;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            written += BuildOne(LanguageDatabase.activeLanguage, seen);
            written += BuildOne(LanguageDatabase.defaultLanguage, seen);
            return written;
        }

        public static void SaveFromLanguage(LoadedLanguage language)
        {
            if (!IsBuildEnabled() || language == null)
            {
                return;
            }

            try
            {
                string cachePath = GetCachePath(language);
                string hash = ComputeLanguageHash(language);
                using (FastLoaderProfiler.Scope("FastLoader.LanguageCache.Write | " + Describe(language)))
                {
                    LanguageCacheFile.Write(cachePath, hash, language);
                }

                using (FastLoaderProfiler.Scope("FastLoader.DefInjectedCache.Write | " + Describe(language)))
                {
                    DefInjectedBinaryCache.Write(GetDefInjectedCachePath(language), language);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to write language cache for " + Describe(language) + ".\n" + ex);
                throw new IOException("Failed to write language cache for " + Describe(language) + ": " + ex.GetBaseException().Message, ex);
            }
        }

        private static int BuildOne(LoadedLanguage language, HashSet<string> seen)
        {
            if (language == null)
            {
                return 0;
            }

            string folderName = language.folderName ?? string.Empty;
            if (!seen.Add(folderName))
            {
                return 0;
            }

            language.LoadData();
            SaveFromLanguage(language);
            return 1;
        }

        private static void LoadDataWithCachedKeyedAndStrings(LoadedLanguage language, LanguageCachePayload payload)
        {
            SetDataIsLoaded(language, true);

            Dictionary<ModContentPack, HashSet<string>> alreadyLoaded = GetAlreadyLoadedFiles(language);
            if (alreadyLoaded != null)
            {
                alreadyLoaded.Clear();
            }

            language.keyedReplacements.Clear();
            foreach (KeyValuePair<string, LoadedLanguage.KeyedReplacement> item in payload.KeyedReplacements)
            {
                language.keyedReplacements[item.Key] = item.Value;
            }

            language.stringFiles.Clear();
            foreach (KeyValuePair<string, List<string>> item in payload.StringFiles)
            {
                language.stringFiles[item.Key] = item.Value;
            }

            language.defInjections.Clear();

            int defInjectedPackageCount = 0;
            int defInjectedInjectionCount = 0;
            bool defInjectedCacheLoaded;
            using (FastLoaderProfiler.Scope("FastLoader.DefInjectedCache.TryRead | " + Describe(language)))
            {
                defInjectedCacheLoaded = DefInjectedBinaryCache.TryRead(
                    GetDefInjectedCachePath(language),
                    language,
                    out defInjectedPackageCount,
                    out defInjectedInjectionCount);
            }

            DeepProfiler.Start("Loading language data from FastLoader cache: " + language.folderName);
            try
            {
                foreach (Tuple<VirtualDirectory, ModContentPack, string> tuple in language.AllDirectories)
                {
                    Tuple<VirtualDirectory, ModContentPack, string> localDirectory = tuple;
                    EnsureAlreadyLoadedBucket(language, localDirectory.Item2);
                    QueueLanguageIconLoad(language, localDirectory);
                    CheckOldKeyedFolder(language, localDirectory);
                    if (!defInjectedCacheLoaded)
                    {
                        LoadDefInjected(language, localDirectory);
                        EnsureAllDefTypesHaveDefInjectionPackage(language);
                    }

                    language.WordInfo.LoadFrom(localDirectory, language);
                }

                if (defInjectedCacheLoaded)
                {
                    EnsureAllDefTypesHaveDefInjectionPackage(language);
                    Log.Message("[FastLoader] DefInjected cache hit: " + Describe(language) +
                        ", packages=" + defInjectedPackageCount +
                        ", injections=" + defInjectedInjectionCount);
                }
            }
            finally
            {
                DeepProfiler.End();
            }
        }

        private static void CheckOldKeyedFolder(LoadedLanguage language, Tuple<VirtualDirectory, ModContentPack, string> localDirectory)
        {
            VirtualDirectory oldKeyed = localDirectory.Item1.GetDirectory("CodeLinked");
            if (oldKeyed.Exists)
            {
                language.loadErrors.Add("Translations aren't called CodeLinked any more. Please rename to Keyed: " + oldKeyed);
            }
        }

        private static void LoadDefInjected(LoadedLanguage language, Tuple<VirtualDirectory, ModContentPack, string> localDirectory)
        {
            VirtualDirectory directory = localDirectory.Item1.GetDirectory("DefLinked");
            if (directory.Exists)
            {
                language.loadErrors.Add("Translations aren't called DefLinked any more. Please rename to DefInjected: " + directory);
            }
            else
            {
                directory = localDirectory.Item1.GetDirectory("DefInjected");
            }

            if (!directory.Exists)
            {
                return;
            }

            foreach (VirtualDirectory defTypeDirectory in directory.GetDirectories("*", SearchOption.TopDirectoryOnly))
            {
                string name = defTypeDirectory.Name;
                Type defType = GenTypes.GetTypeInAnyAssembly(name, null);
                if (defType == null && name.Length > 3)
                {
                    defType = GenTypes.GetTypeInAnyAssembly(name.Substring(0, name.Length - 1), null);
                }

                if (defType == null)
                {
                    language.loadErrors.Add("Error loading language from " + localDirectory + ": dir " + defTypeDirectory.Name + " doesn't correspond to any def type. Skipping...");
                    continue;
                }

                List<VirtualFile> files = new List<VirtualFile>();
                foreach (VirtualFile file in defTypeDirectory.GetFiles("*.xml", SearchOption.AllDirectories))
                {
                    if (language.TryRegisterFileIfNew(localDirectory, file.FullPath))
                    {
                        files.Add(file);
                    }
                }

                List<string> contents = (from file in files.AsParallel()
                                         select file.ReadAllText()).ToList();
                for (int i = 0; i < contents.Count; i++)
                {
                    language.LoadFromFile_DefInject(files[i], defType, contents[i]);
                }
            }
        }

        private static void EnsureAllDefTypesHaveDefInjectionPackage(LoadedLanguage language)
        {
            foreach (Type defType in GenDefDatabase.AllDefTypesWithDatabases())
            {
                if (!language.defInjections.Any(x => x.defType == defType))
                {
                    language.defInjections.Add(new DefInjectionPackage(defType));
                }
            }
        }

        private static void QueueLanguageIconLoad(LoadedLanguage language, Tuple<VirtualDirectory, ModContentPack, string> localDirectory)
        {
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                if (language.icon == BaseContent.BadTex)
                {
                    VirtualFile file = localDirectory.Item1.GetFile("LangIcon.png");
                    if (file.Exists)
                    {
                        language.icon = ModContentLoader<Texture2D>.LoadItem(file).contentItem;
                    }
                }
            });
        }

        private static void ResetAfterFailedCustomLoad(LoadedLanguage language)
        {
            SetDataIsLoaded(language, false);
            Dictionary<ModContentPack, HashSet<string>> alreadyLoaded = GetAlreadyLoadedFiles(language);
            if (alreadyLoaded != null)
            {
                alreadyLoaded.Clear();
            }

            language.keyedReplacements.Clear();
            language.stringFiles.Clear();
            language.defInjections.Clear();
        }

        private static bool IsEnabled()
        {
            return !FastLoaderRuntime.IsCacheFallbackActive &&
                FastLoaderRuntime.ShouldUseXmlCache;
        }

        private static bool IsBuildEnabled()
        {
            return !FastLoaderRuntime.IsCacheFallbackActive &&
                (FastLoaderRuntime.IsManualBuildActive ||
                 FastLoaderRuntime.ShouldUseXmlCache);
        }

        private static string ComputeLanguageHash(LoadedLanguage language)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                AppendHash(sha, "FastLoaderLanguageCacheV1");
                AppendHash(sha, FastLoaderRuntime.FastLoaderVersion);
                AppendHash(sha, VersionControl.CurrentVersionStringWithRev ?? string.Empty);
                AppendHash(sha, FastLoaderHasher.ComputeFastModListHash());
                AppendHash(sha, language != null ? language.folderName ?? string.Empty : string.Empty);
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BytesToHex(sha.Hash);
            }
        }

        private static void AppendHash(System.Security.Cryptography.HashAlgorithm sha, string value)
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
            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2"));
            }

            return builder.ToString();
        }

        private static string GetCachePath(LoadedLanguage language)
        {
            return Path.Combine(CacheRootPath, "language_" + SanitizeFileName(language.folderName ?? "unknown") + ".flang");
        }

        private static string GetDefInjectedCachePath(LoadedLanguage language)
        {
            return Path.Combine(CacheRootPath, "definjected_" + SanitizeFileName(language.folderName ?? "unknown") + DefInjectedCacheExtension);
        }

        private static string SanitizeFileName(string name)
        {
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            }

            return sb.ToString();
        }

        private static string Describe(LoadedLanguage language)
        {
            return language != null ? language.folderName ?? "(unknown language)" : "(null language)";
        }

        private static void SetDataIsLoaded(LoadedLanguage language, bool value)
        {
            if (DataIsLoadedField != null)
            {
                DataIsLoadedField.SetValue(language, value);
            }
        }

        private static Dictionary<ModContentPack, HashSet<string>> GetAlreadyLoadedFiles(LoadedLanguage language)
        {
            return TmpAlreadyLoadedFilesField != null
                ? TmpAlreadyLoadedFilesField.GetValue(language) as Dictionary<ModContentPack, HashSet<string>>
                : null;
        }

        private static void EnsureAlreadyLoadedBucket(LoadedLanguage language, ModContentPack mod)
        {
            Dictionary<ModContentPack, HashSet<string>> alreadyLoaded = GetAlreadyLoadedFiles(language);
            if (alreadyLoaded != null && !alreadyLoaded.ContainsKey(mod))
            {
                alreadyLoaded[mod] = new HashSet<string>();
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to delete file " + path + "\n" + ex);
            }
        }

        private static void TryDeleteEmptyDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
                {
                    Directory.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static class LanguageCacheFile
        {
            public static void Write(string path, string hash, LoadedLanguage language)
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string tempPath = path + ".tmp";
                using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                using (BinaryWriter writer = new BinaryWriter(fs, Encoding.UTF8))
                {
                    writer.Write(Magic);
                    writer.Write(FormatVersion);
                    writer.Write(hash ?? string.Empty);
                    writer.Write(language.folderName ?? string.Empty);
                    writer.Write(DateTime.UtcNow.Ticks);

                    List<KeyValuePair<string, LoadedLanguage.KeyedReplacement>> keyed =
                        new List<KeyValuePair<string, LoadedLanguage.KeyedReplacement>>(language.keyedReplacements);
                    keyed.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
                    writer.Write(keyed.Count);
                    for (int i = 0; i < keyed.Count; i++)
                    {
                        LoadedLanguage.KeyedReplacement replacement = keyed[i].Value;
                        writer.Write(keyed[i].Key ?? string.Empty);
                        writer.Write(replacement != null ? replacement.key ?? string.Empty : string.Empty);
                        writer.Write(replacement != null ? replacement.value ?? string.Empty : string.Empty);
                        writer.Write(replacement != null ? replacement.fileSource ?? string.Empty : string.Empty);
                        writer.Write(replacement != null ? replacement.fileSourceLine : 0);
                        writer.Write(replacement != null ? replacement.fileSourceFullPath ?? string.Empty : string.Empty);
                        writer.Write(replacement != null && replacement.isPlaceholder);
                    }

                    List<KeyValuePair<string, List<string>>> strings =
                        new List<KeyValuePair<string, List<string>>>(language.stringFiles);
                    strings.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
                    writer.Write(strings.Count);
                    for (int i = 0; i < strings.Count; i++)
                    {
                        writer.Write(strings[i].Key ?? string.Empty);
                        List<string> lines = strings[i].Value ?? new List<string>();
                        writer.Write(lines.Count);
                        for (int j = 0; j < lines.Count; j++)
                        {
                            writer.Write(lines[j] ?? string.Empty);
                        }
                    }
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(tempPath, path);
            }

            public static bool TryRead(string path, string expectedHash, string expectedLanguageFolder, out LanguageCachePayload payload)
            {
                payload = null;
                if (!File.Exists(path))
                {
                    return false;
                }

                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                    using (BinaryReader reader = new BinaryReader(fs, Encoding.UTF8))
                    {
                        byte[] magic = reader.ReadBytes(4);
                        if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
                        {
                            return false;
                        }

                        int version = reader.ReadInt32();
                        if (version != FormatVersion)
                        {
                            return false;
                        }

                        string hash = reader.ReadString();
                        if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        string languageFolder = reader.ReadString();
                        if (!string.Equals(languageFolder, expectedLanguageFolder ?? string.Empty, StringComparison.Ordinal))
                        {
                            return false;
                        }

                        reader.ReadInt64();
                        LanguageCachePayload result = new LanguageCachePayload();

                        int keyedCount = reader.ReadInt32();
                        if (keyedCount < 0 || keyedCount > MaxKeyedEntries)
                        {
                            return false;
                        }

                        for (int i = 0; i < keyedCount; i++)
                        {
                            string dictionaryKey = reader.ReadString();
                            LoadedLanguage.KeyedReplacement replacement = new LoadedLanguage.KeyedReplacement
                            {
                                key = reader.ReadString(),
                                value = reader.ReadString(),
                                fileSource = reader.ReadString(),
                                fileSourceLine = reader.ReadInt32(),
                                fileSourceFullPath = reader.ReadString(),
                                isPlaceholder = reader.ReadBoolean()
                            };
                            result.KeyedReplacements[dictionaryKey] = replacement;
                        }

                        int stringFileCount = reader.ReadInt32();
                        if (stringFileCount < 0 || stringFileCount > MaxStringFiles)
                        {
                            return false;
                        }

                        for (int i = 0; i < stringFileCount; i++)
                        {
                            string fileName = reader.ReadString();
                            int lineCount = reader.ReadInt32();
                            if (lineCount < 0 || lineCount > MaxStringLinesPerFile)
                            {
                                return false;
                            }

                            List<string> lines = new List<string>(lineCount);
                            for (int j = 0; j < lineCount; j++)
                            {
                                lines.Add(reader.ReadString());
                            }

                            result.StringFiles[fileName] = lines;
                        }

                        payload = result;
                        return true;
                    }
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }
    }
}
