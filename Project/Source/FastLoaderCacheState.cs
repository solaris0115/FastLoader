using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml;
using Verse;

namespace FastLoader
{
    internal static class FastLoaderCacheKind
    {
        public const string Xml = "xml";
        public const string Language = "language";
        public const string Texture = "texture";
        public const string Atlas = "atlas";
    }

    internal sealed class FastLoaderCacheStageState
    {
        public string Kind;
        public DateTime BuiltUtc;
        public bool Success;
        public int ItemCount;
    }

    internal sealed class FastLoaderCacheStateData
    {
        public string InputHash;
        public DateTime CreatedUtc;
        public string ActiveLanguage;
        public string DefaultLanguage;
        public List<FastLoaderCacheStateMod> Mods = new List<FastLoaderCacheStateMod>();
        public List<FastLoaderCacheStageState> Stages = new List<FastLoaderCacheStageState>();

        public DateTime GetUpdateReferenceUtc()
        {
            DateTime result = DateTime.MinValue;
            for (int i = 0; i < Stages.Count; i++)
            {
                FastLoaderCacheStageState stage = Stages[i];
                if (stage == null || !stage.Success || stage.BuiltUtc == DateTime.MinValue)
                {
                    continue;
                }

                if (result == DateTime.MinValue || stage.BuiltUtc < result)
                {
                    result = stage.BuiltUtc;
                }
            }

            return result != DateTime.MinValue ? result : CreatedUtc;
        }
    }

    internal sealed class FastLoaderCacheStateMod
    {
        public string PackageId;
        public string Name;
        public string WorkshopId;
    }

    internal static class FastLoaderCacheState
    {
        private const string FormatVersion = "3";
        private static readonly FieldInfo PublishedFileIdField =
            typeof(ModMetaData).GetField("publishedFileIdInt", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PublishedFileIdValueField =
            PublishedFileIdField != null ? PublishedFileIdField.FieldType.GetField("m_PublishedFileId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;

        private static string StatePath
        {
            get { return Path.Combine(GenFilePaths.ConfigFolderPath, "FastLoader", "cache_state.xml"); }
        }

        public static void MarkBuilt(string kind, bool success, int itemCount)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                FastLoaderCacheStateData data = BuildCurrentState();
                FastLoaderCacheStateData previous;
                if (TryRead(out previous) && string.Equals(previous.InputHash, data.InputHash, StringComparison.OrdinalIgnoreCase))
                {
                    data.Stages = previous.Stages ?? new List<FastLoaderCacheStageState>();
                }

                UpsertStage(data, kind, success, itemCount);
                WriteState(data);
                ClearIgnoredUpdateSignature();
                Log.Message("[FastLoader] Cache state updated in " + FormatSeconds(stopwatch.Elapsed.TotalMilliseconds) + ": " + kind + ", success=" + success + ", items=" + itemCount + ", mods=" + data.Mods.Count);
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to update cache state.\n" + ex);
            }
        }

        public static bool TryRead(out FastLoaderCacheStateData data)
        {
            data = null;
            if (!File.Exists(StatePath))
            {
                return false;
            }

            try
            {
                data = ReadState();
                return data != null;
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to read cache state. Workshop update check skipped.\n" + ex);
                return false;
            }
        }

        public static void Delete()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    File.Delete(StatePath);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to delete cache state file.\n" + ex);
            }

            ClearIgnoredUpdateSignature();
        }

        public static void RemoveStages(params string[] kinds)
        {
            if (kinds == null || kinds.Length == 0)
            {
                return;
            }

            try
            {
                FastLoaderCacheStateData data;
                if (!TryRead(out data) || data == null || data.Stages == null || data.Stages.Count == 0)
                {
                    return;
                }

                for (int i = data.Stages.Count - 1; i >= 0; i--)
                {
                    FastLoaderCacheStageState stage = data.Stages[i];
                    if (stage == null || ContainsKind(kinds, stage.Kind))
                    {
                        data.Stages.RemoveAt(i);
                    }
                }

                if (data.Stages.Count == 0)
                {
                    Delete();
                    return;
                }

                WriteState(data);
                ClearIgnoredUpdateSignature();
                Log.Message("[FastLoader] Cache state stages removed: " + string.Join(", ", kinds));
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to remove cache state stages.\n" + ex);
            }
        }

        private static FastLoaderCacheStateData BuildCurrentState()
        {
            FastLoaderCacheStateData data = new FastLoaderCacheStateData();
            data.InputHash = FastLoaderHasher.ComputeFastModListHash();
            data.CreatedUtc = DateTime.UtcNow;
            data.ActiveLanguage = GetLanguageFolder(LanguageDatabase.activeLanguage);
            data.DefaultLanguage = GetLanguageFolder(LanguageDatabase.defaultLanguage);

            List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
            for (int i = 0; i < mods.Count; i++)
            {
                ModContentPack mod = mods[i];
                if (mod == null)
                {
                    continue;
                }

                data.Mods.Add(new FastLoaderCacheStateMod
                {
                    PackageId = mod.PackageId ?? string.Empty,
                    Name = mod.Name ?? mod.PackageId ?? string.Empty,
                    WorkshopId = ExtractWorkshopId(mod) ?? string.Empty
                });
            }

            return data;
        }

        private static void UpsertStage(FastLoaderCacheStateData data, string kind, bool success, int itemCount)
        {
            FastLoaderCacheStageState stage = null;
            for (int i = 0; i < data.Stages.Count; i++)
            {
                if (string.Equals(data.Stages[i].Kind, kind, StringComparison.OrdinalIgnoreCase))
                {
                    stage = data.Stages[i];
                    break;
                }
            }

            if (stage == null)
            {
                stage = new FastLoaderCacheStageState();
                data.Stages.Add(stage);
            }

            stage.Kind = kind ?? string.Empty;
            stage.Success = success;
            stage.ItemCount = itemCount;
            stage.BuiltUtc = DateTime.UtcNow;
        }

        private static bool ContainsKind(string[] kinds, string value)
        {
            for (int i = 0; i < kinds.Length; i++)
            {
                if (string.Equals(kinds[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void WriteState(FastLoaderCacheStateData data)
        {
            string directory = Path.GetDirectoryName(StatePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            XmlDocument document = new XmlDocument();
            XmlElement root = document.CreateElement("FastLoaderCacheState");
            document.AppendChild(root);
            root.SetAttribute("formatVersion", FormatVersion);
            AppendElement(document, root, "inputHash", data.InputHash ?? string.Empty);
            AppendElement(document, root, "createdUtc", data.CreatedUtc.ToString("o"));
            AppendElement(document, root, "activeLanguage", data.ActiveLanguage ?? string.Empty);
            AppendElement(document, root, "defaultLanguage", data.DefaultLanguage ?? string.Empty);

            XmlElement stages = document.CreateElement("stages");
            root.AppendChild(stages);
            for (int i = 0; i < data.Stages.Count; i++)
            {
                FastLoaderCacheStageState stage = data.Stages[i];
                XmlElement element = document.CreateElement("stage");
                element.SetAttribute("kind", stage.Kind ?? string.Empty);
                element.SetAttribute("success", stage.Success ? "true" : "false");
                element.SetAttribute("builtUtc", stage.BuiltUtc.ToString("o"));
                element.SetAttribute("itemCount", stage.ItemCount.ToString());
                stages.AppendChild(element);
            }

            XmlElement mods = document.CreateElement("mods");
            root.AppendChild(mods);
            for (int i = 0; i < data.Mods.Count; i++)
            {
                FastLoaderCacheStateMod mod = data.Mods[i];
                XmlElement element = document.CreateElement("mod");
                element.SetAttribute("packageId", mod.PackageId ?? string.Empty);
                element.SetAttribute("name", mod.Name ?? string.Empty);
                element.SetAttribute("workshopId", mod.WorkshopId ?? string.Empty);
                mods.AppendChild(element);
            }

            string tempPath = StatePath + ".tmp";
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

            if (File.Exists(StatePath))
            {
                File.Delete(StatePath);
            }

            File.Move(tempPath, StatePath);
        }

        private static FastLoaderCacheStateData ReadState()
        {
            XmlDocument document = new XmlDocument();
            using (XmlReader reader = XmlReader.Create(StatePath, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true }))
            {
                document.Load(reader);
            }

            XmlElement root = document.DocumentElement;
            if (root == null || root.Name != "FastLoaderCacheState")
            {
                return null;
            }

            string formatVersion = root.GetAttribute("formatVersion");
            if (formatVersion != FormatVersion && formatVersion != "2" && formatVersion != "1")
            {
                return null;
            }

            FastLoaderCacheStateData data = new FastLoaderCacheStateData();
            data.InputHash = ChildText(root, "inputHash");
            DateTime createdUtc;
            if (!DateTime.TryParse(ChildText(root, "createdUtc"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out createdUtc))
            {
                return null;
            }

            data.CreatedUtc = createdUtc.ToUniversalTime();
            data.ActiveLanguage = ChildText(root, "activeLanguage");
            data.DefaultLanguage = ChildText(root, "defaultLanguage");
            if (formatVersion == "1")
            {
                data.Stages.Add(new FastLoaderCacheStageState
                {
                    Kind = "legacy",
                    BuiltUtc = data.CreatedUtc,
                    Success = true,
                    ItemCount = 0
                });
            }
            else
            {
                ReadStages(root, data);
            }

            ReadMods(root, data);
            return data;
        }

        public static string GetCurrentActiveLanguage()
        {
            return GetLanguageFolder(LanguageDatabase.activeLanguage);
        }

        public static string GetCurrentDefaultLanguage()
        {
            return GetLanguageFolder(LanguageDatabase.defaultLanguage);
        }

        private static string GetLanguageFolder(LoadedLanguage language)
        {
            return language != null ? language.folderName ?? string.Empty : string.Empty;
        }

        private static void ReadStages(XmlElement root, FastLoaderCacheStateData data)
        {
            XmlNode stages = root.SelectSingleNode("stages");
            if (stages == null)
            {
                return;
            }

            foreach (XmlNode node in stages.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element || node.Name != "stage")
                {
                    continue;
                }

                DateTime builtUtc;
                if (!DateTime.TryParse(AttributeText(node, "builtUtc"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out builtUtc))
                {
                    continue;
                }

                int itemCount;
                int.TryParse(AttributeText(node, "itemCount"), out itemCount);
                data.Stages.Add(new FastLoaderCacheStageState
                {
                    Kind = AttributeText(node, "kind"),
                    BuiltUtc = builtUtc.ToUniversalTime(),
                    Success = string.Equals(AttributeText(node, "success"), "true", StringComparison.OrdinalIgnoreCase),
                    ItemCount = itemCount
                });
            }
        }

        private static void ReadMods(XmlElement root, FastLoaderCacheStateData data)
        {
            XmlNode mods = root.SelectSingleNode("mods");
            if (mods == null)
            {
                return;
            }

            foreach (XmlNode node in mods.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element || node.Name != "mod")
                {
                    continue;
                }

                data.Mods.Add(new FastLoaderCacheStateMod
                {
                    PackageId = AttributeText(node, "packageId"),
                    Name = AttributeText(node, "name"),
                    WorkshopId = AttributeText(node, "workshopId")
                });
            }
        }

        private static void ClearIgnoredUpdateSignature()
        {
            if (FastLoaderRuntime.Settings == null || string.IsNullOrEmpty(FastLoaderRuntime.Settings.IgnoredWorkshopUpdateSignature))
            {
                return;
            }

            FastLoaderRuntime.Settings.IgnoredWorkshopUpdateSignature = string.Empty;
            FastLoaderRuntime.Settings.Write();
        }

        private static string ExtractWorkshopId(ModContentPack mod)
        {
            string id = ExtractPublishedFileId(mod);
            if (!string.IsNullOrEmpty(id))
            {
                return id;
            }

            return ExtractWorkshopIdFromRoot(mod.RootDir);
        }

        private static string ExtractPublishedFileId(ModContentPack mod)
        {
            try
            {
                ModMetaData metadata = mod.ModMetaData;
                if (metadata == null || !metadata.OnSteamWorkshop || PublishedFileIdField == null || PublishedFileIdValueField == null)
                {
                    return null;
                }

                object wrapper = PublishedFileIdField.GetValue(metadata);
                object value = PublishedFileIdValueField.GetValue(wrapper);
                if (value == null)
                {
                    return null;
                }

                ulong id = Convert.ToUInt64(value);
                return id > 0UL ? id.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractWorkshopIdFromRoot(string rootDir)
        {
            if (string.IsNullOrEmpty(rootDir))
            {
                return null;
            }

            string[] parts = rootDir.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 2; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i - 2], "workshop", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(parts[i - 1], "content", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(parts[i], "294100", StringComparison.OrdinalIgnoreCase) &&
                    IsUnsignedInteger(parts[i + 1]))
                {
                    return parts[i + 1];
                }
            }

            return null;
        }

        private static bool IsUnsignedInteger(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private static string FormatSeconds(double elapsedMs)
        {
            return (elapsedMs / 1000.0).ToString("0.00") + "s";
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
    }
}
