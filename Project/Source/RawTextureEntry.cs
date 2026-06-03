using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FastLoader
{
    internal sealed class RawTextureEntry
    {
        public string InternalPath;
        public int Width;
        public int Height;
        public int TextureFormat;
        public int MipmapCount;
        public int FilterMode;
        public int AnisoLevel;
        public string Name;
        public bool AtlasStub;
        public byte[] RawData;
    }

    internal static class TextureCacheFile
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FLTX");
        private const int FormatVersion = 2;

        public static void Write(string path, string hash, List<RawTextureEntry> entries)
        {
            Write(path, hash, entries, null);
        }

        public static void Write(string path, string hash, List<RawTextureEntry> entries, List<string> atlasDependencies)
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
                WriteFixedString(writer, hash, 64);
                writer.Write(entries.Count);
                writer.Write(DateTime.UtcNow.Ticks);
                writer.Write(atlasDependencies != null ? atlasDependencies.Count : 0);
                if (atlasDependencies != null)
                {
                    for (int i = 0; i < atlasDependencies.Count; i++)
                    {
                        writer.Write(atlasDependencies[i] ?? string.Empty);
                    }
                }

                for (int i = 0; i < entries.Count; i++)
                {
                    RawTextureEntry entry = entries[i];
                    writer.Write(entry.InternalPath ?? string.Empty);
                    writer.Write(entry.Name ?? string.Empty);
                    writer.Write(entry.Width);
                    writer.Write(entry.Height);
                    writer.Write(entry.TextureFormat);
                    writer.Write(entry.MipmapCount);
                    writer.Write(entry.FilterMode);
                    writer.Write(entry.AnisoLevel);
                    writer.Write(entry.AtlasStub);
                    writer.Write(entry.RawData != null ? entry.RawData.Length : 0);
                }

                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].RawData != null && entries[i].RawData.Length > 0)
                    {
                        writer.Write(entries[i].RawData);
                    }
                }
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }

        public static bool TryRead(string path, string expectedHash, out List<RawTextureEntry> entries)
        {
            entries = null;

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

                    string storedHash = ReadFixedString(reader, 64);
                    if (!string.Equals(storedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    int count = reader.ReadInt32();
                    long createdTicks = reader.ReadInt64();

                    if (count < 0 || count > 100000)
                    {
                        return false;
                    }

                    int atlasDependencyCount = reader.ReadInt32();
                    if (atlasDependencyCount < 0 || atlasDependencyCount > 10000)
                    {
                        return false;
                    }

                    List<string> atlasDependencies = new List<string>(atlasDependencyCount);
                    for (int i = 0; i < atlasDependencyCount; i++)
                    {
                        atlasDependencies.Add(reader.ReadString());
                    }

                    if (!StaticAtlasCache.AreCacheDependenciesAvailable(atlasDependencies))
                    {
                        return false;
                    }

                    List<RawTextureEntry> result = new List<RawTextureEntry>(count);
                    int[] rawDataLengths = new int[count];

                    for (int i = 0; i < count; i++)
                    {
                        RawTextureEntry entry = new RawTextureEntry();
                        entry.InternalPath = reader.ReadString();
                        entry.Name = reader.ReadString();
                        entry.Width = reader.ReadInt32();
                        entry.Height = reader.ReadInt32();
                        entry.TextureFormat = reader.ReadInt32();
                        entry.MipmapCount = reader.ReadInt32();
                        entry.FilterMode = reader.ReadInt32();
                        entry.AnisoLevel = reader.ReadInt32();
                        entry.AtlasStub = reader.ReadBoolean();
                        rawDataLengths[i] = reader.ReadInt32();
                        result.Add(entry);
                    }

                    for (int i = 0; i < count; i++)
                    {
                        int length = rawDataLengths[i];
                        if (length > 0)
                        {
                            result[i].RawData = reader.ReadBytes(length);
                            if (result[i].RawData.Length != length)
                            {
                                return false;
                            }
                        }
                        else
                        {
                            result[i].RawData = Array.Empty<byte>();
                            if (!result[i].AtlasStub)
                            {
                                return false;
                            }
                        }
                    }

                    entries = result;
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool HasMatchingHeader(string path, string expectedHash)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
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

                    string storedHash = ReadFixedString(reader, 64);
                    if (!string.Equals(storedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    int count = reader.ReadInt32();
                    return count > 0 && count <= 100000;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void WriteFixedString(BinaryWriter writer, string value, int fixedLength)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length >= fixedLength)
            {
                writer.Write(bytes, 0, fixedLength);
            }
            else
            {
                writer.Write(bytes);
                for (int i = bytes.Length; i < fixedLength; i++)
                {
                    writer.Write((byte)0);
                }
            }
        }

        private static string ReadFixedString(BinaryReader reader, int fixedLength)
        {
            byte[] bytes = reader.ReadBytes(fixedLength);
            int end = Array.IndexOf(bytes, (byte)0);
            if (end < 0)
            {
                end = fixedLength;
            }

            return Encoding.UTF8.GetString(bytes, 0, end);
        }
    }
}
