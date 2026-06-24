using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using RimWorld;
using Verse;

namespace FastLoader
{
    internal enum FastLoaderMode
    {
        None,
        Disabled,
        CacheHit,
        CacheMiss
    }

    internal sealed class CacheEntry
    {
        public string PackageId;
        public string SourceName;
    }

    internal sealed class CacheData
    {
        public string InputHash;
        public string CreatedUtc;
        public List<CacheEntry> Entries = new List<CacheEntry>();
        public XmlDocument ResolvedDefs;
    }

    internal static class FastLoaderRuntime
    {
        public const string CacheFormatVersion = "1";
        public const string FastLoaderVersion = "1.0.1";

        private static CacheData loadedCache;
        private static CacheData lastResolvedXmlSnapshot;
        private static string inputHash;
        private static string statusReason;
        private static int parsedDefCount;
        private static DateTime loadStartedUtc;
        private static bool cacheFallbackActive;
        private static bool lastLoadSkippedPatchApplication;
        private static int manualBuildDepth;
        private static readonly CacheEntry[] EmptyEntries = new CacheEntry[0];
        private static readonly Dictionary<string, bool> PatchContentLookup = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly FieldInfo XmlInheritanceResolvedNodesField = typeof(XmlInheritance).GetField("resolvedNodes", BindingFlags.NonPublic | BindingFlags.Static);

        public static ModContentPack ModContent;
        public static FastLoaderSettings Settings;
        public static FastLoaderMode Mode = FastLoaderMode.None;
        public static FastLoaderMode LastMode = FastLoaderMode.None;
        public static string LastStatus;
        public static string LastStatusReason;
        public static string LastInputHash;
        public static int LastParsedDefCount;
        public static double LastLoadElapsedMs;
        public static string LastXmlCacheBuildFailureReason;

        public static bool IsCacheFallbackActive
        {
            get { return cacheFallbackActive; }
        }

        public static bool IsManualBuildActive
        {
            get { return manualBuildDepth > 0; }
        }

        public static bool IsCacheHit
        {
            get { return Mode == FastLoaderMode.CacheHit && loadedCache != null; }
        }

        public static bool ShouldUseXmlCache
        {
            get { return Settings == null || Settings.UseXmlCache; }
        }

        public static bool ShouldUseTextureCache
        {
            get { return Settings == null || Settings.UseTextureCache; }
        }

        public static bool ShouldUseAtlasCache
        {
            get { return Settings == null || Settings.UseAtlasCache; }
        }

        public static IReadOnlyList<CacheEntry> CacheEntries
        {
            get
            {
                if (loadedCache != null)
                {
                    return loadedCache.Entries;
                }

                return EmptyEntries;
            }
        }

        public static XmlDocument CachedResolvedDefs
        {
            get { return loadedCache != null ? loadedCache.ResolvedDefs : null; }
        }

        public static IDisposable ManualBuildScope()
        {
            manualBuildDepth++;
            return new ManualBuildScopeHandle();
        }

        private static string RootPath
        {
            get { return Path.Combine(GenFilePaths.ConfigFolderPath, "FastLoader"); }
        }

        private sealed class ManualBuildScopeHandle : IDisposable
        {
            private bool disposed;

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                if (manualBuildDepth > 0)
                {
                    manualBuildDepth--;
                }
            }
        }

        private static string CachePath
        {
            get { return Path.Combine(RootPath, "Cache"); }
        }

        private static string ManifestPath
        {
            get { return Path.Combine(CachePath, "manifest.xml"); }
        }

        private static string ResolvedDefsPath
        {
            get { return Path.Combine(CachePath, "resolved_defs.xml"); }
        }

        public static void DeleteAllCaches()
        {
            DeleteXmlCache();
            DeleteResourceCache();
            LanguageBinaryCache.DeleteAll();
            StaticAtlasCache.DeleteAll();
            FastLoaderCacheState.Delete();
            DeleteResidualTempFiles();

            Log.Message("[FastLoader] All cache files cleared.");
        }

        public static void DeleteXmlCache()
        {
            loadedCache = null;
            inputHash = null;
            statusReason = null;
            parsedDefCount = 0;
            lastLoadSkippedPatchApplication = false;
            PatchContentLookup.Clear();

            TryDeleteFile(ManifestPath);
            TryDeleteFile(ResolvedDefsPath);
            TryDeleteFile(ManifestPath + ".tmp");
            TryDeleteFile(ResolvedDefsPath + ".tmp");
            TryDeleteEmptyDirectory(CachePath);

            Log.Message("[FastLoader] XML cache files cleared.");
        }

        public static void DeleteResourceCache()
        {
            TextureRawCache.DeleteAll();
            Log.Message("[FastLoader] Resource cache files cleared.");
        }

        public static void BeginLoad(bool hotReload)
        {
            Mode = FastLoaderMode.None;
            loadedCache = null;
            inputHash = null;
            statusReason = null;
            parsedDefCount = 0;
            cacheFallbackActive = false;
            lastLoadSkippedPatchApplication = false;
            PatchContentLookup.Clear();
            loadStartedUtc = DateTime.UtcNow;
            FastProfile.Begin();

            if (hotReload)
            {
                Mode = FastLoaderMode.Disabled;
                statusReason = "hotReload";
                FastProfile.SetStatus("DISABLED", statusReason, null);
                return;
            }

            try
            {
                using (FastProfile.Scope("Manifest/mod loadout hash"))
                {
                    inputHash = FastLoaderHasher.ComputeFastModListHash();
                }

                if (!ShouldUseXmlCache)
                {
                    Mode = FastLoaderMode.Disabled;
                    statusReason = Settings != null && !Settings.CacheEnabled
                        ? "disabled by settings; profiling vanilla XML routine"
                        : "XML cache disabled by settings";
                    FastProfile.SetStatus("DISABLED", statusReason, inputHash);
                    return;
                }

                string invalidationReason;
                if (TryGetPreloadInvalidationReason(inputHash, out invalidationReason))
                {
                    cacheFallbackActive = true;
                    Mode = FastLoaderMode.Disabled;
                    statusReason = invalidationReason;
                    FastProfile.SetStatus("DISABLED", statusReason, inputHash);
                    Log.Warning("[FastLoader] Cache disabled for this load before cache restore: " + statusReason);
                    return;
                }

                string missReason;
                CacheData cache;
                if (TryLoadCache(inputHash, out cache, out missReason))
                {
                    loadedCache = cache;
                    lastResolvedXmlSnapshot = cache;
                    Mode = FastLoaderMode.CacheHit;
                    statusReason = "manifest match";
                    FastProfile.SetStatus("HIT", statusReason, inputHash);
                    return;
                }

                Mode = FastLoaderMode.CacheMiss;
                statusReason = missReason;
                FastProfile.SetStatus("MISS", statusReason, inputHash);
            }
            catch (Exception ex)
            {
                Mode = FastLoaderMode.CacheMiss;
                statusReason = "hash/cache check exception: " + ex.GetType().Name;
                FastProfile.SetStatus("MISS", statusReason, inputHash);
                ActivateVanillaFallback(FastLoaderCacheKind.Xml, statusReason);
                Log.Warning("[FastLoader] Cache check failed. Falling back to vanilla XML load.\n" + ex);
            }
        }

        private static bool TryGetPreloadInvalidationReason(string currentInputHash, out string reason)
        {
            reason = null;

            FastLoaderCacheStateData state;
            if (!FastLoaderCacheState.TryRead(out state) || state == null)
            {
                return false;
            }

            List<string> reasons = new List<string>();
            if (!string.IsNullOrEmpty(state.InputHash) &&
                !string.Equals(state.InputHash, currentInputHash ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("active mod list/order changed since the cache was built");
            }

            string activeLanguage = FastLoaderCacheState.GetCurrentActiveLanguage();
            if (!string.IsNullOrEmpty(state.ActiveLanguage) &&
                !string.IsNullOrEmpty(activeLanguage) &&
                ShouldUseXmlCache &&
                !string.Equals(state.ActiveLanguage, activeLanguage ?? string.Empty, StringComparison.Ordinal))
            {
                reasons.Add("active language changed since the language cache was built: " + state.ActiveLanguage + " -> " + (activeLanguage ?? string.Empty));
            }

            string defaultLanguage = FastLoaderCacheState.GetCurrentDefaultLanguage();
            if (!string.IsNullOrEmpty(state.DefaultLanguage) &&
                !string.IsNullOrEmpty(defaultLanguage) &&
                ShouldUseXmlCache &&
                !string.Equals(state.DefaultLanguage, defaultLanguage ?? string.Empty, StringComparison.Ordinal))
            {
                reasons.Add("default language changed since the language cache was built: " + state.DefaultLanguage + " -> " + (defaultLanguage ?? string.Empty));
            }

            if (reasons.Count == 0)
            {
                return false;
            }

            reason = "cache invalidated before load: " + string.Join("; ", reasons.ToArray());
            return true;
        }

        public static void ActivateVanillaFallback(string stage, string detail)
        {
            cacheFallbackActive = true;
            FastLoaderCacheIssueReporter.Report(stage, detail);
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

        public static void SaveCacheFromResolvedXml(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetLookup)
        {
            if (Mode == FastLoaderMode.CacheHit)
            {
                return;
            }

            if (string.IsNullOrEmpty(inputHash) || xmlDoc == null || xmlDoc.DocumentElement == null)
            {
                return;
            }

            try
            {
                CacheData data;
                using (FastProfile.Scope("Capture resolved XML snapshot"))
                {
                    data = BuildCacheData(xmlDoc, assetLookup);
                }

                lastResolvedXmlSnapshot = data;
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to capture resolved XML snapshot.\n" + ex);
            }
        }

        public static bool WriteXmlCacheFromCurrentSnapshot()
        {
            if (lastResolvedXmlSnapshot == null ||
                lastResolvedXmlSnapshot.ResolvedDefs == null ||
                lastResolvedXmlSnapshot.ResolvedDefs.DocumentElement == null ||
                string.IsNullOrEmpty(lastResolvedXmlSnapshot.InputHash))
            {
                LastXmlCacheBuildFailureReason = "No resolved XML snapshot is available.";
                Log.Message("[FastLoader] XML cache cannot be built because no resolved XML snapshot is available.");
                return false;
            }

            try
            {
                LastXmlCacheBuildFailureReason = null;
                Directory.CreateDirectory(CachePath);
                WriteResolvedDefs(lastResolvedXmlSnapshot.ResolvedDefs);
                WriteManifest(lastResolvedXmlSnapshot);
                Log.Message("[FastLoader] XML cache written from the current resolved XML snapshot.");
                return true;
            }
            catch (Exception ex)
            {
                LastXmlCacheBuildFailureReason = ex.GetBaseException().Message;
                Log.Warning("[FastLoader] Failed to write XML cache from current snapshot.\n" + ex);
                return false;
            }
        }

        public static void SetParsedDefCount(int count)
        {
            parsedDefCount = count;
        }

        public static void CompleteLoad()
        {
            string status;
            switch (Mode)
            {
                case FastLoaderMode.CacheHit:
                    status = "HIT";
                    break;
                case FastLoaderMode.CacheMiss:
                    status = "MISS";
                    break;
                case FastLoaderMode.Disabled:
                    status = "DISABLED";
                    break;
                default:
                    status = "UNKNOWN";
                    break;
            }

            FastProfile.End(status, statusReason, inputHash, parsedDefCount);
            TimeSpan elapsed = loadStartedUtc == default(DateTime) ? TimeSpan.Zero : DateTime.UtcNow - loadStartedUtc;
            LastMode = Mode;
            LastStatus = status;
            LastStatusReason = statusReason;
            LastInputHash = inputHash;
            LastParsedDefCount = parsedDefCount;
            LastLoadElapsedMs = elapsed.TotalMilliseconds;
            lastLoadSkippedPatchApplication = Mode == FastLoaderMode.CacheHit;
            Log.Message("[FastLoader] XML cache " + status + " in " + FormatSeconds(elapsed) + ". Reason: " + (statusReason ?? string.Empty) + ". Parsed defs: " + parsedDefCount);

            Mode = FastLoaderMode.None;
            loadedCache = null;
            inputHash = null;
            statusReason = null;
            parsedDefCount = 0;
        }

        public static bool ShouldTreatAsLoadedByCachedPatches(ModContentPack mod)
        {
            if (mod == null || (Mode != FastLoaderMode.CacheHit && !lastLoadSkippedPatchApplication))
            {
                return false;
            }

            string key = mod.PackageId ?? mod.PackageIdPlayerFacing ?? mod.RootDir;
            if (string.IsNullOrEmpty(key))
            {
                key = mod.GetHashCode().ToString();
            }

            bool hasPatchContent;
            if (PatchContentLookup.TryGetValue(key, out hasPatchContent))
            {
                return hasPatchContent;
            }

            hasPatchContent = HasPatchFilesInActiveLoadFolders(mod);
            PatchContentLookup[key] = hasPatchContent;
            return hasPatchContent;
        }

        private static bool HasPatchFilesInActiveLoadFolders(ModContentPack mod)
        {
            if (mod.foldersToLoadDescendingOrder == null)
            {
                return false;
            }

            for (int i = 0; i < mod.foldersToLoadDescendingOrder.Count; i++)
            {
                string folder = mod.foldersToLoadDescendingOrder[i];
                if (string.IsNullOrEmpty(folder))
                {
                    continue;
                }

                string patchDir = Path.Combine(folder, "Patches");
                if (!Directory.Exists(patchDir))
                {
                    continue;
                }

                try
                {
                    IEnumerator<string> enumerator = Directory.EnumerateFiles(patchDir, "*.xml", SearchOption.AllDirectories).GetEnumerator();
                    try
                    {
                        if (enumerator.MoveNext())
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        if (enumerator != null)
                        {
                            enumerator.Dispose();
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return false;
        }

        private static string FormatSeconds(TimeSpan elapsed)
        {
            return elapsed.TotalSeconds.ToString("0.00") + "s";
        }

        public static ModContentPack FindMod(string packageId)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                return null;
            }

            List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
            for (int i = 0; i < mods.Count; i++)
            {
                ModContentPack mod = mods[i];
                if (string.Equals(mod.PackageId, packageId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mod.PackageIdPlayerFacing, packageId, StringComparison.OrdinalIgnoreCase))
                {
                    return mod;
                }
            }

            return null;
        }

        private static bool TryLoadCache(string expectedInputHash, out CacheData cache, out string missReason)
        {
            cache = null;
            missReason = null;

            if (!File.Exists(ManifestPath) || !File.Exists(ResolvedDefsPath))
            {
                missReason = "cache files missing";
                return false;
            }

            CacheData manifest;
            using (FastProfile.Scope("Try load XML cache: read manifest"))
            {
                manifest = ReadManifest();
            }

            if (manifest == null)
            {
                missReason = "manifest unreadable";
                return false;
            }

            if (!string.Equals(manifest.InputHash, expectedInputHash, StringComparison.OrdinalIgnoreCase))
            {
                missReason = "mod list changed";
                return false;
            }

            if (HasOnlyUnownedEntries(manifest))
            {
                missReason = "cached xml metadata missing mod ownership";
                return false;
            }

            XmlDocument document = new XmlDocument();
            using (FastProfile.Scope("Try load XML cache: read resolved_defs.xml"))
            {
                using (XmlReader reader = XmlReader.Create(ResolvedDefsPath, XmlSettings()))
                {
                    document.Load(reader);
                }
            }

            using (FastProfile.Scope("Try load XML cache: validate cached XML"))
            {
                if (document.DocumentElement == null || document.DocumentElement.Name != "Defs")
                {
                    missReason = "cached xml root invalid";
                    return false;
                }

                int nodeCount = CountElementChildren(document.DocumentElement);
                if (nodeCount != manifest.Entries.Count)
                {
                    missReason = "cached xml metadata count mismatch";
                    return false;
                }
            }

            manifest.ResolvedDefs = document;
            cache = manifest;
            return true;
        }

        private static bool HasOnlyUnownedEntries(CacheData manifest)
        {
            if (manifest == null || manifest.Entries == null || manifest.Entries.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < manifest.Entries.Count; i++)
            {
                if (!string.IsNullOrEmpty(manifest.Entries[i].PackageId))
                {
                    return false;
                }
            }

            return true;
        }

        private static CacheData BuildCacheData(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetLookup)
        {
            CacheData data = new CacheData();
            data.InputHash = inputHash;
            data.CreatedUtc = DateTime.UtcNow.ToString("o");

            XmlDocument resolvedDoc = new XmlDocument();
            XmlElement root = resolvedDoc.CreateElement("Defs");
            resolvedDoc.AppendChild(root);

            using (FastProfile.Scope("Build resolved XML cache data: resolve/import nodes"))
            {
                foreach (XmlNode node in xmlDoc.DocumentElement.ChildNodes)
                {
                    if (node.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    if (!ShouldLoadNode(node))
                    {
                        continue;
                    }

                    LoadableXmlAsset asset = null;
                    if (assetLookup != null)
                    {
                        assetLookup.TryGetValue(node, out asset);
                    }

                    if (!CanGetResolvedNode(node))
                    {
                        throw new InvalidOperationException("Cannot write FastLoader XML cache because an inherited XML node was not resolved: " + DescribeNode(node, asset));
                    }

                    XmlNode resolved = XmlInheritance.GetResolvedNodeFor(node);
                    XmlNode imported = ImportResolvedNodePreservingOriginalRootName(resolvedDoc, node, resolved);
                    RemoveRootInheritanceAttributes(imported);
                    root.AppendChild(imported);

                    data.Entries.Add(new CacheEntry
                    {
                        PackageId = asset != null && asset.mod != null ? asset.mod.PackageId : string.Empty,
                        SourceName = asset != null ? asset.name : "Unknown"
                    });
                }
            }

            data.ResolvedDefs = resolvedDoc;
            return data;
        }

        private static bool ShouldLoadNode(XmlNode node)
        {
            if (node.Attributes == null)
            {
                return true;
            }

            XmlAttribute abstractAttribute = node.Attributes["Abstract"];
            if (abstractAttribute != null && string.Equals(abstractAttribute.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            XmlAttribute mayRequire = node.Attributes["MayRequire"];
            if (mayRequire != null && !ModLister.AllModsActiveNoSuffix(mayRequire.Value.ToLower().Split(',')))
            {
                return false;
            }

            XmlAttribute mayRequireAnyOf = node.Attributes["MayRequireAnyOf"];
            if (mayRequireAnyOf == null)
            {
                return true;
            }

            string[] anyOf = mayRequireAnyOf.Value.ToLower().Split(new[] { ',' }, StringSplitOptions.None);
            return anyOf.Length == 0 || ModLister.AnyModActiveNoSuffix(anyOf);
        }

        private static bool CanGetResolvedNode(XmlNode node)
        {
            if (node == null || node.Attributes == null || node.Attributes["ParentName"] == null)
            {
                return true;
            }

            IDictionary resolvedNodes = XmlInheritanceResolvedNodesField != null ? XmlInheritanceResolvedNodesField.GetValue(null) as IDictionary : null;
            return resolvedNodes != null && resolvedNodes.Contains(node);
        }

        private static string DescribeNode(XmlNode node, LoadableXmlAsset asset)
        {
            string source = asset != null ? asset.FullFilePath : "Unknown";
            string parentName = node != null && node.Attributes != null && node.Attributes["ParentName"] != null ? node.Attributes["ParentName"].Value : string.Empty;
            return node.Name + " ParentName=" + parentName + " source=" + source;
        }

        private static XmlNode ImportResolvedNodePreservingOriginalRootName(XmlDocument targetDocument, XmlNode originalNode, XmlNode resolvedNode)
        {
            if (resolvedNode == null || originalNode == null || resolvedNode.NodeType != XmlNodeType.Element || string.Equals(resolvedNode.Name, originalNode.Name, StringComparison.Ordinal))
            {
                return targetDocument.ImportNode(resolvedNode, true);
            }

            XmlElement element = targetDocument.CreateElement(originalNode.Name);
            if (resolvedNode.Attributes != null)
            {
                foreach (XmlAttribute attribute in resolvedNode.Attributes)
                {
                    XmlAttribute importedAttribute = (XmlAttribute)targetDocument.ImportNode(attribute, true);
                    element.Attributes.Append(importedAttribute);
                }
            }

            foreach (XmlNode child in resolvedNode.ChildNodes)
            {
                element.AppendChild(targetDocument.ImportNode(child, true));
            }

            return element;
        }

        private static void RemoveRootInheritanceAttributes(XmlNode node)
        {
            if (node == null || node.Attributes == null)
            {
                return;
            }

            XmlAttribute parentName = node.Attributes["ParentName"];
            if (parentName != null)
            {
                node.Attributes.Remove(parentName);
            }

            XmlAttribute inherit = node.Attributes["Inherit"];
            if (inherit != null)
            {
                node.Attributes.Remove(inherit);
            }
        }

        private static void WriteResolvedDefs(XmlDocument document)
        {
            WriteXmlAtomically(ResolvedDefsPath, document);
        }

        private static void WriteManifest(CacheData data)
        {
            XmlDocument document = new XmlDocument();
            XmlElement root = document.CreateElement("FastLoaderCache");
            document.AppendChild(root);
            AppendElement(document, root, "formatVersion", CacheFormatVersion);
            AppendElement(document, root, "fastLoaderVersion", FastLoaderVersion);
            AppendElement(document, root, "gameVersion", VersionControl.CurrentVersionStringWithRev ?? string.Empty);
            AppendElement(document, root, "inputHash", data.InputHash ?? string.Empty);
            AppendElement(document, root, "createdUtc", data.CreatedUtc ?? string.Empty);

            XmlElement entries = document.CreateElement("entries");
            root.AppendChild(entries);
            for (int i = 0; i < data.Entries.Count; i++)
            {
                CacheEntry entry = data.Entries[i];
                XmlElement element = document.CreateElement("entry");
                element.SetAttribute("packageId", entry.PackageId ?? string.Empty);
                element.SetAttribute("sourceName", entry.SourceName ?? "Unknown");
                entries.AppendChild(element);
            }

            WriteXmlAtomically(ManifestPath, document);
        }

        private static CacheData ReadManifest()
        {
            XmlDocument document = new XmlDocument();
            using (XmlReader reader = XmlReader.Create(ManifestPath, XmlSettings()))
            {
                document.Load(reader);
            }

            XmlElement root = document.DocumentElement;
            if (root == null || root.Name != "FastLoaderCache")
            {
                return null;
            }

            string formatVersion = ChildText(root, "formatVersion");
            string gameVersion = ChildText(root, "gameVersion");
            string fastLoaderVersion = ChildText(root, "fastLoaderVersion");
            if (formatVersion != CacheFormatVersion ||
                fastLoaderVersion != FastLoaderVersion ||
                gameVersion != (VersionControl.CurrentVersionStringWithRev ?? string.Empty))
            {
                return null;
            }

            CacheData data = new CacheData();
            data.InputHash = ChildText(root, "inputHash");
            data.CreatedUtc = ChildText(root, "createdUtc");

            XmlNode entries = root.SelectSingleNode("entries");
            if (entries != null)
            {
                foreach (XmlNode node in entries.ChildNodes)
                {
                    if (node.NodeType != XmlNodeType.Element || node.Name != "entry")
                    {
                        continue;
                    }

                    data.Entries.Add(new CacheEntry
                    {
                        PackageId = AttributeText(node, "packageId"),
                        SourceName = AttributeText(node, "sourceName")
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
            string tempPath = path + ".tmp";
            XmlWriterSettings settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = false,
                NewLineHandling = NewLineHandling.None
            };

            using (XmlWriter writer = XmlWriter.Create(tempPath, settings))
            {
                document.Save(writer);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }

        private static void DeleteResidualTempFiles()
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            try
            {
                string[] files = Directory.GetFiles(RootPath, "*.tmp", SearchOption.AllDirectories);
                for (int i = 0; i < files.Length; i++)
                {
                    TryDeleteFile(files[i]);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to delete residual temp cache files.\n" + ex);
            }
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

        private static int CountElementChildren(XmlNode node)
        {
            int count = 0;
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element)
                {
                    count++;
                }
            }

            return count;
        }
    }

    internal static class FastLoaderHasher
    {
        public static string ComputeFastModListHash()
        {
            using (SHA256 sha = SHA256.Create())
            {
                using (FastProfile.Scope("Input hash: fast mod list only"))
                {
                    AppendString(sha, "FastLoaderModLoadoutHashV2");
                    AppendString(sha, VersionControl.CurrentVersionStringWithRev ?? string.Empty);

                    List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
                    AppendString(sha, "modCount=" + mods.Count);
                    for (int i = 0; i < mods.Count; i++)
                    {
                        ModContentPack mod = mods[i];
                        AppendString(sha, i.ToString());
                        AppendString(sha, mod.PackageId ?? string.Empty);
                        AppendString(sha, mod.PackageIdPlayerFacing ?? string.Empty);
                    }
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BytesToHex(sha.Hash);
            }
        }

        private static void AppendString(HashAlgorithm sha, string value)
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
    }
}
