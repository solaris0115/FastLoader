using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Verse;

namespace FastLoader
{
    internal static class StaticAtlasCache
    {
        private const int Magic = 0x54414C46;
        private const int FormatVersion = 1;
        private const string HashVersion = "FastLoaderStaticAtlasV1";
        private const string CacheExtension = ".atlascache";
        private const int MaxTexturePayloadBytes = 128 * 1024 * 1024;

        private static readonly FieldInfo TexturesField = AccessTools.Field(typeof(StaticTextureAtlas), "textures");
        private static readonly FieldInfo TilesField = AccessTools.Field(typeof(StaticTextureAtlas), "tiles");
        private static readonly FieldInfo ColorTextureField = AccessTools.Field(typeof(StaticTextureAtlas), "colorTexture");
        private static readonly FieldInfo MaskTextureField = AccessTools.Field(typeof(StaticTextureAtlas), "maskTexture");
        private static readonly FieldInfo StaticTextureAtlasesField = AccessTools.Field(typeof(GlobalTextureAtlasManager), "staticTextureAtlases");

        private static string currentLoadoutHash;

        private static string CacheRootPath
        {
            get { return Path.Combine(GenFilePaths.ConfigFolderPath, "FastLoader", "StaticAtlasCache"); }
        }

        public static bool TryRestore(StaticTextureAtlas atlas)
        {
            if (!IsEnabled() || atlas == null)
            {
                return false;
            }

            List<Texture2D> textures = GetTextures(atlas);
            if (textures == null || textures.Count == 0)
            {
                return false;
            }

            string hash = ComputeAtlasHash(atlas, textures);
            string path = GetCachePath(hash);
            if (!File.Exists(path))
            {
                return false;
            }

            using (FastLoaderProfiler.Scope("FastLoader.StaticAtlasCache.TryRestore | " + atlas.groupKey))
            {
                try
                {
                    AtlasCacheRecord record;
                    if (!TryRead(path, hash, out record))
                    {
                        return false;
                    }

                    if (record.UvRects.Count != textures.Count)
                    {
                        return false;
                    }

                    Texture2D colorTexture = RestoreTexture(record.ColorTexture);
                    Texture2D maskTexture = record.MaskTexture != null ? RestoreTexture(record.MaskTexture) : null;
                    if (colorTexture == null)
                    {
                        return false;
                    }

                    ColorTextureField.SetValue(atlas, colorTexture);
                    MaskTextureField.SetValue(atlas, maskTexture);
                    RestoreTiles(atlas, textures, record.UvRects);
                    return true;
                }
                catch (Exception ex)
                {
                    FastLoaderRuntime.ActivateVanillaFallback(FastLoaderCacheKind.Atlas, "static atlas restore failed for " + atlas.groupKey + ": " + ex.GetType().Name);
                    Log.Warning("[FastLoader] Static atlas cache restore failed for " + atlas.groupKey + ". Falling back to vanilla bake.\n" + ex);
                    return false;
                }
            }
        }

        public static int BuildFromCurrentAtlases()
        {
            List<StaticTextureAtlas> atlases = GetCurrentAtlases();
            int saved = 0;
            for (int i = 0; i < atlases.Count; i++)
            {
                if (HasCache(atlases[i]) || Save(atlases[i]))
                {
                    saved++;
                }
            }

            Log.Message("[FastLoader] Static atlas cache build completed. Atlases: " + saved);
            return saved;
        }

        public static void DeleteAll()
        {
            currentLoadoutHash = null;
            if (!Directory.Exists(CacheRootPath))
            {
                return;
            }

            try
            {
                string[] files = Directory.GetFiles(CacheRootPath, "*" + CacheExtension, SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    TryDeleteFile(files[i]);
                }

                TryDeleteEmptyDirectory(CacheRootPath);
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to delete static atlas cache files.\n" + ex);
            }
        }

        public static bool Save(StaticTextureAtlas atlas)
        {
            if (atlas == null)
            {
                return false;
            }

            List<Texture2D> textures = GetTextures(atlas);
            Dictionary<Texture, StaticTextureAtlasTile> tiles = GetTiles(atlas);
            Texture2D colorTexture = ColorTextureField.GetValue(atlas) as Texture2D;
            Texture2D maskTexture = MaskTextureField.GetValue(atlas) as Texture2D;
            if (textures == null || textures.Count == 0 || tiles == null || colorTexture == null)
            {
                return false;
            }

            string hash = ComputeAtlasHash(atlas, textures);
            string path = GetCachePath(hash);

            using (FastLoaderProfiler.Scope("FastLoader.StaticAtlasCache.Write | " + atlas.groupKey))
            {
                try
                {
                    AtlasCacheRecord record = new AtlasCacheRecord();
                    record.Hash = hash;
                    record.Group = (int)atlas.groupKey.group;
                    record.HasMask = atlas.groupKey.hasMask;
                    record.TextureCount = textures.Count;
                    record.ColorTexture = CaptureTexture(colorTexture);
                    if (record.ColorTexture == null)
                    {
                        Log.Warning("[FastLoader] Static atlas cache skipped for " + atlas.groupKey + ": color atlas texture is not readable.");
                        return false;
                    }

                    if (maskTexture != null)
                    {
                        record.MaskTexture = CaptureTexture(maskTexture);
                        if (record.MaskTexture == null)
                        {
                            Log.Warning("[FastLoader] Static atlas cache skipped for " + atlas.groupKey + ": mask atlas texture is not readable.");
                            return false;
                        }
                    }
                    else
                    {
                        record.MaskTexture = null;
                    }

                    for (int i = 0; i < textures.Count; i++)
                    {
                        StaticTextureAtlasTile tile;
                        if (!tiles.TryGetValue(textures[i], out tile))
                        {
                            return false;
                        }

                        record.UvRects.Add(tile.uvRect);
                    }

                    Directory.CreateDirectory(CacheRootPath);
                    string tempPath = path + ".tmp";
                    Write(tempPath, record);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    File.Move(tempPath, path);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warning("[FastLoader] Failed to write static atlas cache for " + atlas.groupKey + ".\n" + ex);
                    return false;
                }
            }
        }

        private static bool HasCache(StaticTextureAtlas atlas)
        {
            if (atlas == null)
            {
                return false;
            }

            List<Texture2D> textures = GetTextures(atlas);
            if (textures == null || textures.Count == 0)
            {
                return false;
            }

            return File.Exists(GetCachePath(ComputeAtlasHash(atlas, textures)));
        }

        private static void RestoreTiles(StaticTextureAtlas atlas, List<Texture2D> textures, List<Rect> uvRects)
        {
            Dictionary<Texture, StaticTextureAtlasTile> tiles = new Dictionary<Texture, StaticTextureAtlasTile>();
            for (int i = 0; i < textures.Count; i++)
            {
                Rect uvRect = uvRects[i];
                Mesh mesh = TextureAtlasHelper.CreateMeshForUV(uvRect, 0.5f);
                mesh.name = "TextureAtlasMesh_" + atlas.groupKey + "_" + mesh.GetInstanceID();
                tiles.Add(textures[i], new StaticTextureAtlasTile
                {
                    atlas = atlas,
                    mesh = mesh,
                    uvRect = uvRect
                });
            }

            TilesField.SetValue(atlas, tiles);
        }

        private static TexturePayload CaptureTexture(Texture2D texture)
        {
            if (texture == null)
            {
                return null;
            }

            TexturePayload payload = new TexturePayload
            {
                Name = texture.name ?? string.Empty,
                Width = texture.width,
                Height = texture.height,
                GraphicsFormat = (int)texture.graphicsFormat,
                MipCount = Math.Max(1, texture.mipmapCount),
                FilterMode = (int)texture.filterMode,
                WrapMode = (int)texture.wrapMode,
                AnisoLevel = texture.anisoLevel,
                MipMapBias = texture.mipMapBias
            };

            payload.RawData = ReadRawTextureData(texture, payload.MipCount);
            if (payload.RawData == null || payload.RawData.Length == 0)
            {
                return CaptureRenderTextureCopy(texture);
            }

            if (payload.RawData.Length > MaxTexturePayloadBytes)
            {
                Log.Warning("[FastLoader] Static atlas texture capture skipped for " + texture.name + ": raw texture data is too large.");
                return null;
            }

            return payload;
        }

        private static byte[] ReadRawTextureData(Texture2D texture, int mipCount)
        {
            try
            {
                if (texture.isReadable)
                {
                    return texture.GetRawTextureData();
                }
            }
            catch
            {
            }

            if (GraphicsFormatUtility.IsCompressedFormat(texture.graphicsFormat))
            {
                return null;
            }

            try
            {
                List<byte[]> mipBytes = new List<byte[]>();
                int totalLength = 0;
                for (int mip = 0; mip < mipCount; mip++)
                {
                    AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture, mip);
                    request.WaitForCompletion();
                    if (request.hasError)
                    {
                        return null;
                    }

                    var data = request.GetData<byte>();
                    if ((long)totalLength + data.Length > MaxTexturePayloadBytes)
                    {
                        return null;
                    }

                    byte[] bytes = new byte[data.Length];
                    for (int i = 0; i < data.Length; i++)
                    {
                        bytes[i] = data[i];
                    }

                    mipBytes.Add(bytes);
                    totalLength += bytes.Length;
                }

                byte[] combined = new byte[totalLength];
                int offset = 0;
                for (int i = 0; i < mipBytes.Count; i++)
                {
                    byte[] bytes = mipBytes[i];
                    Buffer.BlockCopy(bytes, 0, combined, offset, bytes.Length);
                    offset += bytes.Length;
                }

                return combined;
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Static atlas GPU readback failed for " + texture.name + ": " + ex.Message);
                return null;
            }
        }

        private static TexturePayload CaptureRenderTextureCopy(Texture2D source)
        {
            long estimatedBytes = (long)source.width * source.height * 4L;
            if (estimatedBytes > MaxTexturePayloadBytes)
            {
                Log.Warning("[FastLoader] Static atlas render texture capture skipped for " + source.name + ": estimated payload is too large.");
                return null;
            }

            RenderTexture rt = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);

            try
            {
                Graphics.Blit(source, rt);
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
                request.WaitForCompletion();
                if (request.hasError)
                {
                    Log.Warning("[FastLoader] Static atlas render texture readback failed for " + source.name + ".");
                    return null;
                }

                var data = request.GetData<byte>();
                byte[] rawData = new byte[data.Length];
                for (int i = 0; i < data.Length; i++)
                {
                    rawData[i] = data[i];
                }

                if (rawData.Length == 0 || rawData.Length > MaxTexturePayloadBytes)
                {
                    Log.Warning("[FastLoader] Static atlas render texture capture skipped for " + source.name + ": raw texture data is invalid.");
                    return null;
                }

                return new TexturePayload
                {
                    Name = source.name ?? string.Empty,
                    Width = source.width,
                    Height = source.height,
                    GraphicsFormat = (int)GraphicsFormat.R8G8B8A8_UNorm,
                    MipCount = 1,
                    FilterMode = (int)source.filterMode,
                    WrapMode = (int)source.wrapMode,
                    AnisoLevel = source.anisoLevel,
                    MipMapBias = source.mipMapBias,
                    RawData = rawData
                };
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Static atlas render texture capture failed for " + source.name + ": " + ex.Message);
                return null;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static Texture2D RestoreTexture(TexturePayload payload)
        {
            if (payload == null || payload.RawData == null || payload.RawData.Length == 0)
            {
                return null;
            }

            Texture2D texture = new Texture2D(
                payload.Width,
                payload.Height,
                (GraphicsFormat)payload.GraphicsFormat,
                Math.Max(1, payload.MipCount),
                TextureCreationFlags.None);
            texture.name = payload.Name ?? string.Empty;
            texture.filterMode = (FilterMode)payload.FilterMode;
            texture.wrapMode = (TextureWrapMode)payload.WrapMode;
            texture.anisoLevel = payload.AnisoLevel;
            texture.mipMapBias = payload.MipMapBias;
            texture.LoadRawTextureData(payload.RawData);
            texture.Apply(false, true);
            return texture;
        }

        private static List<Texture2D> GetTextures(StaticTextureAtlas atlas)
        {
            return TexturesField.GetValue(atlas) as List<Texture2D>;
        }

        private static Dictionary<Texture, StaticTextureAtlasTile> GetTiles(StaticTextureAtlas atlas)
        {
            return TilesField.GetValue(atlas) as Dictionary<Texture, StaticTextureAtlasTile>;
        }

        private static List<StaticTextureAtlas> GetCurrentAtlases()
        {
            List<StaticTextureAtlas> atlases = StaticTextureAtlasesField.GetValue(null) as List<StaticTextureAtlas>;
            return atlases ?? new List<StaticTextureAtlas>();
        }

        private static string ComputeAtlasHash(StaticTextureAtlas atlas, List<Texture2D> textures)
        {
            using (SHA256 sha = SHA256.Create())
            {
                AppendHash(sha, HashVersion);
                AppendHash(sha, VersionControl.CurrentVersionStringWithRev ?? string.Empty);
                AppendHash(sha, GetCurrentLoadoutHash());
                AppendHash(sha, ((int)atlas.groupKey.group).ToString());
                AppendHash(sha, atlas.groupKey.hasMask ? "mask" : "nomask");
                AppendHash(sha, StaticTextureAtlas.MaxAtlasSize.ToString());
                AppendHash(sha, UnityData.ComputeShadersSupported ? "compute" : "no-compute");
                AppendHash(sha, Prefs.TextureCompression ? "compression" : "no-compression");
                AppendHash(sha, textures.Count.ToString());

                for (int i = 0; i < textures.Count; i++)
                {
                    Texture2D texture = textures[i];
                    AppendHash(sha, i.ToString());
                    AppendHash(sha, texture != null ? texture.name ?? string.Empty : string.Empty);
                    AppendHash(sha, texture != null ? texture.width.ToString() : "0");
                    AppendHash(sha, texture != null ? texture.height.ToString() : "0");
                    AppendHash(sha, texture != null ? ((int)texture.graphicsFormat).ToString() : "0");
                    AppendHash(sha, texture != null ? texture.mipmapCount.ToString() : "0");
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BytesToHex(sha.Hash);
            }
        }

        private static string GetCurrentLoadoutHash()
        {
            if (!string.IsNullOrEmpty(currentLoadoutHash))
            {
                return currentLoadoutHash;
            }

            try
            {
                currentLoadoutHash = FastLoaderHasher.ComputeFastModListHash();
            }
            catch (Exception ex)
            {
                currentLoadoutHash = "loadout-hash-error:" + ex.GetType().FullName;
            }

            return currentLoadoutHash;
        }

        private static string GetCachePath(string hash)
        {
            string fileName = "atlas_" + hash.Substring(0, Math.Min(24, hash.Length)) + CacheExtension;
            return Path.Combine(CacheRootPath, fileName);
        }

        private static bool IsEnabled()
        {
            return !FastLoaderRuntime.IsCacheFallbackActive &&
                (FastLoaderRuntime.Settings == null || FastLoaderRuntime.Settings.CacheEnabled);
        }

        private static void Write(string path, AtlasCacheRecord record)
        {
            using (BinaryWriter writer = new BinaryWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None)))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(record.Hash ?? string.Empty);
                writer.Write(record.Group);
                writer.Write(record.HasMask);
                writer.Write(record.TextureCount);
                WriteTexture(writer, record.ColorTexture);
                writer.Write(record.MaskTexture != null);
                if (record.MaskTexture != null)
                {
                    WriteTexture(writer, record.MaskTexture);
                }

                writer.Write(record.UvRects.Count);
                for (int i = 0; i < record.UvRects.Count; i++)
                {
                    Rect rect = record.UvRects[i];
                    writer.Write(rect.x);
                    writer.Write(rect.y);
                    writer.Write(rect.width);
                    writer.Write(rect.height);
                }
            }
        }

        private static bool TryRead(string path, string expectedHash, out AtlasCacheRecord record)
        {
            record = null;
            using (BinaryReader reader = new BinaryReader(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)))
            {
                if (reader.ReadInt32() != Magic)
                {
                    return false;
                }

                if (reader.ReadInt32() != FormatVersion)
                {
                    return false;
                }

                string hash = reader.ReadString();
                if (!string.Equals(hash, expectedHash, StringComparison.Ordinal))
                {
                    return false;
                }

                AtlasCacheRecord result = new AtlasCacheRecord();
                result.Hash = hash;
                result.Group = reader.ReadInt32();
                result.HasMask = reader.ReadBoolean();
                result.TextureCount = reader.ReadInt32();
                result.ColorTexture = ReadTexture(reader);
                if (reader.ReadBoolean())
                {
                    result.MaskTexture = ReadTexture(reader);
                }

                int rectCount = reader.ReadInt32();
                if (rectCount < 0 || rectCount > 100000)
                {
                    return false;
                }

                for (int i = 0; i < rectCount; i++)
                {
                    result.UvRects.Add(new Rect(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle()));
                }

                record = result;
                return true;
            }
        }

        private static void WriteTexture(BinaryWriter writer, TexturePayload texture)
        {
            writer.Write(texture.Name ?? string.Empty);
            writer.Write(texture.Width);
            writer.Write(texture.Height);
            writer.Write(texture.GraphicsFormat);
            writer.Write(texture.MipCount);
            writer.Write(texture.FilterMode);
            writer.Write(texture.WrapMode);
            writer.Write(texture.AnisoLevel);
            writer.Write(texture.MipMapBias);
            writer.Write(texture.RawData.Length);
            writer.Write(texture.RawData);
        }

        private static TexturePayload ReadTexture(BinaryReader reader)
        {
            TexturePayload texture = new TexturePayload();
            texture.Name = reader.ReadString();
            texture.Width = reader.ReadInt32();
            texture.Height = reader.ReadInt32();
            texture.GraphicsFormat = reader.ReadInt32();
            texture.MipCount = reader.ReadInt32();
            texture.FilterMode = reader.ReadInt32();
            texture.WrapMode = reader.ReadInt32();
            texture.AnisoLevel = reader.ReadInt32();
            texture.MipMapBias = reader.ReadSingle();
            int length = reader.ReadInt32();
            if (length <= 0 || length > MaxTexturePayloadBytes)
            {
                throw new InvalidDataException("Invalid static atlas raw texture length: " + length);
            }

            texture.RawData = reader.ReadBytes(length);
            if (texture.RawData.Length != length)
            {
                throw new EndOfStreamException("Static atlas raw texture data was truncated.");
            }

            return texture;
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
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TryDeleteEmptyDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path) && Directory.GetFiles(path).Length == 0 && Directory.GetDirectories(path).Length == 0)
                {
                    Directory.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void AppendHash(HashAlgorithm sha, string value)
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
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }

            return sb.ToString();
        }

        private sealed class AtlasCacheRecord
        {
            public string Hash;
            public int Group;
            public bool HasMask;
            public int TextureCount;
            public TexturePayload ColorTexture;
            public TexturePayload MaskTexture;
            public readonly List<Rect> UvRects = new List<Rect>();
        }

        private sealed class TexturePayload
        {
            public string Name;
            public int Width;
            public int Height;
            public int GraphicsFormat;
            public int MipCount;
            public int FilterMode;
            public int WrapMode;
            public int AnisoLevel;
            public float MipMapBias;
            public byte[] RawData;
        }
    }

    [HarmonyPatch(typeof(StaticTextureAtlas), nameof(StaticTextureAtlas.Bake))]
    internal static class Patch_StaticTextureAtlas_Bake_StaticAtlasCache
    {
        private static bool Prefix(StaticTextureAtlas __instance, bool rebake, ref FastLoaderProfileScope __state)
        {
            __state = FastLoaderProfiler.Scope("FastLoader.StaticAtlas.BakeOrRestore");
            if (rebake)
            {
                return true;
            }

            if (StaticAtlasCache.TryRestore(__instance))
            {
                return false;
            }

            return true;
        }

        private static void Postfix(FastLoaderProfileScope __state)
        {
            if (__state != null)
            {
                __state.Dispose();
            }
        }
    }

}
