using System;
using Verse;

namespace FastLoader
{
    public sealed class FastLoaderBuildResult
    {
        public bool XmlCacheBuilt;
        public int LanguageCachesBuilt;
        public int TexturesSaved;
        public int AtlasesSaved;

        public bool AnyBuilt
        {
            get { return XmlCacheBuilt || LanguageCachesBuilt > 0 || TexturesSaved > 0 || AtlasesSaved > 0; }
        }

        public override string ToString()
        {
            return "xml=" + XmlCacheBuilt + ", languages=" + LanguageCachesBuilt + ", textures=" + TexturesSaved + ", atlases=" + AtlasesSaved;
        }
    }

    public static class FastLoaderBridge
    {
        public static FastLoaderBuildResult BuildXmlAndLanguageCaches()
        {
            using (FastLoaderRuntime.ManualBuildScope())
            {
                FastLoaderBuildResult result = new FastLoaderBuildResult();
                result.XmlCacheBuilt = FastLoaderRuntime.WriteXmlCacheFromCurrentSnapshot();
                result.LanguageCachesBuilt = LanguageBinaryCache.BuildFromLoadedLanguages();
                FastLoaderCacheState.MarkBuilt(FastLoaderCacheKind.Xml, result.XmlCacheBuilt, result.XmlCacheBuilt ? FastLoaderRuntime.LastParsedDefCount : 0);
                FastLoaderCacheState.MarkBuilt(FastLoaderCacheKind.Language, result.LanguageCachesBuilt > 0, result.LanguageCachesBuilt);
                Log.Message("[FastLoader] BuildXmlAndLanguageCaches completed: " + result);
                return result;
            }
        }

        public static int BuildLanguageCache()
        {
            using (FastLoaderRuntime.ManualBuildScope())
            {
                int count = LanguageBinaryCache.BuildFromLoadedLanguages();
                FastLoaderCacheState.MarkBuilt(FastLoaderCacheKind.Language, count > 0, count);
                Log.Message("[FastLoader] BuildLanguageCache completed: languages=" + count);
                return count;
            }
        }

        public static int BuildTextureCache()
        {
            using (FastLoaderRuntime.ManualBuildScope())
            {
                int textures = TextureRawCache.RebuildFromLoadedMods();
                FastLoaderCacheState.MarkBuilt(FastLoaderCacheKind.Texture, true, textures);
                Log.Message("[FastLoader] BuildTextureCache completed: textures=" + textures);
                return textures;
            }
        }

        public static int BuildStaticAtlasCache()
        {
            using (FastLoaderRuntime.ManualBuildScope())
            {
                int atlases = StaticAtlasCache.BuildFromCurrentAtlases();
                FastLoaderCacheState.MarkBuilt(FastLoaderCacheKind.Atlas, atlases > 0, atlases);
                Log.Message("[FastLoader] BuildStaticAtlasCache completed: atlases=" + atlases);
                return atlases;
            }
        }

        public static FastLoaderBuildResult BuildAllCaches()
        {
            using (FastLoaderRuntime.ManualBuildScope())
            {
                try
                {
                    FastLoaderBuildResult result = BuildXmlAndLanguageCaches();
                    if (!result.XmlCacheBuilt)
                    {
                        throw new InvalidOperationException("XML cache build failed: " + (FastLoaderRuntime.LastXmlCacheBuildFailureReason ?? "unknown reason"));
                    }

                    if (result.LanguageCachesBuilt <= 0)
                    {
                        throw new InvalidOperationException("Language cache build failed.");
                    }

                    result.TexturesSaved = TextureRawCache.RebuildFromLoadedMods();
                    result.AtlasesSaved = StaticAtlasCache.BuildFromCurrentAtlases();
                    FastLoaderCacheState.MarkBuilt(FastLoaderCacheKind.Texture, true, result.TexturesSaved);
                    FastLoaderCacheState.MarkBuilt(FastLoaderCacheKind.Atlas, result.AtlasesSaved > 0, result.AtlasesSaved);
                    Log.Message("[FastLoader] BuildAllCaches completed: " + result);
                    return result;
                }
                catch
                {
                    FastLoaderRuntime.DeleteAllCaches();
                    throw;
                }
            }
        }

        public static void DeleteAllCaches()
        {
            FastLoaderRuntime.DeleteAllCaches();
        }
    }
}
