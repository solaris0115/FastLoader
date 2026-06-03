using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace FastLoader
{
    internal static class DefInjectedBinaryCache
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FLDI");
        private const int MaxStrings = 2000000;
        private const int MaxPackages = 10000;
        private const int MaxInjections = 2000000;
        private const int MaxListItemsPerInjection = 200000;

        private static readonly FieldInfo InjectionsField = AccessTools.Field(typeof(DefInjectionPackage), "injections");
        private static readonly Type DefInjectionType = typeof(DefInjectionPackage).GetNestedType("DefInjection", BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo PathField = AccessTools.Field(DefInjectionType, "path");
        private static readonly FieldInfo NormalizedPathField = AccessTools.Field(DefInjectionType, "normalizedPath");
        private static readonly FieldInfo NonBackCompatiblePathField = AccessTools.Field(DefInjectionType, "nonBackCompatiblePath");
        private static readonly FieldInfo SuggestedPathField = AccessTools.Field(DefInjectionType, "suggestedPath");
        private static readonly FieldInfo InjectionField = AccessTools.Field(DefInjectionType, "injection");
        private static readonly FieldInfo FullListInjectionField = AccessTools.Field(DefInjectionType, "fullListInjection");
        private static readonly FieldInfo FullListInjectionCommentsField = AccessTools.Field(DefInjectionType, "fullListInjectionComments");
        private static readonly FieldInfo FileSourceField = AccessTools.Field(DefInjectionType, "fileSource");
        private static readonly FieldInfo IsPlaceholderField = AccessTools.Field(DefInjectionType, "isPlaceholder");

        public static void Write(string path, LoadedLanguage language)
        {
            if (language == null || !CanUseReflection())
            {
                return;
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            StringTableBuilder strings = new StringTableBuilder();
            List<CachedPackageRecord> packages = CapturePackages(language, strings);
            string tempPath = path + ".tmp";

            using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
            using (BinaryWriter writer = new BinaryWriter(fs, Encoding.UTF8))
            {
                writer.Write(Magic);
                strings.Write(writer);

                writer.Write(packages.Count);
                for (int i = 0; i < packages.Count; i++)
                {
                    CachedPackageRecord package = packages[i];
                    writer.Write(package.DefTypeStringId);
                    writer.Write(package.Injections.Count);
                    for (int j = 0; j < package.Injections.Count; j++)
                    {
                        WriteInjection(writer, package.Injections[j]);
                    }
                }
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }

        public static bool TryRead(string path, LoadedLanguage language, out int packageCount, out int injectionCount)
        {
            packageCount = 0;
            injectionCount = 0;

            if (language == null || !CanUseReflection() || !File.Exists(path))
            {
                return false;
            }

            try
            {
                List<DefInjectionPackage> packages = new List<DefInjectionPackage>();
                int totalInjections = 0;

                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024))
                using (BinaryReader reader = new BinaryReader(fs, Encoding.UTF8))
                {
                    if (!ReadMagic(reader))
                    {
                        return false;
                    }

                    string[] strings = ReadStringTable(reader);
                    int packageRecordCount = reader.ReadInt32();
                    if (packageRecordCount < 0 || packageRecordCount > MaxPackages)
                    {
                        return false;
                    }

                    for (int i = 0; i < packageRecordCount; i++)
                    {
                        Type defType = ResolveDefType(ReadStringById(reader, strings));
                        if (defType == null)
                        {
                            return false;
                        }

                        DefInjectionPackage package = new DefInjectionPackage(defType);
                        IDictionary dictionary = GetInjectionDictionary(package);
                        if (dictionary == null)
                        {
                            return false;
                        }

                        int count = reader.ReadInt32();
                        if (count < 0 || count > MaxInjections)
                        {
                            return false;
                        }

                        for (int j = 0; j < count; j++)
                        {
                            ReadInjection(reader, strings, dictionary);
                        }

                        totalInjections += count;
                        packages.Add(package);
                    }
                }

                language.defInjections.Clear();
                for (int i = 0; i < packages.Count; i++)
                {
                    language.defInjections.Add(packages[i]);
                }

                packageCount = packages.Count;
                injectionCount = totalInjections;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool CanUseReflection()
        {
            return InjectionsField != null &&
                DefInjectionType != null &&
                PathField != null &&
                NormalizedPathField != null &&
                NonBackCompatiblePathField != null &&
                SuggestedPathField != null &&
                InjectionField != null &&
                FullListInjectionField != null &&
                FullListInjectionCommentsField != null &&
                FileSourceField != null &&
                IsPlaceholderField != null;
        }

        private static List<CachedPackageRecord> CapturePackages(LoadedLanguage language, StringTableBuilder strings)
        {
            List<CachedPackageRecord> result = new List<CachedPackageRecord>();
            for (int i = 0; i < language.defInjections.Count; i++)
            {
                DefInjectionPackage package = language.defInjections[i];
                if (package == null || package.defType == null)
                {
                    continue;
                }

                object dictionaryObject = InjectionsField.GetValue(package);
                List<DictionaryEntry> entries = GetDictionaryEntries(dictionaryObject);
                entries.Sort((left, right) => string.CompareOrdinal(left.Key as string, right.Key as string));

                CachedPackageRecord record = new CachedPackageRecord
                {
                    DefTypeStringId = strings.GetId(package.defType.FullName)
                };

                for (int j = 0; j < entries.Count; j++)
                {
                    record.Injections.Add(CaptureInjection(entries[j], strings));
                }

                result.Add(record);
            }

            result.Sort((left, right) => string.CompareOrdinal(strings.GetValue(left.DefTypeStringId), strings.GetValue(right.DefTypeStringId)));
            return result;
        }

        private static List<DictionaryEntry> GetDictionaryEntries(object dictionaryObject)
        {
            List<DictionaryEntry> result = new List<DictionaryEntry>();
            IDictionary dictionary = dictionaryObject as IDictionary;
            if (dictionary != null)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    result.Add(entry);
                }

                return result;
            }

            IEnumerable enumerable = dictionaryObject as IEnumerable;
            if (enumerable == null)
            {
                return result;
            }

            foreach (object item in enumerable)
            {
                Type itemType = item.GetType();
                PropertyInfo keyProperty = itemType.GetProperty("Key");
                PropertyInfo valueProperty = itemType.GetProperty("Value");
                if (keyProperty != null && valueProperty != null)
                {
                    result.Add(new DictionaryEntry(keyProperty.GetValue(item, null), valueProperty.GetValue(item, null)));
                }
            }

            return result;
        }

        private static CachedInjectionRecord CaptureInjection(DictionaryEntry entry, StringTableBuilder strings)
        {
            object injection = entry.Value;
            CachedInjectionRecord record = new CachedInjectionRecord
            {
                KeyStringId = strings.GetId(entry.Key as string),
                PathStringId = strings.GetId(GetStringField(PathField, injection)),
                NormalizedPathStringId = strings.GetId(GetStringField(NormalizedPathField, injection)),
                NonBackCompatiblePathStringId = strings.GetId(GetStringField(NonBackCompatiblePathField, injection)),
                SuggestedPathStringId = strings.GetId(GetStringField(SuggestedPathField, injection)),
                InjectionStringId = strings.GetId(GetStringField(InjectionField, injection)),
                FileSourceStringId = strings.GetId(GetStringField(FileSourceField, injection)),
                IsPlaceholder = (bool)IsPlaceholderField.GetValue(injection)
            };

            List<string> fullList = FullListInjectionField.GetValue(injection) as List<string>;
            if (fullList != null)
            {
                for (int i = 0; i < fullList.Count; i++)
                {
                    record.FullListStringIds.Add(strings.GetId(fullList[i]));
                }
            }

            List<Pair<int, string>> comments = FullListInjectionCommentsField.GetValue(injection) as List<Pair<int, string>>;
            if (comments != null)
            {
                for (int i = 0; i < comments.Count; i++)
                {
                    record.FullListComments.Add(new CachedCommentRecord
                    {
                        Index = comments[i].First,
                        TextStringId = strings.GetId(comments[i].Second)
                    });
                }
            }

            return record;
        }

        private static string GetStringField(FieldInfo field, object target)
        {
            return field.GetValue(target) as string;
        }

        private static void WriteInjection(BinaryWriter writer, CachedInjectionRecord injection)
        {
            writer.Write(injection.KeyStringId);
            writer.Write(injection.PathStringId);
            writer.Write(injection.NormalizedPathStringId);
            writer.Write(injection.NonBackCompatiblePathStringId);
            writer.Write(injection.SuggestedPathStringId);
            writer.Write(injection.InjectionStringId);
            writer.Write(injection.FileSourceStringId);
            writer.Write(injection.IsPlaceholder);

            writer.Write(injection.FullListStringIds.Count);
            for (int i = 0; i < injection.FullListStringIds.Count; i++)
            {
                writer.Write(injection.FullListStringIds[i]);
            }

            writer.Write(injection.FullListComments.Count);
            for (int i = 0; i < injection.FullListComments.Count; i++)
            {
                writer.Write(injection.FullListComments[i].Index);
                writer.Write(injection.FullListComments[i].TextStringId);
            }
        }

        private static void ReadInjection(BinaryReader reader, string[] strings, IDictionary dictionary)
        {
            string key = ReadStringById(reader, strings);
            object injection = Activator.CreateInstance(DefInjectionType, true);

            PathField.SetValue(injection, ReadStringById(reader, strings));
            NormalizedPathField.SetValue(injection, ReadStringById(reader, strings));
            NonBackCompatiblePathField.SetValue(injection, ReadStringById(reader, strings));
            SuggestedPathField.SetValue(injection, ReadStringById(reader, strings));
            InjectionField.SetValue(injection, ReadStringById(reader, strings));
            FileSourceField.SetValue(injection, ReadStringById(reader, strings));
            IsPlaceholderField.SetValue(injection, reader.ReadBoolean());

            int fullListCount = reader.ReadInt32();
            if (fullListCount < 0 || fullListCount > MaxListItemsPerInjection)
            {
                throw new InvalidDataException("Invalid DefInjected full list count.");
            }

            List<string> fullList = null;
            if (fullListCount > 0)
            {
                fullList = new List<string>(fullListCount);
                for (int i = 0; i < fullListCount; i++)
                {
                    fullList.Add(ReadStringById(reader, strings));
                }
            }

            FullListInjectionField.SetValue(injection, fullList);

            int commentCount = reader.ReadInt32();
            if (commentCount < 0 || commentCount > MaxListItemsPerInjection)
            {
                throw new InvalidDataException("Invalid DefInjected comment count.");
            }

            List<Pair<int, string>> comments = null;
            if (commentCount > 0)
            {
                comments = new List<Pair<int, string>>(commentCount);
                for (int i = 0; i < commentCount; i++)
                {
                    int index = reader.ReadInt32();
                    string text = ReadStringById(reader, strings);
                    comments.Add(new Pair<int, string>(index, text));
                }
            }

            FullListInjectionCommentsField.SetValue(injection, comments);
            dictionary[key] = injection;
        }

        private static IDictionary GetInjectionDictionary(DefInjectionPackage package)
        {
            return InjectionsField.GetValue(package) as IDictionary;
        }

        private static Type ResolveDefType(string defTypeName)
        {
            if (string.IsNullOrEmpty(defTypeName))
            {
                return null;
            }

            Type result = GenTypes.GetTypeInAnyAssembly(defTypeName, null);
            return result ?? Type.GetType(defTypeName, false);
        }

        private static bool ReadMagic(BinaryReader reader)
        {
            byte[] magic = reader.ReadBytes(4);
            return magic.Length == 4 &&
                magic[0] == Magic[0] &&
                magic[1] == Magic[1] &&
                magic[2] == Magic[2] &&
                magic[3] == Magic[3];
        }

        private static string[] ReadStringTable(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxStrings)
            {
                throw new InvalidDataException("Invalid DefInjected string table count.");
            }

            string[] strings = new string[count];
            for (int i = 0; i < count; i++)
            {
                strings[i] = reader.ReadString();
            }

            return strings;
        }

        private static string ReadStringById(BinaryReader reader, string[] strings)
        {
            int id = reader.ReadInt32();
            if (id == -1)
            {
                return null;
            }

            if (id < 0 || id >= strings.Length)
            {
                throw new InvalidDataException("Invalid DefInjected string id.");
            }

            return strings[id];
        }

        private sealed class CachedPackageRecord
        {
            public int DefTypeStringId;
            public readonly List<CachedInjectionRecord> Injections = new List<CachedInjectionRecord>();
        }

        private sealed class CachedInjectionRecord
        {
            public int KeyStringId;
            public int PathStringId;
            public int NormalizedPathStringId;
            public int NonBackCompatiblePathStringId;
            public int SuggestedPathStringId;
            public int InjectionStringId;
            public int FileSourceStringId;
            public bool IsPlaceholder;
            public readonly List<int> FullListStringIds = new List<int>();
            public readonly List<CachedCommentRecord> FullListComments = new List<CachedCommentRecord>();
        }

        private sealed class CachedCommentRecord
        {
            public int Index;
            public int TextStringId;
        }

        private sealed class StringTableBuilder
        {
            private readonly Dictionary<string, int> ids = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly List<string> values = new List<string>();

            public int GetId(string value)
            {
                if (value == null)
                {
                    return -1;
                }

                int id;
                if (ids.TryGetValue(value, out id))
                {
                    return id;
                }

                id = values.Count;
                ids[value] = id;
                values.Add(value);
                return id;
            }

            public string GetValue(int id)
            {
                return values[id];
            }

            public void Write(BinaryWriter writer)
            {
                writer.Write(values.Count);
                for (int i = 0; i < values.Count; i++)
                {
                    writer.Write(values[i] ?? string.Empty);
                }
            }
        }
    }
}
