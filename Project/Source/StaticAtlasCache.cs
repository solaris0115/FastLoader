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
        private const int FormatVersion = 6;
        private const int MinSupportedFormatVersion = 5;
        private const string HashVersion = "FastLoaderStaticAtlasV5Lz4StripedReadback";
        private const string CacheExtension = ".atlascache";
        private const int BytesPerMegabyte = 1024 * 1024;
        private const int MaxTextureChunkBytes = FastLoaderSettings.MaxAtlasCacheChunkSizeMb * BytesPerMegabyte;
        private const long MaxTexturePayloadBytes = 1024L * 1024L * 1024L;
        private const int MaxTextureChunkCount = 4096;

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
                        Log.Warning("[FastLoader] Static atlas cache restore skipped for " + atlas.groupKey + ": cache record is invalid.");
                        return false;
                    }

                    if (record.UvRects.Count != textures.Count)
                    {
                        Log.Warning("[FastLoader] Static atlas cache restore skipped for " + atlas.groupKey + ": texture count changed.");
                        return false;
                    }

                    Texture2D colorTexture = RestoreTexture(record.ColorTexture);
                    Texture2D maskTexture = record.MaskTexture != null ? RestoreTexture(record.MaskTexture) : null;
                    if (colorTexture == null)
                    {
                        Log.Warning("[FastLoader] Static atlas cache restore skipped for " + atlas.groupKey + ": color texture restore returned null.");
                        return false;
                    }

                    ColorTextureField.SetValue(atlas, colorTexture);
                    MaskTextureField.SetValue(atlas, maskTexture);
                    RestoreTiles(atlas, textures, record.UvRects);
                    Log.Message("[FastLoader] Static atlas cache restored for " + atlas.groupKey + ": textures=" + textures.Count + ", payload=" + FormatBytes(GetTexturePayloadBytes(record.ColorTexture) + GetTexturePayloadBytes(record.MaskTexture)));
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

        public static long EstimateBuildCacheBytes()
        {
            List<StaticTextureAtlas> atlases = GetCurrentAtlases();
            long total = 0L;
            for (int i = 0; i < atlases.Count; i++)
            {
                StaticTextureAtlas atlas = atlases[i];
                if (atlas == null)
                {
                    continue;
                }

                Texture2D colorTexture = ColorTextureField.GetValue(atlas) as Texture2D;
                Texture2D maskTexture = MaskTextureField.GetValue(atlas) as Texture2D;
                total = SafeAdd(total, EstimateAtlasTextureCacheBytes(colorTexture));
                total = SafeAdd(total, EstimateAtlasTextureCacheBytes(maskTexture));
            }

            if (ShouldCompressAtlasCache())
            {
                total = Math.Max(total / 4L, total > 0L ? 64L * BytesPerMegabyte : 0L);
            }

            return total;
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
                    record.AtlasCacheCompressionEnabled = ShouldCompressAtlasCache();
                    record.AtlasCacheChunkSizeMb = GetAtlasCacheChunkSizeMb();
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
                    throw new IOException("Failed to write static atlas cache for " + atlas.groupKey + ": " + ex.GetBaseException().Message, ex);
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
            if (estimatedBytes <= 0L || estimatedBytes > MaxTexturePayloadBytes)
            {
                Log.Warning("[FastLoader] Static atlas render texture capture skipped for " + source.name + ": estimated payload is too large.");
                return null;
            }

            int chunkBytes = GetAtlasCacheChunkSizeBytes();
            int stripeHeight = CalculateStripeHeight(source.width, chunkBytes);
            RenderTexture rt = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);

            try
            {
                Graphics.Blit(source, rt);

                List<TextureChunk> chunks = new List<TextureChunk>();
                for (int y = 0; y < source.height; y += stripeHeight)
                {
                    int height = Math.Min(stripeHeight, source.height - y);
                    AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(rt, 0, 0, source.width, y, height, 0, 1, TextureFormat.RGBA32);
                    request.WaitForCompletion();
                    if (request.hasError)
                    {
                        Log.Warning("[FastLoader] Static atlas render texture stripe readback failed for " + source.name + ".");
                        return null;
                    }

                    var data = request.GetData<byte>();
                    int expectedLength = source.width * height * 4;
                    if (data.Length != expectedLength || data.Length <= 0 || data.Length > chunkBytes)
                    {
                        Log.Warning("[FastLoader] Static atlas render texture stripe capture skipped for " + source.name + ": raw texture chunk is invalid.");
                        return null;
                    }

                    byte[] rawData = new byte[data.Length];
                    for (int i = 0; i < data.Length; i++)
                    {
                        rawData[i] = data[i];
                    }

                    chunks.Add(new TextureChunk
                    {
                        X = 0,
                        Y = y,
                        Width = source.width,
                        Height = height,
                        RawData = rawData
                    });
                }

                if (chunks.Count == 0 || chunks.Count > MaxTextureChunkCount)
                {
                    Log.Warning("[FastLoader] Static atlas render texture capture skipped for " + source.name + ": invalid chunk count.");
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
                    TotalRawLength = checked((int)estimatedBytes),
                    Chunks = chunks
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

        private static int CalculateStripeHeight(int width, int maxChunkBytes)
        {
            int rowBytes = Math.Max(1, width) * 4;
            return Math.Max(1, maxChunkBytes / rowBytes);
        }

        private static Texture2D RestoreTexture(TexturePayload payload)
        {
            if (payload == null ||
                ((payload.RawData == null || payload.RawData.Length == 0) &&
                 (payload.Chunks == null || payload.Chunks.Count == 0)))
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
            texture.LoadRawTextureData(GetRawTextureData(payload));
            texture.Apply(false, true);
            return texture;
        }

        private static byte[] GetRawTextureData(TexturePayload payload)
        {
            if (payload.RawData != null)
            {
                return payload.RawData;
            }

            if (payload.Chunks == null || payload.Chunks.Count == 0)
            {
                throw new InvalidDataException("Static atlas texture has no raw payload.");
            }

            if ((GraphicsFormat)payload.GraphicsFormat != GraphicsFormat.R8G8B8A8_UNorm)
            {
                throw new InvalidDataException("Unsupported chunked static atlas graphics format: " + payload.GraphicsFormat);
            }

            int totalLength = payload.TotalRawLength;
            if (totalLength <= 0 || totalLength > MaxTexturePayloadBytes)
            {
                throw new InvalidDataException("Invalid chunked static atlas raw texture length: " + totalLength);
            }

            byte[] rawData = new byte[totalLength];
            int bytesPerPixel = 4;
            int targetStride = payload.Width * bytesPerPixel;
            for (int i = 0; i < payload.Chunks.Count; i++)
            {
                TextureChunk chunk = payload.Chunks[i];
                int sourceStride = chunk.Width * bytesPerPixel;
                int expectedLength = sourceStride * chunk.Height;
                if (chunk.RawData == null ||
                    chunk.RawData.Length != expectedLength ||
                    chunk.X < 0 ||
                    chunk.Y < 0 ||
                    chunk.Width <= 0 ||
                    chunk.Height <= 0 ||
                    chunk.X + chunk.Width > payload.Width ||
                    chunk.Y + chunk.Height > payload.Height)
                {
                    throw new InvalidDataException("Invalid static atlas raw texture chunk.");
                }

                for (int row = 0; row < chunk.Height; row++)
                {
                    int sourceOffset = row * sourceStride;
                    int targetOffset = ((chunk.Y + row) * payload.Width + chunk.X) * bytesPerPixel;
                    Buffer.BlockCopy(chunk.RawData, sourceOffset, rawData, targetOffset, sourceStride);
                }
            }

            return rawData;
        }

        private static long GetTexturePayloadBytes(TexturePayload payload)
        {
            if (payload == null)
            {
                return 0L;
            }

            if (payload.RawData != null)
            {
                return payload.RawData.Length;
            }

            if (payload.TotalRawLength > 0)
            {
                return payload.TotalRawLength;
            }

            return 0L;
        }

        private static long EstimateAtlasTextureCacheBytes(Texture2D texture)
        {
            if (texture == null)
            {
                return 0L;
            }

            return (long)Math.Max(1, texture.width) * Math.Max(1, texture.height) * 4L;
        }

        private static long SafeAdd(long left, long right)
        {
            if (right <= 0L)
            {
                return left;
            }

            if (long.MaxValue - left < right)
            {
                return long.MaxValue;
            }

            return left + right;
        }

        private static string FormatBytes(long bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("0.00") + "MB";
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

        private static bool ShouldCompressAtlasCache()
        {
            return FastLoaderRuntime.Settings == null || FastLoaderRuntime.Settings.AtlasCacheCompressionEnabled;
        }

        private static int GetAtlasCacheChunkSizeMb()
        {
            if (FastLoaderRuntime.Settings == null)
            {
                return FastLoaderSettings.DefaultAtlasCacheChunkSizeMb;
            }

            FastLoaderRuntime.Settings.NormalizeAtlasCacheSettings();
            return FastLoaderRuntime.Settings.AtlasCacheChunkSizeMb;
        }

        private static int GetAtlasCacheChunkSizeBytes()
        {
            return GetAtlasCacheChunkSizeMb() * BytesPerMegabyte;
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
                writer.Write(record.AtlasCacheCompressionEnabled);
                writer.Write(record.AtlasCacheChunkSizeMb);
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

                int version = reader.ReadInt32();
                if (version < MinSupportedFormatVersion || version > FormatVersion)
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
                if (version >= 6)
                {
                    result.AtlasCacheCompressionEnabled = reader.ReadBoolean();
                    result.AtlasCacheChunkSizeMb = reader.ReadInt32();
                }
                else
                {
                    result.AtlasCacheCompressionEnabled = true;
                    result.AtlasCacheChunkSizeMb = FastLoaderSettings.DefaultAtlasCacheChunkSizeMb;
                }

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
            bool compress = ShouldCompressAtlasCache();
            if (texture.Chunks != null && texture.Chunks.Count > 0)
            {
                writer.Write(compress ? (byte)3 : (byte)1);
                writer.Write(texture.TotalRawLength);
                writer.Write(texture.Chunks.Count);
                for (int i = 0; i < texture.Chunks.Count; i++)
                {
                    TextureChunk chunk = texture.Chunks[i];
                    writer.Write(chunk.X);
                    writer.Write(chunk.Y);
                    writer.Write(chunk.Width);
                    writer.Write(chunk.Height);
                    if (compress)
                    {
                        WriteCompressedBlock(writer, chunk.RawData, MaxTextureChunkBytes);
                    }
                    else
                    {
                        writer.Write(chunk.RawData.Length);
                        writer.Write(chunk.RawData);
                    }
                }
            }
            else
            {
                byte[] compressed = compress ? FastLz4Block.TryCompress(texture.RawData) : null;
                if (compress && compressed != null)
                {
                    writer.Write((byte)2);
                    writer.Write(texture.RawData.Length);
                    writer.Write(compressed.Length);
                    writer.Write(compressed);
                }
                else
                {
                    writer.Write((byte)0);
                    writer.Write(texture.RawData.Length);
                    writer.Write(texture.RawData);
                }
            }
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
            byte storageKind = reader.ReadByte();
            if (storageKind == 0)
            {
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
            }
            else if (storageKind == 2)
            {
                int rawLength = reader.ReadInt32();
                if (rawLength <= 0 || rawLength > MaxTexturePayloadBytes)
                {
                    throw new InvalidDataException("Invalid compressed static atlas raw texture length: " + rawLength);
                }

                int storedLength = reader.ReadInt32();
                if (storedLength <= 0 || storedLength > MaxTexturePayloadBytes)
                {
                    throw new InvalidDataException("Invalid compressed static atlas stored texture length: " + storedLength);
                }

                byte[] compressed = reader.ReadBytes(storedLength);
                if (compressed.Length != storedLength)
                {
                    throw new EndOfStreamException("Static atlas compressed raw texture data was truncated.");
                }

                texture.RawData = FastLz4Block.Decompress(compressed, rawLength);
            }
            else if (storageKind == 1)
            {
                int totalLength = reader.ReadInt32();
                if (totalLength <= 0 || totalLength > MaxTexturePayloadBytes)
                {
                    throw new InvalidDataException("Invalid chunked static atlas raw texture length: " + totalLength);
                }

                int chunkCount = reader.ReadInt32();
                if (chunkCount <= 0 || chunkCount > MaxTextureChunkCount)
                {
                    throw new InvalidDataException("Invalid static atlas raw texture chunk count: " + chunkCount);
                }

                texture.TotalRawLength = totalLength;
                texture.Chunks = new List<TextureChunk>(chunkCount);
                for (int i = 0; i < chunkCount; i++)
                {
                    TextureChunk chunk = new TextureChunk();
                    chunk.X = reader.ReadInt32();
                    chunk.Y = reader.ReadInt32();
                    chunk.Width = reader.ReadInt32();
                    chunk.Height = reader.ReadInt32();
                    int length = reader.ReadInt32();
                    if (length <= 0 || length > MaxTextureChunkBytes)
                    {
                        throw new InvalidDataException("Invalid static atlas raw texture chunk length: " + length);
                    }

                    chunk.RawData = reader.ReadBytes(length);
                    if (chunk.RawData.Length != length)
                    {
                        throw new EndOfStreamException("Static atlas raw texture chunk data was truncated.");
                    }

                    texture.Chunks.Add(chunk);
                }
            }
            else if (storageKind == 3)
            {
                int totalLength = reader.ReadInt32();
                if (totalLength <= 0 || totalLength > MaxTexturePayloadBytes)
                {
                    throw new InvalidDataException("Invalid chunked static atlas raw texture length: " + totalLength);
                }

                int chunkCount = reader.ReadInt32();
                if (chunkCount <= 0 || chunkCount > MaxTextureChunkCount)
                {
                    throw new InvalidDataException("Invalid static atlas raw texture chunk count: " + chunkCount);
                }

                texture.TotalRawLength = totalLength;
                texture.RawData = ReadCompressedChunkedRawData(reader, texture, totalLength, chunkCount);
            }
            else
            {
                throw new InvalidDataException("Invalid static atlas texture storage kind: " + storageKind);
            }

            return texture;
        }

        private static byte[] ReadCompressedChunkedRawData(BinaryReader reader, TexturePayload texture, int totalLength, int chunkCount)
        {
            if ((GraphicsFormat)texture.GraphicsFormat != GraphicsFormat.R8G8B8A8_UNorm)
            {
                throw new InvalidDataException("Unsupported compressed chunked static atlas graphics format: " + texture.GraphicsFormat);
            }

            byte[] rawData = new byte[totalLength];
            int bytesPerPixel = 4;
            int targetStride = texture.Width * bytesPerPixel;
            for (int i = 0; i < chunkCount; i++)
            {
                int x = reader.ReadInt32();
                int y = reader.ReadInt32();
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();
                byte[] chunkData = ReadCompressedBlock(reader, MaxTextureChunkBytes);
                int sourceStride = width * bytesPerPixel;
                int expectedLength = sourceStride * height;
                if (chunkData.Length != expectedLength ||
                    x < 0 ||
                    y < 0 ||
                    width <= 0 ||
                    height <= 0 ||
                    x + width > texture.Width ||
                    y + height > texture.Height)
                {
                    throw new InvalidDataException("Invalid compressed static atlas raw texture chunk.");
                }

                if (x == 0 && width == texture.Width)
                {
                    Buffer.BlockCopy(chunkData, 0, rawData, y * targetStride, expectedLength);
                    continue;
                }

                for (int row = 0; row < height; row++)
                {
                    int sourceOffset = row * sourceStride;
                    int targetOffset = ((y + row) * texture.Width + x) * bytesPerPixel;
                    Buffer.BlockCopy(chunkData, sourceOffset, rawData, targetOffset, sourceStride);
                }
            }

            return rawData;
        }

        private static void WriteCompressedBlock(BinaryWriter writer, byte[] rawData, int maxRawBytes)
        {
            if (rawData == null || rawData.Length <= 0 || rawData.Length > maxRawBytes)
            {
                throw new InvalidDataException("Invalid static atlas block length.");
            }

            byte[] compressed = FastLz4Block.TryCompress(rawData);
            writer.Write(rawData.Length);
            if (compressed != null)
            {
                writer.Write(true);
                writer.Write(compressed.Length);
                writer.Write(compressed);
            }
            else
            {
                writer.Write(false);
                writer.Write(rawData.Length);
                writer.Write(rawData);
            }
        }

        private static byte[] ReadCompressedBlock(BinaryReader reader, int maxRawBytes)
        {
            int rawLength = reader.ReadInt32();
            if (rawLength <= 0 || rawLength > maxRawBytes)
            {
                throw new InvalidDataException("Invalid static atlas block raw length: " + rawLength);
            }

            bool compressed = reader.ReadBoolean();
            int storedLength = reader.ReadInt32();
            if (storedLength <= 0 || storedLength > maxRawBytes)
            {
                throw new InvalidDataException("Invalid static atlas block stored length: " + storedLength);
            }

            byte[] stored = reader.ReadBytes(storedLength);
            if (stored.Length != storedLength)
            {
                throw new EndOfStreamException("Static atlas block data was truncated.");
            }

            if (!compressed)
            {
                if (storedLength != rawLength)
                {
                    throw new InvalidDataException("Static atlas raw block length mismatch.");
                }

                return stored;
            }

            return FastLz4Block.Decompress(stored, rawLength);
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
            public bool AtlasCacheCompressionEnabled;
            public int AtlasCacheChunkSizeMb;
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
            public int TotalRawLength;
            public byte[] RawData;
            public List<TextureChunk> Chunks;
        }

        private sealed class TextureChunk
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;
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
