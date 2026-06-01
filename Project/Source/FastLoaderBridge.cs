using System;
using Verse;

namespace FastLoader
{
    public sealed class FastLoaderBuildResult
    {
        public bool XmlCacheBuilt;
        public int LanguageCachesBuilt;
        public int TexturesSaved;

        public bool AnyBuilt
        {
            get { return XmlCacheBuilt || LanguageCachesBuilt > 0 || TexturesSaved > 0; }
        }

        public override string ToString()
        {
            return "xml=" + XmlCacheBuilt + ", languages=" + LanguageCachesBuilt + ", textures=" + TexturesSaved;
        }
    }

    public static class FastLoaderBridge
    {
        public static FastLoaderBuildResult BuildXmlAndLanguageCaches()
        {
            FastLoaderBuildResult result = new FastLoaderBuildResult();
            result.XmlCacheBuilt = FastLoaderRuntime.WriteXmlCacheFromCurrentSnapshot();
            result.LanguageCachesBuilt = LanguageBinaryCache.BuildFromLoadedLanguages();
            Log.Message("[FastLoader] BuildXmlAndLanguageCaches completed: " + result);
            return result;
        }

        public static int BuildLanguageCache()
        {
            int count = LanguageBinaryCache.BuildFromLoadedLanguages();
            Log.Message("[FastLoader] BuildLanguageCache completed: languages=" + count);
            return count;
        }

        public static int BuildTextureCache()
        {
            int textures = TextureRawCache.RebuildFromLoadedMods();
            Log.Message("[FastLoader] BuildTextureCache completed: textures=" + textures);
            return textures;
        }

        public static FastLoaderBuildResult BuildAllCaches()
        {
            FastLoaderBuildResult result = BuildXmlAndLanguageCaches();
            result.TexturesSaved = TextureRawCache.RebuildFromLoadedMods();
            Log.Message("[FastLoader] BuildAllCaches completed: " + result);
            return result;
        }

        public static void DeleteAllCaches()
        {
            FastLoaderRuntime.DeleteAllCaches();
        }
    }
}
