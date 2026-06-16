using System;
using System.Collections.Generic;
using System.Xml;
using Verse;

namespace FastLoader
{
    internal static class FastLoaderXmlParser
    {
        public static int ParseCachedResolvedXml(XmlDocument xmlDoc, IReadOnlyList<CacheEntry> entries, bool hotReload)
        {
            if (hotReload)
            {
                throw new InvalidOperationException("FastLoader cache path does not support hot reload.");
            }

            if (xmlDoc == null || xmlDoc.DocumentElement == null)
            {
                throw new InvalidOperationException("Cached XML document is empty.");
            }

            List<XmlNode> nodes = new List<XmlNode>();
            using (FastProfile.Scope("Cache hit parse: collect cached XML nodes"))
            {
                foreach (XmlNode node in xmlDoc.DocumentElement.ChildNodes)
                {
                    if (node.NodeType == XmlNodeType.Element)
                    {
                        nodes.Add(node);
                    }
                }
            }

            if (entries == null || entries.Count != nodes.Count)
            {
                throw new InvalidOperationException("Cached XML metadata does not match cached XML node count.");
            }

            LoadedModManager.PatchedDefsForReading.Clear();
            bool useNewDeserializer = !GenCommandLine.CommandLineArgPassed("legacy-xml-deserializer");
            int parsedDefCount = 0;

            using (FastProfile.Scope("Cache hit parse: deserialize cached defs"))
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    XmlNode xmlNode = nodes[i];
                    if (!ShouldLoad(xmlNode))
                    {
                        continue;
                    }

                    CacheEntry entry = entries[i];
                    try
                    {
                        Def def = useNewDeserializer
                            ? DirectXmlToObjectNew.DefFromNodeNew(xmlNode, null)
                            : DirectXmlLoader.DefFromNode(xmlNode, null);

                        if (def == null)
                        {
                            continue;
                        }

                        ModContentPack mod = FastLoaderRuntime.FindMod(entry.PackageId);
                        if (mod != null)
                        {
                            mod.AddDef(def, string.IsNullOrEmpty(entry.SourceName) ? "Unknown" : entry.SourceName);
                        }
                        else
                        {
                            LoadedModManager.PatchedDefsForReading.Add(def);
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("Cached XML parse failed at " + DescribeCachedNode(xmlNode, entry), ex);
                    }

                    parsedDefCount++;
                }
            }

            return parsedDefCount;
        }

        private static string DescribeCachedNode(XmlNode node, CacheEntry entry)
        {
            string nodeName = node != null ? node.Name : "(null node)";
            string defName = ChildText(node, "defName");
            if (string.IsNullOrEmpty(defName))
            {
                defName = ChildText(node, "Name");
            }

            return "node=" + nodeName +
                ", defName=" + (string.IsNullOrEmpty(defName) ? "(empty)" : defName) +
                ", packageId=" + (entry != null ? entry.PackageId ?? string.Empty : string.Empty) +
                ", sourceName=" + (entry != null ? entry.SourceName ?? string.Empty : string.Empty);
        }

        private static string ChildText(XmlNode node, string name)
        {
            if (node == null)
            {
                return string.Empty;
            }

            XmlNode child = node.SelectSingleNode(name);
            return child != null ? child.InnerText : string.Empty;
        }

        private static bool ShouldLoad(XmlNode node)
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
    }
}
