using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;

namespace FastLoader
{
    internal static class FastDefInjectedApplier
    {
        private const BindingFlags FieldBindingFlags = BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private enum PathPartKind
        {
            Field,
            ListIndex,
            ListHandle,
            ListHandleWithIndex
        }

        private sealed class PathState
        {
            public string EffectivePath;
            public string NormalizedPath;
            public string SuggestedPath;
            public bool WasTKey;
            public string[] Parts;
        }

        private struct FieldCacheKey
        {
            public Type Type;
            public string Name;

            public FieldCacheKey(Type type, string name)
            {
                Type = type;
                Name = name ?? string.Empty;
            }
        }

        private sealed class FieldCacheKeyComparer : IEqualityComparer<FieldCacheKey>
        {
            public bool Equals(FieldCacheKey x, FieldCacheKey y)
            {
                return x.Type == y.Type && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(FieldCacheKey obj)
            {
                int typeHash = obj.Type != null ? obj.Type.GetHashCode() : 0;
                return (typeHash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name ?? string.Empty);
            }
        }

        private struct DefCacheKey
        {
            public Type Type;
            public string DefName;

            public DefCacheKey(Type type, string defName)
            {
                Type = type;
                DefName = defName ?? string.Empty;
            }
        }

        private sealed class DefCacheKeyComparer : IEqualityComparer<DefCacheKey>
        {
            public bool Equals(DefCacheKey x, DefCacheKey y)
            {
                return x.Type == y.Type && string.Equals(x.DefName, y.DefName, StringComparison.Ordinal);
            }

            public int GetHashCode(DefCacheKey obj)
            {
                int typeHash = obj.Type != null ? obj.Type.GetHashCode() : 0;
                return (typeHash * 397) ^ StringComparer.Ordinal.GetHashCode(obj.DefName ?? string.Empty);
            }
        }

        private static readonly Dictionary<FieldCacheKey, FieldInfo> FieldCache =
            new Dictionary<FieldCacheKey, FieldInfo>(new FieldCacheKeyComparer());
        private static readonly Dictionary<Type, PropertyInfo> CountPropertyCache =
            new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> ItemPropertyCache =
            new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<string, PathState> PathCache =
            new Dictionary<string, PathState>(StringComparer.Ordinal);
        private static readonly Dictionary<DefCacheKey, Def> DefCache =
            new Dictionary<DefCacheKey, Def>(new DefCacheKeyComparer());

        public static bool TryInjectBefore(LoadedLanguage language)
        {
            return TryInject(language, false, false, "FastLoader.DefInjected.FastBefore");
        }

        public static bool TryInjectAfter(LoadedLanguage language)
        {
            return TryInject(language, true, true, "FastLoader.DefInjected.FastAfter");
        }

        private static bool TryInject(LoadedLanguage language, bool errorOnDefNotFound, bool reportErrors, string scopeName)
        {
            if (!IsEnabled() || language == null)
            {
                return false;
            }

            try
            {
                using (FastLoaderProfiler.Scope(scopeName + " | " + Describe(language)))
                {
                    if (!LanguageBinaryCache.IsDataLoaded(language))
                    {
                        language.LoadData();
                    }

                    DefCache.Clear();
                    PathCache.Clear();

                    int errorCount = language.loadErrors.Count;
                    for (int i = 0; i < language.defInjections.Count; i++)
                    {
                        DefInjectionPackage package = language.defInjections[i];
                        if (package == null || package.defType == null)
                        {
                            continue;
                        }

                        InjectPackage(package, errorOnDefNotFound);
                        if (reportErrors)
                        {
                            errorCount += package.loadErrors.Count;
                        }
                    }

                    if (reportErrors && errorCount != 0)
                    {
                        language.anyError = true;
                        Log.Warning("Translation data for language " + LanguageDatabase.activeLanguage.FriendlyNameEnglish +
                            " has " + errorCount + " errors. Generate translation report for more info.");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                FastLoaderRuntime.ActivateVanillaFallback(FastLoaderCacheKind.Language, Describe(language) + " DefInjected apply failed: " + ex.GetType().Name);
                Log.Warning(FastLoaderDebug.FormatExceptionContext(
                    "DefInjected.FastApply",
                    "language=" + Describe(language),
                    ex));
                return false;
            }
        }

        private static void InjectPackage(DefInjectionPackage package, bool errorOnDefNotFound)
        {
            package.loadSyntaxSuggestions.Clear();
            package.loadErrors.Clear();

            HashSet<string> injectedNormalizedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, DefInjectionPackage.DefInjection> item in package.injections)
            {
                DefInjectionPackage.DefInjection injection = item.Value;
                if (injection != null && injection.injected && !string.IsNullOrEmpty(injection.normalizedPath))
                {
                    injectedNormalizedPaths.Add(injection.normalizedPath);
                }
            }

            foreach (KeyValuePair<string, DefInjectionPackage.DefInjection> item in package.injections)
            {
                DefInjectionPackage.DefInjection injection = item.Value;
                if (injection == null || injection.injected)
                {
                    continue;
                }

                PathState path = GetPathState(item.Key);
                Type valueType = injection.IsFullListInjection ? typeof(List<string>) : typeof(string);
                object value = injection.IsFullListInjection ? (object)injection.fullListInjection : injection.injection;
                bool injected = SetDefFieldAtPath(package, injection, path, value, valueType, errorOnDefNotFound, injectedNormalizedPaths);
                injection.injected = injected;
                injection.normalizedPath = path.NormalizedPath;
                injection.suggestedPath = path.SuggestedPath;
                if (injected)
                {
                    injectedNormalizedPaths.Add(path.NormalizedPath);
                }
            }

            GenGeneric.InvokeStaticMethodOnGenericType(typeof(DefDatabase<>), package.defType, "ClearCachedData");
        }

        private static bool SetDefFieldAtPath(
            DefInjectionPackage package,
            DefInjectionPackage.DefInjection injection,
            PathState path,
            object value,
            Type ensureFieldType,
            bool errorOnDefNotFound,
            HashSet<string> injectedNormalizedPaths)
        {
            injection.replacedString = null;
            injection.replacedList = null;

            string displayPath = path.WasTKey ? path.EffectivePath + " (" + path.NormalizedPath + ")" : path.EffectivePath;
            string[] parts = path.Parts;
            if (parts.Length < 2)
            {
                package.loadErrors.Add("Couldn't inject " + displayPath + " into " + package.defType + " (" + injection.fileSource + "): Path lacks a field.");
                return false;
            }

            string defName = BackCompatibility.BackCompatibleDefName(package.defType, parts[0], true);
            Def def = GetDef(package.defType, defName);
            if (def == null)
            {
                if (errorOnDefNotFound)
                {
                    package.loadErrors.Add("Found no " + package.defType + " named " + defName + " to match " + displayPath + " (" + injection.fileSource + ")");
                }

                return false;
            }

            bool listCanChangeCount = false;
            List<object> valueTypeParents = new List<object>();
            object current = def;

            try
            {
                for (int index = 1; index < parts.Length; index++)
                {
                    string part = parts[index];
                    valueTypeParents.Add(current);
                    int parsedIndex;
                    PathPartKind kind = DeterminePathPartKind(current, part, out parsedIndex, ref part);
                    bool isFinal = index == parts.Length - 1;

                    if (isFinal)
                    {
                        bool result = false;
                        if (injectedNormalizedPaths != null && injectedNormalizedPaths.Contains(path.NormalizedPath))
                        {
                            AddDuplicateError(package, injection, path);
                        }
                        else
                        {
                            result = SetFinalValue(package, injection, current, part, kind, parsedIndex, value, ensureFieldType, listCanChangeCount, displayPath);
                        }

                        WriteBackValueTypeChain(valueTypeParents, parts);
                        UpdateSuggestedPath(package, injection, path, value);
                        return result;
                    }

                    switch (kind)
                    {
                        case PathPartKind.Field:
                            current = EnterField(package, current, part, displayPath, ref listCanChangeCount);
                            break;
                        case PathPartKind.ListIndex:
                        case PathPartKind.ListHandle:
                        case PathPartKind.ListHandleWithIndex:
                            current = EnterList(current, kind, part, parsedIndex, index, path);
                            break;
                    }

                    if (current == null)
                    {
                        throw new InvalidOperationException("Tried to enter null node " + part + ".");
                    }
                }
            }
            catch (Exception ex)
            {
                string error = "Couldn't inject " + displayPath + " into " + package.defType + " (" + injection.fileSource + "): " + ex.Message;
                if (ex.InnerException != null)
                {
                    error += " -> " + ex.InnerException.Message;
                }

                package.loadErrors.Add(error);
                return false;
            }

            return false;
        }

        private static object EnterField(DefInjectionPackage package, object current, string part, string displayPath, ref bool listCanChangeCount)
        {
            FieldInfo field = GetFieldNamed(current.GetType(), part);
            if (field == null)
            {
                throw new InvalidOperationException("Field or TKey " + part + " does not exist.");
            }

            if (field.HasAttribute<NoTranslateAttribute>())
            {
                throw new InvalidOperationException("Translated untranslatable field " + field.Name + " of type " + field.FieldType + " at path " + displayPath + ". Translating this field will break the game.");
            }

            if (field.HasAttribute<UnsavedAttribute>())
            {
                throw new InvalidOperationException("Translated untranslatable field ([Unsaved] attribute) " + field.Name + " of type " + field.FieldType + " at path " + displayPath + ". Translating this field will break the game.");
            }

            if (field.HasAttribute<TranslationCanChangeCountAttribute>())
            {
                listCanChangeCount = true;
            }

            return field.GetValue(current);
        }

        private static object EnterList(object current, PathPartKind kind, string part, int index, int pathIndex, PathState path)
        {
            PropertyInfo itemProperty = GetItemProperty(current.GetType());
            if (itemProperty == null)
            {
                throw new InvalidOperationException("Tried to use index on non-list (missing 'Item' property).");
            }

            bool usedHandle = false;
            if (kind == PathPartKind.ListHandle || kind == PathPartKind.ListHandleWithIndex)
            {
                index = TranslationHandleUtility.GetElementIndexByHandle(current, part, index);
                usedHandle = true;
            }

            int count = GetCount(current);
            if (index < 0 || index >= count)
            {
                throw new InvalidOperationException("Index out of bounds (max index is " + (count - 1) + ")");
            }

            object result = itemProperty.GetValue(current, new object[] { index });
            if (usedHandle)
            {
                string[] normalized = (string[])path.Parts.Clone();
                normalized[pathIndex] = index.ToString();
                path.NormalizedPath = string.Join(".", normalized);
            }
            else if (!path.WasTKey)
            {
                string handle = TranslationHandleUtility.GetBestHandleWithIndexForListElement(current, result);
                if (!string.IsNullOrEmpty(handle))
                {
                    string[] suggested = (string[])path.Parts.Clone();
                    suggested[pathIndex] = handle;
                    path.SuggestedPath = string.Join(".", suggested);
                }
            }

            return result;
        }

        private static bool SetFinalValue(
            DefInjectionPackage package,
            DefInjectionPackage.DefInjection injection,
            object current,
            string part,
            PathPartKind kind,
            int index,
            object value,
            Type ensureFieldType,
            bool listCanChangeCount,
            string displayPath)
        {
            if (kind == PathPartKind.Field)
            {
                FieldInfo field = GetFieldNamed(current.GetType(), part);
                if (field == null)
                {
                    throw new InvalidOperationException("Field " + part + " does not exist in type " + current.GetType() + ".");
                }

                if (field.HasAttribute<NoTranslateAttribute>())
                {
                    package.loadErrors.Add("Translated untranslatable field " + field.Name + " of type " + field.FieldType + " at path " + displayPath + ". Translating this field will break the game. (" + injection.fileSource + ")");
                }
                else if (field.HasAttribute<UnsavedAttribute>())
                {
                    package.loadErrors.Add("Translated untranslatable field (UnsavedAttribute) " + field.Name + " of type " + field.FieldType + " at path " + displayPath + ". Translating this field will break the game. (" + injection.fileSource + ")");
                }
                else if (!injection.isPlaceholder && field.FieldType != ensureFieldType)
                {
                    package.loadErrors.Add("Translated non-" + ensureFieldType + " field " + field.Name + " of type " + field.FieldType + " at path " + displayPath + ". Expected " + ensureFieldType + ". (" + injection.fileSource + ")");
                }
                else if (!injection.isPlaceholder && ensureFieldType != typeof(string) && !field.HasAttribute<TranslationCanChangeCountAttribute>())
                {
                    package.loadErrors.Add("Tried to translate field " + field.Name + " of type " + field.FieldType + " at path " + displayPath + ", but this field doesn't have [TranslationCanChangeCount] attribute so it doesn't allow this type of translation. (" + injection.fileSource + ")");
                }
                else if (!injection.isPlaceholder)
                {
                    if (ensureFieldType == typeof(string))
                    {
                        injection.replacedString = (string)field.GetValue(current);
                    }
                    else
                    {
                        injection.replacedList = field.GetValue(current) as IEnumerable<string>;
                    }

                    field.SetValue(current, value);
                    return true;
                }

                return false;
            }

            object list = current;
            if (list == null)
            {
                throw new InvalidOperationException("Tried to use index on null list at " + displayPath);
            }

            PropertyInfo itemProperty = GetItemProperty(list.GetType());
            if (itemProperty == null)
            {
                throw new InvalidOperationException("Tried to use index on non-list (missing 'Item' property).");
            }

            if (kind == PathPartKind.ListHandle || kind == PathPartKind.ListHandleWithIndex)
            {
                index = TranslationHandleUtility.GetElementIndexByHandle(list, part, index);
            }

            int count = GetCount(list);
            if (index >= count)
            {
                throw new InvalidOperationException("Trying to translate " + package.defType + "." + displayPath + " at index " + index + " but the list only has " + count + " entries (so max index is " + (count - 1) + ").");
            }

            if (!injection.isPlaceholder && itemProperty.PropertyType != ensureFieldType)
            {
                package.loadErrors.Add("Translated non-" + ensureFieldType + " list item of type " + itemProperty.PropertyType + " at path " + displayPath + ". Expected " + ensureFieldType + ". (" + injection.fileSource + ")");
            }
            else if (!injection.isPlaceholder && ensureFieldType != typeof(string) && !listCanChangeCount)
            {
                package.loadErrors.Add("Tried to translate field of type " + itemProperty.PropertyType + " at path " + displayPath + ", but this field doesn't have [TranslationCanChangeCount] attribute so it doesn't allow this type of translation. (" + injection.fileSource + ")");
            }
            else if (index < 0 || index >= count)
            {
                package.loadErrors.Add("Index out of bounds (max index is " + (count - 1) + ")");
            }
            else if (!injection.isPlaceholder)
            {
                injection.replacedString = (string)itemProperty.GetValue(list, new object[] { index });
                itemProperty.SetValue(list, value, new object[] { index });
                return true;
            }

            return false;
        }

        private static void WriteBackValueTypeChain(List<object> parents, string[] parts)
        {
            for (int i = parents.Count - 1; i > 0; i--)
            {
                object child = parents[i];
                if (child == null)
                {
                    continue;
                }

                Type childType = child.GetType();
                if (childType.IsValueType && !childType.IsPrimitive)
                {
                    FieldInfo field = GetFieldNamed(parents[i - 1].GetType(), parts[i]);
                    if (field != null)
                    {
                        field.SetValue(parents[i - 1], child);
                    }
                }
            }
        }

        private static void UpdateSuggestedPath(DefInjectionPackage package, DefInjectionPackage.DefInjection injection, PathState path, object value)
        {
            string pathForComparison = path.EffectivePath;
            if (path.WasTKey)
            {
                pathForComparison = path.SuggestedPath;
            }
            else
            {
                string suggested;
                if (TKeySystem.TrySuggestTKeyPath(path.EffectivePath, out suggested))
                {
                    path.SuggestedPath = suggested;
                }
            }

            if (!string.Equals(pathForComparison, path.SuggestedPath, StringComparison.Ordinal))
            {
                IList<string> list = value as IList<string>;
                string text = list == null ? Convert.ToString(value) : list.ToStringSafeEnumerable();
                package.loadSyntaxSuggestions.Add("Consider using " + path.SuggestedPath + " instead of " + path.EffectivePath + " for translation '" + text + "' (" + injection.fileSource + ")");
            }
        }

        private static PathPartKind DeterminePathPartKind(object current, string part, out int index, ref string listHandle)
        {
            index = -1;
            if (int.TryParse(part, out index))
            {
                return PathPartKind.ListIndex;
            }

            if (GetFieldNamed(current.GetType(), part) != null)
            {
                return PathPartKind.Field;
            }

            if (GetCountProperty(current.GetType()) != null)
            {
                int dash = part.IndexOf('-');
                if (dash >= 0)
                {
                    listHandle = part.Substring(0, dash);
                    index = ParseHelper.FromString<int>(part.Substring(dash + 1));
                    return PathPartKind.ListHandleWithIndex;
                }

                listHandle = part;
                return PathPartKind.ListHandle;
            }

            return PathPartKind.Field;
        }

        private static PathState GetPathState(string path)
        {
            PathState state;
            if (PathCache.TryGetValue(path ?? string.Empty, out state))
            {
                return ClonePathState(state);
            }

            string normalized;
            bool wasTKey = TKeySystem.TryGetNormalizedPath(path, out normalized);
            string effective = wasTKey ? normalized : path;
            state = new PathState
            {
                EffectivePath = path ?? string.Empty,
                NormalizedPath = wasTKey ? normalized : path ?? string.Empty,
                SuggestedPath = path ?? string.Empty,
                WasTKey = wasTKey,
                Parts = (effective ?? string.Empty).Split('.')
            };
            PathCache[path ?? string.Empty] = state;
            return ClonePathState(state);
        }

        private static PathState ClonePathState(PathState source)
        {
            return new PathState
            {
                EffectivePath = source.EffectivePath,
                NormalizedPath = source.NormalizedPath,
                SuggestedPath = source.SuggestedPath,
                WasTKey = source.WasTKey,
                Parts = source.Parts
            };
        }

        private static FieldInfo GetFieldNamed(Type type, string name)
        {
            FieldCacheKey key = new FieldCacheKey(type, name);
            FieldInfo field;
            if (FieldCache.TryGetValue(key, out field))
            {
                return field;
            }

            field = type.GetField(name, FieldBindingFlags);
            if (field == null)
            {
                FieldInfo[] fields = type.GetFields(FieldBindingFlags);
                for (int i = 0; i < fields.Length && field == null; i++)
                {
                    object[] aliases = fields[i].GetCustomAttributes(typeof(LoadAliasAttribute), false);
                    if (aliases == null || aliases.Length == 0)
                    {
                        continue;
                    }

                    for (int j = 0; j < aliases.Length; j++)
                    {
                        if (((LoadAliasAttribute)aliases[j]).alias == name)
                        {
                            field = fields[i];
                            break;
                        }
                    }
                }
            }

            FieldCache[key] = field;
            return field;
        }

        private static PropertyInfo GetCountProperty(Type type)
        {
            PropertyInfo property;
            if (!CountPropertyCache.TryGetValue(type, out property))
            {
                property = type.GetProperty("Count");
                CountPropertyCache[type] = property;
            }

            return property;
        }

        private static PropertyInfo GetItemProperty(Type type)
        {
            PropertyInfo property;
            if (!ItemPropertyCache.TryGetValue(type, out property))
            {
                property = type.GetProperty("Item");
                ItemPropertyCache[type] = property;
            }

            return property;
        }

        private static int GetCount(object list)
        {
            PropertyInfo property = GetCountProperty(list.GetType());
            if (property == null)
            {
                throw new InvalidOperationException("Tried to use index on non-list (missing 'Count' property).");
            }

            return (int)property.GetValue(list, null);
        }

        private static Def GetDef(Type type, string defName)
        {
            DefCacheKey key = new DefCacheKey(type, defName);
            Def result;
            if (!DefCache.TryGetValue(key, out result))
            {
                result = GenDefDatabase.GetDefSilentFail(type, defName, false);
                DefCache[key] = result;
            }

            return result;
        }

        private static void AddDuplicateError(DefInjectionPackage package, DefInjectionPackage.DefInjection injection, PathState path)
        {
            package.loadErrors.Add("Duplicate def-injected translation key. " + path.EffectivePath +
                " refers to a field that was already translated (" + path.SuggestedPath + ") (" + injection.fileSource + ")");
        }

        private static bool IsEnabled()
        {
            return !FastLoaderRuntime.IsCacheFallbackActive &&
                FastLoaderRuntime.ShouldUseXmlCache;
        }

        private static string Describe(LoadedLanguage language)
        {
            return language != null ? language.folderName ?? "(unknown language)" : "(null language)";
        }
    }
}
