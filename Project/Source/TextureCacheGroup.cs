using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Verse;

namespace FastLoader
{
    internal sealed class TextureCacheGroup
    {
        public string GroupId;
        public string GroupName;
        public List<string> PackageIds = new List<string>();
    }

    internal static class TextureCacheGroupManager
    {
        private static List<TextureCacheGroup> groups;
        private static Dictionary<string, TextureCacheGroup> modToGroupIndex;
        private static bool loaded;

        public static List<TextureCacheGroup> Groups
        {
            get
            {
                EnsureLoaded();
                return groups;
            }
        }

        public static TextureCacheGroup FindGroupForMod(string packageId)
        {
            EnsureLoaded();
            if (modToGroupIndex == null || string.IsNullOrEmpty(packageId))
            {
                return null;
            }

            string normalized = packageId.ToLowerInvariant();
            TextureCacheGroup result;
            modToGroupIndex.TryGetValue(normalized, out result);
            return result;
        }

        public static void AddGroup(string groupId, string groupName)
        {
            EnsureLoaded();
            if (groups == null)
            {
                groups = new List<TextureCacheGroup>();
            }

            TextureCacheGroup group = new TextureCacheGroup
            {
                GroupId = groupId,
                GroupName = groupName
            };
            groups.Add(group);
            Save();
        }

        public static void RemoveGroup(string groupId)
        {
            EnsureLoaded();
            if (groups == null)
            {
                return;
            }

            for (int i = groups.Count - 1; i >= 0; i--)
            {
                if (string.Equals(groups[i].GroupId, groupId, StringComparison.OrdinalIgnoreCase))
                {
                    groups.RemoveAt(i);
                }
            }

            RebuildIndex();
            Save();
        }

        public static void AssignModToGroup(string packageId, string groupId)
        {
            EnsureLoaded();
            string normalized = (packageId ?? string.Empty).ToLowerInvariant();

            if (groups != null)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    groups[i].PackageIds.Remove(normalized);
                }
            }

            if (!string.IsNullOrEmpty(groupId))
            {
                TextureCacheGroup target = FindGroupById(groupId);
                if (target != null && !target.PackageIds.Contains(normalized))
                {
                    target.PackageIds.Add(normalized);
                }
            }

            RebuildIndex();
            Save();
        }

        public static void Load()
        {
            groups = new List<TextureCacheGroup>();
            modToGroupIndex = new Dictionary<string, TextureCacheGroup>(StringComparer.OrdinalIgnoreCase);
            loaded = true;

            string path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                XmlDocument doc = new XmlDocument();
                using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true }))
                {
                    doc.Load(reader);
                }

                XmlElement root = doc.DocumentElement;
                if (root == null || root.Name != "TextureCacheGroups")
                {
                    return;
                }

                foreach (XmlNode node in root.ChildNodes)
                {
                    if (node.NodeType != XmlNodeType.Element || node.Name != "group")
                    {
                        continue;
                    }

                    TextureCacheGroup group = new TextureCacheGroup();
                    group.GroupId = GetAttr(node, "id");
                    group.GroupName = GetAttr(node, "name");

                    foreach (XmlNode child in node.ChildNodes)
                    {
                        if (child.NodeType == XmlNodeType.Element && child.Name == "mod")
                        {
                            string pid = child.InnerText;
                            if (!string.IsNullOrEmpty(pid))
                            {
                                group.PackageIds.Add(pid.ToLowerInvariant());
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(group.GroupId))
                    {
                        groups.Add(group);
                    }
                }

                RebuildIndex();
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to load texture cache group settings.\n" + ex);
            }
        }

        public static void Save()
        {
            string path = GetSettingsPath();

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                XmlDocument doc = new XmlDocument();
                XmlElement root = doc.CreateElement("TextureCacheGroups");
                doc.AppendChild(root);

                if (groups != null)
                {
                    for (int i = 0; i < groups.Count; i++)
                    {
                        TextureCacheGroup group = groups[i];
                        XmlElement groupElem = doc.CreateElement("group");
                        groupElem.SetAttribute("id", group.GroupId ?? string.Empty);
                        groupElem.SetAttribute("name", group.GroupName ?? string.Empty);

                        for (int j = 0; j < group.PackageIds.Count; j++)
                        {
                            XmlElement modElem = doc.CreateElement("mod");
                            modElem.InnerText = group.PackageIds[j];
                            groupElem.AppendChild(modElem);
                        }

                        root.AppendChild(groupElem);
                    }
                }

                string tempPath = path + ".tmp";
                using (XmlWriter writer = XmlWriter.Create(tempPath, new XmlWriterSettings { Indent = true }))
                {
                    doc.Save(writer);
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(tempPath, path);
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to save texture cache group settings.\n" + ex);
            }
        }

        private static void EnsureLoaded()
        {
            if (!loaded)
            {
                Load();
            }
        }

        private static TextureCacheGroup FindGroupById(string groupId)
        {
            if (groups == null || string.IsNullOrEmpty(groupId))
            {
                return null;
            }

            for (int i = 0; i < groups.Count; i++)
            {
                if (string.Equals(groups[i].GroupId, groupId, StringComparison.OrdinalIgnoreCase))
                {
                    return groups[i];
                }
            }

            return null;
        }

        private static void RebuildIndex()
        {
            if (modToGroupIndex == null)
            {
                modToGroupIndex = new Dictionary<string, TextureCacheGroup>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                modToGroupIndex.Clear();
            }

            if (groups == null)
            {
                return;
            }

            for (int i = 0; i < groups.Count; i++)
            {
                TextureCacheGroup group = groups[i];
                for (int j = 0; j < group.PackageIds.Count; j++)
                {
                    string pid = group.PackageIds[j];
                    if (!string.IsNullOrEmpty(pid))
                    {
                        modToGroupIndex[pid] = group;
                    }
                }
            }
        }

        private static string GetSettingsPath()
        {
            return Path.Combine(GenFilePaths.ConfigFolderPath, "FastLoader", "TextureCache", "groups.xml");
        }

        private static string GetAttr(XmlNode node, string name)
        {
            if (node.Attributes == null)
            {
                return string.Empty;
            }

            XmlAttribute attr = node.Attributes[name];
            return attr != null ? attr.Value : string.Empty;
        }
    }
}
