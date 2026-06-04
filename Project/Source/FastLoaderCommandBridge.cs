using System;
using Verse;

namespace FastLoader
{
    internal static class FastLoaderCommandBridge
    {
        private const string BuildLanguageArg = "-fastloader-build-language-cache";
        private const string BuildXmlLanguageArg = "-fastloader-build-xml-language-cache";
        private const string BuildAtlasArg = "-fastloader-build-atlas-cache";
        private const string BuildAllArg = "-fastloader-build-all-caches";
        private const string RebuildAllArg = "-fastloader-rebuild-all-caches";
        private static bool processed;

        public static void ProcessCommandLineBuildRequests()
        {
            if (processed)
            {
                return;
            }

            processed = true;

            string[] args = Environment.GetCommandLineArgs();
            bool buildXmlLanguage = HasArg(args, BuildXmlLanguageArg);
            bool buildLanguage = HasArg(args, BuildLanguageArg);
            bool buildAtlas = HasArg(args, BuildAtlasArg);
            bool buildAll = HasArg(args, BuildAllArg);
            bool rebuildAll = HasArg(args, RebuildAllArg);
            if (!buildXmlLanguage && !buildLanguage && !buildAtlas && !buildAll && !rebuildAll)
            {
                return;
            }

            try
            {
                if (buildAll || rebuildAll)
                {
                    if (rebuildAll)
                    {
                        FastLoaderBridge.DeleteAllCaches();
                    }

                    FastLoaderBuildResult result = FastLoaderBridge.BuildAllCaches();
                    if (!result.XmlCacheBuilt || result.LanguageCachesBuilt <= 0 || result.TexturesSaved <= 0)
                    {
                        throw new InvalidOperationException("Build all caches did not create required caches: " + result);
                    }

                    Log.Message("[FastLoader] Command bridge built all caches: " + result);
                    return;
                }

                if (buildXmlLanguage)
                {
                    FastLoaderBuildResult result = FastLoaderBridge.BuildXmlAndLanguageCaches();
                    Log.Message("[FastLoader] Command bridge built XML/language caches: " + result);
                }
                else if (buildLanguage)
                {
                    int count = FastLoaderBridge.BuildLanguageCache();
                    Log.Message("[FastLoader] Command bridge built language caches: languages=" + count);
                }

                if (buildAtlas)
                {
                    int count = FastLoaderBridge.BuildStaticAtlasCache();
                    Log.Message("[FastLoader] Command bridge built static atlas caches: atlases=" + count);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    FastLoaderBridge.DeleteAllCaches();
                }
                catch (Exception deleteEx)
                {
                    Log.Error("[FastLoader] Command bridge failed to remove partial cache files after build failure.\n" + deleteEx);
                }

                Log.Error("[FastLoader] Command bridge cache build failed.\n" + ex);
            }
        }

        private static bool HasArg(string[] args, string target)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], target, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
