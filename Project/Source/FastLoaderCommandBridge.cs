using System;
using Verse;

namespace FastLoader
{
    internal static class FastLoaderCommandBridge
    {
        private const string BuildLanguageArg = "-fastloader-build-language-cache";
        private const string BuildXmlLanguageArg = "-fastloader-build-xml-language-cache";
        private const string BuildAtlasArg = "-fastloader-build-atlas-cache";
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
            if (!buildXmlLanguage && !buildLanguage && !buildAtlas)
            {
                return;
            }

            try
            {
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
